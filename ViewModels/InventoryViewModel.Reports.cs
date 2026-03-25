using System.Collections.ObjectModel;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private string _selectedReportType = "movement";
    private DateTime _reportDateFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _reportDateTo = DateTime.Today;
    private decimal _lowStockThreshold = 10;

    public ICommand GenerateReportCommand { get; private set; } = null!;

    public ObservableCollection<StockMovementReportRow> MovementReportRows { get; } = new();
    public ObservableCollection<StockValuationRow> ValuationReportRows { get; } = new();
    public ObservableCollection<ManagedStockEntry> LowStockAlertRows { get; } = new();
    public ObservableCollection<ManagedInventorySyncRun> SyncRunRows { get; } = new();
    public ObservableCollection<ManagedInventorySyncItemLog> SyncItemLogRows { get; } = new();
    public ObservableCollection<InventoryOutboundCompareRow> OutboundCompareRows { get; } = new();
    public ObservableCollection<string> ReportTypes { get; } = new() { "movement", "valuation", "low_stock", "sync_runs", "sync_items", "lk_outbound_compare" };

    public string SelectedReportType
    {
        get => _selectedReportType;
        set => SetProperty(ref _selectedReportType, value);
    }

    public DateTime ReportDateFrom
    {
        get => _reportDateFrom;
        set => SetProperty(ref _reportDateFrom, value);
    }

    public DateTime ReportDateTo
    {
        get => _reportDateTo;
        set => SetProperty(ref _reportDateTo, value);
    }

    public decimal LowStockThreshold
    {
        get => _lowStockThreshold;
        set => SetProperty(ref _lowStockThreshold, value);
    }

    private void InitializeReportsCommands()
    {
        GenerateReportCommand = new RelayCommand(() => _ = GenerateReportAsync());
    }

    private async Task GenerateReportAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            StatusMessage = "Generating report...";

            switch (SelectedReportType)
            {
                case "movement":
                    var movement = await _accessControlService.GetStockMovementReportAsync(
                        _companyId, _locationId, ReportDateFrom, ReportDateTo);
                    ReplaceCollection(MovementReportRows, movement);
                    StatusMessage = $"Laporan pergerakan stock: {movement.Count} baris.";
                    break;

                case "valuation":
                    var valuation = await _accessControlService.GetStockValuationReportAsync(_companyId, _locationId);
                    ReplaceCollection(ValuationReportRows, valuation);
                    StatusMessage = $"Laporan valuasi stock: {valuation.Count} baris.";
                    break;

                case "low_stock":
                    var lowStock = await _accessControlService.GetLowStockAlertAsync(_companyId, _locationId, LowStockThreshold);
                    ReplaceCollection(LowStockAlertRows, lowStock);
                    StatusMessage = $"Alert stock rendah: {lowStock.Count} item.";
                    break;

                case "sync_runs":
                    var syncRuns = await _accessControlService.GetInventorySyncRunHistoryAsync(_companyId, 200);
                    ReplaceCollection(SyncRunRows, syncRuns);
                    StatusMessage = $"Histori sync run: {syncRuns.Count} data.";
                    break;

                case "sync_items":
                    var syncItems = await _accessControlService.GetInventorySyncItemLogHistoryAsync(_companyId, 1000);
                    ReplaceCollection(SyncItemLogRows, syncItems);
                    StatusMessage = $"Histori sync item: {syncItems.Count} data.";
                    break;

                case "lk_outbound_compare":
                    var compareRows = await _accessControlService.GetInventoryOutboundCompareReportAsync(
                        _companyId,
                        _locationId,
                        ReportDateFrom,
                        ReportDateTo);
                    ReplaceCollection(OutboundCompareRows, compareRows);
                    var mismatchCount = compareRows.Count(x => !string.Equals(x.MatchStatus, "MATCH", StringComparison.OrdinalIgnoreCase));
                    var matchCount = compareRows.Count - mismatchCount;
                    StatusMessage = $"Compare LK Oracle vs Transfer: total {compareRows.Count} baris, match {matchCount}, mismatch {mismatchCount}.";
                    break;
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "GenerateReportFailed", ex.Message);
            StatusMessage = string.IsNullOrWhiteSpace(ex.Message) ? "Gagal generate report." : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
