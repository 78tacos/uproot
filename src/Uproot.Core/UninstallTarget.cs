namespace Uproot.Core;

/// <summary>
/// Everything the leftover scan needs to know about a program, captured before its uninstaller
/// runs. Afterwards the Uninstall entry is gone, Windows Installer has forgotten the product and
/// the executables have been deleted, so none of this could be worked out any more.
/// </summary>
public sealed record UninstallTarget
{
    public required InstalledProgram Program { get; init; }
    public UninstallerProfile? Analysis { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>Folders that are the program's own: anything pointing into them belongs to it.</summary>
    public IReadOnlyList<string> ReferenceFolders { get; init; } = [];

    /// <summary>Every name the program's things may carry, strongest first.</summary>
    public IReadOnlyList<string> ProductNames { get; init; } = [];

    /// <summary>
    /// The names strong enough to trust on their own, anywhere: all of <see cref="ProductNames"/>
    /// except the publisher-stripped one, which is only trusted inside the publisher's folders.
    /// </summary>
    public IReadOnlyList<string> PrimaryNames { get; init; } = [];

    public IReadOnlyList<string> PublisherNames { get; init; } = [];

    /// <summary>Executable file names from the install folder, for matching App Paths registrations.</summary>
    public IReadOnlyList<string> ExecutableNames { get; init; } = [];

    public static UninstallTarget From(UninstallerProfile analysis)
    {
        var program = analysis.Program;
        var installLocation = analysis.InstallLocation;

        var references = new List<string>();
        if (installLocation is not null) references.Add(installLocation);

        // The vendor folder the program sits in, when it is not the program folder itself:
        // "BraveSoftware" for "…\BraveSoftware\Brave-Browser".
        // A packaged app's parent is WindowsApps, which is nobody's vendor folder.
        string? vendorFolder = null;
        if (installLocation is not null && !program.IsStoreApp && Path.GetDirectoryName(installLocation) is { } parent)
        {
            var parentName = Path.GetFileName(parent);
            if (!ProgramNames.IsGeneric(parentName) && !IsStandardRoot(parent)) vendorFolder = parentName;
        }

        var names = ProgramNames.ProductNames(
            program.DisplayName, program.Publisher, installLocation,
            analysis.Msi?.ProductName, analysis.Inno?.AppName);

        // A recorded or folder name that happens to equal the stripped one stays primary: the
        // installer itself chose it.
        var stripped = ProgramNames.PublisherStrippedName(program.DisplayName, program.Publisher);
        var recorded = new[] { analysis.Msi?.ProductName, analysis.Inno?.AppName, ProgramNames.InstallFolderName(installLocation) }
            .Select(ProgramNames.CleanDisplayName)
            .ToList();

        var primary = names
            .Where(n => stripped is null
                        || ProgramNames.Normalize(n) != ProgramNames.Normalize(stripped)
                        || recorded.Any(r => ProgramNames.Normalize(r) == ProgramNames.Normalize(n)))
            .ToList();

        return new UninstallTarget
        {
            Program = program,
            Analysis = analysis,
            InstallLocation = installLocation,
            ReferenceFolders = references,
            ProductNames = names,
            PrimaryNames = primary,
            PublisherNames = ProgramNames.PublisherNames(program.Publisher ?? analysis.Msi?.Manufacturer, vendorFolder),
            ExecutableNames = ExecutablesIn(installLocation)
        };
    }

    private static bool IsStandardRoot(string folder)
    {
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        return new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432") ?? Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86),
            Folder(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Folder(Environment.SpecialFolder.ApplicationData),
            Folder(Environment.SpecialFolder.CommonApplicationData)
        }.Any(root => PathRules.SamePath(root, folder));
    }

    /// <summary>The .exe names in the install folder and one level down, capped so a huge folder stays quick.</summary>
    private static IReadOnlyList<string> ExecutablesIn(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return [];

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 1,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            return Directory.EnumerateFiles(folder, "*.exe", options)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !n.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
