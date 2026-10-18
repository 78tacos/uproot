using System.Text.Json;
using Uproot.Core.Elevation;
using Uproot.Core;

namespace Uproot.Core.Cli;

/// <summary>
/// The command-line side of the uninstaller: list, analyse, scan for leftovers, uninstall.
///
/// Scanning never changes anything. Removal needs --clean, and then takes only what the window
/// would have pre-selected — certain and likely items with no caution against them — because a
/// script has nobody to review the possible ones.
/// </summary>
internal static class ProgramCommands
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ---- --programs --------------------------------------------------------

    public static async Task<int> ListAsync(CommandLineOptions options, CancellationToken ct)
    {
        IReadOnlyList<InstalledProgram> store;
        try
        {
            store = await StoreApps.ReadAllAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            if (!options.Quiet) Console.Error.WriteLine($"note: Microsoft Store apps could not be listed: {ex.Message}");
            store = [];
        }

        var programs = ProgramInventory.ReadVisible()
            .Concat(store)
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(programs.Select(p => new
            {
                id = p.Id,
                name = p.DisplayName,
                version = p.DisplayVersion,
                publisher = p.Publisher,
                scope = p.Scope.ToString(),
                owner = p.Owner,
                installedFor = p.InstalledFor,
                is32Bit = p.Is32Bit,
                technology = UninstallerAnalyzer.QuickDetect(p).ToString(),
                installLocation = p.InstallLocation,
                installDate = p.InstallDate?.ToString("yyyy-MM-dd"),
                sizeKb = p.EstimatedSizeKb,
                uninstallString = p.UninstallString,
                quietUninstallString = p.QuietUninstallString
            }), Indented));

            return ExitCodes.Success;
        }

        if (options.Quiet) return ExitCodes.Success;

        Console.WriteLine($"{programs.Count} programs");
        Console.WriteLine(new string('─', 30));

        foreach (var program in programs)
        {
            var technology = UninstallerAnalyzer.Describe(UninstallerAnalyzer.QuickDetect(program));
            Console.WriteLine($"  {Truncate(program.DisplayName, 46),-46}  {Truncate(program.DisplayVersion ?? "", 16),-16}  "
                              + $"{Truncate(program.InstalledFor, 16),-16}  {technology}");
        }

        return ExitCodes.Success;
    }

    // ---- --analyze ---------------------------------------------------------

    public static async Task<int> AnalyzeAsync(CommandLineOptions options, CancellationToken ct)
    {
        if (await ResolveAsync(options, allowUnregistered: false, ct).ConfigureAwait(false) is not { } program) return ExitCodes.UsageError;

        var analysis = UninstallerAnalyzer.Analyze(program);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(AnalysisJson(analysis), Indented));
            return ExitCodes.Success;
        }

        if (!options.Quiet) WriteAnalysis(analysis);
        return ExitCodes.Success;
    }

    // ---- --leftovers -------------------------------------------------------

    public static async Task<int> LeftoversAsync(CommandLineOptions options, CancellationToken ct)
    {
        if (await ResolveAsync(options, allowUnregistered: true, ct).ConfigureAwait(false) is not { } program) return ExitCodes.UsageError;

        if (options.InstallFolder is not null && program.IsRegistered)
        {
            Console.Error.WriteLine($"--folder is for a program that is no longer listed, and {program.DisplayName} is. "
                                    + "Its own entry already says where it is installed.");
            return ExitCodes.UsageError;
        }

        var session = await UninstallSession.PrepareAsync(program, ct).ConfigureAwait(false);
        var stillInstalled = program.IsRegistered && session.IsStillInstalled();

        // The window pre-selects nothing for a program that is still installed; a script has to
        // say outright that it means to break one.
        if (stillInstalled && options.Clean && !options.Force)
        {
            Console.Error.WriteLine($"{program.DisplayName} is still installed, so --clean would force-remove it rather than "
                                    + "uninstall it. Use --uninstall-program, or add --force if its uninstaller is broken.");
            return ExitCodes.UsageError;
        }

        if (!options.Quiet && !options.Json)
        {
            if (!program.IsRegistered)
                Console.Error.WriteLine($"No installed program matches \"{program.DisplayName}\"; searching by name only.");
            else if (stillInstalled)
                Console.Error.WriteLine($"{program.DisplayName} is still installed. This lists what a forced removal would take; use --uninstall-program to uninstall it properly.");
        }

        return await ScanAndMaybeCleanAsync(session, options, stillInstalled, run: null, ct).ConfigureAwait(false);
    }

    // ---- --uninstall-program ----------------------------------------------

    public static async Task<int> UninstallAsync(CommandLineOptions options, CancellationToken ct)
    {
        if (await ResolveAsync(options, allowUnregistered: false, ct).ConfigureAwait(false) is not { } program) return ExitCodes.UsageError;

        // Elevated as someone else, your own per-user programs belong to a different account.
        if (ProgramOwner.WhyNotHere(program) is { } notHere)
        {
            Console.Error.WriteLine(notHere);
            return ExitCodes.Failed;
        }

        var session = await UninstallSession.PrepareAsync(program, ct).ConfigureAwait(false);
        var analysis = session.Analysis;
        var talk = !options.Quiet && !options.Json;

        if (!analysis.CanUninstall)
        {
            Console.Error.WriteLine(analysis.UninstallerMissing
                ? $"{program.DisplayName}'s uninstaller is missing. Use --leftovers \"{program.DisplayName}\" --clean to remove what is left."
                : $"{program.DisplayName} has no uninstall command.");
            return ExitCodes.Failed;
        }

        if (options.Unattended && analysis.QuietCommand is null)
        {
            Console.Error.WriteLine($"{program.DisplayName}'s uninstaller has no known unattended mode; run it without --unattended to answer its questions.");
            return ExitCodes.UsageError;
        }

        if (program.Scope == ProgramScope.Machine && !ElevationState.IsElevated && talk)
        {
            Console.Error.WriteLine("Not elevated: the uninstaller will ask for administrator rights itself, but leftovers in "
                                    + "Program Files, ProgramData and HKLM can only be removed from an elevated prompt.");
        }

        if (options.RestorePoint)
        {
            if (talk) Console.Error.WriteLine("Creating a System Restore point…");
            var restore = await RestorePoint.CreateAsync($"Before uninstalling {program.DisplayName}", ct).ConfigureAwait(false);
            if (talk) Console.Error.WriteLine(restore.Message);
        }

        if (talk) Console.Error.WriteLine($"Running {(options.Unattended ? analysis.QuietCommand : analysis.InteractiveCommand)!.Display}");

        var progress = talk ? new Progress<string>(s => Console.Error.Write($"\r{Truncate(s, 76),-76}")) : null;

        // Ctrl+C stops the waiting, never the uninstaller: killing it part-way would leave the
        // program half removed.
        var run = await session.RunUninstallerAsync(options.Unattended, progress, ct).ConfigureAwait(false);

        if (talk)
        {
            Console.Error.WriteLine("\r" + new string(' ', 78) + "\r");
            Console.Error.WriteLine(run.Message);
        }

        if (!run.Started || run.Cancelled) return ExitCodes.Failed;

        var stillInstalled = session.IsStillInstalled();
        if (stillInstalled && talk)
            Console.Error.WriteLine($"{program.DisplayName} still looks installed, so leftovers will not be removed.");

        var clean = options with { Clean = options.Clean && !stillInstalled };
        return await ScanAndMaybeCleanAsync(session, clean, stillInstalled, run, ct).ConfigureAwait(false);
    }

    // ---- Shared ------------------------------------------------------------

    private static async Task<int> ScanAndMaybeCleanAsync(
        UninstallSession session, CommandLineOptions options, bool stillInstalled, UninstallerRunResult? run, CancellationToken ct)
    {
        var talk = !options.Quiet && !options.Json;

        if (options.AllUsers && !ElevationState.IsElevated && talk)
            Console.Error.WriteLine("--all-users needs an elevated prompt; scanning only your own profile.");

        var progress = talk ? new Progress<string>(s => Console.Error.Write($"\r{Truncate(s, 76),-76}")) : null;

        var result = await session.ScanAsync(new LeftoverScanOptions
        {
            Mode = options.ScanMode,
            IncludeAllProfiles = options.AllUsers,
            ProgramStillInstalled = stillInstalled
        }, progress, ct).ConfigureAwait(false);

        if (talk) Console.Error.Write("\r" + new string(' ', 78) + "\r");

        // The list goes out before anything is removed, so an interrupted clean-up still shows
        // what it was working through.
        if (talk) WriteLeftovers(session.Program, result, options.ScanMode);

        RemovalReport? report = null;

        if (options.Clean)
        {
            var chosen = result.Items.Where(i => i.Recommended).ToList();

            if (chosen.Count > 0)
            {
                if (talk)
                {
                    Console.WriteLine();
                    Console.Error.WriteLine($"Removing the {chosen.Count} recommended item(s)…");
                }

                report = await session.RemoveAsync(chosen, progress: null, ct: ct).ConfigureAwait(false);
            }
        }

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                program = session.Program.DisplayName,
                registered = session.Program.IsRegistered,
                stillInstalled,
                mode = options.ScanMode.ToString(),
                elevated = ElevationState.IsElevated,
                uninstaller = run is null ? null : new { run.Message, run.ExitCode, run.RestartRequired, run.StoppedWaiting },
                elapsedSeconds = Math.Round(result.Elapsed.TotalSeconds, 1),
                timings = result.Timings.ToDictionary(t => t.Stage, t => Math.Round(t.Elapsed.TotalSeconds, 2)),
                notes = result.Notes,
                items = result.Items.Select(i => new
                {
                    kind = i.Kind.ToString(),
                    category = i.Category.ToString(),
                    path = i.DisplayPath,
                    confidence = i.Confidence.ToString(),
                    mode = i.Mode.ToString(),
                    recommended = i.Recommended,
                    onlyIfEmpty = i.OnlyIfEmpty,
                    owner = i.Owner,
                    sizeBytes = i.SizeBytes,
                    needsElevation = i.NeedsElevation,
                    reason = i.Reason,
                    caution = i.Caution,
                    protectedBecause = i.ProtectedBecause
                }),
                removal = report is null ? null : new
                {
                    backupFolder = report.BackupFolder,
                    outcomes = report.Outcomes.Select(o => new { path = o.Item.DisplayPath, status = o.Status.ToString(), message = o.Message })
                }
            }, Indented));
        }
        else if (talk && report is not null)
        {
            WriteReport(report);
        }

        if (report is not null) return report.Failed > 0 ? ExitCodes.Failed : ExitCodes.Success;

        // Like a scan: 0 means checked and clean, 1 means something was found.
        return result.Items.Any(i => !i.IsProtected) ? ExitCodes.LeftoversFound : ExitCodes.Success;
    }

    /// <summary>
    /// Finds the one program a query means. Ambiguity is an error listing the candidates, never a
    /// guess: picking the wrong program to uninstall is not a mistake a script should be able to make.
    /// Store apps are asked about unless a desktop program matches exactly, since listing them
    /// takes a second or two; then both lists are searched together, so an exact Store app name
    /// beats a desktop program that merely contains it ("Microsoft Teams" is not the Teams add-in).
    /// </summary>
    private static async Task<InstalledProgram?> ResolveAsync(CommandLineOptions options, bool allowUnregistered, CancellationToken ct)
    {
        var query = options.ProgramQuery ?? string.Empty;
        var desktop = ProgramInventory.ReadVisible();
        var matches = ProgramInventory.Find(desktop, query);

        var exact = matches.Count > 0 && matches.All(m =>
            m.Id.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || m.KeyName.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || m.DisplayName.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!exact)
        {
            IReadOnlyList<InstalledProgram> store;
            try
            {
                store = await StoreApps.ReadAllAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                if (!options.Quiet) Console.Error.WriteLine($"note: Microsoft Store apps could not be listed: {ex.Message}");
                store = [];
            }

            matches = ProgramInventory.Find(desktop.Concat(store).ToList(), query);
        }

        if (matches.Count == 1) return matches[0];

        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"\"{query}\" matches {matches.Count} programs. Use the exact name, or the entry path from --programs --json:");
            foreach (var match in matches.Take(15)) Console.Error.WriteLine($"  {match.DisplayName}    {match.Id}");
            return null;
        }

        if (allowUnregistered)
        {
            if (options.InstallFolder is { } folder)
            {
                if (UninstallSession.CheckInstallFolder(folder) is { } problem)
                {
                    Console.Error.WriteLine($"--folder {folder}: {problem}");
                    return null;
                }

                return InstalledProgram.ForName(query, options.Publisher, PathRules.Normalize(folder));
            }

            return InstalledProgram.ForName(query, options.Publisher);
        }

        Console.Error.WriteLine($"No installed program matches \"{query}\". Run uproot programs to see the list.");
        return null;
    }

    // ---- --backups and --restore ------------------------------------------

    public static int Backups(CommandLineOptions options)
    {
        var records = RemovalHistory.List();

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(records.Select(r => new
            {
                name = r.Name,
                folder = r.Folder,
                program = r.Manifest.Program,
                removedAt = r.Manifest.RemovedAt,
                elevated = r.Manifest.Elevated,
                removed = r.Removed.Count,
                restorable = r.Restorable.Count,
                oldFormat = r.IsOldFormat
            }), Indented));

            return ExitCodes.Success;
        }

        if (options.Quiet) return ExitCodes.Success;

        if (records.Count == 0)
        {
            Console.WriteLine("No removals have been backed up.");
            return ExitCodes.Success;
        }

        Console.WriteLine($"{records.Count} past removal{(records.Count == 1 ? "" : "s")}, newest first");
        Console.WriteLine(new string('─', 40));

        foreach (var record in records)
        {
            var restorable = record.IsOldFormat ? "older format: restore by hand from the folder" : $"{record.Restorable.Count} can be restored";
            Console.WriteLine($"  {record.Name}");
            Console.WriteLine($"      {record.Manifest.RemovedAt.LocalDateTime:g}  ·  {record.Removed.Count} removed  ·  {restorable}");
        }

        Console.WriteLine();
        Console.WriteLine("Undo one with: uproot restore \"<name>\"   (or uproot restore latest)");
        return ExitCodes.Success;
    }

    public static async Task<int> RestoreAsync(CommandLineOptions options, CancellationToken ct)
    {
        var records = RemovalHistory.List();
        var record = RemovalHistory.Find(records, options.RestoreQuery ?? string.Empty);

        if (record is null)
        {
            Console.Error.WriteLine($"No single backup matches \"{options.RestoreQuery}\". Run uproot backups to see them.");
            return ExitCodes.UsageError;
        }

        var items = record.Restorable;
        var talk = !options.Quiet && !options.Json;

        if (items.Count == 0)
        {
            Console.Error.WriteLine(record.IsOldFormat
                ? $"{record.Name} was made by an earlier version that did not record which backup is which. Its .reg files can be double-clicked to put keys back: {record.Folder}"
                : $"Nothing in {record.Name} can be restored.");
            return ExitCodes.Failed;
        }

        if (talk) Console.Error.WriteLine($"Restoring {items.Count} item(s) from {record.Name}…");

        var report = await UninstallSession.RestoreAsync(record, items, progress: null, ct).ConfigureAwait(false);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                backup = record.Name,
                elevated = ElevationState.IsElevated,
                outcomes = report.Outcomes.Select(o => new
                {
                    kind = o.Item.Kind,
                    path = o.Item.DisplayPath ?? o.Item.Path,
                    status = o.Status.ToString(),
                    message = o.Message
                })
            }, Indented));
        }
        else if (talk)
        {
            foreach (var outcome in report.Outcomes)
            {
                var status = outcome.Status switch
                {
                    RestoreStatus.Restored => "restored",
                    RestoreStatus.RestoredNeedsRestart => "restart ",
                    RestoreStatus.Skipped => "skipped ",
                    RestoreStatus.Refused => "refused ",
                    _ => "failed  "
                };

                Console.WriteLine($"  {status}  {outcome.Item.DisplayPath ?? outcome.Item.Path}");
                if (outcome.Status != RestoreStatus.Restored)
                    foreach (var line in Wrap(outcome.Message, 66)) Console.WriteLine($"{"",12}{line}");
            }

            Console.WriteLine();
            Console.WriteLine($"Restore: {report.Summary}.");
        }

        return report.NotRestored > 0 ? ExitCodes.Failed : ExitCodes.Success;
    }

    private static object AnalysisJson(UninstallerProfile analysis) => new
    {
        program = analysis.Program.DisplayName,
        id = analysis.Program.Id,
        technology = analysis.Technology.ToString(),
        registeredCommand = analysis.RegisteredCommand?.Display,
        interactiveCommand = analysis.InteractiveCommand?.Display,
        quietCommand = analysis.QuietCommand?.Display,
        uninstallerMissing = analysis.UninstallerMissing,
        uninstallerCompany = analysis.UninstallerCompany,
        installLocation = analysis.InstallLocation,
        msi = analysis.Msi is not { } msi ? null : new
        {
            msi.ProductCode, msi.UpgradeCode, msi.LocalPackage, msi.ComponentCount, msi.InstalledComponentCount,
            msi.SharedComponentCount, msi.CustomActionCount, msi.PatchCount, files = msi.Files.Count,
            registry = msi.Registry.Count, msi.Services, relatedProducts = msi.RelatedProducts
        },
        inno = analysis.Inno is not { } inno ? null : new { inno.AppId, inno.AppName, files = inno.Files.Count, registryKeys = inno.RegistryKeys.Count },
        findings = analysis.Findings.Select(f => new { tone = f.Tone.ToString(), f.Title, f.Detail })
    };

    private static void WriteAnalysis(UninstallerProfile analysis)
    {
        var program = analysis.Program;

        Console.WriteLine(program.DisplayName);
        Console.WriteLine(new string('─', Math.Min(78, program.DisplayName.Length + 4)));
        Console.WriteLine($"  Entry:        {program.Id}");
        if (!string.IsNullOrWhiteSpace(program.Summary)) Console.WriteLine($"  Details:      {program.Summary}");
        Console.WriteLine($"  Installer:    {analysis.TechnologyName}");
        if (analysis.RegisteredCommand is { } registered) Console.WriteLine($"  Registered:   {registered.Display}");
        if (analysis.InteractiveCommand is { } interactive && interactive != analysis.RegisteredCommand)
            Console.WriteLine($"  Will run:     {interactive.Display}");
        Console.WriteLine($"  Unattended:   {analysis.QuietCommand?.Display ?? "not available"}");
        Console.WriteLine($"  Installed at: {analysis.InstallLocation ?? "not recorded"}");
        Console.WriteLine();

        foreach (var finding in analysis.Findings)
        {
            var marker = finding.Tone switch { FindingTone.Good => "+", FindingTone.Warning => "!", _ => "-" };
            Console.WriteLine($"  {marker} {finding.Title}");
            foreach (var line in Wrap(finding.Detail, 72)) Console.WriteLine($"      {line}");
        }
    }

    private static void WriteLeftovers(InstalledProgram program, LeftoverScanResult result, ScanMode mode)
    {
        var removable = result.Items.Count(i => !i.IsProtected);
        var headline = $"Leftovers of {program.DisplayName} — {mode} scan, {result.Elapsed.TotalSeconds:0.0}s";

        Console.WriteLine(headline);
        Console.WriteLine(new string('─', Math.Min(78, headline.Length)));

        if (result.Items.Count == 0)
        {
            Console.WriteLine("Nothing found.");
        }
        else
        {
            Console.WriteLine($"{removable} found, {result.RecommendedCount} recommended"
                              + (result.RecommendedBytes > 0 ? $" ({Sizes.Format(result.RecommendedBytes)})" : string.Empty));
            Console.WriteLine();

            LeftoverCategory? category = null;

            foreach (var item in result.Items)
            {
                if (item.Category != category)
                {
                    category = item.Category;
                    Console.WriteLine();
                    Console.WriteLine($"  {item.CategoryName.ToUpperInvariant()} ({result.Items.Count(i => i.Category == category)})");
                }

                var box = item.IsProtected ? "[-]" : item.Recommended ? "[x]" : "[ ]";
                var confidence = item.IsProtected ? "Protected" : item.Confidence.ToString();
                var size = item.SizeBytes is > 0 and var bytes ? $"  ({Sizes.Format(bytes)})" : string.Empty;
                var owner = item.Owner is null ? string.Empty : $"  [{item.Owner}]";

                Console.WriteLine($"  {box} {confidence,-9}  {item.KindName,-23}  {item.DisplayPath}{size}{owner}");
                foreach (var line in Wrap(item.ProtectedBecause ?? item.Reason, 64)) Console.WriteLine($"{"",41}{line}");
                if (item.Caution is { } caution) Console.WriteLine($"{"",41}Caution: {caution}");
            }
        }

        foreach (var note in result.Notes)
        {
            Console.WriteLine();
            foreach (var line in Wrap(note, 76)) Console.Error.WriteLine($"note: {line}");
        }

        if (!ElevationState.IsElevated && result.Items.Any(i => i.NeedsElevation && !i.IsProtected))
            Console.Error.WriteLine("note: some of these need an elevated prompt to remove.");
    }

    private static void WriteReport(RemovalReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"Removal: {report.Summary}.");

        foreach (var outcome in report.Outcomes.Where(o => o.Status is RemovalStatus.Failed or RemovalStatus.Refused))
            Console.Error.WriteLine($"  could not remove {outcome.Item.DisplayPath}: {outcome.Message}");

        if (report.BackupFolder is { } folder)
        {
            Console.WriteLine($"Backups: {folder}");
            Console.WriteLine($"Undo with: uproot restore \"{Path.GetFileName(folder)}\"");
        }
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
