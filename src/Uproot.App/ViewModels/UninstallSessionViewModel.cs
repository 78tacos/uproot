using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using Uproot.Core.Elevation;
using Uproot.Core;

namespace Uproot.App.ViewModels;

public enum SessionStep
{
    Options,
    Running,
    Scanning,
    Results,
    Removing,
    Done
}

/// <summary>
/// One uninstall, start to finish — or several in a row: choose how hard to look, run each
/// program's own uninstaller, review what they left, remove what is chosen, and undo it if need be.
/// </summary>
public sealed class UninstallSessionViewModel : ObservableObject
{
    public const string ProtectedGroup = "Protected — found but kept";

    private readonly IReadOnlyList<UninstallSession> _sessions;
    private readonly HashSet<UninstallSession> _stillInstalled = [];
    private CancellationTokenSource? _stopWaiting;
    private CancellationTokenSource? _work;

    private SessionStep _step = SessionStep.Options;
    private ScanMode _mode = ScanMode.Moderate;
    private bool _includeAllProfiles;
    private bool _createRestorePoint;
    private bool _runUnattended;
    private bool _allowProtected;
    private string _statusText = string.Empty;
    private string _progressText = string.Empty;
    private string? _progressHeader;
    private int _foundCount;
    private int _protectedCount;
    private TimeSpan _elapsed;
    private bool _scanned;
    private RemovalReport? _report;
    private RestoreReport? _undo;
    private ScanMode _scannedMode;

    public UninstallSessionViewModel(UninstallerProfile analysis, bool leftoversOnly) : this([analysis], leftoversOnly)
    {
    }

    public UninstallSessionViewModel(IReadOnlyList<UninstallerProfile> analyses, bool leftoversOnly)
    {
        _sessions = analyses.Select(UninstallSession.FromAnalysis).ToList();
        LeftoversOnly = leftoversOnly;

        _includeAllProfiles = false;
        _createRestorePoint = IsElevated && !leftoversOnly && _sessions.Any(s => s.Program.Scope == ProgramScope.Machine && !s.Program.IsStoreApp);

        if (leftoversOnly)
        {
            foreach (var session in _sessions.Where(s => s.Program.IsRegistered)) _stillInstalled.Add(session);
        }

        Leftovers = [];
        LeftoversView = CollectionViewSource.GetDefaultView(Leftovers);
        LeftoversView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverRowViewModel.Group)));

        Outcomes = [];
        Notes = [];

        StartCommand = AsyncRelayCommand.Create(StartAsync, () => Step == SessionStep.Options);
        StopWaitingCommand = RelayCommand.Create(() => _stopWaiting?.Cancel(), () => Step == SessionStep.Running);
        CancelWorkCommand = RelayCommand.Create(() => _work?.Cancel(), () => Step is SessionStep.Scanning or SessionStep.Removing);
        RescanCommand = AsyncRelayCommand.Create(ScanAsync, () => Step == SessionStep.Results);
        SelectRecommendedCommand = RelayCommand.Create(() => Select(r => r.Item.Recommended && !r.StillInstalled));
        // Of several programs, "Select all" leaves out the things of any still installed: removing
        // those is a forced removal, and has to be chosen item by item or by its own group.
        SelectAllCommand = RelayCommand.Create(() => Select(r => !r.Item.IsProtected && !(IsBatch && r.StillInstalled)));
        SelectNoneCommand = RelayCommand.Create(() => Select(_ => false));
        SelectGroupCommand = new RelayCommand(p => SelectGroup(p as string, true));
        ClearGroupCommand = new RelayCommand(p => SelectGroup(p as string, false));
        OpenBackupCommand = RelayCommand.Create(OpenBackupFolder, () => _report?.BackupFolder is not null);
        OpenLocationCommand = new RelayCommand(p => OpenLocation(p as LeftoverRowViewModel));
        RestartAsAdminCommand = RelayCommand.Create(RestartAsAdministrator, () => !IsElevated);
        UndoCommand = AsyncRelayCommand.Create(UndoAsync, () => CanUndo);
    }

    // ---- Facts about the programs ------------------------------------------------

    public InstalledProgram Program => _sessions[0].Program;
    public UninstallerProfile Analysis => _sessions[0].Analysis;
    public bool IsBatch => _sessions.Count > 1;
    public int ProgramCount => _sessions.Count;
    public bool LeftoversOnly { get; }
    public bool IsElevated => ElevationState.IsElevated;

    public string Title => LeftoversOnly
        ? $"Remove leftovers of {Program.DisplayName}"
        : IsBatch ? $"Uninstall {_sessions.Count} programs" : $"Uninstall {Program.DisplayName}";

    public string Subtitle => Step switch
    {
        SessionStep.Options => LeftoversOnly
            ? "Choose how hard to look. Nothing is removed until you have reviewed what was found."
            : IsBatch
                ? "The programs' own uninstallers run one after another, then one scan covers what they all left. Nothing else is removed until you have reviewed it."
                : "Choose how hard to look afterwards. The program's own uninstaller runs first; nothing else is removed until you have reviewed it.",
        SessionStep.Running => IsBatch
            ? "The programs' own uninstallers are running in turn. Answer their questions in their windows."
            : "The program's own uninstaller is running. Answer its questions in its window.",
        SessionStep.Scanning => "Looking for what was left behind.",
        SessionStep.Results => "Review what was found. Pre-selected items are the ones the scan is confident about.",
        SessionStep.Removing => "Removing the selected items.",
        _ => "Finished."
    };

    /// <summary>For one program, what its analysis found; for several, a line on each.</summary>
    public IReadOnlyList<FindingViewModel> Findings => IsBatch
        ? _sessions.Select(s => new FindingViewModel(new AnalysisFinding(
            s.Analysis.CanUninstall ? FindingTone.Info : FindingTone.Warning,
            s.Program.DisplayName,
            s.Analysis.CanUninstall
                ? $"{s.Analysis.TechnologyName}. {(s.Analysis.QuietCommand is null ? "Its own dialogs will ask the questions." : "Can run unattended.")}"
                : "Its uninstaller is missing, so only its leftovers will be looked for."))).ToList()
        : Analysis.Findings.Select(f => new FindingViewModel(f)).ToList();

    public string FindingsHeading => IsBatch ? "THE PROGRAMS" : "WHAT THE ANALYSIS FOUND";

    // ---- Options ----------------------------------------------------------------

    public SessionStep Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;

            Raise(nameof(Subtitle));
            Raise(nameof(IsOptions));
            Raise(nameof(IsRunning));
            Raise(nameof(IsScanning));
            Raise(nameof(IsResults));
            Raise(nameof(IsRemoving));
            Raise(nameof(IsDone));
            Raise(nameof(IsBusy));
            Raise(nameof(StartButtonText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsOptions => Step == SessionStep.Options;
    public bool IsRunning => Step == SessionStep.Running;
    public bool IsScanning => Step == SessionStep.Scanning;
    public bool IsResults => Step == SessionStep.Results;
    public bool IsRemoving => Step == SessionStep.Removing;
    public bool IsDone => Step == SessionStep.Done;
    public bool IsBusy => Step is SessionStep.Running or SessionStep.Scanning or SessionStep.Removing;

    public string StartButtonText => LeftoversOnly ? "Scan for leftovers" : IsBatch ? $"Uninstall {_sessions.Count}" : "Uninstall";

    public bool IsSafe
    {
        get => _mode == ScanMode.Safe;
        set { if (value) Mode = ScanMode.Safe; }
    }

    public bool IsModerate
    {
        get => _mode == ScanMode.Moderate;
        set { if (value) Mode = ScanMode.Moderate; }
    }

    public bool IsExpert
    {
        get => _mode == ScanMode.Expert;
        set { if (value) Mode = ScanMode.Expert; }
    }

    public ScanMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(IsSafe));
            Raise(nameof(IsModerate));
            Raise(nameof(IsExpert));
            Raise(nameof(RescanNeeded));
        }
    }

    public IReadOnlyList<ScanMode> Modes { get; } = Enum.GetValues<ScanMode>();

    public bool IncludeAllProfiles
    {
        get => _includeAllProfiles;
        set => Set(ref _includeAllProfiles, value);
    }

    /// <summary>
    /// Whose profile is searched without "every profile": a per-user program's own account's when
    /// that is not yours — Uproot elevated as an administrator, uninstalling the administrator's
    /// program — and otherwise yours.
    /// </summary>
    private IReadOnlyList<string> SearchedProfiles => _sessions
        .Select(s => s.Program is { Scope: ProgramScope.User, Owner: { } owner } ? $"{InstalledProgram.ShortAccount(owner)}'s" : YoursName)
        .Distinct()
        .ToList();

    private static string YoursName =>
        AccountContext.Current is { AsAnotherAccount: true, SignedInAccount: { } you } ? $"yours ({InstalledProgram.ShortAccount(you)})" : "mine";

    public string AllProfilesLabel => SearchedProfiles.Count == 1
        ? $"Every user profile on this PC, not just {SearchedProfiles[0]}"
        : "Every user profile on this PC, not just the ones these programs are installed in";

    public string AllProfilesNote
    {
        get
        {
            var owners = _sessions.Select(s => s.Program)
                .Where(p => p is { Scope: ProgramScope.User, Owner: not null })
                .Select(p => InstalledProgram.ShortAccount(p.Owner!))
                .Distinct()
                .ToList();

            var whose = owners.Count == 0
                ? ""
                : $"{string.Join(" and ", owners)}'s profile is searched, where {(IsBatch ? "those are" : "it is")} installed. "
                  + "Yours is not, in case you have a copy of your own. ";

            return whose + (IsElevated
                ? "Every profile loads each account's registry hive for the scan and unloads it straight after."
                : "Every profile needs administrator rights: other people's profiles are not readable from a standard account.");
        }
    }

    public bool CreateRestorePoint
    {
        get => _createRestorePoint;
        set => Set(ref _createRestorePoint, value);
    }

    public string RestorePointNote => IsElevated
        ? "Windows allows one restore point a day; if one was made recently it declines, and the result says so."
        : "Needs administrator rights.";

    public bool CanRunUnattended => !LeftoversOnly && _sessions.Any(s => s.Analysis.QuietCommand is not null);

    public bool RunUnattended
    {
        get => _runUnattended;
        set => Set(ref _runUnattended, value);
    }

    public string UnattendedNote
    {
        get
        {
            if (IsBatch)
            {
                var quiet = _sessions.Count(s => s.Analysis.QuietCommand is not null);
                return quiet == _sessions.Count
                    ? "All of them can run without their dialogs."
                    : $"{quiet} of {_sessions.Count} can run without their dialogs; the rest will still show theirs.";
            }

            return Analysis.QuietCommand is { } command ? command.Display : "This uninstaller has no known unattended mode.";
        }
    }

    /// <summary>
    /// Shown before a machine-wide uninstall from a standard account. The uninstaller will ask for
    /// rights itself, but the leftovers afterwards could not be removed — and once the program is
    /// gone there is no entry left to restart and come back to.
    /// </summary>
    public bool ShowElevationWarning => !IsElevated && _sessions.Any(s => s.Program.Scope == ProgramScope.Machine);

    public string ElevationWarning => LeftoversOnly
        ? "Uproot is running as a standard user, so leftovers in Program Files, ProgramData and the machine-wide registry can be found but not removed."
        : "Uproot is running as a standard user. Uninstallers will ask for administrator rights themselves, but leftovers in Program Files, ProgramData and the machine-wide registry could then be found and not removed. Restart as administrator first to clean up fully.";

    public bool ShowStillInstalledWarning => LeftoversOnly && Program.IsRegistered;

    // ---- Progress -----------------------------------------------------------------

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>"2 of 3: Contoso Widget" while several programs are being worked through.</summary>
    public string? ProgressHeader
    {
        get => _progressHeader;
        private set => Set(ref _progressHeader, value);
    }

    public ObservableCollection<string> Notes { get; }

    // ---- Results ------------------------------------------------------------------

    public ObservableCollection<LeftoverRowViewModel> Leftovers { get; }
    public ICollectionView LeftoversView { get; }

    public bool HasLeftovers => Leftovers.Count > 0;

    public int SelectedCount => Leftovers.Count(l => l.IsSelected);

    public string SelectedSummary
    {
        get
        {
            var selected = Leftovers.Where(l => l.IsSelected).Select(l => l.Item).ToList();
            var bytes = LeftoverScanResult.TotalBytes(selected);
            var protectedCount = selected.Count(i => i.IsProtected);

            var text = bytes > 0 ? $"{selected.Count} selected, {Sizes.Format(bytes)}" : $"{selected.Count} selected";
            return protectedCount > 0 ? $"{text} ({protectedCount} protected)" : text;
        }
    }

    public string ResultSummary
    {
        get
        {
            if (!_scanned) return string.Empty;

            if (_foundCount == 0)
                return _protectedCount == 0 ? "Nothing was left behind." : $"Nothing removable was left behind; {_protectedCount} item(s) were found but are protected.";

            var line = $"{_foundCount} leftover{(_foundCount == 1 ? "" : "s")} found by a {_scannedMode.ToString().ToLowerInvariant()} scan in {_elapsed.TotalSeconds:0.0}s";
            if (_protectedCount > 0) line += $", plus {_protectedCount} protected";
            return line + ".";
        }
    }

    public bool RescanNeeded => _scanned && Mode != _scannedMode;

    public bool NeedsAdminForSelection => !IsElevated && Leftovers.Any(l => l.IsSelected && l.Item.NeedsElevation);

    public string RemoveButtonText => $"Remove selected ({SelectedCount})";

    /// <summary>Protected items a person may choose to remove anyway.</summary>
    public int OverridableCount => Leftovers.Count(r => r.Item.CanOverride);

    public bool HasOverridable => OverridableCount > 0;

    /// <summary>
    /// Lets protected items be selected, after a warning. Nothing protected is ever selected by
    /// this alone, and items protected absolutely stay out of reach.
    /// </summary>
    public bool AllowProtected
    {
        get => _allowProtected;
        set
        {
            if (value == _allowProtected) return;

            if (value && ConfirmOverride is not null && !ConfirmOverride(OverrideWarning))
            {
                // Declined: tell the checkbox it is still unticked.
                Raise();
                return;
            }

            _allowProtected = value;
            foreach (var row in Leftovers) row.AllowProtected = value;

            Raise();
            RaiseSelection();
        }
    }

    public string OverrideWarning =>
        $"{OverridableCount} item(s) are protected because they belong to another installed program, are shared with one, "
        + "or could not be tied to this program for certain.\n\n"
        + "Turning protection off lets you select them. Nothing is selected for you, and anything that is part of Windows "
        + "or a standard folder stays protected regardless.\n\n"
        + "Removing the wrong one can break another program. Registry items are backed up and files go to the Recycle Bin, "
        + "so a removal can be undone from Restore removed items.\n\n"
        + "Turn protection off for this list?";

    // ---- Done ---------------------------------------------------------------------

    public ObservableCollection<OutcomeRowViewModel> Outcomes { get; }

    public string DoneSummary => _undo is not null
        ? $"Undone: {_undo.Summary}." + (_undo.NeedsRestart > 0 ? " Restart Windows to finish." : string.Empty)
        : _report is null
            ? string.Empty
            : $"{_report.Summary}." + (_report.AtRestart > 0 ? " Restart Windows to finish." : string.Empty);

    public string? BackupFolder => _report?.BackupFolder;

    public bool CanUndo => Step == SessionStep.Done && _undo is null && _report?.BackupFolder is not null
                           && _report.Outcomes.Any(o => o.Status is RemovalStatus.Removed or RemovalStatus.RemovedAtRestart);

    /// <summary>True once anything on the machine has changed, so the program list should be reloaded.</summary>
    public bool ChangedAnything { get; private set; }

    // ---- Commands -----------------------------------------------------------------

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopWaitingCommand { get; }
    public RelayCommand CancelWorkCommand { get; }
    public AsyncRelayCommand RescanCommand { get; }
    public RelayCommand SelectRecommendedCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand SelectGroupCommand { get; }
    public RelayCommand ClearGroupCommand { get; }
    public RelayCommand OpenBackupCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>Asked before removing anything; the window shows the confirmation.</summary>
    public Func<string, bool>? ConfirmRemoval { get; set; }

    /// <summary>Asked before protection is turned off; the window shows the warning.</summary>
    public Func<string, bool>? ConfirmOverride { get; set; }

    /// <summary>Asked before undoing the removal.</summary>
    public Func<string, bool>? ConfirmUndo { get; set; }

    /// <summary>Raised when the app should shut down so an elevated instance can take over.</summary>
    public event EventHandler? ExitRequested;

    // ---- Flow ---------------------------------------------------------------------

    private async Task StartAsync()
    {
        Notes.Clear();

        // A retry starts from what is true now, not from what the last attempt found.
        _stillInstalled.Clear();
        if (LeftoversOnly)
        {
            foreach (var session in _sessions.Where(s => s.Program.IsRegistered)) _stillInstalled.Add(session);
        }

        try
        {
            if (!LeftoversOnly && !await RunUninstallersAsync().ConfigureAwait(true)) return;
            await ScanAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // An async command has nowhere to report a failure; leave the window somewhere usable.
            StatusText = $"Something went wrong: {ex.Message}";
            ProgressHeader = null;
            Step = _scanned ? SessionStep.Results : SessionStep.Options;
        }
    }

    /// <summary>
    /// Runs each program's uninstaller in turn. False when nothing was started, and the window
    /// has gone back to its options.
    /// </summary>
    private async Task<bool> RunUninstallersAsync()
    {
        if (CreateRestorePoint && IsElevated)
        {
            Step = SessionStep.Running;
            ProgressText = "Creating a System Restore point…";

            var description = IsBatch ? $"Before uninstalling {_sessions.Count} programs" : $"Before uninstalling {Program.DisplayName}";
            var restore = await RestorePoint.CreateAsync(description).ConfigureAwait(true);
            Notes.Add(restore.Message);
        }

        Step = SessionStep.Running;
        var started = 0;

        for (var i = 0; i < _sessions.Count; i++)
        {
            var session = _sessions[i];
            ProgressHeader = IsBatch ? $"{i + 1} of {_sessions.Count}: {session.Program.DisplayName}" : null;

            if (!session.Analysis.CanUninstall)
            {
                // A missing uninstaller does not make a program uninstalled: its leftovers are a
                // forced removal, and nothing of its is pre-selected.
                if (await Task.Run(session.IsStillInstalled).ConfigureAwait(true)) _stillInstalled.Add(session);
                Notes.Add($"{session.Program.DisplayName}: its uninstaller is missing, so only its leftovers are looked for, and none are pre-selected.");
                continue;
            }

            _stopWaiting = new CancellationTokenSource();
            var progress = new Progress<string>(s => ProgressText = s);
            var quiet = RunUnattended && session.Analysis.QuietCommand is not null;
            var run = await session.RunUninstallerAsync(quiet, progress, _stopWaiting.Token).ConfigureAwait(true);

            if (!run.Started || run.Cancelled)
            {
                if (!IsBatch)
                {
                    StatusText = run.Message;
                    ProgressHeader = null;
                    Step = SessionStep.Options;
                    return false;
                }

                _stillInstalled.Add(session);
                Notes.Add($"{session.Program.DisplayName}: {run.Message}");
                continue;
            }

            started++;
            ChangedAnything = true;
            Notes.Add(IsBatch ? $"{session.Program.DisplayName}: {run.Message}" : run.Message);

            if (await Task.Run(session.IsStillInstalled).ConfigureAwait(true))
            {
                _stillInstalled.Add(session);
                Notes.Add($"{session.Program.DisplayName} still appears to be installed — its uninstaller may have been cancelled or failed. "
                          + "Nothing of its is pre-selected, because removing it now would be a forced removal.");
            }

            // Stopped waiting, that uninstaller may still be running; starting the next beside it
            // would collide with it — Windows Installer takes one at a time. The rest wait.
            if (run.StoppedWaiting && i < _sessions.Count - 1)
            {
                foreach (var rest in _sessions.Skip(i + 1)) _stillInstalled.Add(rest);
                Notes.Add($"You stopped waiting for {session.Program.DisplayName}, so the remaining "
                          + $"{_sessions.Count - i - 1} program(s) were not started. Uninstall them again once it has finished.");
                break;
            }
        }

        ProgressHeader = null;

        if (IsBatch && started == 0 && _sessions.All(s => s.Analysis.CanUninstall))
        {
            StatusText = "None of the uninstallers ran.";
            Step = SessionStep.Options;
            return false;
        }

        return true;
    }

    private async Task ScanAsync()
    {
        Step = SessionStep.Scanning;
        ProgressText = "Preparing the scan…";
        _work = new CancellationTokenSource();

        try
        {
            var rows = new Dictionary<string, LeftoverRowViewModel>(StringComparer.Ordinal);
            var notes = new List<string>();
            var elapsed = TimeSpan.Zero;

            for (var i = 0; i < _sessions.Count; i++)
            {
                var session = _sessions[i];
                var prefix = IsBatch ? $"{i + 1} of {_sessions.Count}: " : string.Empty;
                var progress = new Progress<string>(s => ProgressText = prefix + s + "…");
                var stillInstalled = _stillInstalled.Contains(session);

                var result = await session.ScanAsync(new LeftoverScanOptions
                {
                    Mode = Mode,
                    IncludeAllProfiles = IncludeAllProfiles && IsElevated,
                    ProgramStillInstalled = stillInstalled
                }, progress, _work.Token).ConfigureAwait(true);

                elapsed += result.Elapsed;
                notes.AddRange(result.Notes.Select(n => IsBatch ? $"{session.Program.DisplayName}: {n}" : n));

                foreach (var item in result.Items)
                {
                    // Found for two programs, it is one item: protected if either scan protected it
                    // (kept always if either says so), and not pre-selected if either program is
                    // still installed.
                    if (rows.TryGetValue(item.Identity, out var existing))
                    {
                        var firmer = Firmer(existing.Item, item);
                        rows[item.Identity] = new LeftoverRowViewModel(firmer, IsElevated, existing.Program, existing.StillInstalled || stillInstalled)
                        {
                            AllowProtected = AllowProtected
                        };
                        continue;
                    }

                    rows[item.Identity] = new LeftoverRowViewModel(item, IsElevated, IsBatch ? session.Program.DisplayName : null, stillInstalled)
                    {
                        AllowProtected = AllowProtected
                    };
                }
            }

            _scannedMode = Mode;
            _scanned = true;
            _elapsed = elapsed;

            // Protection comes back on with every new list: turning it off was agreed to for the
            // items shown then, not for whatever a rescan finds.
            if (_allowProtected)
            {
                _allowProtected = false;
                Raise(nameof(AllowProtected));
                foreach (var row in rows.Values) row.AllowProtected = false;
            }

            foreach (var row in Leftovers) row.PropertyChanged -= OnRowChanged;
            Leftovers.Clear();

            foreach (var row in rows.Values)
            {
                row.IsSelected = row.Item.Recommended && !row.StillInstalled;
                row.PropertyChanged += OnRowChanged;
                Leftovers.Add(row);
            }

            _foundCount = Leftovers.Count(r => !r.Item.IsProtected);
            _protectedCount = Leftovers.Count - _foundCount;

            foreach (var note in notes.Where(n => !Notes.Contains(n))) Notes.Add(note);

            StatusText = string.Empty;
            Step = SessionStep.Results;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
            Step = _scanned ? SessionStep.Results : SessionStep.Options;
        }
        catch (Exception ex)
        {
            StatusText = $"The scan failed: {ex.Message}";
            Step = _scanned ? SessionStep.Results : SessionStep.Options;
        }
        finally
        {
            RaiseResults();
        }
    }

    /// <summary>Of the same item found for two programs, the one more firmly protected.</summary>
    private static LeftoverItem Firmer(LeftoverItem a, LeftoverItem b)
    {
        if (a.KeptAlways != b.KeptAlways) return a.KeptAlways ? a : b;
        if (a.IsProtected != b.IsProtected) return a.IsProtected ? a : b;
        return a;
    }

    public async Task RemoveAsync()
    {
        var chosen = Leftovers.Where(l => l.IsSelected && l.CanSelect).Select(l => l.Item).ToList();
        if (chosen.Count == 0) return;

        var question = $"Remove {chosen.Count} item(s)?\n\n"
                       + "Registry keys and values are exported to .reg files first, scheduled tasks and firewall rules are saved, "
                       + "and files and folders go to the Recycle Bin. The removal can be undone afterwards.";

        if (chosen.Any(i => i.Confidence == LeftoverConfidence.Possible))
            question += "\n\nSome of these are only possible matches. Make sure you have looked at each one.";

        var forced = Leftovers.Count(l => l.IsSelected && l.CanSelect && l.StillInstalled);
        if (forced > 0)
            question += $"\n\n{forced} of them belong to a program that is still installed. Removing them breaks it rather than uninstalling it.";

        var overridden = chosen.Where(i => i.IsProtected).ToList();
        if (overridden.Count > 0)
        {
            question += $"\n\n{overridden.Count} of them are PROTECTED and you have chosen to remove them anyway:\n"
                        + string.Join("\n", overridden.Take(5).Select(i => $"  • {i.DisplayPath}"))
                        + (overridden.Count > 5 ? $"\n  … and {overridden.Count - 5} more" : string.Empty)
                        + "\nThis can break the programs they belong to.";
        }

        if (ConfirmRemoval is not null && !ConfirmRemoval(question)) return;

        Step = SessionStep.Removing;
        _work = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(s => ProgressText = s);
            var options = new RemovalOptions { AllowProtected = AllowProtected && overridden.Count > 0 };
            var report = await UninstallSession.RemoveAsync(_sessions, chosen, options, progress, _work.Token).ConfigureAwait(true);

            _report = report;
            ChangedAnything = true;

            Outcomes.Clear();
            foreach (var outcome in report.Outcomes.OrderBy(o => o.Status == RemovalStatus.Removed))
                Outcomes.Add(new OutcomeRowViewModel(outcome));

            Step = SessionStep.Done;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped. Anything already removed stays removed; rescan to see what is left.";
            Step = SessionStep.Results;
        }
        catch (Exception ex)
        {
            StatusText = $"Removal failed: {ex.Message}";
            Step = SessionStep.Results;
        }
        finally
        {
            Raise(nameof(DoneSummary));
            Raise(nameof(BackupFolder));
            Raise(nameof(CanUndo));
        }
    }

    /// <summary>Puts back everything the removal just took, from its backups and the Recycle Bin.</summary>
    private async Task UndoAsync()
    {
        if (_report?.BackupFolder is not { } folder || RemovalHistory.Read(folder) is not { } record) return;

        var items = record.Restorable;
        if (items.Count == 0)
        {
            StatusText = "There is nothing in this removal's backup to put back.";
            return;
        }

        if (ConfirmUndo is not null
            && !ConfirmUndo($"Put back the {items.Count} item(s) this removal took?\n\nRegistry items come back from their backups, and files from the Recycle Bin."))
        {
            return;
        }

        StatusText = "Putting things back…";

        try
        {
            _undo = await UninstallSession.RestoreAsync(record, items).ConfigureAwait(true);

            Outcomes.Clear();
            foreach (var outcome in _undo.Outcomes.OrderBy(o => o.Status == RestoreStatus.Restored))
                Outcomes.Add(new OutcomeRowViewModel(outcome));

            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not undo it: {ex.Message}";
        }
        finally
        {
            Raise(nameof(DoneSummary));
            Raise(nameof(CanUndo));
        }
    }

    // ---- Selection ----------------------------------------------------------------

    private void Select(Func<LeftoverRowViewModel, bool> predicate)
    {
        foreach (var row in Leftovers.Where(r => r.CanSelect)) row.IsSelected = predicate(row);
        RaiseSelection();
    }

    /// <summary>
    /// A group's own "Select all". Only the protected group's link can select protected items,
    /// and only once protection is off: that is the one deliberate way to choose them in bulk.
    /// </summary>
    private void SelectGroup(string? group, bool selected)
    {
        foreach (var row in Leftovers.Where(r => r.CanSelect && r.Group == group)) row.IsSelected = selected;
        RaiseSelection();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeftoverRowViewModel.IsSelected)) RaiseSelection();
    }

    private void RaiseSelection()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(SelectedSummary));
        Raise(nameof(RemoveButtonText));
        Raise(nameof(NeedsAdminForSelection));
    }

    private void RaiseResults()
    {
        Raise(nameof(HasLeftovers));
        Raise(nameof(ResultSummary));
        Raise(nameof(RescanNeeded));
        Raise(nameof(OverridableCount));
        Raise(nameof(HasOverridable));
        RaiseSelection();
    }

    // ---- Other actions --------------------------------------------------------------

    private void OpenBackupFolder()
    {
        if (_report?.BackupFolder is { } folder && Directory.Exists(folder)) Open("explorer.exe", folder);
    }

    /// <summary>Shows an item where it lives: Explorer for files, Registry Editor for keys, the right console otherwise.</summary>
    private void OpenLocation(LeftoverRowViewModel? row)
    {
        if (row is null) return;
        var item = row.Item;

        switch (item.Kind)
        {
            case LeftoverKind.Folder when Directory.Exists(item.Path):
                Open("explorer.exe", item.Path);
                break;

            case LeftoverKind.File when File.Exists(item.Path):
                Open("explorer.exe", $"/select,\"{item.Path}\"");
                break;

            case LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.PathEntry when item.RegistryKey is { } key:
                RegistryEditor.OpenAt(key);
                break;

            case LeftoverKind.Service:
                Open("services.msc", null);
                break;

            case LeftoverKind.ScheduledTask:
                Open("taskschd.msc", null);
                break;

            case LeftoverKind.FirewallRule:
                Open("wf.msc", null);
                break;
        }
    }

    private void Open(string file, string? argument)
    {
        try
        {
            var startInfo = new ProcessStartInfo(file) { UseShellExecute = true };
            if (argument is not null) startInfo.Arguments = argument;
            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"Could not open it: {ex.Message}";
        }
    }

    private void RestartAsAdministrator()
    {
        var outcome = ElevationState.RelaunchAsAdministrator();
        StatusText = outcome.Message;

        if (outcome.Started) ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One leftover in the review list.</summary>
public sealed class LeftoverRowViewModel(LeftoverItem item, bool elevated, string? program, bool stillInstalled) : ObservableObject
{
    private bool _isSelected;
    private bool _allowProtected;

    public LeftoverItem Item { get; } = item;

    /// <summary>The program it was found for, when several are being removed together.</summary>
    public string? Program { get; } = program;

    /// <summary>Its program is still installed, so it is not pre-selected.</summary>
    public bool StillInstalled { get; } = stillInstalled;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value && CanSelect);
    }

    /// <summary>Protection is off for the list this row is in.</summary>
    public bool AllowProtected
    {
        get => _allowProtected;
        set
        {
            if (!Set(ref _allowProtected, value)) return;

            if (!CanSelect) IsSelected = false;
            Raise(nameof(CanSelect));
            Raise(nameof(Chips));
        }
    }

    public bool CanSelect => !Item.IsProtected || (AllowProtected && Item.CanOverride);

    public string Group => Item.IsProtected ? UninstallSessionViewModel.ProtectedGroup : Item.CategoryName;
    public string Path => Item.DisplayPath;
    public string KindName => Item.KindName;
    public string Detail => Item.ProtectedBecause ?? Item.Reason;
    public string? Caution => Item.Caution;

    public IReadOnlyList<ChipViewModel> Chips
    {
        get
        {
            var chips = new List<ChipViewModel>();

            if (Item.KeptAlways)
            {
                chips.Add(new ChipViewModel("Always kept", LabelTone.Neutral,
                    "Part of Windows, a standard folder, or holding something that must stay. It cannot be removed, even with protection off."));
            }
            else if (Item.IsProtected)
            {
                chips.Add(AllowProtected
                    ? new ChipViewModel("Protected", LabelTone.Danger, "Protection is off: you can select this. " + Item.ProtectedBecause)
                    : new ChipViewModel("Protected", LabelTone.Neutral, "Kept unless you turn protection off. " + Item.ProtectedBecause));
            }
            else
            {
                chips.Add(Item.Confidence switch
                {
                    LeftoverConfidence.Certain => new ChipViewModel("Certain", LabelTone.Success,
                        "Recorded by the installer, or points into the program's own folder."),
                    LeftoverConfidence.Likely => new ChipViewModel("Likely", LabelTone.Info,
                        "Named after the program, in a place programs keep their own things."),
                    _ => new ChipViewModel("Possible", LabelTone.Warning,
                        "A partial match. Look at it before removing it.")
                });
            }

            if (Program is { } program) chips.Add(new ChipViewModel(program, LabelTone.Info, "Found for this program."));
            if (StillInstalled && !Item.IsProtected)
                chips.Add(new ChipViewModel("Still installed", LabelTone.Warning, "Its program is still installed: removing this breaks it rather than uninstalling it."));
            if (Item.SizeBytes is > 0 and var size) chips.Add(new ChipViewModel(Sizes.Format(size), LabelTone.Neutral, "Disk space it takes."));
            if (Item.Owner is { } owner) chips.Add(new ChipViewModel(owner, LabelTone.Neutral, $"In {owner}'s profile."));
            if (Item.OnlyIfEmpty) chips.Add(new ChipViewModel("If empty", LabelTone.Neutral, "Removed only if nothing else is left in it when its turn comes."));
            if (Item.NeedsElevation && !elevated && CanSelect)
                chips.Add(new ChipViewModel("Needs admin", LabelTone.Warning, "Removing this needs Uproot to run as administrator."));

            return chips;
        }
    }
}

/// <summary>One line in the results of a removal or a restore.</summary>
public sealed class OutcomeRowViewModel
{
    public OutcomeRowViewModel(RemovalOutcome outcome)
    {
        Path = outcome.Item.DisplayPath;
        Message = outcome.Message;
        Status = outcome.Status switch
        {
            RemovalStatus.Removed => new ChipViewModel("Removed", outcome.Overridden ? LabelTone.Warning : LabelTone.Success, outcome.Message),
            RemovalStatus.RemovedAtRestart => new ChipViewModel("At restart", LabelTone.Info, outcome.Message),
            RemovalStatus.Skipped => new ChipViewModel("Skipped", LabelTone.Neutral, outcome.Message),
            RemovalStatus.Refused => new ChipViewModel("Refused", LabelTone.Warning, outcome.Message),
            _ => new ChipViewModel("Failed", LabelTone.Danger, outcome.Message)
        };
    }

    public OutcomeRowViewModel(RestoreOutcome outcome)
    {
        Path = outcome.Item.DisplayPath ?? outcome.Item.Path;
        Message = outcome.Message;
        Status = outcome.Status switch
        {
            RestoreStatus.Restored => new ChipViewModel("Restored", LabelTone.Success, outcome.Message),
            RestoreStatus.RestoredNeedsRestart => new ChipViewModel("At restart", LabelTone.Info, outcome.Message),
            RestoreStatus.Skipped => new ChipViewModel("Skipped", LabelTone.Neutral, outcome.Message),
            RestoreStatus.Refused => new ChipViewModel("Refused", LabelTone.Warning, outcome.Message),
            _ => new ChipViewModel("Failed", LabelTone.Danger, outcome.Message)
        };
    }

    public string Path { get; }
    public string Message { get; }
    public ChipViewModel Status { get; }
}
