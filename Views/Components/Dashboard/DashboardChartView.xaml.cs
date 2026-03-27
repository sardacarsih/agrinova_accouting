using System.Windows;
using System.Windows.Controls;
using Accounting.Services;
using Accounting.ViewModels;

namespace Accounting.Views.Components.Dashboard;

public partial class DashboardChartView : UserControl
{
    public DashboardChartView()
    {
        InitializeComponent();
    }

    private void TrendDetail_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountingDashboardViewModel viewModel)
        {
            viewModel.RequestTrendDrillDown();
        }
    }

    private void ExpenseItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountingDashboardViewModel viewModel ||
            sender is not FrameworkElement element ||
            element.Tag is not ExpenseAccountBarItem item)
        {
            return;
        }

        viewModel.RequestExpenseAccountDrillDown(item);
    }

    private void AssetDetail_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountingDashboardViewModel viewModel)
        {
            viewModel.RequestAssetDrillDown();
        }
    }

    private void TrendPoint_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountingDashboardViewModel viewModel ||
            sender is not FrameworkElement element ||
            element.Tag is not DashboardLineMarkerPoint marker)
        {
            return;
        }

        viewModel.RequestRevenueDrillDown(marker.Index);
    }

    private void AssetSlice_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not AccountingDashboardViewModel viewModel)
        {
            return;
        }

        viewModel.RequestAssetDrillDown();
    }
}
