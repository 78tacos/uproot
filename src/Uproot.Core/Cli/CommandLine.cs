namespace Uproot.Core.Cli;

/// <summary>Exit codes, chosen so a script can branch on the result without parsing output.</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>A scan found leftovers that could be removed, and nothing was removed.</summary>
    public const int LeftoversFound = 1;

    public const int UsageError = 64;
    public const int Failed = 70;

    /// <summary>128 + SIGINT, the shell convention for "interrupted by Ctrl+C".</summary>
    public const int Interrupted = 130;
}

/// <summary>A parsed command line.</summary>
public sealed record CommandLineOptions
{
    public CliVerb Verb { get; init; } = CliVerb.Gui;

    public bool Json { get; init; }
    public bool Quiet { get; init; }

    /// <summary>The program named by analyze, leftovers or uninstall.</summary>
    public string? ProgramQuery { get; init; }

    /// <summary>--mode: how hard the leftover scan looks.</summary>
    public ScanMode ScanMode { get; init; } = ScanMode.Moderate;

    /// <summary>--all-users: include every profile on the PC in the leftover scan.</summary>
    public bool AllUsers { get; init; }

    /// <summary>--unattended: run the program's uninstaller without its dialogs, where it has a way to.</summary>
    public bool Unattended { get; init; }

    /// <summary>--clean: remove the recommended leftovers after scanning. Nothing is removed without it.</summary>
    public bool Clean { get; init; }

    /// <summary>--restore-point: create a System Restore point before uninstalling.</summary>
    public bool RestorePoint { get; init; }

    /// <summary>--force: let leftovers --clean remove a program that is still installed.</summary>
    public bool Force { get; init; }

    /// <summary>--publisher: for leftovers of a program no longer listed, the publisher to search by too.</summary>
    public string? Publisher { get; init; }

    /// <summary>--folder: for leftovers of a program no longer listed, the folder it was installed in.</summary>
    public string? InstallFolder { get; init; }

    /// <summary>restore: which past removal to undo — a backup folder's name, a unique start of one, or "latest".</summary>
    public string? RestoreQuery { get; init; }

    public string? Error { get; init; }
}

public enum CliVerb
{
    /// <summary>No command: start the window.</summary>
    Gui,
    Help,
    Version,

    /// <summary>List installed programs, as Apps &amp; Features would.</summary>
    Programs,

    /// <summary>Analyse a program's uninstaller without running it.</summary>
    Analyze,

    /// <summary>Scan for a program's leftovers without changing anything, unless --clean is given.</summary>
    Leftovers,

    /// <summary>Run a program's uninstaller, then scan for what it left behind.</summary>
    Uninstall,

    /// <summary>List past removals that can be undone.</summary>
    Backups,

    /// <summary>Put back what a past removal took.</summary>
    Restore
}

public static class CommandLineParser
{
    /// <summary>
    /// Parses the arguments. Anything unrecognised is an error rather than being ignored: a typo
    /// in a script should fail loudly, not quietly do something else.
    ///
    /// Commands are plain words (<c>uproot leftovers "Old Tool"</c>). The <c>--programs</c>,
    /// <c>--leftovers</c> and similar forms WGUpdater's wgu used are accepted as well.
    /// </summary>
    public static CommandLineOptions Parse(string[] args)
    {
        if (args.Length == 0) return new CommandLineOptions();

        var verb = CliVerb.Gui;
        var json = false;
        var quiet = false;
        string? programQuery = null;
        string? publisher = null;
        string? installFolder = null;
        string? restoreQuery = null;
        var scanMode = ScanMode.Moderate;
        var allUsers = false;
        var unattended = false;
        var clean = false;
        var restorePoint = false;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var word = arg.ToLowerInvariant();

            // A command word only counts as the first argument, so a program called "Backups"
            // can still be named: uproot leftovers Backups.
            if (i == 0 && CommandFor(word) is { } command)
            {
                switch (command)
                {
                    case CliVerb.Help:
                        return new CommandLineOptions { Verb = CliVerb.Help };

                    case CliVerb.Version:
                        return new CommandLineOptions { Verb = CliVerb.Version };

                    case CliVerb.Analyze or CliVerb.Leftovers or CliVerb.Uninstall:
                        if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                            return Fail($"{arg} needs a program name, for example: uproot {Name(command)} \"Adobe Acrobat\"");

                        programQuery = args[++i];
                        break;

                    case CliVerb.Restore:
                        if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                            return Fail("restore needs a backup's name from uproot backups, or \"latest\".");

                        restoreQuery = args[++i];
                        break;
                }

                verb = command;
                continue;
            }

            switch (word)
            {
                case "--help" or "-h" or "-?" or "/?":
                    return new CommandLineOptions { Verb = CliVerb.Help };

                case "--version" or "-v":
                    return new CommandLineOptions { Verb = CliVerb.Version };

                case "--mode":
                    if (i + 1 >= args.Length)
                        return Fail("--mode needs safe, moderate or expert.");

                    if (!Enum.TryParse(args[++i], ignoreCase: true, out scanMode) || !Enum.IsDefined(scanMode))
                        return Fail($"Unknown scan mode '{args[i]}'. Use safe, moderate or expert.");
                    break;

                case "--publisher":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                        return Fail("--publisher needs a name.");

                    publisher = args[++i];
                    break;

                case "--folder":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                        return Fail("--folder needs the folder the program was installed in.");

                    installFolder = args[++i];
                    break;

                case "--json":
                    json = true;
                    break;

                case "--quiet" or "-q":
                    quiet = true;
                    break;

                case "--all-users":
                    allUsers = true;
                    break;

                case "--unattended":
                    unattended = true;
                    break;

                case "--clean":
                    clean = true;
                    break;

                case "--restore-point":
                    restorePoint = true;
                    break;

                case "--force":
                    force = true;
                    break;

                // The window's own switch: restarting as administrator passes it.
                case "--restarted" when i == 0:
                    return new CommandLineOptions();

                default:
                    return Fail(i == 0
                        ? $"Unknown command '{arg}'. Run uproot help to see what is available."
                        : $"Unrecognised option '{arg}'. Run uproot help to see what is available.");
            }
        }

        if (verb == CliVerb.Gui)
            return Fail("No command given. Run uproot help to see what is available.");

        if (clean && verb is not (CliVerb.Leftovers or CliVerb.Uninstall))
            return Fail("--clean only applies to leftovers and uninstall.");

        if (publisher is not null && verb != CliVerb.Leftovers)
            return Fail("--publisher only applies to leftovers.");

        if (installFolder is not null && verb != CliVerb.Leftovers)
            return Fail("--folder only applies to leftovers.");

        if (force && !(verb == CliVerb.Leftovers && clean))
            return Fail("--force only applies to leftovers --clean.");

        if ((unattended || restorePoint) && verb != CliVerb.Uninstall)
            return Fail("--unattended and --restore-point only apply to uninstall.");

        if ((allUsers || scanMode != ScanMode.Moderate) && verb is not (CliVerb.Leftovers or CliVerb.Uninstall))
            return Fail("--mode and --all-users only apply to leftovers and uninstall.");

        return new CommandLineOptions
        {
            Verb = verb,
            Json = json,
            Quiet = quiet,
            ProgramQuery = programQuery,
            Publisher = publisher,
            InstallFolder = installFolder,
            RestoreQuery = restoreQuery,
            ScanMode = scanMode,
            AllUsers = allUsers,
            Unattended = unattended,
            Clean = clean,
            RestorePoint = restorePoint,
            Force = force
        };
    }

    private static CliVerb? CommandFor(string word) => word switch
    {
        "help" => CliVerb.Help,
        "version" => CliVerb.Version,
        "programs" or "list" or "--programs" => CliVerb.Programs,
        "analyze" or "analyse" or "--analyze" or "--analyse" => CliVerb.Analyze,
        "leftovers" or "--leftovers" => CliVerb.Leftovers,
        "uninstall" or "--uninstall-program" => CliVerb.Uninstall,
        "backups" or "--backups" => CliVerb.Backups,
        "restore" or "--restore" => CliVerb.Restore,
        _ => null
    };

    private static string Name(CliVerb verb) => verb.ToString().ToLowerInvariant();

    private static CommandLineOptions Fail(string message) =>
        new() { Verb = CliVerb.Help, Error = message };

    public const string Usage = """
        Uproot — uninstall programs, then remove what their uninstallers leave behind.

        USAGE
          Uproot.exe                         Start the window.
          uproot <command> [options]         Run one command and exit.

        COMMANDS
          programs [--json]                  List installed programs, as Apps & Features does,
                                             including Microsoft Store apps.
          analyze <name> [--json]            Examine a program's uninstaller without running it:
                                             what built it, what it will run, whether it can run
                                             unattended, and what it recorded installing.
          leftovers <name> [options]         Scan for what a program left behind. Changes nothing
                                             unless --clean is given. Works for a program that is
                                             no longer listed too; add --publisher, and --folder
                                             with the folder it was installed in, to help it.
          uninstall <name> [options]         Run the program's own uninstaller, wait for it to
                                             finish, then scan for leftovers.
          backups [--json]                   List past removals, newest first.
          restore <name>|latest [--json]     Put back what one removal took: registry keys and
                                             values, tasks, services, PATH entries and firewall
                                             rules from its backups, and files from the Recycle Bin.
          help                               Show this text.
          version                            Print the version, build number and commit.

        SCAN OPTIONS (leftovers and uninstall)
          --mode safe|moderate|expert        How hard to look (default moderate). Safe finds only
                                             what is provably the program's; expert adds partial
                                             matches and a registry-wide search, never pre-selected.
          --all-users                        Include every profile on this PC. Needs an elevated
                                             prompt.
          --clean                            Remove the recommended leftovers: registry keys are
                                             backed up to .reg files, files go to the Recycle Bin.
          --force                            With leftovers --clean, allow it for a program that is
                                             still installed — a forced removal, for when its own
                                             uninstaller is broken.
          --unattended                       uninstall only: run the uninstaller without its
                                             dialogs, where it has a known way to.
          --restore-point                    uninstall only: create a System Restore point first.
                                             Needs an elevated prompt.
          Protected items are never removed from the command line. The window can, after a
          warning, because someone is there to read it.

        OUTPUT
          --json                   Emit JSON instead of text, for scripting.
          --quiet, -q              Print nothing; rely on the exit code.

        EXIT CODES
          0   The command succeeded; for leftovers, nothing removable was found.
          1   leftovers found something removable and --clean was not given.
          64  Bad usage.
          70  The command failed.
          130 Stopped with Ctrl+C.

        EXAMPLES
          uproot programs --json > programs.json
          uproot analyze "Adobe Acrobat"
          uproot leftovers "Adobe Acrobat" --mode expert --json > leftovers.json
          uproot uninstall "Adobe Acrobat" --all-users --clean
          uproot leftovers "Old Tool" --folder "C:\Tools\OldTool"
          uproot restore latest

        Uproot writes nothing until something is removed. Removals keep their backups under
        %LOCALAPPDATA%\Uproot\Backups, or for an administrator under
        %ProgramData%\Uproot Backups. Past removals WGUpdater made are listed and restored too.
        """;
}
