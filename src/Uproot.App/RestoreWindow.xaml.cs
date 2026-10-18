using System.Windows;
using Uproot.App.ViewModels;

namespace Uproot.App;

public partial class RestoreWindow : Window
{
    public RestoreWindow(RestoreViewModel viewModel, string? select = null)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.ConfirmRestore = message =>
            MessageBox.Show(this, message, "Restore removed items", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        Loaded += (_, _) =>
        {
            viewModel.Load();
            if (select is not null) viewModel.Select(select);
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
