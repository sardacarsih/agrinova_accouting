using System.Collections.ObjectModel;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private ManagedStockTransaction? _stockOutHeader;
    private string _stockOutSearchKeyword = string.Empty;

    public ICommand NewStockOutCommand { get; private set; } = null!;
    public ICommand SaveStockOutDraftCommand { get; private set; } = null!;
    public ICommand SubmitStockOutCommand { get; private set; } = null!;
    public ICommand ApproveStockOutCommand { get; private set; } = null!;
    public ICommand PostStockOutCommand { get; private set; } = null!;
    public ICommand AddStockOutLineCommand { get; private set; } = null!;
    public ICommand RemoveStockOutLineCommand { get; private set; } = null!;
    public ICommand SearchStockOutCommand { get; private set; } = null!;
    public ICommand LoadStockOutCommand { get; private set; } = null!;

    public ObservableCollection<StockTransactionLineEditor> StockOutLines { get; } = new();
    public ObservableCollection<ManagedStockTransactionSummary> StockOutTransactionList { get; } = new();

    public ManagedStockTransaction? StockOutHeader
    {
        get => _stockOutHeader;
        set
        {
            if (SetProperty(ref _stockOutHeader, value))
            {
                RaiseInventoryActionStateChanged();
            }
        }
    }

    public string StockOutSearchKeyword
    {
        get => _stockOutSearchKeyword;
        set => SetProperty(ref _stockOutSearchKeyword, value);
    }

    public bool CanCreateStockOut => CanCreateInventoryDocument(_canCreateStockOut);

    public bool CanSaveStockOutDraft => CanSaveInventoryDraft(StockOutHeader?.Id, StockOutHeader?.Status, _canCreateStockOut, _canUpdateStockOut);

    public bool CanSubmitStockOut => CanAdvanceInventoryWorkflow(StockOutHeader?.Id, StockOutHeader?.Status, "DRAFT", _canSubmitStockOut);

    public bool CanApproveStockOut => CanAdvanceInventoryWorkflow(StockOutHeader?.Id, StockOutHeader?.Status, "SUBMITTED", _canApproveStockOut);

    public bool CanPostStockOut => CanAdvanceInventoryWorkflow(StockOutHeader?.Id, StockOutHeader?.Status, "APPROVED", _canPostStockOut);

    public string NewStockOutTooltip => BuildNewInventoryDocumentTooltip("transaksi stok keluar", _canCreateStockOut);

    public string SaveStockOutDraftTooltip => BuildSaveInventoryDraftTooltip(
        "transaksi stok keluar",
        StockOutHeader?.Id,
        StockOutHeader?.Status,
        _canCreateStockOut,
        _canUpdateStockOut);

    public string SubmitStockOutTooltip => BuildInventoryWorkflowTooltip(
        "transaksi stok keluar",
        "submit",
        StockOutHeader?.Id,
        StockOutHeader?.Status,
        "DRAFT",
        _canSubmitStockOut,
        "Ajukan transaksi stok keluar untuk proses berikutnya.");

    public string ApproveStockOutTooltip => BuildInventoryWorkflowTooltip(
        "transaksi stok keluar",
        "approve",
        StockOutHeader?.Id,
        StockOutHeader?.Status,
        "SUBMITTED",
        _canApproveStockOut,
        "Setujui transaksi stok keluar yang sudah diajukan.");

    public string PostStockOutTooltip => BuildInventoryWorkflowTooltip(
        "transaksi stok keluar",
        "posting",
        StockOutHeader?.Id,
        StockOutHeader?.Status,
        "APPROVED",
        _canPostStockOut,
        "Posting transaksi stok keluar yang sudah disetujui.");

    private void InitializeStockOutCommands()
    {
        NewStockOutCommand = new RelayCommand(NewStockOut);
        SaveStockOutDraftCommand = new RelayCommand(() => _ = SaveStockOutDraftAsync());
        SubmitStockOutCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(StockOutHeader, "SUBMITTED", "STOCK_OUT"));
        ApproveStockOutCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(StockOutHeader, "APPROVED", "STOCK_OUT"));
        PostStockOutCommand = new RelayCommand(() => _ = ChangeStockTransactionStatusAsync(StockOutHeader, "POSTED", "STOCK_OUT"));
        AddStockOutLineCommand = new RelayCommand(AddStockOutLine);
        RemoveStockOutLineCommand = new RelayCommand(RemoveStockOutLine);
        SearchStockOutCommand = new RelayCommand(() => _ = SearchStockOutAsync());
        LoadStockOutCommand = new RelayCommand(obj => _ = LoadStockTransactionAsync(obj, "STOCK_OUT"));
    }

    private void RefreshStockOutExpenseAccountOptions()
    {
        var expenseAccounts = Accounts
            .Where(account => account.IsActive &&
                              account.IsPosting &&
                              string.Equals(account.AccountType, "EXPENSE", StringComparison.OrdinalIgnoreCase))
            .OrderBy(account => account.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ReplaceCollection(StockOutExpenseAccountOptions, expenseAccounts);
        SyncAllStockOutExpenseAccountNames();
    }

    private void SyncStockOutExpenseAccountName(StockTransactionLineEditor? line)
    {
        if (line is null)
        {
            return;
        }

        var code = (line.ExpenseAccountCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            line.ExpenseAccountName = string.Empty;
            return;
        }

        var account = StockOutExpenseAccountOptions.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? Accounts.FirstOrDefault(x => x.IsActive && x.IsPosting && string.Equals(x.AccountType, "EXPENSE", StringComparison.OrdinalIgnoreCase) && string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        line.ExpenseAccountName = account?.Name ?? string.Empty;
    }

    private void SyncAllStockOutExpenseAccountNames()
    {
        foreach (var line in StockOutLines)
        {
            SyncStockOutExpenseAccountName(line);
        }
    }

    private void NewStockOut()
    {
        if (!CanCreateStockOut)
        {
            StatusMessage = NewStockOutTooltip;
            return;
        }

        StockOutHeader = new ManagedStockTransaction
        {
            Id = 0,
            CompanyId = _companyId,
            LocationId = _locationId,
            TransactionType = "STOCK_OUT",
            TransactionDate = DateTime.Today,
            Status = "DRAFT"
        };
        ResetStockOutLineAutoCostHandlers();
        StockOutLines.Clear();
        ClearOutboundAutoCostCache();
        AddStockOutLine();
        StatusMessage = "Transaksi barang keluar baru siap.";
    }

    private void AddStockOutLine()
    {
        AddTransactionLine(StockOutLines);
        if (StockOutLines.Count > 0)
        {
            var line = StockOutLines[^1];
            AttachStockOutLineAutoCostHandler(line);
            SyncStockOutExpenseAccountName(line);
        }
    }

    private void RemoveStockOutLine(object? parameter)
    {
        if (parameter is StockTransactionLineEditor line)
        {
            DetachStockOutLineAutoCostHandler(line);
        }

        RemoveTransactionLine(StockOutLines, parameter);
    }

    private async Task SaveStockOutDraftAsync()
    {
        if (!CanSaveStockOutDraft)
        {
            StatusMessage = SaveStockOutDraftTooltip;
            return;
        }

        var header = StockOutHeader;
        if (header is null)
        {
            StatusMessage = SaveStockOutDraftTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.TransactionDate, "Simpan draft transaksi stock out"))
        {
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;
            var lines = StockOutLines.Where(l => l.ItemId > 0).Select((l, i) => new ManagedStockTransactionLine
            {
                Id = 0,
                TransactionId = header.Id,
                LineNo = i + 1,
                ItemId = l.ItemId,
                Qty = l.Qty,
                UnitCost = l.UnitCost,
                ExpenseAccountCode = l.ExpenseAccountCode,
                Notes = l.Notes
            }).ToList();

            var result = await _accessControlService.SaveStockTransactionDraftAsync(header, lines, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await LoadStockTransactionAsync(result.EntityId, "STOCK_OUT");
                await SearchStockOutAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SearchStockOutAsync()
    {
        try
        {
            var filter = new InventoryTransactionSearchFilter
            {
                Keyword = StockOutSearchKeyword,
                TransactionType = "STOCK_OUT"
            };
            var results = await _accessControlService.SearchStockTransactionsAsync(_companyId, _locationId, filter);
            ReplaceCollection(StockOutTransactionList, results);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "SearchStockOutFailed", ex.Message);
        }
    }
}
