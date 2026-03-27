using System.Windows;
using System.Windows.Controls;
using Accounting.ViewModels;

namespace Accounting.Views.Components.Dashboard;

public partial class DashboardAlertView : UserControl
{
    public DashboardAlertView()
    {
        InitializeComponent();
    }

    private void AlertAction_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountingDashboardViewModel viewModel ||
            sender is not FrameworkElement element ||
            element.Tag is not DashboardAlertRowViewModel alert)
        {
            return;
        }

        viewModel.ExecuteAlertAction(alert);
    }
}
