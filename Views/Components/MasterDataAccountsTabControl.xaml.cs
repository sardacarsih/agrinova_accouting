using System.Windows;
using System.Windows.Controls;
using Accounting.ViewModels;

namespace Accounting.Views.Components;

public partial class MasterDataAccountsTabControl : UserControl
{
    public MasterDataAccountsTabControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => NotifyViewModel();
    }

    private void AccountEditorText_OnChanged(object sender, TextChangedEventArgs e)
    {
        NotifyViewModel();
    }

    private void AccountEditorSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        NotifyViewModel();
    }

    private void AccountEditorCheck_OnChanged(object sender, RoutedEventArgs e)
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
