using System.Windows;
using Accounting.Services;
using Accounting.ViewModels;

namespace Accounting;

public partial class AccessSelectionWindow : Window
{
    private readonly AccessSelectionViewModel _viewModel;

    public AccessSelectionWindow(AccessSelectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    public UserAccessContext? SelectedSessionContext { get; private set; }

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryBuildSessionContext(out var context) || context is null)
        {
            return;
        }

        SelectedSessionContext = context;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

