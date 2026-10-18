using System.IO;
using System.Windows;
using System.Windows.Controls;
using Uproot.Core;

namespace Uproot.App;

public partial class LeftoverSearchWindow : Window
{
    private readonly string _folderHint;

    public LeftoverSearchWindow()
    {
        InitializeComponent();
        _folderHint = FolderNote.Text;
        Loaded += (_, _) => NameBox.Focus();
    }

    public string ProgramName => NameBox.Text.Trim();

    public string? Publisher => string.IsNullOrWhiteSpace(PublisherBox.Text) ? null : PublisherBox.Text.Trim();

    public string? InstallFolder => PathRules.Normalize(FolderBox.Text);

    /// <summary>
    /// A name too generic to match anything would only produce an empty scan, and a folder that is
    /// not there, or holds far more than one program, cannot be anyone's install folder: both are
    /// refused up front.
    /// </summary>
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var nameOk = !ProgramNames.IsGeneric(ProgramNames.CleanDisplayName(ProgramName));
        var folderProblem = FolderProblem();

        FolderNote.Text = folderProblem ?? _folderHint;
        FolderNote.Foreground = folderProblem is null
            ? (System.Windows.Media.Brush)FindResource("TextMuted")
            : (System.Windows.Media.Brush)FindResource("ToneWarningFg");

        SearchButton.IsEnabled = nameOk && folderProblem is null;
    }

    private string? FolderProblem()
    {
        if (string.IsNullOrWhiteSpace(FolderBox.Text)) return null;

        var folder = InstallFolder;
        if (folder is null || !Directory.Exists(folder)) return "That folder does not exist.";
        if (UninstallerAnalyzer.IsTooBroad(folder)) return "That folder holds far more than one program, so it cannot be one program's install folder.";

        return null;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Where was the program installed?" };
        if (InstallFolder is { } current && Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    /// <summary>
    /// A named folder is checked against the whole safety policy before going on — every installed
    /// program, standard and known folders wherever they have been moved — since it will be offered
    /// for removal along with everything pointing into it. Too slow to run on every keystroke.
    /// </summary>
    private void OnSearch(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FolderBox.Text) && UninstallSession.CheckInstallFolder(FolderBox.Text) is { } problem)
        {
            FolderNote.Text = problem;
            FolderNote.Foreground = (System.Windows.Media.Brush)FindResource("ToneWarningFg");
            return;
        }

        DialogResult = true;
        Close();
    }
}
