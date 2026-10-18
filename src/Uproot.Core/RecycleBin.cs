using System.Text;

namespace Uproot.Core;

/// <summary>Something in the Recycle Bin: where it came from, when it went, and where it is now.</summary>
public sealed record RecycledItem(string OriginalPath, DateTimeOffset DeletedAt, string DataPath, string InfoPath)
{
    public bool IsFolder => Directory.Exists(DataPath);
}

/// <summary>
/// Finds items in an account's Recycle Bin by where they came from, and puts them back.
///
/// Each drive has its own bin, $Recycle.Bin\&lt;SID&gt;, holding every deleted item as a pair: $R…
/// is the item itself and $I… its record, which says where it came from and when it was
/// deleted. Reading the records directly finds an item by its full original path whatever the
/// display language or Explorer's settings, which the shell's own interface cannot promise.
/// </summary>
public static class RecycleBin
{
    /// <summary>Items that came from <paramref name="originalPath"/>, most recently deleted first.</summary>
    public static IReadOnlyList<RecycledItem> FindDeletedFrom(string originalPath, string sid)
    {
        var normalized = PathRules.Normalize(originalPath);
        if (normalized is null || Path.GetPathRoot(normalized) is not { Length: > 0 } root) return [];

        var bin = Path.Combine(root, "$Recycle.Bin", sid);
        if (!Directory.Exists(bin)) return [];

        var found = new List<RecycledItem>();

        try
        {
            foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
            {
                if (ReadRecord(info) is not { } record) continue;
                if (!PathRules.SamePath(record.OriginalPath, normalized)) continue;

                var data = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
                if (File.Exists(data) || Directory.Exists(data)) found.Add(record with { DataPath = data });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return found.OrderByDescending(f => f.DeletedAt).ToList();
    }

    /// <summary>
    /// Reads a $I record. Version 1 (Windows Vista to 8) holds a fixed 260-character path;
    /// version 2 (Windows 10 onwards) a length-prefixed one.
    /// </summary>
    public static RecycledItem? ReadRecord(string infoPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoPath);
            if (bytes.Length < 24) return null;

            var version = BitConverter.ToInt64(bytes, 0);
            var deleted = DateTimeOffset.FromFileTime(BitConverter.ToInt64(bytes, 16));

            string path;
            if (version == 1)
            {
                path = Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24));
            }
            else if (version == 2 && bytes.Length >= 28)
            {
                var chars = BitConverter.ToInt32(bytes, 24);
                if (chars <= 0 || 28 + chars * 2L > bytes.Length) return null;
                path = Encoding.Unicode.GetString(bytes, 28, chars * 2);
            }
            else
            {
                return null;
            }

            var end = path.IndexOf('\0');
            if (end >= 0) path = path[..end];

            return path.Length == 0 ? null : new RecycledItem(path, deleted, string.Empty, infoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves an item back where it came from. Refuses to overwrite anything already there, and
    /// removes the record only once the item itself is back.
    /// </summary>
    public static void Restore(RecycledItem item)
    {
        if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            throw new IOException($"Something is already at {item.OriginalPath}.");

        if (Path.GetDirectoryName(item.OriginalPath) is { } parent) Directory.CreateDirectory(parent);

        if (Directory.Exists(item.DataPath)) Directory.Move(item.DataPath, item.OriginalPath);
        else File.Move(item.DataPath, item.OriginalPath);

        try { File.Delete(item.InfoPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
