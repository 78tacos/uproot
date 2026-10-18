using System.Text.RegularExpressions;

namespace Uproot.Core;

/// <summary>
/// An uninstall command split into the executable and its arguments.
///
/// Registry uninstall strings are free text written by whoever authored the installer. They are
/// quoted, unquoted with spaces in the path, carry environment variables, or name a bare
/// "MsiExec.exe" that only resolves through the system directory. The arguments are kept as the
/// raw text the vendor wrote rather than re-tokenised, because each installer framework parses
/// its own command line and re-quoting it is how a working uninstaller gets broken.
/// </summary>
public sealed partial record UninstallCommand(string FileName, string Arguments)
{
    public bool IsMsiExec =>
        Path.GetFileNameWithoutExtension(FileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    public bool FileExists => File.Exists(FileName);

    /// <summary>The command as it would be typed, for display.</summary>
    public string Display => Arguments.Length == 0 ? Quote(FileName) : $"{Quote(FileName)} {Arguments}";

    public override string ToString() => Display;

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCodePattern();

    [GeneratedRegex(@"(?<![\w-])[A-Za-z_][A-Za-z0-9_.]*=(""[^""]*""|\S+)")]
    private static partial Regex MsiPropertyPattern();

    /// <summary>
    /// Parses a registry command line. Null when there is nothing usable in it.
    /// <paramref name="fileExists"/> is injectable so tests do not depend on the machine.
    /// </summary>
    public static UninstallCommand? Parse(string? commandLine, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        fileExists ??= File.Exists;
        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        string file;
        string arguments;

        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close < 0)
            {
                file = text.Trim('"');
                arguments = string.Empty;
            }
            else
            {
                file = text[1..close];
                arguments = text[(close + 1)..].Trim();
            }
        }
        else
        {
            (file, arguments) = SplitUnquoted(text, fileExists);
        }

        file = file.Trim();
        if (file.Length == 0) return null;

        return new UninstallCommand(ResolveSystemExecutable(file, fileExists), arguments);
    }

    /// <summary>
    /// An unquoted path with spaces is ambiguous: "C:\Program Files\App\uninst.exe /S" could split
    /// at any space. The first split that names an existing .exe wins; failing that, the first
    /// that merely ends in .exe, which is what a missing uninstaller still needs reported as.
    /// </summary>
    private static (string File, string Arguments) SplitUnquoted(string text, Func<string, bool> fileExists)
    {
        var boundaries = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ') boundaries.Add(i);
        }
        boundaries.Add(text.Length);

        string? firstExe = null;
        var firstExeEnd = -1;

        foreach (var end in boundaries)
        {
            var candidate = text[..end];
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            if (fileExists(candidate)) return (candidate, text[end..].Trim());

            if (firstExe is null)
            {
                firstExe = candidate;
                firstExeEnd = end;
            }
        }

        if (firstExe is not null) return (firstExe, text[firstExeEnd..].Trim());

        var space = text.IndexOf(' ');
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>"MsiExec.exe" and "rundll32" mean the copies in the system directory.</summary>
    private static string ResolveSystemExecutable(string file, Func<string, bool> fileExists)
    {
        if (Path.IsPathRooted(file) || file.Contains('\\') || file.Contains('/')) return file;

        var name = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : file + ".exe";
        var system = Path.Combine(Environment.SystemDirectory, name);

        return fileExists(system) ? system : file;
    }

    // ---- Windows Installer ---------------------------------------------------

    /// <summary>The product code an msiexec command acts on, if it names one.</summary>
    public string? MsiProductCode =>
        IsMsiExec && ProductCodePattern().Match(Arguments) is { Success: true } match
            ? match.Value.ToUpperInvariant()
            : null;

    /// <summary>
    /// True when this msiexec command opens the package's maintenance screens (/I) rather than
    /// removing it (/X). Many vendors register /I — Adobe does — so "Uninstall" in Apps &amp;
    /// Features lands on a Modify / Repair / Remove wizard instead of a removal.
    /// </summary>
    public bool IsMsiMaintenanceMode =>
        IsMsiExec && Regex.IsMatch(Arguments, @"(^|\s)[/-][Ii](\s|\{|$)");

    /// <summary>
    /// msiexec /X for the product, keeping any PROPERTY=value pairs the vendor added. With
    /// <paramref name="quiet"/> it shows progress but asks nothing, and never restarts on its own:
    /// a restart is reported through exit code 3010 instead.
    /// </summary>
    public static UninstallCommand MsiRemove(string productCode, bool quiet, string? originalArguments = null)
    {
        var properties = originalArguments is null
            ? []
            : MsiPropertyPattern().Matches(originalArguments).Select(m => m.Value).ToList();

        var arguments = new List<string> { $"/X{productCode}" };
        if (quiet) arguments.AddRange(["/qb", "/norestart"]);
        arguments.AddRange(properties);

        return new UninstallCommand(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), string.Join(' ', arguments));
    }

    /// <summary>Appends switches unless they are already present.</summary>
    public UninstallCommand WithSwitches(params string[] switches)
    {
        var extra = switches
            .Where(s => !Regex.IsMatch(Arguments, $@"(^|\s){Regex.Escape(s)}(\s|$)", RegexOptions.IgnoreCase))
            .ToList();

        if (extra.Count == 0) return this;

        var joined = string.Join(' ', extra);
        return this with { Arguments = Arguments.Length == 0 ? joined : $"{Arguments} {joined}" };
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
