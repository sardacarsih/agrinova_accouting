using System.ComponentModel;
using Accounting.Infrastructure.Logging;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private readonly Dictionary<long, decimal> _outboundAutoCostCache = new();
    private readonly HashSet<StockTransactionLineEditor> _stockOutAutoCostSubscribedLines = new();
    private readonly HashSet<StockTransactionLineEditor> _transferAutoCostSubscribedLines = new();

    private void ClearOutboundAutoCostCache()
    {
        _outboundAutoCostCache.Clear();
    }

    private void AttachStockOutLineAutoCostHandlers(IEnumerable<StockTransactionLineEditor> lines)
    {
        foreach (var line in lines)
        {
            AttachStockOutLineAutoCostHandler(line);
        }
    }

    private void ResetStockOutLineAutoCostHandlers()
    {
        foreach (var line in _stockOutAutoCostSubscribedLines.ToList())
        {
            line.PropertyChanged -= OnStockOutLinePropertyChanged;
        }

        _stockOutAutoCostSubscribedLines.Clear();
    }

    private void AttachStockOutLineAutoCostHandler(StockTransactionLineEditor? line)
    {
        if (line is null || !_stockOutAutoCostSubscribedLines.Add(line))
        {
            return;
        }

        line.PropertyChanged += OnStockOutLinePropertyChanged;
    }

    private void DetachStockOutLineAutoCostHandler(StockTransactionLineEditor? line)
    {
        if (line is null || !_stockOutAutoCostSubscribedLines.Remove(line))
        {
            return;
        }

        line.PropertyChanged -= OnStockOutLinePropertyChanged;
    }

    private void AttachTransferLineAutoCostHandlers(IEnumerable<StockTransactionLineEditor> lines)
    {
        foreach (var line in lines)
        {
            AttachTransferLineAutoCostHandler(line);
        }
    }

    private void ResetTransferLineAutoCostHandlers()
    {
        foreach (var line in _transferAutoCostSubscribedLines.ToList())
        {
            line.PropertyChanged -= OnTransferLinePropertyChanged;
        }

        _transferAutoCostSubscribedLines.Clear();
    }

    private void AttachTransferLineAutoCostHandler(StockTransactionLineEditor? line)
    {
        if (line is null || !_transferAutoCostSubscribedLines.Add(line))
        {
            return;
        }

        line.PropertyChanged += OnTransferLinePropertyChanged;
    }

    private void DetachTransferLineAutoCostHandler(StockTransactionLineEditor? line)
    {
        if (line is null || !_transferAutoCostSubscribedLines.Remove(line))
        {
            return;
        }

        line.PropertyChanged -= OnTransferLinePropertyChanged;
    }

    private void OnStockOutLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StockTransactionLineEditor line)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(StockTransactionLineEditor.ItemId), StringComparison.Ordinal))
        {
            _ = AutoFillOutboundUnitCostAsync(line, "STOCK_OUT");
            return;
        }

        if (string.Equals(e.PropertyName, nameof(StockTransactionLineEditor.ExpenseAccountCode), StringComparison.Ordinal))
        {
            SyncStockOutExpenseAccountName(line);
        }
    }

    private void OnTransferLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(StockTransactionLineEditor.ItemId), StringComparison.Ordinal) ||
            sender is not StockTransactionLineEditor line)
        {
            return;
        }

        _ = AutoFillOutboundUnitCostAsync(line, "TRANSFER");
    }

    private async Task AutoFillOutboundUnitCostAsync(StockTransactionLineEditor line, string transactionType)
    {
        if (line.ItemId <= 0)
        {
            line.UnitCost = 0;
            return;
        }

        try
        {
            if (!_outboundAutoCostCache.TryGetValue(line.ItemId, out var unitCost))
            {
                var lookup = await _accessControlService.GetOutboundAutoUnitCostLookupAsync(
                    _companyId,
                    _locationId,
                    new[] { line.ItemId });
                unitCost = lookup.TryGetValue(line.ItemId, out var value) ? value : 0m;
                _outboundAutoCostCache[line.ItemId] = unitCost;
            }

            line.UnitCost = unitCost;

            if (unitCost <= 0)
            {
                var itemLabel = ResolveOutboundItemLabel(line.ItemId);
                var txLabel = string.Equals(transactionType, "TRANSFER", StringComparison.OrdinalIgnoreCase)
                    ? "transfer"
                    : "barang keluar";
                StatusMessage = $"Harga otomatis {txLabel} item {itemLabel} belum tersedia, diisi 0. Cek stock in/opening balance atau jalankan recalculate costing.";
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "AutoFillOutboundUnitCostFailed", ex.Message);
            StatusMessage = "Gagal mengisi harga otomatis transaksi keluar.";
        }
    }

    private string ResolveOutboundItemLabel(long itemId)
    {
        var item = StockItemLookupOptions.FirstOrDefault(x => x.Id == itemId)
            ?? Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null)
        {
            return $"#{itemId}";
        }

        if (string.IsNullOrWhiteSpace(item.Code))
        {
            return string.IsNullOrWhiteSpace(item.Name) ? $"#{itemId}" : item.Name.Trim();
        }

        return string.IsNullOrWhiteSpace(item.Name)
            ? item.Code.Trim()
            : $"{item.Code.Trim()} - {item.Name.Trim()}";
    }

    private bool TryBuildOutboundZeroCostWarning(string txType, out string warningMessage)
    {
        warningMessage = string.Empty;
        if (!string.Equals(txType, "STOCK_OUT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(txType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lines = string.Equals(txType, "TRANSFER", StringComparison.OrdinalIgnoreCase)
            ? TransferLines
            : StockOutLines;
        var zeroCostLines = lines.Count(x => x.ItemId > 0 && x.Qty > 0 && x.UnitCost <= 0);
        if (zeroCostLines <= 0)
        {
            return false;
        }

        var txLabel = string.Equals(txType, "TRANSFER", StringComparison.OrdinalIgnoreCase)
            ? "transfer"
            : "barang keluar";
        warningMessage = $"PERINGATAN: {zeroCostLines} baris transaksi {txLabel} masih bernilai biaya 0.";
        return true;
    }
}
