using System.Windows;
using System.Windows.Controls;
using Accounting.ViewModels;

namespace Accounting.Views.Components;

public partial class MasterDataWorkspaceControl : UserControl
{
    public MasterDataWorkspaceControl()
    {
        InitializeComponent();
    }

    private void AccountModalText_OnChanged(object sender, TextChangedEventArgs e)
    {
        NotifyViewModel();
    }

    private void AccountModalSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        NotifyViewModel();
    }

    private void AccountModalCheck_OnChanged(object sender, RoutedEventArgs e)
    {
        NotifyViewModel();
    }

    private void NotifyViewModel()
    {
        if (DataContext is MasterDataViewModel viewModel)
        {
            viewModel.NotifyAccountFormChanged();
        }
    }
}
