using System.Text;
using System.Text.RegularExpressions;

namespace Uproot.Core;

/// <summary>
/// What an Inno Setup uninstaller's log (unins000.dat) says was installed.
///
/// Inno Setup writes every file, folder and registry key it creates into this log, and its
/// uninstaller replays the log in reverse. The format is a private binary one, so rather than
/// decode every record type this pulls out the strings that are recognisably paths and registry
/// keys. That is enough for what the scanner needs — a list of places to check afterwards — and it
/// degrades to "nothing found" rather than to a wrong answer if the format changes.
///
/// Registry records keep their hive as a separate number the extraction cannot see, so the keys
/// come back without one; the scanner tries them against the hive the program was registered in.
/// </summary>
public sealed partial record InnoSetupLog
{
    public const string HeaderPrefix = "Inno Setup Uninstall Log";

    public string? AppId { get; init; }
    public string? AppName { get; init; }
    public bool Is64Bit { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyList<string> RegistryKeys { get; init; } = [];

    /// <summary>The log is skipped past this size: a real one is a few megabytes at most.</summary>
    private const long MaximumSize = 64L * 1024 * 1024;

    // A path runs until a character no real path in a log would contain. Everything past Latin is
    // excluded because a stray length field decodes as CJK and would otherwise glue itself on.
    [GeneratedRegex(@"[A-Za-z]:\\[\x20-\x7E -ɏ]{2,259}")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"(?i)(?<![A-Za-z])(Software|SYSTEM)\\[\x20-\x7E -ɏ]{2,255}")]
    private static partial Regex RegistryPattern();

    /// <summary>The log beside an Inno uninstaller: unins000.exe keeps unins000.dat.</summary>
    public static string? LogPathFor(string? uninstallerPath)
    {
        if (string.IsNullOrWhiteSpace(uninstallerPath)) return null;

        var log = Path.ChangeExtension(uninstallerPath, ".dat");
        return File.Exists(log) ? log : null;
    }

    /// <summary>Reads a log. Null when the file is missing, too large, or not an Inno Setup log.</summary>
    public static InnoSetupLog? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 448 || info.Length > MaximumSize) return null;

            var bytes = File.ReadAllBytes(path);
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static InnoSetupLog? Parse(byte[] bytes)
    {
        if (bytes.Length < 448) return null;

        // Header: a 64-byte ID, then the AppId and AppName as fixed 128-byte ANSI fields.
        var id = Encoding.ASCII.GetString(bytes, 0, 64).TrimEnd('\0', ' ');
        if (!id.StartsWith(HeaderPrefix, StringComparison.Ordinal)) return null;

        var appId = Encoding.ASCII.GetString(bytes, 64, 128).TrimEnd('\0', ' ');
        var appName = Encoding.ASCII.GetString(bytes, 192, 128).TrimEnd('\0', ' ');

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Unicode builds store UTF-16 strings, which can start on either byte, and older ANSI
        // builds store single-byte ones; all three decodings are searched.
        var texts = new[]
        {
            Encoding.Unicode.GetString(bytes),
            Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1),
            Encoding.Latin1.GetString(bytes)
        };

        foreach (var text in texts)
        {
            foreach (Match match in PathPattern().Matches(text))
            {
                var value = CleanTail(match.Value);

                // Wildcards are delete patterns ("new_*"), not things that were installed.
                if (value.Length > 3 && !value.Contains('*') && !value.Contains('?')) files.Add(value);
            }

            foreach (Match match in RegistryPattern().Matches(text))
            {
                var value = CleanTail(match.Value);
                if (value.Contains('\\') && !value.Contains('*')) keys.Add(value);
            }
        }

        return new InnoSetupLog
        {
            AppId = string.IsNullOrWhiteSpace(appId) ? null : appId,
            AppName = string.IsNullOrWhiteSpace(appName) ? null : appName,
            Is64Bit = id.Contains("64-bit", StringComparison.OrdinalIgnoreCase),
            Files = files.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            RegistryKeys = keys.Order(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static string CleanTail(string value) => value.TrimEnd(' ', '.', '\\');
}
