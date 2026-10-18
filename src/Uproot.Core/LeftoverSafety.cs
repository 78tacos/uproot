using Microsoft.Win32;

namespace Uproot.Core;

/// <summary>The folders a safety policy protects, separated from how they are found so tests can supply their own.</summary>
public sealed record SafetyRules
{
    /// <summary>Nothing at or below these may be removed.</summary>
    public IReadOnlyList<string> ProtectedTrees { get; init; } = [];

    /// <summary>These folders may not be removed, though what is inside them may.</summary>
    public IReadOnlyList<string> ProtectedFolders { get; init; } = [];

    /// <summary>Carve-outs from <see cref="ProtectedTrees"/>: anything strictly below these is allowed.</summary>
    public IReadOnlyList<string> AllowedWithinTrees { get; init; } = [];

    /// <summary>
    /// Uproot's own data and the backups it restores from, WGUpdater's included. Nothing at or
    /// below these may be removed: a scan for a program whose name matches a backup's folder
    /// would otherwise offer the backup of its own last removal.
    /// </summary>
    public IReadOnlyList<string> OwnTrees { get; init; } = [];

    /// <summary>
    /// Keys to treat as environment blocks for PATH entries, besides every account's own
    /// Environment key and the machine-wide one, which always count. Tests supply their own.
    /// </summary>
    public IReadOnlyList<RegistryLocation> EnvironmentKeys { get; init; } = [];

    /// <summary>Standard subfolders of every Start menu, which belong to Windows rather than to any program.</summary>
    private static readonly string[] StartMenuSubfolders =
    [
        "Startup", "Accessories", "Administrative Tools", "System Tools", "Windows PowerShell",
        "Maintenance", "Accessibility", "Windows Accessories", "Windows Administrative Tools",
        "Windows Ease of Access", "Windows System", "Windows Tools"
    ];

    /// <param name="profileRoots">Every profile folder on the PC.</param>
    /// <param name="knownFolders">
    /// Accounts' own folders wherever they have been redirected — Documents in OneDrive, say — and
    /// their OneDrive folders, from <see cref="UserProfiles.KnownFolders"/>. Standard folders like
    /// any other: never removed themselves, and nothing holding one is either.
    /// </param>
    public static SafetyRules ForThisMachine(IEnumerable<string> profileRoots, IEnumerable<string>? knownFolders = null)
    {
        string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        var windows = Folder(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Folder(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Folder(Environment.SpecialFolder.ProgramFilesX86);
        var programData = Folder(Environment.SpecialFolder.CommonApplicationData);
        var systemDrive = Path.GetPathRoot(windows) ?? @"C:\";
        var users = Path.GetDirectoryName(Folder(Environment.SpecialFolder.UserProfile).TrimEnd('\\')) ?? Path.Combine(systemDrive, "Users");
        var publicProfile = Environment.GetEnvironmentVariable("PUBLIC") ?? Path.Combine(users, "Public");
        var commonPrograms = Folder(Environment.SpecialFolder.CommonPrograms);

        var trees = new List<string>
        {
            windows,
            Path.Combine(systemDrive, "$Recycle.Bin"),
            Path.Combine(systemDrive, "System Volume Information"),
            Path.Combine(systemDrive, "Recovery"),
            Path.Combine(systemDrive, "Boot"),
            Path.Combine(users, "Default"),
            Path.Combine(users, "All Users"),
            Path.Combine(programData, "Package Cache"),
            Path.Combine(programData, "regid.1991-06.com.microsoft"),
            Path.Combine(programFiles, "WindowsApps"),
            Path.Combine(programFiles, "ModifiableWindowsApps")
        };

        foreach (var name in new[]
                 {
                     "Windows", "Windows Defender", "Crypto", "Windows NT", "Network", "Diagnosis", "Search",
                     "Provisioning", "DeviceSync", "Vault", "IdentityCRL", "Spectrum", "NetFramework",
                     "Windows Security Health", "User Account Pictures", "Storage Health"
                 })
        {
            trees.Add(Path.Combine(programData, "Microsoft", name));
        }

        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            foreach (var name in new[]
                     {
                         "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows NT", "Windows Mail",
                         "Windows Media Player", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security",
                         "Windows Sidebar", "WindowsPowerShell", "Internet Explorer", @"Common Files\microsoft shared",
                         @"Common Files\System", @"Common Files\Services", @"Common Files\SpeechEngines"
                     })
            {
                trees.Add(Path.Combine(root, name));
            }
        }

        var own = new List<string>
        {
            RemovalBackup.DataRoot, RemovalBackup.LegacyDefaultRoot, BackupTrust.AdminRoot, BackupTrust.LegacyAdminRoot
        };

        var folders = new List<string>
        {
            // The folder Uproot runs from: it may hold other things, but it is not a leftover.
            AppContext.BaseDirectory,
            programFiles, programFilesX86,
            Path.Combine(programFiles, "Common Files"), Path.Combine(programFilesX86, "Common Files"),
            programData, Path.Combine(programData, "Microsoft"),
            users, publicProfile,
            Folder(Environment.SpecialFolder.CommonStartMenu), commonPrograms,
            Folder(Environment.SpecialFolder.CommonDesktopDirectory),
            Folder(Environment.SpecialFolder.CommonDocuments)
        };

        folders.AddRange(StartMenuSubfolders.Select(s => Path.Combine(commonPrograms, s)));
        folders.AddRange(DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName));

        foreach (var name in new[] { "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos" })
            folders.Add(Path.Combine(publicProfile, name));

        foreach (var profile in profileRoots)
        {
            var roaming = Path.Combine(profile, "AppData", "Roaming");
            var local = Path.Combine(profile, "AppData", "Local");
            var startMenu = Path.Combine(roaming, "Microsoft", "Windows", "Start Menu");
            var programs = Path.Combine(startMenu, "Programs");
            var quickLaunch = Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch");

            folders.AddRange(
            [
                profile, Path.Combine(profile, "AppData"), roaming, local, Path.Combine(profile, "AppData", "LocalLow"),
                Path.Combine(local, "Programs"), Path.Combine(local, "Temp"),
                Path.Combine(roaming, "Microsoft"), Path.Combine(local, "Microsoft"),
                Path.Combine(roaming, "Microsoft", "Windows"), startMenu, programs,
                quickLaunch, Path.Combine(quickLaunch, "User Pinned"), Path.Combine(quickLaunch, "User Pinned", "TaskBar"),
                Path.Combine(roaming, "Microsoft", "Internet Explorer")
            ]);

            folders.AddRange(StartMenuSubfolders.Select(s => Path.Combine(programs, s)));

            own.Add(Path.Combine(local, "Uproot"));
            own.Add(Path.Combine(local, "WGUpdater", "Uninstall backups"));

            foreach (var name in new[] { "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos",
                                         "Favorites", "Links", "Saved Games", "Contacts", "Searches", "OneDrive" })
            {
                folders.Add(Path.Combine(profile, name));
            }

            trees.AddRange(
            [
                Path.Combine(local, "Microsoft", "Windows"),
                Path.Combine(local, "Microsoft", "WindowsApps"),
                Path.Combine(local, "Packages"),
                Path.Combine(roaming, "Microsoft", "Windows", "Recent"),
                Path.Combine(roaming, "Microsoft", "Windows", "SendTo"),
                Path.Combine(roaming, "Microsoft", "Windows", "Themes"),
                Path.Combine(roaming, "Microsoft", "Windows", "Libraries"),
                Path.Combine(roaming, "Microsoft", "Windows", "Network Shortcuts"),
                Path.Combine(roaming, "Microsoft", "Windows", "Printer Shortcuts"),
                Path.Combine(roaming, "Microsoft", "Windows", "Templates"),
                Path.Combine(roaming, "Microsoft", "Protect"),
                Path.Combine(roaming, "Microsoft", "Crypto"),
                Path.Combine(roaming, "Microsoft", "SystemCertificates")
            ]);
        }

        folders.AddRange(knownFolders ?? []);

        return new SafetyRules
        {
            ProtectedTrees = trees,
            ProtectedFolders = folders,
            OwnTrees = own,

            // The all-users Start menu sits inside Windows' own ProgramData folder, but program
            // shortcuts and folders in it are exactly the leftovers worth removing.
            AllowedWithinTrees = [commonPrograms]
        };
    }
}

/// <summary>What another installed program lays claim to: its folders and its names.</summary>
public sealed record ProgramClaim(string DisplayName, IReadOnlyList<string> Folders, IReadOnlyList<string> Names)
{
    public static ProgramClaim From(InstalledProgram program)
    {
        var folders = new List<string>();

        void Add(string? path)
        {
            if (PathRules.Normalize(path) is { } normalized && !folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                folders.Add(normalized);
        }

        Add(program.InstallLocation);
        if (program.Values.TryGetValue("Inno Setup: App Path", out var innoPath)) Add(innoPath);

        // The folders its own executables sit in, unless those are shared system locations that
        // prove nothing about ownership: msiexec lives in System32, Burn caches in Package Cache.
        foreach (var command in new[] { program.UninstallString, program.DisplayIcon?.Split(',')[0] })
        {
            if (PathRules.CommandExecutable(command) is not { } exe) continue;
            if (IsSharedExecutableLocation(exe)) continue;
            Add(UninstallerAnalyzer.ProgramFolderFromExecutable(exe));
        }

        var names = ProgramNames.ProductNames(program.DisplayName, program.Publisher, program.InstallLocation);
        return new ProgramClaim(program.DisplayName, folders, names);
    }

    internal static bool IsSharedExecutableLocation(string executable)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return PathRules.IsWithin(executable, windows)
               || PathRules.IsWithin(executable, Path.Combine(programData, "Package Cache"))
               || PathRules.IsWithin(executable, Path.Combine(programData, "Microsoft"))
               || PathRules.IsWithin(executable, Path.GetTempPath());
    }
}

/// <summary>The outcome of checking a candidate name against every other installed program.</summary>
public sealed record NameVerdict(bool Blocked, string? Note)
{
    public static readonly NameVerdict Clear = new(false, null);
}

/// <summary>
/// Why something must be kept, and how firmly.
///
/// An absolute verdict is a floor nobody can override: Windows itself, the standard folders and
/// keys every program shares, and anything the removal could not be contained to. The rest —
/// another program's folder, a name that matches another program better, a service whose program
/// cannot be identified — are judgements a person may overrule, having been warned.
/// </summary>
public sealed record SafetyVerdict(string Reason, bool Absolute)
{
    /// <summary>
    /// The installed programs whose things this would take, when that is the reason. A person who
    /// overrides protection is shown them; the remover checks none has been added since.
    /// </summary>
    public IReadOnlyList<string> Claimants { get; init; } = [];

    public static SafetyVerdict Always(string reason) => new(reason, true);
    public static SafetyVerdict UnlessOverridden(string reason) => new(reason, false);

    /// <summary>The firmer of two verdicts: absolute beats overridable, and either beats none.</summary>
    public static SafetyVerdict? Firmest(SafetyVerdict? a, SafetyVerdict? b) =>
        a is null ? b : b is null ? a : b.Absolute && !a.Absolute ? b : a;
}

/// <summary>
/// Decides what may never be offered for removal, whichever scan mode found it.
///
/// Three kinds of rule. Some locations belong to Windows and are off limits outright. Some
/// folders and keys are containers that everything uses — Program Files, AppData, a file
/// extension's key — which may hold leftovers but may never be removed themselves. And some
/// things belong to another program that is still installed: its folders, and names that match
/// it better than they match the program being removed.
///
/// The scanner consults this when it builds the list, and the remover consults it again
/// immediately before deleting anything, so an item cannot reach deletion without passing twice.
/// </summary>
public sealed class LeftoverSafety
{
    private readonly SafetyRules _rules;
    private readonly IReadOnlyList<ProgramClaim> _others;
    private readonly HashSet<string> _protectedFolders;

    public LeftoverSafety(SafetyRules rules, IEnumerable<ProgramClaim> otherPrograms)
    {
        _rules = rules with
        {
            ProtectedTrees = rules.ProtectedTrees.Select(PathRules.Normalize).OfType<string>().ToList(),
            AllowedWithinTrees = rules.AllowedWithinTrees.Select(PathRules.Normalize).OfType<string>().ToList(),
            OwnTrees = rules.OwnTrees.Select(PathRules.Normalize).OfType<string>().ToList()
        };

        _protectedFolders = new HashSet<string>(
            rules.ProtectedFolders.Select(PathRules.Normalize).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        // A claim on a protected folder is noise — some installers record C:\ or Program Files as
        // their location — and honouring it would block everything underneath.
        _others = otherPrograms
            .Select(o => o with { Folders = o.Folders.Where(f => !IsProtectedFolder(f) && !IsInProtectedTree(f)).ToList() })
            .ToList();
    }

    public IReadOnlyList<ProgramClaim> OtherPrograms => _others;

    public static LeftoverSafety ForThisMachine(IEnumerable<InstalledProgram> otherPrograms, IEnumerable<string> profileRoots,
        IEnumerable<string>? knownFolders = null) =>
        new(SafetyRules.ForThisMachine(profileRoots, knownFolders), otherPrograms.Select(ProgramClaim.From));

    // ---- Files and folders -------------------------------------------------

    /// <summary>Why a file or folder must not be removed, or null when it may be.</summary>
    public string? CheckPath(string? path) => JudgePath(path)?.Reason;

    /// <summary>
    /// Why a file or folder must not be removed, and whether that can be overridden. Windows'
    /// folders, shared standard folders, drive roots and network paths never can; another
    /// installed program's folder can.
    /// </summary>
    public SafetyVerdict? JudgePath(string? path)
    {
        var normalized = PathRules.Normalize(path);
        if (normalized is null) return SafetyVerdict.Always("Not a full local path.");

        // Nothing a program leaves behind lives on a network share, and a UNC spelling of a local
        // folder (\\localhost\C$\Windows) would otherwise match none of the rules below.
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            return SafetyVerdict.Always("A network path; only local leftovers are removed.");

        if (Path.GetPathRoot(normalized) is { } root && normalized.Length <= root.Length)
            return SafetyVerdict.Always("A drive root.");

        if (_rules.OwnTrees.Any(t => PathRules.IsWithin(normalized, t)))
            return SafetyVerdict.Always("Uproot's own data, or the backups it puts removed items back from.");

        if (IsInProtectedTree(normalized)) return SafetyVerdict.Always("Part of Windows or a location Windows manages.");

        if (IsProtectedFolder(normalized)) return SafetyVerdict.Always("A standard folder that other programs use too.");

        if (Path.GetFileName(normalized).StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)
            && _protectedFolders.Contains(Path.GetDirectoryName(normalized) ?? string.Empty))
        {
            return SafetyVerdict.Always("A OneDrive folder.");
        }

        // A folder holding a protected one cannot go either, whatever anyone chooses: the protected
        // folder would go with it. The standard folders sit at fixed places inside each other, so
        // this only matters for a path that is not itself one of them. Both lists are normalized
        // already, so a plain prefix test is enough and keeps this cheap on a hot path.
        var prefix = normalized + @"\";
        if (_protectedFolders.Concat(_rules.ProtectedTrees)
                .FirstOrDefault(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } inside)
        {
            return SafetyVerdict.Always($"Contains {inside}, which is part of Windows or a standard folder.");
        }

        if (_rules.OwnTrees.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } backups)
            return SafetyVerdict.Always($"Contains {backups}, where Uproot keeps the backups it puts removed items back from.");

        // Every installed program the folder belongs to or holds, not just the first: overriding
        // protection takes all of them, so all of them are named.
        var belongsTo = new List<string>();
        var contains = new List<string>();

        foreach (var other in _others)
        {
            foreach (var folder in other.Folders)
            {
                if (PathRules.IsWithin(normalized, folder)) { belongsTo.Add(other.DisplayName); break; }
                if (PathRules.IsStrictlyWithin(folder, normalized)) { contains.Add(other.DisplayName); break; }
            }
        }

        if (belongsTo.Count == 0 && contains.Count == 0) return null;

        var parts = new List<string>();
        if (belongsTo.Count > 0) parts.Add($"Belongs to {Join(belongsTo)}");
        if (contains.Count > 0) parts.Add($"{(parts.Count > 0 ? "contains" : "Contains")} {Join(contains)}");

        var plural = belongsTo.Count + contains.Count > 1;
        return SafetyVerdict.UnlessOverridden($"{string.Join(" and ", parts)}, which {(plural ? "are" : "is")} still installed.")
            with { Claimants = belongsTo.Concat(contains).Distinct().ToList() };
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + $" and {names[^1]}"
    };

    private bool IsProtectedFolder(string normalized) => _protectedFolders.Contains(normalized);

    /// <summary>
    /// Why a command, image path or value that mentions a path belongs to another installed
    /// program, or null. A task or service pointing into someone else's folder is theirs, however
    /// it was found.
    /// </summary>
    public string? ClaimedByOther(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        foreach (var other in _others)
        {
            foreach (var folder in other.Folders)
            {
                if (PathRules.References(text, folder))
                    return $"Runs something in {other.DisplayName}'s folder, and it is still installed.";
            }
        }

        return null;
    }

    private bool IsInProtectedTree(string normalized) =>
        _rules.ProtectedTrees.Any(t => PathRules.IsWithin(normalized, t))
        && !_rules.AllowedWithinTrees.Any(a => PathRules.IsStrictlyWithin(normalized, a));

    // ---- Names -------------------------------------------------------------

    /// <summary>
    /// Checks a name-matched candidate against every other installed program. A name that matches
    /// another program better is theirs; one that matches equally well, or that is the start of
    /// another program's name ("Acrobat" when "Acrobat Reader" is installed), is ambiguous.
    /// </summary>
    public NameVerdict CheckName(string candidateName, NameMatch ourMatch)
    {
        string? note = null;

        foreach (var other in _others)
        {
            var (theirs, _) = ProgramNames.Best(candidateName, other.Names);

            if (theirs > ourMatch)
                return new NameVerdict(true, $"Matches {other.DisplayName}, which is still installed, more closely.");

            if (theirs != NameMatch.None && theirs == ourMatch)
                note ??= $"Also matches {other.DisplayName}, which is still installed.";

            if (ourMatch == NameMatch.Exact
                && other.Names.Any(n => ProgramNames.Compare(n, candidateName) == NameMatch.Prefix))
            {
                note ??= $"{other.DisplayName} is also installed and may keep its data here.";
            }
        }

        return note is null ? NameVerdict.Clear : new NameVerdict(false, note);
    }

    // ---- Registry ----------------------------------------------------------

    private static readonly string[] ProtectedMachineTrees =
    [
        "SAM", "SECURITY", "HARDWARE", "BCD00000000", "SYSTEM",
        @"SOFTWARE\Microsoft\Windows NT", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer", @"SOFTWARE\Classes\Installer",
        @"SOFTWARE\Microsoft\Cryptography", @"SOFTWARE\Microsoft\Windows Defender",
        @"SOFTWARE\Policies", @"SOFTWARE\WOW6432Node\Policies",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy",
        @"SOFTWARE\Classes\Local Settings"
    ];

    private static readonly string[] ProtectedUserTrees =
    [
        @"Software\Policies", @"Software\Microsoft\Windows\CurrentVersion\Policies",
        @"Software\Microsoft\Windows\CurrentVersion\Group Policy", @"Software\Classes\Local Settings",
        @"Software\Microsoft\Windows\Shell", @"Software\Microsoft\Windows NT",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist",
        "Control Panel", "Keyboard Layout", "Printers", "System", "Network", "Console", "AppEvents"
    ];

    private static readonly string[] ProtectedKeys =
    [
        "", "SOFTWARE", @"SOFTWARE\WOW6432Node", @"SOFTWARE\Classes", @"SOFTWARE\Classes\WOW6432Node",
        @"SOFTWARE\Microsoft", @"SOFTWARE\WOW6432Node\Microsoft",
        @"SOFTWARE\Microsoft\Windows", @"SOFTWARE\WOW6432Node\Microsoft\Windows",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
        @"SOFTWARE\Clients", @"SOFTWARE\RegisteredApplications", @"SOFTWARE\WOW6432Node\RegisteredApplications",
        "Environment", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer"
    ];

    /// <summary>Class keys shared by every program: file types, "*", folders, drives.</summary>
    private static readonly HashSet<string> SharedClassKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "Directory", "Drive", "Folder", "AllFilesystemObjects", "SystemFileAssociations", "Applications",
        "Unknown", "exefile", "lnkfile", "batfile", "cmdfile", "txtfile", "htmlfile", "http", "https",
        "InternetShortcut", "CLSID", "Interface", "TypeLib", "AppID", "WOW6432Node", "Background",
        "DesktopBackground", "LibraryFolder", "MIME", "Protocols", "PackagedCom"
    };

    /// <summary>Keys that are only ever containers for other programs' registrations.</summary>
    private static readonly HashSet<string> ContainerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell", "shellex", "ContextMenuHandlers", "PropertySheetHandlers", "DragDropHandlers", "CopyHookHandlers",
        "ColumnHandlers", "IconHandler", "OpenWithProgids", "OpenWithList", "ShellNew", "PersistentHandler",
        "Implemented Categories", "Content Type"
    };

    /// <summary>Why a registry key must not be removed, or null when it may be.</summary>
    public string? CheckRegistryKey(RegistryLocation key) => JudgeRegistryKey(key)?.Reason;

    /// <summary>
    /// Why a registry key must not be removed. Every rule here is absolute: Windows' own trees, the
    /// standard keys and shared class keys hold everyone's settings, so no choice can make removing
    /// them safe. An event log source is the one key inside Windows' trees that may go, and only
    /// when its messages come from the program's own files.
    /// </summary>
    public SafetyVerdict? JudgeRegistryKey(RegistryLocation key)
    {
        var (isUser, relative) = Relative(key);

        if (relative.Length == 0) return SafetyVerdict.Always("A registry hive root.");

        if (!isUser && EventLogSource(relative) is { } source) return JudgeEventLogSource(key, source.Log, source.Name);

        var trees = isUser ? ProtectedUserTrees : ProtectedMachineTrees;
        if (trees.Any(t => IsKeyWithin(relative, t))) return SafetyVerdict.Always("Part of Windows' own configuration.");

        if (ProtectedKeys.Any(k => relative.Equals(k, StringComparison.OrdinalIgnoreCase))
            || (isUser && relative.Equals("Software", StringComparison.OrdinalIgnoreCase)))
        {
            return SafetyVerdict.Always("A standard key that other programs use too.");
        }

        // Under Classes, shared class keys and the containers beneath them stay; a program's own
        // ProgID, CLSID or verb underneath them may go.
        var classesPrefix = isUser ? @"Software\Classes\" : @"SOFTWARE\Classes\";
        if (relative.StartsWith(classesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var segments = relative[classesPrefix.Length..].Split('\\');
            var leaf = segments[^1];

            if (segments.Length == 1 && (leaf.StartsWith('.') || SharedClassKeys.Contains(leaf)))
                return SafetyVerdict.Always("A file type or class key that other programs use too.");

            if (ContainerKeys.Contains(leaf) || leaf.StartsWith('.') || SharedClassKeys.Contains(leaf))
            {
                var underShared = segments.Take(segments.Length - 1)
                    .Any(s => s.StartsWith('.') || SharedClassKeys.Contains(s) || ContainerKeys.Contains(s));

                if (underShared || ContainerKeys.Contains(leaf))
                    return SafetyVerdict.Always("A shared container key; only the entries inside it that belong to the program may go.");
            }
        }

        return null;
    }

    /// <summary>Why a single value must not be removed. Values inside protected trees stay.</summary>
    public string? CheckRegistryValue(RegistryLocation key, string? valueName) => JudgeRegistryValue(key, valueName)?.Reason;

    /// <summary>
    /// Why a single value must not be removed. Values inside Windows' trees stay, except in the few
    /// keys where Windows keeps one value per program path — its compatibility records and the
    /// like — and then only a value naming a path that is not Windows' own.
    /// </summary>
    public SafetyVerdict? JudgeRegistryValue(RegistryLocation key, string? valueName)
    {
        var (isUser, relative) = Relative(key);

        if ((isUser ? UserTraceKeys : MachineTraceKeys).Any(t => relative.Equals(t, StringComparison.OrdinalIgnoreCase)))
            return JudgeTraceValue(valueName);

        var trees = isUser ? ProtectedUserTrees : ProtectedMachineTrees;
        if (trees.Any(t => IsKeyWithin(relative, t))) return SafetyVerdict.Always("Part of Windows' own configuration.");

        // PATH and the like are single values holding many programs' entries. Their parts are
        // removed one at a time as PATH entries instead.
        if (isUser && relative.Equals("Environment", StringComparison.OrdinalIgnoreCase))
            return SafetyVerdict.Always("An environment variable that other programs use too.");

        if (!isUser && SharedDllsKeys.Any(k => relative.Equals(k, StringComparison.OrdinalIgnoreCase)))
            return JudgeSharedDllsValue(valueName);

        return null;
    }

    // ---- Windows' own records of programs ----------------------------------

    /// <summary>
    /// Keys in which Windows keeps one value per program, named by the program's path: the names
    /// Explorer caches for menus, and the compatibility settings and records. Windows recreates
    /// any of them it needs, so a value for a program that is gone is safe to remove — the key
    /// itself never is.
    /// </summary>
    private static readonly string[] UserTraceKeys =
    [
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
    ];

    private static readonly string[] MachineTraceKeys =
    [
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders"
    ];

    public static IReadOnlyList<string> UserTraceKeyPaths => UserTraceKeys;
    public static IReadOnlyList<string> MachineTraceKeyPaths => MachineTraceKeys;

    private static readonly string[] SharedDllsKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs"
    ];

    /// <summary>The path a trace value is named after, without MuiCache's suffixes.</summary>
    public static string? TracedPath(string? valueName)
    {
        if (string.IsNullOrEmpty(valueName)) return null;

        foreach (var suffix in new[] { ".FriendlyAppName", ".ApplicationCompany" })
        {
            if (valueName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return valueName[..^suffix.Length];
        }

        return valueName;
    }

    private SafetyVerdict? JudgeTraceValue(string? valueName)
    {
        var path = PathRules.Normalize(TracedPath(valueName));
        if (path is null) return SafetyVerdict.Always("Not a record about a program path.");

        return JudgePath(path) switch
        {
            null => null,
            { Absolute: true } => SafetyVerdict.Always("Windows' record about one of its own programs or folders."),
            var other => other
        };
    }

    /// <summary>
    /// A SharedDLLs count goes only when the file it counts is gone and is not Windows' own:
    /// while the file is there, the count is what stops the next uninstaller deleting it from
    /// under whoever else uses it.
    /// </summary>
    private SafetyVerdict? JudgeSharedDllsValue(string? valueName)
    {
        var path = PathRules.Normalize(valueName);
        if (path is null) return SafetyVerdict.Always("Not a file path.");

        if (JudgePath(path) is { Absolute: true }) return SafetyVerdict.Always("Counts a file that is part of Windows.");
        if (File.Exists(path)) return SafetyVerdict.Always("The file is still there, and other programs may count on it.");

        return null;
    }

    /// <summary>The log and source of an event log source key, when that is what the key is.</summary>
    public static (string Log, string Name)? EventLogSourceOf(RegistryLocation key) =>
        key.Hive == RegistryHive.LocalMachine ? EventLogSource(key.SubKey) : null;

    /// <summary>The log and source names of HKLM\SYSTEM\CurrentControlSet\Services\EventLog\&lt;log&gt;\&lt;source&gt;.</summary>
    private static (string Log, string Name)? EventLogSource(string relative)
    {
        var segments = relative.Split('\\');
        return segments.Length == 6
               && segments[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
               && segments[1].Equals("CurrentControlSet", StringComparison.OrdinalIgnoreCase)
               && segments[2].Equals("Services", StringComparison.OrdinalIgnoreCase)
               && segments[3].Equals("EventLog", StringComparison.OrdinalIgnoreCase)
            ? (segments[4], segments[5])
            : null;
    }

    private SafetyVerdict? JudgeEventLogSource(RegistryLocation key, string log, string source)
    {
        using var open = key.Open();
        if (open is null) return null;

        var files = new[] { "EventMessageFile", "CategoryMessageFile", "ParameterMessageFile" }
            .Select(name => open.GetValue(name) as string)
            .OfType<string>()
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return JudgeEventLogSource(log, source, files);
    }

    /// <summary>
    /// An event log source may go when every file it takes its messages from is the program's:
    /// not Windows', and not another installed program's. The log's own key never goes, and nor
    /// does a source with no message files, whose owner cannot be told.
    /// </summary>
    internal SafetyVerdict? JudgeEventLogSource(string log, string source, IEnumerable<string> messageFiles)
    {
        if (source.Equals(log, StringComparison.OrdinalIgnoreCase)) return SafetyVerdict.Always("The event log's own source.");

        var files = messageFiles.Select(PathRules.Normalize).ToList();
        if (files.Count == 0 || files.Any(f => f is null)) return SafetyVerdict.Always("Whose messages these are cannot be told.");

        // .NET's shared message file says nothing about whose a source is — any program's may use
        // it — so it neither protects the source nor proves it the program's. The scanner then
        // goes by the source's name.
        foreach (var file in files.OfType<string>().Where(f => !IsGenericMessageFile(f)))
        {
            if (IsWindowsProgram(file)) return SafetyVerdict.Always("An event source for part of Windows.");
            if (ClaimedByOther(file) is { } claimed) return SafetyVerdict.UnlessOverridden(claimed);
        }

        return null;
    }

    /// <summary>.NET's shared event message file, which any program's event source may use.</summary>
    internal static bool IsGenericMessageFile(string path) =>
        Path.GetFileName(path).Equals("EventLogMessages.dll", StringComparison.OrdinalIgnoreCase);

    // ---- PATH entries ------------------------------------------------------

    /// <summary>The machine-wide environment block.</summary>
    public static readonly RegistryLocation MachineEnvironmentKey =
        RegistryLocation.Machine(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");

    /// <summary>True for an account's own Environment key or the machine-wide one.</summary>
    public bool IsEnvironmentKey(RegistryLocation key)
    {
        var (isUser, relative) = Relative(key);
        return (isUser && relative.Equals("Environment", StringComparison.OrdinalIgnoreCase))
               || key.Equals(MachineEnvironmentKey)
               || _rules.EnvironmentKeys.Contains(key);
    }

    /// <summary>
    /// Whether one entry may be taken out of a PATH value. Only PATH, only in an environment key,
    /// and only an entry naming a folder that could itself be removed: C:\Windows\System32 stays
    /// on PATH whatever anyone chooses, while another program's folder can be overridden.
    /// </summary>
    public SafetyVerdict? JudgePathEntry(RegistryLocation key, string? valueName, string entry)
    {
        if (!IsEnvironmentKey(key)) return SafetyVerdict.Always("Not an environment variable.");
        if (!string.Equals(valueName, "Path", StringComparison.OrdinalIgnoreCase))
            return SafetyVerdict.Always("Only entries in PATH are removed this way.");

        return JudgePath(entry.Trim().Trim('"')) switch
        {
            null => null,
            { Absolute: true } verdict => SafetyVerdict.Always($"A PATH entry for {entry}, which stays: {verdict.Reason}"),
            var other => other
        };
    }

    // ---- Services, tasks, firewall rules -----------------------------------

    /// <summary>
    /// Why a service must not be removed: one whose program is part of Windows — anywhere in its
    /// protected folders, which includes Defender's platform under ProgramData — or belongs to
    /// another installed program. A service with no readable image path is refused too, since
    /// nothing can then be said about whose it is.
    /// </summary>
    public string? CheckService(string name, string? imagePath) => JudgeService(name, imagePath)?.Reason;

    public SafetyVerdict? JudgeService(string name, string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/'))
            return SafetyVerdict.Always("Not a valid service name.");

        var image = PathRules.ServiceImage(imagePath);
        if (image is null) return SafetyVerdict.UnlessOverridden("Its program could not be identified.");

        if (IsWindowsProgram(image)) return SafetyVerdict.Always("Runs a program that is part of Windows.");

        return ClaimedByOther(image) is { } claimed ? SafetyVerdict.UnlessOverridden(claimed) : null;
    }

    /// <summary>Why a scheduled task must not be removed: Windows' own, or one that runs another program.</summary>
    public string? CheckTask(string taskPath, IEnumerable<string>? commands = null) => JudgeTask(taskPath, commands)?.Reason;

    public SafetyVerdict? JudgeTask(string taskPath, IEnumerable<string>? commands = null)
    {
        if (taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) return SafetyVerdict.Always("Windows' own scheduled task.");

        SafetyVerdict? verdict = null;

        foreach (var command in commands ?? [])
        {
            if (PathRules.CommandExecutable(command) is { } exe && IsWindowsProgram(exe) && !IsCommonHost(exe))
                return SafetyVerdict.Always("Runs a program that is part of Windows.");

            if (ClaimedByOther(command) is { } claimed) verdict ??= SafetyVerdict.UnlessOverridden(claimed);
        }

        return verdict;
    }

    /// <summary>
    /// Whether a set of firewall rules sharing a name may go. They are removed by name, so every
    /// rule with it is judged: one that is Windows' own, or not tied to a single program, keeps
    /// them all.
    /// </summary>
    public SafetyVerdict? JudgeFirewallRules(IEnumerable<FirewallRuleInfo> rules)
    {
        SafetyVerdict? verdict = null;

        foreach (var rule in rules)
        {
            if (rule.Grouping?.StartsWith('@') == true) return SafetyVerdict.Always("One of Windows' own firewall rules.");

            if (string.IsNullOrWhiteSpace(rule.ApplicationName))
                return SafetyVerdict.Always("A rule shares this name without being tied to one program.");

            var app = PathRules.Normalize(rule.ApplicationName);
            if (app is null) return SafetyVerdict.Always("Its program could not be identified.");
            if (IsWindowsProgram(app)) return SafetyVerdict.Always("Lets a Windows program through the firewall.");

            if (ClaimedByOther(app) is { } claimed) verdict ??= SafetyVerdict.UnlessOverridden(claimed);
        }

        return verdict;
    }

    /// <summary>
    /// Inside a protected tree, or in the real Windows folder whatever rules this policy was
    /// given: nothing Windows itself runs is ever a leftover.
    /// </summary>
    private bool IsWindowsProgram(string path) =>
        IsInProtectedTree(path) || PathRules.IsWithin(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>
    /// Windows programs that tasks routinely use to run someone else's script. A task running
    /// rundll32 or PowerShell is not Windows' task on that account.
    /// </summary>
    private static bool IsCommonHost(string executable) =>
        Path.GetFileName(executable).ToLowerInvariant() is "rundll32.exe" or "cmd.exe" or "powershell.exe"
            or "wscript.exe" or "cscript.exe" or "msiexec.exe" or "conhost.exe" or "pwsh.exe";

    /// <summary>
    /// A key's path with the user-hive prefix removed, so HKCU\Software\X, HKU\S-1-5-21-…\Software\X
    /// and a mounted HKU\Uproot_…\Software\X are judged by the same rules.
    /// </summary>
    private static (bool IsUser, string Relative) Relative(RegistryLocation key)
    {
        switch (key.Hive)
        {
            case RegistryHive.CurrentUser:
                return (true, key.SubKey);

            case RegistryHive.Users:
            {
                var slash = key.SubKey.IndexOf('\\');
                var hiveName = slash < 0 ? key.SubKey : key.SubKey[..slash];
                var rest = slash < 0 ? string.Empty : key.SubKey[(slash + 1)..];

                // HKU\<sid>_Classes is that user's Software\Classes.
                if (hiveName.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    return (true, rest.Length == 0 ? @"Software\Classes" : @"Software\Classes\" + rest);

                return (true, rest);
            }

            default:
                return (false, key.SubKey);
        }
    }

    private static bool IsKeyWithin(string relative, string tree) =>
        relative.Equals(tree, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(tree + @"\", StringComparison.OrdinalIgnoreCase);
}
