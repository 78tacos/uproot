using Uproot.Core.Elevation;

namespace Uproot.Core;

/// <summary>One profile's places to search: its folders, shortcuts and registry.</summary>
public sealed record ProfileScope
{
    /// <summary>Account name shown against per-user items. Null for machine-wide locations.</summary>
    public string? Owner { get; init; }

    /// <summary>The profile folder itself, for attributing files found by other means. Null for machine-wide locations.</summary>
    public string? ProfileRoot { get; init; }

    /// <summary>Folders programs keep their own things in: AppData\Roaming, Local, LocalLow, Local\Programs.</summary>
    public IReadOnlyList<string> DataFolders { get; init; } = [];

    /// <summary>Start menu and desktop folders, searched for shortcuts.</summary>
    public IReadOnlyList<string> ShortcutFolders { get; init; } = [];

    /// <summary>Temp folders, searched only in Expert mode.</summary>
    public IReadOnlyList<string> TempFolders { get; init; } = [];

    /// <summary>Documents, searched only in Expert mode and never pre-selected: it holds people's own files.</summary>
    public IReadOnlyList<string> DocumentFolders { get; init; } = [];

    /// <summary>Software roots: HKCU\Software, and its WOW6432Node where present.</summary>
    public IReadOnlyList<RegistryLocation> SoftwareKeys { get; init; } = [];

    /// <summary>Run and RunOnce keys.</summary>
    public IReadOnlyList<RegistryLocation> RunKeys { get; init; } = [];

    /// <summary>Software\Classes roots, for COM and file-type registrations.</summary>
    public IReadOnlyList<RegistryLocation> ClassesKeys { get; init; } = [];

    /// <summary>The Environment key whose PATH is checked for entries pointing into the program.</summary>
    public RegistryLocation? EnvironmentKey { get; init; }

    /// <summary>
    /// Keys where Windows keeps one value per program, named by its path: the names Explorer
    /// caches, compatibility settings and records, Windows Installer's folder list.
    /// </summary>
    public IReadOnlyList<RegistryLocation> TraceKeys { get; init; } = [];
}

/// <summary>
/// Where the leftover scanner looks. <see cref="ForThisMachine"/> builds the real thing; tests build
/// one pointing at temporary folders and a throwaway registry key, so running them cannot touch
/// anything real.
/// </summary>
public sealed record LeftoverScanContext
{
    /// <summary>Machine-wide locations, expressed as a profile with no owner.</summary>
    public required ProfileScope Machine { get; init; }

    public IReadOnlyList<ProfileScope> Profiles { get; init; } = [];

    /// <summary>App Paths keys, where programs register their executables by name.</summary>
    public IReadOnlyList<RegistryLocation> AppPathsKeys { get; init; } = [];

    /// <summary>HKLM\SYSTEM\CurrentControlSet\Services. Null to skip services.</summary>
    public RegistryLocation? ServicesKey { get; init; }

    /// <summary>HKLM\SYSTEM\CurrentControlSet\Services\EventLog. Null to skip event log sources.</summary>
    public RegistryLocation? EventLogKey { get; init; }

    /// <summary>SharedDLLs keys, where Windows counts the programs using each shared file.</summary>
    public IReadOnlyList<RegistryLocation> SharedDllsKeys { get; init; } = [];

    /// <summary>The firewall to check for rules. Null to skip it.</summary>
    public IFirewall? Firewall { get; init; }

    public bool IncludeScheduledTasks { get; init; }

    /// <summary>Profile folders, for the safety policy's list of standard folders.</summary>
    public IReadOnlyList<string> ProfileRoots { get; init; } = [];

    public IEnumerable<ProfileScope> AllScopes => Profiles.Prepend(Machine);

    /// <summary>
    /// The real machine. With <paramref name="includeAllProfiles"/>, other accounts' hives are
    /// loaded for as long as <paramref name="mounts"/> is alive, so dispose it promptly.
    /// <paramref name="ownerSid"/> names the profile a per-user program belongs to when that is not
    /// the signed-in person's: its leftovers are there, and the signed-in person's profile, with
    /// their own copy of it perhaps, is left alone.
    /// </summary>
    public static LeftoverScanContext ForThisMachine(bool includeAllProfiles, out UserProfiles.HiveMounts mounts, string? ownerSid = null)
    {
        string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Folder(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Folder(Environment.SpecialFolder.ProgramFilesX86);
        var programData = Folder(Environment.SpecialFolder.CommonApplicationData);

        var machine = new ProfileScope
        {
            DataFolders =
            [
                programFiles, programFilesX86,
                Path.Combine(programFiles, "Common Files"), Path.Combine(programFilesX86, "Common Files"),
                programData
            ],
            ShortcutFolders = [Folder(Environment.SpecialFolder.CommonPrograms), Folder(Environment.SpecialFolder.CommonDesktopDirectory)],
            TempFolders = [Path.Combine(Folder(Environment.SpecialFolder.Windows), "Temp")],
            SoftwareKeys = [RegistryLocation.Machine("SOFTWARE"), RegistryLocation.Machine(@"SOFTWARE\WOW6432Node")],
            RunKeys =
            [
                RegistryLocation.Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                RegistryLocation.Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                RegistryLocation.Machine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
                RegistryLocation.Machine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce")
            ],
            ClassesKeys = [RegistryLocation.Machine(@"SOFTWARE\Classes"), RegistryLocation.Machine(@"SOFTWARE\Classes\WOW6432Node")],
            EnvironmentKey = LeftoverSafety.MachineEnvironmentKey,
            TraceKeys = LeftoverSafety.MachineTraceKeyPaths.Select(RegistryLocation.Machine).ToList()
        };

        var all = UserProfiles.List();
        var chosen = ChooseProfiles(all, includeAllProfiles && ElevationValidation.IsCurrentProcessElevated(), ownerSid);

        mounts = UserProfiles.MountMissing(chosen);

        var profiles = mounts.Profiles.Select(ForProfile).ToList();

        return new LeftoverScanContext
        {
            Machine = machine,
            Profiles = profiles,
            AppPathsKeys =
            [
                RegistryLocation.Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
                RegistryLocation.Machine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
                .. mounts.Profiles.Where(p => p.Hive is not null)
                    .Select(p => p.Hive!.Child(@"Software\Microsoft\Windows\CurrentVersion\App Paths"))
            ],
            ServicesKey = RegistryLocation.Machine(@"SYSTEM\CurrentControlSet\Services"),
            EventLogKey = RegistryLocation.Machine(@"SYSTEM\CurrentControlSet\Services\EventLog"),
            SharedDllsKeys =
            [
                RegistryLocation.Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs"),
                RegistryLocation.Machine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs")
            ],
            Firewall = WindowsFirewall.Instance,
            IncludeScheduledTasks = true,
            ProfileRoots = all.Select(p => p.ProfilePath).ToList()
        };
    }

    /// <summary>
    /// Which profiles to search: all of them when asked; else the owner's, for a per-user program
    /// of another account; else the signed-in person's. The first is the one the program's own
    /// entry is attributed to.
    /// </summary>
    internal static IReadOnlyList<UserProfile> ChooseProfiles(IReadOnlyList<UserProfile> all, bool everyProfile, string? ownerSid)
    {
        var owner = ownerSid is null ? null : all.FirstOrDefault(p => p.Sid.Equals(ownerSid, StringComparison.OrdinalIgnoreCase));

        if (everyProfile)
            return owner is null ? all : all.OrderByDescending(p => p == owner).ToList();

        return owner is not null ? [owner] : all.Where(p => p.IsCurrent).ToList();
    }

    private static ProfileScope ForProfile(UserProfile profile)
    {
        var software = profile.Hive?.Child("Software");

        // HKCU\Software\Classes already is the per-user classes hive for this process's own
        // account; anyone else's is reached through HKU\<sid>_Classes when it is loaded.
        RegistryLocation? classes = software is null
            ? null
            : profile.Hive!.Hive == Microsoft.Win32.RegistryHive.CurrentUser || profile.ClassesHive is null
                ? software.Child("Classes")
                : profile.ClassesHive;

        // The trace keys are listed as HKCU paths; the MuiCache one lives in the classes hive.
        var traces = new List<RegistryLocation>();
        foreach (var path in LeftoverSafety.UserTraceKeyPaths)
        {
            const string classesPrefix = @"Software\Classes\";

            if (path.StartsWith(classesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (classes is not null) traces.Add(classes.Child(path[classesPrefix.Length..]));
            }
            else if (profile.Hive is { } hive)
            {
                traces.Add(hive.Child(path));
            }
        }

        return new ProfileScope
        {
            Owner = profile.AccountName,
            ProfileRoot = profile.ProfilePath,
            DataFolders =
            [
                profile.RoamingAppData, profile.LocalAppData, profile.LocalLowAppData,
                Path.Combine(profile.LocalAppData, "Programs")
            ],
            ShortcutFolders =
            [
                profile.StartMenuPrograms,
                ShellFolder(profile, "Desktop") ?? Path.Combine(profile.ProfilePath, "Desktop"),
                Path.Combine(profile.RoamingAppData, "Microsoft", "Internet Explorer", "Quick Launch")
            ],
            TempFolders = [Path.Combine(profile.LocalAppData, "Temp")],
            DocumentFolders = [ShellFolder(profile, "Personal") ?? Path.Combine(profile.ProfilePath, "Documents")],
            SoftwareKeys = software is null ? [] : [software, software.Child("WOW6432Node")],
            RunKeys = software is null
                ? []
                : [software.Child(@"Microsoft\Windows\CurrentVersion\Run"), software.Child(@"Microsoft\Windows\CurrentVersion\RunOnce")],
            ClassesKeys = classes is null ? [] : [classes],
            EnvironmentKey = profile.Hive?.Child("Environment"),
            TraceKeys = traces
        };
    }

    /// <summary>
    /// A known folder as the account has it set, which may be redirected to OneDrive. Read from
    /// the account's own hive, because this process may be running as someone else.
    /// </summary>
    private static string? ShellFolder(UserProfile profile, string name)
    {
        using var key = profile.Hive?.Child(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders").Open();
        if (key?.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw) return null;

        var expanded = raw.Replace("%USERPROFILE%", profile.ProfilePath, StringComparison.OrdinalIgnoreCase);
        return PathRules.Normalize(expanded);
    }
}
