using System.Diagnostics;
using System.Text;
using Uproot.Core.Elevation;

namespace Uproot.Core;

public enum FindingTone
{
    Info,
    Good,
    Warning
}

/// <summary>One thing the analysis learned about an uninstaller, in words a person can act on.</summary>
public sealed record AnalysisFinding(FindingTone Tone, string Title, string Detail);

/// <summary>
/// Everything Uproot worked out about a program's built-in uninstaller before running it:
/// what framework built it, what it will run, whether it can run unattended, and what it is
/// expected to leave behind.
/// </summary>
public sealed record UninstallerProfile
{
    public required InstalledProgram Program { get; init; }
    public InstallerTechnology Technology { get; init; }

    /// <summary>The command the registry entry holds, parsed.</summary>
    public UninstallCommand? RegisteredCommand { get; init; }

    /// <summary>What Uproot will run for an interactive uninstall, which is not always the registered command.</summary>
    public UninstallCommand? InteractiveCommand { get; init; }

    /// <summary>An unattended variant, when one is known. Null means the uninstaller has to be answered.</summary>
    public UninstallCommand? QuietCommand { get; init; }

    public bool UninstallerMissing { get; init; }

    public string? UninstallerCompany { get; init; }
    public string? UninstallerDescription { get; init; }

    /// <summary>Where the program lives, from the entry or worked out from its files.</summary>
    public string? InstallLocation { get; init; }

    public MsiFootprint? Msi { get; init; }
    public InnoSetupLog? Inno { get; init; }

    public IReadOnlyList<AnalysisFinding> Findings { get; init; } = [];

    public string TechnologyName => UninstallerAnalyzer.Describe(Technology);

    public bool CanUninstall => InteractiveCommand is not null && !UninstallerMissing;
}

/// <summary>
/// Looks at a program's uninstaller before it runs, to uninstall better than Apps &amp; Features
/// would: the right command, an unattended mode where one exists, and a record of what the
/// installer put where, so the leftover scan afterwards knows exactly what to check.
/// </summary>
public static class UninstallerAnalyzer
{
    public static string Describe(InstallerTechnology technology) => technology switch
    {
        InstallerTechnology.WindowsInstaller => "Windows Installer (MSI)",
        InstallerTechnology.InnoSetup => "Inno Setup",
        InstallerTechnology.Nsis => "NSIS",
        InstallerTechnology.InstallShield => "InstallShield",
        InstallerTechnology.WixBurn => "WiX bundle",
        InstallerTechnology.Squirrel => "Squirrel",
        InstallerTechnology.ChromiumSetup => "Chromium installer",
        InstallerTechnology.ClickToRun => "Office Click-to-Run",
        InstallerTechnology.Msix => "Microsoft Store app",
        _ => "Unknown"
    };

    /// <summary>
    /// The framework, from the registry entry alone. Cheap enough for every row of a list; the
    /// full analysis also looks inside the uninstaller binary.
    /// </summary>
    public static InstallerTechnology QuickDetect(InstalledProgram program)
    {
        if (program.IsStoreApp) return InstallerTechnology.Msix;

        var command = UninstallCommand.Parse(program.UninstallString);
        var fileName = command is null ? string.Empty : Path.GetFileName(command.FileName);
        var arguments = command?.Arguments ?? string.Empty;

        if (program.IsWindowsInstaller || command?.IsMsiExec == true) return InstallerTechnology.WindowsInstaller;

        if (program.KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase)
            || program.Values.ContainsKey("Inno Setup: Setup Version"))
        {
            return InstallerTechnology.InnoSetup;
        }

        if (program.Values.ContainsKey("BundleCachePath") || program.Values.ContainsKey("BundleUpgradeCode")
            || program.Values.ContainsKey("BundleProviderKey"))
        {
            return InstallerTechnology.WixBurn;
        }

        if (fileName.Equals("Update.exe", StringComparison.OrdinalIgnoreCase)
            && arguments.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerTechnology.Squirrel;
        }

        if (fileName.Equals("OfficeClickToRun.exe", StringComparison.OrdinalIgnoreCase)) return InstallerTechnology.ClickToRun;

        if (fileName.Equals("setup.exe", StringComparison.OrdinalIgnoreCase)
            && arguments.Contains("--uninstall", StringComparison.OrdinalIgnoreCase)
            && (command?.FileName.Contains(@"\Application\", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return InstallerTechnology.ChromiumSetup;
        }

        var text = program.UninstallString ?? string.Empty;
        if (text.Contains("InstallShield Installation Information", StringComparison.OrdinalIgnoreCase)
            || text.Contains("-runfromtemp", StringComparison.OrdinalIgnoreCase)
            || text.Contains("IsUninst", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerTechnology.InstallShield;
        }

        return InstallerTechnology.Unknown;
    }

    /// <summary>
    /// The full analysis. Reads the uninstaller binary, the MSI database or Inno log where there
    /// is one, and works out the commands to use. Can take a few seconds for a large MSI.
    /// </summary>
    public static UninstallerProfile Analyze(InstalledProgram program, CancellationToken ct = default)
    {
        if (program.IsStoreApp) return AnalyzeStoreApp(program);

        var registered = UninstallCommand.Parse(program.UninstallString);
        var technology = QuickDetect(program);

        var uninstallerPath = registered is { IsMsiExec: false } ? registered.FileName : null;
        var missing = registered is null || (uninstallerPath is not null && !File.Exists(uninstallerPath));

        if (technology == InstallerTechnology.Unknown && uninstallerPath is not null && !missing)
            technology = DetectFromBinary(uninstallerPath);

        string? company = null, description = null;
        if (uninstallerPath is not null && !missing)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(uninstallerPath);
                company = Blank(version.CompanyName);
                description = Blank(version.FileDescription);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        ct.ThrowIfCancellationRequested();

        MsiFootprint? msi = null;
        if (program.ProductCode is { } productCode && MsiInterop.IsProductInstalled(productCode))
            msi = MsiFootprint.Read(productCode, ct);

        InnoSetupLog? inno = null;
        if (technology == InstallerTechnology.InnoSetup && InnoSetupLog.LogPathFor(uninstallerPath) is { } logPath)
            inno = InnoSetupLog.Read(logPath);

        var installLocation = InferInstallLocation(program, registered, msi);
        var (interactive, quiet) = Commands(program, technology, registered);

        var profile = new UninstallerProfile
        {
            Program = program,
            Technology = technology,
            RegisteredCommand = registered,
            InteractiveCommand = interactive,
            QuietCommand = quiet,
            UninstallerMissing = missing,
            UninstallerCompany = company,
            UninstallerDescription = description,
            InstallLocation = installLocation,
            Msi = msi,
            Inno = inno
        };

        return profile with { Findings = Findings(profile) };
    }

    /// <summary>
    /// A packaged app has no uninstaller of its own: Windows removes the package, its folder and
    /// its data. The command shown is what Uproot has Windows run.
    /// </summary>
    private static UninstallerProfile AnalyzeStoreApp(InstalledProgram program)
    {
        var fullName = program.PackageFullName;
        var command = StoreApps.IsValidFullName(fullName) ? StoreApps.RemoveCommand(fullName!) : null;

        var profile = new UninstallerProfile
        {
            Program = program,
            Technology = InstallerTechnology.Msix,
            InteractiveCommand = command,
            QuietCommand = command,
            UninstallerMissing = command is null,
            InstallLocation = StoreApps.PackageFolder(program)
        };

        var findings = new List<AnalysisFinding>
        {
            new(FindingTone.Good, "Removed by Windows",
                "A Microsoft Store (MSIX) app. Windows removes its install folder and its data folder in "
                + "AppData\\Local\\Packages itself, so there is no uninstaller to follow. The leftover scan looks for what "
                + "the app wrote outside its package: firewall rules, startup entries, folders of its own.")
        };

        // Elevated with someone else's administrator credentials, packages are that account's.
        var interactive = ElevationValidation.InteractiveUserSid();
        if (interactive is not null && !interactive.Equals(ElevationValidation.CurrentUserSid(), StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new AnalysisFinding(FindingTone.Warning, "For the account Uproot runs as",
                "Uproot is running under a different account from the person signed in, so this is that account's copy "
                + "of the app, and removing it leaves the signed-in person's copy in place."));
        }
        else
        {
            findings.Add(new AnalysisFinding(FindingTone.Info, "Only for this account",
                "It is removed for you. If other people on this PC installed it too, they keep their copies."));
        }

        return profile with { Findings = findings };
    }

    // ---- Commands --------------------------------------------------------------

    private static (UninstallCommand? Interactive, UninstallCommand? Quiet) Commands(
        InstalledProgram program, InstallerTechnology technology, UninstallCommand? registered)
    {
        var vendorQuiet = UninstallCommand.Parse(program.QuietUninstallString);

        if (technology == InstallerTechnology.WindowsInstaller && program.ProductCode is { } productCode)
        {
            // /X removes; /I opens the maintenance wizard. Using /X for the interactive run too
            // means "Uninstall" does what it says rather than offering Modify and Repair first.
            return (UninstallCommand.MsiRemove(productCode, quiet: false, registered?.Arguments),
                    UninstallCommand.MsiRemove(productCode, quiet: true, registered?.Arguments));
        }

        if (registered is null) return (null, vendorQuiet);

        var quiet = vendorQuiet ?? technology switch
        {
            InstallerTechnology.InnoSetup => registered.WithSwitches("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"),
            InstallerTechnology.Nsis => registered.WithSwitches("/S"),
            InstallerTechnology.WixBurn => registered.WithSwitches("/quiet", "/norestart"),
            InstallerTechnology.Squirrel => registered.WithSwitches("-s"),
            InstallerTechnology.ChromiumSetup => registered.WithSwitches("--force-uninstall"),
            InstallerTechnology.ClickToRun => registered.WithSwitches("DisplayLevel=False"),
            _ => null
        };

        return (registered, quiet);
    }

    // ---- Binary signatures -----------------------------------------------------

    private static readonly (byte[] Signature, InstallerTechnology Technology)[] Signatures =
    [
        (Encoding.ASCII.GetBytes("Nullsoft.NSIS"), InstallerTechnology.Nsis),
        (Encoding.ASCII.GetBytes("Nullsoft Install System"), InstallerTechnology.Nsis),
        (Encoding.ASCII.GetBytes("Inno Setup"), InstallerTechnology.InnoSetup),
        (Encoding.ASCII.GetBytes(".wixburn"), InstallerTechnology.WixBurn),
        (Encoding.ASCII.GetBytes("InstallShield"), InstallerTechnology.InstallShield)
    ];

    /// <summary>
    /// Identifies an uninstaller by strings its framework always embeds. Reads at most 32 MB, in
    /// overlapping chunks so a signature straddling two reads is still found.
    /// </summary>
    public static InstallerTechnology DetectFromBinary(string path)
    {
        const int chunk = 1024 * 1024;
        const long limit = 32L * 1024 * 1024;
        var overlap = Signatures.Max(s => s.Signature.Length);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[chunk + overlap];
            var carried = 0;
            long total = 0;

            while (total < limit)
            {
                var read = stream.Read(buffer, carried, chunk);
                if (read <= 0) break;

                var span = buffer.AsSpan(0, carried + read);
                foreach (var (signature, technology) in Signatures)
                {
                    if (span.IndexOf(signature) >= 0) return technology;
                }

                carried = Math.Min(overlap, span.Length);
                span[^carried..].CopyTo(buffer);
                total += read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return InstallerTechnology.Unknown;
    }

    // ---- Install location ------------------------------------------------------

    /// <summary>
    /// Where the program actually lives. Many MSI packages never record InstallLocation, so this
    /// falls back to what the installer did record, then to the folder its own executables sit
    /// in — skipping shared places like System32 or the package cache that prove nothing.
    /// </summary>
    public static string? InferInstallLocation(InstalledProgram program, UninstallCommand? registered, MsiFootprint? msi)
    {
        string? Usable(string? path)
        {
            var normalized = PathRules.Normalize(path);
            if (normalized is null || !Directory.Exists(normalized)) return null;
            if (ProgramClaim.IsSharedExecutableLocation(normalized + @"\x")) return null;
            return IsTooBroad(normalized) ? null : normalized;
        }

        if (Usable(program.InstallLocation) is { } recorded) return recorded;

        if (program.Values.TryGetValue("Inno Setup: App Path", out var innoPath) && Usable(innoPath) is { } inno) return inno;

        if (Usable(msi?.InstallLocation) is { } fromMsi) return fromMsi;

        foreach (var command in new[] { program.DisplayIcon?.Split(',')[0], registered?.IsMsiExec == false ? registered.FileName : null })
        {
            if (PathRules.CommandExecutable(command) is not { } exe) continue;
            if (ProgramClaim.IsSharedExecutableLocation(exe)) continue;
            if (Usable(ProgramFolderFromExecutable(exe)) is { } folder) return folder;
        }

        return null;
    }

    /// <summary>
    /// The program folder an executable belongs to, climbing out of the subfolders installers put
    /// their uninstallers in: "…\Mozilla Firefox\uninstall\helper.exe" is Firefox's folder, and
    /// "…\Brave-Browser\Application\1.2.3\Installer\setup.exe" is Brave's.
    /// </summary>
    public static string? ProgramFolderFromExecutable(string executable)
    {
        var folder = Path.GetDirectoryName(executable);

        for (var i = 0; i < 4 && folder is not null; i++)
        {
            var leaf = Path.GetFileName(folder);
            var climb = ProgramNames.IsVersionLike(leaf)
                        || leaf.Equals("uninstall", StringComparison.OrdinalIgnoreCase)
                        || leaf.Equals("uninst", StringComparison.OrdinalIgnoreCase)
                        || leaf.Equals("_uninstall", StringComparison.OrdinalIgnoreCase)
                        || leaf.Equals("uninstaller", StringComparison.OrdinalIgnoreCase)
                        || leaf.Equals("installer", StringComparison.OrdinalIgnoreCase)
                        || leaf.Equals("application", StringComparison.OrdinalIgnoreCase);

            if (!climb) break;

            var parent = Path.GetDirectoryName(folder);
            if (parent is null || IsTooBroad(parent)) break;
            folder = parent;
        }

        return folder;
    }

    /// <summary>A folder too general to be any one program's: a drive root, Program Files, a profile.</summary>
    public static bool IsTooBroad(string folder)
    {
        var normalized = PathRules.Normalize(folder);
        if (normalized is null) return true;

        if (Path.GetPathRoot(normalized) is { } root && normalized.Length <= root.Length) return true;

        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        string[] broad =
        [
            Environment.GetEnvironmentVariable("ProgramW6432") ?? Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86),
            Folder(Environment.SpecialFolder.CommonProgramFiles),
            Folder(Environment.SpecialFolder.CommonProgramFilesX86),
            Folder(Environment.SpecialFolder.CommonApplicationData),
            Folder(Environment.SpecialFolder.UserProfile),
            Folder(Environment.SpecialFolder.LocalApplicationData),
            Folder(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Folder(Environment.SpecialFolder.Windows)
        ];

        return broad.Any(b => PathRules.SamePath(b, normalized));
    }

    // ---- Findings --------------------------------------------------------------

    private static IReadOnlyList<AnalysisFinding> Findings(UninstallerProfile profile)
    {
        var program = profile.Program;
        var findings = new List<AnalysisFinding>();

        if (!program.IsRegistered)
        {
            return profile.InstallLocation is { } folder
                ?
                [
                    new AnalysisFinding(FindingTone.Info, "Searching by name and folder",
                        $"No program by this name is registered. The scan treats {folder} as its install folder: the folder "
                        + "itself, and anything pointing into it — shortcuts, startup entries, services, tasks, COM classes, "
                        + "firewall rules, PATH entries — is taken as the program's. Folders and keys named after it are "
                        + "found as well.")
                ]
                :
                [
                    new AnalysisFinding(FindingTone.Info, "Searching by name only",
                        "No program by this name is registered, so the scan works from the name and publisher alone: "
                        + "folders, keys, services and tasks named after it, and anything left pointing at files that "
                        + "no longer exist in a folder named after it.")
                ];
        }

        if (profile.UninstallerMissing)
        {
            findings.Add(new AnalysisFinding(FindingTone.Warning, "The uninstaller is missing",
                profile.RegisteredCommand is { } command
                    ? $"The entry points at {command.FileName}, which no longer exists. Use \"Remove leftovers only\" to clean up what is left."
                    : "The entry has no uninstall command at all. Use \"Remove leftovers only\" to clean up what is left."));
        }

        switch (profile.Technology)
        {
            case InstallerTechnology.WindowsInstaller when profile.Msi is { } msi:
                findings.Add(new AnalysisFinding(FindingTone.Good, "Windows Installer package",
                    $"Windows keeps a record of everything this package installed: {msi.ComponentCount:N0} components, "
                    + $"{msi.InstalledComponentCount:N0} of them on this PC, covering {msi.Files.Count:N0} files and "
                    + $"{msi.Registry.Count:N0} registry entries. That record was read before uninstalling, so "
                    + "afterwards each item can be checked individually rather than guessed at."));

                if (msi.SharedComponentCount > 0)
                {
                    findings.Add(new AnalysisFinding(FindingTone.Info, $"{msi.SharedComponentCount:N0} shared components are protected",
                        "These are used by other installed programs or marked permanent by the package"
                        + (msi.SharedSystemFileCount > 0 ? $", including {msi.SharedSystemFileCount:N0} files in the Windows folder such as the Visual C++ runtime" : string.Empty)
                        + ". Windows Installer leaves them on purpose, and they are never offered for removal."));
                }

                if (msi.CustomActionCount > 0)
                {
                    findings.Add(new AnalysisFinding(FindingTone.Warning, $"{msi.CustomActionCount:N0} custom actions",
                        "The package runs its own code while installing and uninstalling. Anything that code creates "
                        + "— settings, caches, scheduled tasks, services — is invisible to Windows Installer and is "
                        + "the usual source of leftovers."));
                }

                findings.Add(new AnalysisFinding(FindingTone.Info, "Files created after installing will stay",
                    "Windows Installer only removes what it installed. Settings, caches and logs the program wrote "
                    + "into user profiles later are not in its record, so expect those to be left behind."));

                if (msi.PatchCount > 0)
                    findings.Add(new AnalysisFinding(FindingTone.Info, $"{msi.PatchCount} patch(es) applied", "They are removed along with the product."));

                if (msi.Services.Count > 0)
                    findings.Add(new AnalysisFinding(FindingTone.Info, "Installs services", string.Join(", ", msi.Services)));

                if (msi.RelatedProducts.Count > 0)
                {
                    findings.Add(new AnalysisFinding(FindingTone.Info, $"{msi.RelatedProducts.Count} related package(s) installed",
                        "Other packages share this product's upgrade code — often language packs or add-ons. They "
                        + "have their own entries and keep their own files."));
                }

                break;

            case InstallerTechnology.WindowsInstaller:
                findings.Add(new AnalysisFinding(FindingTone.Warning, "Windows Installer record unavailable",
                    "This is registered as an MSI package, but Windows Installer does not know the product or its "
                    + "cached package is gone. The leftover scan will rely on names and paths instead."));
                break;

            case InstallerTechnology.InnoSetup:
                findings.Add(new AnalysisFinding(FindingTone.Good, "Inno Setup uninstaller",
                    profile.Inno is { } inno
                        ? $"Its log lists {inno.Files.Count:N0} files and folders and {inno.RegistryKeys.Count:N0} registry keys it "
                          + "installed. Those were read before uninstalling so each can be checked afterwards. Anything the "
                          + "program created later — settings in AppData, for one — is not in the log."
                        : "Inno Setup logs what it installs, but the log could not be read. Settings the program "
                          + "created later are never in the log anyway."));
                break;

            case InstallerTechnology.Nsis:
                findings.Add(new AnalysisFinding(FindingTone.Info, "NSIS uninstaller",
                    "NSIS keeps no record of what it installed; the uninstaller removes whatever its author "
                    + "listed. It copies itself to your Temp folder and relaunches from there, so Uproot "
                    + "follows the relaunched copy rather than assuming it finished when the first process exits."));
                break;

            case InstallerTechnology.WixBurn:
                findings.Add(new AnalysisFinding(FindingTone.Info, "WiX bundle",
                    "A bootstrapper that installed a chain of packages. Uninstalling it removes each package in turn."));
                break;

            case InstallerTechnology.Squirrel:
                findings.Add(new AnalysisFinding(FindingTone.Info, "Squirrel installer",
                    "Removes the per-user app folder. Settings in AppData\\Roaming usually survive it."));
                break;

            case InstallerTechnology.ChromiumSetup:
                findings.Add(new AnalysisFinding(FindingTone.Info, "Chromium-based installer",
                    "Browsing data — bookmarks, history, passwords — lives in the profile's User Data folder and is "
                    + "kept unless you tick the option to delete it in the uninstaller's own dialog."));
                break;

            case InstallerTechnology.InstallShield:
                findings.Add(new AnalysisFinding(FindingTone.Info, "InstallShield",
                    "InstallShield setups have no reliable unattended uninstall without a response file, so its own "
                    + "dialogs will ask the questions."));
                break;

            case InstallerTechnology.ClickToRun:
                findings.Add(new AnalysisFinding(FindingTone.Info, "Office Click-to-Run",
                    "Office removes itself through its own service. Microsoft's Support and Recovery Assistant is "
                    + "the tool for a removal that has gone wrong."));
                break;
        }

        if (profile.RegisteredCommand is { IsMsiMaintenanceMode: true })
        {
            findings.Add(new AnalysisFinding(FindingTone.Info, "The registered command opens the maintenance wizard",
                "Its entry runs msiexec /I, which offers Modify and Repair before Remove. Uproot runs msiexec /X "
                + "instead, which goes straight to removal."));
        }

        if (!profile.UninstallerMissing)
        {
            findings.Add(profile.QuietCommand is { } quiet
                ? new AnalysisFinding(FindingTone.Good, "Can run unattended", quiet.Display)
                : new AnalysisFinding(FindingTone.Info, "No unattended mode known", "You will answer the uninstaller's own questions."));
        }

        if (program.Scope == ProgramScope.User && ElevationState.IsElevated)
        {
            findings.Add(new AnalysisFinding(FindingTone.Warning, "Per-user entry, elevated session",
                "This command comes from a registry key the account itself can change, and Uproot is running "
                + "as administrator, so it will run with administrator rights too."));
        }

        if (profile.InstallLocation is null && !profile.UninstallerMissing)
        {
            findings.Add(new AnalysisFinding(FindingTone.Info, "Install folder not recorded",
                "The entry does not say where the program lives and it could not be worked out, so the scan "
                + "will rely on names."));
        }

        return findings;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
