using System.Security;
using Microsoft.Win32;

namespace Uproot.Core;

/// <summary>
/// A registry key named by hive and literal path.
///
/// Everything is opened through the 64-bit view, with 32-bit locations spelled out as literal
/// WOW6432Node segments rather than reached through <see cref="RegistryView.Registry32"/>. That
/// way redirection never happens behind the scanner's back: the path a leftover is shown with is
/// exactly the key that gets exported and deleted.
/// </summary>
public sealed record RegistryLocation(RegistryHive Hive, string SubKey)
{
    public static RegistryLocation Machine(string subKey) => new(RegistryHive.LocalMachine, Trim(subKey));
    public static RegistryLocation CurrentUser(string subKey) => new(RegistryHive.CurrentUser, Trim(subKey));
    public static RegistryLocation Users(string subKey) => new(RegistryHive.Users, Trim(subKey));

    public string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => Hive.ToString()
    };

    /// <summary>The path as a person reads it, and as reg.exe accepts it.</summary>
    public string DisplayPath => SubKey.Length == 0 ? HiveName : $@"{HiveName}\{SubKey}";

    public string LeafName
    {
        get
        {
            var slash = SubKey.LastIndexOf('\\');
            return slash < 0 ? SubKey : SubKey[(slash + 1)..];
        }
    }

    public RegistryLocation? Parent
    {
        get
        {
            if (SubKey.Length == 0) return null;
            var slash = SubKey.LastIndexOf('\\');
            return new RegistryLocation(Hive, slash < 0 ? string.Empty : SubKey[..slash]);
        }
    }

    public int Depth => SubKey.Length == 0 ? 0 : SubKey.Count(c => c == '\\') + 1;

    public RegistryLocation Child(string name) =>
        new(Hive, SubKey.Length == 0 ? name : $@"{SubKey}\{name}");

    /// <summary>True when this key is the same as, or sits underneath, <paramref name="other"/>.</summary>
    public bool IsWithin(RegistryLocation other) =>
        Hive == other.Hive
        && (other.SubKey.Length == 0
            || SubKey.Equals(other.SubKey, StringComparison.OrdinalIgnoreCase)
            || SubKey.StartsWith(other.SubKey + @"\", StringComparison.OrdinalIgnoreCase));

    public RegistryKey? Open(bool writable = false)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
            return SubKey.Length == 0
                ? RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64)
                : root.OpenSubKey(SubKey, writable);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    public bool Exists()
    {
        using var key = Open();
        return key is not null;
    }

    public bool Equals(RegistryLocation? other) =>
        other is not null
        && Hive == other.Hive
        && string.Equals(SubKey, other.SubKey, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(Hive, StringComparer.OrdinalIgnoreCase.GetHashCode(SubKey));

    public override string ToString() => DisplayPath;

    /// <summary>
    /// Parses "HKLM\SOFTWARE\X", "HKEY_CURRENT_USER\Software\X" and the like. Null when the hive
    /// is not one this app works with.
    /// </summary>
    public static RegistryLocation? Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var trimmed = path.Trim().Trim('\\');
        var slash = trimmed.IndexOf('\\');
        var hiveName = slash < 0 ? trimmed : trimmed[..slash];
        var rest = slash < 0 ? string.Empty : trimmed[(slash + 1)..];

        RegistryHive? hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => null
        };

        return hive is { } h ? new RegistryLocation(h, Trim(rest)) : null;
    }

    private static string Trim(string subKey) => subKey.Trim().Trim('\\');

    // ---- WOW64 -----------------------------------------------------------

    /// <summary>
    /// Subkeys of Software\Classes that WOW64 still redirects for 32-bit code. The rest of
    /// Classes has been shared between the two views since Windows 7.
    /// </summary>
    private static readonly HashSet<string> RedirectedClassesKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "DirectShow", "Interface", "Media Type", "MediaFoundation"
    };

    /// <summary>Keys directly under HKLM\SOFTWARE that 32-bit code sees unredirected.</summary>
    private static readonly HashSet<string> SharedSoftwareKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Classes", "Policies", "RegisteredApplications", "WOW6432Node", "Clients"
    };

    /// <summary>
    /// Where a key written by 32-bit code actually lives, spelled with WOW6432Node. A 32-bit
    /// MSI component that writes HKLM\SOFTWARE\Adobe lands in HKLM\SOFTWARE\WOW6432Node\Adobe,
    /// and the leftover has to be looked for there.
    /// </summary>
    public RegistryLocation As32BitWrite()
    {
        var parts = SubKey.Split('\\');
        if (parts.Length < 2) return this;

        var softwareIndex = Hive == RegistryHive.Users ? 1 : 0;
        if (parts.Length <= softwareIndex + 1 ||
            !parts[softwareIndex].Equals("Software", StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        var next = parts[softwareIndex + 1];

        if (next.Equals("Classes", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > softwareIndex + 2 && RedirectedClassesKeys.Contains(parts[softwareIndex + 2]))
                return Insert(parts, softwareIndex + 2, "WOW6432Node");

            return this;
        }

        // HKCU\Software is shared apart from Classes; only the machine hive redirects wholesale.
        if (Hive != RegistryHive.LocalMachine) return this;
        if (SharedSoftwareKeys.Contains(next)) return this;

        return Insert(parts, softwareIndex + 1, "WOW6432Node");
    }

    private RegistryLocation Insert(string[] parts, int index, string segment)
    {
        var list = parts.ToList();
        list.Insert(index, segment);
        return this with { SubKey = string.Join('\\', list) };
    }
}
