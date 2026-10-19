using System.Windows;
using System.Windows.Threading;
using Uproot.App.Cli;
using Uproot.App.ViewModels;
using Uproot.Core;
using Uproot.Core.Cli;

namespace Uproot.App;

public partial class App : Application
{
    internal const string SingleInstanceMutexName = @"Local\Uproot.SingleInstance";

    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The elevated half of installing or removing Uproot: no window, no console, no mutex.
        if (e.Args.Length > 0 && e.Args[0] is Installation.InstallElevatedSwitch or Installation.UninstallElevatedSwitch)
        {
            Environment.Exit(InstallFlow.RunElevatedStep(e.Args));
            return;
        }

        var commandLine = CommandLineParser.Parse(e.Args);

        // Apps & Features runs Uproot.exe self-uninstall, with no console to talk through, so
        // these two ask and report in dialogs. uproot.com does them in the console.
        if (commandLine.Verb is CliVerb.SelfInstall or CliVerb.SelfUninstall)
        {
            Environment.Exit(commandLine.Verb == CliVerb.SelfInstall
                ? InstallFlow.SelfInstall(commandLine.Quiet)
                : InstallFlow.SelfUninstall(commandLine.Quiet));
            return;
        }

        if (commandLine.Verb != CliVerb.Gui)
        {
            RunCommandLineAndExit(commandLine);
            return;
        }

        // Restarting as administrator starts the new process before this one has released the
        // mutex, so that one waits rather than declaring itself a duplicate.
        var restarted = e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase);

        if (!TryAcquireSingleInstance(restarted))
        {
            MessageBox.Show(
                _otherInstanceIsElevated
                    ? "Uproot is already running with administrator rights. Switch to that window, or close it first."
                    : "Uproot is already running. Switch to that window.",
                "Uproot", MessageBoxButton.OK, MessageBoxImage.Information);

            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ThemeManager.FollowSystem();

        var viewModel = new UninstallerViewModel();
        viewModel.ExitRequested += (_, _) => ExitForRestart();

        var window = new UninstallerWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Runs a command and exits with its status code. A console is borrowed from the calling
    /// shell for the duration. uproot.com is the better way to do this from a shell, because a
    /// shell does not wait for a windowed program; this keeps Uproot.exe usable on its own.
    /// </summary>
    private static void RunCommandLineAndExit(CommandLineOptions options)
    {
        ConsoleHost.Attach();

        int exitCode;
        try
        {
            exitCode = CliRunner.RunAsync(options).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Uproot failed: {ex.Message}");
            exitCode = ExitCodes.Failed;
        }

        ConsoleHost.Release(waitForKey: ConsoleHost.OwnsConsoleWindow);
        Environment.Exit(exitCode);
    }

    /// <summary>Set when the running copy's mutex could not even be opened: it belongs to an elevated process, or another account.</summary>
    private bool _otherInstanceIsElevated;

    private bool TryAcquireSingleInstance(bool waitForPredecessor)
    {
        var deadline = DateTime.UtcNow + (waitForPredecessor ? TimeSpan.FromSeconds(8) : TimeSpan.Zero);

        while (true)
        {
            try
            {
                _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var acquired);
                if (acquired) return true;

                _singleInstance.Dispose();
                _singleInstance = null;
            }
            catch (UnauthorizedAccessException)
            {
                // A copy running as administrator, or as another account, owns the mutex, and this
                // process may not open it. That is "already running", not a reason to crash. It is
                // also what an elevated copy meets while the copy it replaces is still exiting.
                _singleInstance = null;
                _otherInstanceIsElevated = true;
            }

            if (DateTime.UtcNow >= deadline) return false;

            Thread.Sleep(250);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Uproot hit an unexpected error and will keep running.\n\n{e.Exception.Message}",
            "Uproot", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }

    /// <summary>Shuts down so an elevated instance, started by "Restart as administrator", can take over.</summary>
    public void ExitForRestart()
    {
        foreach (var window in Windows.OfType<Window>().Where(w => w != MainWindow).ToList())
            window.Close();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.Detach();

        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }
}
