namespace Uproot.Core;

/// <summary>Path comparisons that treat "C:\Foo" and "C:\Foo\" as the same folder, and "C:\FooBar" as a different one.</summary>
public static class PathRules
{
    /// <summary>
    /// A full, trimmed path with environment variables expanded, device prefixes and 8.3 short
    /// names resolved, and no trailing separator. Null for anything that is not an absolute path.
    ///
    /// Every containment check goes through this, so every spelling of one place has to come out
    /// the same: "\\?\C:\Windows" and "C:\WINDOW~1" left as they are would sail past a rule that
    /// protects C:\Windows.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var expanded = StripDevicePrefix(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim()));
            if (expanded.Length == 0 || !Path.IsPathFullyQualified(expanded)) return null;

            var full = LongName(Path.GetFullPath(expanded));
            var root = Path.GetPathRoot(full);

            // Keep a drive root recognisable as one: "C:\" rather than "C:".
            return root is not null && full.Length <= root.Length
                ? root
                : Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>True for a UNC or other network path, which a leftover never legitimately is.</summary>
    public static bool IsNetworkPath(string? path) =>
        Normalize(path) is { } normalized && normalized.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>"\\?\C:\x", "\??\C:\x" and "\\.\C:\x" all mean C:\x; "\\?\UNC\server\share" means \\server\share.</summary>
    private static string StripDevicePrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        return path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string shortPath, System.Text.StringBuilder longPath, uint length);

    /// <summary>
    /// Expands 8.3 short names, so "C:\PROGRA~1\Foo" and "C:\Program Files\Foo" are compared as
    /// the same place. Only a path that exists can be expanded; the rest is returned as it was,
    /// with its existing prefix expanded where possible.
    /// </summary>
    private static string LongName(string path)
    {
        if (!path.Contains('~')) return path;

        var buffer = new System.Text.StringBuilder(1024);
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length > 0 && length < buffer.Capacity) return buffer.ToString();

        // The file itself may be gone; expand the deepest part that still exists.
        var parent = Path.GetDirectoryName(path);
        return parent is null || parent == path ? path : Path.Combine(LongName(parent), Path.GetFileName(path));
    }

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or anything inside it.</summary>
    public static bool IsWithin(string? path, string? root)
    {
        if (path is null || root is null) return false;

        var p = Normalize(path);
        var r = Normalize(root);
        if (p is null || r is null) return false;

        if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;

        var prefix = r.EndsWith('\\') ? r : r + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/> but not the folder itself.</summary>
    public static bool IsStrictlyWithin(string? path, string? root) =>
        IsWithin(path, root) && !SamePath(path, root);

    /// <summary>
    /// True when a command line, service image path or registry value mentions a path inside
    /// <paramref name="folder"/>.
    ///
    /// The match has to be followed by a separator, a closing quote or a list separator. A space
    /// does not count: folder names contain spaces, and "C:\Program Files\Microsoft VS Code" is the
    /// start of "C:\Program Files\Microsoft VS Code Insiders\Code - Insiders.exe" — a different
    /// program entirely.
    /// </summary>
    public static bool References(string? text, string? folder)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(folder)) return false;

        var start = 0;
        while (true)
        {
            var index = text.IndexOf(folder, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;

            var end = index + folder.Length;
            if (end >= text.Length || text[end] is '\\' or '"' or ';') return true;

            start = index + 1;
        }
    }

    /// <summary>The executable a command line starts, quoted or not. Null when there is none.</summary>
    public static string? CommandExecutable(string? command) =>
        UninstallCommand.Parse(command)?.FileName is { } file && Normalize(file) is { } full ? full : null;

    /// <summary>
    /// The file a service's ImagePath runs, in the forms the service manager accepts: quoted or
    /// not, with arguments, "\??\"-prefixed, "\SystemRoot\"-rooted, or relative to the Windows
    /// folder ("system32\drivers\x.sys").
    /// </summary>
    public static string? ServiceImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var text = imagePath.Trim();

        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            text = Path.Combine(windows, text[@"\SystemRoot\".Length..]);
        else if (text.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)
                 || text.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            text = Path.Combine(windows, text);

        var file = UninstallCommand.Parse(StripDevicePrefix(text))?.FileName;
        return Normalize(file);
    }
}
