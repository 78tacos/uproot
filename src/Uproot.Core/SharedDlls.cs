using System.Security;
using Microsoft.Win32;

namespace Uproot.Core;

/// <summary>
/// The legacy shared-file reference counts under CurrentVersion\SharedDLLs.
///
/// Before Windows Installer tracked components itself, installers counted shared files here, and
/// many still do. Windows Installer honours the count: a file whose count is still above zero after
/// a product is removed is kept, because some other program — MSI or not — still uses it. The
/// leftover scan has to honour it too, or it offers exactly the files Windows kept on purpose.
/// </summary>
public static class SharedDlls
{
    private static readonly string[] Keys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs"
    ];

    /// <summary>Every counted path and its count, from both registry views.</summary>
    public static IReadOnlyDictionary<string, int> Read()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Keys)
        {
            using var key = RegistryLocation.Machine(path).Open();
            if (key is null) continue;

            try
            {
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not int count || PathRules.Normalize(name) is not { } file) continue;
                    counts[file] = Math.Max(count, counts.GetValueOrDefault(file));
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return counts;
    }

    /// <summary>
    /// True when a file is counted as used by more programs than <paramref name="ownUses"/> —
    /// one while the program being removed still holds its own reference, none once it is gone.
    /// </summary>
    public static bool IsInUseByOthers(IReadOnlyDictionary<string, int> counts, string file, int ownUses) =>
        PathRules.Normalize(file) is { } normalized && counts.TryGetValue(normalized, out var count) && count > ownUses;
}
