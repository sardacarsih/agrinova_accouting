using System.Windows.Controls;
using System.Windows;
using Accounting.ViewModels;

namespace Accounting.Views.Components;

public partial class MasterDataCostCentersTabControl : UserControl
{
    public MasterDataCostCentersTabControl()
    {
        InitializeComponent();
    }

    private void TreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MasterDataViewModel viewModel)
        {
            viewModel.SetSelectedEstateHierarchyItem(e.NewValue);
        }
    }
}
