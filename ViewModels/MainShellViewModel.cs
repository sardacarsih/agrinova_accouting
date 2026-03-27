using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class MainShellNavigationItem : ViewModelBase
{
    private int _depth;
    private bool _isExpanded;
    private bool _isSelected;

    public MainShellNavigationItem(string scopeCode, string label, string glyph, string? subCode = null)
    {
        ScopeCode = scopeCode;
        Label = label;
        Glyph = glyph;
        SubCode = subCode;
    }

    public string ScopeCode { get; }

    public string? SubCode { get; }

    public string Label { get; }

    public string Glyph { get; }

    public ObservableCollection<MainShellNavigationItem>? Children { get; init; }

    public bool HasChildren => Children is { Count: > 0 };

    public MainShellNavigationItem? Parent { get; private set; }

    public int Depth
    {
        get => _depth;
        private set
        {
            if (!SetProperty(ref _depth, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ItemMargin));
        }
    }

    public Thickness ItemMargin => new(Math.Max(0, Depth * 14), 1, 0, 1);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void ConfigureHierarchy(MainShellNavigationItem? parent, int depth)
    {
        Parent = parent;
        Depth = depth;
    }
}

public sealed class MainShellViewModel : ViewModelBase, IDisposable
{
    private const bool EnableAccountingBudgetMenu = false;

    private readonly Action _signOutRequested;
    private readonly Action _openWorkContextSelectorRequested;
    private readonly DispatcherTimer _clockTimer;
    private readonly IAccessControlService _accessControlService;
    private readonly long _selectedCompanyId;
    private readonly long _selectedLocationId;

    private MainShellNavigationItem? _selectedNavigationItem;
    private string _currentScopeTitle = "Dashboard";
    private string _selectedSettingsTab = "companies";
    private bool _isSidebarCollapsed;
    private bool _manualSidebarCollapsed;
    private bool _hasManualSidebarPreference;
    private bool _isAutoCollapsed;
    private GridLength _sidebarColumnWidth = new(188);
    private Thickness _contentPadding = new(8);
    private Thickness _shellOuterMargin = new(8);
    private GridLength _shellColumnGap = new(6);
    private GridLength _shellRowGap = new(8);
    private double _contentMaxWidth = 1500;
    private string _currentTimeText = string.Empty;
    private string _currentScopePlaceholderTitle = "Ruang Kerja";
    private string _currentScopePlaceholderDescription = "Ruang kerja siap digunakan.";
    private string _currentPeriodStatusText = "OPEN";
    private string _currentPeriodMonthText = DateTime.Today.ToString("MM/yyyy");
    private bool _isCurrentPeriodOpen = true;
    private DateTime _lastPeriodRefreshAt = DateTime.MinValue;
    private bool _isRefreshingPeriodStatus;

    public MainShellViewModel(
        UserAccessContext accessContext,
        string environmentName,
        IAccessControlService accessControlService,
        Action signOutRequested,
        Action openWorkContextSelectorRequested)
    {
        _signOutRequested = signOutRequested;
        _openWorkContextSelectorRequested = openWorkContextSelectorRequested;
        _accessControlService = accessControlService;
        _selectedCompanyId = accessContext.SelectedCompanyId;
        _selectedLocationId = accessContext.SelectedLocationId;

        AppDisplayName = "AgrInova Suite";
        EnvironmentName = environmentName;
        CurrentUserDisplayName = string.IsNullOrWhiteSpace(accessContext.Username) ? "User" : accessContext.Username.Trim();
        CurrentRoleDisplayName = string.IsNullOrWhiteSpace(accessContext.SelectedRoleCode)
            ? "-"
            : $"{accessContext.SelectedRoleCode} - {accessContext.SelectedRoleName}";
        CurrentCompanyDisplayName = string.IsNullOrWhiteSpace(accessContext.SelectedCompanyCode)
            ? "-"
            : $"{accessContext.SelectedCompanyCode} - {accessContext.SelectedCompanyName}";
        CurrentLocationDisplayName = string.IsNullOrWhiteSpace(accessContext.SelectedLocationCode)
            ? "-"
            : $"{accessContext.SelectedLocationCode} - {accessContext.SelectedLocationName}";

        ToggleSidebarCommand = new RelayCommand(ToggleSidebar);
        NavigateCommand = new RelayCommand(OnNavigateItem);
        OpenWorkContextSelectorCommand = new RelayCommand(() => _openWorkContextSelectorRequested());
        SignOutCommand = new RelayCommand(() => _signOutRequested());
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        var canManageAccountingPeriod = accessContext.IsSuperRole ||
                                        accessContext.RoleCodes.Contains("SUPER_ADMIN") ||
                                        accessContext.RoleCodes.Contains("FINANCE_ADMIN");
        var canOperateInventoryOpeningBalance = accessContext.IsSuperRole ||
                                                accessContext.RoleCodes.Contains("SUPER_ADMIN");
        UserManagement = new UserManagementViewModel(
            accessControlService,
            CurrentUserDisplayName,
            accessContext.HasAction("inventory", "api_inv", "manage_master_company"));
        MasterData = new MasterDataViewModel(
            accessControlService,
            CurrentUserDisplayName,
            accessContext.SelectedCompanyId,
            accessContext.SelectedLocationId,
            canManageAccountingPeriod,
            ConfirmPeriodClose);
        MasterData.AccountingPeriodStateChanged += OnAccountingPeriodStateChanged;
        Inventory = new InventoryViewModel(
            accessControlService,
            accessContext,
            canOperateInventoryOpeningBalance);
        JournalManagement = new JournalManagementViewModel(
            accessControlService,
            accessContext,
            CurrentLocationDisplayName);
        Reports = new ReportsViewModel(
            accessControlService,
            accessContext,
            accessContext.SelectedCompanyId,
            accessContext.SelectedLocationId);
        AccountingDashboard = new AccountingDashboardViewModel(accessControlService, accessContext);
        AccountingDashboard.DrillDownRequested += OnDashboardDrillDownRequested;

        NavigationItems = BuildNavigation(accessContext);
        var firstLeaf = FindFirstLeaf(NavigationItems) ?? NavigationItems.FirstOrDefault(x => !x.HasChildren);
        SelectedNavigationItem = firstLeaf;

        ConnectionStatusText = "Online";
        ServerName = "agrinova_accounting";
        NotificationCount = 3;

        UpdateClockText();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClockText();
            _ = RefreshStatusBarAccountingPeriodAsync();
        };
        _clockTimer.Start();

        _ = RefreshStatusBarAccountingPeriodAsync(force: true);
    }

    public string AppDisplayName { get; }

    public string EnvironmentName { get; }

    public int NotificationCount { get; }

    public string CurrentUserDisplayName { get; }

    public string CurrentRoleDisplayName { get; }

    public string CurrentCompanyDisplayName { get; }

    public string CurrentLocationDisplayName { get; }

    public string ActiveAccessSummary => $"Pengguna: {CurrentUserDisplayName}  |  Role: {CurrentRoleDisplayName}  |  Company: {CurrentCompanyDisplayName}  |  Lokasi: {CurrentLocationDisplayName}";

    public string WorkContextButtonLabel => "Konteks Kerja";

    public string WorkContextButtonTooltip => $"Lingkungan: {EnvironmentName}{Environment.NewLine}Akses aktif: {ActiveAccessSummary}";

    public string ConnectionStatusText { get; }

    public string ServerName { get; }

    public ObservableCollection<MainShellNavigationItem> NavigationItems { get; }

    public ICommand ToggleSidebarCommand { get; }

    public ICommand NavigateCommand { get; }

    public ICommand OpenWorkContextSelectorCommand { get; }

    public ICommand SignOutCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public UserManagementViewModel UserManagement { get; }

    public MasterDataViewModel MasterData { get; }

    public AccountingDashboardViewModel AccountingDashboard { get; }

    public InventoryViewModel Inventory { get; }

    public JournalManagementViewModel JournalManagement { get; }

    public ReportsViewModel Reports { get; }

    public MainShellNavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (value is { HasChildren: true })
            {
                return;
            }

            var prev = _selectedNavigationItem;
            if (!SetProperty(ref _selectedNavigationItem, value))
            {
                return;
            }

            if (prev is not null)
            {
                prev.IsSelected = false;
            }

            if (value is not null)
            {
                value.IsSelected = true;
                ExpandAncestors(value);
            }

            var settingsSubCode = string.Equals(value?.ScopeCode, "settings", StringComparison.OrdinalIgnoreCase)
                ? value?.SubCode
                : null;
            if (!string.IsNullOrWhiteSpace(settingsSubCode))
            {
                SelectedSettingsTab = settingsSubCode;
                UserManagement.SelectedSettingsSubTab = settingsSubCode;
            }

            if (string.Equals(value?.ScopeCode, "inventory", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value?.SubCode))
            {
                Inventory.SelectedInventoryTab = value.SubCode;
            }

            if (string.Equals(value?.ScopeCode, "transactions", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value?.SubCode))
            {
                JournalManagement.NavigateToJournalScenario(value.SubCode);
            }

            if (string.Equals(value?.ScopeCode, "master_data", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value?.SubCode))
            {
                MasterData.NavigateToMasterDataSubmenu(value.SubCode);
            }

            if (string.Equals(value?.ScopeCode, "reports", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value?.SubCode))
            {
                Reports.NavigateToReportSubmenu(value.SubCode);
            }

            CurrentScopeTitle = value?.Label ?? "Dashboard";
            UpdateScopePlaceholder();
            OnPropertyChanged(nameof(IsDashboardSelected));
            OnPropertyChanged(nameof(IsScopePlaceholderSelected));
            OnPropertyChanged(nameof(IsUserManagementSelected));
            OnPropertyChanged(nameof(IsMasterDataSelected));
            OnPropertyChanged(nameof(IsInventorySelected));
            OnPropertyChanged(nameof(IsJournalSelected));
            OnPropertyChanged(nameof(IsReportsSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
            OnPropertyChanged(nameof(IsStandardWorkspaceSelected));

            if (IsUserManagementSelected)
            {
                _ = UserManagement.EnsureLoadedAsync();
            }

            if (IsDashboardSelected)
            {
                _ = AccountingDashboard.EnsureLoadedAsync();
            }

            if (IsJournalSelected)
            {
                _ = JournalManagement.EnsureLoadedAsync();
            }

            if (IsMasterDataSelected)
            {
                _ = MasterData.EnsureLoadedAsync();
            }

            if (IsInventorySelected)
            {
                _ = Inventory.EnsureLoadedAsync();
            }

            if (IsReportsSelected)
            {
                _ = Reports.EnsureLoadedAsync();
            }

            if (IsSettingsSelected)
            {
                _ = UserManagement.EnsureLoadedAsync();

                if (IsSettingsPeriodSubTab(value?.SubCode))
                {
                    _ = MasterData.EnsureLoadedAsync();
                }
            }

            if (IsMasterDataSelected ||
                IsJournalSelected ||
                (IsSettingsSelected && IsSettingsPeriodSubTab(value?.SubCode)))
            {
                _ = RefreshStatusBarAccountingPeriodAsync(force: true);
            }

            AccountingDashboard.SetIsActive(IsDashboardSelected);
        }
    }

    public bool IsUserManagementSelected =>
        string.Equals(SelectedNavigationItem?.ScopeCode, "user_management", StringComparison.OrdinalIgnoreCase);

    public bool IsDashboardSelected =>
        string.Equals(SelectedNavigationItem?.ScopeCode, "dashboard", StringComparison.OrdinalIgnoreCase);

    public bool IsMasterDataSelected =>
        string.Equals(SelectedNavigationItem?.ScopeCode, "master_data", StringComparison.OrdinalIgnoreCase);

    public bool IsJournalSelected =>
        string.Equals(SelectedNavigationItem?.ScopeCode, "transactions", StringComparison.OrdinalIgnoreCase);

    public bool IsInventorySelected =>
        string.Equals(SelectedNavigationItem?.ScopeCode, "inventory", StringComparison.OrdinalIgnoreCase);

    public bool IsReportsSelected =>
        string.Equals(SelectedNavigationItem?.ScopeCode, "reports", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsSelected =>
        string.Equals(SelectedNavigationItem?.ScopeCode, "settings", StringComparison.OrdinalIgnoreCase);

    public string SelectedSettingsTab
    {
        get => _selectedSettingsTab;
        private set => SetProperty(ref _selectedSettingsTab, value);
    }

    public bool IsStandardWorkspaceSelected => !IsUserManagementSelected && !IsJournalSelected && !IsMasterDataSelected && !IsInventorySelected && !IsReportsSelected && !IsSettingsSelected;

    public bool IsScopePlaceholderSelected => !IsUserManagementSelected && !IsDashboardSelected && !IsJournalSelected && !IsMasterDataSelected && !IsInventorySelected && !IsReportsSelected && !IsSettingsSelected;

    public string CurrentScopeTitle
    {
        get => _currentScopeTitle;
        private set => SetProperty(ref _currentScopeTitle, value);
    }

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        private set
        {
            if (!SetProperty(ref _isSidebarCollapsed, value))
            {
                return;
            }

            SidebarColumnWidth = value ? new GridLength(52) : new GridLength(188);
            OnPropertyChanged(nameof(SidebarToggleGlyph));
            OnPropertyChanged(nameof(SidebarToggleTooltip));
        }
    }

    public string SidebarToggleGlyph => IsSidebarCollapsed ? "\uE76C" : "\uE76B";

    public string SidebarToggleTooltip => IsSidebarCollapsed ? "Buka sidebar" : "Tutup sidebar";

    public GridLength SidebarColumnWidth
    {
        get => _sidebarColumnWidth;
        private set => SetProperty(ref _sidebarColumnWidth, value);
    }

    public Thickness ContentPadding
    {
        get => _contentPadding;
        private set => SetProperty(ref _contentPadding, value);
    }

    public Thickness ShellOuterMargin
    {
        get => _shellOuterMargin;
        private set => SetProperty(ref _shellOuterMargin, value);
    }

    public GridLength ShellColumnGap
    {
        get => _shellColumnGap;
        private set => SetProperty(ref _shellColumnGap, value);
    }

    public GridLength ShellRowGap
    {
        get => _shellRowGap;
        private set => SetProperty(ref _shellRowGap, value);
    }

    public double ContentMaxWidth
    {
        get => _contentMaxWidth;
        private set => SetProperty(ref _contentMaxWidth, value);
    }

    public string CurrentTimeText
    {
        get => _currentTimeText;
        private set => SetProperty(ref _currentTimeText, value);
    }

    public string CurrentScopePlaceholderTitle
    {
        get => _currentScopePlaceholderTitle;
        private set => SetProperty(ref _currentScopePlaceholderTitle, value);
    }

    public string CurrentScopePlaceholderDescription
    {
        get => _currentScopePlaceholderDescription;
        private set => SetProperty(ref _currentScopePlaceholderDescription, value);
    }

    public string CurrentPeriodStatusText
    {
        get => _currentPeriodStatusText;
        private set => SetProperty(ref _currentPeriodStatusText, value);
    }

    public string CurrentPeriodMonthText
    {
        get => _currentPeriodMonthText;
        private set => SetProperty(ref _currentPeriodMonthText, value);
    }

    public bool IsCurrentPeriodOpen
    {
        get => _isCurrentPeriodOpen;
        private set => SetProperty(ref _isCurrentPeriodOpen, value);
    }

    public void UpdateLayout(double windowWidth, double dpiScale)
    {
        var width = Math.Max(windowWidth, 1100);
        var dpi = Math.Clamp(dpiScale, 1.0, 2.0);

        _isAutoCollapsed = width < 1920;
        IsSidebarCollapsed = _hasManualSidebarPreference
            ? _manualSidebarCollapsed
            : _isAutoCollapsed;

        ContentMaxWidth = 10000;

        var basePadding = width < 1400 ? 4.0 : width < 1900 ? 6.0 : 8.0;
        var dpiAdjustment = (dpi - 1.0) * 3.0;
        var effectivePadding = Math.Clamp(basePadding - dpiAdjustment, 3, 10);
        ContentPadding = new Thickness(effectivePadding);

        var baseOuterMargin = width < 1400 ? 4.0 : width < 1900 ? 6.0 : 8.0;
        var effectiveOuterMargin = Math.Clamp(baseOuterMargin - ((dpi - 1.0) * 1.3), 2, 8);
        ShellOuterMargin = new Thickness(effectiveOuterMargin);

        ShellColumnGap = new GridLength(width < 1400 ? 2 : 4);
        ShellRowGap = new GridLength(width < 1400 ? 4 : 6);
    }

    public void Dispose()
    {
        MasterData.AccountingPeriodStateChanged -= OnAccountingPeriodStateChanged;
        AccountingDashboard.DrillDownRequested -= OnDashboardDrillDownRequested;
        AccountingDashboard.Dispose();
        _clockTimer.Stop();
    }

    private async Task RefreshStatusBarAccountingPeriodAsync(bool force = false)
    {
        if (_selectedCompanyId <= 0 || _selectedLocationId <= 0)
        {
            return;
        }

        if (_isRefreshingPeriodStatus)
        {
            return;
        }

        var now = DateTime.Now;
        if (!force && _lastPeriodRefreshAt != DateTime.MinValue && (now - _lastPeriodRefreshAt).TotalSeconds < 30)
        {
            return;
        }

        _isRefreshingPeriodStatus = true;
        try
        {
            _lastPeriodRefreshAt = now;
            var periods = await _accessControlService.GetAccountingPeriodsAsync(_selectedCompanyId, _selectedLocationId);
            var currentMonth = new DateTime(now.Year, now.Month, 1);
            var current = periods.FirstOrDefault(x => x.PeriodMonth.Date == currentMonth.Date);

            var isOpen = current?.IsOpen ?? true;
            IsCurrentPeriodOpen = isOpen;
            CurrentPeriodMonthText = currentMonth.ToString("MM/yyyy");
            CurrentPeriodStatusText = isOpen ? "OPEN" : "CLOSED";
            Inventory.ApplyCurrentAccountingPeriodState(currentMonth, isOpen);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(
                nameof(MainShellViewModel),
                "RefreshStatusBarAccountingPeriodFailed",
                $"action=refresh_period_status company_id={_selectedCompanyId} location_id={_selectedLocationId}",
                ex);
            // Keep last known status when refresh fails.
        }
        finally
        {
            _isRefreshingPeriodStatus = false;
        }
    }

    private static ObservableCollection<MainShellNavigationItem> BuildNavigation(UserAccessContext accessContext)
    {
        var items = new List<MainShellNavigationItem>();

        var canAccountingDashboard = accessContext.HasSubmodule("accounting", "dashboard");
        var canAccountingMasterData = accessContext.HasAction("accounting", "master_data", "view");
        var canAccountingTransactions = accessContext.HasAction("accounting", "transactions", "view");
        var canAccountingReports = accessContext.HasAction("accounting", "reports", "view");
        var canAccountingSettings = accessContext.HasAction("accounting", "settings", "view");
        var canShowGeneralLedger = canAccountingDashboard || canAccountingMasterData || canAccountingTransactions || canAccountingReports || canAccountingSettings;

        if (canShowGeneralLedger)
        {
            var generalLedgerChildren = new List<MainShellNavigationItem>();

            if (canAccountingDashboard)
            {
                generalLedgerChildren.Add(new MainShellNavigationItem("dashboard", "Dashboard", "\uE80F"));
            }

            if (canAccountingTransactions)
            {
                generalLedgerChildren.Add(new MainShellNavigationItem("general_ledger", "Jurnal", "\uE7C3")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>
                    {
                        new("transactions", "Jurnal", "\uE7C3", subCode: "jurnal_umum")
                    }
                });
            }

            if (canAccountingMasterData)
            {
                generalLedgerChildren.Add(new MainShellNavigationItem("general_ledger", "Master Akun", "\uE8D2")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>
                    {
                        new("master_data", "Daftar Akun (Chart of Accounts)", "\uE8D2", subCode: "coa"),
                        new("master_data", "Kategori Akun", "\uE8EC", subCode: "kategori_akun"),
                        new("master_data", "Mapping Akun", "\uE713", subCode: "mapping_akun")
                    }
                });
            }

            if (canAccountingMasterData || canAccountingTransactions)
            {
                var periodPostingChildren = new List<MainShellNavigationItem>();
                if (canAccountingTransactions)
                {
                    periodPostingChildren.Add(new MainShellNavigationItem("transactions", "Posting Jurnal", "\uE73E", subCode: "posting_jurnal"));
                    periodPostingChildren.Add(new MainShellNavigationItem("transactions", "Jurnal Belum Diposting", "\uE9D2", subCode: "jurnal_belum_posting"));
                }

                if (canAccountingMasterData)
                {
                    periodPostingChildren.Add(new MainShellNavigationItem("master_data", "Buka / Tutup Periode", "\uE8C7", subCode: "buka_tutup_periode"));
                    periodPostingChildren.Add(new MainShellNavigationItem("master_data", "Kunci Periode", "\uE72E", subCode: "kunci_periode"));
                }

                if (periodPostingChildren.Count > 0)
                {
                    generalLedgerChildren.Add(new MainShellNavigationItem("general_ledger", "Periode & Posting", "\uE916")
                    {
                        Children = new ObservableCollection<MainShellNavigationItem>(periodPostingChildren)
                    });
                }
            }

            if (canAccountingReports)
            {
                generalLedgerChildren.Add(new MainShellNavigationItem("general_ledger", "Inquiry", "\uE8F1")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>
                    {
                        new("reports", "Buku Besar", "\uE9D2", subCode: "buku_besar"),
                        new("reports", "Neraca Saldo (Trial Balance)", "\uE9D2", subCode: "trial_balance"),
                        new("reports", "Sub Ledger", "\uE9D2", subCode: "sub_ledger"),
                        new("reports", "Mutasi Akun", "\uE9D2", subCode: "mutasi_akun")
                    }
                });

                generalLedgerChildren.Add(new MainShellNavigationItem("general_ledger", "Laporan", "\uE9D2")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>
                    {
                        new("reports", "Laporan Laba Rugi", "\uE9D2", subCode: "laporan_laba_rugi"),
                        new("reports", "Laporan Neraca", "\uE9D2", subCode: "laporan_neraca"),
                        new("reports", "Laporan Arus Kas", "\uE9D2", subCode: "laporan_arus_kas"),
                        new("reports", "Laporan Kustom", "\uE9D2", subCode: "laporan_kustom")
                    }
                });
            }

            if (EnableAccountingBudgetMenu && (canAccountingReports || canAccountingSettings))
            {
                generalLedgerChildren.Add(new MainShellNavigationItem("general_ledger", "Anggaran", "\uE8C7")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>
                    {
                        new("reports", "Setup Anggaran", "\uE713", subCode: "setup_anggaran"),
                        new("reports", "Realisasi vs Anggaran", "\uE9D2", subCode: "realisasi_vs_anggaran"),
                        new("reports", "Kontrol Anggaran", "\uE8C7", subCode: "kontrol_anggaran")
                    }
                });
            }

            if (canAccountingSettings)
            {
                generalLedgerChildren.Add(new MainShellNavigationItem("general_ledger", "Pengaturan", "\uE713")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>
                    {
                        new("settings", "Default Akun", "\uE713", subCode: "default_akun"),
                        new("settings", "Penomoran Jurnal", "\uE713", subCode: "penomoran_jurnal"),
                        new("settings", "Mata Uang & Kurs", "\uE713", subCode: "mata_uang_kurs"),
                        new("settings", "Tahun Fiskal", "\uE713", subCode: "tahun_fiskal")
                    }
                });
            }

            if (generalLedgerChildren.Count > 0)
            {
                items.Add(new MainShellNavigationItem("general_ledger", "General Ledger", "\uE8E5")
                {
                    IsExpanded = true,
                    Children = new ObservableCollection<MainShellNavigationItem>(generalLedgerChildren)
                });
            }
        }

        if (accessContext.HasModule("inventory"))
        {
            var canViewItem = accessContext.HasSubmodule("inventory", "item");
            var canViewCategory = accessContext.HasSubmodule("inventory", "kategori");
            var canViewUnit = accessContext.HasSubmodule("inventory", "satuan");
            var canViewWarehouse = accessContext.HasSubmodule("inventory", "gudang");
            var canViewStockIn = accessContext.HasSubmodule("inventory", "stock_in");
            var canViewStockOut = accessContext.HasSubmodule("inventory", "stock_out");
            var canViewTransfer = accessContext.HasSubmodule("inventory", "transfer");
            var canViewStockOpname = accessContext.HasSubmodule("inventory", "stock_opname");
            var canViewReports = accessContext.HasSubmodule("inventory", "reports");
            var canViewApiInv = accessContext.HasSubmodule("inventory", "api_inv");
            var canViewInventoryDashboard = accessContext.HasSubmodule("inventory", "dashboard");

            var inventoryChildren = new List<MainShellNavigationItem>();
            if (canViewInventoryDashboard)
            {
                inventoryChildren.Add(new MainShellNavigationItem("inventory", "Dashboard", "\uE80F", subCode: "dashboard"));
            }

            var masterDataChildren = new List<MainShellNavigationItem>();
            if (canViewItem)
            {
                masterDataChildren.Add(new MainShellNavigationItem("inventory", "Data Barang", "\uE8D9", subCode: "item"));
            }

            if (canViewCategory)
            {
                masterDataChildren.Add(new MainShellNavigationItem("inventory", "Kategori Barang", "\uE8EC", subCode: "kategori"));
            }

            if (canViewUnit)
            {
                masterDataChildren.Add(new MainShellNavigationItem("inventory", "Satuan", "\uE8C8", subCode: "satuan"));
            }

            if (canViewWarehouse)
            {
                masterDataChildren.Add(new MainShellNavigationItem("inventory", "Gudang", "\uE7EE", subCode: "gudang"));
                masterDataChildren.Add(new MainShellNavigationItem("inventory", "Lokasi Penyimpanan", "\uE707", subCode: "lokasi_penyimpanan"));
            }

            if (masterDataChildren.Count > 0)
            {
                inventoryChildren.Add(new MainShellNavigationItem("inventory", "Master Data", "\uE8D2")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>(masterDataChildren)
                });
            }

            var transaksiChildren = new List<MainShellNavigationItem>();
            if (canViewStockIn)
            {
                transaksiChildren.Add(new MainShellNavigationItem("inventory", "Barang Masuk", "\uE8D8", subCode: "stock_in"));
            }

            if (canViewStockOut)
            {
                transaksiChildren.Add(new MainShellNavigationItem("inventory", "Barang Keluar", "\uE8D8", subCode: "stock_out"));
            }

            if (canViewTransfer)
            {
                transaksiChildren.Add(new MainShellNavigationItem("inventory", "Transfer Antar Gudang", "\uE8AB", subCode: "transfer"));
            }

            if (canViewStockOpname)
            {
                transaksiChildren.Add(new MainShellNavigationItem("inventory", "Stok Opname", "\uE9D5", subCode: "stock_opname"));
            }

            if (transaksiChildren.Count > 0)
            {
                inventoryChildren.Add(new MainShellNavigationItem("inventory", "Transaksi", "\uE7C3")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>(transaksiChildren)
                });
            }

            var inquiryChildren = new List<MainShellNavigationItem>();
            if (canViewReports)
            {
                inquiryChildren.Add(new MainShellNavigationItem("inventory", "Posisi Stok", "\uE8B8", subCode: "stock_position"));
                inquiryChildren.Add(new MainShellNavigationItem("inventory", "Mutasi Barang", "\uE9D2", subCode: "inquiry_mutation"));
                inquiryChildren.Add(new MainShellNavigationItem("inventory", "Histori Transaksi", "\uE81C", subCode: "transaction_history"));
                inquiryChildren.Add(new MainShellNavigationItem("inventory", "Kartu Stok", "\uE9D2", subCode: "stock_card"));
                inquiryChildren.Add(new MainShellNavigationItem("inventory", "Komparasi Outbound", "\uE9D2", subCode: "lk_outbound_compare"));
            }

            if (inquiryChildren.Count > 0)
            {
                inventoryChildren.Add(new MainShellNavigationItem("inventory", "Inquiry", "\uE8F1")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>(inquiryChildren)
                });
            }

            var laporanChildren = new List<MainShellNavigationItem>();
            if (canViewReports)
            {
                laporanChildren.Add(new MainShellNavigationItem("inventory", "Laporan Stok", "\uE9D2", subCode: "report_stock"));
                laporanChildren.Add(new MainShellNavigationItem("inventory", "Laporan Mutasi", "\uE9D2", subCode: "report_mutation"));
                laporanChildren.Add(new MainShellNavigationItem("inventory", "Laporan Nilai Persediaan", "\uE9D2", subCode: "report_valuation"));
                laporanChildren.Add(new MainShellNavigationItem("inventory", "Laporan Stok Opname", "\uE9D2", subCode: "report_stock_opname"));
                laporanChildren.Add(new MainShellNavigationItem("inventory", "Alert Stok Minimum", "\uE9D2", subCode: "low_stock"));
            }

            if (laporanChildren.Count > 0)
            {
                inventoryChildren.Add(new MainShellNavigationItem("inventory", "Laporan", "\uE9D2")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>(laporanChildren)
                });
            }

            var integrasiChildren = new List<MainShellNavigationItem>();
            if (canViewApiInv)
            {
                integrasiChildren.Add(new MainShellNavigationItem("settings", "Mapping Akun Persediaan", "\uE713", subCode: "api_inv"));
            }

            if (canViewReports || canViewApiInv)
            {
                integrasiChildren.Add(new MainShellNavigationItem("inventory", "Histori Sinkronisasi (Run)", "\uE9D2", subCode: "sync_runs"));
                integrasiChildren.Add(new MainShellNavigationItem("inventory", "Histori Sinkronisasi (Item)", "\uE9D2", subCode: "sync_items"));
            }

            if (integrasiChildren.Count > 0)
            {
                inventoryChildren.Add(new MainShellNavigationItem("inventory", "Integrasi", "\uE774")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>(integrasiChildren)
                });
            }

            if (canViewApiInv)
            {
                inventoryChildren.Add(new MainShellNavigationItem("settings", "Pengaturan", "\uE713", subCode: "api_inv"));
            }

            if (inventoryChildren.Count > 0)
            {
                items.Add(new MainShellNavigationItem("inventory", "Inventori", "\uE8D9")
                {
                    IsExpanded = true,
                    Children = new ObservableCollection<MainShellNavigationItem>(inventoryChildren)
                });
            }
        }

        if (accessContext.HasModule("fixed_asset"))
        {
            var canViewDashboard = accessContext.HasSubmodule("fixed_asset", "dashboard");
            var canViewAssetRegister = accessContext.HasSubmodule("fixed_asset", "asset_register");
            var canViewDepreciation = accessContext.HasSubmodule("fixed_asset", "depreciation");
            var canViewDisposal = accessContext.HasSubmodule("fixed_asset", "disposal");
            var canViewReports = accessContext.HasSubmodule("fixed_asset", "reports");
            var canViewSettings = accessContext.HasSubmodule("fixed_asset", "settings");

            var fixedAssetChildren = new List<MainShellNavigationItem>();
            if (canViewDashboard)
            {
                fixedAssetChildren.Add(new MainShellNavigationItem("fixed_asset", "Dashboard", "\uE80F", subCode: "dashboard"));
            }

            if (canViewAssetRegister)
            {
                fixedAssetChildren.Add(new MainShellNavigationItem("fixed_asset", "Register Aset", "\uE8D9", subCode: "asset_register"));
            }

            var fixedAssetTransactionChildren = new List<MainShellNavigationItem>();
            if (canViewDepreciation)
            {
                fixedAssetTransactionChildren.Add(new MainShellNavigationItem("fixed_asset", "Depresiasi", "\uE8B8", subCode: "depreciation"));
            }

            if (canViewDisposal)
            {
                fixedAssetTransactionChildren.Add(new MainShellNavigationItem("fixed_asset", "Disposal", "\uE74D", subCode: "disposal"));
            }

            if (fixedAssetTransactionChildren.Count > 0)
            {
                fixedAssetChildren.Add(new MainShellNavigationItem("fixed_asset", "Transaksi", "\uE7C3")
                {
                    Children = new ObservableCollection<MainShellNavigationItem>(fixedAssetTransactionChildren)
                });
            }

            if (canViewReports)
            {
                fixedAssetChildren.Add(new MainShellNavigationItem("fixed_asset", "Laporan", "\uE9D2", subCode: "reports"));
            }

            if (canViewSettings)
            {
                fixedAssetChildren.Add(new MainShellNavigationItem("fixed_asset", "Pengaturan", "\uE713", subCode: "settings"));
            }

            if (fixedAssetChildren.Count > 0)
            {
                items.Add(new MainShellNavigationItem("fixed_asset", "Aset Tetap", "\uED43")
                {
                    IsExpanded = true,
                    Children = new ObservableCollection<MainShellNavigationItem>(fixedAssetChildren)
                });
            }
        }

        if (accessContext.HasSubmodule("accounting", "user_management"))
        {
            items.Add(new MainShellNavigationItem("user_management", "Manajemen Pengguna", "\uE77B"));
        }

        if (items.Count == 0)
        {
            items.Add(new MainShellNavigationItem("dashboard", "Dashboard", "\uE80F"));
        }

        InitializeNavigationTree(items);
        return new ObservableCollection<MainShellNavigationItem>(items);
    }

    private static void InitializeNavigationTree(IEnumerable<MainShellNavigationItem> items, MainShellNavigationItem? parent = null, int depth = 0)
    {
        foreach (var item in items)
        {
            item.ConfigureHierarchy(parent, depth);
            if (!item.HasChildren || item.Children is null)
            {
                continue;
            }

            InitializeNavigationTree(item.Children, item, depth + 1);
        }
    }

    private static MainShellNavigationItem? FindFirstLeaf(IEnumerable<MainShellNavigationItem> items)
    {
        foreach (var item in items)
        {
            if (!item.HasChildren)
            {
                return item;
            }

            if (item.Children is null)
            {
                continue;
            }

            var leaf = FindFirstLeaf(item.Children);
            if (leaf is not null)
            {
                return leaf;
            }
        }

        return null;
    }

    private static void ExpandAncestors(MainShellNavigationItem item)
    {
        var current = item.Parent;
        while (current is not null)
        {
            current.IsExpanded = true;
            current = current.Parent;
        }
    }

    private void OnNavigateItem(object? parameter)
    {
        if (parameter is not MainShellNavigationItem item)
        {
            return;
        }

        if (item.HasChildren)
        {
            item.IsExpanded = !item.IsExpanded;
            return;
        }

        if (IsUserManagementSelected &&
            !string.Equals(item.ScopeCode, "user_management", StringComparison.OrdinalIgnoreCase) &&
            !UserManagement.TryLeaveRoleEditor())
        {
            return;
        }

        SelectedNavigationItem = item;
    }

    private void OnDashboardDrillDownRequested(DashboardDrillRequest request)
    {
        if (request is null)
        {
            return;
        }

        var targetItem = request.TargetModule switch
        {
            "transactions" => FindNavigationItem("transactions", request.TargetSubCode) ?? FindNavigationItem("transactions", "jurnal_umum"),
            "reports" => FindNavigationItem("reports", request.TargetSubCode) ?? FindNavigationItem("reports", "trial_balance"),
            "inventory" => FindNavigationItem("inventory", request.TargetSubCode) ?? FindNavigationItem("inventory", "dashboard"),
            _ => null
        };

        if (targetItem is not null)
        {
            SelectedNavigationItem = targetItem;
        }

        if (string.Equals(request.TargetModule, "transactions", StringComparison.OrdinalIgnoreCase))
        {
            JournalManagement.ApplyDashboardDrillDown(request);
        }
        else if (string.Equals(request.TargetModule, "reports", StringComparison.OrdinalIgnoreCase))
        {
            Reports.ApplyDashboardDrillDown(request);
        }
        else if (string.Equals(request.TargetModule, "inventory", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(request.TargetSubCode))
        {
            Inventory.SelectedInventoryTab = request.TargetSubCode;
        }
    }

    private MainShellNavigationItem? FindNavigationItem(string scopeCode, string? subCode)
    {
        return FindNavigationItemRecursive(NavigationItems, scopeCode, subCode);
    }

    private static MainShellNavigationItem? FindNavigationItemRecursive(
        IEnumerable<MainShellNavigationItem> items,
        string scopeCode,
        string? subCode)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.ScopeCode, scopeCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.SubCode ?? string.Empty, subCode ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            if (item.Children is null)
            {
                continue;
            }

            var nested = FindNavigationItemRecursive(item.Children, scopeCode, subCode);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool IsSettingsPeriodSubTab(string? subCode)
    {
        return string.Equals(subCode, "periods", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(subCode, "period_audit", StringComparison.OrdinalIgnoreCase);
    }

    private void ToggleSidebar()
    {
        _hasManualSidebarPreference = true;
        _manualSidebarCollapsed = !IsSidebarCollapsed;
        IsSidebarCollapsed = _manualSidebarCollapsed;
    }

    private void UpdateClockText()
    {
        CurrentTimeText = DateTime.Now.ToString("ddd, dd MMM yyyy HH:mm:ss");
    }

    private void UpdateScopePlaceholder()
    {
        var code = SelectedNavigationItem?.ScopeCode ?? "dashboard";
        (string Title, string Description) = code switch
        {
            "dashboard" => ("Dashboard", "Ringkasan KPI, notifikasi penting, dan status operasional."),
            "master_data" => ("Master Data", "Kelola data induk seperti akun, periode, dan konfigurasi dasar."),
            "inventory" => ("Inventori", "Kelola master barang, transaksi stok, dan laporan inventori."),
            "fixed_asset" => ("Aset Tetap", "Kelola siklus aset tetap: register, depresiasi, disposal, dan laporan."),
            "transactions" => ("Transaksi", "Kelola jurnal transaksi dari draft hingga proses posting."),
            "reports" => ("Laporan", "Tinjau dan ekspor laporan keuangan serta laporan operasional."),
            "settings" => ("Pengaturan", "Atur parameter aplikasi, preferensi, dan konfigurasi sistem."),
            "user_management" => ("Manajemen Pengguna", "Kelola pengguna, role, permission modul, company, dan akses lokasi."),
            _ => ("Ruang Kerja", "Ruang kerja siap digunakan.")
        };

        CurrentScopePlaceholderTitle = Title;
        CurrentScopePlaceholderDescription = Description;
    }

    private void OnAccountingPeriodStateChanged(DateTime periodMonth, bool isOpen)
    {
        var currentMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (periodMonth.Date != currentMonth.Date)
        {
            return;
        }

        IsCurrentPeriodOpen = isOpen;
        CurrentPeriodMonthText = currentMonth.ToString("MM/yyyy");
        CurrentPeriodStatusText = isOpen ? "OPEN" : "CLOSED";
        Inventory.ApplyCurrentAccountingPeriodState(currentMonth, isOpen);
    }

    private static bool ConfirmPeriodClose(string message)
    {
        var result = MessageBox.Show(
            message,
            "Konfirmasi Tutup Periode",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private static void OpenLogFolder()
    {
        var logDirectory = FileAppLogger.GetDefaultLogDirectory();
        try
        {
            Directory.CreateDirectory(logDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = logDirectory,
                UseShellExecute = true
            });
            AppServices.Logger.LogInfo(
                nameof(MainShellViewModel),
                "OpenLogFolder",
                $"action=open_log_folder path={logDirectory}");
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MainShellViewModel),
                "OpenLogFolderFailed",
                $"action=open_log_folder path={logDirectory}",
                ex);
            MessageBox.Show(
                "Folder log tidak bisa dibuka. Silakan cek izin akses folder aplikasi.",
                "Accounting",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
