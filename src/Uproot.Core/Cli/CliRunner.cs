namespace Uproot.Core.Cli;

/// <summary>
/// Runs Uproot without a window.
///
/// Progress goes to stderr and results to stdout, so <c>--json</c> can be redirected into a file
/// without progress chatter contaminating it.
/// </summary>
public static class CliRunner
{
    /// <summary>
    /// Runs one command. The first Ctrl+C cancels it cleanly — for an uninstall that means
    /// stopping the wait for the uninstaller, which keeps running on its own. A second Ctrl+C is
    /// left to terminate the process at once.
    /// </summary>
    public static async Task<int> RunAsync(CommandLineOptions options)
    {
        using var interrupt = new CancellationTokenSource();

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (interrupt.IsCancellationRequested) return;   // second press: default, exit now

            e.Cancel = true;
            interrupt.Cancel();
            Console.Error.WriteLine();
            Console.Error.WriteLine("Stopping… press Ctrl+C again to exit immediately.");
        };

        Console.CancelKeyPress += onCancel;

        try
        {
            return await RunCoreAsync(options, interrupt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
        {
            return ExitCodes.Interrupted;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<int> RunCoreAsync(CommandLineOptions options, CancellationToken ct)
    {
        switch (options.Verb)
        {
            case CliVerb.Help:
                if (options.Error is { } error)
                {
                    Console.Error.WriteLine(error);
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(CommandLineParser.Usage);
                    return ExitCodes.UsageError;
                }

                Console.WriteLine($"Uproot {BuildInfo.Current.DisplayWithCommit}");
                Console.WriteLine();
                Console.WriteLine(CommandLineParser.Usage);
                return ExitCodes.Success;

            case CliVerb.Version:
                Console.WriteLine(Version());
                return ExitCodes.Success;

            case CliVerb.Programs:
                return await ProgramCommands.ListAsync(options, ct).ConfigureAwait(false);

            case CliVerb.Analyze:
                return await ProgramCommands.AnalyzeAsync(options, ct).ConfigureAwait(false);

            case CliVerb.Backups:
                return ProgramCommands.Backups(options);

            case CliVerb.Restore:
                return await ProgramCommands.RestoreAsync(options, ct).ConfigureAwait(false);

            case CliVerb.Leftovers:
                return await ProgramCommands.LeftoversAsync(options, ct).ConfigureAwait(false);

            case CliVerb.Uninstall:
                return await ProgramCommands.UninstallAsync(options, ct).ConfigureAwait(false);

            default:
                Console.Error.WriteLine(CommandLineParser.Usage);
                return ExitCodes.UsageError;
        }
    }

    /// <summary>
    /// "Uproot 0.9.0 (build 3, abc1234)". When uproot.com sits beside an Uproot.exe of a
    /// different build — a half-copied folder — that is said too.
    /// </summary>
    private static string Version()
    {
        var line = $"Uproot {BuildInfo.Current.DisplayWithCommit}";

        var folder = Path.GetDirectoryName(Environment.ProcessPath);
        var app = folder is null ? null : Path.Combine(folder, "Uproot.exe");

        if (app is not null && !string.Equals(app, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            && BuildInfo.FromFile(app) is { } appBuild && !appBuild.IsSameBuild(BuildInfo.Current))
            line += $"{Environment.NewLine}warning: {app} is {appBuild.DisplayWithCommit}";

        return line;
    }
}
