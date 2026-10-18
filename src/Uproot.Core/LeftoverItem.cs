namespace Uproot.Core;

/// <summary>How hard the leftover scan looks.</summary>
public enum ScanMode
{
    /// <summary>
    /// Only what is provably the program's: its install folder, what its installer recorded, its
    /// own Publisher\Product keys and folders, and anything pointing into its install folder.
    /// </summary>
    Safe,

    /// <summary>
    /// Adds name-based matches in the usual places: Program Files, ProgramData, every included
    /// profile's AppData, the Start menu, services, tasks and COM registrations, and the records
    /// Windows keeps about programs it has run.
    /// </summary>
    Moderate,

    /// <summary>
    /// Adds partial name matches, publisher folders, Temp and Documents, and a search for the
    /// install path across HKLM\SOFTWARE and HKCU\Software. Uncertain items are never pre-selected.
    /// </summary>
    Expert
}

public enum LeftoverKind
{
    File,
    Folder,
    RegistryKey,
    RegistryValue,
    Service,
    ScheduledTask,

    /// <summary>One entry in a PATH variable; the variable itself stays, with the rest of its entries.</summary>
    PathEntry,

    /// <summary>Windows Firewall rules sharing one name, all for the program.</summary>
    FirewallRule
}

/// <summary>How leftovers are grouped for review, in the order they are shown.</summary>
public enum LeftoverCategory
{
    Entry,
    Services,
    ScheduledTasks,
    Startup,
    Firewall,
    Environment,
    Settings,
    Registrations,
    WindowsRecords,
    Shortcuts,
    Files
}

/// <summary>How sure the scan is that something belongs to the program being removed.</summary>
public enum LeftoverConfidence
{
    /// <summary>A plausible match that needs a person to judge. Never pre-selected.</summary>
    Possible,

    /// <summary>Matched by name in a place programs keep their own things. Pre-selected.</summary>
    Likely,

    /// <summary>Recorded by the installer, or points into the program's own folder. Pre-selected.</summary>
    Certain
}

/// <summary>Something the program left behind, with the evidence for thinking so.</summary>
public sealed record LeftoverItem
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>
    /// File or folder path; for registry items the key; for services and tasks their name; for a
    /// PATH entry the entry as written; for firewall rules the rules' name.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>The key a registry item, service or PATH entry lives in.</summary>
    public RegistryLocation? RegistryKey { get; init; }

    /// <summary>The value's name for a registry value or PATH entry; empty string is the key's default value.</summary>
    public string? ValueName { get; init; }

    public required LeftoverConfidence Confidence { get; init; }

    /// <summary>Why this was flagged, in plain words.</summary>
    public required string Reason { get; init; }

    /// <summary>The least thorough mode that finds it.</summary>
    public ScanMode Mode { get; init; } = ScanMode.Safe;

    /// <summary>
    /// A folder or key removed only if it is empty by the time its turn comes — a publisher
    /// folder, say, that should go only once everything of this program's inside it has.
    /// </summary>
    public bool OnlyIfEmpty { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>The account a per-user item belongs to. Null for machine-wide items.</summary>
    public string? Owner { get; init; }

    /// <summary>Set when the item was found but may not be removed; shown so nothing is silently skipped.</summary>
    public string? ProtectedBecause { get; init; }

    /// <summary>
    /// A protected item that may never be removed, whatever anyone chooses: part of Windows, a
    /// standard folder, or something removing would take a protected thing along with it. The
    /// rest of the protected items can be removed by someone who turns protection off.
    /// </summary>
    public bool KeptAlways { get; init; }

    /// <summary>A caveat that stops short of protecting it: another program might use it too.</summary>
    public string? Caution { get; init; }

    public bool IsProtected => ProtectedBecause is not null;

    /// <summary>Protected, but a person who has been warned may choose to remove it anyway.</summary>
    public bool CanOverride => IsProtected && !KeptAlways;

    /// <summary>Pre-selected: not protected, not in doubt.</summary>
    public bool Recommended => !IsProtected && Caution is null && Confidence >= LeftoverConfidence.Likely;

    /// <summary>Removing this needs administrator rights.</summary>
    public bool NeedsElevation { get; init; }

    public string DisplayPath => Kind switch
    {
        LeftoverKind.RegistryValue => $@"{Path}\{(string.IsNullOrEmpty(ValueName) ? "(Default)" : ValueName)}",
        LeftoverKind.PathEntry => $"{Path}   (PATH in {RegistryKey?.DisplayPath})",
        LeftoverKind.FirewallRule => $"Firewall rule \"{Path}\"",
        _ => Path
    };

    /// <summary>Identity for de-duplication: the same thing found two ways is one item.</summary>
    public string Identity => $"{Kind}|{DisplayPath}".ToUpperInvariant();

    public LeftoverCategory Category
    {
        get
        {
            switch (Kind)
            {
                case LeftoverKind.Service:
                    return LeftoverCategory.Services;

                case LeftoverKind.ScheduledTask:
                    return LeftoverCategory.ScheduledTasks;

                case LeftoverKind.FirewallRule:
                    return LeftoverCategory.Firewall;

                case LeftoverKind.PathEntry:
                    return LeftoverCategory.Environment;

                case LeftoverKind.RegistryKey or LeftoverKind.RegistryValue:
                    var path = RegistryKey?.SubKey ?? Path;
                    if (path.Contains(@"\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase)) return LeftoverCategory.Entry;
                    if (path.Contains(@"\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase)
                        || path.Contains(@"\App Paths\", StringComparison.OrdinalIgnoreCase))
                    {
                        return LeftoverCategory.Startup;
                    }

                    if (IsWindowsRecord(path)) return LeftoverCategory.WindowsRecords;

                    return path.Contains(@"\Classes", StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith("Classes", StringComparison.OrdinalIgnoreCase)
                           || (RegistryKey?.SubKey.Split('\\')[0].EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) ?? false)
                           || path.Contains(@"\Shell Extensions\", StringComparison.OrdinalIgnoreCase)
                           || path.Contains(@"\ShellIconOverlayIdentifiers\", StringComparison.OrdinalIgnoreCase)
                           || path.Contains(@"\Browser Helper Objects\", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(@"\RegisteredApplications", StringComparison.OrdinalIgnoreCase)
                        ? LeftoverCategory.Registrations
                        : LeftoverCategory.Settings;

                default:
                    return Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                           || Path.Contains(@"\Start Menu\", StringComparison.OrdinalIgnoreCase)
                        ? LeftoverCategory.Shortcuts
                        : LeftoverCategory.Files;
            }
        }
    }

    /// <summary>Keys where Windows itself keeps records about programs, rather than the program keeping its own.</summary>
    private static bool IsWindowsRecord(string subKey) =>
        subKey.Contains(@"\Shell\MuiCache", StringComparison.OrdinalIgnoreCase)
        || subKey.Contains(@"\AppCompatFlags\", StringComparison.OrdinalIgnoreCase)
        || subKey.Contains(@"\Installer\Folders", StringComparison.OrdinalIgnoreCase)
        || subKey.Contains(@"\SharedDLLs", StringComparison.OrdinalIgnoreCase)
        || subKey.Contains(@"\Services\EventLog\", StringComparison.OrdinalIgnoreCase);

    public string CategoryName => Category switch
    {
        LeftoverCategory.Entry => "Apps & Features entry",
        LeftoverCategory.Services => "Services",
        LeftoverCategory.ScheduledTasks => "Scheduled tasks",
        LeftoverCategory.Startup => "Startup entries and App Paths",
        LeftoverCategory.Firewall => "Windows Firewall rules",
        LeftoverCategory.Environment => "PATH entries",
        LeftoverCategory.Settings => "Program settings in the registry",
        LeftoverCategory.Registrations => "COM classes, file types and menu entries",
        LeftoverCategory.WindowsRecords => "Windows' records of the program",
        LeftoverCategory.Shortcuts => "Shortcuts",
        _ => "Files and folders"
    };

    public string KindName => Kind switch
    {
        LeftoverKind.File => "File",
        LeftoverKind.Folder => OnlyIfEmpty ? "Folder (if empty)" : "Folder",
        LeftoverKind.RegistryKey => OnlyIfEmpty ? "Registry key (if empty)" : "Registry key",
        LeftoverKind.RegistryValue => "Registry value",
        LeftoverKind.Service => "Service",
        LeftoverKind.ScheduledTask => "Scheduled task",
        LeftoverKind.PathEntry => "PATH entry",
        LeftoverKind.FirewallRule => "Firewall rule",
        _ => Kind.ToString()
    };
}

public sealed record LeftoverScanOptions
{
    public ScanMode Mode { get; init; } = ScanMode.Moderate;

    /// <summary>Look in every profile on the PC, not just the signed-in person's. Needs administrator rights.</summary>
    public bool IncludeAllProfiles { get; init; }

    /// <summary>
    /// True when the program's uninstaller has not been run, or failed: everything is still in
    /// use, and removing it is a forced uninstall rather than a clean-up.
    /// </summary>
    public bool ProgramStillInstalled { get; init; }
}

public sealed record LeftoverScanResult(IReadOnlyList<LeftoverItem> Items, IReadOnlyList<string> Notes, TimeSpan Elapsed)
{
    /// <summary>How long each stage took, for working out why a scan was slow.</summary>
    public IReadOnlyList<(string Stage, TimeSpan Elapsed)> Timings { get; init; } = [];

    public int RecommendedCount => Items.Count(i => i.Recommended);
    public long RecommendedBytes => TotalBytes(Items.Where(i => i.Recommended));

    /// <summary>
    /// Disk space the items occupy, counting a file or folder inside another listed folder once.
    /// A certain file can be listed inside a merely likely folder, and adding both would double it.
    /// </summary>
    public static long TotalBytes(IEnumerable<LeftoverItem> items)
    {
        var list = items.Where(i => i.SizeBytes is > 0).ToList();
        var folders = list.Where(i => i.Kind == LeftoverKind.Folder && !i.OnlyIfEmpty).ToList();

        return list
            .Where(i => !folders.Any(f => !ReferenceEquals(f, i) && PathRules.IsStrictlyWithin(i.Path, f.Path)))
            .Sum(i => i.SizeBytes ?? 0);
    }
}
