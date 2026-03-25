using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private ManagedStockTransaction? _stockInHeader;
    private string _stockInSearchKeyword = string.Empty;

    public ICommand NewStockInCommand { get; private set; } = null!;
    public ICommand SaveStockInDraftCommand { get; private set; } = null!;
    public ICommand SubmitStockInCommand { get; private set; } = null!;
    public ICommand ApproveStockInCommand { get; private set; } = null!;
    public ICommand PostStockInCommand { get; private set; } = null!;
    public ICommand AddStockInLineCommand { get; private set; } = null!;
    public ICommand RemoveStockInLineCommand { get; private set; } = null!;
    public ICommand SearchStockInCommand { get; private set; } = null!;
    public ICommand LoadStockInCommand { get; private set; } = null!;
    public ICommand DownloadOpeningBalanceTemplateCommand { get; private set; } = null!;
    public ICommand ImportOpeningBalanceCommand { get; private set; } = null!;

    public ObservableCollection<StockTransactionLineEditor> StockInLines { get; } = new();
    public ObservableCollection<ManagedStockTransactionSummary> StockInTransactionList { get; } = new();

    public ManagedStockTransaction? StockInHeader
    {
        get => _stockInHeader;
        set
        {
            if (SetProperty(ref _stockInHeader, value))
            {
                RaiseInventoryActionStateChanged();
            }
        }
    }

    public string StockInSearchKeyword
    {
        get => _stockInSearchKeyword;
        set => SetProperty(ref _stockInSearchKeyword, value);
    }

    public bool CanCreateStockIn => CanCreateInventoryDocument(_canCreateStockIn);

    public bool CanSaveStockInDraft => CanSaveInventoryDraft(StockInHeader?.Id, StockInHeader?.Status, _canCreateStockIn, _canUpdateStockIn);

    public bool CanSubmitStockIn => CanAdvanceInventoryWorkflow(StockInHeader?.Id, StockInHeader?.Status, "DRAFT", _canSubmitStockIn);

    public bool CanApproveStockIn => CanAdvanceInventoryWorkflow(StockInHeader?.Id, StockInHeader?.Status, "SUBMITTED", _canApproveStockIn);

    public bool CanPostStockIn => CanAdvanceInventoryWorkflow(StockInHeader?.Id, StockInHeader?.Status, "APPROVED", _canPostStockIn);

    public string NewStockInTooltip => BuildNewInventoryDocumentTooltip("transaksi stok masuk", _canCreateStockIn);

    public string SaveStockInDraftTooltip => BuildSaveInventoryDraftTooltip(
        "transaksi stok masuk",
        StockInHeader?.Id,
        StockInHeader?.Status,
        _canCreateStockIn,
        _canUpdateStockIn);

    public string SubmitStockInTooltip => BuildInventoryWorkflowTooltip(
        "transaksi stok masuk",
        "submit",
        StockInHeader?.Id,
        StockInHeader?.Status,
        "DRAFT",
        _canSubmitStockIn,
        "Ajukan transaksi stok masuk untuk proses berikutnya.");

    public string ApproveStockInTooltip => BuildInventoryWorkflowTooltip(
        "transaksi stok masuk",
        "approve",
        StockInHeader?.Id,
        StockInHeader?.Status,
        "SUBMITTED",
        _canApproveStockIn,
        "Setujui transaksi stok masuk yang sudah diajukan.");

    public string PostStockInTooltip => BuildInventoryWorkflowTooltip(
        "transaksi stok masuk",
        "posting",
        StockInHeader?.Id,
        StockInHeader?.Status,
        "APPROVED",
        _canPostStockIn,
        "Posting transaksi stok masuk yang sudah disetujui.");

    private void InitializeStockInCommands()
    {
        NewStockInCommand = new RelayCommand(NewStockIn);
        SaveStockInDraftCommand = new RelayCommand(() => _ = SaveStockInDraftAsync());
        SubmitStockInCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(StockInHeader, "SUBMITTED", "STOCK_IN"));
        ApproveStockInCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(StockInHeader, "APPROVED", "STOCK_IN"));
        PostStockInCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(StockInHeader, "POSTED", "STOCK_IN"));
        AddStockInLineCommand = new RelayCommand(() => AddTransactionLine(StockInLines));
        RemoveStockInLineCommand = new RelayCommand(obj => RemoveTransactionLine(StockInLines, obj));
        SearchStockInCommand = new RelayCommand(() => _ = SearchStockInAsync());
        LoadStockInCommand = new RelayCommand(obj => _ = LoadStockTransactionAsync(obj, "STOCK_IN"));
        DownloadOpeningBalanceTemplateCommand = new RelayCommand(DownloadOpeningBalanceTemplate);
        ImportOpeningBalanceCommand = new RelayCommand(() => _ = ImportOpeningBalanceAsync());
    }

    private void NewStockIn()
    {
        if (!CanCreateStockIn)
        {
            StatusMessage = NewStockInTooltip;
            return;
        }

        StockInHeader = new ManagedStockTransaction
        {
            Id = 0,
            CompanyId = _companyId,
            LocationId = _locationId,
            TransactionType = "STOCK_IN",
            TransactionDate = DateTime.Today,
            Status = "DRAFT"
        };
        StockInLines.Clear();
        AddTransactionLine(StockInLines);
        StatusMessage = "Transaksi barang masuk baru siap.";
    }

    private void DownloadOpeningBalanceTemplate()
    {
        if (!CanOperateOpeningBalance)
        {
            StatusMessage = BuildOpeningBalanceAdminOnlyMessage();
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"INVENTORY_OPENING_BALANCE_TEMPLATE_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var result = _inventoryOpeningBalanceXlsxService.CreateTemplate(dialog.FileName);
        StatusMessage = result.Message;
    }

    private async Task ImportOpeningBalanceAsync()
    {
        if (!CanOperateOpeningBalance)
        {
            StatusMessage = BuildOpeningBalanceAdminOnlyMessage();
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Memvalidasi file saldo awal...";

            var parseResult = _inventoryOpeningBalanceXlsxService.Parse(dialog.FileName);
            if (!parseResult.IsSuccess)
            {
                StatusMessage = parseResult.Message;
                return;
            }

            var validationResult = await _accessControlService.ImportInventoryOpeningBalanceAsync(
                _companyId,
                parseResult.Bundle,
                _actorUsername,
                validateOnly: true,
                replaceExistingBatch: true);
            if (!validationResult.IsSuccess)
            {
                StatusMessage = validationResult.Message;
                return;
            }

            StatusMessage = "Mengimpor saldo awal inventory...";
            var importResult = await _accessControlService.ImportInventoryOpeningBalanceAsync(
                _companyId,
                parseResult.Bundle,
                _actorUsername,
                validateOnly: false,
                replaceExistingBatch: true);
            StatusMessage = importResult.Message;
            if (importResult.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await SearchStockInAsync();
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(InventoryViewModel),
                "ImportOpeningBalanceFailed",
                $"action=import_opening_balance company_id={_companyId} file_path={dialog.FileName}",
                ex);
            StatusMessage = "Import saldo awal gagal diproses.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildOpeningBalanceAdminOnlyMessage() =>
        "Operasional saldo awal inventory hanya untuk role SUPER_ADMIN.";

    private async Task SaveStockInDraftAsync()
    {
        if (!CanSaveStockInDraft)
        {
            StatusMessage = SaveStockInDraftTooltip;
            return;
        }

        var header = StockInHeader;
        if (header is null)
        {
            StatusMessage = SaveStockInDraftTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.TransactionDate, "Simpan draft transaksi stock in"))
        {
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;
            var lines = StockInLines.Where(l => l.ItemId > 0).Select((l, i) => new ManagedStockTransactionLine
            {
                Id = 0,
                TransactionId = header.Id,
                LineNo = i + 1,
                ItemId = l.ItemId,
                Qty = l.Qty,
                UnitCost = l.UnitCost,
                Notes = l.Notes
            }).ToList();

            var result = await _accessControlService.SaveStockTransactionDraftAsync(header, lines, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await LoadStockTransactionAsync(result.EntityId, "STOCK_IN");
                await SearchStockInAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SearchStockInAsync()
    {
        try
        {
            var filter = new InventoryTransactionSearchFilter
            {
                Keyword = StockInSearchKeyword,
                TransactionType = "STOCK_IN"
            };
            var results = await _accessControlService.SearchStockTransactionsAsync(_companyId, _locationId, filter);
            ReplaceCollection(StockInTransactionList, results);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "SearchStockInFailed", ex.Message);
        }
    }

    private async Task LoadStockTransactionAsync(object? parameter, string expectedType)
    {
        long transactionId = parameter switch
        {
            long id => id,
            ManagedStockTransactionSummary s => s.Id,
            _ => 0
        };

        if (transactionId <= 0) return;

        try
        {
            IsBusy = true;
            var bundle = await _accessControlService.GetStockTransactionBundleAsync(transactionId);
            if (bundle is null) return;

            if (expectedType == "STOCK_IN")
            {
                StockInHeader = bundle.Header;
                ReplaceTransactionLines(StockInLines, bundle.Lines);
            }
            else if (expectedType == "STOCK_OUT")
            {
                ClearOutboundAutoCostCache();
                ResetStockOutLineAutoCostHandlers();
                StockOutHeader = bundle.Header;
                ReplaceTransactionLines(StockOutLines, bundle.Lines);
                AttachStockOutLineAutoCostHandlers(StockOutLines);
                SyncAllStockOutExpenseAccountNames();
            }
            else if (expectedType == "TRANSFER")
            {
                ClearOutboundAutoCostCache();
                ResetTransferLineAutoCostHandlers();
                TransferHeader = bundle.Header;
                ReplaceTransactionLines(TransferLines, bundle.Lines);
                AttachTransferLineAutoCostHandlers(TransferLines);
            }

            EnsureStockItemLookupContains(bundle.Lines.Select(x => x.ItemId));

            StatusMessage = $"Transaksi {bundle.Header.TransactionNo} dimuat.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChangeStockTransactionStatusAsync(ManagedStockTransaction? header, string targetStatus, string txType)
    {
        if (header is null || header.Id <= 0)
        {
            StatusMessage = "Simpan transaksi terlebih dahulu.";
            return;
        }

        var statusTooltip = GetStockTransactionWorkflowTooltip(header, txType, targetStatus);
        if (!CanChangeStockTransactionStatus(header, txType, targetStatus))
        {
            StatusMessage = statusTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.TransactionDate, $"Ubah status transaksi ke {targetStatus}"))
        {
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;
            AccessOperationResult result;

            switch (targetStatus)
            {
                case "SUBMITTED":
                    result = await _accessControlService.SubmitStockTransactionAsync(header.Id, _actorUsername);
                    break;
                case "APPROVED":
                    result = await _accessControlService.ApproveStockTransactionAsync(header.Id, _actorUsername);
                    break;
                case "POSTED":
                    result = await _accessControlService.PostStockTransactionAsync(header.Id, _actorUsername);
                    break;
                default:
                    return;
            }

            var shouldAppendZeroCostWarning = string.Equals(targetStatus, "SUBMITTED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetStatus, "POSTED", StringComparison.OrdinalIgnoreCase);
            var zeroCostWarningMessage = string.Empty;
            var hasZeroCostWarning = shouldAppendZeroCostWarning &&
                                     TryBuildOutboundZeroCostWarning(txType, out zeroCostWarningMessage);
            StatusMessage = result.IsSuccess && hasZeroCostWarning
                ? $"{result.Message} {zeroCostWarningMessage}"
                : result.Message;

            if (result.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await LoadStockTransactionAsync(header.Id, txType);
                if (txType == "STOCK_IN") await SearchStockInAsync();
                else if (txType == "STOCK_OUT") await SearchStockOutAsync();
                else if (txType == "TRANSFER") await SearchTransferAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChangeStockTransactionStatus(ManagedStockTransaction? header, string txType, string targetStatus)
    {
        return (txType, targetStatus) switch
        {
            ("STOCK_IN", "SUBMITTED") => CanSubmitStockIn,
            ("STOCK_IN", "APPROVED") => CanApproveStockIn,
            ("STOCK_IN", "POSTED") => CanPostStockIn,
            ("STOCK_OUT", "SUBMITTED") => CanSubmitStockOut,
            ("STOCK_OUT", "APPROVED") => CanApproveStockOut,
            ("STOCK_OUT", "POSTED") => CanPostStockOut,
            ("TRANSFER", "SUBMITTED") => CanSubmitTransfer,
            ("TRANSFER", "APPROVED") => CanApproveTransfer,
            ("TRANSFER", "POSTED") => CanPostTransfer,
            _ => false
        };
    }

    private string GetStockTransactionWorkflowTooltip(ManagedStockTransaction? header, string txType, string targetStatus)
    {
        return (txType, targetStatus) switch
        {
            ("STOCK_IN", "SUBMITTED") => SubmitStockInTooltip,
            ("STOCK_IN", "APPROVED") => ApproveStockInTooltip,
            ("STOCK_IN", "POSTED") => PostStockInTooltip,
            ("STOCK_OUT", "SUBMITTED") => SubmitStockOutTooltip,
            ("STOCK_OUT", "APPROVED") => ApproveStockOutTooltip,
            ("STOCK_OUT", "POSTED") => PostStockOutTooltip,
            ("TRANSFER", "SUBMITTED") => SubmitTransferTooltip,
            ("TRANSFER", "APPROVED") => ApproveTransferTooltip,
            ("TRANSFER", "POSTED") => PostTransferTooltip,
            _ => "Aksi ini tidak tersedia."
        };
    }

    private static void AddTransactionLine(ObservableCollection<StockTransactionLineEditor> lines)
    {
        lines.Add(new StockTransactionLineEditor { LineNo = lines.Count + 1 });
    }

    private static void RemoveTransactionLine(ObservableCollection<StockTransactionLineEditor> lines, object? parameter)
    {
        if (parameter is StockTransactionLineEditor line)
        {
            lines.Remove(line);
            for (int i = 0; i < lines.Count; i++)
            {
                lines[i].LineNo = i + 1;
            }
        }
    }

    private static void ReplaceTransactionLines(ObservableCollection<StockTransactionLineEditor> target, List<ManagedStockTransactionLine> source)
    {
        target.Clear();
        foreach (var line in source)
        {
            target.Add(new StockTransactionLineEditor
            {
                LineNo = line.LineNo,
                ItemId = line.ItemId,
                ItemCode = line.ItemCode,
                ItemName = line.ItemName,
                Uom = line.Uom,
                Qty = line.Qty,
                UnitCost = line.UnitCost,
                ExpenseAccountCode = line.ExpenseAccountCode,
                Notes = line.Notes
            });
        }
    }
}
