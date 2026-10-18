using System.Text;
using System.Text.RegularExpressions;

namespace Uproot.Core;

/// <summary>How closely a folder or key name matches a program name.</summary>
public enum NameMatch
{
    None,

    /// <summary>The program name appears as a whole word inside it: "Launch Acrobat Helper".</summary>
    Contains,

    /// <summary>It starts with the program name and then a separator: "Acrobat Distiller".</summary>
    Prefix,

    /// <summary>The same name, ignoring case, spacing and punctuation: "AdobeAcrobat".</summary>
    Exact
}

/// <summary>
/// Turns display names and publishers into the names a program's files and keys are likely to
/// carry, and compares names against them.
///
/// Everything here errs towards not matching. A false negative leaves a folder behind, which is
/// what the built-in uninstaller already did; a false positive offers someone else's data for
/// deletion.
/// </summary>
public static partial class ProgramNames
{
    /// <summary>
    /// Names that say nothing about which product something belongs to. A program whose cleaned
    /// name is one of these contributes no name to match on, so an app called "Tools" can never
    /// claim every folder called Tools.
    /// </summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "system", "system32", "syswow64", "common", "common files", "shared",
        "program", "programs", "program files", "programdata", "data", "appdata", "local", "locallow",
        "roaming", "temp", "tmp", "cache", "caches", "update", "updates", "updater", "setup", "install",
        "installer", "installers", "uninstall", "uninstaller", "service", "services", "driver", "drivers",
        "runtime", "runtimes", "tools", "tool", "bin", "lib", "app", "apps", "application", "applications",
        "software", "plugins", "plugin", "addins", "addin", "extensions", "extension", "packages",
        "package", "help", "docs", "documentation", "resources", "config", "configuration", "settings",
        "logs", "log", "crashdumps", "users", "user", "public", "default", "desktop", "documents",
        "downloads", "start menu", "x64", "x86", "amd64", "arm64", "win32", "win64", "client", "server",
        "agent", "helper", "core", "sdk", "components", "component", "framework", "library", "manager",
        "launcher", "browser", "player", "viewer", "editor", "studio", "suite", "utilities", "utility",
        "driver package", "the", "inc", "llc", "ltd", "corp", "corporation", "company", "team",
        "network", "security", "support", "internet", "games", "game", "media", "fonts", "drivers",
        "packages cache", "package cache", "installer cache", "office", "classes", "clsid", "wow6432node",
        "policies", "vendor"
    };

    /// <summary>Trailing words that name a company's legal form rather than the company.</summary>
    private static readonly HashSet<string> PublisherSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "incorporated", "llc", "l.l.c", "ltd", "limited", "corp", "corporation", "co", "company",
        "gmbh", "ag", "sa", "s.a", "sarl", "bv", "b.v", "pbc", "plc", "pty", "oy", "ab", "as", "kk",
        "technologies", "technology", "software", "systems", "solutions", "labs", "team", "community",
        "development", "foundation", "project", "group", "studios", "international", "holdings", "srl",
        "s.r.l", "s.r.o", "spa", "s.p.a", "ug", "e.v", "sp. z o.o"
    };

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"\s+-\s+[a-z]{2}(-[a-z]{2,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageSuffix();

    [GeneratedRegex(@"(\s+-)?\s+v?\d+(\.\d+)+[a-z0-9.\-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"\s+(x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|win64|win32)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureSuffix();

    [GeneratedRegex(@"^(v?\d+([._]\d+)*|app-\d+(\.\d+)*)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLike();

    /// <summary>
    /// A display name with the noise stripped: "(64-bit)", "(x64 en-US)", trailing version
    /// numbers, " - en-us". "Adobe Acrobat (64-bit)" becomes "Adobe Acrobat".
    /// </summary>
    public static string CleanDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return string.Empty;

        var name = Parenthetical().Replace(displayName, string.Empty).Trim();

        // Each pass can uncover the next ("Foo 1.2 x64 - en-us"), so repeat until stable.
        for (var i = 0; i < 4; i++)
        {
            var before = name;
            name = LanguageSuffix().Replace(name, string.Empty).Trim();
            name = ArchitectureSuffix().Replace(name, string.Empty).Trim();
            name = VersionSuffix().Replace(name, string.Empty).Trim();
            name = name.TrimEnd('-', ',', ' ').Trim();
            if (name == before) break;
        }

        return Collapse(name);
    }

    /// <summary>
    /// A publisher with its legal form removed: "Adobe Systems Incorporated" is "Adobe",
    /// "The Git Development Community" is "Git", "Notepad++ Team" is "Notepad++".
    /// </summary>
    public static string CleanPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return string.Empty;

        var name = Parenthetical().Replace(publisher, string.Empty).Trim().Trim(',', '.').Trim();

        if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) name = name[4..];

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Keep at least one word: "Software" on its own is still somebody's name for themselves.
        while (words.Count > 1 && PublisherSuffixes.Contains(words[^1].Trim(',', '.')))
        {
            words.RemoveAt(words.Count - 1);
        }

        return Collapse(string.Join(' ', words).Trim(',', '.', ' '));
    }

    /// <summary>
    /// Case, spacing and punctuation removed, keeping the characters that distinguish product
    /// names from each other: "Notepad++" stays distinct from "Notepad".
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            else if (c is '+' or '#') builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>True when a name is too generic to identify anything by.</summary>
    public static bool IsGeneric(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;

        var trimmed = name.Trim();
        var normalized = Normalize(trimmed);

        return normalized.Length < 3
               || normalized.All(char.IsDigit)
               || Generic.Contains(trimmed)
               || Generic.Contains(normalized)
               || VersionLike().IsMatch(trimmed);
    }

    /// <summary>True for folder names like "1.2.3" or "app-1.0.9003" that a version sits in.</summary>
    public static bool IsVersionLike(string? name) =>
        !string.IsNullOrWhiteSpace(name) && VersionLike().IsMatch(name.Trim());

    /// <summary>
    /// How <paramref name="candidate"/> (a folder, key, task or service name) relates to one
    /// program name. Generic program names never match.
    /// </summary>
    public static NameMatch Compare(string? candidate, string? programName)
    {
        if (string.IsNullOrWhiteSpace(candidate) || IsGeneric(programName)) return NameMatch.None;

        var name = candidate.Trim();
        var variant = programName!.Trim();

        if (Normalize(name) == Normalize(variant)) return NameMatch.Exact;

        // Short names only match exactly. "Git" as a prefix would claim "Git LFS", which is fair,
        // but four letters is where "Acro" stops being a word and starts being a coincidence.
        if (Normalize(variant).Length < 4) return NameMatch.None;

        if (name.Length > variant.Length
            && name.StartsWith(variant, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(name[variant.Length]))
        {
            return NameMatch.Prefix;
        }

        var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(variant)}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase) ? NameMatch.Contains : NameMatch.None;
    }

    /// <summary>The strongest match against any of several names, and which name produced it.</summary>
    public static (NameMatch Match, string? Name) Best(string? candidate, IEnumerable<string> names)
    {
        var best = (NameMatch.None, (string?)null);

        foreach (var name in names)
        {
            var match = Compare(candidate, name);
            if (match > best.Item1) best = (match, name);
            if (match == NameMatch.Exact) break;
        }

        return best;
    }

    /// <summary>
    /// The product names a program's files and keys are likely to be called, strongest first:
    /// the cleaned display name, the same without its publisher ("Acrobat"), the install folder's
    /// own name ("Acrobat DC"), and any names the installer recorded for itself.
    /// </summary>
    public static IReadOnlyList<string> ProductNames(
        string? displayName, string? publisher, string? installLocation, params string?[] recordedNames)
    {
        var names = new List<string>();

        void Add(string? value)
        {
            var clean = CleanDisplayName(value);
            if (IsGeneric(clean)) return;
            if (names.Any(n => Normalize(n) == Normalize(clean))) return;
            names.Add(clean);
        }

        Add(CleanDisplayName(displayName));

        // Generic publishers still count here: "Microsoft Visual Studio Code" should yield
        // "Visual Studio Code" even though "Microsoft" identifies nothing on its own.
        Add(PublisherStrippedName(displayName, publisher));

        foreach (var recorded in recordedNames) Add(recorded);

        if (InstallFolderName(installLocation) is { } folder) Add(folder);

        return names;
    }

    /// <summary>
    /// The product name with its publisher taken off the front — "Acrobat" from "Adobe Acrobat",
    /// "Notes" from "Fabrikam Notes". Null when the name does not start with the publisher.
    ///
    /// This one is weaker than the others. It is exactly what goes inside a publisher's folder
    /// (Adobe\Acrobat), but on its own it is often an ordinary word, and a folder called "Notes"
    /// directly in AppData could belong to anything.
    /// </summary>
    public static string? PublisherStrippedName(string? displayName, string? publisher)
    {
        var cleaned = CleanDisplayName(displayName);
        var cleanPublisher = CleanPublisher(publisher);
        var publisherWord = cleanPublisher.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        foreach (var prefix in new[] { cleanPublisher, publisherWord })
        {
            if (!string.IsNullOrEmpty(prefix) && cleaned.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = cleaned[(prefix.Length + 1)..];
                return IsGeneric(stripped) ? null : stripped;
            }
        }

        return null;
    }

    /// <summary>
    /// The names a publisher's folders are likely to carry: the cleaned publisher, its first word
    /// when that is distinctive ("Brave Software" → "Brave"), and the vendor folder the program
    /// was actually installed under ("BraveSoftware").
    ///
    /// Generic publishers such as Microsoft are included, because "Microsoft\Teams" is still a
    /// Publisher\Product pattern worth looking inside. Whether a publisher folder may ever be
    /// offered on its own is a separate question, answered with <see cref="IsGeneric"/>.
    /// </summary>
    public static IReadOnlyList<string> PublisherNames(string? publisher, string? vendorFolder)
    {
        var names = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || Normalize(value).Length < 2) return;
            if (names.Any(n => Normalize(n) == Normalize(value))) return;
            names.Add(value.Trim());
        }

        var cleaned = CleanPublisher(publisher);
        Add(cleaned);

        var firstWord = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstWord is not null && Normalize(firstWord).Length >= 4 && !IsGeneric(firstWord)) Add(firstWord);

        if (!IsVersionLike(vendorFolder)) Add(vendorFolder);

        return names;
    }

    /// <summary>
    /// The name of the folder a program lives in, skipping the generic ones: for
    /// "…\Brave-Browser\Application" that is "Brave-Browser", not "Application".
    /// </summary>
    public static string? InstallFolderName(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return null;

        try
        {
            var path = Path.TrimEndingDirectorySeparator(installLocation.Trim().Trim('"'));

            for (var i = 0; i < 3 && !string.IsNullOrEmpty(path); i++)
            {
                var leaf = Path.GetFileName(path);
                if (!IsGeneric(leaf)) return leaf;
                path = Path.GetDirectoryName(path);
            }
        }
        catch (ArgumentException)
        {
        }

        return null;
    }

    private static string Collapse(string value) =>
        Regex.Replace(value, @"\s{2,}", " ").Trim();
}
