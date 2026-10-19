using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Uproot.Core.Elevation;
using Uproot.Core;

namespace Uproot.App.ViewModels;

/// <summary>Past removals, and putting back what one of them took.</summary>
public sealed class RestoreViewModel : ObservableObject
{
    private RemovalRowViewModel? _selected;
    private bool _isRestoring;
    private string _statusText = string.Empty;
    private RestoreReport? _report;

    public RestoreViewModel()
    {
        Removals = [];
        Items = [];

        RefreshCommand = RelayCommand.Create(Load, () => !IsRestoring);
        RestoreCommand = AsyncRelayCommand.Create(RestoreAsync, () => !IsRestoring && Items.Any(i => i.IsSelected));
        OpenFolderCommand = RelayCommand.Create(OpenFolder, () => Selected is not null);
        OpenRecycleBinCommand = RelayCommand.Create(() => Open("explorer.exe", "shell:RecycleBinFolder"));
        SelectAllCommand = RelayCommand.Create(() => { foreach (var item in Items.Where(i => i.CanSelect)) item.IsSelected = true; Raise(nameof(SelectedSummary)); });
        SelectNoneCommand = RelayCommand.Create(() => { foreach (var item in Items) item.IsSelected = false; Raise(nameof(SelectedSummary)); });
        RestartAsAdminCommand = AsyncRelayCommand.Create(RestartAsAdministrator, () => !IsElevated);
    }

    public ObservableCollection<RemovalRowViewModel> Removals { get; }
    public ObservableCollection<RestoreItemRowViewModel> Items { get; }

    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenRecycleBinCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public AsyncRelayCommand RestartAsAdminCommand { get; }

    /// <summary>Asked before restoring; the window shows the confirmation.</summary>
    public Func<string, bool>? ConfirmRestore { get; set; }

    /// <summary>Raised when the app should shut down so an elevated instance can take over.</summary>
    public event EventHandler? ExitRequested;

    public bool IsElevated => ElevationState.IsElevated;

    public bool HasRemovals => Removals.Count > 0;

    public RemovalRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _report = null;
            ShowItems();
        }
    }

    public bool IsRestoring
    {
        get => _isRestoring;
        private set
        {
            Set(ref _isRestoring, value);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SelectedSummary => $"{Items.Count(i => i.IsSelected)} of {Items.Count(i => i.CanSelect)} selected";

    public string? DetailHeading => Selected is null
        ? null
        : $"{Selected.Title} — removed {Selected.Record.Manifest.RemovedAt.LocalDateTime:f}";

    public string? DetailNote
    {
        get
        {
            if (Selected is null) return null;
            var record = Selected.Record;

            if (record.IsOldFormat)
                return "Made by an earlier version, which did not record which backup belongs to which item. Open the folder and double-click a .reg file to put a key back; files are in the Recycle Bin.";

            if (IsElevated)
            {
                return "Registry items, tasks, services and firewall rules come back from their backups. Files are not moved back "
                       + "while Uproot runs as administrator — restore those from the Recycle Bin, or from Uproot without "
                       + "administrator rights.";
            }

            return record.Manifest.Elevated
                ? "This removal was made as administrator. Files can be put back from here; machine-wide items need Uproot to run as administrator."
                : "Registry items come back from the .reg files in the backup folder, tasks and firewall rules from their saved copies, and files from the Recycle Bin.";
        }
    }

    public string ResultSummary => _report is null ? string.Empty : $"{_report.Summary}." + (_report.NeedsRestart > 0 ? " Restart Windows to finish." : string.Empty);

    public void Load()
    {
        var previous = Selected?.Record.Folder;

        Removals.Clear();
        foreach (var record in RemovalHistory.List()) Removals.Add(new RemovalRowViewModel(record));

        Raise(nameof(HasRemovals));
        Selected = Removals.FirstOrDefault(r => r.Record.Folder == previous) ?? Removals.FirstOrDefault();
        if (Removals.Count == 0) ShowItems();
    }

    /// <summary>Selects a particular removal, for arriving straight from one just made.</summary>
    public void Select(string folder)
    {
        if (Removals.FirstOrDefault(r => string.Equals(r.Record.Folder, folder, StringComparison.OrdinalIgnoreCase)) is { } row)
            Selected = row;
    }

    private void ShowItems()
    {
        foreach (var item in Items) item.PropertyChanged -= OnItemChanged;
        Items.Clear();

        if (Selected is { } row)
        {
            foreach (var item in row.Record.Removed)
            {
                var itemRow = new RestoreItemRowViewModel(item, row.Record.IsOldFormat);
                itemRow.PropertyChanged += OnItemChanged;
                Items.Add(itemRow);
            }
        }

        Raise(nameof(DetailHeading));
        Raise(nameof(DetailNote));
        Raise(nameof(ResultSummary));
        Raise(nameof(SelectedSummary));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RestoreItemRowViewModel.IsSelected))
        {
            Raise(nameof(SelectedSummary));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RestoreAsync()
    {
        if (Selected is not { } row) return;

        var chosen = Items.Where(i => i.IsSelected).ToList();
        if (chosen.Count == 0) return;

        if (ConfirmRestore is not null && !ConfirmRestore($"Put back {chosen.Count} item(s) from {row.Title}?")) return;

        IsRestoring = true;
        StatusText = "Putting things back…";

        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            _report = await UninstallSession.RestoreAsync(row.Record, chosen.Select(c => c.Item).ToList(), progress).ConfigureAwait(true);

            foreach (var outcome in _report.Outcomes)
            {
                var itemRow = Items.FirstOrDefault(i => ReferenceEquals(i.Item, outcome.Item));
                itemRow?.Apply(outcome);
            }

            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsRestoring = false;
            Raise(nameof(ResultSummary));
            Raise(nameof(SelectedSummary));
        }
    }

    private void OpenFolder()
    {
        if (Selected?.Record.Folder is { } folder && Directory.Exists(folder)) Open("explorer.exe", folder);
    }

    private void Open(string file, string argument)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { ArgumentList = { argument }, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"Could not open it: {ex.Message}";
        }
    }

    private async Task RestartAsAdministrator()
    {
        var outcome = await InstallFlow.RestartAsAdministratorAsync();
        StatusText = outcome.Message;

        if (outcome.Started) ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One past removal in the list.</summary>
public sealed class RemovalRowViewModel(RemovalRecord record)
{
    public RemovalRecord Record { get; } = record;

    public string Title => Record.Title;

    public string Summary
    {
        get
        {
            var removed = Record.Removed.Count;
            var restorable = Record.IsOldFormat ? "restore by hand" : $"{Record.Restorable.Count} can be restored";
            return $"{Record.Manifest.RemovedAt.LocalDateTime:g}  ·  {removed} removed  ·  {restorable}";
        }
    }

    public IReadOnlyList<ChipViewModel> Chips =>
        Record.Manifest.Elevated
            ? [new ChipViewModel("As administrator", LabelTone.Neutral, "Made while Uproot ran as administrator.")]
            : [];
}

/// <summary>One item a removal took, with the outcome of restoring it once that has been tried.</summary>
public sealed class RestoreItemRowViewModel : ObservableObject
{
    private readonly bool _oldFormat;
    private bool _isSelected;
    private ChipViewModel? _outcome;
    private string? _message;

    public RestoreItemRowViewModel(ManifestItem item, bool oldFormat)
    {
        Item = item;
        _oldFormat = oldFormat;
        CanSelect = !oldFormat && RemovalRestorer.CanTry(item);
        _isSelected = CanSelect;
    }

    public ManifestItem Item { get; }
    public bool CanSelect { get; private set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value && CanSelect);
    }

    public string Path => Item.DisplayPath ?? Item.Path;

    public string KindName => Item.ParsedKind switch
    {
        LeftoverKind.File => "File",
        LeftoverKind.Folder => "Folder",
        LeftoverKind.RegistryKey => "Registry key",
        LeftoverKind.RegistryValue => "Registry value",
        LeftoverKind.Service => "Service",
        LeftoverKind.ScheduledTask => "Scheduled task",
        LeftoverKind.PathEntry => "PATH entry",
        LeftoverKind.FirewallRule => "Firewall rule",
        _ => Item.Kind
    };

    public string Detail => _message
                            ?? (_oldFormat
                                ? "Recorded by an earlier version: put it back by hand from the backup folder or the Recycle Bin."
                                : CanSelect
                                    ? Item.Overridden ? $"{Item.Reason} Removed although it was protected." : Item.Reason ?? string.Empty
                                    : Item.Recycled || Item.ParsedKind is not (LeftoverKind.File or LeftoverKind.Folder)
                                        ? "Nothing was backed up for this."
                                        : "Deleted outright rather than sent to the Recycle Bin, so it cannot be restored.");

    public ChipViewModel? Outcome
    {
        get => _outcome;
        private set => Set(ref _outcome, value);
    }

    public void Apply(RestoreOutcome outcome)
    {
        Outcome = outcome.Status switch
        {
            RestoreStatus.Restored => new ChipViewModel("Restored", LabelTone.Success, outcome.Message),
            RestoreStatus.RestoredNeedsRestart => new ChipViewModel("At restart", LabelTone.Info, outcome.Message),
            RestoreStatus.Skipped => new ChipViewModel("Skipped", LabelTone.Neutral, outcome.Message),
            RestoreStatus.Refused => new ChipViewModel("Refused", LabelTone.Warning, outcome.Message),
            _ => new ChipViewModel("Failed", LabelTone.Danger, outcome.Message)
        };

        _message = outcome.Message;
        Raise(nameof(Detail));

        if (outcome.Status is RestoreStatus.Restored or RestoreStatus.RestoredNeedsRestart or RestoreStatus.Skipped)
        {
            IsSelected = false;
            CanSelect = false;
            Raise(nameof(CanSelect));
        }
    }
}
