using System.Windows;
using System.Windows.Controls;
using Uproot.App.ViewModels;

namespace Uproot.App;

public partial class UninstallerWindow : Window
{
    private readonly UninstallerViewModel _viewModel;

    public UninstallerWindow(UninstallerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.SessionRequested += OnSessionRequested;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    /// <summary>
    /// Opens the uninstall flow for one program or several. It is modal because the program list
    /// behind it goes stale the moment an uninstaller runs, and is reloaded when the session closes.
    /// </summary>
    private void OnSessionRequested(object? sender, SessionRequest request)
    {
        var session = new UninstallSessionViewModel(request.Analyses, request.LeftoversOnly);
        session.ExitRequested += (_, _) => (Application.Current as App)?.ExitForRestart();

        var window = new UninstallSessionWindow(session) { Owner = this };
        window.ShowDialog();

        _viewModel.AfterSession(session.ChangedAnything);
    }

    private void OnProgramSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedRows(((ListBox)sender).SelectedItems);

    private async void OnSearchByName(object sender, RoutedEventArgs e)
    {
        var dialog = new LeftoverSearchWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        await _viewModel.SearchByNameAsync(dialog.ProgramName, dialog.Publisher, dialog.InstallFolder);
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var restore = new RestoreViewModel();
        restore.ExitRequested += (_, _) => (Application.Current as App)?.ExitForRestart();

        var window = new RestoreWindow(restore) { Owner = this };
        window.ShowDialog();

        // Putting things back can put programs back in the list.
        _viewModel.AfterSession(changedAnything: true);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the program list",
            FileName = $"Installed programs {DateTime.Now:yyyy-MM-dd}.csv",
            DefaultExt = ".csv",
            Filter = "CSV file (*.csv)|*.csv",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _viewModel.ExportTo(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not save the list: {ex.Message}", "Export the program list", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
