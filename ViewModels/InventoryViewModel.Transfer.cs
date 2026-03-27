using System.Collections.ObjectModel;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private ManagedStockTransaction? _transferHeader;
    private string _transferSearchKeyword = string.Empty;

    public ICommand NewTransferCommand { get; private set; } = null!;
    public ICommand SaveTransferDraftCommand { get; private set; } = null!;
    public ICommand SubmitTransferCommand { get; private set; } = null!;
    public ICommand ApproveTransferCommand { get; private set; } = null!;
    public ICommand PostTransferCommand { get; private set; } = null!;
    public ICommand AddTransferLineCommand { get; private set; } = null!;
    public ICommand RemoveTransferLineCommand { get; private set; } = null!;
    public ICommand SearchTransferCommand { get; private set; } = null!;
    public ICommand LoadTransferCommand { get; private set; } = null!;

    public ObservableCollection<StockTransactionLineEditor> TransferLines { get; } = new();
    public ObservableCollection<ManagedStockTransactionSummary> TransferTransactionList { get; } = new();

    public ManagedStockTransaction? TransferHeader
    {
        get => _transferHeader;
        set
        {
            if (SetProperty(ref _transferHeader, value))
            {
                RaiseInventoryActionStateChanged();
            }
        }
    }

    public string TransferSearchKeyword
    {
        get => _transferSearchKeyword;
        set => SetProperty(ref _transferSearchKeyword, value);
    }

    public bool CanCreateTransfer => CanCreateInventoryDocument(_canCreateTransfer);

    public bool CanSaveTransferDraft => CanSaveInventoryDraft(TransferHeader?.Id, TransferHeader?.Status, _canCreateTransfer, _canUpdateTransfer);

    public bool CanSubmitTransfer => CanAdvanceInventoryWorkflow(TransferHeader?.Id, TransferHeader?.Status, "DRAFT", _canSubmitTransfer);

    public bool CanApproveTransfer => CanAdvanceInventoryWorkflow(TransferHeader?.Id, TransferHeader?.Status, "SUBMITTED", _canApproveTransfer);

    public bool CanPostTransfer => CanAdvanceInventoryWorkflow(TransferHeader?.Id, TransferHeader?.Status, "APPROVED", _canPostTransfer);

    public string NewTransferTooltip => BuildNewInventoryDocumentTooltip("transaksi transfer", _canCreateTransfer);

    public string SaveTransferDraftTooltip => BuildSaveInventoryDraftTooltip(
        "transaksi transfer",
        TransferHeader?.Id,
        TransferHeader?.Status,
        _canCreateTransfer,
        _canUpdateTransfer);

    public string SubmitTransferTooltip => BuildInventoryWorkflowTooltip(
        "transaksi transfer",
        "submit",
        TransferHeader?.Id,
        TransferHeader?.Status,
        "DRAFT",
        _canSubmitTransfer,
        "Ajukan transaksi transfer untuk proses berikutnya.");

    public string ApproveTransferTooltip => BuildInventoryWorkflowTooltip(
        "transaksi transfer",
        "approve",
        TransferHeader?.Id,
        TransferHeader?.Status,
        "SUBMITTED",
        _canApproveTransfer,
        "Setujui transaksi transfer yang sudah diajukan.");

    public string PostTransferTooltip => BuildInventoryWorkflowTooltip(
        "transaksi transfer",
        "posting",
        TransferHeader?.Id,
        TransferHeader?.Status,
        "APPROVED",
        _canPostTransfer,
        "Posting transaksi transfer yang sudah disetujui.");

    private void InitializeTransferCommands()
    {
        NewTransferCommand = new RelayCommand(NewTransfer);
        SaveTransferDraftCommand = new RelayCommand(() => _ = SaveTransferDraftAsync());
        SubmitTransferCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(TransferHeader, "SUBMITTED", "TRANSFER"));
        ApproveTransferCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(TransferHeader, "APPROVED", "TRANSFER"));
        PostTransferCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(TransferHeader, "POSTED", "TRANSFER"));
        AddTransferLineCommand = new RelayCommand(AddTransferLine);
        RemoveTransferLineCommand = new RelayCommand(RemoveTransferLine);
        SearchTransferCommand = new RelayCommand(() => _ = SearchTransferAsync());
        LoadTransferCommand = new RelayCommand(obj => _ = LoadStockTransactionAsync(obj, "TRANSFER"));
    }

    private void NewTransfer()
    {
        if (!CanCreateTransfer)
        {
            StatusMessage = NewTransferTooltip;
            return;
        }

        TransferHeader = new ManagedStockTransaction
        {
            Id = 0,
            CompanyId = _companyId,
            LocationId = _locationId,
            TransactionType = "TRANSFER",
            TransactionDate = DateTime.Today,
            Status = "DRAFT"
        };
        ResetTransferLineAutoCostHandlers();
        TransferLines.Clear();
        ClearOutboundAutoCostCache();
        AddTransferLine();
        SelectedTransferTabIndex = 1;
        StatusMessage = "Transaksi transfer baru siap.";
    }

    private void AddTransferLine()
    {
        AddTransactionLine(TransferLines);
        if (TransferLines.Count > 0)
        {
            AttachTransferLineAutoCostHandler(TransferLines[^1]);
        }
    }

    private void RemoveTransferLine(object? parameter)
    {
        if (parameter is StockTransactionLineEditor line)
        {
            DetachTransferLineAutoCostHandler(line);
        }

        RemoveTransactionLine(TransferLines, parameter);
    }

    private async Task SaveTransferDraftAsync()
    {
        if (!CanSaveTransferDraft)
        {
            StatusMessage = SaveTransferDraftTooltip;
            return;
        }

        var header = TransferHeader;
        if (header is null)
        {
            StatusMessage = SaveTransferDraftTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.TransactionDate, "Simpan draft transaksi transfer"))
        {
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;
            var lines = TransferLines.Where(l => l.ItemId > 0).Select((l, i) => new ManagedStockTransactionLine
            {
                Id = 0,
                TransactionId = header.Id,
                LineNo = i + 1,
                ItemId = l.ItemId,
                Qty = l.Qty,
                UnitCost = l.UnitCost,
                WarehouseId = l.WarehouseId,
                DestinationWarehouseId = l.DestinationWarehouseId,
                Notes = l.Notes
            }).ToList();

            var result = await _accessControlService.SaveStockTransactionDraftAsync(header, lines, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await LoadStockTransactionAsync(result.EntityId, "TRANSFER");
                await SearchTransferAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SearchTransferAsync()
    {
        try
        {
            var filter = new InventoryTransactionSearchFilter
            {
                Keyword = TransferSearchKeyword,
                TransactionType = "TRANSFER"
            };
            var results = await _accessControlService.SearchStockTransactionsAsync(_companyId, _locationId, filter);
            ReplaceCollection(TransferTransactionList, results);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "SearchTransferFailed", ex.Message);
        }
    }
}
