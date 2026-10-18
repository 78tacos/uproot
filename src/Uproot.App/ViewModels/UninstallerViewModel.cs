using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Data;
using Uproot.Core.Elevation;
using Uproot.Core;

namespace Uproot.App.ViewModels;

public enum ProgramFilter
{
    All,
    Everyone,
    JustMe,

    /// <summary>Per-user programs of the account Uproot runs as, when that is not the person signed in.</summary>
    OtherAccount,
    StoreApps,
    UninstallerMissing
}

public enum ProgramSort
{
    Name,
    Publisher,
    Newest,
    Largest
}

/// <summary>The installed-programs list, and the analysis of whichever program is selected.</summary>
public sealed class UninstallerViewModel : ObservableObject
{
    private readonly Dictionary<string, UninstallerProfile> _analyses = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _analysisCancellation;

    private string _searchText = string.Empty;
    private ProgramFilter _filter = ProgramFilter.All;
    private ProgramSort _sort = ProgramSort.Name;
    private bool _showSystemComponents;
    private ProgramRowViewModel? _selected;
    private IReadOnlyList<ProgramRowViewModel> _selectedRows = [];
    private UninstallerProfile? _analysis;
    private bool _isLoading;
    private bool _isLoadingStoreApps;
    private bool _isAnalyzing;
    private bool _isPreparingBatch;
    private string _statusText = "Loading installed programs…";

    public UninstallerViewModel()
    {
        Programs = [];
        ProgramsView = CollectionViewSource.GetDefaultView(Programs);
        ProgramsView.Filter = PassesFilter;
        ApplySort();

        RefreshCommand = AsyncRelayCommand.Create(LoadAsync, () => !IsLoading);
        RelaunchAsAdminCommand = RelayCommand.Create(RelaunchAsAdministrator, () => !IsElevated);
        OpenInstallFolderCommand = RelayCommand.Create(OpenInstallFolder, () => Analysis?.InstallLocation is not null);
        UninstallCommand = RelayCommand.Create(
            () => RequestSession(leftoversOnly: false),
            () => Analysis is { CanUninstall: true } && !IsAnalyzing);
        LeftoversOnlyCommand = RelayCommand.Create(
            () => RequestSession(leftoversOnly: true),
            () => Analysis is not null && !IsAnalyzing && Analysis.Program.IsRegistered);
        UninstallSelectedCommand = AsyncRelayCommand.Create(UninstallSelectedAsync, () => SelectedRows.Count > 1 && !_isPreparingBatch);
        OpenRegistryEntryCommand = RelayCommand.Create(OpenRegistryEntry, () => Selected is { Program.IsStoreApp: false });
        CopyNameCommand = RelayCommand.Create(CopyName, () => Selected is not null);
    }

    public ObservableCollection<ProgramRowViewModel> Programs { get; }
    public ICollectionView ProgramsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand RelaunchAsAdminCommand { get; }
    public RelayCommand OpenInstallFolderCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand LeftoversOnlyCommand { get; }
    public AsyncRelayCommand UninstallSelectedCommand { get; }
    public RelayCommand OpenRegistryEntryCommand { get; }
    public RelayCommand CopyNameCommand { get; }

    /// <summary>Raised when the window should open an uninstall session.</summary>
    public event EventHandler<SessionRequest>? SessionRequested;

    /// <summary>Raised when the app should shut down so an elevated instance can take over.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Elevated as another account, that account's own per-user programs get a filter of their
    /// own, named for it, beside "just for me".
    /// </summary>
    public IReadOnlyList<FilterOption> Filters { get; } =
    [
        new(ProgramFilter.All, "All programs"),
        new(ProgramFilter.Everyone, "Installed for everyone"),
        new(ProgramFilter.JustMe, "Installed just for me"),
        .. ProgramOwner.OtherAccount is { } other
            ? [new FilterOption(ProgramFilter.OtherAccount, $"Installed just for {InstalledProgram.ShortAccount(other)}")]
            : Array.Empty<FilterOption>(),
        new(ProgramFilter.StoreApps, "Microsoft Store apps"),
        new(ProgramFilter.UninstallerMissing, "Uninstaller missing")
    ];

    public IReadOnlyList<SortOption> Sorts { get; } =
    [
        new(ProgramSort.Name, "Name"),
        new(ProgramSort.Publisher, "Publisher"),
        new(ProgramSort.Newest, "Newest first"),
        new(ProgramSort.Largest, "Largest first")
    ];

    public bool IsElevated => ElevationState.IsElevated;
    public bool ShowElevationNotice => !IsElevated;

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (Set(ref _isAnalyzing, value)) Raise(nameof(AnalysisStatus));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) RefreshFilter(); }
    }

    public ProgramFilter Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) RefreshFilter(); }
    }

    public ProgramSort Sort
    {
        get => _sort;
        set { if (Set(ref _sort, value)) ApplySort(); }
    }

    /// <summary>Show what Apps &amp; Features hides: system components, updates and the parts of bundles.</summary>
    public bool ShowSystemComponents
    {
        get => _showSystemComponents;
        set { if (Set(ref _showSystemComponents, value)) RefreshFilter(); }
    }

    public ProgramRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _ = AnalyzeSelectedAsync();
        }
    }

    /// <summary>Every selected row, for uninstalling several at once. The window keeps it up to date.</summary>
    public IReadOnlyList<ProgramRowViewModel> SelectedRows
    {
        get => _selectedRows;
        private set
        {
            _selectedRows = value;
            Raise();
            Raise(nameof(IsMultiSelect));
            Raise(nameof(UninstallSelectedText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsMultiSelect => SelectedRows.Count > 1;
    public string UninstallSelectedText => $"Uninstall {SelectedRows.Count} selected…";

    public void SetSelectedRows(IList items) => SelectedRows = items.OfType<ProgramRowViewModel>().ToList();

    public UninstallerProfile? Analysis
    {
        get => _analysis;
        private set
        {
            if (!Set(ref _analysis, value)) return;

            Raise(nameof(Findings));
            Raise(nameof(RegisteredCommandText));
            Raise(nameof(RunCommandText));
            Raise(nameof(QuietCommandText));
            Raise(nameof(InstallLocationText));
            Raise(nameof(TechnologyText));
            Raise(nameof(AnalysisStatus));
        }
    }

    public IReadOnlyList<FindingViewModel> Findings =>
        Analysis?.Findings.Select(f => new FindingViewModel(f)).ToList() ?? [];

    public string AnalysisStatus => IsAnalyzing
        ? "Examining the uninstaller…"
        : Analysis is null ? string.Empty : $"Analysed with {Analysis.TechnologyName}.";

    public string? TechnologyText => Analysis?.TechnologyName;
    public string? RegisteredCommandText => Analysis?.RegisteredCommand?.Display ?? (Analysis?.Program.IsStoreApp == true ? "None — Windows removes packaged apps itself." : null);

    public string? RunCommandText =>
        Analysis?.InteractiveCommand is { } run && run != Analysis.RegisteredCommand ? run.Display : null;

    public string QuietCommandText => Analysis is null
        ? string.Empty
        : Analysis.QuietCommand?.Display ?? "Not available — its own dialogs will ask the questions.";

    public string InstallLocationText => Analysis?.InstallLocation ?? "Not recorded";

    public string SummaryLine
    {
        get
        {
            var visible = Programs.Count(p => !p.Program.IsSystemComponent && !p.Program.IsUpdateOrComponent && !p.Program.IsStoreApp);
            var perUser = Programs.Where(p => p.Program.Scope == ProgramScope.User && !p.Program.IsSystemComponent && !p.Program.IsStoreApp).ToList();
            var mine = perUser.Count(p => p.Program.Owner is null);
            var store = Programs.Count(p => p.Program.IsStoreApp);
            var missing = Programs.Count(p => p.UninstallerMissing && !p.Program.IsSystemComponent);

            var parts = new List<string> { $"{visible} programs" };
            if (mine > 0) parts.Add($"{mine} installed just for you");

            foreach (var owner in perUser.Where(p => p.Program.Owner is not null).GroupBy(p => p.Program.Owner!))
                parts.Add($"{owner.Count()} just for {InstalledProgram.ShortAccount(owner.Key)}");

            if (store > 0) parts.Add($"{store} Store apps");
            else if (_isLoadingStoreApps) parts.Add("looking for Store apps…");
            if (missing > 0) parts.Add($"{missing} with a missing uninstaller");
            return string.Join("  ·  ", parts);
        }
    }

    // ---- Loading -------------------------------------------------------------

    public async Task LoadAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        StatusText = "Loading installed programs…";

        try
        {
            var previous = Selected?.Program.Id;
            var programs = await Task.Run(() => ProgramInventory.ReadAll()
                .Select(p => new ProgramRowViewModel(p))
                .ToList()).ConfigureAwait(true);

            _analyses.Clear();
            Programs.Clear();
            foreach (var row in programs) Programs.Add(row);

            Selected = Programs.FirstOrDefault(p => p.Program.Id == previous && PassesFilter(p))
                       ?? ProgramsView.Cast<ProgramRowViewModel>().FirstOrDefault();

            Raise(nameof(SummaryLine));
            StatusText = IsElevated
                ? "Running as administrator: leftovers anywhere on the PC can be removed."
                : "Select a program to see how its uninstaller works. Ctrl-click or Shift-click to select several.";

            _ = LoadStoreAppsAsync(previous);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not read the installed programs: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Store apps come from PowerShell and take a second or two, so they join the list when ready.</summary>
    private async Task LoadStoreAppsAsync(string? previous)
    {
        _isLoadingStoreApps = true;
        Raise(nameof(SummaryLine));

        try
        {
            var apps = await StoreApps.ReadAllAsync().ConfigureAwait(true);

            foreach (var app in apps)
            {
                if (Programs.Any(p => p.Program.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase))) continue;
                Programs.Add(new ProgramRowViewModel(app));
            }

            if (previous is not null && Selected?.Program.Id != previous
                && Programs.FirstOrDefault(p => p.Program.Id == previous && PassesFilter(p)) is { } restored)
            {
                Selected = restored;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Could not list Microsoft Store apps: {ex.Message}";
        }
        finally
        {
            _isLoadingStoreApps = false;
            Raise(nameof(SummaryLine));
        }
    }

    private async Task AnalyzeSelectedAsync()
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation = new CancellationTokenSource();
        var ct = _analysisCancellation.Token;

        if (Selected is not { } row)
        {
            Analysis = null;
            return;
        }

        if (_analyses.TryGetValue(row.Program.Id, out var cached))
        {
            Analysis = cached;
            return;
        }

        Analysis = null;
        IsAnalyzing = true;

        try
        {
            var analysis = await Task.Run(() => UninstallerAnalyzer.Analyze(row.Program, ct), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            _analyses[row.Program.Id] = analysis;
            row.ApplyAnalysis(analysis);
            Analysis = analysis;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) StatusText = $"Could not analyse {row.Name}: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsAnalyzing = false;
        }
    }

    // ---- Filtering and sorting -------------------------------------------------

    /// <summary>
    /// Re-filters, and moves the selection if the filter hid it. Otherwise the detail pane — and its
    /// Uninstall button — would go on acting for a program no longer on screen.
    /// </summary>
    private void RefreshFilter()
    {
        ProgramsView.Refresh();

        if (Selected is null || !PassesFilter(Selected))
            Selected = ProgramsView.Cast<ProgramRowViewModel>().FirstOrDefault();
    }

    private bool PassesFilter(object obj)
    {
        if (obj is not ProgramRowViewModel row) return false;
        var program = row.Program;

        if (!_showSystemComponents && (program.IsSystemComponent || program.IsUpdateOrComponent)) return false;

        switch (_filter)
        {
            case ProgramFilter.Everyone when program.Scope != ProgramScope.Machine:
            case ProgramFilter.JustMe when program.Scope != ProgramScope.User || program.IsStoreApp || program.Owner is not null:
            case ProgramFilter.OtherAccount when program.Scope != ProgramScope.User || program.IsStoreApp || program.Owner is null:
            case ProgramFilter.StoreApps when !program.IsStoreApp:
            case ProgramFilter.UninstallerMissing when !row.UninstallerMissing:
                return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText)) return true;

        var needle = _searchText.Trim();
        return program.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || (program.Publisher?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
               || program.KeyName.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort()
    {
        if (ProgramsView is ListCollectionView view) view.CustomSort = new ProgramComparer(_sort);
    }

    private sealed class ProgramComparer(ProgramSort sort) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ProgramRowViewModel a || y is not ProgramRowViewModel b) return 0;

            var byName = StringComparer.CurrentCultureIgnoreCase.Compare(a.Program.DisplayName, b.Program.DisplayName);

            var primary = sort switch
            {
                ProgramSort.Publisher => StringComparer.CurrentCultureIgnoreCase.Compare(a.Program.Publisher ?? "￿", b.Program.Publisher ?? "￿"),
                ProgramSort.Newest => Nullable.Compare(b.Program.InstallDate, a.Program.InstallDate),
                ProgramSort.Largest => Nullable.Compare(b.Program.EstimatedSizeKb, a.Program.EstimatedSizeKb),
                _ => 0
            };

            return primary != 0 ? primary : byName;
        }
    }

    // ---- Actions ---------------------------------------------------------------

    private void RequestSession(bool leftoversOnly)
    {
        if (Analysis is not { } analysis) return;

        if (AccountBlock(analysis.Program) is { } reason)
        {
            StatusText = reason;
            return;
        }

        SessionRequested?.Invoke(this, new SessionRequest([analysis], leftoversOnly));
    }

    /// <summary>
    /// Elevated as an account other than the person signed in, the signed-in person's own per-user
    /// programs would be uninstalled as the wrong account, so they are refused here, with the
    /// reason. That account's own programs, labelled with its name, run as it, which is right.
    /// </summary>
    internal static string? AccountBlock(InstalledProgram program) => ProgramOwner.WhyNotHere(program);

    /// <summary>What Restart as admin will do: for a standard user, it switches to the administrator account.</summary>
    public string RelaunchAsAdminTooltip => AccountContext.Current.CanElevateAsSelf
        ? "Closes Uproot and opens it again with administrator rights, back on this window."
        : "Your account is a standard user, so this opens Uproot as the administrator account you sign in with. "
          + "Programs installed for everyone, and that account's own, can be removed there; ones installed just for you cannot.";

    public bool ShowOtherAccountNotice => AccountContext.Current.AsAnotherAccount;

    public string? OtherAccountNotice => AccountContext.Current.DescribeOtherAccount() is { } who
        ? $"{who} Programs installed for everyone can be uninstalled here, and so can {OtherAccountShort}'s own, which are "
          + $"labelled \"{OtherAccountShort} only\". Ones installed just for you cannot. Files removed here go to "
          + $"{OtherAccountShort}'s Recycle Bin, so restore them from there."
        : null;

    private static string OtherAccountShort =>
        ProgramOwner.OtherAccount is { } other ? InstalledProgram.ShortAccount(other) : "that account";

    /// <summary>Analyses every selected program, then opens one session that uninstalls them in turn.</summary>
    private async Task UninstallSelectedAsync()
    {
        var rows = SelectedRows.ToList();
        if (rows.Count < 2) return;

        _isPreparingBatch = true;
        StatusText = $"Examining {rows.Count} uninstallers…";

        try
        {
            var blocked = rows.Select(r => AccountBlock(r.Program)).FirstOrDefault(b => b is not null);
            if (blocked is not null)
            {
                StatusText = $"Nothing was started. {blocked}";
                return;
            }

            var analyses = new List<UninstallerProfile>();

            foreach (var row in rows)
            {
                if (!_analyses.TryGetValue(row.Program.Id, out var analysis))
                {
                    analysis = await Task.Run(() => UninstallerAnalyzer.Analyze(row.Program)).ConfigureAwait(true);
                    _analyses[row.Program.Id] = analysis;
                    row.ApplyAnalysis(analysis);
                }

                analyses.Add(analysis);
            }

            StatusText = string.Empty;
            SessionRequested?.Invoke(this, new SessionRequest(analyses, LeftoversOnly: false));
        }
        catch (Exception ex)
        {
            StatusText = $"Could not examine them all: {ex.Message}";
        }
        finally
        {
            _isPreparingBatch = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Starts a leftover search for a program that is no longer listed.</summary>
    public async Task SearchByNameAsync(string name, string? publisher, string? installFolder)
    {
        var program = InstalledProgram.ForName(name, publisher, installFolder);
        var analysis = await Task.Run(() => UninstallerAnalyzer.Analyze(program)).ConfigureAwait(true);
        SessionRequested?.Invoke(this, new SessionRequest([analysis], LeftoversOnly: true));
    }

    public void AfterSession(bool changedAnything)
    {
        if (changedAnything) _ = LoadAsync();
    }

    /// <summary>Writes the programs the list is showing, as filtered and sorted, to a CSV file.</summary>
    public void ExportTo(string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Name,Version,Publisher,Installed for,Type,Installer,Install date,Size (MB),Install location,Uninstall command,Registry entry");

        foreach (var row in ProgramsView.Cast<ProgramRowViewModel>())
        {
            var p = row.Program;
            string[] fields =
            [
                p.DisplayName,
                p.DisplayVersion ?? string.Empty,
                p.Publisher ?? string.Empty,
                p.IsStoreApp ? $"{p.InstalledFor} (Store app)" : p.InstalledFor,
                p.Is32Bit ? "32-bit" : string.Empty,
                row.TechnologyName,
                p.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                p.EstimatedSizeKb is { } kb ? (kb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) : string.Empty,
                p.InstallLocation ?? string.Empty,
                p.UninstallString ?? string.Empty,
                p.IsStoreApp ? string.Empty : p.Id
            ];

            builder.AppendLine(string.Join(",", fields.Select(Csv)));
        }

        // UTF-8 with a byte order mark, so Excel opens names with accents correctly.
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        StatusText = $"Exported {ProgramsView.Cast<object>().Count()} programs to {path}.";
    }

    /// <summary>
    /// A CSV field, quoted when it needs to be. A leading =, +, - or @ is prefixed with an
    /// apostrophe so a spreadsheet shows a program's name rather than running it as a formula.
    /// </summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private void OpenInstallFolder()
    {
        if (Analysis?.InstallLocation is not { } folder || !Directory.Exists(folder)) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"Could not open {folder}: {ex.Message}";
        }
    }

    private void OpenRegistryEntry()
    {
        if (Selected is { Program.IsStoreApp: false } row) RegistryEditor.OpenAt(row.Program.Key);
    }

    private void CopyName()
    {
        if (Selected is not { } row) return;

        try
        {
            System.Windows.Clipboard.SetText(row.Program.DisplayVersion is { } version ? $"{row.Name} {version}" : row.Name);
            StatusText = $"Copied \"{row.Name}\".";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText = "The clipboard is in use by another program.";
        }
    }

    private void RelaunchAsAdministrator()
    {
        var outcome = ElevationState.RelaunchAsAdministrator();
        StatusText = outcome.Message;

        if (outcome.Started) ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record SessionRequest(IReadOnlyList<UninstallerProfile> Analyses, bool LeftoversOnly);

/// <summary>
/// A filter choice. ToString is what the closed dropdown shows: the app's ComboBox template
/// presents the selected item directly, so DisplayMemberPath alone only affects the open list.
/// </summary>
public sealed record FilterOption(ProgramFilter Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record SortOption(ProgramSort Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One program in the list.</summary>
public sealed class ProgramRowViewModel : ObservableObject
{
    private InstallerTechnology _technology;

    public ProgramRowViewModel(InstalledProgram program)
    {
        Program = program;
        _technology = UninstallerAnalyzer.QuickDetect(program);

        var command = UninstallCommand.Parse(program.UninstallString);
        UninstallerMissing = !program.IsStoreApp && (command is null || (!command.IsMsiExec && !command.FileExists));
    }

    public InstalledProgram Program { get; }

    public string Name => Program.DisplayName;
    public string Summary => Program.Summary;
    public bool UninstallerMissing { get; private set; }
    public string TechnologyName => UninstallerAnalyzer.Describe(_technology);

    public IReadOnlyList<ChipViewModel> Chips
    {
        get
        {
            var chips = new List<ChipViewModel>();

            if (Program.IsStoreApp)
            {
                chips.Add(new ChipViewModel("Store app", LabelTone.Info, Program.Owner is { } storeOwner
                    ? $"A Microsoft Store (MSIX) app of {storeOwner}'s, the account Uproot is running as. Windows removes it itself."
                    : "A Microsoft Store (MSIX) app, installed for your account. Windows removes it itself."));
                if (Program.Owner is not null) chips.Add(OwnerChip());
            }
            else
            {
                chips.Add(Program.Scope == ProgramScope.Machine
                    ? new(Program.InstalledFor, LabelTone.Neutral, "Installed for everyone. Removing its leftovers needs administrator rights.")
                    : Program.Owner is not null
                        ? OwnerChip()
                        : new(Program.InstalledFor, LabelTone.Neutral, "Installed for your account only."));
                chips.Add(new(TechnologyName, LabelTone.Info, "The installer framework behind its uninstaller."));
            }

            if (Program.Is32Bit) chips.Add(new ChipViewModel("32-bit", LabelTone.Neutral, "Registered by a 32-bit installer."));
            if (UninstallerMissing) chips.Add(new ChipViewModel("Uninstaller missing", LabelTone.Warning, "Its uninstaller is gone. Remove its leftovers instead."));
            if (Program.IsSystemComponent || Program.IsUpdateOrComponent)
                chips.Add(new ChipViewModel("Hidden", LabelTone.Neutral, "Apps & Features does not list this: it is a system component, an update or part of a bundle."));

            return chips;
        }
    }

    /// <summary>Another account's program, in that account's name, so it never reads as yours.</summary>
    private ChipViewModel OwnerChip() => new(Program.InstalledFor, LabelTone.Warning,
        $"Installed just for {Program.Owner}, the account Uproot is running as — not for you. Uninstalling it removes "
        + $"it from {Program.Owner}, and its leftovers are looked for in that account's profile.");

    /// <summary>The full analysis can identify what the registry alone could not.</summary>
    public void ApplyAnalysis(UninstallerProfile analysis)
    {
        _technology = analysis.Technology;
        UninstallerMissing = analysis.UninstallerMissing;
        Raise(nameof(Chips));
        Raise(nameof(UninstallerMissing));
        Raise(nameof(TechnologyName));
    }
}

/// <summary>A small coloured label, reusing the update list's chip colours.</summary>
public sealed record ChipViewModel(string Text, LabelTone Tone, string Explanation);

public sealed class FindingViewModel(AnalysisFinding finding)
{
    public string Title { get; } = finding.Title;
    public string Detail { get; } = finding.Detail;

    public LabelTone Tone { get; } = finding.Tone switch
    {
        FindingTone.Good => LabelTone.Success,
        FindingTone.Warning => LabelTone.Warning,
        _ => LabelTone.Info
    };

    public string Marker => finding.Tone switch
    {
        FindingTone.Good => "✓",
        FindingTone.Warning => "!",
        _ => "i"
    };
}
