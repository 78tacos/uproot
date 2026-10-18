using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Uproot.Core;

public enum RemovalStatus
{
    Removed,

    /// <summary>In use; Windows will delete it during the next restart.</summary>
    RemovedAtRestart,

    /// <summary>Already gone, or a folder/key that was not empty when its turn came.</summary>
    Skipped,

    /// <summary>The safety policy refused it at the last moment.</summary>
    Refused,

    Failed
}

public sealed record RemovalOutcome(LeftoverItem Item, RemovalStatus Status, string Message)
{
    /// <summary>The file in the backup folder that puts this back: a .reg export, task XML or firewall rules.</summary>
    public string? BackupFile { get; init; }

    /// <summary>Removed although it was protected, because the person turned protection off.</summary>
    public bool Overridden { get; init; }

    /// <summary>Went to the Recycle Bin, from where it can be restored.</summary>
    public bool Recycled { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

public sealed record RemovalReport(IReadOnlyList<RemovalOutcome> Outcomes, string? BackupFolder)
{
    public int Removed => Outcomes.Count(o => o.Status == RemovalStatus.Removed);
    public int AtRestart => Outcomes.Count(o => o.Status == RemovalStatus.RemovedAtRestart);
    public int Failed => Outcomes.Count(o => o.Status is RemovalStatus.Failed or RemovalStatus.Refused);
    public int Skipped => Outcomes.Count(o => o.Status == RemovalStatus.Skipped);

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Removed} removed" };
            if (AtRestart > 0) parts.Add($"{AtRestart} at next restart");
            if (Skipped > 0) parts.Add($"{Skipped} skipped");
            if (Failed > 0) parts.Add($"{Failed} could not be removed");
            return string.Join(", ", parts);
        }
    }
}

public sealed record RemovalOptions
{
    /// <summary>Send files and folders to the Recycle Bin rather than deleting them outright.</summary>
    public bool UseRecycleBin { get; init; } = true;

    /// <summary>Where registry and task backups go. Null for the default under LocalAppData.</summary>
    public string? BackupRoot { get; init; }

    /// <summary>Used to name the backup folder.</summary>
    public string? ProgramName { get; init; }

    /// <summary>
    /// Remove items the scan marked protected, where protection can be overridden, because the
    /// person chose to after being warned. Items protected absolutely are refused regardless, and
    /// so is anything that has become protected since the scan: nobody has seen why.
    /// </summary>
    public bool AllowProtected { get; init; }
}

/// <summary>
/// Removes leftovers, safely and recoverably.
///
/// Each item is checked against the safety policy again immediately before it is touched, so a
/// list that was edited, stale or tampered with still cannot reach a protected location. Registry
/// keys and values are exported to .reg files first, scheduled tasks to XML and firewall rules to
/// JSON, into a dated backup folder; files go to the Recycle Bin. Services and tasks go first,
/// because they hold files open; empty-only folders and keys go last, deepest first, once their
/// contents have gone.
/// </summary>
public sealed class LeftoverRemover(LeftoverSafety safety, IFirewall? firewall = null)
{
    private readonly IFirewall _firewall = firewall ?? WindowsFirewall.Instance;

    public Task<RemovalReport> RemoveAsync(
        IReadOnlyList<LeftoverItem> items, RemovalOptions options, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Remove(items, options, progress, ct), ct);

    public RemovalReport Remove(
        IReadOnlyList<LeftoverItem> items, RemovalOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var backup = new RemovalBackup(options);
        var outcomes = new List<RemovalOutcome>();

        var ordered = items
            .OrderBy(i => i.OnlyIfEmpty)
            .ThenBy(i => Order(i.Kind))
            .ThenByDescending(i => i.DisplayPath.Length)
            .ToList();

        // Hives of other accounts the scan loaded have to be loaded again to remove from them.
        var mountNames = items
            .Select(i => i.RegistryKey)
            .OfType<RegistryLocation>()
            .Where(k => k.Hive == RegistryHive.Users && k.SubKey.StartsWith(UserProfiles.MountPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.SubKey.Split('\\')[0]);

        using var mounts = UserProfiles.MountByName(mountNames);

        try
        {
            foreach (var item in ordered)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Removing {item.DisplayPath}");

                RemovalOutcome outcome;
                try
                {
                    outcome = RemoveOne(item, options, backup);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One item failing — reg.exe timing out, a task the scheduler will not hand
                    // over — must not stop the rest, or leave the run without a manifest.
                    outcome = new RemovalOutcome(item, RemovalStatus.Failed, Friendly(ex));
                }

                outcomes.Add(outcome);
            }
        }
        finally
        {
            // Written even when stopped part-way, so there is always a record of what went.
            backup.WriteManifest(new RemovalReport(outcomes, backup.Folder));
        }

        return new RemovalReport(outcomes, backup.Folder);
    }

    private static int Order(LeftoverKind kind) => kind switch
    {
        LeftoverKind.Service => 0,
        LeftoverKind.ScheduledTask => 1,
        LeftoverKind.FirewallRule => 2,
        LeftoverKind.PathEntry => 3,
        LeftoverKind.RegistryValue => 4,
        LeftoverKind.RegistryKey => 5,
        LeftoverKind.File => 6,
        _ => 7
    };

    private RemovalOutcome RemoveOne(LeftoverItem item, RemovalOptions options, RemovalBackup backup)
    {
        // What the scan said, first. An absolute protection is never lifted, and a protected item
        // is only ever removed because the person turned protection off.
        if (item.IsProtected && (item.KeptAlways || !options.AllowProtected))
            return new RemovalOutcome(item, RemovalStatus.Refused, item.ProtectedBecause!);

        // Then the policy's view now, from what is there now: what a task or service runs, and
        // which firewall rules share the name, read fresh rather than taken from the list.
        IReadOnlyList<FirewallRuleInfo> rules = [];
        if (item.Kind == LeftoverKind.FirewallRule)
            rules = _firewall.ReadAll().Where(r => r.Name.Equals(item.Path, StringComparison.OrdinalIgnoreCase)).ToList();

        var verdict = item.Kind switch
        {
            LeftoverKind.File or LeftoverKind.Folder => safety.JudgePath(item.Path),
            LeftoverKind.RegistryKey => safety.JudgeRegistryKey(item.RegistryKey!),
            LeftoverKind.RegistryValue => safety.JudgeRegistryValue(item.RegistryKey!, item.ValueName),
            LeftoverKind.Service => safety.JudgeService(item.Path, ServiceImagePath(item.Path)),
            LeftoverKind.ScheduledTask => safety.JudgeTask(item.Path, TaskCommands(item.Path)),
            LeftoverKind.PathEntry => item.RegistryKey is null
                ? SafetyVerdict.Always("No environment key.")
                : safety.JudgePathEntry(item.RegistryKey, item.ValueName, item.Path),
            LeftoverKind.FirewallRule => safety.JudgeFirewallRules(rules),
            _ => SafetyVerdict.Always("Unknown item type.")
        };

        // Newly protected since the scan counts as not overridden: the person never saw why.
        if (verdict is not null && (verdict.Absolute || !options.AllowProtected || !item.IsProtected))
            return new RemovalOutcome(item, RemovalStatus.Refused, verdict.Reason);

        // Nor does protection by a program the person was not shown — one installed since, or one
        // the reason they saw did not name.
        if (verdict?.Claimants.FirstOrDefault(c => !item.ProtectedBecause!.Contains(c, StringComparison.OrdinalIgnoreCase)) is { } unseen)
        {
            return new RemovalOutcome(item, RemovalStatus.Refused,
                $"{verdict.Reason} {unseen} was not named when you chose to remove it, so it was left alone.");
        }

        var outcome = item.Kind switch
        {
            LeftoverKind.File => RemoveFile(item, options),
            LeftoverKind.Folder => RemoveFolder(item, options),
            LeftoverKind.RegistryKey => RemoveKey(item, backup),
            LeftoverKind.RegistryValue => RemoveValue(item, backup),
            LeftoverKind.Service => RemoveService(item, backup),
            LeftoverKind.ScheduledTask => RemoveTask(item, backup),
            LeftoverKind.PathEntry => RemovePathEntry(item, backup),
            LeftoverKind.FirewallRule => RemoveFirewallRules(item, rules, backup),
            _ => new RemovalOutcome(item, RemovalStatus.Failed, "Unknown item type.")
        };

        return item.IsProtected && outcome.Status is RemovalStatus.Removed or RemovalStatus.RemovedAtRestart
            ? outcome with { Overridden = true, Message = $"{outcome.Message} It was protected ({item.ProtectedBecause}); you chose to remove it." }
            : outcome;
    }

    // ---- Files -------------------------------------------------------------

    private static RemovalOutcome RemoveFile(LeftoverItem item, RemovalOptions options)
    {
        if (!File.Exists(item.Path)) return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");

        return Dispose(item, options, isFolder: false);
    }

    private static RemovalOutcome RemoveFolder(LeftoverItem item, RemovalOptions options)
    {
        if (!Directory.Exists(item.Path)) return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");

        // A junction or symbolic link is removed as the link itself, and never handed to the
        // shell: whatever it points at was not what the scan judged, and may not be removable.
        if (new DirectoryInfo(item.Path).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(item.Path, recursive: false);
            return new RemovalOutcome(item, RemovalStatus.Removed, "Removed the link; what it pointed to was left alone.");
        }

        if (item.OnlyIfEmpty)
        {
            if (Directory.EnumerateFileSystemEntries(item.Path).Any())
                return new RemovalOutcome(item, RemovalStatus.Skipped, "Kept: something else is still in it.");

            Directory.Delete(item.Path);
            return new RemovalOutcome(item, RemovalStatus.Removed, "Removed the empty folder.");
        }

        return Dispose(item, options, isFolder: true);
    }

    private static RemovalOutcome Dispose(LeftoverItem item, RemovalOptions options, bool isFolder)
    {
        // Asked for the Recycle Bin, it is the Recycle Bin or nothing. Falling back to a permanent
        // delete would override the person who just declined the shell's "delete permanently?"
        // warning, and surprise anyone on a drive that has no Recycle Bin.
        if (options.UseRecycleBin)
        {
            return Recycle(item.Path) switch
            {
                RecycleResult.Recycled when !File.Exists(item.Path) && !Directory.Exists(item.Path)
                    => new RemovalOutcome(item, RemovalStatus.Removed, "Moved to the Recycle Bin.") { Recycled = true },
                RecycleResult.Declined
                    => new RemovalOutcome(item, RemovalStatus.Skipped, "Left in place: you chose not to delete it permanently."),
                _ => new RemovalOutcome(item, RemovalStatus.Failed,
                    "Could not move it to the Recycle Bin — it may be in use, or on a drive without one. Nothing was deleted.")
            };
        }

        try
        {
            // Directory.Delete removes a junction or symbolic link itself, never what it points
            // at, so a link planted inside a leftover folder cannot redirect the deletion.
            if (isFolder) Directory.Delete(item.Path, recursive: true);
            else
            {
                File.SetAttributes(item.Path, FileAttributes.Normal);
                File.Delete(item.Path);
            }

            return new RemovalOutcome(item, RemovalStatus.Removed, "Deleted.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ScheduleForRestart(item, isFolder)
                ? new RemovalOutcome(item, RemovalStatus.RemovedAtRestart, "In use; it will be deleted when Windows next restarts.")
                : new RemovalOutcome(item, RemovalStatus.Failed, Friendly(ex));
        }
    }

    /// <summary>
    /// Queues a file, or a folder and everything in it, for deletion at the next restart. Needs
    /// administrator rights; Windows processes the queue in order, so contents go before folders.
    /// </summary>
    private static bool ScheduleForRestart(LeftoverItem item, bool isFolder)
    {
        if (!Elevation.ElevationValidation.IsCurrentProcessElevated()) return false;

        try
        {
            if (!isFolder) return MoveFileExW(item.Path, null, MoveFileDelayUntilReboot);

            // Enumeration options skip links inside the folder, not the folder itself; a folder
            // that is a junction to another drive must queue the link, never everything behind it.
            if (new DirectoryInfo(item.Path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return MoveFileExW(item.Path, null, MoveFileDelayUntilReboot);

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

            foreach (var file in Directory.EnumerateFiles(item.Path, "*", options))
                MoveFileExW(file, null, MoveFileDelayUntilReboot);

            foreach (var folder in Directory.EnumerateDirectories(item.Path, "*", options).OrderByDescending(d => d.Length))
                MoveFileExW(folder, null, MoveFileDelayUntilReboot);

            return MoveFileExW(item.Path, null, MoveFileDelayUntilReboot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Registry ----------------------------------------------------------

    private static RemovalOutcome RemoveKey(LeftoverItem item, RemovalBackup backup)
    {
        var key = item.RegistryKey!;
        if (!key.Exists()) return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");

        // Deleting a tree through a registry link would delete wherever the link points.
        if (RegistryLinks.HasLink(key, includeSubtree: true)) return LinkRefused(item);

        if (item.OnlyIfEmpty)
        {
            using var open = key.Open();
            if (open is not null && (open.SubKeyCount > 0 || open.ValueCount > 0))
                return new RemovalOutcome(item, RemovalStatus.Skipped, "Kept: something else is still in it.");
        }

        if (backup.ExportKey(key) is not { } file) return CouldNotBackUp(item);

        using var parent = key.Parent!.Open(writable: true);
        if (parent is null) return new RemovalOutcome(item, RemovalStatus.Failed, "Access is denied.");

        parent.DeleteSubKeyTree(key.LeafName, throwOnMissingSubKey: false);
        return new RemovalOutcome(item, RemovalStatus.Removed, "Removed (backed up first).") { BackupFile = file };
    }

    private static RemovalOutcome RemoveValue(LeftoverItem item, RemovalBackup backup)
    {
        var location = item.RegistryKey!;
        var name = item.ValueName ?? string.Empty;

        if (RegistryLinks.HasLink(location, includeSubtree: false)) return LinkRefused(item);

        using var key = location.Open(writable: true);
        if (key is null)
        {
            return location.Exists()
                ? new RemovalOutcome(item, RemovalStatus.Failed, "Access is denied.")
                : new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");
        }

        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");

        if (backup.ExportKey(location) is not { } file) return CouldNotBackUp(item);

        key.DeleteValue(name, throwOnMissingValue: false);
        return new RemovalOutcome(item, RemovalStatus.Removed, "Removed (its key was backed up first).") { BackupFile = file };
    }

    /// <summary>
    /// Takes one entry out of PATH, leaving every other entry, their order and the value's type as
    /// they were, then tells running programs the environment changed.
    /// </summary>
    private static RemovalOutcome RemovePathEntry(LeftoverItem item, RemovalBackup backup)
    {
        var location = item.RegistryKey!;
        if (RegistryLinks.HasLink(location, includeSubtree: false)) return LinkRefused(item);

        using var key = location.Open(writable: true);
        if (key is null)
        {
            return location.Exists()
                ? new RemovalOutcome(item, RemovalStatus.Failed, "Access is denied.")
                : new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");
        }

        if (key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
            return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");

        var kind = key.GetValueKind("Path");
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            return new RemovalOutcome(item, RemovalStatus.Failed, "PATH is not stored as text here, so it was left alone.");

        var remaining = EnvironmentPath.Without(value, item.Path, out var removed);
        if (removed == 0) return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");

        if (backup.ExportKey(location) is not { } file) return CouldNotBackUp(item);

        key.SetValue("Path", remaining, kind);
        EnvironmentPath.Broadcast();

        return new RemovalOutcome(item, RemovalStatus.Removed,
            "Taken out of PATH; the other entries are unchanged. Programs already running keep the old PATH until restarted.")
        {
            BackupFile = file
        };
    }

    private static RemovalOutcome CouldNotBackUp(LeftoverItem item) =>
        new(item, RemovalStatus.Failed, "Could not back it up first, so it was left alone.");

    private static RemovalOutcome LinkRefused(LeftoverItem item) =>
        new(item, RemovalStatus.Refused, "A registry link is on the way to it or inside it, which could lead somewhere else entirely, so it was left alone.");

    // ---- Services, tasks and firewall rules --------------------------------

    /// <summary>
    /// The key of the service named by the item. Always derived from the name, because the name is
    /// what sc.exe deletes: checking one key and deleting another would check nothing.
    /// </summary>
    private static RegistryLocation ServiceKey(string name) =>
        RegistryLocation.Machine($@"SYSTEM\CurrentControlSet\Services\{name}");

    private static string? ServiceImagePath(string name)
    {
        if (name.Contains('\\') || name.Contains('/')) return null;

        using var key = ServiceKey(name).Open();
        return key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>What a task runs now, read fresh so the safety check sees the task as it is.</summary>
    private static IReadOnlyList<string> TaskCommands(string taskPath) =>
        ScheduledTaskReader.ReadAll()
            .FirstOrDefault(t => t.Path.Equals(taskPath, StringComparison.OrdinalIgnoreCase))?.Commands ?? [];

    private static RemovalOutcome RemoveService(LeftoverItem item, RemovalBackup backup)
    {
        var name = item.Path;
        var location = ServiceKey(name);

        if (!location.Exists()) return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");
        if (backup.ExportKey(location) is not { } file) return CouldNotBackUp(item);

        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        Run(sc, "stop", name);

        // A service takes a moment to stop; deleting a running one only marks it for deletion.
        for (var i = 0; i < 20 && Run(sc, "query", name).StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase); i++)
            Thread.Sleep(500);

        var result = Run(sc, "delete", name);

        return result.Success
            ? new RemovalOutcome(item, RemovalStatus.Removed, "Stopped and removed (backed up first).") { BackupFile = file }
            : result.ExitCode == 1072
                ? new RemovalOutcome(item, RemovalStatus.RemovedAtRestart, "Marked for deletion; it goes when Windows next restarts.") { BackupFile = file }
                : new RemovalOutcome(item, RemovalStatus.Failed, result.Combined.Trim());
    }

    private static ProcessResult Run(string file, params string[] arguments) =>
        ProcessRunner.RunAsync(file, arguments, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    private static RemovalOutcome RemoveTask(LeftoverItem item, RemovalBackup backup)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return new RemovalOutcome(item, RemovalStatus.Failed, "The Task Scheduler is unavailable.");

        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();

        var slash = item.Path.LastIndexOf('\\');
        var folderPath = slash <= 0 ? @"\" : item.Path[..slash];
        var name = item.Path[(slash + 1)..];

        dynamic folder;
        dynamic task;
        try
        {
            folder = service.GetFolder(folderPath);
            task = folder.GetTask(name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                       || (ex is COMException com && (uint)com.HResult is 0x80070002 or 0x80070003))
        {
            return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");
        }

        if (backup.SaveTask(name, (string)task.Xml) is not { } file) return CouldNotBackUp(item);

        folder.DeleteTask(name, 0);
        return new RemovalOutcome(item, RemovalStatus.Removed, "Removed (its definition was saved first).") { BackupFile = file };
    }

    private RemovalOutcome RemoveFirewallRules(LeftoverItem item, IReadOnlyList<FirewallRuleInfo> rules, RemovalBackup backup)
    {
        if (rules.Count == 0) return new RemovalOutcome(item, RemovalStatus.Skipped, "Already gone.");
        if (backup.SaveFirewallRules(item.Path, rules) is not { } file) return CouldNotBackUp(item);

        // Names are compared exactly by the firewall; the scan grouped them regardless of case.
        var gone = rules.Select(r => r.Name).Distinct(StringComparer.Ordinal).All(_firewall.RemoveAll);

        return gone
            ? new RemovalOutcome(item, RemovalStatus.Removed,
                $"Removed {(rules.Count == 1 ? "the rule" : $"{rules.Count} rules")} (saved first).") { BackupFile = file }
            : new RemovalOutcome(item, RemovalStatus.Failed, "Windows Firewall kept some of the rules.") { BackupFile = file };
    }

    // ---- Recycle Bin -------------------------------------------------------

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoErrorUi = 0x400;
    private const ushort FofWantNukeWarning = 0x4000;
    private const int MoveFileDelayUntilReboot = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct operation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string? replacement, int flags);

    private enum RecycleResult
    {
        Recycled,

        /// <summary>The shell warned it would be deleted permanently, and the person said no.</summary>
        Declined,

        Failed
    }

    /// <summary>
    /// Sends a path to the Recycle Bin. If the item is too large for the bin the shell warns rather
    /// than deleting it silently, and a "no" there is reported as declined.
    /// </summary>
    private static RecycleResult Recycle(string path)
    {
        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning
        };

        var result = SHFileOperationW(ref operation);

        if (operation.AnyOperationsAborted) return RecycleResult.Declined;
        return result == 0 ? RecycleResult.Recycled : RecycleResult.Failed;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access is denied. Restart Uproot as administrator to remove this.",
        IOException io when io.HResult == unchecked((int)0x80070020) => "In use by another program.",
        _ => ex.Message
    };
}
