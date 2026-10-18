using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Uproot.Core;

/// <summary>
/// Finds registry symbolic links, which an account can create in its own hive and which Windows
/// follows when a key is opened by path. Deleting a key tree or setting a value through one would
/// act wherever the link points rather than where the path says, so a key on the way to an item,
/// or anywhere inside a key being deleted, that is a link stops the removal or restore.
/// </summary>
public static class RegistryLinks
{
    private const int RegOptionOpenLink = 0x8;
    private const int KeyRead = 0x20019;
    private const int KeyWow6464Key = 0x100;
    private const int RegLink = 6;
    private const int ErrorSuccess = 0;
    private const int ErrorNoMoreItems = 259;

    /// <summary>A tree bigger than this is not checked to the end, and counts as containing a link.</summary>
    private const int MaxKeys = 50_000;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(IntPtr key, string subKey, int options, int access, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved, out int type, IntPtr data, ref int size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegEnumKeyExW(SafeRegistryHandle key, int index, StringBuilder name, ref int nameLength,
        IntPtr reserved, IntPtr className, IntPtr classLength, IntPtr lastWrite);

    private static IntPtr Root(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => unchecked((IntPtr)(int)0x80000002),
        RegistryHive.CurrentUser => unchecked((IntPtr)(int)0x80000001),
        RegistryHive.Users => unchecked((IntPtr)(int)0x80000003),
        _ => throw new ArgumentOutOfRangeException(nameof(hive))
    };

    /// <summary>
    /// True when any key from the hive root down to <paramref name="key"/> is a link, or, with
    /// <paramref name="includeSubtree"/>, any key beneath it. A key that does not exist is no link.
    /// </summary>
    public static bool HasLink(RegistryLocation key, bool includeSubtree)
    {
        var segments = key.SubKey.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var path = new StringBuilder();

        foreach (var segment in segments)
        {
            if (path.Length > 0) path.Append('\\');
            path.Append(segment);

            switch (IsLink(key.Hive, path.ToString()))
            {
                case true: return true;
                case null: return false;   // the rest of the path does not exist
            }
        }

        if (!includeSubtree) return false;

        var count = 0;
        return SubtreeHasLink(key.Hive, key.SubKey, ref count);
    }

    /// <summary>True for a link, false for an ordinary key, null when the key does not exist.</summary>
    private static bool? IsLink(RegistryHive hive, string subKey)
    {
        var result = RegOpenKeyExW(Root(hive), subKey, RegOptionOpenLink, KeyRead | KeyWow6464Key, out var handle);
        using (handle)
        {
            if (result != ErrorSuccess) return result == 2 ? null : true;   // not found, or unopenable: treat as a link
            var size = 0;
            return RegQueryValueExW(handle, "SymbolicLinkValue", IntPtr.Zero, out var type, IntPtr.Zero, ref size) == ErrorSuccess && type == RegLink;
        }
    }

    private static bool SubtreeHasLink(RegistryHive hive, string subKey, ref int count)
    {
        var result = RegOpenKeyExW(Root(hive), subKey, RegOptionOpenLink, KeyRead | KeyWow6464Key, out var handle);
        List<string> children = [];

        using (handle)
        {
            if (result != ErrorSuccess) return result != 2;

            var size = 0;
            if (RegQueryValueExW(handle, "SymbolicLinkValue", IntPtr.Zero, out var type, IntPtr.Zero, ref size) == ErrorSuccess && type == RegLink)
                return true;

            for (var index = 0; ; index++)
            {
                var name = new StringBuilder(256);
                var length = name.Capacity;
                var enumerated = RegEnumKeyExW(handle, index, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (enumerated == ErrorNoMoreItems) break;
                if (enumerated != ErrorSuccess) return true;

                children.Add(name.ToString());
                if (++count > MaxKeys) return true;
            }
        }

        foreach (var child in children)
        {
            if (SubtreeHasLink(hive, $@"{subKey}\{child}", ref count)) return true;
        }

        return false;
    }
}
