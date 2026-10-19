using System.Diagnostics;
using System.IO;
using System.Windows;
using Uproot.Core;
using Uproot.Core.Elevation;

namespace Uproot.App;

/// <summary>
/// Installing for everyone, removing that install, and restarting as administrator, with the
/// dialogs each needs. The work itself is <see cref="Installation"/>'s.
/// </summary>
internal static class InstallFlow
{
    private const string Caption = "Uproot";

    // ---- The elevated half ----------------------------------------------------

    /// <summary>
    /// Uproot.exe --install-elevated|--uninstall-elevated [--quiet] [--then-restart]: the part that
    /// needs administrator rights, started through the prompt by the copy that asked. It reads
    /// nothing from the command line but those switches, installs the folder it is running from,
    /// and reports through its exit code; it shows what went wrong itself, unless --quiet.
    /// </summary>
    public static int RunElevatedStep(string[] args)
    {
        var install = args[0].Equals(Installation.InstallElevatedSwitch, StringComparison.OrdinalIgnoreCase);
        var rest = args.Skip(1).Select(a => a.ToLowerInvariant()).ToList();
        var quiet = rest.Remove("--quiet");
        var thenRestart = rest.Remove(Installation.ThenRestartSwitch);

        if (rest.Count > 0) return Installation.ExitCodes.BadSource;
        if (!ElevationState.IsElevated) return Installation.ExitCodes.NotElevated;

        InstallOutcome outcome;
        if (install)
        {
            var source = AppContext.BaseDirectory;
            var repairing = Installation.IsRunningInstalled();

            if (!repairing && Installation.ProblemWithFolder(source) is { } problem)
            {
                if (!quiet) MessageBox.Show(problem, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                return Installation.ExitCodes.BadSource;
            }

            outcome = Installation.InstallForEveryone(repairing ? [] : Installation.FromFolder(source));
            Installation.Log($"{(outcome.Succeeded ? "OK  " : "FAIL")} {outcome.Message} {string.Join(" ", outcome.Notes)}".TrimEnd());
        }
        else
        {
            outcome = Installation.UninstallForEveryone();
        }

        if (!quiet && (!outcome.Succeeded || outcome.Notes.Count > 0))
        {
            MessageBox.Show(string.Join("\n\n", [outcome.Message, .. outcome.Notes]), Caption, MessageBoxButton.OK,
                outcome.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // One administrator prompt, not two: this process already has the rights, so the copy it
        // just installed starts with them.
        if (install && outcome.Succeeded && thenRestart) StartInstalled();

        return outcome.ExitCode;
    }

    // ---- Installing from the window ---------------------------------------------

    /// <summary>
    /// Installs this copy for everyone, after saying what that does. True when it was installed.
    /// <paramref name="thenRestart"/> has the elevated half start the installed copy as
    /// administrator straight afterwards, so restarting as administrator takes one prompt.
    /// </summary>
    public static async Task<bool> InstallAsync(Window? owner, bool askFirst = true, bool thenRestart = false)
    {
        var source = AppContext.BaseDirectory;

        if (Installation.ProblemWithFolder(source) is { } problem)
        {
            Show(owner, problem, MessageBoxImage.Warning);
            return false;
        }

        if (askFirst && Ask(owner, InstallQuestion(), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;

        InstallOutcome outcome;
        if (ElevationState.IsElevated)
        {
            outcome = await Task.Run(() => Installation.InstallForEveryone(Installation.FromFolder(source)));
            if (outcome.Succeeded && thenRestart) StartInstalled();
        }
        else if (Installation.CurrentAppPath() is { } app)
        {
            outcome = await Task.Run(() => Installation.RunElevated(install: true, app, quiet: false, thenRestart));
        }
        else
        {
            outcome = new InstallOutcome(false, $"{Installation.AppFile} is not in {source}.");
        }

        if (!outcome.Succeeded)
        {
            Show(owner, outcome.Message, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private static string InstallQuestion()
    {
        var installed = Installation.FindInstalled();
        var head = installed is null
            ? $"Install Uproot {BuildInfo.Current.Display} for everyone on this PC?"
            : $"Replace the installed Uproot {installed.Build?.Display ?? ""} with {BuildInfo.Current.Display}?".Replace("  ", " ");

        return head + "\n\n"
               + $"It goes in {Installation.Folder}, which only administrators can change, with a Start menu shortcut, "
               + "an Apps & Features entry, and the folder on PATH so \"uproot\" works in any shell. From then on, "
               + "restarting as administrator uses that copy.\n\n"
               + "Windows will ask for administrator rights.";
    }

    /// <summary>Starts the installed copy: as administrator when this process is, otherwise as you.</summary>
    public static bool StartInstalled()
    {
        if (Installation.FindInstalled() is not { } installed) return false;

        try
        {
            var startInfo = new ProcessStartInfo(installed.ExecutablePath) { UseShellExecute = true, WorkingDirectory = Installation.Folder };
            startInfo.ArgumentList.Add("--restarted");
            using var _ = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ---- Restarting as administrator ------------------------------------------------

    /// <summary>
    /// What every window's Restart as admin does. The installed copy is the one restarted when
    /// there is one, since only administrators can change it. A copy in a folder you can change
    /// says so first and offers to install; one in a folder you cannot change is restarted as is.
    /// </summary>
    public static async Task<RelaunchOutcome> RestartAsAdministratorAsync()
    {
        if (ElevationState.IsElevated) return new RelaunchOutcome(false, "Uproot is already running as administrator.");

        if (Installation.IsRunningInstalled()) return ElevationState.RelaunchAsAdministrator();

        if (Installation.FindInstalled() is { } installed)
        {
            var outcome = ElevationState.RelaunchAsAdministrator(installed.ExecutablePath);
            return outcome.Started
                ? outcome with { Message = $"Restarting the copy installed in {Installation.Folder} with administrator rights…" }
                : outcome;
        }

        var folder = Path.GetDirectoryName(Environment.ProcessPath);
        if (folder is null || !IsWritableByYou(folder)) return ElevationState.RelaunchAsAdministrator();

        var answer = Ask(ActiveWindow(),
            $"This copy of Uproot is in {folder}, which programs running as you can change. Restarting it as "
            + "administrator would run whatever is in that folder with full rights.\n\n"
            + $"Install Uproot for everyone first? It goes in {Installation.Folder}, which only administrators can change, "
            + "and from then on that copy is the one restarted as administrator.\n\n"
            + "Yes: install, then restart from there (one administrator prompt).\n"
            + "No: restart this copy anyway.\n"
            + "Cancel: do neither.",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        switch (answer)
        {
            case MessageBoxResult.No:
                return ElevationState.RelaunchAsAdministrator();

            case MessageBoxResult.Yes:
                return await InstallAsync(ActiveWindow(), askFirst: false, thenRestart: true)
                    ? new RelaunchOutcome(true, $"Installed in {Installation.Folder}; restarting from there with administrator rights…")
                    : new RelaunchOutcome(false, "Uproot was not installed, so it was not restarted.");

            default:
                return new RelaunchOutcome(false, "Not restarted.", Cancelled: true);
        }
    }

    /// <summary>
    /// Whether this unelevated process can create a file in <paramref name="folder"/>. If it can,
    /// so can anything else running as you. The probe deletes itself as it closes.
    /// </summary>
    internal static bool IsWritableByYou(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".uproot-write-check-{Guid.NewGuid():N}.tmp");
            using var _ = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // ---- From Apps & Features, or a shortcut ------------------------------------------

    /// <summary>Uproot.exe self-uninstall [--quiet]: what Apps &amp; Features runs.</summary>
    public static int SelfUninstall(bool quiet)
    {
        if (Installation.FindInstalled() is not { } installed)
        {
            if (!quiet) Show(null, "Uproot is not installed for everyone on this PC, so there is nothing to remove.", MessageBoxImage.Information);
            return Installation.ExitCodes.Done;
        }

        if (!quiet)
        {
            while (WindowIsOpen())
            {
                if (Ask(null, "Uproot is open. Close it, then press OK to carry on removing it.", MessageBoxButton.OKCancel,
                        MessageBoxImage.Information) != MessageBoxResult.OK)
                    return Installation.ExitCodes.Failed;
            }

            var confirmed = Ask(null,
                "Remove Uproot from this PC?\n\n"
                + $"This deletes {Installation.Folder}, the Start menu shortcut, the PATH entry and the Apps & Features entry. "
                + "Backups of past removals are kept, in %LOCALAPPDATA%\\Uproot\\Backups and %ProgramData%\\Uproot Backups.",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirmed != MessageBoxResult.Yes) return Installation.ExitCodes.Failed;
        }

        var outcome = ElevationState.IsElevated
            ? Installation.UninstallForEveryone()
            : Installation.RunElevated(install: false, installed.ExecutablePath, quiet);

        if (!quiet)
        {
            Show(null, string.Join("\n\n", [outcome.Message, .. outcome.Notes]),
                outcome.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        return outcome.ExitCode;
    }

    /// <summary>Uproot.exe self-install [--quiet]: the same as the window's Install button, for a shortcut or a script.</summary>
    public static int SelfInstall(bool quiet)
    {
        var source = AppContext.BaseDirectory;
        var repairing = Installation.IsRunningInstalled();

        if (!repairing && Installation.ProblemWithFolder(source) is { } problem)
        {
            if (!quiet) Show(null, problem, MessageBoxImage.Warning);
            return Installation.ExitCodes.BadSource;
        }

        if (!quiet && Ask(null, InstallQuestion(), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return Installation.ExitCodes.Failed;

        InstallOutcome outcome;
        if (ElevationState.IsElevated)
            outcome = Installation.InstallForEveryone(repairing ? [] : Installation.FromFolder(source));
        else if (Installation.CurrentAppPath() is { } app)
            outcome = Installation.RunElevated(install: true, app, quiet);
        else
            outcome = new InstallOutcome(false, $"{Installation.AppFile} is not in {source}.");

        if (!quiet)
        {
            Show(null, string.Join("\n\n", [outcome.Message, .. outcome.Notes]),
                outcome.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        return outcome.ExitCode;
    }

    // ---- Pieces ------------------------------------------------------------------------

    /// <summary>The window, if one is open: the single-instance mutex only exists while it is.</summary>
    private static bool WindowIsOpen()
    {
        try
        {
            if (!Mutex.TryOpenExisting(App.SingleInstanceMutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static Window? ActiveWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    private static void Show(Window? owner, string text, MessageBoxImage image) => Ask(owner, text, MessageBoxButton.OK, image);

    private static MessageBoxResult Ask(Window? owner, string text, MessageBoxButton buttons, MessageBoxImage image) =>
        owner is not null
            ? MessageBox.Show(owner, text, Caption, buttons, image)
            : MessageBox.Show(text, Caption, buttons, image);
}
