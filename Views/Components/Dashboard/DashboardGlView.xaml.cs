using System.Windows;
using System.Windows.Controls;
using Accounting.Services;
using Accounting.ViewModels;

namespace Accounting.Views.Components.Dashboard;

public partial class DashboardGlView : UserControl
{
    public DashboardGlView()
    {
        InitializeComponent();
    }

    private void JournalItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountingDashboardViewModel viewModel ||
            sender is not FrameworkElement element ||
            element.Tag is not DashboardJournalItem item)
        {
            return;
        }

        viewModel.OpenJournal(item);
    }
}
