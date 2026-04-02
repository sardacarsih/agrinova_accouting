using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
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

    public ICommand ExportReportCommand { get; private set; } = null!;

    public ObservableCollection<ManagedStockEntry> StockPositionReportRows { get; } = new();
    public ObservableCollection<StockMovementReportRow> MovementReportRows { get; } = new();
    public ObservableCollection<StockValuationRow> ValuationReportRows { get; } = new();
    public ObservableCollection<InventoryTransactionHistoryRow> TransactionHistoryRows { get; } = new();
    public ObservableCollection<InventoryStockCardRow> StockCardRows { get; } = new();
    public ObservableCollection<InventoryStockOpnameReportRow> StockOpnameReportRows { get; } = new();
    public ObservableCollection<ManagedStockEntry> LowStockAlertRows { get; } = new();
    public ObservableCollection<ManagedInventorySyncRun> SyncRunRows { get; } = new();
    public ObservableCollection<ManagedInventorySyncItemLog> SyncItemLogRows { get; } = new();
    public ObservableCollection<InventoryOutboundCompareRow> OutboundCompareRows { get; } = new();
    public ObservableCollection<InventoryReportOption> ReportTypes { get; } = new()
    {
        new() { Code = "stock_position", Label = "Laporan Stok" },
        new() { Code = "movement", Label = "Laporan Mutasi" },
        new() { Code = "valuation", Label = "Laporan Nilai Persediaan" },
        new() { Code = "transaction_history", Label = "Histori Transaksi" },
        new() { Code = "stock_card", Label = "Kartu Stok" },
        new() { Code = "stock_opname", Label = "Laporan Stok Opname" },
        new() { Code = "low_stock", Label = "Alert Stok Minimum" },
        new() { Code = "sync_runs", Label = "Histori Sync Run" },
        new() { Code = "sync_items", Label = "Histori Sync Item" },
        new() { Code = "lk_outbound_compare", Label = "Compare LK Oracle vs Transfer" }
    };

    public string SelectedReportType
    {
        get => _selectedReportType;
        set
        {
            if (!SetProperty(ref _selectedReportType, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanExportReport));
            OnPropertyChanged(nameof(ExportReportTooltip));
        }
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

    public bool CanExportReport => !IsBusy && _canExportReports && GetCurrentReportRowCount() > 0;

    public string ExportReportTooltip
    {
        get
        {
            if (!_canExportReports)
            {
                return "Anda tidak memiliki izin export laporan inventori.";
            }

            if (IsBusy)
            {
                return "Sedang memproses laporan. Tunggu hingga selesai.";
            }

            return GetCurrentReportRowCount() > 0
                ? "Export laporan inventori aktif ke Excel."
                : "Tampilkan laporan terlebih dahulu sebelum export.";
        }
    }

    private void InitializeReportsCommands()
    {
        GenerateReportCommand = new RelayCommand(() => _ = GenerateReportAsync());
        ExportReportCommand = new RelayCommand(() => _ = ExportReportAsync());
    }

    private async Task GenerateReportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Generating report...";

            switch (SelectedReportType)
            {
                case "stock_position":
                    var stockPosition = await _accessControlService.GetStockPositionReportAsync(_companyId, _locationId);
                    ReplaceCollection(StockPositionReportRows, stockPosition);
                    StatusMessage = $"Laporan posisi stok: {stockPosition.Count} baris.";
                    break;

                case "movement":
                    var movement = await _accessControlService.GetStockMovementReportAsync(
                        _companyId,
                        _locationId,
                        ReportDateFrom,
                        ReportDateTo);
                    ReplaceCollection(MovementReportRows, movement);
                    StatusMessage = $"Laporan mutasi stok: {movement.Count} baris.";
                    break;

                case "valuation":
                    var valuation = await _accessControlService.GetStockValuationReportAsync(_companyId, _locationId);
                    ReplaceCollection(ValuationReportRows, valuation);
                    StatusMessage = $"Laporan nilai persediaan: {valuation.Count} baris.";
                    break;

                case "transaction_history":
                    var transactionHistory = await _accessControlService.GetInventoryTransactionHistoryAsync(
                        _companyId,
                        _locationId,
                        ReportDateFrom,
                        ReportDateTo);
                    ReplaceCollection(TransactionHistoryRows, transactionHistory);
                    StatusMessage = $"Histori transaksi inventory: {transactionHistory.Count} baris.";
                    break;

                case "stock_card":
                    var stockCard = await _accessControlService.GetInventoryStockCardAsync(
                        _companyId,
                        _locationId,
                        ReportDateFrom,
                        ReportDateTo);
                    ReplaceCollection(StockCardRows, stockCard);
                    StatusMessage = $"Kartu stok inventory: {stockCard.Count} baris.";
                    break;

                case "stock_opname":
                    var stockOpname = await _accessControlService.GetStockOpnameReportAsync(
                        _companyId,
                        _locationId,
                        ReportDateFrom,
                        ReportDateTo);
                    ReplaceCollection(StockOpnameReportRows, stockOpname);
                    StatusMessage = $"Laporan stok opname: {stockOpname.Count} baris.";
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
                    var mismatchCount = compareRows.Count(row => !string.Equals(row.MatchStatus, "MATCH", StringComparison.OrdinalIgnoreCase));
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
            OnPropertyChanged(nameof(CanExportReport));
            OnPropertyChanged(nameof(ExportReportTooltip));
        }
    }

    private Task ExportReportAsync()
    {
        if (!CanExportReport)
        {
            StatusMessage = ExportReportTooltip;
            return Task.CompletedTask;
        }

        var reportOption = ReportTypes.FirstOrDefault(option => string.Equals(option.Code, SelectedReportType, StringComparison.OrdinalIgnoreCase));
        var safeReportCode = string.IsNullOrWhiteSpace(reportOption?.Code)
            ? "INVENTORY_REPORT"
            : reportOption!.Code.Trim().ToUpperInvariant();

        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"INVENTORY_{safeReportCode}_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return Task.CompletedTask;
        }

        try
        {
            IsBusy = true;
            var (sheetName, rows) = BuildSelectedReportExportPayload();
            _inventoryReportXlsxService.Export(dialog.FileName, sheetName, rows);
            StatusMessage = $"Laporan inventori berhasil di-export ke {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "ExportInventoryReportFailed", ex.Message);
            StatusMessage = string.IsNullOrWhiteSpace(ex.Message) ? "Gagal export laporan inventori." : ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanExportReport));
            OnPropertyChanged(nameof(ExportReportTooltip));
        }

        return Task.CompletedTask;
    }

    private (string SheetName, IReadOnlyCollection<IReadOnlyCollection<object?>> Rows) BuildSelectedReportExportPayload()
    {
        List<IReadOnlyCollection<object?>> rows = SelectedReportType switch
        {
            "stock_position" => BuildStockPositionExportRows(),
            "movement" => BuildMovementExportRows(),
            "valuation" => BuildValuationExportRows(),
            "transaction_history" => BuildTransactionHistoryExportRows(),
            "stock_card" => BuildStockCardExportRows(),
            "stock_opname" => BuildStockOpnameExportRows(),
            "low_stock" => BuildLowStockExportRows(),
            "sync_runs" => BuildSyncRunExportRows(),
            "sync_items" => BuildSyncItemExportRows(),
            "lk_outbound_compare" => BuildOutboundCompareExportRows(),
            _ => throw new InvalidOperationException("Jenis laporan inventori tidak dikenali.")
        };

        var sheetName = ReportTypes.FirstOrDefault(option => string.Equals(option.Code, SelectedReportType, StringComparison.OrdinalIgnoreCase))?.Label
            ?? "Inventory Report";
        return (sheetName, rows);
    }

    private int GetCurrentReportRowCount() => SelectedReportType switch
    {
        "stock_position" => StockPositionReportRows.Count,
        "movement" => MovementReportRows.Count,
        "valuation" => ValuationReportRows.Count,
        "transaction_history" => TransactionHistoryRows.Count,
        "stock_card" => StockCardRows.Count,
        "stock_opname" => StockOpnameReportRows.Count,
        "low_stock" => LowStockAlertRows.Count,
        "sync_runs" => SyncRunRows.Count,
        "sync_items" => SyncItemLogRows.Count,
        "lk_outbound_compare" => OutboundCompareRows.Count,
        _ => 0
    };

    private List<IReadOnlyCollection<object?>> BuildStockPositionExportRows()
    {
        var rows = CreateExportRows("Kode Item", "Nama Item", "UoM", "Lokasi", "Gudang", "Qty");
        rows.AddRange(StockPositionReportRows.Select(row => CreateExportRow(
            row.ItemCode,
            row.ItemName,
            row.Uom,
            row.LocationName,
            row.WarehouseName,
            row.Qty)));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildMovementExportRows()
    {
        var rows = CreateExportRows("Kode Item", "Nama Item", "UoM", "Opening", "Masuk", "Keluar", "Adjust", "Closing");
        rows.AddRange(MovementReportRows.Select(row => CreateExportRow(
            row.ItemCode,
            row.ItemName,
            row.Uom,
            row.OpeningQty,
            row.InQty,
            row.OutQty,
            row.AdjustmentQty,
            row.ClosingQty)));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildValuationExportRows()
    {
        var rows = CreateExportRows("Kode Item", "Nama Item", "UoM", "Qty", "Avg Cost", "Nilai");
        rows.AddRange(ValuationReportRows.Select(row => CreateExportRow(
            row.ItemCode,
            row.ItemName,
            row.Uom,
            row.Qty,
            row.AvgCost,
            row.TotalValue)));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildTransactionHistoryExportRows()
    {
        var rows = CreateExportRows("Tanggal", "Dokumen", "Tipe", "Status", "Gudang", "Gudang Tujuan", "Kode Item", "Nama Item", "UoM", "Qty", "Effect Qty", "Ref No", "Deskripsi");
        rows.AddRange(TransactionHistoryRows.Select(row => CreateExportRow(
            row.TxDate.ToString("yyyy-MM-dd"),
            row.DocumentNo,
            row.DocumentType,
            row.Status,
            row.WarehouseName,
            row.DestinationWarehouseName,
            row.ItemCode,
            row.ItemName,
            row.Uom,
            row.Qty,
            row.EffectQty,
            row.ReferenceNo,
            row.Description)));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildStockCardExportRows()
    {
        var rows = CreateExportRows("Tanggal", "Dokumen", "Tipe", "Gudang", "Kode Item", "Nama Item", "UoM", "Opening", "Masuk", "Keluar", "Adjust", "Balance", "Ref No", "Deskripsi");
        rows.AddRange(StockCardRows.Select(row => CreateExportRow(
            row.TxDate.ToString("yyyy-MM-dd"),
            row.DocumentNo,
            row.DocumentType,
            row.WarehouseName,
            row.ItemCode,
            row.ItemName,
            row.Uom,
            row.OpeningQty,
            row.InQty,
            row.OutQty,
            row.AdjustmentQty,
            row.BalanceQty,
            row.ReferenceNo,
            row.Description)));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildStockOpnameExportRows()
    {
        var rows = CreateExportRows("Tanggal Opname", "No Opname", "Status", "Gudang", "Kode Item", "Nama Item", "UoM", "Qty Sistem", "Qty Aktual", "Selisih", "Deskripsi", "Catatan");
        rows.AddRange(StockOpnameReportRows.Select(row => CreateExportRow(
            row.OpnameDate.ToString("yyyy-MM-dd"),
            row.OpnameNo,
            row.Status,
            row.WarehouseName,
            row.ItemCode,
            row.ItemName,
            row.Uom,
            row.SystemQty,
            row.ActualQty,
            row.DifferenceQty,
            row.Description,
            row.Notes)));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildLowStockExportRows()
    {
        var rows = CreateExportRows("Kode Item", "Nama Item", "UoM", "Lokasi", "Gudang", "Qty");
        rows.AddRange(LowStockAlertRows.Select(row => CreateExportRow(
            row.ItemCode,
            row.ItemName,
            row.Uom,
            row.LocationName,
            row.WarehouseName,
            row.Qty)));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildSyncRunExportRows()
    {
        var rows = CreateExportRows("Run ID", "Direction", "Status", "Started", "Ended", "Total", "Success", "Failed", "Message");
        rows.AddRange(SyncRunRows.Select(row => CreateExportRow(
            row.Id,
            row.Direction,
            row.Status,
            row.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            row.EndedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            row.TotalItems,
            row.SuccessItems,
            row.FailedItems,
            row.Message)));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildSyncItemExportRows()
    {
        var rows = CreateExportRows("Log ID", "Run ID", "Direction", "Item Code", "Category", "Operation", "Result", "Error", "Logged At");
        rows.AddRange(SyncItemLogRows.Select(row => CreateExportRow(
            row.Id,
            row.SyncRunId,
            row.Direction,
            row.ItemCode,
            row.CategoryCode,
            row.Operation,
            row.Result,
            row.ErrorMessage,
            row.LoggedAt.ToString("yyyy-MM-dd HH:mm:ss"))));
        return rows;
    }

    private List<IReadOnlyCollection<object?>> BuildOutboundCompareExportRows()
    {
        var rows = CreateExportRows("Tanggal", "Kode Item", "Nama Item", "Kode Gudang", "Gudang Tujuan", "Qty LK Oracle", "Qty Transfer", "Selisih", "Status");
        rows.AddRange(OutboundCompareRows.Select(row => CreateExportRow(
            row.TxDate.ToString("yyyy-MM-dd"),
            row.ItemCode,
            row.ItemName,
            row.WarehouseCode,
            row.WarehouseName,
            row.QtyLkOracle,
            row.QtyTransferInternal,
            row.QtyDiff,
            row.MatchStatus)));
        return rows;
    }

    private static List<IReadOnlyCollection<object?>> CreateExportRows(params object?[] header)
    {
        return new List<IReadOnlyCollection<object?>> { CreateExportRow(header) };
    }

    private static IReadOnlyCollection<object?> CreateExportRow(params object?[] values) => values;
}
