using System.ComponentModel;
using System.Windows;
using Uproot.App.ViewModels;

namespace Uproot.App;

public partial class UninstallSessionWindow : Window
{
    private readonly UninstallSessionViewModel _viewModel;

    public UninstallSessionWindow(UninstallSessionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.ConfirmRemoval = message =>
            MessageBox.Show(this, message, "Remove leftovers", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        viewModel.ConfirmUndo = message =>
            MessageBox.Show(this, message, "Undo the removal", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // No is the default: turning protection off has to be a deliberate second choice.
        viewModel.ConfirmOverride = message =>
            MessageBox.Show(this, message, "Turn protection off?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCount == 0)
        {
            MessageBox.Show(this, "Nothing is selected.", "Remove leftovers", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _viewModel.RemoveAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Closing mid-way is allowed but not silently: the uninstaller carries on regardless, and a
    /// removal stopped part-way leaves some items gone and others not.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            var message = _viewModel.IsRunning
                ? "The uninstaller is still running and will carry on without Uproot. Close anyway? Leftovers will not be scanned for."
                : "Stop and close? Anything already removed stays removed.";

            if (MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _viewModel.StopWaitingCommand.Execute(null);
            _viewModel.CancelWorkCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
