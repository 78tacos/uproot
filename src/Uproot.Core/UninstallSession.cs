namespace Uproot.Core;

/// <summary>
/// One program's uninstall, from analysis to clean-up. The window and the command line both drive
/// this, so they behave the same.
///
/// The order matters. Everything about the program is captured by <see cref="PrepareAsync"/>
/// before its uninstaller runs, because afterwards the entry, the Windows Installer record and the
/// executables are gone. What other programs claim is worked out at scan time instead, after the
/// uninstaller, because only then is it certain which of them are still installed.
/// </summary>
public sealed class UninstallSession
{
    private UninstallSession(UninstallerProfile analysis)
    {
        Analysis = analysis;
        Target = UninstallTarget.From(analysis);
    }

    public UninstallerProfile Analysis { get; }
    public UninstallTarget Target { get; }
    public InstalledProgram Program => Analysis.Program;

    public UninstallerRunResult? RunResult { get; private set; }

    public static Task<UninstallSession> PrepareAsync(InstalledProgram program, CancellationToken ct = default) =>
        Task.Run(() => new UninstallSession(UninstallerAnalyzer.Analyze(program, ct)), ct);

    public static UninstallSession FromAnalysis(UninstallerProfile analysis) => new(analysis);

    /// <summary>Runs the program's own uninstaller, interactively or unattended.</summary>
    public async Task<UninstallerRunResult> RunUninstallerAsync(bool quiet, IProgress<string>? progress = null, CancellationToken stopWaiting = default)
    {
        var command = quiet ? Analysis.QuietCommand ?? Analysis.InteractiveCommand : Analysis.InteractiveCommand;

        if (command is null || Analysis.UninstallerMissing)
        {
            RunResult = new UninstallerRunResult { Message = "There is no uninstaller to run. Scan for leftovers instead." };
            return RunResult;
        }

        if (Program.IsStoreApp)
        {
            RunResult = await StoreApps.UninstallAsync(Program, progress, stopWaiting).ConfigureAwait(false);
            return RunResult;
        }

        progress?.Report($"Starting {Path.GetFileName(command.FileName)}…");
        RunResult = await UninstallerRunner.RunAsync(command, progress, stopWaiting).ConfigureAwait(false);
        return RunResult;
    }

    /// <summary>
    /// Whether the program still looks installed: its entry is still there, or Windows Installer
    /// still has the product. A packaged app's repository key goes when Windows removes it.
    /// </summary>
    public bool IsStillInstalled()
    {
        if (Program.IsStoreApp) return Program.Key.Exists();
        if (Program.ProductCode is { } productCode && MsiInterop.IsProductInstalled(productCode)) return true;
        return ProgramInventory.Read(Program.Key, Program.Scope, Program.Is32Bit) is not null;
    }

    /// <summary>Scans the real machine for leftovers.</summary>
    public Task<LeftoverScanResult> ScanAsync(LeftoverScanOptions options, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            progress?.Report("Preparing the scan");

            var context = LeftoverScanContext.ForThisMachine(options.IncludeAllProfiles, out var mounts,
                Program.Scope == ProgramScope.User ? Program.OwnerSid : null);
            using (mounts)
            {
                var safety = CreateSafety(context.ProfileRoots, mounts.Profiles);
                var scanner = new LeftoverScanner(context, safety);
                return scanner.Scan(Target, options, progress, ct);
            }
        }, ct);

    /// <summary>Removes the chosen leftovers, re-checking each against the safety policy first.</summary>
    public Task<RemovalReport> RemoveAsync(IReadOnlyList<LeftoverItem> items, RemovalOptions? options = null,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        RemoveAsync([this], items, options, progress, ct);

    /// <summary>
    /// Removes the leftovers of several programs uninstalled together, in one pass: none of them
    /// claims anything against the others, and a folder they shared goes once all of their things
    /// in it have gone.
    /// </summary>
    public static Task<RemovalReport> RemoveAsync(IReadOnlyList<UninstallSession> sessions, IReadOnlyList<LeftoverItem> items,
        RemovalOptions? options = null, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var profiles = UserProfiles.List();

            // Removing from another account's profile needs that account's programs known too,
            // which means its hive loaded for the duration — but only then.
            var others = profiles.Where(p => !p.IsCurrent).ToList();
            var mine = profiles.FirstOrDefault(p => p.IsCurrent)?.Sid ?? "\0";
            var touchesOthers = items.Any(i =>
                others.Any(p => PathRules.IsWithin(i.Path, p.ProfilePath))
                || (i.RegistryKey is { Hive: Microsoft.Win32.RegistryHive.Users } key
                    && !key.SubKey.StartsWith(mine + @"\", StringComparison.OrdinalIgnoreCase)
                    && !key.SubKey.StartsWith(mine + "_Classes", StringComparison.OrdinalIgnoreCase)));

            using var mounts = touchesOthers ? UserProfiles.MountMissing(profiles) : new UserProfiles.HiveMounts();
            var withHives = touchesOthers ? mounts.Profiles : profiles.ToList();

            // Of several programs, one whose uninstaller was cancelled or failed is still installed
            // and still claims its things against the others'. A single program is always the one
            // being removed — still installed means a forced removal, chosen as such.
            var removing = sessions.Count == 1 ? sessions : sessions.Where(s => !s.IsStillInstalled()).ToList();

            var safety = CreateSafety(removing.Select(s => s.Program.Key), profiles.Select(p => p.ProfilePath), withHives);
            var remover = new LeftoverRemover(safety);

            var name = sessions.Count == 1 ? sessions[0].Program.DisplayName : $"{sessions.Count} programs";
            return remover.Remove(items, (options ?? new RemovalOptions()) with { ProgramName = name }, progress, ct);
        }, ct);

    /// <summary>
    /// The safety policy as of now: every other registered program, including the hidden ones and
    /// those installed just for another account whose profile is loaded, claims its folders and
    /// names. The programs being removed are excluded by their keys, so a second entry for the
    /// same product — a program installed for two accounts has one per hive — still counts as someone else.
    /// </summary>
    private static LeftoverSafety CreateSafety(IEnumerable<RegistryLocation> removing, IEnumerable<string> profileRoots,
        IEnumerable<UserProfile> profilesWithHives)
    {
        var excluded = removing.ToHashSet();

        var otherHives = profilesWithHives
            .Where(p => p.Hive is { } hive && hive.Hive != Microsoft.Win32.RegistryHive.CurrentUser)
            .Select(p => (p.Hive!.Child(ProgramInventory.UninstallPath), ProgramScope.User, false));

        var profiles = profilesWithHives.ToList();

        var others = ProgramInventory.ReadAll(ProgramInventory.DefaultRoots().Concat(otherHives))
            .DistinctBy(p => p.Key)
            .Where(p => !excluded.Contains(p.Key));

        return LeftoverSafety.ForThisMachine(others, profileRoots, profiles.SelectMany(UserProfiles.KnownFolders));
    }

    private LeftoverSafety CreateSafety(IEnumerable<string> profileRoots, IEnumerable<UserProfile> profilesWithHives) =>
        CreateSafety([Program.Key], profileRoots, profilesWithHives);

    /// <summary>
    /// Why a folder someone names as a removed program's install folder cannot be treated as one,
    /// or null when it can. It is offered for removal and everything pointing into it is taken as
    /// the program's, so it has to be a folder the safety policy would let go at all: not a
    /// standard folder or anything holding one, not a known folder wherever it has been moved, not
    /// part of Windows, and not another installed program's.
    /// </summary>
    public static string? CheckInstallFolder(string? folder)
    {
        var normalized = PathRules.Normalize(folder);
        if (normalized is null || !Directory.Exists(normalized)) return "That folder does not exist.";

        if (UninstallerAnalyzer.IsTooBroad(normalized) || ProgramClaim.IsSharedExecutableLocation(normalized + @"\x"))
            return "That folder is not one program's: it holds far more, or belongs to Windows or Temp.";

        var profiles = UserProfiles.List();
        var safety = CreateSafety([], profiles.Select(p => p.ProfilePath), profiles);

        return safety.JudgePath(normalized) is { } verdict
            ? $"That folder cannot be treated as one program's install folder. {verdict.Reason}"
            : null;
    }

    /// <summary>
    /// Puts back what a past removal took. Only the policy's absolute rules apply here — whose
    /// folder something is does not matter when putting it back where it was.
    /// </summary>
    public static Task<RestoreReport> RestoreAsync(RemovalRecord record, IReadOnlyList<ManifestItem> items,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var profiles = UserProfiles.List();
            var safety = LeftoverSafety.ForThisMachine([], profiles.Select(p => p.ProfilePath), profiles.SelectMany(UserProfiles.KnownFolders));
            return new RemovalRestorer(safety).Restore(record, items, progress, ct);
        }, ct);
}
