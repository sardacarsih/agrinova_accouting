using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel : ViewModelBase
{
    private const int ItemSearchDefaultPageSize = 50;
    private const int StockItemLookupDefaultPageSize = 50;
    private static readonly HashSet<string> ReportBackedTabs = new(StringComparer.OrdinalIgnoreCase)
    {
        "reports",
        "inquiry_mutation",
        "lk_outbound_compare",
        "report_stock",
        "report_mutation",
        "report_valuation",
        "low_stock",
        "sync_runs",
        "sync_items"
    };

    private static readonly HashSet<string> PlaceholderTabs = new(StringComparer.OrdinalIgnoreCase)
    {
        "lokasi_penyimpanan",
        "stock_adjustment",
        "transaction_history",
        "stock_card",
        "report_stock_opname"
    };

    private readonly IAccessControlService _accessControlService;
    private readonly InventoryImportXlsxService _inventoryImportXlsxService = new();
    private readonly InventoryOpeningBalanceXlsxService _inventoryOpeningBalanceXlsxService = new();
    private readonly string _actorUsername;
    private readonly long _companyId;
    private readonly long _locationId;

    private ManagedInventoryCategory? _selectedCategory;
    private ManagedInventoryItem? _selectedItem;
    private ManagedStockEntry? _selectedStockEntry;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private bool _isLoaded;
    private string _selectedInventoryTab = "dashboard";
    private long? _masterCompanyId;
    private string _masterCompanyLabel = string.Empty;
    private bool _canMaintainMasterInventoryData;
    private bool _canOperateOpeningBalance;
    private readonly bool _canSyncUploadInventoryMaster;
    private readonly bool _canSyncDownloadInventoryMaster;
    private readonly bool _canCreateCategory;
    private readonly bool _canUpdateCategory;
    private readonly bool _canDeleteCategory;
    private readonly bool _canCreateItem;
    private readonly bool _canUpdateItem;
    private readonly bool _canDeleteItem;
    private readonly bool _canCreateUnit;
    private readonly bool _canUpdateUnit;
    private readonly bool _canDeleteUnit;
    private readonly bool _canCreateWarehouse;
    private readonly bool _canUpdateWarehouse;
    private readonly bool _canDeleteWarehouse;
    private readonly bool _canImportInventoryMasterData;
    private readonly bool _canDownloadInventoryImportTemplate;
    private readonly bool _canCreateStockIn;
    private readonly bool _canUpdateStockIn;
    private readonly bool _canSubmitStockIn;
    private readonly bool _canApproveStockIn;
    private readonly bool _canPostStockIn;
    private readonly bool _canCreateStockOut;
    private readonly bool _canUpdateStockOut;
    private readonly bool _canSubmitStockOut;
    private readonly bool _canApproveStockOut;
    private readonly bool _canPostStockOut;
    private readonly bool _canCreateTransfer;
    private readonly bool _canUpdateTransfer;
    private readonly bool _canSubmitTransfer;
    private readonly bool _canApproveTransfer;
    private readonly bool _canPostTransfer;
    private readonly bool _canCreateStockOpname;
    private readonly bool _canUpdateStockOpname;
    private readonly bool _canSubmitStockOpname;
    private readonly bool _canApproveStockOpname;
    private readonly bool _canPostStockOpname;
    private string _itemSearchKeyword = string.Empty;
    private int _itemSearchPage = 1;
    private int _itemSearchTotalCount;
    private string _stockItemLookupKeyword = string.Empty;
    private int _stockItemLookupPage = 1;
    private int _stockItemLookupTotalCount;
    private bool _isCurrentAccountingPeriodOpen = true;
    private string _currentAccountingPeriodMonthText = DateTime.Today.ToString("yyyy-MM");
    private string _currentAccountingPeriodStatusText = "OPEN";

    public InventoryViewModel(
        IAccessControlService accessControlService,
        UserAccessContext accessContext,
        string companyDisplayName,
        string locationDisplayName,
        bool canOperateOpeningBalance = false)
    {
        _accessControlService = accessControlService;
        _actorUsername = string.IsNullOrWhiteSpace(accessContext.Username) ? "SYSTEM" : accessContext.Username.Trim();
        _companyId = accessContext.SelectedCompanyId;
        _locationId = accessContext.SelectedLocationId;
        _canOperateOpeningBalance = canOperateOpeningBalance;
        _canSyncUploadInventoryMaster = accessContext.HasAction("inventory", "api_inv", "sync_upload");
        _canSyncDownloadInventoryMaster = accessContext.HasAction("inventory", "api_inv", "sync_download");
        _canCreateCategory = accessContext.HasAction("inventory", "kategori", "create");
        _canUpdateCategory = accessContext.HasAction("inventory", "kategori", "update");
        _canDeleteCategory = accessContext.HasAction("inventory", "kategori", "delete");
        _canCreateItem = accessContext.HasAction("inventory", "item", "create");
        _canUpdateItem = accessContext.HasAction("inventory", "item", "update");
        _canDeleteItem = accessContext.HasAction("inventory", "item", "delete");
        _canCreateUnit = accessContext.HasAction("inventory", "satuan", "create");
        _canUpdateUnit = accessContext.HasAction("inventory", "satuan", "update");
        _canDeleteUnit = accessContext.HasAction("inventory", "satuan", "delete");
        _canCreateWarehouse = accessContext.HasAction("inventory", "gudang", "create");
        _canUpdateWarehouse = accessContext.HasAction("inventory", "gudang", "update");
        _canDeleteWarehouse = accessContext.HasAction("inventory", "gudang", "delete");
        _canImportInventoryMasterData = accessContext.HasAction("inventory", "api_inv", "import_master_data");
        _canDownloadInventoryImportTemplate = accessContext.HasAction("inventory", "api_inv", "download_import_template");
        _canCreateStockIn = accessContext.HasAction("inventory", "stock_in", "create");
        _canUpdateStockIn = accessContext.HasAction("inventory", "stock_in", "update");
        _canSubmitStockIn = accessContext.HasAction("inventory", "stock_in", "submit");
        _canApproveStockIn = accessContext.HasAction("inventory", "stock_in", "approve");
        _canPostStockIn = accessContext.HasAction("inventory", "stock_in", "post");
        _canCreateStockOut = accessContext.HasAction("inventory", "stock_out", "create");
        _canUpdateStockOut = accessContext.HasAction("inventory", "stock_out", "update");
        _canSubmitStockOut = accessContext.HasAction("inventory", "stock_out", "submit");
        _canApproveStockOut = accessContext.HasAction("inventory", "stock_out", "approve");
        _canPostStockOut = accessContext.HasAction("inventory", "stock_out", "post");
        _canCreateTransfer = accessContext.HasAction("inventory", "transfer", "create");
        _canUpdateTransfer = accessContext.HasAction("inventory", "transfer", "update");
        _canSubmitTransfer = accessContext.HasAction("inventory", "transfer", "submit");
        _canApproveTransfer = accessContext.HasAction("inventory", "transfer", "approve");
        _canPostTransfer = accessContext.HasAction("inventory", "transfer", "post");
        _canCreateStockOpname = accessContext.HasAction("inventory", "stock_opname", "create");
        _canUpdateStockOpname = accessContext.HasAction("inventory", "stock_opname", "update");
        _canSubmitStockOpname = accessContext.HasAction("inventory", "stock_opname", "submit");
        _canApproveStockOpname = accessContext.HasAction("inventory", "stock_opname", "approve");
        _canPostStockOpname = accessContext.HasAction("inventory", "stock_opname", "post");

        CompanyDisplayName = string.IsNullOrWhiteSpace(companyDisplayName) ? "-" : companyDisplayName.Trim();
        LocationDisplayName = string.IsNullOrWhiteSpace(locationDisplayName) ? "-" : locationDisplayName.Trim();

        Categories = new ObservableCollection<ManagedInventoryCategory>();
        ActiveCategories = new ObservableCollection<ManagedInventoryCategory>();
        Items = new ObservableCollection<ManagedInventoryItem>();
        ItemSearchResults = new ObservableCollection<ManagedInventoryItem>();
        StockItemLookupOptions = new ObservableCollection<ManagedInventoryItem>();
        StockEntries = new ObservableCollection<ManagedStockEntry>();
        Accounts = new ObservableCollection<ManagedAccount>();
        StockOutExpenseAccountOptions = new ObservableCollection<ManagedAccount>();
        Units = new ObservableCollection<ManagedInventoryUnit>();
        Warehouses = new ObservableCollection<ManagedWarehouse>();
        UomOptions = new ObservableCollection<string> { "PCS", "KG", "LTR", "TON", "SET", "BOX" };

        RefreshCommand = new RelayCommand(() => _ = LoadDataAsync());
        SyncFromMasterCommand = new RelayCommand(() => _ = SyncFromMasterAsync());

        NewCategoryCommand = new RelayCommand(NewCategory);
        SaveCategoryCommand = new RelayCommand(() => _ = SaveCategoryAsync());
        DeactivateCategoryCommand = new RelayCommand(() => _ = DeactivateCategoryAsync());

        NewItemCommand = new RelayCommand(NewItem);
        SaveItemCommand = new RelayCommand(() => _ = SaveItemAsync());
        DeactivateItemCommand = new RelayCommand(() => _ = DeactivateItemAsync());
        ImportInventoryMasterDataCommand = new RelayCommand(() => _ = ImportInventoryMasterDataAsync());
        DownloadInventoryImportTemplateCommand = new RelayCommand(() => DownloadInventoryImportTemplate());
        SearchItemsCommand = new RelayCommand(() => _ = SearchItemsAsync());
        ResetItemSearchCommand = new RelayCommand(() => _ = ResetItemSearchAsync());
        PreviousItemPageCommand = new RelayCommand(() => _ = GoToPreviousItemPageAsync());
        NextItemPageCommand = new RelayCommand(() => _ = GoToNextItemPageAsync());
        SearchStockItemLookupCommand = new RelayCommand(() => _ = SearchStockItemLookupAsync());
        ResetStockItemLookupCommand = new RelayCommand(() => _ = ResetStockItemLookupAsync());
        PreviousStockItemLookupPageCommand = new RelayCommand(() => _ = GoToPreviousStockItemLookupPageAsync());
        NextStockItemLookupPageCommand = new RelayCommand(() => _ = GoToNextStockItemLookupPageAsync());

        NewUnitCommand = new RelayCommand(NewUnit);
        SaveUnitCommand = new RelayCommand(() => _ = SaveUnitAsync());
        DeactivateUnitCommand = new RelayCommand(() => _ = DeactivateUnitAsync());

        NewWarehouseCommand = new RelayCommand(NewWarehouse);
        SaveWarehouseCommand = new RelayCommand(() => _ = SaveWarehouseAsync());
        DeactivateWarehouseCommand = new RelayCommand(() => _ = DeactivateWarehouseAsync());

        InitializeStockInCommands();
        InitializeStockOutCommands();
        InitializeTransferCommands();
        InitializeStockOpnameCommands();
        InitializeDashboardCommands();
        InitializeReportsCommands();
    }

    public string CompanyDisplayName { get; }

    public string LocationDisplayName { get; }

    public ObservableCollection<ManagedInventoryCategory> Categories { get; }

    public ObservableCollection<ManagedInventoryCategory> ActiveCategories { get; }

    public ObservableCollection<ManagedInventoryItem> Items { get; }

    public ObservableCollection<ManagedInventoryItem> ItemSearchResults { get; }

    public ObservableCollection<ManagedInventoryItem> StockItemLookupOptions { get; }

    public ObservableCollection<ManagedStockEntry> StockEntries { get; }

    public ObservableCollection<ManagedAccount> Accounts { get; }

    public ObservableCollection<ManagedAccount> StockOutExpenseAccountOptions { get; }

    public ObservableCollection<ManagedInventoryUnit> Units { get; }

    public ObservableCollection<ManagedWarehouse> Warehouses { get; }

    public ObservableCollection<string> UomOptions { get; }

    public ICommand RefreshCommand { get; }

    public ICommand SyncFromMasterCommand { get; }

    public ICommand NewCategoryCommand { get; }

    public ICommand SaveCategoryCommand { get; }

    public ICommand DeactivateCategoryCommand { get; }

    public ICommand NewItemCommand { get; }

    public ICommand SaveItemCommand { get; }

    public ICommand DeactivateItemCommand { get; }

    public ICommand ImportInventoryMasterDataCommand { get; }

    public ICommand DownloadInventoryImportTemplateCommand { get; }

    public ICommand SearchItemsCommand { get; }

    public ICommand ResetItemSearchCommand { get; }

    public ICommand PreviousItemPageCommand { get; }

    public ICommand NextItemPageCommand { get; }

    public ICommand SearchStockItemLookupCommand { get; }

    public ICommand ResetStockItemLookupCommand { get; }

    public ICommand PreviousStockItemLookupPageCommand { get; }

    public ICommand NextStockItemLookupPageCommand { get; }

    public ICommand NewUnitCommand { get; }

    public ICommand SaveUnitCommand { get; }

    public ICommand DeactivateUnitCommand { get; }

    public ICommand NewWarehouseCommand { get; }

    public ICommand SaveWarehouseCommand { get; }

    public ICommand DeactivateWarehouseCommand { get; }

    public string SelectedInventoryTab
    {
        get => _selectedInventoryTab;
        set
        {
            if (!SetProperty(ref _selectedInventoryTab, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsInventoryDashboardSelected));
            OnPropertyChanged(nameof(IsInventoryItemSelected));
            OnPropertyChanged(nameof(IsInventoryCategorySelected));
            OnPropertyChanged(nameof(IsInventoryUnitSelected));
            OnPropertyChanged(nameof(IsInventoryWarehouseSelected));
            OnPropertyChanged(nameof(IsInventoryStockInSelected));
            OnPropertyChanged(nameof(IsInventoryStockOutSelected));
            OnPropertyChanged(nameof(IsInventoryTransferSelected));
            OnPropertyChanged(nameof(IsInventoryStockOpnameSelected));
            OnPropertyChanged(nameof(IsInventoryStockPositionSelected));
            OnPropertyChanged(nameof(IsInventoryReportsSelected));
            OnPropertyChanged(nameof(IsInventoryPlaceholderSelected));
            OnPropertyChanged(nameof(InventoryPlaceholderTitle));
            OnPropertyChanged(nameof(InventoryPlaceholderDescription));

            ApplyInventoryTabPreset(value);
        }
    }

    public bool IsInventoryDashboardSelected => SelectedInventoryTab == "dashboard";

    public bool IsInventoryItemSelected => SelectedInventoryTab == "item";

    public bool IsInventoryCategorySelected => SelectedInventoryTab == "kategori";

    public bool IsInventoryUnitSelected => SelectedInventoryTab == "satuan";

    public bool IsInventoryWarehouseSelected => SelectedInventoryTab == "gudang";

    public bool IsInventoryStockInSelected => SelectedInventoryTab == "stock_in";

    public bool IsInventoryStockOutSelected => SelectedInventoryTab == "stock_out";

    public bool IsInventoryTransferSelected => SelectedInventoryTab == "transfer";

    public bool IsInventoryStockOpnameSelected => SelectedInventoryTab == "stock_opname";

    public bool IsInventoryStockPositionSelected => SelectedInventoryTab == "stock_position";

    public bool IsInventoryReportsSelected => ReportBackedTabs.Contains(SelectedInventoryTab);

    public bool IsInventoryPlaceholderSelected => PlaceholderTabs.Contains(SelectedInventoryTab);

    public string InventoryPlaceholderTitle => SelectedInventoryTab switch
    {
        "lokasi_penyimpanan" => "Lokasi Penyimpanan",
        "stock_adjustment" => "Penyesuaian Stok",
        "transaction_history" => "Histori Transaksi",
        "stock_card" => "Kartu Stok",
        "report_stock_opname" => "Laporan Stok Opname",
        _ => "Fitur Inventori"
    };

    public string InventoryPlaceholderDescription => SelectedInventoryTab switch
    {
        "lokasi_penyimpanan" => "Submenu ini sudah disiapkan. Implementasi manajemen lokasi penyimpanan akan ditambahkan pada iterasi berikutnya.",
        "stock_adjustment" => "Submenu ini sudah disiapkan. Modul penyesuaian stok terpisah dari stok opname akan ditambahkan pada iterasi berikutnya.",
        "transaction_history" => "Submenu ini sudah disiapkan. Riwayat transaksi lintas dokumen akan ditambahkan pada iterasi berikutnya.",
        "stock_card" => "Submenu ini sudah disiapkan. Kartu stok per item/lokasi akan ditambahkan pada iterasi berikutnya.",
        "report_stock_opname" => "Submenu ini sudah disiapkan. Laporan stok opname khusus akan ditambahkan pada iterasi berikutnya.",
        _ => "Fitur ini belum tersedia."
    };

    public ManagedInventoryCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedCategory));
            RaiseInventoryActionStateChanged();
        }
    }

    public ManagedInventoryItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }

            RefreshItemCategoryOptions(value?.CategoryId);
            OnPropertyChanged(nameof(HasSelectedItem));
            RaiseInventoryActionStateChanged();
        }
    }

    public ManagedStockEntry? SelectedStockEntry
    {
        get => _selectedStockEntry;
        set => SetProperty(ref _selectedStockEntry, value);
    }

    public bool HasSelectedCategory => SelectedCategory is not null && SelectedCategory.Id > 0;

    public bool HasSelectedItem => SelectedItem is not null && SelectedItem.Id > 0;

    public string ItemSearchKeyword
    {
        get => _itemSearchKeyword;
        set => SetProperty(ref _itemSearchKeyword, value);
    }

    public int ItemSearchPage
    {
        get => _itemSearchPage;
        private set
        {
            if (!SetProperty(ref _itemSearchPage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanGoToPreviousItemPage));
            OnPropertyChanged(nameof(CanGoToNextItemPage));
            OnPropertyChanged(nameof(ItemSearchPageSummary));
        }
    }

    public int ItemSearchTotalCount
    {
        get => _itemSearchTotalCount;
        private set
        {
            if (!SetProperty(ref _itemSearchTotalCount, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ItemSearchTotalPages));
            OnPropertyChanged(nameof(CanGoToNextItemPage));
            OnPropertyChanged(nameof(ItemSearchPageSummary));
        }
    }

    public int ItemSearchPageSize => ItemSearchDefaultPageSize;

    public int ItemSearchTotalPages => Math.Max(1, (int)Math.Ceiling(ItemSearchTotalCount / (double)ItemSearchPageSize));

    public bool CanGoToPreviousItemPage => ItemSearchPage > 1;

    public bool CanGoToNextItemPage => ItemSearchPage < ItemSearchTotalPages;

    public string ItemSearchPageSummary => ItemSearchTotalCount <= 0
        ? "Total 0 item"
        : $"Halaman {ItemSearchPage:N0} / {ItemSearchTotalPages:N0} - Total {ItemSearchTotalCount:N0} item";

    public string StockItemLookupKeyword
    {
        get => _stockItemLookupKeyword;
        set => SetProperty(ref _stockItemLookupKeyword, value);
    }

    public int StockItemLookupPage
    {
        get => _stockItemLookupPage;
        private set
        {
            if (!SetProperty(ref _stockItemLookupPage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanGoToPreviousStockItemLookupPage));
            OnPropertyChanged(nameof(CanGoToNextStockItemLookupPage));
            OnPropertyChanged(nameof(StockItemLookupPageSummary));
        }
    }

    public int StockItemLookupTotalCount
    {
        get => _stockItemLookupTotalCount;
        private set
        {
            if (!SetProperty(ref _stockItemLookupTotalCount, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StockItemLookupTotalPages));
            OnPropertyChanged(nameof(CanGoToNextStockItemLookupPage));
            OnPropertyChanged(nameof(StockItemLookupPageSummary));
        }
    }

    public int StockItemLookupPageSize => StockItemLookupDefaultPageSize;

    public int StockItemLookupTotalPages => Math.Max(1, (int)Math.Ceiling(StockItemLookupTotalCount / (double)StockItemLookupPageSize));

    public bool CanGoToPreviousStockItemLookupPage => StockItemLookupPage > 1;

    public bool CanGoToNextStockItemLookupPage => StockItemLookupPage < StockItemLookupTotalPages;

    public string StockItemLookupPageSummary => StockItemLookupTotalCount <= 0
        ? "Item lookup: 0 data"
        : $"Item lookup: halaman {StockItemLookupPage:N0}/{StockItemLookupTotalPages:N0} - total {StockItemLookupTotalCount:N0}";

    public long? MasterCompanyId
    {
        get => _masterCompanyId;
        private set => SetProperty(ref _masterCompanyId, value);
    }

    public string MasterCompanyLabel
    {
        get => _masterCompanyLabel;
        private set => SetProperty(ref _masterCompanyLabel, value);
    }

    public bool CanMaintainMasterInventoryData
    {
        get => _canMaintainMasterInventoryData;
        private set
        {
            if (!SetProperty(ref _canMaintainMasterInventoryData, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsInventoryReadOnlyByMasterPolicy));
            OnPropertyChanged(nameof(CanSyncFromMaster));
            OnPropertyChanged(nameof(SyncActionLabel));
            OnPropertyChanged(nameof(SyncActionTooltip));
            RaiseInventoryActionStateChanged();
        }
    }

    public bool IsInventoryReadOnlyByMasterPolicy => !CanMaintainMasterInventoryData;

    public bool CanOperateOpeningBalance
    {
        get => _canOperateOpeningBalance;
        private set => SetProperty(ref _canOperateOpeningBalance, value);
    }

    public bool CanSyncFromMaster =>
        MasterCompanyId.HasValue &&
        MasterCompanyId.Value > 0 &&
        !IsBusy &&
        (CanMaintainMasterInventoryData ? _canSyncUploadInventoryMaster : _canSyncDownloadInventoryMaster);

    public string SyncActionLabel => CanMaintainMasterInventoryData ? "Upload ke Pusat" : "Download dari Pusat";

    public string SyncActionTooltip => BuildSyncActionTooltip();

    public bool ShowInventoryPolicyBanner => CanSyncFromMaster;

    public string InventoryPolicyBannerText => CanMaintainMasterInventoryData
        ? "Company ini adalah master data inventory. Perubahan akan menjadi sumber sinkronisasi pusat."
        : $"Company ini read-only untuk master data inventory. Gunakan Download dari pusat ({MasterCompanyLabel}).";

    public bool CanImportInventoryMasterData =>
        CanMaintainMasterInventoryData &&
        !IsBusy &&
        _canImportInventoryMasterData;

    public bool CanDownloadInventoryImportTemplate =>
        CanMaintainMasterInventoryData &&
        !IsBusy &&
        _canDownloadInventoryImportTemplate;

    public string ImportInventoryMasterDataTooltip => BuildInventoryMasterImportTooltip("import master inventory");

    public string DownloadInventoryImportTemplateTooltip => BuildInventoryMasterImportTooltip("download template import inventory");

    public bool CanCreateCategory => CanCreateMasterEntity(_canCreateCategory);

    public bool CanSaveCategory => CanSaveMasterEntity(SelectedCategory?.Id, _canCreateCategory, _canUpdateCategory);

    public bool CanDeactivateCategory => CanDeactivateMasterEntity(SelectedCategory?.Id, _canDeleteCategory);

    public string NewCategoryTooltip => BuildNewMasterEntityTooltip("kategori", _canCreateCategory);

    public string SaveCategoryTooltip => BuildSaveMasterEntityTooltip(
        "kategori",
        SelectedCategory?.Id,
        _canCreateCategory,
        _canUpdateCategory);

    public string DeactivateCategoryTooltip => BuildDeactivateMasterEntityTooltip(
        "kategori",
        SelectedCategory?.Id,
        _canDeleteCategory);

    public bool CanCreateItem => CanCreateMasterEntity(_canCreateItem);

    public bool CanSaveItem => CanSaveMasterEntity(SelectedItem?.Id, _canCreateItem, _canUpdateItem);

    public bool CanDeactivateItem => CanDeactivateMasterEntity(SelectedItem?.Id, _canDeleteItem);

    public string NewItemTooltip => BuildNewMasterEntityTooltip("item", _canCreateItem);

    public string SaveItemTooltip => BuildSaveMasterEntityTooltip(
        "item",
        SelectedItem?.Id,
        _canCreateItem,
        _canUpdateItem);

    public string DeactivateItemTooltip => BuildDeactivateMasterEntityTooltip(
        "item",
        SelectedItem?.Id,
        _canDeleteItem);

    public bool IsCurrentAccountingPeriodOpen
    {
        get => _isCurrentAccountingPeriodOpen;
        private set
        {
            if (!SetProperty(ref _isCurrentAccountingPeriodOpen, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CurrentAccountingPeriodStatusText));
            OnPropertyChanged(nameof(CanOperateCurrentPeriodTransactions));
            OnPropertyChanged(nameof(ShowInventoryPeriodBanner));
            OnPropertyChanged(nameof(InventoryPeriodBannerText));
            OnPropertyChanged(nameof(InventoryPeriodActionTooltip));
            RaiseInventoryActionStateChanged();
        }
    }

    public string CurrentAccountingPeriodMonthText
    {
        get => _currentAccountingPeriodMonthText;
        private set
        {
            if (!SetProperty(ref _currentAccountingPeriodMonthText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowInventoryPeriodBanner));
            OnPropertyChanged(nameof(InventoryPeriodBannerText));
            OnPropertyChanged(nameof(InventoryPeriodActionTooltip));
            RaiseInventoryActionStateChanged();
        }
    }

    public string CurrentAccountingPeriodStatusText
    {
        get => _currentAccountingPeriodStatusText;
        private set => SetProperty(ref _currentAccountingPeriodStatusText, value);
    }

    public bool ShowInventoryPeriodBanner => !string.IsNullOrWhiteSpace(CurrentAccountingPeriodMonthText);

    public bool CanOperateCurrentPeriodTransactions => IsCurrentAccountingPeriodOpen && !IsBusy;

    public string InventoryPeriodBannerText => IsCurrentAccountingPeriodOpen
        ? $"Periode {CurrentAccountingPeriodMonthText} OPEN. Transaksi inventory periode aktif dapat diproses."
        : $"Periode {CurrentAccountingPeriodMonthText} CLOSED. Aksi simpan/submit/approve/post inventory dinonaktifkan.";

    public string InventoryPeriodActionTooltip => IsCurrentAccountingPeriodOpen
        ? $"Periode {CurrentAccountingPeriodMonthText} OPEN."
        : $"Periode {CurrentAccountingPeriodMonthText} CLOSED. Buka ulang periode dari Settings > Periode Akuntansi jika diperlukan.";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanOperateCurrentPeriodTransactions));
            OnPropertyChanged(nameof(CanSyncFromMaster));
            OnPropertyChanged(nameof(SyncActionTooltip));
            RaiseInventoryActionStateChanged();
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded)
        {
            return;
        }

        await LoadDataAsync();
    }

    public void ApplyCurrentAccountingPeriodState(DateTime periodMonth, bool isOpen)
    {
        var month = new DateTime(periodMonth.Year, periodMonth.Month, 1);
        IsCurrentAccountingPeriodOpen = isOpen;
        CurrentAccountingPeriodMonthText = month.ToString("yyyy-MM");
        CurrentAccountingPeriodStatusText = isOpen ? "OPEN" : "CLOSED";
    }

    private async Task LoadDataAsync(long? selectedCategoryId = null, long? selectedItemId = null, bool forceReload = false)
    {
        if (IsBusy && !forceReload)
        {
            return;
        }

        var preferredSelectedItemId = selectedItemId ?? SelectedItem?.Id;

        try
        {
            IsBusy = true;
            StatusMessage = "Memuat data inventori...";

            var data = await _accessControlService.GetInventoryWorkspaceDataAsync(_companyId, _locationId);

            ReplaceCollection(Categories, data.Categories);
            ReplaceCollection(Items, data.Items);
            ReplaceCollection(StockEntries, data.StockEntries);
            ReplaceCollection(Accounts, data.Accounts);
            RefreshStockOutExpenseAccountOptions();
            ReplaceCollection(Units, data.Units);
            ReplaceCollection(Warehouses, data.Warehouses);

            // Update UomOptions from Units if available
            if (data.Units.Count > 0)
            {
                UomOptions.Clear();
                foreach (var unit in data.Units.Where(u => u.IsActive))
                {
                    UomOptions.Add(unit.Code);
                }
            }

            SelectedCategory = selectedCategoryId.HasValue
                ? Categories.FirstOrDefault(x => x.Id == selectedCategoryId.Value) ?? Categories.FirstOrDefault()
                : Categories.FirstOrDefault();

            await LoadStockItemLookupAsync(StockItemLookupPage);
            await LoadItemSearchResultsAsync(ItemSearchPage, preferredSelectedItemId);
            ApplyMasterInventoryContext(data);
            await RefreshCurrentAccountingPeriodStateAsync();

            _isLoaded = true;
            StatusMessage = CanMaintainMasterInventoryData
                ? "Data inventori siap digunakan."
                : BuildMasterReadOnlyMessage();
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(InventoryViewModel),
                "LoadDataFailed",
                $"action=load_inventory company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = "Gagal memuat data inventori.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCurrentAccountingPeriodStateAsync()
    {
        var month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var isOpen = true;

        try
        {
            var periods = await _accessControlService.GetAccountingPeriodsAsync(_companyId, _locationId);
            var current = periods.FirstOrDefault(x => x.PeriodMonth.Date == month.Date);
            isOpen = current?.IsOpen ?? true;
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(
                nameof(InventoryViewModel),
                "RefreshCurrentAccountingPeriodStateFailed",
                $"action=refresh_inventory_period_status company_id={_companyId} location_id={_locationId}",
                ex);
        }

        ApplyCurrentAccountingPeriodState(month, isOpen);
    }

    private async Task<bool> EnsureAccountingPeriodOpenForDateAsync(DateTime date, string actionName)
    {
        var month = new DateTime(date.Year, date.Month, 1);
        try
        {
            var periods = await _accessControlService.GetAccountingPeriodsAsync(_companyId, _locationId);
            var period = periods.FirstOrDefault(x => x.PeriodMonth.Date == month.Date);
            var isOpen = period?.IsOpen ?? true;

            var currentMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            if (month.Date == currentMonth.Date)
            {
                ApplyCurrentAccountingPeriodState(month, isOpen);
            }

            if (isOpen)
            {
                return true;
            }

            StatusMessage = $"Periode {month:yyyy-MM} CLOSED. {actionName} tidak dapat diproses.";
            return false;
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(
                nameof(InventoryViewModel),
                "EnsureAccountingPeriodOpenFailed",
                $"action=validate_inventory_period company_id={_companyId} location_id={_locationId} period={month:yyyy-MM}",
                ex);
            StatusMessage = "Gagal memvalidasi status periode akuntansi.";
            return false;
        }
    }

    private async Task SearchItemsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await LoadItemSearchResultsAsync(1, SelectedItem?.Id);
            StatusMessage = ItemSearchTotalCount <= 0
                ? "Data item tidak ditemukan."
                : $"Menampilkan {ItemSearchPageSummary.ToLowerInvariant()}.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "SearchItemsFailed", ex.Message);
            StatusMessage = "Gagal mencari data item.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResetItemSearchAsync()
    {
        ItemSearchKeyword = string.Empty;
        await SearchItemsAsync();
    }

    private async Task GoToPreviousItemPageAsync()
    {
        if (!CanGoToPreviousItemPage || IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await LoadItemSearchResultsAsync(ItemSearchPage - 1, SelectedItem?.Id);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "PreviousItemPageFailed", ex.Message);
            StatusMessage = "Gagal membuka halaman item sebelumnya.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GoToNextItemPageAsync()
    {
        if (!CanGoToNextItemPage || IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await LoadItemSearchResultsAsync(ItemSearchPage + 1, SelectedItem?.Id);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "NextItemPageFailed", ex.Message);
            StatusMessage = "Gagal membuka halaman item berikutnya.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadItemSearchResultsAsync(int requestedPage, long? preferredSelectedItemId = null)
    {
        var searchResult = await _accessControlService.SearchInventoryItemsAsync(
            _companyId,
            new InventoryItemSearchFilter
            {
                Keyword = ItemSearchKeyword,
                Page = requestedPage,
                PageSize = ItemSearchDefaultPageSize
            });

        ReplaceCollection(ItemSearchResults, searchResult.Items);
        ItemSearchPage = Math.Max(1, searchResult.Page);
        ItemSearchTotalCount = Math.Max(0, searchResult.TotalCount);

        ManagedInventoryItem? selectedItem = null;
        if (preferredSelectedItemId.HasValue && preferredSelectedItemId.Value > 0)
        {
            selectedItem = ItemSearchResults.FirstOrDefault(x => x.Id == preferredSelectedItemId.Value);
        }

        selectedItem ??= ItemSearchResults.FirstOrDefault(x => x.Id == SelectedItem?.Id);
        selectedItem ??= ItemSearchResults.FirstOrDefault();
        SelectedItem = selectedItem;
    }

    private async Task SearchStockItemLookupAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await LoadStockItemLookupAsync(1);
            StatusMessage = StockItemLookupTotalCount <= 0
                ? "Data item lookup tidak ditemukan."
                : $"Lookup item siap. {StockItemLookupPageSummary}.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "SearchStockItemLookupFailed", ex.Message);
            StatusMessage = "Gagal memuat item lookup.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResetStockItemLookupAsync()
    {
        StockItemLookupKeyword = string.Empty;
        await SearchStockItemLookupAsync();
    }

    private async Task GoToPreviousStockItemLookupPageAsync()
    {
        if (!CanGoToPreviousStockItemLookupPage || IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await LoadStockItemLookupAsync(StockItemLookupPage - 1);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "PreviousStockItemLookupPageFailed", ex.Message);
            StatusMessage = "Gagal membuka halaman lookup item sebelumnya.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GoToNextStockItemLookupPageAsync()
    {
        if (!CanGoToNextStockItemLookupPage || IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await LoadStockItemLookupAsync(StockItemLookupPage + 1);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(nameof(InventoryViewModel), "NextStockItemLookupPageFailed", ex.Message);
            StatusMessage = "Gagal membuka halaman lookup item berikutnya.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadStockItemLookupAsync(int requestedPage)
    {
        var searchResult = await _accessControlService.SearchInventoryItemsAsync(
            _companyId,
            new InventoryItemSearchFilter
            {
                Keyword = StockItemLookupKeyword,
                Page = requestedPage,
                PageSize = StockItemLookupDefaultPageSize
            });

        ReplaceCollection(StockItemLookupOptions, searchResult.Items);
        StockItemLookupPage = Math.Max(1, searchResult.Page);
        StockItemLookupTotalCount = Math.Max(0, searchResult.TotalCount);
        EnsureStockItemLookupContainsCurrentLines();
    }

    private void EnsureStockItemLookupContainsCurrentLines()
    {
        EnsureStockItemLookupContains(StockInLines.Select(x => x.ItemId));
        EnsureStockItemLookupContains(StockOutLines.Select(x => x.ItemId));
        EnsureStockItemLookupContains(TransferLines.Select(x => x.ItemId));
    }

    private void EnsureStockItemLookupContains(IEnumerable<long> itemIds)
    {
        var missingIds = itemIds
            .Where(x => x > 0)
            .Distinct()
            .Where(itemId => StockItemLookupOptions.All(x => x.Id != itemId))
            .ToList();

        if (missingIds.Count == 0)
        {
            return;
        }

        foreach (var missingId in missingIds)
        {
            var fallbackItem = Items.FirstOrDefault(x => x.Id == missingId);
            if (fallbackItem is null)
            {
                continue;
            }

            StockItemLookupOptions.Add(fallbackItem);
        }
    }

    // --- Category CRUD ---

    private void NewCategory()
    {
        if (!CanCreateCategory)
        {
            StatusMessage = NewCategoryTooltip;
            return;
        }

        SelectedCategory = new ManagedInventoryCategory
        {
            Id = 0,
            CompanyId = _companyId,
            Code = string.Empty,
            Name = string.Empty,
            AccountCode = string.Empty,
            IsActive = true
        };

        StatusMessage = "Input kategori baru siap.";
    }

    private async Task SaveCategoryAsync()
    {
        if (!CanSaveCategory || SelectedCategory is null)
        {
            StatusMessage = SaveCategoryTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SaveInventoryCategoryAsync(_companyId, SelectedCategory, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(selectedCategoryId: result.EntityId, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeactivateCategoryAsync()
    {
        if (!CanDeactivateCategory || SelectedCategory is null || SelectedCategory.Id <= 0)
        {
            StatusMessage = DeactivateCategoryTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SoftDeleteInventoryCategoryAsync(_companyId, SelectedCategory.Id, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Item CRUD ---

    private void NewItem()
    {
        if (!CanCreateItem)
        {
            StatusMessage = NewItemTooltip;
            return;
        }

        SelectedItem = new ManagedInventoryItem
        {
            Id = 0,
            CompanyId = _companyId,
            Code = string.Empty,
            Name = string.Empty,
            Uom = "PCS",
            Category = string.Empty,
            CategoryId = null,
            IsActive = true
        };

        StatusMessage = "Input item baru siap.";
    }

    private async Task SaveItemAsync()
    {
        if (!CanSaveItem || SelectedItem is null)
        {
            StatusMessage = SaveItemTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SaveInventoryItemAsync(_companyId, SelectedItem, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(selectedItemId: result.EntityId, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeactivateItemAsync()
    {
        if (!CanDeactivateItem || SelectedItem is null || SelectedItem.Id <= 0)
        {
            StatusMessage = DeactivateItemTooltip;
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SoftDeleteInventoryItemAsync(_companyId, SelectedItem.Id, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncFromMasterAsync()
    {
        if (!CanSyncFromMaster)
        {
            StatusMessage = "Sync pusat tidak tersedia untuk company ini.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var shouldReload = false;
        var selectedCategoryId = SelectedCategory?.Id;
        var selectedItemId = SelectedItem?.Id;

        try
        {
            IsBusy = true;
            var result = CanMaintainMasterInventoryData
                ? await _accessControlService.UploadInventoryToCentralAsync(_companyId, _actorUsername)
                : await _accessControlService.DownloadInventoryFromCentralAsync(_companyId, _actorUsername);
            StatusMessage = result.Message;
            shouldReload = result.IsSuccess && !CanMaintainMasterInventoryData;
        }
        finally
        {
            IsBusy = false;
        }

        if (shouldReload)
        {
            await LoadDataAsync(selectedCategoryId, selectedItemId);
        }
    }

    private void ApplyMasterInventoryContext(InventoryWorkspaceData data)
    {
        MasterCompanyId = data.MasterCompanyId;
        MasterCompanyLabel = data.MasterCompanyId.HasValue && data.MasterCompanyId.Value > 0
            ? string.IsNullOrWhiteSpace(data.MasterCompanyCode)
                ? $"ID {data.MasterCompanyId.Value}"
                : $"{data.MasterCompanyCode} - {data.MasterCompanyName}"
            : "Belum dikonfigurasi";
        CanMaintainMasterInventoryData = data.CanMaintainMasterInventoryData;
        OnPropertyChanged(nameof(CanSyncFromMaster));
        OnPropertyChanged(nameof(SyncActionLabel));
        OnPropertyChanged(nameof(SyncActionTooltip));
        OnPropertyChanged(nameof(ShowInventoryPolicyBanner));
        OnPropertyChanged(nameof(InventoryPolicyBannerText));
    }

    private Task RefreshWorkspaceAfterMutationAsync()
    {
        ClearOutboundAutoCostCache();
        return LoadDataAsync(SelectedCategory?.Id, SelectedItem?.Id, forceReload: true);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void RefreshItemCategoryOptions(long? selectedCategoryId = null)
    {
        ActiveCategories.Clear();

        foreach (var category in Categories.Where(x => x.IsActive))
        {
            ActiveCategories.Add(category);
        }

        if (!selectedCategoryId.HasValue || selectedCategoryId.Value <= 0)
        {
            return;
        }

        var selectedInactive = Categories.FirstOrDefault(x => x.Id == selectedCategoryId.Value && !x.IsActive);
        if (selectedInactive is not null && ActiveCategories.All(x => x.Id != selectedInactive.Id))
        {
            ActiveCategories.Add(selectedInactive);
        }
    }

    private string BuildMasterReadOnlyMessage() =>
        $"Company ini read-only untuk master data inventory. Gunakan Download dari pusat ({MasterCompanyLabel}).";

    private void RaiseInventoryActionStateChanged()
    {
        OnPropertyChanged(nameof(CanCreateCategory));
        OnPropertyChanged(nameof(CanImportInventoryMasterData));
        OnPropertyChanged(nameof(CanDownloadInventoryImportTemplate));
        OnPropertyChanged(nameof(ImportInventoryMasterDataTooltip));
        OnPropertyChanged(nameof(DownloadInventoryImportTemplateTooltip));
        OnPropertyChanged(nameof(CanSaveCategory));
        OnPropertyChanged(nameof(CanDeactivateCategory));
        OnPropertyChanged(nameof(NewCategoryTooltip));
        OnPropertyChanged(nameof(SaveCategoryTooltip));
        OnPropertyChanged(nameof(DeactivateCategoryTooltip));

        OnPropertyChanged(nameof(CanCreateItem));
        OnPropertyChanged(nameof(CanSaveItem));
        OnPropertyChanged(nameof(CanDeactivateItem));
        OnPropertyChanged(nameof(NewItemTooltip));
        OnPropertyChanged(nameof(SaveItemTooltip));
        OnPropertyChanged(nameof(DeactivateItemTooltip));

        OnPropertyChanged(nameof(CanCreateUnit));
        OnPropertyChanged(nameof(CanSaveUnit));
        OnPropertyChanged(nameof(CanDeactivateUnit));
        OnPropertyChanged(nameof(NewUnitTooltip));
        OnPropertyChanged(nameof(SaveUnitTooltip));
        OnPropertyChanged(nameof(DeactivateUnitTooltip));

        OnPropertyChanged(nameof(CanCreateWarehouse));
        OnPropertyChanged(nameof(CanSaveWarehouse));
        OnPropertyChanged(nameof(CanDeactivateWarehouse));
        OnPropertyChanged(nameof(NewWarehouseTooltip));
        OnPropertyChanged(nameof(SaveWarehouseTooltip));
        OnPropertyChanged(nameof(DeactivateWarehouseTooltip));

        OnPropertyChanged(nameof(CanCreateStockIn));
        OnPropertyChanged(nameof(CanSaveStockInDraft));
        OnPropertyChanged(nameof(CanSubmitStockIn));
        OnPropertyChanged(nameof(CanApproveStockIn));
        OnPropertyChanged(nameof(CanPostStockIn));
        OnPropertyChanged(nameof(NewStockInTooltip));
        OnPropertyChanged(nameof(SaveStockInDraftTooltip));
        OnPropertyChanged(nameof(SubmitStockInTooltip));
        OnPropertyChanged(nameof(ApproveStockInTooltip));
        OnPropertyChanged(nameof(PostStockInTooltip));

        OnPropertyChanged(nameof(CanCreateStockOut));
        OnPropertyChanged(nameof(CanSaveStockOutDraft));
        OnPropertyChanged(nameof(CanSubmitStockOut));
        OnPropertyChanged(nameof(CanApproveStockOut));
        OnPropertyChanged(nameof(CanPostStockOut));
        OnPropertyChanged(nameof(NewStockOutTooltip));
        OnPropertyChanged(nameof(SaveStockOutDraftTooltip));
        OnPropertyChanged(nameof(SubmitStockOutTooltip));
        OnPropertyChanged(nameof(ApproveStockOutTooltip));
        OnPropertyChanged(nameof(PostStockOutTooltip));

        OnPropertyChanged(nameof(CanCreateTransfer));
        OnPropertyChanged(nameof(CanSaveTransferDraft));
        OnPropertyChanged(nameof(CanSubmitTransfer));
        OnPropertyChanged(nameof(CanApproveTransfer));
        OnPropertyChanged(nameof(CanPostTransfer));
        OnPropertyChanged(nameof(NewTransferTooltip));
        OnPropertyChanged(nameof(SaveTransferDraftTooltip));
        OnPropertyChanged(nameof(SubmitTransferTooltip));
        OnPropertyChanged(nameof(ApproveTransferTooltip));
        OnPropertyChanged(nameof(PostTransferTooltip));

        OnPropertyChanged(nameof(CanCreateStockOpname));
        OnPropertyChanged(nameof(CanGenerateStockOpnameLines));
        OnPropertyChanged(nameof(CanSaveStockOpnameDraft));
        OnPropertyChanged(nameof(CanSubmitStockOpname));
        OnPropertyChanged(nameof(CanApproveStockOpname));
        OnPropertyChanged(nameof(CanPostStockOpname));
        OnPropertyChanged(nameof(NewStockOpnameTooltip));
        OnPropertyChanged(nameof(GenerateStockOpnameLinesTooltip));
        OnPropertyChanged(nameof(SaveStockOpnameDraftTooltip));
        OnPropertyChanged(nameof(SubmitStockOpnameTooltip));
        OnPropertyChanged(nameof(ApproveStockOpnameTooltip));
        OnPropertyChanged(nameof(PostStockOpnameTooltip));
    }

    private bool CanCreateInventoryDocument(bool hasCreatePermission)
    {
        return CanOperateCurrentPeriodTransactions && hasCreatePermission;
    }

    private bool CanCreateMasterEntity(bool hasCreatePermission)
    {
        return CanMaintainMasterInventoryData && !IsBusy && hasCreatePermission;
    }

    private bool CanSaveMasterEntity(long? entityId, bool canCreatePermission, bool canUpdatePermission)
    {
        if (!CanMaintainMasterInventoryData || IsBusy || !entityId.HasValue)
        {
            return false;
        }

        return entityId.Value > 0 ? canUpdatePermission : canCreatePermission;
    }

    private bool CanDeactivateMasterEntity(long? entityId, bool canDeletePermission)
    {
        return CanMaintainMasterInventoryData &&
               !IsBusy &&
               entityId.HasValue &&
               entityId.Value > 0 &&
               canDeletePermission;
    }

    private bool CanSaveInventoryDraft(long? entityId, string? currentStatus, bool canCreatePermission, bool canUpdatePermission)
    {
        if (!entityId.HasValue || !CanOperateCurrentPeriodTransactions)
        {
            return false;
        }

        if (entityId.Value > 0 && !string.Equals(currentStatus, "DRAFT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return entityId.Value > 0 ? canUpdatePermission : canCreatePermission;
    }

    private bool CanAdvanceInventoryWorkflow(long? entityId, string? currentStatus, string requiredStatus, bool hasPermission)
    {
        return entityId.HasValue &&
               entityId.Value > 0 &&
               CanOperateCurrentPeriodTransactions &&
               hasPermission &&
               string.Equals(currentStatus, requiredStatus, StringComparison.OrdinalIgnoreCase);
    }

    private string BuildNewInventoryDocumentTooltip(string documentLabel, bool hasCreatePermission)
    {
        if (IsBusy)
        {
            return "Sedang memproses data. Tunggu hingga selesai.";
        }

        if (!hasCreatePermission)
        {
            return $"Anda tidak memiliki izin membuat {documentLabel}.";
        }

        if (!IsCurrentAccountingPeriodOpen)
        {
            return InventoryPeriodActionTooltip;
        }

        return $"Buat {documentLabel} baru.";
    }

    private string BuildNewMasterEntityTooltip(string entityLabel, bool hasCreatePermission)
    {
        if (IsBusy)
        {
            return "Sedang memproses data. Tunggu hingga selesai.";
        }

        if (!CanMaintainMasterInventoryData)
        {
            return BuildMasterReadOnlyMessage();
        }

        if (!hasCreatePermission)
        {
            return $"Anda tidak memiliki izin membuat {entityLabel}.";
        }

        return $"Buat {entityLabel} baru.";
    }

    private string BuildSaveMasterEntityTooltip(
        string entityLabel,
        long? entityId,
        bool canCreatePermission,
        bool canUpdatePermission)
    {
        if (IsBusy)
        {
            return "Sedang memproses data. Tunggu hingga selesai.";
        }

        if (!CanMaintainMasterInventoryData)
        {
            return BuildMasterReadOnlyMessage();
        }

        if (!entityId.HasValue)
        {
            return $"Pilih atau buat {entityLabel} terlebih dahulu.";
        }

        if (entityId.Value > 0 && !canUpdatePermission)
        {
            return $"Anda tidak memiliki izin memperbarui {entityLabel}.";
        }

        if (entityId.Value <= 0 && !canCreatePermission)
        {
            return $"Anda tidak memiliki izin membuat {entityLabel}.";
        }

        return entityId.Value > 0
            ? $"Simpan perubahan {entityLabel}."
            : $"Simpan {entityLabel} baru.";
    }

    private string BuildDeactivateMasterEntityTooltip(
        string entityLabel,
        long? entityId,
        bool canDeletePermission)
    {
        if (IsBusy)
        {
            return "Sedang memproses data. Tunggu hingga selesai.";
        }

        if (!CanMaintainMasterInventoryData)
        {
            return BuildMasterReadOnlyMessage();
        }

        if (!entityId.HasValue || entityId.Value <= 0)
        {
            return $"Pilih {entityLabel} aktif terlebih dahulu.";
        }

        if (!canDeletePermission)
        {
            return $"Anda tidak memiliki izin menonaktifkan {entityLabel}.";
        }

        return $"Nonaktifkan {entityLabel} yang dipilih.";
    }

    private string BuildInventoryMasterImportTooltip(string actionLabel)
    {
        if (IsBusy)
        {
            return "Sedang memproses data. Tunggu hingga selesai.";
        }

        if (!CanMaintainMasterInventoryData)
        {
            return BuildMasterReadOnlyMessage();
        }

        if (string.Equals(actionLabel, "import master inventory", StringComparison.Ordinal))
        {
            return _canImportInventoryMasterData
                ? "Import master inventory dari file Excel."
                : "Anda tidak memiliki izin import master data inventory.";
        }

        if (string.Equals(actionLabel, "download template import inventory", StringComparison.Ordinal))
        {
            return _canDownloadInventoryImportTemplate
                ? "Download template import master inventory."
                : "Anda tidak memiliki izin download template import inventory.";
        }

        return actionLabel switch
        {
            _ => "Aksi import master inventory tersedia."
        };
    }

    private string BuildSaveInventoryDraftTooltip(
        string documentLabel,
        long? entityId,
        string? currentStatus,
        bool canCreatePermission,
        bool canUpdatePermission)
    {
        if (IsBusy)
        {
            return "Sedang memproses data. Tunggu hingga selesai.";
        }

        if (!entityId.HasValue)
        {
            return $"Buat {documentLabel} terlebih dahulu.";
        }

        if (entityId.Value > 0 && !canUpdatePermission)
        {
            return $"Anda tidak memiliki izin memperbarui draft {documentLabel}.";
        }

        if (entityId.Value <= 0 && !canCreatePermission)
        {
            return $"Anda tidak memiliki izin membuat draft {documentLabel}.";
        }

        if (!IsCurrentAccountingPeriodOpen)
        {
            return InventoryPeriodActionTooltip;
        }

        if (entityId.Value > 0 && !string.Equals(currentStatus, "DRAFT", StringComparison.OrdinalIgnoreCase))
        {
            return $"Hanya {documentLabel} berstatus DRAFT yang bisa disimpan.";
        }

        return $"Simpan {documentLabel} sebagai draft.";
    }

    private string BuildInventoryWorkflowTooltip(
        string documentLabel,
        string actionLabel,
        long? entityId,
        string? currentStatus,
        string requiredStatus,
        bool hasPermission,
        string readyMessage)
    {
        if (IsBusy)
        {
            return "Sedang memproses data. Tunggu hingga selesai.";
        }

        if (!hasPermission)
        {
            return $"Anda tidak memiliki izin {actionLabel} {documentLabel}.";
        }

        if (!IsCurrentAccountingPeriodOpen)
        {
            return InventoryPeriodActionTooltip;
        }

        if (!entityId.HasValue || entityId.Value <= 0)
        {
            return $"Simpan {documentLabel} terlebih dahulu.";
        }

        if (!string.Equals(currentStatus, requiredStatus, StringComparison.OrdinalIgnoreCase))
        {
            return $"Hanya {documentLabel} berstatus {requiredStatus} yang dapat diproses.";
        }

        return readyMessage;
    }

    private string BuildSyncActionTooltip()
    {
        if (IsBusy)
        {
            return "Sedang memproses data. Tunggu hingga selesai.";
        }

        if (!MasterCompanyId.HasValue || MasterCompanyId.Value <= 0)
        {
            return "Sinkronisasi pusat belum tersedia karena master company inventory belum dikonfigurasi.";
        }

        if (CanMaintainMasterInventoryData)
        {
            return _canSyncUploadInventoryMaster
                ? "Upload perubahan master inventory ke server pusat."
                : "Anda tidak memiliki izin upload inventory ke server pusat.";
        }

        return _canSyncDownloadInventoryMaster
            ? $"Download master inventory terbaru dari pusat ({MasterCompanyLabel})."
            : "Anda tidak memiliki izin download inventory dari server pusat.";
    }

    private void ApplyInventoryTabPreset(string tabCode)
    {
        var reportPreset = tabCode switch
        {
            "inquiry_mutation" => "movement",
            "lk_outbound_compare" => "lk_outbound_compare",
            "report_stock" => "valuation",
            "report_mutation" => "movement",
            "report_valuation" => "valuation",
            "low_stock" => "low_stock",
            "sync_runs" => "sync_runs",
            "sync_items" => "sync_items",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(reportPreset))
        {
            return;
        }

        if (string.Equals(SelectedReportType, reportPreset, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedReportType = reportPreset;
    }
}
