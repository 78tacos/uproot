using Uproot.Core.Elevation;

namespace Uproot.Core.Cli;

/// <summary>uproot self-install and self-uninstall, from a shell.</summary>
public static class SetupCommands
{
    public static int SelfInstall(CommandLineOptions options)
    {
        var talk = !options.Quiet;
        var source = AppContext.BaseDirectory;
        var installed = Installation.FindInstalled();

        // Run from the installed folder, this only puts the entry, shortcut and PATH right again.
        var repairing = Installation.IsRunningInstalled();

        if (!repairing && Installation.ProblemWithFolder(source) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return ExitCodes.UsageError;
        }

        if (talk && installed is not null && !repairing)
            Console.Error.WriteLine($"Replacing {installed.Build?.Display ?? "the installed copy"} with {BuildInfo.Current.Display}.");

        InstallOutcome outcome;
        if (ElevationState.IsElevated)
        {
            outcome = Installation.InstallForEveryone(repairing ? [] : Installation.FromFolder(source));
        }
        else
        {
            if (Installation.CurrentAppPath() is not { } app)
            {
                Console.Error.WriteLine($"{Installation.AppFile} is not beside this program, so there is nothing to run as administrator.");
                return ExitCodes.UsageError;
            }

            if (talk) Console.Error.WriteLine("Asking for administrator rights…");
            outcome = Installation.RunElevated(install: true, app, options.Quiet);
        }

        return Report(outcome, talk);
    }

    public static int SelfUninstall(CommandLineOptions options)
    {
        var talk = !options.Quiet;

        if (Installation.FindInstalled() is not { } installed)
        {
            if (talk) Console.WriteLine("Uproot is not installed for everyone on this PC, so there is nothing to remove.");
            return ExitCodes.Success;
        }

        InstallOutcome outcome;
        if (ElevationState.IsElevated)
        {
            outcome = Installation.UninstallForEveryone();
        }
        else
        {
            // The installed copy does the removal, not this one: it is the copy only
            // administrators can change.
            if (talk) Console.Error.WriteLine("Asking for administrator rights…");
            outcome = Installation.RunElevated(install: false, installed.ExecutablePath, options.Quiet);
        }

        return Report(outcome, talk);
    }

    private static int Report(InstallOutcome outcome, bool talk)
    {
        if (talk)
        {
            (outcome.Succeeded ? Console.Out : Console.Error).WriteLine(outcome.Message);
            foreach (var note in outcome.Notes) Console.Error.WriteLine($"note: {note}");
        }

        return outcome.Succeeded ? ExitCodes.Success : ExitCodes.Failed;
    }
}
