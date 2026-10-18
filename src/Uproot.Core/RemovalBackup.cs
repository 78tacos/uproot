using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Uproot.Core.Elevation;

namespace Uproot.Core;

/// <summary>What one removal did, written beside its backups so it can be reviewed and undone.</summary>
public sealed record RemovalManifest
{
    public const int CurrentFormat = 2;

    /// <summary>
    /// 2 since manifests recorded which backup file belongs to which item and when each went.
    /// The first manifests had no format number at all, which reads as 0.
    /// </summary>
    public int Format { get; init; }

    public string? Program { get; init; }
    public DateTimeOffset RemovedAt { get; init; }

    /// <summary>Whether the removal ran with administrator rights.</summary>
    public bool Elevated { get; init; }

    /// <summary>The account the removal ran as, by SID.</summary>
    public string? Account { get; init; }

    public bool RecycleBin { get; init; }
    public IReadOnlyList<ManifestItem> Items { get; init; } = [];
}

public sealed record ManifestItem
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public string? RegistryKey { get; init; }
    public string? ValueName { get; init; }
    public string? DisplayPath { get; init; }
    public string? Owner { get; init; }
    public required string Status { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }

    /// <summary>The file in the backup folder holding what is needed to put this back.</summary>
    public string? Backup { get; init; }

    /// <summary>It was protected, and removed because the person turned protection off.</summary>
    public bool Overridden { get; init; }

    /// <summary>A file or folder that went to the Recycle Bin.</summary>
    public bool Recycled { get; init; }

    public DateTimeOffset? At { get; init; }

    public LeftoverKind? ParsedKind => Enum.TryParse<LeftoverKind>(Kind, out var kind) ? kind : null;
    public RemovalStatus? ParsedStatus => Enum.TryParse<RemovalStatus>(Status, out var status) ? status : null;
}

/// <summary>
/// A dated folder holding .reg exports, task definitions, firewall rules and the manifest,
/// created on first use so nothing is written when nothing is removed.
///
/// Removals made without administrator rights keep their backups in the account's own profile.
/// Removals made as administrator keep them under ProgramData, in a tree only administrators and
/// SYSTEM can change — every folder from the root down — with the account allowed to read its own.
/// A folder in the profile would not do for those: the account controls the folder above it, so
/// it could rename the backup away and put another in its place, however the backup itself was
/// locked. A restore that runs elevated later trusts machine-wide items only from that tree.
/// </summary>
public sealed class RemovalBackup(RemovalOptions options)
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private int _count;
    private readonly Dictionary<RegistryLocation, string> _exported = [];

    public string? Folder { get; private set; }

    /// <summary>Uproot's own folder in the profile. Nothing is written there until something is removed.</summary>
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Uproot");

    /// <summary>Where removals made without administrator rights keep their backups.</summary>
    public static string DefaultRoot => Path.Combine(DataRoot, "Backups");

    /// <summary>
    /// Where WGUpdater kept them, when the uninstaller was part of it. Those removals are listed
    /// and restored like Uproot's own; nothing new is written there.
    /// </summary>
    public static string LegacyDefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WGUpdater", "Uninstall backups");

    public const string ManifestName = "manifest.json";

    private string EnsureFolder()
    {
        if (Folder is not null) return Folder;

        var elevated = ElevationValidation.IsCurrentProcessElevated();
        var root = options.BackupRoot ?? (elevated ? BackupTrust.TryEnsureAdminFolder() : null) ?? DefaultRoot;
        var locked = elevated && options.BackupRoot is null && BackupTrust.IsAdminLocation(root);

        if (!locked) Directory.CreateDirectory(root);

        var baseName = $"{DateTime.Now:yyyy-MM-dd HHmmss} {Sanitize(options.ProgramName ?? "leftovers")}";

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var path = Path.Combine(root, attempt == 0 ? baseName : $"{baseName} ({attempt + 1})");
            if (Directory.Exists(path)) continue;

            if (!locked)
            {
                Directory.CreateDirectory(path);
                Folder = path;
                return path;
            }

            // Created with its permissions in one step, then checked: a folder that appeared
            // first under the same name is not ours, whatever it contains.
            BackupTrust.CreateLocked(path, ElevationValidation.CurrentUserSid());
            if (BackupTrust.IsTrusted(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Folder = path;
                return path;
            }
        }

        throw new IOException("Could not create a backup folder.");
    }

    /// <summary>Exports a key to a .reg file. Returns the file's name, or null if the export failed.</summary>
    public string? ExportKey(RegistryLocation key)
    {
        if (_exported.TryGetValue(key, out var existing)) return existing;

        var name = $"{++_count:000} {Sanitize(key.LeafName)}.reg";
        var file = Path.Combine(EnsureFolder(), name);
        var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");

        var result = ProcessRunner.RunAsync(reg, ["export", key.DisplayPath, file, "/y", "/reg:64"], TimeSpan.FromSeconds(60))
            .GetAwaiter().GetResult();

        if (!result.Success || !File.Exists(file)) return null;

        _exported[key] = name;
        return name;
    }

    /// <summary>Saves a scheduled task's definition. Returns the file's name within the backup folder.</summary>
    public string? SaveTask(string name, string xml)
    {
        try
        {
            var folder = Path.Combine(EnsureFolder(), "Scheduled tasks");
            Directory.CreateDirectory(folder);

            var fileName = $"{++_count:000} {Sanitize(name)}.xml";
            File.WriteAllText(Path.Combine(folder, fileName), xml);
            return Path.Combine("Scheduled tasks", fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves the firewall rules about to be removed, with everything needed to recreate them.</summary>
    public string? SaveFirewallRules(string name, IReadOnlyList<FirewallRuleInfo> rules)
    {
        try
        {
            var folder = Path.Combine(EnsureFolder(), "Firewall rules");
            Directory.CreateDirectory(folder);

            var fileName = $"{++_count:000} {Sanitize(name)}.json";
            File.WriteAllText(Path.Combine(folder, fileName), JsonSerializer.Serialize(rules, Json));
            return Path.Combine("Firewall rules", fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the manifest: every item, what happened to it, and the backup that puts it back.
    /// Written whenever anything was removed, including files that went to the Recycle Bin, so
    /// the removal can be found and undone later.
    /// </summary>
    public void WriteManifest(RemovalReport report)
    {
        if (Folder is null && !report.Outcomes.Any(o => o.Status is RemovalStatus.Removed or RemovalStatus.RemovedAtRestart)) return;

        try
        {
            var folder = EnsureFolder();
            var manifest = new RemovalManifest
            {
                Format = RemovalManifest.CurrentFormat,
                Program = options.ProgramName,
                RemovedAt = DateTimeOffset.Now,
                Elevated = ElevationValidation.IsCurrentProcessElevated(),
                Account = ElevationValidation.CurrentUserSid(),
                RecycleBin = options.UseRecycleBin,
                Items = report.Outcomes.Select(o => new ManifestItem
                {
                    Kind = o.Item.Kind.ToString(),
                    Path = o.Item.Path,
                    RegistryKey = o.Item.RegistryKey?.DisplayPath,
                    ValueName = o.Item.ValueName,
                    DisplayPath = o.Item.DisplayPath,
                    Owner = o.Item.Owner,
                    Status = o.Status.ToString(),
                    Message = o.Message,
                    Reason = o.Item.Reason,
                    Backup = o.BackupFile,
                    Overridden = o.Overridden,
                    Recycled = o.Recycled,
                    At = o.At
                }).ToList()
            };

            File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(manifest, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 60 ? clean[..60] : clean;
    }
}

/// <summary>
/// The tree under ProgramData where removals made as administrator keep their backups, locked
/// against change by standard accounts, and telling whether a folder in it still is.
/// </summary>
public static class BackupTrust
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// A folder of its own in ProgramData, which any account can create folders in. If a
    /// standard account made it first, <see cref="TryEnsureAdminFolder"/> takes it back and locks
    /// it, and what that account put in it stays untrusted.
    /// </summary>
    public static string AdminRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Uproot Backups");

    /// <summary>
    /// The tree WGUpdater used, when the uninstaller was part of it. Its backups are trusted on
    /// the same terms as Uproot's own; nothing new is written there.
    /// </summary>
    public static string LegacyAdminRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WGUpdater Uninstall Backups");

    private static IEnumerable<string> AdminRoots => [AdminRoot, LegacyAdminRoot];

    /// <summary>The account's own folder in the admin tree.</summary>
    public static string AdminFolderFor(string sid) => Path.Combine(AdminRoot, sid);

    /// <summary>The account's folder in WGUpdater's admin tree.</summary>
    public static string LegacyAdminFolderFor(string sid) => Path.Combine(LegacyAdminRoot, sid);

    /// <summary>
    /// The account's folder in the admin tree, created or taken back and locked, every level from
    /// the root down. Null when that cannot be done, and the caller falls back to the profile.
    /// Must be called elevated.
    /// </summary>
    public static string? TryEnsureAdminFolder()
    {
        if (ElevationValidation.CurrentUserSid() is not { } sid) return null;

        try
        {
            Secure(AdminRoot, reader: null);

            var folder = AdminFolderFor(sid);
            Secure(folder, reader: sid);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or PrivilegeNotHeldException or IdentityNotMappedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes one level of the tree locked: creates it locked, or, when it exists already — made
    /// first by someone else, say — takes ownership and replaces its permissions. A link is never
    /// accepted. What such a folder already holds stays untrusted, since its entries keep their
    /// own owners and fail <see cref="IsTrusted"/>.
    /// </summary>
    private static void Secure(string path, string? reader)
    {
        var info = new DirectoryInfo(path);

        if (!info.Exists)
        {
            CreateLocked(path, reader);
        }
        else
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException($"{path} is a link.");

            if (!IsLocked(info.GetAccessControl()))
            {
                // Ownership first, in its own write, so the new permissions are ours to set.
                var ownership = new DirectorySecurity();
                ownership.SetOwner(Administrators);
                info.SetAccessControl(ownership);
                info.SetAccessControl(LockedDescriptor(reader));
            }
        }

        info.Refresh();
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsLocked(info.GetAccessControl()))
            throw new InvalidOperationException($"{path} could not be locked.");
    }

    /// <summary>True for a folder inside an admin tree, Uproot's or WGUpdater's.</summary>
    public static bool IsAdminLocation(string folder) =>
        AdminRoots.Any(root => PathRules.IsStrictlyWithin(folder, root) || PathRules.SamePath(folder, root));

    /// <summary>
    /// A backup folder whose contents can be trusted as written by an administrator: inside an
    /// admin tree, locked itself along with everything in it, and with every folder above it up
    /// to that tree's root locked too, so nothing can have been renamed or swapped in along the way.
    /// </summary>
    public static bool IsTrusted(string folder)
    {
        var normalized = PathRules.Normalize(folder);
        if (normalized is null) return false;

        var root = AdminRoots.FirstOrDefault(r => PathRules.IsStrictlyWithin(normalized, r));
        if (root is null || !IsLocked(normalized)) return false;

        try
        {
            for (var parent = Path.GetDirectoryName(normalized); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                var info = new DirectoryInfo(parent);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsLocked(info.GetAccessControl())) return false;
                if (PathRules.SamePath(parent, root)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return false;
    }

    /// <summary>TrustedInstaller, which owns much of Windows and may appear as an owner.</summary>
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static bool IsTrustedPrincipal(IdentityReference? identity) =>
        identity is SecurityIdentifier sid && (sid == System || sid == Administrators || sid == TrustedInstaller);

    /// <summary>
    /// Creates a folder that administrators and SYSTEM control and <paramref name="reader"/>, when
    /// given, can only read. Must be called elevated.
    /// </summary>
    public static void CreateLocked(string path, string? reader) => LockedDescriptor(reader).CreateDirectory(path);

    private static DirectorySecurity LockedDescriptor(string? reader)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Administrators);

        foreach (var sid in new[] { System, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        if (reader is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(reader),
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// True when the folder and everything in it can be changed only by administrators, SYSTEM or
    /// TrustedInstaller: owned by one of them, and granting nobody else any right to write,
    /// delete, re-permission or take ownership. A link is never trusted.
    /// </summary>
    public static bool IsLocked(string folder)
    {
        try
        {
            var root = new DirectoryInfo(folder);
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (!IsLocked(root.GetAccessControl())) return false;

            foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

                FileSystemSecurity security = entry is DirectoryInfo directory
                    ? directory.GetAccessControl()
                    : ((FileInfo)entry).GetAccessControl();

                if (!IsLocked(security)) return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or PrivilegeNotHeldException or IdentityNotMappedException)
        {
            return false;
        }
    }

    private static bool IsLocked(FileSystemSecurity security)
    {
        if (!IsTrustedPrincipal(security.GetOwner(typeof(SecurityIdentifier)))) return false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || IsTrustedPrincipal(rule.IdentityReference)) continue;

            // Generic rights are stored unmapped on inheritable entries; GENERIC_ALL and
            // GENERIC_WRITE grant writing just as surely as the specific bits.
            const int genericWriteOrAll = 0x40000000 | 0x10000000;
            if ((rule.FileSystemRights & WriteRights) != 0 || ((int)rule.FileSystemRights & genericWriteOrAll) != 0) return false;
        }

        return true;
    }
}
