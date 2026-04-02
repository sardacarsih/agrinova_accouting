using System.Collections.ObjectModel;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private ManagedStockAdjustment? _stockAdjustmentHeader;
    private string _stockAdjustmentSearchKeyword = string.Empty;

    public ICommand NewStockAdjustmentCommand { get; private set; } = null!;
    public ICommand SaveStockAdjustmentDraftCommand { get; private set; } = null!;
    public ICommand SubmitStockAdjustmentCommand { get; private set; } = null!;
    public ICommand ApproveStockAdjustmentCommand { get; private set; } = null!;
    public ICommand PostStockAdjustmentCommand { get; private set; } = null!;
    public ICommand AddStockAdjustmentLineCommand { get; private set; } = null!;
    public ICommand RemoveStockAdjustmentLineCommand { get; private set; } = null!;
    public ICommand SearchStockAdjustmentCommand { get; private set; } = null!;
    public ICommand LoadStockAdjustmentCommand { get; private set; } = null!;

    public ObservableCollection<StockTransactionLineEditor> StockAdjustmentLines { get; } = new();
    public ObservableCollection<ManagedStockAdjustment> StockAdjustmentList { get; } = new();

    public ManagedStockAdjustment? StockAdjustmentHeader
    {
        get => _stockAdjustmentHeader;
        set
        {
            if (SetProperty(ref _stockAdjustmentHeader, value))
            {
                RaiseInventoryActionStateChanged();
            }
        }
    }

    public string StockAdjustmentSearchKeyword
    {
        get => _stockAdjustmentSearchKeyword;
        set => SetProperty(ref _stockAdjustmentSearchKeyword, value);
    }

    public bool CanCreateStockAdjustment => CanCreateInventoryDocument(_canCreateStockAdjustment);

    public bool CanSaveStockAdjustmentDraft => CanSaveInventoryDraft(StockAdjustmentHeader?.Id, StockAdjustmentHeader?.Status, _canCreateStockAdjustment, _canUpdateStockAdjustment);

    public bool CanSubmitStockAdjustment => CanAdvanceInventoryWorkflow(StockAdjustmentHeader?.Id, StockAdjustmentHeader?.Status, "DRAFT", _canSubmitStockAdjustment);

    public bool CanApproveStockAdjustment => CanAdvanceInventoryWorkflow(StockAdjustmentHeader?.Id, StockAdjustmentHeader?.Status, "SUBMITTED", _canApproveStockAdjustment);

    public bool CanPostStockAdjustment => CanAdvanceInventoryWorkflow(StockAdjustmentHeader?.Id, StockAdjustmentHeader?.Status, "APPROVED", _canPostStockAdjustment);

    public string NewStockAdjustmentTooltip => BuildNewInventoryDocumentTooltip("stock adjustment", _canCreateStockAdjustment);

    public string SaveStockAdjustmentDraftTooltip => BuildSaveInventoryDraftTooltip(
        "stock adjustment",
        StockAdjustmentHeader?.Id,
        StockAdjustmentHeader?.Status,
        _canCreateStockAdjustment,
        _canUpdateStockAdjustment);

    public string SubmitStockAdjustmentTooltip => BuildInventoryWorkflowTooltip(
        "stock adjustment",
        "submit",
        StockAdjustmentHeader?.Id,
        StockAdjustmentHeader?.Status,
        "DRAFT",
        _canSubmitStockAdjustment,
        "Ajukan stock adjustment untuk proses berikutnya.");

    public string ApproveStockAdjustmentTooltip => BuildInventoryWorkflowTooltip(
        "stock adjustment",
        "approve",
        StockAdjustmentHeader?.Id,
        StockAdjustmentHeader?.Status,
        "SUBMITTED",
        _canApproveStockAdjustment,
        "Setujui stock adjustment yang sudah diajukan.");

    public string PostStockAdjustmentTooltip => BuildInventoryWorkflowTooltip(
        "stock adjustment",
        "posting",
        StockAdjustmentHeader?.Id,
        StockAdjustmentHeader?.Status,
        "APPROVED",
        _canPostStockAdjustment,
        "Posting stock adjustment yang sudah disetujui.");

    private void InitializeStockAdjustmentCommands()
    {
        NewStockAdjustmentCommand = new RelayCommand(NewStockAdjustment);
        SaveStockAdjustmentDraftCommand = new RelayCommand(() => _ = SaveStockAdjustmentDraftAsync());
        SubmitStockAdjustmentCommand = new RelayCommand(() => _ = ChangeStockAdjustmentStatusAsync("SUBMITTED"));
        ApproveStockAdjustmentCommand = new RelayCommand(() => _ = ChangeStockAdjustmentStatusAsync("APPROVED"));
        PostStockAdjustmentCommand = new RelayCommand(() => _ = ChangeStockAdjustmentStatusAsync("POSTED"));
        AddStockAdjustmentLineCommand = new RelayCommand(() => AddTransactionLine(StockAdjustmentLines));
        RemoveStockAdjustmentLineCommand = new RelayCommand(obj => RemoveTransactionLine(StockAdjustmentLines, obj));
        SearchStockAdjustmentCommand = new RelayCommand(() => _ = SearchStockAdjustmentAsync());
        LoadStockAdjustmentCommand = new RelayCommand(obj => _ = LoadStockAdjustmentAsync(obj));
    }

    private void NewStockAdjustment()
    {
        if (!CanCreateStockAdjustment)
        {
            StatusMessage = NewStockAdjustmentTooltip;
            return;
        }

        StockAdjustmentHeader = new ManagedStockAdjustment
        {
            Id = 0,
            CompanyId = _companyId,
            LocationId = _locationId,
            AdjustmentDate = DateTime.Today,
            Status = "DRAFT"
        };
        StockAdjustmentLines.Clear();
        AddTransactionLine(StockAdjustmentLines);
        SelectedStockAdjustmentTabIndex = 1;
        StatusMessage = "Stock adjustment baru siap.";
    }

    private async Task SaveStockAdjustmentDraftAsync()
    {
        if (!CanSaveStockAdjustmentDraft)
        {
            StatusMessage = SaveStockAdjustmentDraftTooltip;
            return;
        }

        var header = StockAdjustmentHeader;
        if (header is null)
        {
            StatusMessage = SaveStockAdjustmentDraftTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.AdjustmentDate, "Simpan draft stock adjustment"))
        {
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;
            var lines = StockAdjustmentLines.Where(l => l.ItemId > 0).Select((line, index) => new ManagedStockAdjustmentLine
            {
                LineNo = index + 1,
                ItemId = line.ItemId,
                QtyAdjustment = line.Qty,
                UnitCost = line.UnitCost,
                Notes = line.Notes
            }).ToList();

            var result = await _accessControlService.SaveStockAdjustmentDraftAsync(header, lines, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await LoadStockAdjustmentAsync(result.EntityId);
                await SearchStockAdjustmentAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChangeStockAdjustmentStatusAsync(string targetStatus)
    {
        var header = StockAdjustmentHeader;
        if (header is null || header.Id <= 0)
        {
            StatusMessage = "Simpan stock adjustment terlebih dahulu.";
            return;
        }

        var statusTooltip = targetStatus switch
        {
            "SUBMITTED" => SubmitStockAdjustmentTooltip,
            "APPROVED" => ApproveStockAdjustmentTooltip,
            "POSTED" => PostStockAdjustmentTooltip,
            _ => "Aksi ini tidak tersedia."
        };
        var canRun = targetStatus switch
        {
            "SUBMITTED" => CanSubmitStockAdjustment,
            "APPROVED" => CanApproveStockAdjustment,
            "POSTED" => CanPostStockAdjustment,
            _ => false
        };
        if (!canRun)
        {
            StatusMessage = statusTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.AdjustmentDate, $"Ubah status stock adjustment ke {targetStatus}"))
        {
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;
            AccessOperationResult result = targetStatus switch
            {
                "SUBMITTED" => await _accessControlService.SubmitStockAdjustmentAsync(header.Id, _actorUsername),
                "APPROVED" => await _accessControlService.ApproveStockAdjustmentAsync(header.Id, _actorUsername),
                "POSTED" => await _accessControlService.PostStockAdjustmentAsync(header.Id, _actorUsername),
                _ => new AccessOperationResult(false, "Aksi ini tidak tersedia.")
            };

            StatusMessage = result.Message;
            if (result.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await LoadStockAdjustmentAsync(header.Id);
                await SearchStockAdjustmentAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SearchStockAdjustmentAsync()
    {
        try
        {
            var results = await _accessControlService.SearchStockAdjustmentsAsync(_companyId, _locationId, StockAdjustmentSearchKeyword);
            ReplaceCollection(StockAdjustmentList, results);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "SearchStockAdjustmentFailed", ex.Message);
        }
    }

    private async Task LoadStockAdjustmentAsync(object? parameter)
    {
        long adjustmentId = parameter switch
        {
            long id => id,
            ManagedStockAdjustment adjustment => adjustment.Id,
            _ => 0
        };

        if (adjustmentId <= 0) return;

        try
        {
            IsBusy = true;
            var bundle = await _accessControlService.GetStockAdjustmentBundleAsync(adjustmentId);
            if (bundle is null) return;

            StockAdjustmentHeader = bundle.Header;
            StockAdjustmentLines.Clear();
            foreach (var line in bundle.Lines)
            {
                StockAdjustmentLines.Add(new StockTransactionLineEditor
                {
                    LineNo = line.LineNo,
                    ItemId = line.ItemId,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName,
                    Uom = line.Uom,
                    ItemLookupText = line.ItemCode,
                    Qty = line.QtyAdjustment,
                    UnitCost = line.UnitCost,
                    Notes = line.Notes
                });
            }

            EnsureStockItemLookupContains(bundle.Lines.Select(x => x.ItemId));
            SelectedStockAdjustmentTabIndex = 1;
            StatusMessage = $"Stock adjustment {bundle.Header.AdjustmentNo} dimuat.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
