using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace Uproot.Core;

/// <summary>
/// Reads the installed-programs list from the three places Windows keeps it: the 64-bit and
/// 32-bit machine Uninstall keys, and the current account's own.
/// </summary>
public static class ProgramInventory
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Uninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Everything registered, including the entries Apps &amp; Features hides. The leftover
    /// scanner needs those: a hidden MSI component still owns its install folder, and treating
    /// that folder as unclaimed would offer it for deletion.
    /// </summary>
    public static IReadOnlyList<InstalledProgram> ReadAll() => ReadAll(DefaultRoots()).Select(ProgramOwner.Tag).ToList();

    /// <summary>What Apps &amp; Features would list: no system components, no updates.</summary>
    public static IReadOnlyList<InstalledProgram> ReadVisible() =>
        ReadAll().Where(p => !p.IsSystemComponent && !p.IsUpdateOrComponent).ToList();

    public static IReadOnlyList<InstalledProgram> ReadAll(IEnumerable<(RegistryLocation Root, ProgramScope Scope, bool Is32Bit)> roots)
    {
        var programs = new List<InstalledProgram>();

        foreach (var (root, scope, is32Bit) in roots)
        {
            using var key = root.Open();
            if (key is null) continue;

            string[] names;
            try { names = key.GetSubKeyNames(); }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var name in names)
            {
                if (Read(root.Child(name), scope, is32Bit) is { } program) programs.Add(program);
            }
        }

        return programs
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static IEnumerable<(RegistryLocation Root, ProgramScope Scope, bool Is32Bit)> DefaultRoots()
    {
        yield return (RegistryLocation.Machine(UninstallPath), ProgramScope.Machine, false);
        yield return (RegistryLocation.Machine(Uninstall32Path), ProgramScope.Machine, true);

        // HKCU\Software is shared between the two views, so one read covers per-user entries
        // written by 32-bit and 64-bit installers alike.
        yield return (RegistryLocation.CurrentUser(UninstallPath), ProgramScope.User, false);

        // Elevated with someone else's administrator credentials, HKCU is that administrator's.
        // The person at the keyboard still expects to see their own programs.
        var interactive = Elevation.ElevationValidation.InteractiveUserSid();
        var process = Elevation.ElevationValidation.CurrentUserSid();

        if (interactive is not null && !interactive.Equals(process, StringComparison.OrdinalIgnoreCase))
            yield return (RegistryLocation.Users($@"{interactive}\{UninstallPath}"), ProgramScope.User, false);
    }

    /// <summary>Reads one Uninstall subkey. Null when it has no display name, as Windows treats it.</summary>
    public static InstalledProgram? Read(RegistryLocation location, ProgramScope scope, bool is32Bit = false)
    {
        try
        {
            using var key = location.Open();
            if (key?.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
                return null;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is string text && !string.IsNullOrWhiteSpace(text))
                    values[valueName] = text.Trim();
            }

            var releaseType = Text(values, "ReleaseType");

            return new InstalledProgram
            {
                Key = location,
                Scope = scope,
                Is32Bit = is32Bit,
                DisplayName = displayName.Trim(),
                DisplayVersion = Text(values, "DisplayVersion"),
                Publisher = Text(values, "Publisher"),
                InstallLocation = Text(values, "InstallLocation"),
                InstallSource = Text(values, "InstallSource"),
                DisplayIcon = Text(values, "DisplayIcon"),
                UninstallString = Text(values, "UninstallString"),
                QuietUninstallString = Text(values, "QuietUninstallString"),
                ModifyPath = Text(values, "ModifyPath"),
                InstallDate = ParseDate(Text(values, "InstallDate")),
                EstimatedSizeKb = Number(key.GetValue("EstimatedSize")),
                IsWindowsInstaller = Number(key.GetValue("WindowsInstaller")) == 1,
                IsSystemComponent = Number(key.GetValue("SystemComponent")) == 1,
                IsUpdateOrComponent = Text(values, "ParentKeyName") is not null
                                      || releaseType is "Update" or "Hotfix" or "Security Update"
                                          or "Update Rollup" or "Service Pack",
                Values = values
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds programs for a command-line query: an exact registry path or key name first, then an
    /// exact display name, then a name containing the text. Ambiguity is the caller's to report.
    /// </summary>
    public static IReadOnlyList<InstalledProgram> Find(IReadOnlyList<InstalledProgram> programs, string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return [];

        var byKey = programs
            .Where(p => p.Id.Equals(needle, StringComparison.OrdinalIgnoreCase)
                        || p.KeyName.Equals(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byKey.Count > 0) return byKey;

        var exact = programs
            .Where(p => p.DisplayName.Equals(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0) return exact;

        return programs
            .Where(p => p.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string? Text(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    private static long? Number(object? value) => value switch
    {
        int i => i,
        long l => l,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
