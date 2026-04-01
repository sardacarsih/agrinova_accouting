using System.Collections.ObjectModel;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class PeriodCloseChecklistItem
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Severity { get; init; } = "Ready";

    public string Message { get; init; } = string.Empty;
}

public sealed partial class MasterDataViewModel : ViewModelBase
{
    private const int AccountDefaultPageSize = 50;
    private const string AccountStatusActive = "Aktif";
    private const string AccountStatusAll = "Semua";
    private const string AccountStatusInactive = "Nonaktif";

    private readonly IAccessControlService _accessControlService;
    private readonly AccountImportExportXlsxService _accountImportExportXlsxService = new();
    private readonly EstateHierarchyImportExportXlsxService _estateHierarchyImportExportXlsxService = new();
    private readonly string _actorUsername;
    private readonly long _companyId;
    private readonly long _locationId;
    private readonly Func<string, bool> _confirmClosePeriod;
    private readonly RelayCommand _saveAccountCommand;
    private readonly RelayCommand _deactivateAccountCommand;
    private readonly RelayCommand _editAccountCommand;
    private readonly RelayCommand _closeMasterDataModalCommand;
    private readonly RelayCommand _openCostCenterDetailCommand;
    private readonly RelayCommand _nextCloseWizardStepCommand;
    private readonly RelayCommand _previousCloseWizardStepCommand;
    private readonly RelayCommand _validateCloseWizardChecklistCommand;
    private readonly RelayCommand _confirmCloseWizardCommand;
    private readonly RelayCommand _cancelCloseWizardCommand;
    private readonly RelayCommand _previousAccountPageCommand;
    private readonly RelayCommand _nextAccountPageCommand;
    private readonly RelayCommand _exportAccountsCommand;
    private readonly RelayCommand _importAccountsCommand;
    private readonly RelayCommand _newEstateCommand;
    private readonly RelayCommand _newDivisionCommand;
    private readonly RelayCommand _newBlockCommand;
    private readonly RelayCommand _editEstateHierarchyCommand;
    private readonly RelayCommand _deactivateEstateHierarchyCommand;
    private readonly RelayCommand _saveEstateHierarchyCommand;
    private readonly RelayCommand _cancelEstateHierarchyEditCommand;
    private readonly RelayCommand _importEstateHierarchyCommand;
    private readonly RelayCommand _exportEstateHierarchyCommand;

    private ManagedAccount? _selectedAccount;
    private ManagedAccountingPeriod? _selectedAccountingPeriod;
    private ManagedAccount? _accountDraft;
    private ManagedCostCenter? _selectedCostCenter;
    private ManagedCostCenter? _costCenterDetail;
    private object? _selectedEstateHierarchyItem;
    private ManagedEstate? _selectedEstateHierarchyEstate;
    private ManagedDivision? _selectedEstateHierarchyDivision;
    private ManagedBlock? _selectedEstateHierarchyBlock;
    private DateTime _periodMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string _periodNote = string.Empty;
    private string _statusMessage = string.Empty;
    private string _accountSearchText = string.Empty;
    private string _costCenterSearchText = string.Empty;
    private string _estateHierarchySearchText = string.Empty;
    private string _selectedAccountStatusFilter = AccountStatusActive;
    private string _selectedCostCenterStatusFilter = AccountStatusActive;
    private string _selectedEstateHierarchyStatusFilter = AccountStatusActive;
    private int _accountPage = 1;
    private int _accountTotalCount;
    private ManagedAccount? _selectedParentAccountOption;
    private bool _isSyncingParentSelection;
    private bool _isCloseWizardOpen;
    private int _closeWizardStep = 1;
    private string _closeWizardValidationSummary = "Checklist belum dijalankan.";
    private string _closeWizardImpactSummary = string.Empty;
    private string _closeWizardResultMessage = string.Empty;
    private bool _closeWizardHasBlockers;
    private bool _closeWizardHasWarnings;
    private bool _closeWizardActionSucceeded;
    private bool _isBusy;
    private bool _isLoaded;
    private bool _isMasterDataModalOpen;
    private bool _isEstateHierarchyEditorOpen;
    private string _selectedMasterDataSubmenu = "coa";
    private string _activeMasterDataModal = string.Empty;
    private string _estateHierarchyEditorLevel = "ESTATE";
    private long _estateHierarchyEditorEntityId;
    private string _estateHierarchyEditorEstateCode = string.Empty;
    private string _estateHierarchyEditorDivisionCode = string.Empty;
    private string _estateHierarchyEditorCode = string.Empty;
    private string _estateHierarchyEditorName = string.Empty;
    private bool _estateHierarchyEditorIsActive = true;

    public MasterDataViewModel(
        IAccessControlService accessControlService,
        string actorUsername,
        long companyId,
        long locationId,
        bool canManageAccountingPeriod,
        Func<string, bool>? confirmClosePeriod = null)
    {
        _accessControlService = accessControlService;
        _actorUsername = string.IsNullOrWhiteSpace(actorUsername) ? "SYSTEM" : actorUsername.Trim();
        _companyId = companyId;
        _locationId = locationId;
        _confirmClosePeriod = confirmClosePeriod ?? (_ => true);

        CanManageAccountingPeriod = canManageAccountingPeriod;

        Accounts = new ObservableCollection<ManagedAccount>();
        CostCenters = new ObservableCollection<ManagedCostCenter>();
        VisibleCostCenters = new ObservableCollection<ManagedCostCenter>();
        EstateHierarchyEstates = new ObservableCollection<ManagedEstate>();
        VisibleEstateHierarchyEstates = new ObservableCollection<ManagedEstate>();
        ParentAccountOptions = new ObservableCollection<ManagedAccount>();
        AccountingPeriods = new ObservableCollection<ManagedAccountingPeriod>();
        AccountingPeriodAuditLogs = new ObservableCollection<ManagedAuditLog>();
        AccountTypeOptions = new ObservableCollection<string>
        {
            "ASSET",
            "LIABILITY",
            "EQUITY",
            "REVENUE",
            "EXPENSE"
        };
        SubledgerTypeOptions = new ObservableCollection<string>
        {
            string.Empty,
            "VENDOR",
            "CUSTOMER",
            "EMPLOYEE"
        };
        AccountStatusFilterOptions = new ObservableCollection<string>
        {
            AccountStatusActive,
            AccountStatusAll,
            AccountStatusInactive
        };
        ClosePeriodChecklistItems = new ObservableCollection<PeriodCloseChecklistItem>();
        AccountImportErrorPanel = new InventoryImportErrorPanelState("Error Import Master Akun");
        EstateHierarchyImportErrorPanel = new InventoryImportErrorPanelState("Error Import Estate/Division/Blok");

        RefreshCommand = new RelayCommand(() => _ = LoadDataAsync());

        NewAccountCommand = new RelayCommand(NewAccount);
        _saveAccountCommand = new RelayCommand(() => _ = SaveAccountAsync(), () => CanSaveAccount);
        SaveAccountCommand = _saveAccountCommand;
        _deactivateAccountCommand = new RelayCommand(() => _ = DeactivateAccountAsync(), () => CanDeactivateAccount);
        DeactivateAccountCommand = _deactivateAccountCommand;
        _editAccountCommand = new RelayCommand(OpenEditAccountModal, () => CanEditAccount);
        EditAccountCommand = _editAccountCommand;
        _closeMasterDataModalCommand = new RelayCommand(CloseMasterDataModal);
        CloseMasterDataModalCommand = _closeMasterDataModalCommand;
        _openCostCenterDetailCommand = new RelayCommand(OpenCostCenterDetailModal, () => CanOpenCostCenterDetail);
        OpenCostCenterDetailCommand = _openCostCenterDetailCommand;
        RebuildAccountHierarchyCommand = new RelayCommand(() => _ = RebuildAccountHierarchyAsync());
        _previousAccountPageCommand = new RelayCommand(() => _ = GoToPreviousAccountPageAsync(), () => CanGoToPreviousAccountPage && !IsBusy);
        PreviousAccountPageCommand = _previousAccountPageCommand;
        _nextAccountPageCommand = new RelayCommand(() => _ = GoToNextAccountPageAsync(), () => CanGoToNextAccountPage && !IsBusy);
        NextAccountPageCommand = _nextAccountPageCommand;
        _exportAccountsCommand = new RelayCommand(() => _ = ExportAccountsAsync(), () => CanExportAccounts);
        ExportAccountsCommand = _exportAccountsCommand;
        _importAccountsCommand = new RelayCommand(() => _ = ImportAccountsAsync(), () => CanImportAccounts);
        ImportAccountsCommand = _importAccountsCommand;
        _newEstateCommand = new RelayCommand(OpenNewEstateEditor, () => CanCreateEstate);
        NewEstateCommand = _newEstateCommand;
        _newDivisionCommand = new RelayCommand(OpenNewDivisionEditor, () => CanCreateDivision);
        NewDivisionCommand = _newDivisionCommand;
        _newBlockCommand = new RelayCommand(OpenNewBlockEditor, () => CanCreateBlock);
        NewBlockCommand = _newBlockCommand;
        _editEstateHierarchyCommand = new RelayCommand(OpenEditEstateHierarchyEditor, () => CanEditEstateHierarchy);
        EditEstateHierarchyCommand = _editEstateHierarchyCommand;
        _deactivateEstateHierarchyCommand = new RelayCommand(() => _ = DeactivateEstateHierarchyAsync(), () => CanDeactivateEstateHierarchy);
        DeactivateEstateHierarchyCommand = _deactivateEstateHierarchyCommand;
        _saveEstateHierarchyCommand = new RelayCommand(() => _ = SaveEstateHierarchyAsync(), () => CanSaveEstateHierarchy);
        SaveEstateHierarchyCommand = _saveEstateHierarchyCommand;
        _cancelEstateHierarchyEditCommand = new RelayCommand(CancelEstateHierarchyEdit);
        CancelEstateHierarchyEditCommand = _cancelEstateHierarchyEditCommand;
        _importEstateHierarchyCommand = new RelayCommand(() => _ = ImportEstateHierarchyAsync(), () => CanImportEstateHierarchy);
        ImportEstateHierarchyCommand = _importEstateHierarchyCommand;
        _exportEstateHierarchyCommand = new RelayCommand(() => _ = ExportEstateHierarchyAsync(), () => CanExportEstateHierarchy);
        ExportEstateHierarchyCommand = _exportEstateHierarchyCommand;

        NewPeriodCommand = new RelayCommand(NewPeriod);
        OpenPeriodCommand = new RelayCommand(() => _ = SetPeriodStateAsync(isOpen: true));
        ClosePeriodCommand = new RelayCommand(StartCloseWizard);
        RefreshPeriodAuditCommand = new RelayCommand(() => _ = LoadPeriodAuditLogsAsync());
        _validateCloseWizardChecklistCommand = new RelayCommand(() => _ = ValidateCloseWizardChecklistAsync(), () => IsCloseWizardOpen && !IsBusy);
        ValidateCloseWizardChecklistCommand = _validateCloseWizardChecklistCommand;
        _nextCloseWizardStepCommand = new RelayCommand(() => _ = MoveCloseWizardStepAsync(1), () => CanMoveToNextCloseWizardStep);
        NextCloseWizardStepCommand = _nextCloseWizardStepCommand;
        _previousCloseWizardStepCommand = new RelayCommand(() => MoveCloseWizardStep(-1), () => CanMoveToPreviousCloseWizardStep);
        PreviousCloseWizardStepCommand = _previousCloseWizardStepCommand;
        _confirmCloseWizardCommand = new RelayCommand(() => _ = ConfirmCloseWizardAsync(), () => CanConfirmCloseWizard);
        ConfirmCloseWizardCommand = _confirmCloseWizardCommand;
        _cancelCloseWizardCommand = new RelayCommand(CancelCloseWizard);
        CancelCloseWizardCommand = _cancelCloseWizardCommand;
    }

    public event Action<DateTime, bool>? AccountingPeriodStateChanged;

    public bool CanManageAccountingPeriod { get; }

    public string PeriodActionHint =>
        CanManageAccountingPeriod
            ? "Periode bisa dibuka/ditutup sesuai kebijakan perusahaan."
            : "Hanya role SUPER_ADMIN atau FINANCE_ADMIN yang boleh membuka/menutup periode.";

    public string SelectedMasterDataSubmenu
    {
        get => _selectedMasterDataSubmenu;
        private set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "coa" : value.Trim().ToLowerInvariant();
            if (!SetProperty(ref _selectedMasterDataSubmenu, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsMasterAccountListSelected));
            OnPropertyChanged(nameof(IsMasterCostCenterSelected));
            OnPropertyChanged(nameof(IsMasterPeriodSelected));
            OnPropertyChanged(nameof(IsMasterPlaceholderSelected));
            OnPropertyChanged(nameof(MasterPlaceholderTitle));
            OnPropertyChanged(nameof(MasterPlaceholderDescription));
        }
    }

    public bool IsMasterAccountListSelected => string.Equals(SelectedMasterDataSubmenu, "coa", StringComparison.OrdinalIgnoreCase);

    public bool IsMasterCostCenterSelected => string.Equals(SelectedMasterDataSubmenu, "cost_centers", StringComparison.OrdinalIgnoreCase);

    public bool IsMasterPeriodSelected =>
        string.Equals(SelectedMasterDataSubmenu, "buka_tutup_periode", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedMasterDataSubmenu, "kunci_periode", StringComparison.OrdinalIgnoreCase);

    public bool IsMasterPlaceholderSelected =>
        string.Equals(SelectedMasterDataSubmenu, "kategori_akun", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedMasterDataSubmenu, "mapping_akun", StringComparison.OrdinalIgnoreCase);

    public string MasterPlaceholderTitle => SelectedMasterDataSubmenu switch
    {
        "kategori_akun" => "Kategori Akun",
        "mapping_akun" => "Mapping Akun",
        _ => "Master Akun"
    };

    public string MasterPlaceholderDescription => SelectedMasterDataSubmenu switch
    {
        "kategori_akun" => "Menu ini sudah tersedia pada struktur baru. Manajemen kategori akun terpisah akan ditambahkan pada iterasi berikutnya.",
        "mapping_akun" => "Menu ini sudah tersedia pada struktur baru. Workflow mapping akun akan ditambahkan pada iterasi berikutnya.",
        _ => "Fitur ini belum tersedia."
    };

    public ObservableCollection<ManagedAccount> Accounts { get; }

    public ObservableCollection<ManagedCostCenter> CostCenters { get; }

    public ObservableCollection<ManagedCostCenter> VisibleCostCenters { get; }

    public ObservableCollection<ManagedEstate> EstateHierarchyEstates { get; }

    public ObservableCollection<ManagedEstate> VisibleEstateHierarchyEstates { get; }
    
    public ObservableCollection<ManagedAccount> ParentAccountOptions { get; }

    public ObservableCollection<string> AccountStatusFilterOptions { get; }

    public ObservableCollection<ManagedAccountingPeriod> AccountingPeriods { get; }

    public ObservableCollection<ManagedAuditLog> AccountingPeriodAuditLogs { get; }

    public ObservableCollection<PeriodCloseChecklistItem> ClosePeriodChecklistItems { get; }

    public ObservableCollection<string> AccountTypeOptions { get; }

    public ObservableCollection<string> SubledgerTypeOptions { get; }

    public InventoryImportErrorPanelState AccountImportErrorPanel { get; }

    public InventoryImportErrorPanelState EstateHierarchyImportErrorPanel { get; }

    public ICommand RefreshCommand { get; }

    public ICommand NewAccountCommand { get; }

    public ICommand SaveAccountCommand { get; }

    public ICommand DeactivateAccountCommand { get; }

    public ICommand EditAccountCommand { get; }

    public ICommand CloseMasterDataModalCommand { get; }

    public ICommand OpenCostCenterDetailCommand { get; }

    public ICommand RebuildAccountHierarchyCommand { get; }

    public ICommand PreviousAccountPageCommand { get; }

    public ICommand NextAccountPageCommand { get; }

    public ICommand ExportAccountsCommand { get; }

    public ICommand ImportAccountsCommand { get; }

    public ICommand NewEstateCommand { get; }

    public ICommand NewDivisionCommand { get; }

    public ICommand NewBlockCommand { get; }

    public ICommand EditEstateHierarchyCommand { get; }

    public ICommand DeactivateEstateHierarchyCommand { get; }

    public ICommand SaveEstateHierarchyCommand { get; }

    public ICommand CancelEstateHierarchyEditCommand { get; }

    public ICommand ImportEstateHierarchyCommand { get; }

    public ICommand ExportEstateHierarchyCommand { get; }

    public ICommand NewPeriodCommand { get; }

    public ICommand OpenPeriodCommand { get; }

    public ICommand ClosePeriodCommand { get; }

    public ICommand RefreshPeriodAuditCommand { get; }

    public ICommand ValidateCloseWizardChecklistCommand { get; }

    public ICommand NextCloseWizardStepCommand { get; }

    public ICommand PreviousCloseWizardStepCommand { get; }

    public ICommand ConfirmCloseWizardCommand { get; }

    public ICommand CancelCloseWizardCommand { get; }

    public ManagedAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value))
            {
                return;
            }

            SyncSelectedParentAccountOption();
            OnPropertyChanged(nameof(HasSelectedAccount));
            OnPropertyChanged(nameof(CanEditAccount));
            OnPropertyChanged(nameof(CanDeactivateAccount));
            _editAccountCommand.RaiseCanExecuteChanged();
            _deactivateAccountCommand.RaiseCanExecuteChanged();
        }
    }

    public ManagedAccount? AccountDraft
    {
        get => _accountDraft;
        private set
        {
            if (!SetProperty(ref _accountDraft, value))
            {
                return;
            }

            SyncSelectedParentAccountOption();
            NotifyAccountFormChanged();
        }
    }

    public ManagedAccount? SelectedParentAccountOption
    {
        get => _selectedParentAccountOption;
        set
        {
            if (!SetProperty(ref _selectedParentAccountOption, value))
            {
                return;
            }

            if (_isSyncingParentSelection)
            {
                return;
            }

            if (AccountDraft is null)
            {
                return;
            }

            if (value is null)
            {
                AccountDraft.ParentAccountId = null;
                AccountDraft.ParentAccountCode = string.Empty;
                AccountDraft.HierarchyLevel = 1;
                AccountDraft.IsPosting = false;
            }
            else
            {
                AccountDraft.ParentAccountId = value.Id;
                AccountDraft.ParentAccountCode = value.Code;
                AccountDraft.HierarchyLevel = 2;
                AccountDraft.IsPosting = true;
            }

            NotifyAccountFormChanged();
        }
    }

    public ManagedCostCenter? SelectedCostCenter
    {
        get => _selectedCostCenter;
        set
        {
            if (!SetProperty(ref _selectedCostCenter, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanOpenCostCenterDetail));
            _openCostCenterDetailCommand.RaiseCanExecuteChanged();
        }
    }

    public ManagedCostCenter? CostCenterDetail
    {
        get => _costCenterDetail;
        private set => SetProperty(ref _costCenterDetail, value);
    }

    public object? SelectedEstateHierarchyItem
    {
        get => _selectedEstateHierarchyItem;
        private set => SetProperty(ref _selectedEstateHierarchyItem, value);
    }

    public ManagedEstate? SelectedEstateHierarchyEstate
    {
        get => _selectedEstateHierarchyEstate;
        private set => SetProperty(ref _selectedEstateHierarchyEstate, value);
    }

    public ManagedDivision? SelectedEstateHierarchyDivision
    {
        get => _selectedEstateHierarchyDivision;
        private set => SetProperty(ref _selectedEstateHierarchyDivision, value);
    }

    public ManagedBlock? SelectedEstateHierarchyBlock
    {
        get => _selectedEstateHierarchyBlock;
        private set => SetProperty(ref _selectedEstateHierarchyBlock, value);
    }

    public ManagedAccountingPeriod? SelectedAccountingPeriod
    {
        get => _selectedAccountingPeriod;
        set
        {
            if (!SetProperty(ref _selectedAccountingPeriod, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedAccountingPeriod));
            if (value is null)
            {
                return;
            }

            PeriodMonth = value.PeriodMonth;
            PeriodNote = value.Note;
        }
    }

    public bool HasSelectedAccount => SelectedAccount is not null && SelectedAccount.Id > 0;

    public bool HasSelectedCostCenter => SelectedCostCenter is not null;

    public bool HasSelectedAccountingPeriod => SelectedAccountingPeriod is not null;

    public string AccountSearchText
    {
        get => _accountSearchText;
        set
        {
            if (!SetProperty(ref _accountSearchText, value ?? string.Empty))
            {
                return;
            }

            _ = LoadAccountPageAsync(1, SelectedAccount?.Id);
        }
    }

    public string SelectedAccountStatusFilter
    {
        get => _selectedAccountStatusFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AccountStatusActive : value.Trim();
            if (!SetProperty(ref _selectedAccountStatusFilter, normalized))
            {
                return;
            }

            _ = LoadAccountPageAsync(1, SelectedAccount?.Id);
        }
    }

    public string CostCenterSearchText
    {
        get => _costCenterSearchText;
        set
        {
            if (!SetProperty(ref _costCenterSearchText, value ?? string.Empty))
            {
                return;
            }

            ApplyCostCenterFilter();
        }
    }

    public string SelectedCostCenterStatusFilter
    {
        get => _selectedCostCenterStatusFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AccountStatusActive : value.Trim();
            if (!SetProperty(ref _selectedCostCenterStatusFilter, normalized))
            {
                return;
            }

            ApplyCostCenterFilter();
        }
    }

    public string EstateHierarchySearchText
    {
        get => _estateHierarchySearchText;
        set
        {
            if (!SetProperty(ref _estateHierarchySearchText, value ?? string.Empty))
            {
                return;
            }

            ApplyEstateHierarchyFilter();
        }
    }

    public string SelectedEstateHierarchyStatusFilter
    {
        get => _selectedEstateHierarchyStatusFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AccountStatusActive : value.Trim();
            if (!SetProperty(ref _selectedEstateHierarchyStatusFilter, normalized))
            {
                return;
            }

            ApplyEstateHierarchyFilter();
        }
    }

    public int AccountPage
    {
        get => _accountPage;
        private set
        {
            if (!SetProperty(ref _accountPage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AccountTotalPages));
            OnPropertyChanged(nameof(CanGoToPreviousAccountPage));
            OnPropertyChanged(nameof(CanGoToNextAccountPage));
            OnPropertyChanged(nameof(AccountPageSummary));
            _previousAccountPageCommand.RaiseCanExecuteChanged();
            _nextAccountPageCommand.RaiseCanExecuteChanged();
        }
    }

    public int AccountTotalCount
    {
        get => _accountTotalCount;
        private set
        {
            if (!SetProperty(ref _accountTotalCount, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AccountTotalPages));
            OnPropertyChanged(nameof(CanGoToPreviousAccountPage));
            OnPropertyChanged(nameof(CanGoToNextAccountPage));
            OnPropertyChanged(nameof(AccountPageSummary));
            OnPropertyChanged(nameof(TotalAccountsCount));
            _previousAccountPageCommand.RaiseCanExecuteChanged();
            _nextAccountPageCommand.RaiseCanExecuteChanged();
        }
    }

    public int AccountPageSize => AccountDefaultPageSize;

    public int AccountTotalPages => Math.Max(1, (int)Math.Ceiling(AccountTotalCount / (double)AccountPageSize));

    public bool CanGoToPreviousAccountPage => AccountPage > 1;

    public bool CanGoToNextAccountPage => AccountPage < AccountTotalPages;

    public string AccountPageSummary => AccountTotalCount <= 0
        ? "Total 0 akun"
        : $"Halaman {AccountPage:N0} / {AccountTotalPages:N0} - Total {AccountTotalCount:N0} akun";

    public int TotalAccountsCount => AccountTotalCount;

    public int ActiveAccountsCount => Accounts.Count(x => x.IsActive);

    public int VisibleAccountsCount => Accounts.Count;

    public bool IsAccountCreateMode => AccountDraft is not null && AccountDraft.Id <= 0;

    public bool IsAccountEditMode => AccountDraft is not null && AccountDraft.Id > 0;

    public bool IsMasterDataModalOpen
    {
        get => _isMasterDataModalOpen;
        private set
        {
            if (!SetProperty(ref _isMasterDataModalOpen, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAccountModalOpen));
            OnPropertyChanged(nameof(IsCostCenterDetailModalOpen));
        }
    }

    public bool IsAccountModalOpen => IsMasterDataModalOpen && string.Equals(_activeMasterDataModal, "account", StringComparison.OrdinalIgnoreCase);

    public bool IsCostCenterDetailModalOpen => IsMasterDataModalOpen && string.Equals(_activeMasterDataModal, "cost_center_detail", StringComparison.OrdinalIgnoreCase);

    public bool CanExportAccounts => !IsBusy && _companyId > 0;

    public bool CanImportAccounts => !IsBusy && _companyId > 0;

    public string ExportAccountsTooltip => CanExportAccounts
        ? "Export seluruh master akun company aktif ke XLSX."
        : "Master akun sedang sibuk atau company tidak valid.";

    public string ImportAccountsTooltip => CanImportAccounts
        ? "Import master akun dari XLSX dengan mode upsert only."
        : "Master akun sedang sibuk atau company tidak valid.";

    public bool CanCreateEstate => !IsBusy && _companyId > 0 && _locationId > 0;

    public bool CanCreateDivision => !IsBusy && SelectedEstateHierarchyEstate is not null;

    public bool CanCreateBlock => !IsBusy && SelectedEstateHierarchyDivision is not null;

    public bool CanEditEstateHierarchy => !IsBusy && HasSelectedEstateHierarchyItem;

    public bool CanDeactivateEstateHierarchy => !IsBusy && HasSelectedEstateHierarchyItem;

    public bool CanImportEstateHierarchy => !IsBusy && _companyId > 0 && _locationId > 0;

    public bool CanExportEstateHierarchy => !IsBusy && _companyId > 0 && _locationId > 0;

    public bool CanSaveEstateHierarchy =>
        !IsBusy &&
        IsEstateHierarchyEditorOpen &&
        !string.IsNullOrWhiteSpace(EstateHierarchyEditorLevel) &&
        !string.IsNullOrWhiteSpace(EstateHierarchyEditorCode) &&
        !string.IsNullOrWhiteSpace(EstateHierarchyEditorName) &&
        (string.Equals(EstateHierarchyEditorLevel, "ESTATE", StringComparison.OrdinalIgnoreCase) ||
         !string.IsNullOrWhiteSpace(EstateHierarchyEditorEstateCode)) &&
        (string.Equals(EstateHierarchyEditorLevel, "BLOCK", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(EstateHierarchyEditorDivisionCode)
            : true);

    public string ExportEstateHierarchyTooltip => CanExportEstateHierarchy
        ? "Export seluruh master estate/division/blok ke XLSX."
        : "Hierarchy estate/division/blok sedang sibuk atau company/lokasi tidak valid.";

    public string ImportEstateHierarchyTooltip => CanImportEstateHierarchy
        ? "Import hierarchy estate/division/blok dari workbook XLSX 3 sheet."
        : "Hierarchy estate/division/blok sedang sibuk atau company/lokasi tidak valid.";

    public string EstateHierarchyEditorTitle =>
        EstateHierarchyEditorLevel switch
        {
            "DIVISION" => _estateHierarchyEditorEntityId > 0 ? "Edit Divisi" : "Divisi Baru",
            "BLOCK" => _estateHierarchyEditorEntityId > 0 ? "Edit Blok" : "Blok Baru",
            _ => _estateHierarchyEditorEntityId > 0 ? "Edit Estate" : "Estate Baru"
        };

    public string EstateHierarchyEditorSubtitle =>
        EstateHierarchyEditorLevel switch
        {
            "DIVISION" => "Divisi terhubung ke estate terpilih dan parent tidak dapat dipindahkan dari editor ini.",
            "BLOCK" => "Blok terhubung ke divisi terpilih dan menjadi level posting untuk jurnal.",
            _ => "Estate adalah level teratas hierarchy lokasi."
        };

    public string SelectedEstateHierarchyLevelLabel =>
        SelectedEstateHierarchyItem switch
        {
            ManagedBlock => "Blok",
            ManagedDivision => "Divisi",
            ManagedEstate => "Estate",
            _ => "-"
        };

    public string SelectedEstateHierarchyCode =>
        SelectedEstateHierarchyItem switch
        {
            ManagedBlock block => block.CostCenterCode,
            ManagedDivision division => $"{division.EstateCode}-{division.Code}",
            ManagedEstate estate => estate.Code,
            _ => "-"
        };

    public string SelectedEstateHierarchyName =>
        SelectedEstateHierarchyItem switch
        {
            ManagedBlock block => block.Name,
            ManagedDivision division => division.Name,
            ManagedEstate estate => estate.Name,
            _ => "Pilih node hierarchy di panel kiri."
        };

    public string SelectedEstateHierarchyParentLabel =>
        SelectedEstateHierarchyItem switch
        {
            ManagedBlock block => $"{block.EstateCode} / {block.DivisionCode}",
            ManagedDivision division => division.EstateCode,
            ManagedEstate => "(Root)",
            _ => "-"
        };

    public string SelectedEstateHierarchyStatusText =>
        SelectedEstateHierarchyItem switch
        {
            ManagedBlock block => block.IsActive ? "Aktif" : "Nonaktif",
            ManagedDivision division => division.IsActive ? "Aktif" : "Nonaktif",
            ManagedEstate estate => estate.IsActive ? "Aktif" : "Nonaktif",
            _ => "-"
        };

    public bool CanEditAccount => !IsBusy && SelectedAccount is not null;

    public bool CanOpenCostCenterDetail => !IsBusy && SelectedCostCenter is not null;

    public int VisibleCostCentersCount => VisibleCostCenters.Count;

    public int VisibleEstatesCount => VisibleEstateHierarchyEstates.Count;

    public int VisibleDivisionsCount => VisibleEstateHierarchyEstates.Sum(x => x.Divisions.Count);

    public int VisibleBlocksCount => VisibleEstateHierarchyEstates.Sum(x => x.Divisions.Sum(y => y.Blocks.Count));

    public string AccountEditorTitle =>
        AccountDraft is null
            ? "Editor Master Akun"
            : IsAccountCreateMode
                ? "Buat Akun Baru"
                : "Edit Akun";

    public string AccountEditorSubtitle =>
        AccountDraft is null
            ? "Pilih akun dari daftar atau klik Akun Baru untuk mulai input."
            : IsAccountCreateMode
                ? "Lengkapi kode, nama, tipe akun, dan parent jika akun turunan."
                : "Perubahan tersimpan pada akun terpilih. Gunakan Nonaktifkan jika akun tidak digunakan lagi.";

    public string DerivedParentAccountCode
    {
        get
        {
            if (AccountDraft is null)
            {
                return "-";
            }

            if (!AccountDraft.ParentAccountId.HasValue)
            {
                return "(Summary Root)";
            }

            return string.IsNullOrWhiteSpace(AccountDraft.ParentAccountCode)
                ? "-"
                : AccountDraft.ParentAccountCode;
        }
    }

    public string DerivedHierarchyLevelText
    {
        get
        {
            if (AccountDraft is null)
            {
                return "-";
            }

            return AccountDraft.HierarchyLevel <= 1
                ? "1 (Summary)"
                : "2 (Posting)";
        }
    }

    public string DerivedPostingModeText
    {
        get
        {
            if (AccountDraft is null)
            {
                return "-";
            }

            return AccountDraft.IsPosting ? "Posting" : "Non-Posting";
        }
    }

    public bool HasSelectedEstateHierarchyItem => SelectedEstateHierarchyItem is not null;

    public bool IsEstateHierarchyEditorOpen
    {
        get => _isEstateHierarchyEditorOpen;
        private set
        {
            if (!SetProperty(ref _isEstateHierarchyEditorOpen, value))
            {
                return;
            }

            NotifyEstateHierarchyEditorChanged();
        }
    }

    public string EstateHierarchyEditorLevel
    {
        get => _estateHierarchyEditorLevel;
        private set
        {
            if (!SetProperty(ref _estateHierarchyEditorLevel, value))
            {
                return;
            }

            NotifyEstateHierarchyEditorChanged();
        }
    }

    public string EstateHierarchyEditorEstateCode
    {
        get => _estateHierarchyEditorEstateCode;
        set
        {
            if (!SetProperty(ref _estateHierarchyEditorEstateCode, value ?? string.Empty))
            {
                return;
            }

            NotifyEstateHierarchyEditorChanged();
        }
    }

    public string EstateHierarchyEditorDivisionCode
    {
        get => _estateHierarchyEditorDivisionCode;
        set
        {
            if (!SetProperty(ref _estateHierarchyEditorDivisionCode, value ?? string.Empty))
            {
                return;
            }

            NotifyEstateHierarchyEditorChanged();
        }
    }

    public string EstateHierarchyEditorCode
    {
        get => _estateHierarchyEditorCode;
        set
        {
            if (!SetProperty(ref _estateHierarchyEditorCode, value ?? string.Empty))
            {
                return;
            }

            NotifyEstateHierarchyEditorChanged();
        }
    }

    public string EstateHierarchyEditorName
    {
        get => _estateHierarchyEditorName;
        set
        {
            if (!SetProperty(ref _estateHierarchyEditorName, value ?? string.Empty))
            {
                return;
            }

            NotifyEstateHierarchyEditorChanged();
        }
    }

    public bool EstateHierarchyEditorIsActive
    {
        get => _estateHierarchyEditorIsActive;
        set
        {
            if (!SetProperty(ref _estateHierarchyEditorIsActive, value))
            {
                return;
            }

            NotifyEstateHierarchyEditorChanged();
        }
    }

    public bool IsAccountValidationError => !IsAccountFormValid(out _);

    public string AccountValidationMessage
    {
        get
        {
            if (IsAccountFormValid(out var message))
            {
                return "Siap disimpan.";
            }

            return message;
        }
    }

    public bool CanSaveAccount => !IsBusy && IsAccountFormValid(out _);

    public bool CanDeactivateAccount =>
        !IsBusy &&
        ((AccountDraft is { Id: > 0, IsActive: true }) || (SelectedAccount is { Id: > 0, IsActive: true }));

    public DateTime PeriodMonth
    {
        get => _periodMonth;
        set => SetProperty(ref _periodMonth, new DateTime(value.Year, value.Month, 1));
    }

    public string PeriodNote
    {
        get => _periodNote;
        set => SetProperty(ref _periodNote, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsCloseWizardOpen
    {
        get => _isCloseWizardOpen;
        private set
        {
            if (!SetProperty(ref _isCloseWizardOpen, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CloseWizardTitle));
            OnPropertyChanged(nameof(CanMoveToNextCloseWizardStep));
            OnPropertyChanged(nameof(CanMoveToPreviousCloseWizardStep));
            OnPropertyChanged(nameof(CanConfirmCloseWizard));
            OnPropertyChanged(nameof(ShowCloseWizardNavigationButtons));
            OnPropertyChanged(nameof(ShowCloseWizardConfirmButton));
            OnPropertyChanged(nameof(ShowCloseWizardFinishButton));
            RaiseCloseWizardCanExecuteChanged();
        }
    }

    public int CloseWizardStep
    {
        get => _closeWizardStep;
        private set
        {
            var normalized = Math.Clamp(value, 1, 4);
            if (!SetProperty(ref _closeWizardStep, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsCloseWizardStep1));
            OnPropertyChanged(nameof(IsCloseWizardStep2));
            OnPropertyChanged(nameof(IsCloseWizardStep3));
            OnPropertyChanged(nameof(IsCloseWizardStep4));
            OnPropertyChanged(nameof(CloseWizardStepTitle));
            OnPropertyChanged(nameof(CloseWizardNextLabel));
            OnPropertyChanged(nameof(CanMoveToNextCloseWizardStep));
            OnPropertyChanged(nameof(CanMoveToPreviousCloseWizardStep));
            OnPropertyChanged(nameof(CanConfirmCloseWizard));
            OnPropertyChanged(nameof(ShowCloseWizardNavigationButtons));
            OnPropertyChanged(nameof(ShowCloseWizardConfirmButton));
            OnPropertyChanged(nameof(ShowCloseWizardFinishButton));
            RaiseCloseWizardCanExecuteChanged();
        }
    }

    public bool IsCloseWizardStep1 => IsCloseWizardOpen && CloseWizardStep == 1;

    public bool IsCloseWizardStep2 => IsCloseWizardOpen && CloseWizardStep == 2;

    public bool IsCloseWizardStep3 => IsCloseWizardOpen && CloseWizardStep == 3;

    public bool IsCloseWizardStep4 => IsCloseWizardOpen && CloseWizardStep == 4;

    public string CloseWizardTitle =>
        IsCloseWizardOpen
            ? $"Wizard Tutup Periode ({CloseWizardStep}/4)"
            : "Aksi Periode Terpilih";

    public string CloseWizardStepTitle => CloseWizardStep switch
    {
        1 => "Langkah 1: Konfirmasi Periode",
        2 => "Langkah 2: Checklist Pra-Closing",
        3 => "Langkah 3: Ringkasan Dampak",
        4 => "Langkah 4: Hasil Eksekusi",
        _ => "Langkah 1: Konfirmasi Periode"
    };

    public string CloseWizardValidationSummary
    {
        get => _closeWizardValidationSummary;
        private set => SetProperty(ref _closeWizardValidationSummary, value);
    }

    public string CloseWizardImpactSummary
    {
        get => _closeWizardImpactSummary;
        private set => SetProperty(ref _closeWizardImpactSummary, value);
    }

    public string CloseWizardResultMessage
    {
        get => _closeWizardResultMessage;
        private set => SetProperty(ref _closeWizardResultMessage, value);
    }

    public bool CloseWizardHasBlockers
    {
        get => _closeWizardHasBlockers;
        private set
        {
            if (!SetProperty(ref _closeWizardHasBlockers, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanMoveToNextCloseWizardStep));
            OnPropertyChanged(nameof(CanConfirmCloseWizard));
            RaiseCloseWizardCanExecuteChanged();
        }
    }

    public bool CloseWizardHasWarnings
    {
        get => _closeWizardHasWarnings;
        private set => SetProperty(ref _closeWizardHasWarnings, value);
    }

    public bool CloseWizardActionSucceeded
    {
        get => _closeWizardActionSucceeded;
        private set => SetProperty(ref _closeWizardActionSucceeded, value);
    }

    public bool CanMoveToNextCloseWizardStep =>
        IsCloseWizardOpen &&
        !IsBusy &&
        ((CloseWizardStep == 1) || (CloseWizardStep == 2 && !CloseWizardHasBlockers));

    public bool CanMoveToPreviousCloseWizardStep =>
        IsCloseWizardOpen &&
        !IsBusy &&
        (CloseWizardStep == 2 || CloseWizardStep == 3);

    public bool CanConfirmCloseWizard =>
        IsCloseWizardOpen &&
        !IsBusy &&
        CloseWizardStep == 3 &&
        !CloseWizardHasBlockers;

    public bool ShowCloseWizardNavigationButtons =>
        IsCloseWizardOpen && (CloseWizardStep == 1 || CloseWizardStep == 2 || CloseWizardStep == 3);

    public bool ShowCloseWizardConfirmButton => IsCloseWizardOpen && CloseWizardStep == 3;

    public bool ShowCloseWizardFinishButton => IsCloseWizardOpen && CloseWizardStep == 4;

    public string CloseWizardNextLabel => CloseWizardStep switch
    {
        1 => "Jalankan Checklist",
        2 => "Lanjut ke Dampak",
        _ => "Lanjut"
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(OpenPeriodTooltip));
            OnPropertyChanged(nameof(ClosePeriodTooltip));
            OnPropertyChanged(nameof(CanMoveToNextCloseWizardStep));
            OnPropertyChanged(nameof(CanMoveToPreviousCloseWizardStep));
              OnPropertyChanged(nameof(CanConfirmCloseWizard));
              OnPropertyChanged(nameof(CanEditAccount));
              OnPropertyChanged(nameof(CanOpenCostCenterDetail));
              OnPropertyChanged(nameof(CanExportAccounts));
              OnPropertyChanged(nameof(CanImportAccounts));
              OnPropertyChanged(nameof(CanCreateEstate));
              OnPropertyChanged(nameof(CanCreateDivision));
              OnPropertyChanged(nameof(CanCreateBlock));
              OnPropertyChanged(nameof(CanEditEstateHierarchy));
              OnPropertyChanged(nameof(CanDeactivateEstateHierarchy));
              OnPropertyChanged(nameof(CanSaveEstateHierarchy));
              OnPropertyChanged(nameof(CanImportEstateHierarchy));
              OnPropertyChanged(nameof(CanExportEstateHierarchy));
              OnPropertyChanged(nameof(ExportAccountsTooltip));
              OnPropertyChanged(nameof(ImportAccountsTooltip));
              OnPropertyChanged(nameof(ExportEstateHierarchyTooltip));
              OnPropertyChanged(nameof(ImportEstateHierarchyTooltip));
              NotifyAccountFormChanged();
              _previousAccountPageCommand.RaiseCanExecuteChanged();
              _nextAccountPageCommand.RaiseCanExecuteChanged();
              _editAccountCommand.RaiseCanExecuteChanged();
              _openCostCenterDetailCommand.RaiseCanExecuteChanged();
              _exportAccountsCommand.RaiseCanExecuteChanged();
              _importAccountsCommand.RaiseCanExecuteChanged();
              _newEstateCommand.RaiseCanExecuteChanged();
              _newDivisionCommand.RaiseCanExecuteChanged();
              _newBlockCommand.RaiseCanExecuteChanged();
              _editEstateHierarchyCommand.RaiseCanExecuteChanged();
              _deactivateEstateHierarchyCommand.RaiseCanExecuteChanged();
              _saveEstateHierarchyCommand.RaiseCanExecuteChanged();
              _importEstateHierarchyCommand.RaiseCanExecuteChanged();
              _exportEstateHierarchyCommand.RaiseCanExecuteChanged();
              RaiseCloseWizardCanExecuteChanged();
          }
      }

    public string OpenPeriodTooltip =>
        !CanManageAccountingPeriod
            ? "Tidak memiliki izin membuka periode."
            : IsBusy
                ? "Sedang memproses data. Tunggu hingga selesai."
                : "Buka periode bulan terpilih agar jurnal bisa disimpan/diposting.";

    public string ClosePeriodTooltip =>
        !CanManageAccountingPeriod
            ? "Tidak memiliki izin menutup periode."
            : IsBusy
                ? "Sedang memproses data. Tunggu hingga selesai."
                : "Tutup periode bulan terpilih agar jurnal tidak bisa disimpan/diposting.";

    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded)
        {
            return;
        }

        await LoadDataAsync();
    }

    public void NavigateToMasterDataSubmenu(string? subCode)
    {
        var normalized = string.IsNullOrWhiteSpace(subCode) ? "coa" : subCode.Trim().ToLowerInvariant();
        if (IsMasterDataModalOpen)
        {
            CloseMasterDataModal();
        }
        SelectedMasterDataSubmenu = normalized;
    }

    public void NotifyAccountFormChanged()
    {
        OnPropertyChanged(nameof(IsAccountCreateMode));
        OnPropertyChanged(nameof(IsAccountEditMode));
        OnPropertyChanged(nameof(AccountEditorTitle));
        OnPropertyChanged(nameof(AccountEditorSubtitle));
        OnPropertyChanged(nameof(DerivedParentAccountCode));
        OnPropertyChanged(nameof(DerivedHierarchyLevelText));
        OnPropertyChanged(nameof(DerivedPostingModeText));
        OnPropertyChanged(nameof(IsAccountValidationError));
        OnPropertyChanged(nameof(AccountValidationMessage));
        OnPropertyChanged(nameof(CanSaveAccount));
        OnPropertyChanged(nameof(CanDeactivateAccount));
        OnPropertyChanged(nameof(IsAccountModalOpen));
        OnPropertyChanged(nameof(IsCostCenterDetailModalOpen));

        _saveAccountCommand.RaiseCanExecuteChanged();
        _deactivateAccountCommand.RaiseCanExecuteChanged();
        _editAccountCommand.RaiseCanExecuteChanged();
        _openCostCenterDetailCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadDataAsync(long? selectedAccountId = null, DateTime? selectedPeriodMonth = null, bool forceReload = false)
    {
        if (IsBusy && !forceReload)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Memuat master data akun, hierarchy estate/blok, dan periode...";
            var accountTask = _accessControlService.SearchAccountsAsync(
                _companyId,
                BuildAccountSearchFilter(AccountPage),
                _actorUsername);
            var parentTask = _accessControlService.GetAccountsAsync(_companyId, includeInactive: false, actorUsername: _actorUsername);
            var periodTask = _accessControlService.GetAccountingPeriodsAsync(_companyId, _locationId, _actorUsername);
            var hierarchyTask = _accessControlService.GetEstateHierarchyAsync(_companyId, _locationId, includeInactive: true, actorUsername: _actorUsername);
            var auditTask = _accessControlService.GetAuditLogsAsync("ACCOUNTING_PERIOD", 300);
            await Task.WhenAll(accountTask, parentTask, periodTask, hierarchyTask, auditTask);

            ApplyAccountSearchResult(accountTask.Result, selectedAccountId);
            UpdateParentAccountOptions(parentTask.Result);
            ReplaceCollection(EstateHierarchyEstates, hierarchyTask.Result.Estates.OrderBy(x => x.Code));
            ApplyEstateHierarchyFilter();
            ReplaceCollection(AccountingPeriods, periodTask.Result.OrderByDescending(x => x.PeriodMonth));
            ReplaceCollection(
                AccountingPeriodAuditLogs,
                auditTask.Result
                    .Where(x => x.Details.Contains($"company={_companyId};", StringComparison.OrdinalIgnoreCase) &&
                                x.Details.Contains($"location={_locationId};", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CreatedAt));

            var month = selectedPeriodMonth.HasValue
                ? new DateTime(selectedPeriodMonth.Value.Year, selectedPeriodMonth.Value.Month, 1)
                : PeriodMonth;

            SelectedAccountingPeriod = AccountingPeriods.FirstOrDefault(x => x.PeriodMonth.Date == month.Date)
                ?? AccountingPeriods.FirstOrDefault();

            _isLoaded = true;
            StatusMessage = "Master data akun, hierarchy estate/blok, dan periode siap digunakan.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MasterDataViewModel),
                "LoadDataFailed",
                $"action=load_master_data company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = "Gagal memuat master data akun/hierarchy/periode.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewAccount()
    {
        AccountDraft = new ManagedAccount
        {
            Id = 0,
            CompanyId = _companyId,
            Code = string.Empty,
            Name = string.Empty,
            AccountType = "ASSET",
            ParentAccountId = null,
            ParentAccountCode = string.Empty,
            HierarchyLevel = 1,
            IsPosting = false,
            IsActive = true
        };
        SelectedParentAccountOption = null;
        OpenModal("account");

        StatusMessage = "Input akun baru siap.";
    }

    private void OpenEditAccountModal()
    {
        if (SelectedAccount is null)
        {
            StatusMessage = "Pilih akun yang ingin diedit.";
            return;
        }

        AccountDraft = CloneAccount(SelectedAccount);
        SyncSelectedParentAccountOption();
        OpenModal("account");
        StatusMessage = $"Akun {SelectedAccount.Code} siap diedit.";
    }

    private void OpenCostCenterDetailModal()
    {
        if (SelectedCostCenter is null)
        {
            StatusMessage = "Pilih blok yang ingin dilihat.";
            return;
        }

        CostCenterDetail = SelectedCostCenter;
        OpenModal("cost_center_detail");
        StatusMessage = $"Detail blok {SelectedCostCenter.CostCenterCode} siap ditampilkan.";
    }

    private void CloseMasterDataModal()
    {
        IsMasterDataModalOpen = false;
        _activeMasterDataModal = string.Empty;
        AccountDraft = null;
        CostCenterDetail = null;
        SelectedParentAccountOption = null;
        NotifyAccountFormChanged();
    }

    private void OpenModal(string modalName)
    {
        _activeMasterDataModal = modalName;
        IsMasterDataModalOpen = true;
        NotifyAccountFormChanged();
    }

    private async Task SaveAccountAsync()
    {
        ApplyParentSelectionToSelectedAccount();

        if (!IsAccountFormValid(out var validationMessage))
        {
            StatusMessage = validationMessage;
            NotifyAccountFormChanged();
            return;
        }

        if (AccountDraft is null)
        {
            StatusMessage = "Pilih atau buat akun terlebih dahulu.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var periodMonth = SelectedAccountingPeriod?.PeriodMonth ?? PeriodMonth;
            var result = await _accessControlService.SaveAccountAsync(_companyId, AccountDraft, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                CloseMasterDataModal();
                await LoadDataAsync(result.EntityId, periodMonth, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeactivateAccountAsync()
    {
        if (!CanDeactivateAccount)
        {
            StatusMessage = "Pilih akun aktif terlebih dahulu.";
            return;
        }

        var targetAccount = AccountDraft?.Id > 0 ? AccountDraft : SelectedAccount;
        if (targetAccount is null)
        {
            StatusMessage = "Pilih akun aktif terlebih dahulu.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var selectedId = targetAccount.Id;
            var periodMonth = SelectedAccountingPeriod?.PeriodMonth ?? PeriodMonth;
            var result = await _accessControlService.SoftDeleteAccountAsync(_companyId, selectedId, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                CloseMasterDataModal();
                await LoadDataAsync(selectedId, periodMonth, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RebuildAccountHierarchyAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var selectedId = SelectedAccount?.Id;
            var periodMonth = SelectedAccountingPeriod?.PeriodMonth ?? PeriodMonth;
            var result = await _accessControlService.RebuildAccountHierarchyAsync(_companyId, _actorUsername);
            StatusMessage = result.Message;
            if (!result.IsSuccess)
            {
                return;
            }

            await LoadDataAsync(selectedId, periodMonth, forceReload: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GoToPreviousAccountPageAsync()
    {
        if (!CanGoToPreviousAccountPage || IsBusy)
        {
            return;
        }

        await LoadAccountPageAsync(AccountPage - 1, SelectedAccount?.Id);
    }

    private async Task GoToNextAccountPageAsync()
    {
        if (!CanGoToNextAccountPage || IsBusy)
        {
            return;
        }

        await LoadAccountPageAsync(AccountPage + 1, SelectedAccount?.Id);
    }

    private async Task LoadAccountPageAsync(int requestedPage, long? preferredSelectedAccountId = null)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SearchAccountsAsync(
                _companyId,
                BuildAccountSearchFilter(requestedPage),
                _actorUsername);
            ApplyAccountSearchResult(result, preferredSelectedAccountId);
            StatusMessage = AccountTotalCount <= 0
                ? "Tidak ada akun sesuai filter."
                : $"Data akun siap. {AccountPageSummary}.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MasterDataViewModel),
                "LoadAccountPageFailed",
                $"action=load_account_page company_id={_companyId} page={requestedPage}",
                ex);
            StatusMessage = "Gagal memuat halaman akun.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AccountSearchFilter BuildAccountSearchFilter(int requestedPage)
    {
        return new AccountSearchFilter
        {
            Keyword = AccountSearchText,
            Status = SelectedAccountStatusFilter,
            Page = requestedPage,
            PageSize = AccountPageSize
        };
    }

    private void ApplyAccountSearchResult(AccountSearchResult result, long? preferredSelectedAccountId = null)
    {
        ReplaceCollection(Accounts, result.Items);
        AccountPage = Math.Max(1, result.Page);
        AccountTotalCount = Math.Max(0, result.TotalCount);

        var preferredAccount = preferredSelectedAccountId.HasValue
            ? Accounts.FirstOrDefault(x => x.Id == preferredSelectedAccountId.Value)
            : null;
        SelectedAccount = preferredAccount ?? Accounts.FirstOrDefault();

        OnPropertyChanged(nameof(VisibleAccountsCount));
        OnPropertyChanged(nameof(ActiveAccountsCount));
    }

    private void UpdateParentAccountOptions(IEnumerable<ManagedAccount> source)
    {
        ReplaceCollection(
            ParentAccountOptions,
            source
                .Where(x => x.IsActive && (!x.ParentAccountId.HasValue || x.HierarchyLevel <= 1))
                .OrderBy(x => x.Code));
        SyncSelectedParentAccountOption();
    }

    private void SyncSelectedParentAccountOption()
    {
        _isSyncingParentSelection = true;
        try
        {
            if (AccountDraft?.ParentAccountId is not long parentId || parentId <= 0)
            {
                SelectedParentAccountOption = null;
                return;
            }

            SelectedParentAccountOption = ParentAccountOptions.FirstOrDefault(x => x.Id == parentId);
        }
        finally
        {
            _isSyncingParentSelection = false;
        }
    }

    private void ApplyParentSelectionToSelectedAccount()
    {
        if (AccountDraft is null)
        {
            return;
        }

        if (SelectedParentAccountOption is null)
        {
            AccountDraft.ParentAccountId = null;
            AccountDraft.ParentAccountCode = string.Empty;
            AccountDraft.HierarchyLevel = 1;
            AccountDraft.IsPosting = false;
            return;
        }

        AccountDraft.ParentAccountId = SelectedParentAccountOption.Id;
        AccountDraft.ParentAccountCode = SelectedParentAccountOption.Code;
        AccountDraft.HierarchyLevel = 2;
        AccountDraft.IsPosting = true;
    }

    private void NewPeriod()
    {
        SelectedAccountingPeriod = null;
        PeriodMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        PeriodNote = string.Empty;
        StatusMessage = "Input periode baru siap.";
    }

    private void StartCloseWizard()
    {
        if (!CanManageAccountingPeriod)
        {
            StatusMessage = "Anda tidak memiliki izin untuk mengelola periode akuntansi.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsCloseWizardOpen = true;
        CloseWizardStep = 1;
        CloseWizardValidationSummary = "Checklist belum dijalankan.";
        CloseWizardImpactSummary = BuildCloseWizardImpactSummary();
        CloseWizardResultMessage = string.Empty;
        CloseWizardHasBlockers = false;
        CloseWizardHasWarnings = false;
        CloseWizardActionSucceeded = false;
        ClosePeriodChecklistItems.Clear();
    }

    private void CancelCloseWizard()
    {
        IsCloseWizardOpen = false;
        CloseWizardStep = 1;
        CloseWizardValidationSummary = "Checklist belum dijalankan.";
        CloseWizardImpactSummary = string.Empty;
        CloseWizardResultMessage = string.Empty;
        CloseWizardHasBlockers = false;
        CloseWizardHasWarnings = false;
        CloseWizardActionSucceeded = false;
        ClosePeriodChecklistItems.Clear();
    }

    private void MoveCloseWizardStep(int delta)
    {
        if (!IsCloseWizardOpen || IsBusy)
        {
            return;
        }

        CloseWizardStep = Math.Clamp(CloseWizardStep + delta, 1, 4);
    }

    private async Task MoveCloseWizardStepAsync(int delta)
    {
        if (!IsCloseWizardOpen || IsBusy)
        {
            return;
        }

        if (delta > 0 && CloseWizardStep == 1)
        {
            CloseWizardStep = 2;
            await ValidateCloseWizardChecklistAsync();
            return;
        }

        if (delta > 0 && CloseWizardStep == 2)
        {
            if (CloseWizardHasBlockers)
            {
                StatusMessage = "Checklist masih memiliki blocker. Selesaikan dulu sebelum lanjut.";
                return;
            }

            CloseWizardStep = 3;
            CloseWizardImpactSummary = BuildCloseWizardImpactSummary();
            return;
        }

        MoveCloseWizardStep(delta);
    }

    private async Task ValidateCloseWizardChecklistAsync()
    {
        if (!IsCloseWizardOpen || IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var monthStart = new DateTime(PeriodMonth.Year, PeriodMonth.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);

            var checklist = new List<PeriodCloseChecklistItem>();
            var periods = await _accessControlService.GetAccountingPeriodsAsync(_companyId, _locationId, _actorUsername);
            var selectedPeriod = periods.FirstOrDefault(x => x.PeriodMonth.Date == monthStart.Date);

            if (selectedPeriod is null)
            {
                checklist.Add(new PeriodCloseChecklistItem
                {
                    Key = "period_exists",
                    Label = "Periode akuntansi tersedia",
                    Severity = "Blocker",
                    Message = $"Periode {monthStart:yyyy-MM} belum terbentuk pada lokasi aktif."
                });
            }
            else if (!selectedPeriod.IsOpen)
            {
                checklist.Add(new PeriodCloseChecklistItem
                {
                    Key = "period_open",
                    Label = "Status periode",
                    Severity = "Blocker",
                    Message = $"Periode {monthStart:yyyy-MM} sudah CLOSED."
                });
            }
            else
            {
                checklist.Add(new PeriodCloseChecklistItem
                {
                    Key = "period_open",
                    Label = "Status periode",
                    Severity = "Ready",
                    Message = $"Periode {monthStart:yyyy-MM} masih OPEN dan siap divalidasi untuk closing."
                });
            }

            var pendingJournals = await CountPendingJournalsAsync(monthStart, monthEnd);
            checklist.Add(new PeriodCloseChecklistItem
            {
                Key = "journal_pending",
                Label = "Jurnal accounting pending",
                Severity = pendingJournals > 0 ? "Blocker" : "Ready",
                Message = pendingJournals > 0
                    ? $"Ditemukan {pendingJournals:N0} jurnal belum POSTED pada periode ini."
                    : "Tidak ada jurnal accounting pending."
            });

            var pendingInventoryTx = await CountPendingInventoryTransactionsAsync(monthStart, monthEnd);
            checklist.Add(new PeriodCloseChecklistItem
            {
                Key = "inventory_tx_pending",
                Label = "Transaksi inventory pending",
                Severity = pendingInventoryTx > 0 ? "Blocker" : "Ready",
                Message = pendingInventoryTx > 0
                    ? $"Ditemukan {pendingInventoryTx:N0} transaksi inventory belum POSTED."
                    : "Semua transaksi inventory periode ini sudah POSTED."
            });

            var (pendingOpname, postedOpname) = await CountStockOpnamePeriodStatusAsync(monthStart, monthEnd);
            checklist.Add(new PeriodCloseChecklistItem
            {
                Key = "inventory_opname_pending",
                Label = "Stock opname pending",
                Severity = pendingOpname > 0 ? "Blocker" : "Ready",
                Message = pendingOpname > 0
                    ? $"Ditemukan {pendingOpname:N0} stock opname belum POSTED."
                    : "Tidak ada stock opname pending."
            });

            checklist.Add(new PeriodCloseChecklistItem
            {
                Key = "inventory_opname_coverage",
                Label = "Cakupan stock opname",
                Severity = postedOpname > 0 ? "Ready" : "Warning",
                Message = postedOpname > 0
                    ? $"Stock opname POSTED periode ini: {postedOpname:N0} dokumen."
                    : "Belum ada stock opname POSTED pada periode ini. Lanjutkan hanya jika kebijakan internal mengizinkan."
            });

            checklist.Add(new PeriodCloseChecklistItem
            {
                Key = "close_note",
                Label = "Catatan penutupan",
                Severity = string.IsNullOrWhiteSpace(PeriodNote) ? "Warning" : "Ready",
                Message = string.IsNullOrWhiteSpace(PeriodNote)
                    ? "Catatan close masih kosong."
                    : "Catatan close sudah terisi."
            });

            ReplaceCollection(ClosePeriodChecklistItems, checklist);

            CloseWizardHasBlockers = checklist.Any(x => string.Equals(x.Severity, "Blocker", StringComparison.OrdinalIgnoreCase));
            CloseWizardHasWarnings = checklist.Any(x => string.Equals(x.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
            var readyCount = checklist.Count(x => string.Equals(x.Severity, "Ready", StringComparison.OrdinalIgnoreCase));
            var warningCount = checklist.Count(x => string.Equals(x.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
            var blockerCount = checklist.Count(x => string.Equals(x.Severity, "Blocker", StringComparison.OrdinalIgnoreCase));

            CloseWizardValidationSummary = blockerCount > 0
                ? $"Checklist: {readyCount} Ready, {warningCount} Warning, {blockerCount} Blocker."
                : $"Checklist valid: {readyCount} Ready, {warningCount} Warning, tanpa blocker.";
            CloseWizardImpactSummary = BuildCloseWizardImpactSummary();
            StatusMessage = CloseWizardValidationSummary;
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MasterDataViewModel),
                "ValidateCloseWizardChecklistFailed",
                $"action=validate_close_wizard company_id={_companyId} location_id={_locationId} period={PeriodMonth:yyyy-MM}",
                ex);
            CloseWizardValidationSummary = "Gagal memvalidasi checklist close periode.";
            StatusMessage = CloseWizardValidationSummary;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<int> CountPendingJournalsAsync(DateTime monthStart, DateTime monthEnd)
    {
        var pendingStatuses = new[] { "DRAFT", "SUBMITTED", "APPROVED" };
        var pendingCount = 0;
        foreach (var status in pendingStatuses)
        {
            var journals = await _accessControlService.SearchJournalsAsync(
                _companyId,
                _locationId,
                new JournalSearchFilter
                {
                    DateFrom = monthStart,
                    DateTo = monthEnd,
                    Status = status
                },
                _actorUsername);
            pendingCount += journals.Count;
        }

        return pendingCount;
    }

    private async Task<int> CountPendingInventoryTransactionsAsync(DateTime monthStart, DateTime monthEnd)
    {
        var txList = await _accessControlService.SearchStockTransactionsAsync(
            _companyId,
            _locationId,
            new InventoryTransactionSearchFilter
            {
                DateFrom = monthStart,
                DateTo = monthEnd
            });

        return txList.Count(x => !string.Equals(x.Status, "POSTED", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(int PendingOpnameCount, int PostedOpnameCount)> CountStockOpnamePeriodStatusAsync(
        DateTime monthStart,
        DateTime monthEnd)
    {
        var allOpname = await _accessControlService.SearchStockOpnameAsync(_companyId, _locationId, string.Empty);
        var scoped = allOpname.Where(x => x.OpnameDate.Date >= monthStart.Date && x.OpnameDate.Date <= monthEnd.Date).ToList();
        var postedCount = scoped.Count(x => string.Equals(x.Status, "POSTED", StringComparison.OrdinalIgnoreCase));
        var pendingCount = scoped.Count - postedCount;
        return (pendingCount, postedCount);
    }

    private string BuildCloseWizardImpactSummary()
    {
        var monthStart = new DateTime(PeriodMonth.Year, PeriodMonth.Month, 1);
        return
            $"Periode {monthStart:yyyy-MM} akan diubah ke CLOSED.\n" +
            "- Simpan/Post jurnal pada periode ini akan dinonaktifkan.\n" +
            "- Sistem akan membentuk jurnal penutup otomatis jika syarat backend terpenuhi.\n" +
            "- Audit log close/reopen akan direkam untuk company dan lokasi aktif.";
    }

    private async Task ConfirmCloseWizardAsync()
    {
        if (!CanConfirmCloseWizard)
        {
            StatusMessage = CloseWizardHasBlockers
                ? "Checklist masih memiliki blocker. Close periode tidak dapat dilanjutkan."
                : "Wizard close belum siap dikonfirmasi.";
            return;
        }

        CloseWizardResultMessage = string.Empty;
        CloseWizardActionSucceeded = false;

        var result = await SetPeriodStateAsync(isOpen: false, skipConfirmationPrompt: true);
        CloseWizardActionSucceeded = result.IsSuccess;
        CloseWizardResultMessage = result.Message;
        CloseWizardStep = 4;
    }

    private async Task<AccessOperationResult> SetPeriodStateAsync(bool isOpen, bool skipConfirmationPrompt = false)
    {
        if (!CanManageAccountingPeriod)
        {
            return new AccessOperationResult(false, "Anda tidak memiliki izin untuk mengelola periode akuntansi.");
        }

        if (IsBusy)
        {
            return new AccessOperationResult(false, "Sedang memproses data. Tunggu hingga selesai.");
        }

        if (!isOpen && !skipConfirmationPrompt)
        {
            var confirmText =
                $"Menutup periode {PeriodMonth:yyyy-MM} akan menonaktifkan Save/Post jurnal pada bulan tersebut. Lanjutkan?";
            if (!_confirmClosePeriod(confirmText))
            {
                StatusMessage = "Penutupan periode dibatalkan.";
                return new AccessOperationResult(false, StatusMessage);
            }
        }

        AccessOperationResult result;
        try
        {
            IsBusy = true;
            var accountId = SelectedAccount?.Id;
            result = await _accessControlService.SetAccountingPeriodOpenStateAsync(
                _companyId,
                _locationId,
                PeriodMonth,
                isOpen,
                _actorUsername,
                PeriodNote);

            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(accountId, PeriodMonth, forceReload: true);
                AccountingPeriodStateChanged?.Invoke(
                    new DateTime(PeriodMonth.Year, PeriodMonth.Month, 1),
                    isOpen);
            }
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MasterDataViewModel),
                "SetPeriodStateAsyncFailed",
                $"action=set_period_state company_id={_companyId} location_id={_locationId} period={PeriodMonth:yyyy-MM} is_open={isOpen}",
                ex);
            result = new AccessOperationResult(false, "Gagal memperbarui status periode.");
            StatusMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
        }

        return result;
    }

    private async Task LoadPeriodAuditLogsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var auditLogs = await _accessControlService.GetAuditLogsAsync("ACCOUNTING_PERIOD", 300);
            ReplaceCollection(
                AccountingPeriodAuditLogs,
                auditLogs
                    .Where(x => x.Details.Contains($"company={_companyId};", StringComparison.OrdinalIgnoreCase) &&
                                x.Details.Contains($"location={_locationId};", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CreatedAt));
            StatusMessage = "Audit periode berhasil dimuat.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MasterDataViewModel),
                "LoadPeriodAuditLogsFailed",
                $"action=load_period_audit company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = "Gagal memuat audit periode.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsAccountFormValid(out string message)
    {
        if (AccountDraft is null)
        {
            message = "Pilih akun dari daftar atau klik Akun Baru.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AccountDraft.Code))
        {
            message = "Kode akun wajib diisi.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AccountDraft.Name))
        {
            message = "Nama akun wajib diisi.";
            return false;
        }

        if (!IsSegmentedAccountCode(AccountDraft.Code))
        {
            message = "Format kode akun harus 99.99999.999.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AccountDraft.AccountType))
        {
            message = "Tipe akun wajib dipilih.";
            return false;
        }

        if (AccountDraft.RequiresSubledger && string.IsNullOrWhiteSpace(AccountDraft.AllowedSubledgerType))
        {
            message = "Jenis buku bantu wajib dipilih jika akun mewajibkan buku bantu.";
            return false;
        }

        if (SelectedParentAccountOption is not null)
        {
            if (AccountDraft.Id > 0 && SelectedParentAccountOption.Id == AccountDraft.Id)
            {
                message = "Parent akun tidak boleh akun itu sendiri.";
                return false;
            }

            if (SelectedParentAccountOption.ParentAccountId.HasValue)
            {
                message = "Parent akun harus akun level 1 (summary).";
                return false;
            }

            if (!string.Equals(AccountDraft.AccountType, SelectedParentAccountOption.AccountType, StringComparison.OrdinalIgnoreCase))
            {
                message = "Tipe akun child harus sama dengan parent.";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private void ApplyCostCenterFilter()
    {
        IEnumerable<ManagedCostCenter> query = CostCenters;

        var keyword = (CostCenterSearchText ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                x.CostCenterCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.CostCenterName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.EstateCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.DivisionCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.BlockCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.BlockName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(SelectedCostCenterStatusFilter, AccountStatusActive, StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.IsActive);
        }
        else if (string.Equals(SelectedCostCenterStatusFilter, AccountStatusInactive, StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => !x.IsActive);
        }

        ReplaceCollection(VisibleCostCenters, query.OrderBy(x => x.CostCenterCode));
        SelectedCostCenter = VisibleCostCenters.FirstOrDefault();
        OnPropertyChanged(nameof(VisibleCostCentersCount));
    }

    private static ManagedAccount CloneAccount(ManagedAccount source)
    {
        return new ManagedAccount
        {
            Id = source.Id,
            CompanyId = source.CompanyId,
            Code = source.Code,
            Name = source.Name,
            AccountType = source.AccountType,
            ParentAccountId = source.ParentAccountId,
            ParentAccountCode = source.ParentAccountCode,
            HierarchyLevel = source.HierarchyLevel,
            IsPosting = source.IsPosting,
            IsActive = source.IsActive,
            RequiresDepartment = source.RequiresDepartment,
            RequiresProject = source.RequiresProject,
            RequiresCostCenter = source.RequiresCostCenter,
            RequiresSubledger = source.RequiresSubledger,
            AllowedSubledgerType = source.AllowedSubledgerType
        };
    }

    private static bool IsSegmentedAccountCode(string? accountCode)
    {
        var code = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length != 12 || code[2] != '.' || code[8] != '.')
        {
            return false;
        }

        if (!char.IsDigit(code[0]) || !char.IsDigit(code[1]))
        {
            return false;
        }

        for (var i = 3; i <= 7; i++)
        {
            if (!char.IsDigit(code[i]))
            {
                return false;
            }
        }

        for (var i = 9; i <= 11; i++)
        {
            if (!char.IsDigit(code[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void RaiseCloseWizardCanExecuteChanged()
    {
        _validateCloseWizardChecklistCommand.RaiseCanExecuteChanged();
        _nextCloseWizardStepCommand.RaiseCanExecuteChanged();
        _previousCloseWizardStepCommand.RaiseCanExecuteChanged();
        _confirmCloseWizardCommand.RaiseCanExecuteChanged();
        _cancelCloseWizardCommand.RaiseCanExecuteChanged();
    }
}
