using System.Collections.ObjectModel;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private int _totalItemCount;
    private decimal _totalStockValue;
    private int _lowStockCount;
    private int _pendingTransactionCount;

    public ICommand RefreshDashboardCommand { get; private set; } = null!;

    public ObservableCollection<ManagedStockTransactionSummary> DashboardRecentTransactions { get; } = new();
    public ObservableCollection<ManagedStockEntry> DashboardLowStockItems { get; } = new();

    public int TotalItemCount
    {
        get => _totalItemCount;
        private set => SetProperty(ref _totalItemCount, value);
    }

    public decimal TotalStockValue
    {
        get => _totalStockValue;
        private set => SetProperty(ref _totalStockValue, value);
    }

    public int LowStockCount
    {
        get => _lowStockCount;
        private set => SetProperty(ref _lowStockCount, value);
    }

    public int PendingTransactionCount
    {
        get => _pendingTransactionCount;
        private set => SetProperty(ref _pendingTransactionCount, value);
    }

    private void InitializeDashboardCommands()
    {
        RefreshDashboardCommand = new RelayCommand(() => _ = LoadDashboardAsync());
    }

    private async Task LoadDashboardAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            StatusMessage = "Memuat dashboard inventori...";

            var data = await _accessControlService.GetInventoryDashboardDataAsync(_companyId, _locationId);

            TotalItemCount = data.TotalItemCount;
            TotalStockValue = data.TotalStockValue;
            LowStockCount = data.LowStockCount;
            PendingTransactionCount = data.PendingTransactionCount;
            ReplaceCollection(DashboardRecentTransactions, data.RecentTransactions);
            ReplaceCollection(DashboardLowStockItems, data.LowStockItems);

            StatusMessage = "Dashboard inventori siap.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "LoadDashboardFailed", ex.Message);
            StatusMessage = "Gagal memuat dashboard.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
