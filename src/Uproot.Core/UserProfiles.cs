using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using Uproot.Core.Elevation;

namespace Uproot.Core;

/// <summary>An account's profile on this machine: its folder, and where its registry hive can be reached.</summary>
public sealed record UserProfile
{
    public required string Sid { get; init; }
    public required string ProfilePath { get; init; }
    public required string AccountName { get; init; }

    /// <summary>The person signed in at the keyboard, which is not always the account this process runs as.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>
    /// The hive, as a root to build registry locations on: HKCU for this process's own account,
    /// HKU\&lt;sid&gt; for a signed-in account, and HKU\&lt;mount&gt; once loaded from NTUSER.DAT.
    /// Null when the hive is not loaded.
    /// </summary>
    public RegistryLocation? Hive { get; init; }

    /// <summary>Where the account's per-user classes are, when loaded.</summary>
    public RegistryLocation? ClassesHive { get; init; }

    public string RoamingAppData => Path.Combine(ProfilePath, "AppData", "Roaming");
    public string LocalAppData => Path.Combine(ProfilePath, "AppData", "Local");
    public string LocalLowAppData => Path.Combine(ProfilePath, "AppData", "LocalLow");
    public string StartMenuPrograms => Path.Combine(RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs");

    public override string ToString() => AccountName;
}

/// <summary>
/// Finds the profiles on this machine and gives the scanner access to their registry hives.
///
/// A signed-in account's hive is already loaded under HKEY_USERS. Anyone else's lives in their
/// NTUSER.DAT and has to be loaded to be read, which needs administrator rights and the backup and
/// restore privileges. A loaded hive is unloaded again as soon as the work is done: a user who
/// signs in while their hive is held by another process gets a temporary profile.
/// </summary>
public static class UserProfiles
{
    private const string ProfileListPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>The prefix for hives this app loads, so they are recognisable in HKEY_USERS.</summary>
    public const string MountPrefix = "Uproot_";

    /// <summary>
    /// The prefix WGUpdater used. A removal it made from a loaded hive names keys under it, so
    /// restoring one loads the hive under that name again.
    /// </summary>
    public const string LegacyMountPrefix = "WGU_";

    /// <summary>True for "Uproot_&lt;sid&gt;" or WGUpdater's "WGU_&lt;sid&gt;": a hive loaded by name, not a signed-in one.</summary>
    public static bool IsMountName(string name) =>
        name.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(LegacyMountPrefix, StringComparison.OrdinalIgnoreCase);

    private static string SidOfMount(string name) =>
        name.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase) ? name[MountPrefix.Length..] : name[LegacyMountPrefix.Length..];

    /// <summary>
    /// Real people's profiles: local accounts (S-1-5-21-…) and Microsoft Entra ones (S-1-12-1-…).
    /// Service accounts and the default profile are left out.
    /// </summary>
    public static IReadOnlyList<UserProfile> List()
    {
        var current = ElevationValidation.InteractiveUserSid() ?? ElevationValidation.CurrentUserSid();
        var processSid = ElevationValidation.CurrentUserSid();
        var profiles = new List<UserProfile>();

        using var list = Registry.LocalMachine.OpenSubKey(ProfileListPath);
        if (list is null) return profiles;

        foreach (var sid in list.GetSubKeyNames())
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                && !sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A ".bak" key is a profile Windows could not load and set aside.
            if (sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;

            string? path;
            try
            {
                using var entry = list.OpenSubKey(sid);
                path = entry?.GetValue("ProfileImagePath") as string;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            path = PathRules.Normalize(path);
            if (path is null || !Directory.Exists(path)) continue;

            var isProcessUser = string.Equals(sid, processSid, StringComparison.OrdinalIgnoreCase);

            profiles.Add(new UserProfile
            {
                Sid = sid,
                ProfilePath = path,
                AccountName = AccountName(sid) ?? Path.GetFileName(path),
                IsCurrent = string.Equals(sid, current, StringComparison.OrdinalIgnoreCase),
                Hive = isProcessUser
                    ? RegistryLocation.CurrentUser(string.Empty)
                    : IsLoaded(sid) ? RegistryLocation.Users(sid) : null,
                ClassesHive = IsLoaded(sid + "_Classes") ? RegistryLocation.Users(sid + "_Classes") : null
            });
        }

        return profiles
            .OrderByDescending(p => p.IsCurrent)
            .ThenBy(p => p.AccountName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The profile of the person at the keyboard.</summary>
    public static UserProfile? Current() => List().FirstOrDefault(p => p.IsCurrent);

    /// <summary>
    /// The account's own folders wherever they have been moved to — Documents, Desktop, Pictures
    /// and the rest of its User Shell Folders, often redirected into OneDrive — and its OneDrive
    /// folders themselves. Read from the account's hive when it is loaded; for the account this
    /// process runs as, from Windows directly as well.
    /// </summary>
    public static IReadOnlyList<string> KnownFolders(UserProfile profile)
    {
        var folders = new List<string>();

        void Add(string? path)
        {
            if (PathRules.Normalize(path) is { } normalized && !folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                folders.Add(normalized);
        }

        string Expand(string raw) => Environment.ExpandEnvironmentVariables(
            raw.Replace("%USERPROFILE%", profile.ProfilePath, StringComparison.OrdinalIgnoreCase));

        if (profile.Hive is { } hive)
        {
            try
            {
                using (var shell = hive.Child(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders").Open())
                {
                    foreach (var name in shell?.GetValueNames() ?? [])
                    {
                        if (shell!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw) Add(Expand(raw));
                    }
                }

                var accounts = hive.Child(@"Software\Microsoft\OneDrive\Accounts");
                using var accountsKey = accounts.Open();
                foreach (var account in accountsKey?.GetSubKeyNames() ?? [])
                {
                    using var key = accounts.Child(account).Open();
                    if (key?.GetValue("UserFolder") is string userFolder) Add(userFolder);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        if (profile.Hive?.Hive == RegistryHive.CurrentUser)
        {
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures,
                         Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Favorites
                     })
            {
                Add(Environment.GetFolderPath(folder));
            }

            foreach (var variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
                Add(Environment.GetEnvironmentVariable(variable));
        }

        return folders;
    }

    private static bool IsLoaded(string hiveName)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey(hiveName);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string? AccountName(string sid)
    {
        try
        {
            var account = (NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount));
            var name = account.Value;
            var slash = name.IndexOf('\\');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException)
        {
            return null;
        }
    }

    // ---- Loading hives -----------------------------------------------------

    /// <summary>
    /// Loads the hives of profiles that are not signed in, for as long as the returned object is
    /// alive. Profiles that cannot be loaded are returned unchanged, still without a hive.
    /// </summary>
    public static HiveMounts MountMissing(IEnumerable<UserProfile> profiles)
    {
        var mounts = new HiveMounts();

        foreach (var profile in profiles)
        {
            if (profile.Hive is not null)
            {
                mounts.Profiles.Add(profile);
                continue;
            }

            var mountName = MountPrefix + profile.Sid;

            // Left behind by a run that crashed before it could unload: reuse it, and take
            // ownership so it is unloaded this time — while it stays mounted, that person gets a
            // temporary profile when they sign in.
            if (IsLoaded(mountName))
            {
                mounts.Adopt(mountName);
                mounts.Profiles.Add(profile with { Hive = RegistryLocation.Users(mountName) });
                continue;
            }

            var hiveFile = Path.Combine(profile.ProfilePath, "NTUSER.DAT");

            if (ElevationValidation.IsCurrentProcessElevated() && File.Exists(hiveFile) && mounts.TryLoad(mountName, hiveFile))
            {
                mounts.Profiles.Add(profile with { Hive = RegistryLocation.Users(mountName) });
            }
            else
            {
                mounts.Profiles.Add(profile);
            }
        }

        return mounts;
    }

    /// <summary>Loads the named hives again, for removing items a scan found in them.</summary>
    public static HiveMounts MountByName(IEnumerable<string> mountNames)
    {
        var mounts = new HiveMounts();
        var profiles = List();

        foreach (var mountName in mountNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsMountName(mountName)) continue;

            // Only this app (and WGUpdater before it) uses the prefix, so one already mounted is
            // ours to unload afterwards.
            if (IsLoaded(mountName))
            {
                mounts.Adopt(mountName);
                continue;
            }

            var sid = SidOfMount(mountName);
            var profile = profiles.FirstOrDefault(p => p.Sid.Equals(sid, StringComparison.OrdinalIgnoreCase));
            if (profile is null) continue;

            var hiveFile = Path.Combine(profile.ProfilePath, "NTUSER.DAT");
            if (File.Exists(hiveFile)) mounts.TryLoad(mountName, hiveFile);
        }

        return mounts;
    }

    public sealed class HiveMounts : IDisposable
    {
        private readonly List<string> _loaded = [];

        public List<UserProfile> Profiles { get; } = [];

        public IReadOnlyList<string> Loaded => _loaded;

        /// <summary>Takes responsibility for unloading a hive something else left mounted.</summary>
        internal void Adopt(string mountName)
        {
            if (ElevationValidation.IsCurrentProcessElevated() && !_loaded.Contains(mountName, StringComparer.OrdinalIgnoreCase))
                _loaded.Add(mountName);
        }

        internal bool TryLoad(string mountName, string hiveFile)
        {
            if (!EnablePrivilege("SeBackupPrivilege") || !EnablePrivilege("SeRestorePrivilege")) return false;

            var result = RegLoadKeyW(HkeyUsers, mountName, hiveFile);
            if (result != 0) return false;

            _loaded.Add(mountName);
            return true;
        }

        public void Dispose()
        {
            foreach (var mountName in _loaded)
            {
                // Handles into the hive must all be closed before it will unload. Finalisers are
                // what release any a caller forgot, so give them a chance before retrying.
                for (var attempt = 0; attempt < 5 && IsLoaded(mountName); attempt++)
                {
                    if (RegUnLoadKeyW(HkeyUsers, mountName) == 0) break;

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(200);
                }
            }

            _loaded.Clear();
        }
    }

    // ---- Native ------------------------------------------------------------

    private static readonly IntPtr HkeyUsers = unchecked((IntPtr)(int)0x80000003);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadKeyW(IntPtr hKey, string subKey, string file);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegUnLoadKeyW(IntPtr hKey, string subKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    private static bool EnablePrivilege(string name)
    {
        const uint adjust = 0x20, query = 0x8;

        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, adjust | query, out var token)) return false;

        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return false;

            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) return false;

            // AdjustTokenPrivileges "succeeds" without granting anything the token lacks.
            return Marshal.GetLastWin32Error() == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
