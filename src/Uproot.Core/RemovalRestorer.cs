using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using Uproot.Core.Elevation;

namespace Uproot.Core;

/// <summary>A past removal, as its backup folder records it.</summary>
public sealed record RemovalRecord(string Folder, RemovalManifest Manifest)
{
    public string Name => System.IO.Path.GetFileName(Folder);
    public string Title => Manifest.Program ?? Name;

    public IReadOnlyList<ManifestItem> Removed =>
        Manifest.Items.Where(i => i.ParsedStatus is RemovalStatus.Removed or RemovalStatus.RemovedAtRestart).ToList();

    /// <summary>Items there is something to put back from: a backup file, the Recycle Bin, or the PATH entry itself.</summary>
    public IReadOnlyList<ManifestItem> Restorable => Removed.Where(RemovalRestorer.CanTry).ToList();

    /// <summary>The first manifests did not record which backup belongs to which item.</summary>
    public bool IsOldFormat => Manifest.Format < RemovalManifest.CurrentFormat;
}

/// <summary>The removals Uproot, or WGUpdater before it, has backed up, newest first.</summary>
public static class RemovalHistory
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Removals backed up in <paramref name="root"/>, or by default in every place this account's
    /// backups go: its profile, for removals made without administrator rights, and its folder in
    /// the admin tree under ProgramData, for those made as administrator. Both of WGUpdater's
    /// places are read as well, for removals made before the uninstaller became Uproot.
    /// </summary>
    public static IReadOnlyList<RemovalRecord> List(string? root = null)
    {
        var sid = ElevationValidation.CurrentUserSid();
        var roots = root is not null
            ? [root]
            : new[]
                {
                    RemovalBackup.DefaultRoot,
                    sid is null ? null : BackupTrust.AdminFolderFor(sid),
                    RemovalBackup.LegacyDefaultRoot,
                    sid is null ? null : BackupTrust.LegacyAdminFolderFor(sid)
                }
                .OfType<string>()
                .ToArray();

        var records = new List<RemovalRecord>();

        foreach (var folderRoot in roots)
        {
            if (!Directory.Exists(folderRoot)) continue;

            try
            {
                foreach (var folder in Directory.EnumerateDirectories(folderRoot, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    if (Read(folder) is { } record) records.Add(record);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return records.OrderByDescending(r => r.Manifest.RemovedAt).ToList();
    }

    public static RemovalRecord? Read(string folder)
    {
        try
        {
            var file = System.IO.Path.Combine(folder, RemovalBackup.ManifestName);
            if (!File.Exists(file)) return null;

            var manifest = JsonSerializer.Deserialize<RemovalManifest>(File.ReadAllText(file), Json);
            return manifest is null ? null : new RemovalRecord(folder, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A record by folder name, "latest", or a unique prefix of the name.</summary>
    public static RemovalRecord? Find(IReadOnlyList<RemovalRecord> records, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        if (query.Equals("latest", StringComparison.OrdinalIgnoreCase)) return records.FirstOrDefault();

        var exact = records.FirstOrDefault(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                                                || r.Folder.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var matches = records.Where(r => r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}

public enum RestoreStatus
{
    Restored,

    /// <summary>Put back, but Windows only picks it up after a restart: a service.</summary>
    RestoredNeedsRestart,

    /// <summary>Already back, or nothing to restore it from.</summary>
    Skipped,

    /// <summary>Not restored, on purpose: the backup cannot be trusted for it, or it would breach the safety policy.</summary>
    Refused,

    Failed
}

public sealed record RestoreOutcome(ManifestItem Item, RestoreStatus Status, string Message);

public sealed record RestoreReport(IReadOnlyList<RestoreOutcome> Outcomes)
{
    public int Restored => Outcomes.Count(o => o.Status is RestoreStatus.Restored or RestoreStatus.RestoredNeedsRestart);
    public int NeedsRestart => Outcomes.Count(o => o.Status == RestoreStatus.RestoredNeedsRestart);
    public int NotRestored => Outcomes.Count(o => o.Status is RestoreStatus.Refused or RestoreStatus.Failed);
    public int Skipped => Outcomes.Count(o => o.Status == RestoreStatus.Skipped);

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Restored} restored" };
            if (NeedsRestart > 0) parts.Add($"{NeedsRestart} after a restart");
            if (Skipped > 0) parts.Add($"{Skipped} skipped");
            if (NotRestored > 0) parts.Add($"{NotRestored} not restored");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Puts back what a removal took, from its backup folder and the Recycle Bin.
///
/// A backup made without administrator rights lives in the account's own profile, where a
/// program running without those rights could have changed it. That does not matter for the
/// account's own things, which it could change anyway. It matters for machine-wide ones: an
/// elevated restore writes into HKLM and re-creates services, firewall rules and tasks. Those
/// are restored while elevated only from a backup in the admin tree under ProgramData, where
/// every folder from the root down is still locked against standard accounts.
///
/// Backups are read once, checked, and applied from what was checked: registry data is written
/// through the registry API from the parsed file, never by handing the file to reg.exe, which
/// would read it again. Files are never moved back while elevated at all — the Recycle Bin and
/// the path they return to are both the account's to change, so a move run as administrator
/// could be steered anywhere; a restore without administrator rights, or Explorer's own Restore,
/// puts them back instead. And every item must pass the same safety checks a removal would have
/// applied: a backup is never a way to write what a removal could not have touched.
/// </summary>
public sealed class RemovalRestorer(LeftoverSafety safety, IFirewall? firewall = null)
{
    private readonly IFirewall _firewall = firewall ?? WindowsFirewall.Instance;

    public static bool CanTry(ManifestItem item) =>
        item.ParsedKind is { } kind && (kind switch
        {
            LeftoverKind.File or LeftoverKind.Folder => item.Recycled,
            LeftoverKind.PathEntry => item.RegistryKey is not null,
            _ => item.Backup is not null
        });

    public RestoreReport Restore(RemovalRecord record, IReadOnlyList<ManifestItem> items,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var elevated = ElevationValidation.IsCurrentProcessElevated();
        var locked = elevated && BackupTrust.IsTrusted(record.Folder);
        var outcomes = new List<RestoreOutcome>();

        // Other accounts' hives are loaded only for a backup that is trusted: for any other, the
        // items in them are refused anyway, and loading a hive gives its owner a temporary profile
        // if they sign in meanwhile.
        var mountNames = locked
            ? items
                .Select(i => RegistryLocation.Parse(i.RegistryKey))
                .OfType<RegistryLocation>()
                .Where(k => k.Hive == RegistryHive.Users && UserProfiles.IsMountName(k.SubKey))
                .Select(k => k.SubKey.Split('\\')[0])
            : [];

        using var mounts = UserProfiles.MountByName(mountNames);

        // The reverse of removal: files first, so a service or task comes back to find its program.
        foreach (var item in items.OrderBy(i => Order(i.ParsedKind)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Restoring {item.DisplayPath ?? item.Path}");

            RestoreOutcome outcome;
            try
            {
                outcome = RestoreOne(record, item, elevated, locked);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = new RestoreOutcome(item, RestoreStatus.Failed, ex is UnauthorizedAccessException
                    ? "Access is denied. Restart Uproot as administrator to restore this."
                    : ex.Message);
            }

            outcomes.Add(outcome);
        }

        return new RestoreReport(outcomes);
    }

    private static int Order(LeftoverKind? kind) => kind switch
    {
        LeftoverKind.Folder => 0,
        LeftoverKind.File => 1,
        LeftoverKind.RegistryKey => 2,
        LeftoverKind.RegistryValue => 3,
        LeftoverKind.PathEntry => 4,
        LeftoverKind.FirewallRule => 5,
        LeftoverKind.Service => 6,
        _ => 7
    };

    private RestoreOutcome RestoreOne(RemovalRecord record, ManifestItem item, bool elevated, bool locked)
    {
        if (item.ParsedStatus is not (RemovalStatus.Removed or RemovalStatus.RemovedAtRestart))
            return Skip(item, "It was not removed.");

        if (item.ParsedKind is not { } kind) return Refuse(item, "Not a kind of item this version knows.");
        if (!CanTry(item)) return Skip(item, "There is nothing to restore it from.");

        if (elevated && kind is LeftoverKind.File or LeftoverKind.Folder)
        {
            return Refuse(item, "Files are not put back while Uproot runs as administrator: the Recycle Bin, and the folder "
                                + "they go back to, can be changed from a standard account, so a move made with administrator "
                                + "rights could be sent somewhere else. Restore it from the Recycle Bin in Explorer, or undo "
                                + "the removal from Uproot without administrator rights.");
        }

        if (elevated && IsMachineWide(item, kind) && !locked)
        {
            return Refuse(item, "This backup could have been changed by a program running without administrator rights, "
                                + "so machine-wide items are not restored from it while elevated.");
        }

        // Writing through a registry link would write wherever the link points.
        var target = kind == LeftoverKind.Service
            ? RegistryLocation.Machine($@"SYSTEM\CurrentControlSet\Services\{item.Path}")
            : kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.PathEntry ? RegistryLocation.Parse(item.RegistryKey) : null;

        if (target is not null && RegistryLinks.HasLink(target, includeSubtree: false))
            return Refuse(item, "A registry link is on the way to where it would go back, so it was not restored.");

        return kind switch
        {
            LeftoverKind.File or LeftoverKind.Folder => RestoreRecycled(record, item),
            LeftoverKind.RegistryKey => RestoreKey(record, item),
            LeftoverKind.RegistryValue => RestoreValue(record, item),
            LeftoverKind.PathEntry => RestorePathEntry(item),
            LeftoverKind.Service => RestoreService(record, item),
            LeftoverKind.ScheduledTask => RestoreTask(record, item),
            LeftoverKind.FirewallRule => RestoreFirewallRules(record, item),
            _ => Refuse(item, "Not a kind of item this version knows.")
        };
    }

    /// <summary>
    /// Anything outside the account's own profile and hive. Services, firewall rules and tasks
    /// always count: a task's definition can name any account to run as.
    /// </summary>
    private static bool IsMachineWide(ManifestItem item, LeftoverKind kind)
    {
        switch (kind)
        {
            case LeftoverKind.File or LeftoverKind.Folder:
                return !PathRules.IsWithin(item.Path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            case LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.PathEntry:
                var key = RegistryLocation.Parse(item.RegistryKey);
                if (key is null) return true;
                if (key.Hive == RegistryHive.CurrentUser) return false;
                if (key.Hive != RegistryHive.Users) return true;

                var hive = key.SubKey.Split('\\')[0];
                var me = ElevationValidation.CurrentUserSid();
                return me is null || !(hive.Equals(me, StringComparison.OrdinalIgnoreCase)
                                       || hive.Equals(me + "_Classes", StringComparison.OrdinalIgnoreCase));

            default:
                return true;
        }
    }

    /// <summary>The backup file for an item, which must be inside its backup folder.</summary>
    private static string? BackupFile(RemovalRecord record, ManifestItem item)
    {
        if (item.Backup is null) return null;

        var folder = Path.GetFullPath(record.Folder).TrimEnd('\\') + @"\";
        var file = Path.GetFullPath(Path.Combine(folder, item.Backup));
        return file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(file) ? file : null;
    }

    // ---- Files ---------------------------------------------------------------

    /// <summary>
    /// Moves a file or folder back from this account's own Recycle Bin. Only ever runs without
    /// administrator rights, so it can do nothing the account could not do itself.
    /// </summary>
    private RestoreOutcome RestoreRecycled(RemovalRecord record, ManifestItem item)
    {
        if (!item.Recycled) return Skip(item, "It was deleted outright rather than sent to the Recycle Bin.");

        var path = PathRules.Normalize(item.Path);
        if (path is null || safety.JudgePath(path) is { Absolute: true }) return Refuse(item, "Not somewhere a leftover could have been.");

        if (File.Exists(path) || Directory.Exists(path)) return Skip(item, "Something is already there.");

        // This account's bin, whatever the manifest says: another account's could not be read
        // anyway, and the manifest is not the thing to trust about it.
        var sid = ElevationValidation.CurrentUserSid();
        if (sid is null) return Fail(item, "Could not tell whose Recycle Bin to look in.");

        var when = item.At ?? record.Manifest.RemovedAt;
        var match = RecycleBin.FindDeletedFrom(path, sid)
            .Where(r => (r.DeletedAt - when).Duration() < TimeSpan.FromMinutes(30))
            .OrderBy(r => (r.DeletedAt - when).Duration())
            .FirstOrDefault();

        if (match is null) return Fail(item, "It is no longer in the Recycle Bin; it may have been emptied.");

        RecycleBin.Restore(match with { OriginalPath = path });
        return new RestoreOutcome(item, RestoreStatus.Restored, "Moved back from the Recycle Bin.");
    }

    // ---- Registry ------------------------------------------------------------

    /// <summary>
    /// Reads an item's .reg backup once and checks it writes nothing outside <paramref name="key"/>.
    /// Null with the reason when it cannot be used.
    /// </summary>
    private static (RegFile? File, string? Problem) ReadBackup(RemovalRecord record, ManifestItem item, RegistryLocation key)
    {
        if (BackupFile(record, item) is not { } path) return (null, "Its backup file is missing.");

        var reg = RegFile.Read(path);
        return reg.OnlyTouches(key) ? (reg, null) : (null, "Its backup file holds more than that key, or something unexpected, so it was not used.");
    }

    private RestoreOutcome RestoreKey(RemovalRecord record, ManifestItem item)
    {
        if (RegistryLocation.Parse(item.RegistryKey) is not { } key) return Refuse(item, "The backup does not say which key it is.");

        var (reg, problem) = ReadBackup(record, item, key);
        if (reg is null) return Refuse(item, problem!);

        // An event log source is judged by the message files it would get back, which only the
        // backup can say: the key itself is gone.
        var verdict = LeftoverSafety.EventLogSourceOf(key) is { } source
            ? safety.JudgeEventLogSource(source.Log, source.Name, MessageFiles(reg, key))
            : safety.JudgeRegistryKey(key);

        if (verdict is { Absolute: true }) return Refuse(item, verdict.Reason);
        if (key.Exists()) return Skip(item, "The key is there already.");

        reg.WriteWithin(key);
        return new RestoreOutcome(item, RestoreStatus.Restored, "Put back from its backup.");
    }

    private static IEnumerable<string> MessageFiles(RegFile reg, RegistryLocation key) =>
        new[] { "EventMessageFile", "CategoryMessageFile", "ParameterMessageFile" }
            .Select(name => reg.FindValue(key, name)?.Data as string)
            .OfType<string>()
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private RestoreOutcome RestoreValue(RemovalRecord record, ManifestItem item)
    {
        if (RegistryLocation.Parse(item.RegistryKey) is not { } key) return Refuse(item, "The backup does not say which key it is.");
        if (safety.JudgeRegistryValue(key, item.ValueName) is { Absolute: true } verdict) return Refuse(item, verdict.Reason);

        var (reg, problem) = ReadBackup(record, item, key);
        if (reg is null) return Refuse(item, problem!);

        if (reg.FindValue(key, item.ValueName) is not { Data: not null } value)
            return Fail(item, "The value is not in its backup.");

        using var open = OpenOrCreate(key);
        if (open is null) return Fail(item, "Could not open the key to put it back.");

        if (open.GetValueNames().Contains(item.ValueName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            return Skip(item, "The value is there already.");

        RegFile.Write(open, value with { Name = item.ValueName ?? string.Empty });
        return new RestoreOutcome(item, RestoreStatus.Restored, "Put back from its backup.");
    }

    private static RegistryKey? OpenOrCreate(RegistryLocation key)
    {
        using var root = RegistryKey.OpenBaseKey(key.Hive, RegistryView.Registry64);
        return root.CreateSubKey(key.SubKey, writable: true);
    }

    private RestoreOutcome RestorePathEntry(ManifestItem item)
    {
        if (RegistryLocation.Parse(item.RegistryKey) is not { } key) return Refuse(item, "The backup does not say which variable it was in.");
        if (safety.JudgePathEntry(key, item.ValueName ?? "Path", item.Path) is { Absolute: true } verdict) return Refuse(item, verdict.Reason);

        using var open = OpenOrCreate(key);
        if (open is null) return Fail(item, "Could not open the environment key.");

        var current = open.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var kind = current is null ? RegistryValueKind.ExpandString : open.GetValueKind("Path");
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) return Fail(item, "PATH is not stored as text here.");

        var updated = EnvironmentPath.With(current, item.Path);
        if (updated == current) return Skip(item, "It is on PATH already.");

        open.SetValue("Path", updated, kind);
        EnvironmentPath.Broadcast();
        return new RestoreOutcome(item, RestoreStatus.Restored, "Added back to the end of PATH.");
    }

    // ---- Services, tasks, firewall rules -----------------------------------

    /// <summary>
    /// A service comes back from its key's export, judged like a removal would judge it: by the
    /// program the restored ImagePath would run. Windows loads it at the next restart.
    /// </summary>
    private RestoreOutcome RestoreService(RemovalRecord record, ManifestItem item)
    {
        var name = item.Path;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/')) return Refuse(item, "Not a valid service name.");

        var key = RegistryLocation.Machine($@"SYSTEM\CurrentControlSet\Services\{name}");

        var (reg, problem) = ReadBackup(record, item, key);
        if (reg is null) return Refuse(item, problem!);

        var imagePath = reg.FindValue(key, "ImagePath")?.Data as string;
        if (safety.JudgeService(name, imagePath) is { Absolute: true } verdict) return Refuse(item, verdict.Reason);

        if (key.Exists()) return Skip(item, "The service is there already.");

        reg.WriteWithin(key);
        return new RestoreOutcome(item, RestoreStatus.RestoredNeedsRestart, "Its registration is back; Windows loads it at the next restart.");
    }

    private RestoreOutcome RestoreTask(RemovalRecord record, ManifestItem item)
    {
        var path = item.Path;
        if (!path.StartsWith('\\') || path.Contains("..") || path.EndsWith('\\')) return Refuse(item, "Not a valid task path.");

        if (BackupFile(record, item) is not { } file) return Fail(item, "Its saved definition is missing.");
        var xml = File.ReadAllText(file);
        var document = XDocument.Parse(xml);

        // Judged as a removal would judge it: by where it lives and what it runs.
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var commands = document.Descendants(ns + "Exec")
            .Select(exec => (Command: exec.Element(ns + "Command")?.Value, Arguments: exec.Element(ns + "Arguments")?.Value))
            .Where(e => !string.IsNullOrWhiteSpace(e.Command))
            .Select(e => $"\"{e.Command!.Trim().Trim('"')}\" {e.Arguments}".Trim())
            .ToList();

        if (safety.JudgeTask(path, commands) is { Absolute: true } verdict) return Refuse(item, verdict.Reason);

        // The definition names the account it runs as and how it signs in; registration has to
        // be told the same. A task that stored a password cannot be recreated without it.
        var principal = document.Descendants(ns + "Principal").FirstOrDefault();
        var logon = principal?.Element(ns + "LogonType")?.Value;
        var group = principal?.Element(ns + "GroupId")?.Value;

        var logonType = logon switch
        {
            "Password" => -1,
            "S4U" => 2,
            "ServiceAccount" => 5,
            "InteractiveTokenOrPassword" => 6,
            _ when group is not null => 4,
            _ => 3
        };

        if (logonType < 0)
            return Fail(item, $"This task ran with a stored password. Import {Path.GetFileName(file)} in Task Scheduler to put it back.");

        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return Fail(item, "The Task Scheduler is unavailable.");

        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();

        var slash = path.LastIndexOf('\\');
        var folderPath = slash <= 0 ? @"\" : path[..slash];
        var name = path[(slash + 1)..];

        dynamic folder = EnsureTaskFolder(service, folderPath);

        try
        {
            folder.GetTask(name);
            return Skip(item, "A task by that name is there already.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                       || (ex is COMException com && (uint)com.HResult is 0x80070002 or 0x80070003))
        {
        }

        const int taskCreate = 2;
        folder.RegisterTask(name, xml, taskCreate, null, null, logonType, null);
        return new RestoreOutcome(item, RestoreStatus.Restored, "Registered again from its saved definition.");
    }

    private static dynamic EnsureTaskFolder(dynamic service, string folderPath)
    {
        try
        {
            return service.GetFolder(folderPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                       || (ex is COMException com && (uint)com.HResult is 0x80070002 or 0x80070003))
        {
            dynamic root = service.GetFolder(@"\");
            return root.CreateFolder(folderPath.TrimStart('\\'), null);
        }
    }

    private RestoreOutcome RestoreFirewallRules(RemovalRecord record, ManifestItem item)
    {
        if (BackupFile(record, item) is not { } file) return Fail(item, "Its saved rules are missing.");

        var rules = JsonSerializer.Deserialize<List<FirewallRuleInfo>>(File.ReadAllText(file), RemovalBackup.Json) ?? [];
        if (rules.Count == 0 || rules.Any(r => !r.Name.Equals(item.Path, StringComparison.OrdinalIgnoreCase)))
            return Refuse(item, "The saved rules do not match this item.");

        if (safety.JudgeFirewallRules(rules) is { Absolute: true } verdict) return Refuse(item, verdict.Reason);

        var existing = _firewall.ReadAll().Where(r => r.Name.Equals(item.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (existing.Count > 0) return Skip(item, "Rules by that name are there already.");

        foreach (var rule in rules) _firewall.Add(rule);
        return new RestoreOutcome(item, RestoreStatus.Restored, $"Recreated {(rules.Count == 1 ? "the rule" : $"{rules.Count} rules")}.");
    }

    private static RestoreOutcome Skip(ManifestItem item, string message) => new(item, RestoreStatus.Skipped, message);
    private static RestoreOutcome Refuse(ManifestItem item, string message) => new(item, RestoreStatus.Refused, message);
    private static RestoreOutcome Fail(ManifestItem item, string message) => new(item, RestoreStatus.Failed, message);
}
