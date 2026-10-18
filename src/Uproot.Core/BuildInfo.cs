using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Uproot.Core;

/// <summary>
/// Which build of Uproot a file is: the release version, the build number and the commit.
///
/// It is read from the informational version, which Directory.Build.targets writes as
/// <c>0.9.0+57.abc1234</c> and which Windows shows as the Product version.
/// </summary>
public sealed partial record BuildInfo : IComparable<BuildInfo>
{
    public required Version Version { get; init; }

    /// <summary>Commits in the history at build time. Null for a build that was not numbered.</summary>
    public int? BuildNumber { get; init; }

    /// <summary>Abbreviated commit hash, or null when it is not known.</summary>
    public string? Commit { get; init; }

    /// <summary>Built from a checkout with uncommitted changes, so the commit is not the whole story.</summary>
    public bool Dirty { get; init; }

    /// <summary>The build this code is running from.</summary>
    public static BuildInfo Current { get; } = FromAssembly(typeof(BuildInfo).Assembly);

    [GeneratedRegex(@"^\s*v?(?<version>\d+\.\d+\.\d+)(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?(?:\+(?<meta>[0-9A-Za-z.-]+))?\s*$")]
    private static partial Regex Shape();

    [GeneratedRegex("^[0-9a-f]{7,40}$")]
    private static partial Regex CommitShape();

    /// <summary>
    /// Parses an informational version. Returns null for anything that is not one, rather than
    /// guessing: a wrong answer here would tell someone an old copy is current.
    /// </summary>
    public static BuildInfo? Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;

        var match = Shape().Match(informationalVersion);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version)) return null;

        int? buildNumber = null;
        string? commit = null;
        var dirty = false;

        if (match.Groups["meta"].Success)
        {
            foreach (var part in match.Groups["meta"].Value.Split('.'))
            {
                if (part.Equals("dirty", StringComparison.OrdinalIgnoreCase))
                {
                    dirty = true;
                }
                else if (buildNumber is null && commit is null
                         && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    buildNumber = number;
                }
                else if (commit is null && CommitShape().IsMatch(part))
                {
                    commit = part.Length > 7 ? part[..7] : part;
                }
            }
        }

        return new BuildInfo { Version = version, BuildNumber = buildNumber, Commit = commit, Dirty = dirty };
    }

    /// <summary>The build of an executable on disk, from its Product version. Null if unreadable.</summary>
    public static BuildInfo? FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return File.Exists(path) ? Parse(FileVersionInfo.GetVersionInfo(path).ProductVersion) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static BuildInfo FromAssembly(Assembly assembly) =>
        Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new BuildInfo { Version = assembly.GetName().Version ?? new Version(0, 0, 0) };

    // ---- Presentation ---------------------------------------------------

    /// <summary>1.1.0</summary>
    public string VersionText => Version.ToString(3);

    /// <summary>1.1.0 (build 57), for anywhere a person reads it.</summary>
    public string Display => BuildNumber is { } number
        ? $"{VersionText} (build {number}{(Dirty ? ", modified" : "")})"
        : Commit is { } commit
            ? $"{VersionText} ({commit})"
            : $"{VersionText} (unnumbered build)";

    /// <summary>1.1.0 (build 57, abc1234), for when the commit matters too.</summary>
    public string DisplayWithCommit => BuildNumber is { } number && Commit is { } commit
        ? $"{VersionText} (build {number}, {commit}{(Dirty ? ", modified" : "")})"
        : Display;

    /// <summary>
    /// The exact identity, in the same form it is stored in: 1.1.0+57.abc1234. This is what goes
    /// on an elevated command line, where the administrator approving it can read it.
    /// </summary>
    public string Identity
    {
        get
        {
            var meta = new List<string>();
            if (BuildNumber is { } number) meta.Add(number.ToString(CultureInfo.InvariantCulture));
            if (Commit is { } commit) meta.Add(commit);
            if (Dirty) meta.Add("dirty");

            return meta.Count == 0 ? VersionText : $"{VersionText}+{string.Join('.', meta)}";
        }
    }

    /// <summary>
    /// Orders by version, then by build number. An unnumbered build sorts below a numbered one of
    /// the same version: every unnumbered build predates numbering.
    /// </summary>
    public int CompareTo(BuildInfo? other)
    {
        if (other is null) return 1;

        var byVersion = Version.CompareTo(other.Version);
        if (byVersion != 0) return byVersion;

        return (BuildNumber ?? -1).CompareTo(other.BuildNumber ?? -1);
    }

    /// <summary>True when both name the same build, ignoring nothing that identifies it.</summary>
    public bool IsSameBuild(BuildInfo? other) =>
        other is not null && string.Equals(Identity, other.Identity, StringComparison.OrdinalIgnoreCase);

    /// <summary>How <paramref name="build"/> stands relative to <paramref name="reference"/>.</summary>
    public static BuildRelation Relate(BuildInfo? build, BuildInfo? reference)
    {
        if (build is null || reference is null) return BuildRelation.Unknown;
        if (build.IsSameBuild(reference)) return BuildRelation.Same;

        return build.CompareTo(reference) switch
        {
            < 0 => BuildRelation.Older,
            > 0 => BuildRelation.Newer,
            _ => BuildRelation.Different
        };
    }

    /// <summary>
    /// 1.1.0.57: the form Apps &amp; Features and winget expect for DisplayVersion, which compare
    /// dotted numbers and would misread anything else.
    /// </summary>
    public string DottedVersion => BuildNumber is { } number ? $"{VersionText}.{number}" : VersionText;

    public override string ToString() => Display;
}

/// <summary>How one build stands relative to another.</summary>
public enum BuildRelation
{
    /// <summary>Either build could not be read.</summary>
    Unknown,
    Same,
    Older,
    Newer,

    /// <summary>
    /// Same version and build number, different build: two branches with the same number of
    /// commits, or one built with uncommitted changes. Neither can be called newer.
    /// </summary>
    Different
}
