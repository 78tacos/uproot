using System.Diagnostics;
using System.IO;
using System.Windows;
using Uproot.Core;
using Uproot.Core.Elevation;

namespace Uproot.Setup;

public partial class SetupWindow : Window
{
    private enum Stage { Ready, Working, Installed, Failed }

    private Stage _stage = Stage.Ready;

    public SetupWindow()
    {
        InitializeComponent();
        Describe();
    }

    private void Describe()
    {
        var installed = Installation.FindInstalled();
        var mine = BuildInfo.Current;

        Heading.Text = installed is null ? "Install Uproot" : "Update Uproot";
        BuildLine.Text = $"Version {mine.Display}";
        FolderLine.Text = $"{Installation.Folder}, holding Uproot.exe (the window) and uproot.com (the command line).";

        ExistingLine.Text = installed switch
        {
            null => "Uproot is not installed on this PC yet.",
            { Build: null } => "A copy is installed already, of a version that could not be read. Installing replaces it.",
            { Build: { } build } when build.IsSameBuild(mine) => "This build is installed already. Installing it again puts back anything missing.",
            { Build: { } build } when build.CompareTo(mine) > 0 => $"The installed copy is {build.Display}, newer than this one. Installing replaces it with this older build.",
            { Build: { } build } => $"Replaces the installed {build.Display}. A copy that is open keeps running until it is closed."
        };
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Ready or Stage.Failed:
                await InstallAsync();
                break;

            case Stage.Installed:
                OpenUproot();
                Close();
                break;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_stage != Stage.Working) Close();
    }

    private async Task InstallAsync()
    {
        _stage = Stage.Working;
        ActionButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        ResultPanel.Visibility = Visibility.Visible;
        WorkingBar.Visibility = Visibility.Visible;
        ResultText.Text = $"Installing into {Installation.Folder}…";

        var outcome = await Task.Run(() =>
        {
            try
            {
                using var payload = Payload.Open();
                return Installation.InstallForEveryone(payload.Files);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return new InstallOutcome(false, ex.Message);
            }
        });

        Installation.Log($"Uproot setup {BuildInfo.Current.Display}: {(outcome.Succeeded ? "OK" : "FAIL")} {outcome.Message} {string.Join(" ", outcome.Notes)}".TrimEnd());

        WorkingBar.Visibility = Visibility.Collapsed;
        CloseButton.IsEnabled = true;
        ActionButton.IsEnabled = true;

        if (!outcome.Succeeded)
        {
            _stage = Stage.Failed;
            ResultText.Text = outcome.Message;
            ActionButton.Content = "Try again";
            CloseButton.Content = "Close";
            return;
        }

        _stage = Stage.Installed;
        Heading.Text = "Uproot is installed";
        ResultText.Text = string.Join("\n\n", [outcome.Message, .. outcome.Notes]);
        CloseButton.Content = "Close";

        // Opened from here it would run with this setup's administrator rights. It is started
        // through Explorer instead, as the person signed in — which only works when that is the
        // account this setup runs as.
        if (AccountContext.Current.AsAnotherAccount)
        {
            ResultText.Text += "\n\nOpen it from the Start menu.";
            ActionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ActionButton.Content = "Open Uproot";
        }
    }

    /// <summary>
    /// Starts the installed copy without this setup's rights: Explorer hands the launch to the
    /// shell already running for the person signed in.
    /// </summary>
    private static void OpenUproot()
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(new ProcessStartInfo(explorer, $"\"{Path.Combine(Installation.Folder, Installation.AppFile)}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Could not open Uproot: {ex.Message}\n\nOpen it from the Start menu.", "Uproot setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
