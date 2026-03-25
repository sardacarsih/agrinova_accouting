using System.Collections.ObjectModel;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class StockOpnameLineEditor : ViewModelBase
{
    private int _lineNo;
    private long _itemId;
    private string _itemCode = string.Empty;
    private string _itemName = string.Empty;
    private string _uom = string.Empty;
    private decimal _systemQty;
    private decimal _actualQty;
    private string _notes = string.Empty;

    public int LineNo
    {
        get => _lineNo;
        set => SetProperty(ref _lineNo, value);
    }

    public long ItemId
    {
        get => _itemId;
        set => SetProperty(ref _itemId, value);
    }

    public string ItemCode
    {
        get => _itemCode;
        set => SetProperty(ref _itemCode, value);
    }

    public string ItemName
    {
        get => _itemName;
        set => SetProperty(ref _itemName, value);
    }

    public string Uom
    {
        get => _uom;
        set => SetProperty(ref _uom, value);
    }

    public decimal SystemQty
    {
        get => _systemQty;
        set => SetProperty(ref _systemQty, value);
    }

    public decimal ActualQty
    {
        get => _actualQty;
        set
        {
            if (SetProperty(ref _actualQty, value))
            {
                OnPropertyChanged(nameof(DifferenceQty));
            }
        }
    }

    public decimal DifferenceQty => ActualQty - SystemQty;

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }
}

public sealed partial class InventoryViewModel
{
    private ManagedStockOpname? _stockOpnameHeader;
    private string _stockOpnameSearchKeyword = string.Empty;

    public ICommand NewStockOpnameCommand { get; private set; } = null!;
    public ICommand SaveStockOpnameDraftCommand { get; private set; } = null!;
    public ICommand SubmitStockOpnameCommand { get; private set; } = null!;
    public ICommand ApproveStockOpnameCommand { get; private set; } = null!;
    public ICommand PostStockOpnameCommand { get; private set; } = null!;
    public ICommand GenerateOpnameLinesCommand { get; private set; } = null!;
    public ICommand SearchStockOpnameCommand { get; private set; } = null!;
    public ICommand LoadStockOpnameCommand { get; private set; } = null!;

    public ObservableCollection<StockOpnameLineEditor> StockOpnameLines { get; } = new();
    public ObservableCollection<ManagedStockOpname> StockOpnameList { get; } = new();

    public ManagedStockOpname? StockOpnameHeader
    {
        get => _stockOpnameHeader;
        set
        {
            if (SetProperty(ref _stockOpnameHeader, value))
            {
                RaiseInventoryActionStateChanged();
            }
        }
    }

    public string StockOpnameSearchKeyword
    {
        get => _stockOpnameSearchKeyword;
        set => SetProperty(ref _stockOpnameSearchKeyword, value);
    }

    public bool CanCreateStockOpname => CanCreateInventoryDocument(_canCreateStockOpname);

    public bool CanGenerateStockOpnameLines =>
        StockOpnameHeader is not null &&
        StockOpnameHeader.WarehouseId.HasValue &&
        CanSaveInventoryDraft(StockOpnameHeader.Id, StockOpnameHeader.Status, _canCreateStockOpname, _canUpdateStockOpname);

    public bool CanSaveStockOpnameDraft => CanSaveInventoryDraft(StockOpnameHeader?.Id, StockOpnameHeader?.Status, _canCreateStockOpname, _canUpdateStockOpname);

    public bool CanSubmitStockOpname => CanAdvanceInventoryWorkflow(StockOpnameHeader?.Id, StockOpnameHeader?.Status, "DRAFT", _canSubmitStockOpname);

    public bool CanApproveStockOpname => CanAdvanceInventoryWorkflow(StockOpnameHeader?.Id, StockOpnameHeader?.Status, "SUBMITTED", _canApproveStockOpname);

    public bool CanPostStockOpname => CanAdvanceInventoryWorkflow(StockOpnameHeader?.Id, StockOpnameHeader?.Status, "APPROVED", _canPostStockOpname);

    public string NewStockOpnameTooltip => BuildNewInventoryDocumentTooltip("stock opname", _canCreateStockOpname);

    public string GenerateStockOpnameLinesTooltip =>
        StockOpnameHeader is null
            ? "Buat stock opname terlebih dahulu."
            : !StockOpnameHeader.WarehouseId.HasValue
                ? "Pilih gudang terlebih dahulu."
                : BuildSaveInventoryDraftTooltip(
                    "stock opname",
                    StockOpnameHeader.Id,
                    StockOpnameHeader.Status,
                    _canCreateStockOpname,
                    _canUpdateStockOpname);

    public string SaveStockOpnameDraftTooltip => BuildSaveInventoryDraftTooltip(
        "stock opname",
        StockOpnameHeader?.Id,
        StockOpnameHeader?.Status,
        _canCreateStockOpname,
        _canUpdateStockOpname);

    public string SubmitStockOpnameTooltip => BuildInventoryWorkflowTooltip(
        "stock opname",
        "submit",
        StockOpnameHeader?.Id,
        StockOpnameHeader?.Status,
        "DRAFT",
        _canSubmitStockOpname,
        "Ajukan stock opname untuk proses berikutnya.");

    public string ApproveStockOpnameTooltip => BuildInventoryWorkflowTooltip(
        "stock opname",
        "approve",
        StockOpnameHeader?.Id,
        StockOpnameHeader?.Status,
        "SUBMITTED",
        _canApproveStockOpname,
        "Setujui stock opname yang sudah diajukan.");

    public string PostStockOpnameTooltip => BuildInventoryWorkflowTooltip(
        "stock opname",
        "posting",
        StockOpnameHeader?.Id,
        StockOpnameHeader?.Status,
        "APPROVED",
        _canPostStockOpname,
        "Posting stock opname yang sudah disetujui.");

    private void InitializeStockOpnameCommands()
    {
        NewStockOpnameCommand = new RelayCommand(NewStockOpname);
        SaveStockOpnameDraftCommand = new RelayCommand(() => _ = SaveStockOpnameDraftAsync());
        SubmitStockOpnameCommand = new RelayCommand(() => _ = ChangeStockOpnameStatusAsync("SUBMITTED"));
        ApproveStockOpnameCommand = new RelayCommand(() => _ = ChangeStockOpnameStatusAsync("APPROVED"));
        PostStockOpnameCommand = new RelayCommand(() => _ = ChangeStockOpnameStatusAsync("POSTED"));
        GenerateOpnameLinesCommand = new RelayCommand(() => _ = GenerateOpnameLinesAsync());
        SearchStockOpnameCommand = new RelayCommand(() => _ = SearchStockOpnameAsync());
        LoadStockOpnameCommand = new RelayCommand(obj => _ = LoadStockOpnameAsync(obj));
    }

    private void NewStockOpname()
    {
        if (!CanCreateStockOpname)
        {
            StatusMessage = NewStockOpnameTooltip;
            return;
        }

        StockOpnameHeader = new ManagedStockOpname
        {
            Id = 0,
            CompanyId = _companyId,
            LocationId = _locationId,
            OpnameDate = DateTime.Today,
            Status = "DRAFT"
        };
        StockOpnameLines.Clear();
        StatusMessage = "Stock opname baru siap.";
    }

    private async Task GenerateOpnameLinesAsync()
    {
        if (!CanGenerateStockOpnameLines)
        {
            StatusMessage = GenerateStockOpnameLinesTooltip;
            return;
        }

        var header = StockOpnameHeader;
        if (header is null || !header.WarehouseId.HasValue)
        {
            StatusMessage = GenerateStockOpnameLinesTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.OpnameDate, "Generate baris stock opname"))
        {
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;
            var lines = await _accessControlService.GenerateOpnameLinesFromStockAsync(
                _companyId, _locationId, header.WarehouseId.Value);

            StockOpnameLines.Clear();
            int lineNo = 1;
            foreach (var line in lines)
            {
                StockOpnameLines.Add(new StockOpnameLineEditor
                {
                    LineNo = lineNo++,
                    ItemId = line.ItemId,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName,
                    Uom = line.Uom,
                    SystemQty = line.SystemQty,
                    ActualQty = line.SystemQty
                });
            }

            StatusMessage = $"{lines.Count} baris stock opname di-generate.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveStockOpnameDraftAsync()
    {
        if (!CanSaveStockOpnameDraft)
        {
            StatusMessage = SaveStockOpnameDraftTooltip;
            return;
        }

        var header = StockOpnameHeader;
        if (header is null)
        {
            StatusMessage = SaveStockOpnameDraftTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.OpnameDate, "Simpan draft stock opname"))
        {
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;
            var lines = StockOpnameLines.Where(l => l.ItemId > 0).Select((l, i) => new ManagedStockOpnameLine
            {
                LineNo = i + 1,
                ItemId = l.ItemId,
                SystemQty = l.SystemQty,
                ActualQty = l.ActualQty,
                DifferenceQty = l.DifferenceQty,
                Notes = l.Notes
            }).ToList();

            var result = await _accessControlService.SaveStockOpnameDraftAsync(header, lines, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await LoadStockOpnameAsync(result.EntityId);
                await SearchStockOpnameAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChangeStockOpnameStatusAsync(string targetStatus)
    {
        var header = StockOpnameHeader;
        if (header is null || header.Id <= 0)
        {
            StatusMessage = "Simpan stock opname terlebih dahulu.";
            return;
        }

        var statusTooltip = targetStatus switch
        {
            "SUBMITTED" => SubmitStockOpnameTooltip,
            "APPROVED" => ApproveStockOpnameTooltip,
            "POSTED" => PostStockOpnameTooltip,
            _ => "Aksi ini tidak tersedia."
        };
        var canRunAction = targetStatus switch
        {
            "SUBMITTED" => CanSubmitStockOpname,
            "APPROVED" => CanApproveStockOpname,
            "POSTED" => CanPostStockOpname,
            _ => false
        };
        if (!canRunAction)
        {
            StatusMessage = statusTooltip;
            return;
        }

        if (!await EnsureAccountingPeriodOpenForDateAsync(header.OpnameDate, $"Ubah status stock opname ke {targetStatus}"))
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
                    result = await _accessControlService.SubmitStockOpnameAsync(header.Id, _actorUsername);
                    break;
                case "APPROVED":
                    result = await _accessControlService.ApproveStockOpnameAsync(header.Id, _actorUsername);
                    break;
                case "POSTED":
                    result = await _accessControlService.PostStockOpnameAsync(header.Id, _actorUsername);
                    break;
                default:
                    return;
            }

            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await RefreshWorkspaceAfterMutationAsync();
                await LoadStockOpnameAsync(header.Id);
                await SearchStockOpnameAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadStockOpnameAsync(object? parameter)
    {
        long opnameId = parameter switch
        {
            long id => id,
            ManagedStockOpname o => o.Id,
            _ => 0
        };

        if (opnameId <= 0) return;

        try
        {
            IsBusy = true;
            var bundle = await _accessControlService.GetStockOpnameBundleAsync(opnameId);
            if (bundle is null) return;

            StockOpnameHeader = bundle.Header;
            StockOpnameLines.Clear();
            foreach (var line in bundle.Lines)
            {
                StockOpnameLines.Add(new StockOpnameLineEditor
                {
                    LineNo = line.LineNo,
                    ItemId = line.ItemId,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName,
                    Uom = line.Uom,
                    SystemQty = line.SystemQty,
                    ActualQty = line.ActualQty,
                    Notes = line.Notes
                });
            }

            StatusMessage = $"Stock opname {bundle.Header.OpnameNo} dimuat.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SearchStockOpnameAsync()
    {
        try
        {
            var results = await _accessControlService.SearchStockOpnameAsync(_companyId, _locationId, StockOpnameSearchKeyword);
            ReplaceCollection(StockOpnameList, results);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "SearchStockOpnameFailed", ex.Message);
        }
    }
}
