using System.Text;

namespace Uproot.Core;

/// <summary>
/// Reads the target path out of a .lnk file.
///
/// This parses the Shell Link format directly rather than going through WScript.Shell, because
/// the scanner reads hundreds of shortcuts and Windows Script Host is commonly disabled by policy
/// on managed machines — the very machines an uninstaller like this gets pointed at. Only the
/// fields that carry a filesystem path are read.
/// </summary>
public static class ShortcutFile
{
    private const int HasLinkTargetIdList = 0x1;
    private const int HasLinkInfo = 0x2;
    private const int HasName = 0x4;
    private const int HasRelativePath = 0x8;
    private const int HasWorkingDir = 0x10;
    private const int HasArguments = 0x20;
    private const int HasIconLocation = 0x40;
    private const int IsUnicode = 0x80;

    private const uint EnvironmentBlockSignature = 0xA0000001;
    private const uint DarwinBlockSignature = 0xA0000006;

    /// <summary>
    /// The path a shortcut points at, or null when it has none: an MSI advertised shortcut, a
    /// shell namespace item, or a file that is not a shortcut at all.
    /// </summary>
    public static string? ReadTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 0x4C || info.Length > 1024 * 1024) return null;

            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? Parse(byte[] data)
    {
        try
        {
            if (data.Length < 0x4C || BitConverter.ToUInt32(data, 0) != 0x4C) return null;

            var flags = BitConverter.ToInt32(data, 0x14);
            var offset = 0x4C;

            if ((flags & HasLinkTargetIdList) != 0)
                offset += 2 + BitConverter.ToUInt16(data, offset);

            string? target = null;

            if ((flags & HasLinkInfo) != 0)
            {
                var infoStart = offset;
                var infoSize = BitConverter.ToInt32(data, infoStart);
                var headerSize = BitConverter.ToInt32(data, infoStart + 4);
                var infoFlags = BitConverter.ToInt32(data, infoStart + 8);

                // VolumeIDAndLocalBasePath: the target lives on a local volume.
                if ((infoFlags & 1) != 0)
                {
                    if (headerSize >= 0x24)
                    {
                        var unicodeOffset = BitConverter.ToInt32(data, infoStart + 0x1C);
                        if (unicodeOffset > 0) target = ReadUnicodeZ(data, infoStart + unicodeOffset);
                    }

                    if (string.IsNullOrEmpty(target))
                    {
                        var ansiOffset = BitConverter.ToInt32(data, infoStart + 0x10);
                        target = ReadAnsiZ(data, infoStart + ansiOffset);
                    }
                }

                offset = infoStart + infoSize;
            }

            var unicode = (flags & IsUnicode) != 0;
            string? relativePath = null;
            string? workingDir = null;

            foreach (var (flag, index) in new[] { (HasName, 0), (HasRelativePath, 1), (HasWorkingDir, 2), (HasArguments, 3), (HasIconLocation, 4) })
            {
                if ((flags & flag) == 0) continue;

                var count = BitConverter.ToUInt16(data, offset);
                offset += 2;
                var byteCount = unicode ? count * 2 : count;
                var value = unicode
                    ? Encoding.Unicode.GetString(data, offset, byteCount)
                    : Encoding.Latin1.GetString(data, offset, byteCount);
                offset += byteCount;

                if (index == 1) relativePath = value;
                else if (index == 2) workingDir = value;
            }

            // Extra data blocks: an environment-variable target overrides a stale local path, and a
            // Darwin descriptor marks an MSI advertised shortcut with no real target.
            while (offset + 8 <= data.Length)
            {
                var size = BitConverter.ToInt32(data, offset);
                if (size < 8 || offset + size > data.Length) break;

                var signature = BitConverter.ToUInt32(data, offset + 4);

                if (signature == DarwinBlockSignature && string.IsNullOrEmpty(target)) return null;

                if (signature == EnvironmentBlockSignature && size >= 0x314)
                {
                    var expanded = ReadUnicodeZ(data, offset + 8 + 260, 520);
                    if (string.IsNullOrEmpty(expanded)) expanded = ReadAnsiZ(data, offset + 8, 260);
                    if (!string.IsNullOrEmpty(expanded)) target = Environment.ExpandEnvironmentVariables(expanded);
                }

                offset += size;
            }

            if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(relativePath) && !string.IsNullOrEmpty(workingDir))
            {
                try { target = Path.GetFullPath(Path.Combine(workingDir, relativePath)); }
                catch (ArgumentException) { }
            }

            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string ReadAnsiZ(byte[] data, int offset, int max = int.MaxValue)
    {
        var end = offset;
        while (end < data.Length && end - offset < max && data[end] != 0) end++;
        return Encoding.Latin1.GetString(data, offset, end - offset);
    }

    private static string ReadUnicodeZ(byte[] data, int offset, int maxBytes = int.MaxValue)
    {
        var end = offset;
        while (end + 1 < data.Length && end - offset < maxBytes && (data[end] != 0 || data[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(data, offset, end - offset);
    }
}
