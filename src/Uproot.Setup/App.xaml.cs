using System.Windows;
using Microsoft.Win32;
using Uproot.Core;
using Uproot.Core.Elevation;

namespace Uproot.Setup;

public partial class App : Application
{
    private const string Usage = """
        Uproot-Setup.exe [--quiet]

        Installs Uproot for everyone on this PC, in Program Files, or updates the installed copy.

          --quiet, /quiet, /silent   No window. Reports through the exit code, and writes
                                     setup.log in the install folder.
          --help, /?                 Show this text.

        Exit codes: 0 installed; 1 installed, but the Start menu shortcut or PATH entry could
        not be added; 64 bad usage or an incomplete setup; 70 failed.

        To remove Uproot, use Apps & Features, or run:
          "C:\Program Files\Uproot\Uproot.exe" self-uninstall --quiet
        """;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var quiet = false;
        foreach (var arg in e.Args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--quiet" or "/quiet" or "-q" or "/q" or "--silent" or "/silent" or "/s":
                    quiet = true;
                    break;

                case "--help" or "-h" or "/?" or "-?":
                    MessageBox.Show(Usage, "Uproot setup", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown(Installation.ExitCodes.Done);
                    return;

                default:
                    if (!quiet) MessageBox.Show($"Unrecognised option '{arg}'.\n\n{Usage}", "Uproot setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown(Installation.ExitCodes.BadSource);
                    return;
            }
        }

        if (quiet)
        {
            Shutdown(RunQuietly());
            return;
        }

        UseWindowsTheme();

        var window = new SetupWindow();
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        window.Show();
    }

    /// <summary>The same install as the window, with nobody to ask: for Intune, ConfigMgr or a script.</summary>
    private static int RunQuietly()
    {
        if (!ElevationState.IsElevated) return Installation.ExitCodes.NotElevated;

        try
        {
            using var payload = Payload.Open();
            var outcome = Installation.InstallForEveryone(payload.Files);

            Installation.Log($"Uproot setup {BuildInfo.Current.Display}, quiet: {(outcome.Succeeded ? "OK" : "FAIL")} {outcome.Message} {string.Join(" ", outcome.Notes)}".TrimEnd());
            return outcome.ExitCode;
        }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.IOException or UnauthorizedAccessException)
        {
            Installation.Log($"Uproot setup {BuildInfo.Current.Display}, quiet: FAIL {ex.Message}");
            return ex is System.IO.InvalidDataException ? Installation.ExitCodes.BadSource : Installation.ExitCodes.Failed;
        }
    }

    /// <summary>Light or dark as Windows' app theme is; the palette defaults to dark until told.</summary>
    private void UseWindowsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value && value == 0) return;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return;
        }

        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("Themes/Light.xaml", UriKind.Relative) };
    }
}
