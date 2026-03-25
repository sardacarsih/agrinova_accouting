using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class SelectableOption : ViewModelBase
{
    private bool _isSelected;
    private bool _isEnabled = true;

    public long Id { get; init; }

    public string Label { get; init; } = string.Empty;

    public long? GroupId { get; init; }

    public string ModuleCode { get; init; } = string.Empty;

    public string ModuleName { get; init; } = string.Empty;

    public string SubmoduleCode { get; init; } = string.Empty;

    public string SubmoduleName { get; init; } = string.Empty;

    public string ActionCode { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsEnabled && value)
            {
                return;
            }

            SetProperty(ref _isSelected, value);
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            if (!value)
            {
                IsSelected = false;
            }
        }
    }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Label) ? base.ToString() ?? string.Empty : Label;
    }
}

public sealed class PermissionSubmoduleGroup : ViewModelBase
{
    private bool _isExpanded = true;

    public string ModuleCode { get; init; } = string.Empty;

    public string ModuleName { get; init; } = string.Empty;

    public string SubmoduleCode { get; init; } = string.Empty;

    public string SubmoduleName { get; init; } = string.Empty;

    public ObservableCollection<SelectableOption> Actions { get; init; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public int SelectedActionsCount => Actions.Count(x => x.IsSelected);

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedActionsCount));
    }
}

public sealed class PermissionModuleGroup : ViewModelBase
{
    private bool _isExpanded = true;

    public string ModuleCode { get; init; } = string.Empty;

    public string ModuleName { get; init; } = string.Empty;

    public ObservableCollection<PermissionSubmoduleGroup> Submodules { get; init; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public int SelectedActionsCount => Submodules.Sum(x => x.SelectedActionsCount);

    public void NotifySelectionChanged()
    {
        foreach (var submodule in Submodules)
        {
            submodule.NotifySelectionChanged();
        }

        OnPropertyChanged(nameof(SelectedActionsCount));
    }
}

public sealed class UserManagementViewModel : ViewModelBase
{
    private readonly IAccessControlService _accessControlService;
    private readonly string _actorUsername;
    private readonly bool _canManageMasterCompanySetting;

    private readonly Dictionary<long, HashSet<long>> _userRoleMap = new();
    private readonly Dictionary<long, HashSet<long>> _roleScopeMap = new();
    private readonly Dictionary<long, HashSet<long>> _userCompanyMap = new();
    private readonly Dictionary<long, HashSet<long>> _userLocationMap = new();

    private ManagedUser? _selectedUser;
    private ManagedRole? _selectedRole;
    private ManagedCompany? _selectedCompany;
    private ManagedLocation? _selectedLocation;
    private long? _selectedInventoryMasterCompanyId;
    private long? _selectedInventoryCostingCompanyId;
    private long? _selectedInventoryCostingLocationId;
    private string _centralSyncBaseUrl = string.Empty;
    private string _centralSyncApiKey = string.Empty;
    private string _centralSyncUploadPath = "/api/inventory/sync/upload";
    private string _centralSyncDownloadPath = "/api/inventory/sync/download";
    private int _centralSyncTimeoutSeconds = 30;
    private string _inventoryValuationMethod = "AVERAGE";
    private string _inventoryCogsAccountCode = string.Empty;
    private string _inventoryCompanyDefaultValuationMethod = "AVERAGE";
    private string _inventoryCompanyDefaultCogsAccountCode = string.Empty;
    private bool _useInventoryLocationOverride;
    private long? _selectedUserRoleId;
    private long? _selectedUserDefaultCompanyId;
    private long? _selectedUserDefaultLocationId;
    private string _newUserPassword = string.Empty;
    private string _statusMessage = string.Empty;
    private string _roleSearchText = string.Empty;
    private string _rolePermissionSearchText = string.Empty;
    private string _selectedSettingsSubTab = "companies";
    private bool _showSelectedRolePermissionsOnly;
    private bool _isRoleEditorDirty;
    private bool _suppressRoleEditorDirtyTracking;
    private bool _isRoleSelectionInternalChange;
    private int _selectedRolePermissionCount;
    private int _visibleRolePermissionCount;
    private int _selectedRoleAssignedUserCount;
    private RoleEditorSnapshot? _roleEditorSnapshot;
    private bool _suppressInventoryCostingAutoLoad;
    private bool _suppressDefaultSelectionSync;
    private bool _isBusy;
    private bool _isLoaded;

    private sealed class RoleEditorSnapshot
    {
        public long RoleId { get; init; }

        public string Code { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public bool IsSuperRole { get; init; }

        public bool IsActive { get; init; }

        public HashSet<long> SelectedScopeIds { get; init; } = new();
    }

    public UserManagementViewModel(
        IAccessControlService accessControlService,
        string actorUsername,
        bool canManageMasterCompanySetting = false)
    {
        _accessControlService = accessControlService;
        _actorUsername = string.IsNullOrWhiteSpace(actorUsername) ? "SYSTEM" : actorUsername.Trim();
        _canManageMasterCompanySetting = canManageMasterCompanySetting;

        Users = new ObservableCollection<ManagedUser>();
        Roles = new ObservableCollection<ManagedRole>();
        AccessScopes = new ObservableCollection<ManagedAccessScope>();
        Companies = new ObservableCollection<ManagedCompany>();
        Locations = new ObservableCollection<ManagedLocation>();
        FilteredLocations = new ObservableCollection<ManagedLocation>();
        ActiveCompanyOptions = new ObservableCollection<ManagedCompany>();
        InventoryCostingLocationOptions = new ObservableCollection<ManagedLocation>();
        InventoryCostingAccountOptions = new ObservableCollection<ManagedAccount>();
        InventoryValuationMethodOptions = new ObservableCollection<string>(new[]
        {
            "AVERAGE",
            "FIFO",
            "LIFO"
        });

        UserRoleOptions = new ObservableCollection<SelectableOption>();
        RoleScopeOptions = new ObservableCollection<SelectableOption>();
        UserCompanyOptions = new ObservableCollection<SelectableOption>();
        UserLocationOptions = new ObservableCollection<SelectableOption>();
        UserDefaultCompanyOptions = new ObservableCollection<ManagedCompany>();
        UserDefaultLocationOptions = new ObservableCollection<ManagedLocation>();
        LocationTypeOptions = new ObservableCollection<string>(new[] { "ESTATE", "MILL", "OFFICE" });
        RolePermissionModules = new ObservableCollection<PermissionModuleGroup>();
        FilteredRoles = new ObservableCollection<ManagedRole>();

        RefreshCommand = new RelayCommand(() => _ = LoadDataAsync());
        NewUserCommand = new RelayCommand(NewUser);
        SaveUserCommand = new RelayCommand(() => _ = SaveUserAsync());
        NewRoleCommand = new RelayCommand(NewRole);
        CloneRoleCommand = new RelayCommand(() => _ = CloneRoleAsync());
        SaveRoleCommand = new RelayCommand(() => _ = SaveRoleAsync());
        DeleteRoleCommand = new RelayCommand(() => _ = DeleteRoleAsync());
        NewCompanyCommand = new RelayCommand(NewCompany);
        SaveCompanyCommand = new RelayCommand(() => _ = SaveCompanyAsync());
        DeleteCompanyCommand = new RelayCommand(() => _ = DeleteCompanyAsync());
        NewLocationCommand = new RelayCommand(NewLocation);
        SaveLocationCommand = new RelayCommand(() => _ = SaveLocationAsync());
        DeleteLocationCommand = new RelayCommand(() => _ = DeleteLocationAsync());
        SaveInventoryMasterCompanyCommand = new RelayCommand(() => _ = SaveInventoryMasterCompanyAsync());
        SaveInventoryCostingSettingsCommand = new RelayCommand(() => _ = SaveInventoryCostingSettingsAsync());
        SaveInventoryLocationCostingSettingsCommand = new RelayCommand(() => _ = SaveInventoryLocationCostingSettingsAsync());
        RecalculateInventoryCostingCompanyCommand = new RelayCommand(() => _ = RecalculateInventoryCostingCompanyAsync());
        RecalculateInventoryCostingLocationCommand = new RelayCommand(() => _ = RecalculateInventoryCostingLocationAsync());
        SaveInventoryCentralSyncSettingsCommand = new RelayCommand(() => _ = SaveInventoryCentralSyncSettingsAsync());
        SelectAllScopesCommand = new RelayCommand(() => SetAllRoleScopes(true));
        DeselectAllScopesCommand = new RelayCommand(() => SetAllRoleScopes(false));
        SelectAllModuleActionsCommand = new RelayCommand(parameter => SetAllModuleActions(parameter, true));
        DeselectAllModuleActionsCommand = new RelayCommand(parameter => SetAllModuleActions(parameter, false));
        SelectAllSubmoduleActionsCommand = new RelayCommand(parameter => SetAllSubmoduleActions(parameter, true));
        DeselectAllSubmoduleActionsCommand = new RelayCommand(parameter => SetAllSubmoduleActions(parameter, false));
        ExpandAllRolePermissionsCommand = new RelayCommand(ExpandAllRolePermissionGroups);
        CollapseAllRolePermissionsCommand = new RelayCommand(CollapseAllRolePermissionGroups);
        ClearRolePermissionFilterCommand = new RelayCommand(ClearRolePermissionFilter);
        DiscardRoleChangesCommand = new RelayCommand(DiscardRoleChanges);
        SelectAllUserCompaniesCommand = new RelayCommand(() => SetAllOptions(UserCompanyOptions, true));
        DeselectAllUserCompaniesCommand = new RelayCommand(() => SetAllOptions(UserCompanyOptions, false));
        SelectAllUserLocationsCommand = new RelayCommand(() => SetAllOptions(UserLocationOptions, true));
        DeselectAllUserLocationsCommand = new RelayCommand(() => SetAllOptions(UserLocationOptions, false));
    }

    public ObservableCollection<ManagedUser> Users { get; }

    public ObservableCollection<ManagedRole> Roles { get; }

    public ObservableCollection<ManagedAccessScope> AccessScopes { get; }

    public ObservableCollection<ManagedCompany> Companies { get; }

    public ObservableCollection<ManagedLocation> Locations { get; }

    public ObservableCollection<ManagedLocation> FilteredLocations { get; }

    public ObservableCollection<ManagedCompany> ActiveCompanyOptions { get; }

    public ObservableCollection<ManagedLocation> InventoryCostingLocationOptions { get; }

    public ObservableCollection<ManagedAccount> InventoryCostingAccountOptions { get; }

    public ObservableCollection<string> InventoryValuationMethodOptions { get; }

    public ObservableCollection<SelectableOption> UserRoleOptions { get; }

    public ObservableCollection<SelectableOption> RoleScopeOptions { get; }

    public ObservableCollection<SelectableOption> UserCompanyOptions { get; }

    public ObservableCollection<SelectableOption> UserLocationOptions { get; }

    public ObservableCollection<ManagedCompany> UserDefaultCompanyOptions { get; }

    public ObservableCollection<ManagedLocation> UserDefaultLocationOptions { get; }

    public ObservableCollection<string> LocationTypeOptions { get; }

    public ObservableCollection<PermissionModuleGroup> RolePermissionModules { get; }

    public ObservableCollection<ManagedRole> FilteredRoles { get; }

    public ICommand RefreshCommand { get; }

    public ICommand NewUserCommand { get; }

    public ICommand SaveUserCommand { get; }

    public ICommand NewRoleCommand { get; }

    public ICommand CloneRoleCommand { get; }

    public ICommand SaveRoleCommand { get; }

    public ICommand DeleteRoleCommand { get; }

    public ICommand NewCompanyCommand { get; }

    public ICommand SaveCompanyCommand { get; }

    public ICommand DeleteCompanyCommand { get; }

    public ICommand NewLocationCommand { get; }

    public ICommand SaveLocationCommand { get; }

    public ICommand DeleteLocationCommand { get; }

    public ICommand SaveInventoryMasterCompanyCommand { get; }

    public ICommand SaveInventoryCostingSettingsCommand { get; }

    public ICommand SaveInventoryLocationCostingSettingsCommand { get; }

    public ICommand RecalculateInventoryCostingCompanyCommand { get; }

    public ICommand RecalculateInventoryCostingLocationCommand { get; }

    public ICommand SaveInventoryCentralSyncSettingsCommand { get; }

    public ICommand SelectAllScopesCommand { get; }

    public ICommand DeselectAllScopesCommand { get; }

    public ICommand SelectAllModuleActionsCommand { get; }

    public ICommand DeselectAllModuleActionsCommand { get; }

    public ICommand SelectAllSubmoduleActionsCommand { get; }

    public ICommand DeselectAllSubmoduleActionsCommand { get; }

    public ICommand ExpandAllRolePermissionsCommand { get; }

    public ICommand CollapseAllRolePermissionsCommand { get; }

    public ICommand ClearRolePermissionFilterCommand { get; }

    public ICommand DiscardRoleChangesCommand { get; }

    public ICommand SelectAllUserCompaniesCommand { get; }

    public ICommand DeselectAllUserCompaniesCommand { get; }

    public ICommand SelectAllUserLocationsCommand { get; }

    public ICommand DeselectAllUserLocationsCommand { get; }

    public int TotalRolesCount => Roles.Count;

    public int ActiveRolesCount => Roles.Count(r => r.IsActive);

    public int TotalScopesCount => AccessScopes.Count;

    public int TotalUsersCount => Users.Count;

    public string RoleSearchText
    {
        get => _roleSearchText;
        set
        {
            if (!SetProperty(ref _roleSearchText, value))
            {
                return;
            }

            FilterRoles();
        }
    }

    public string RolePermissionSearchText
    {
        get => _rolePermissionSearchText;
        set
        {
            if (!SetProperty(ref _rolePermissionSearchText, value))
            {
                return;
            }

            BuildRolePermissionTree();
            OnPropertyChanged(nameof(HasRolePermissionFilter));
        }
    }

    public bool ShowSelectedRolePermissionsOnly
    {
        get => _showSelectedRolePermissionsOnly;
        set
        {
            if (!SetProperty(ref _showSelectedRolePermissionsOnly, value))
            {
                return;
            }

            BuildRolePermissionTree();
            OnPropertyChanged(nameof(HasRolePermissionFilter));
        }
    }

    public bool HasRolePermissionFilter =>
        ShowSelectedRolePermissionsOnly || !string.IsNullOrWhiteSpace(RolePermissionSearchText);

    public bool IsRoleEditorDirty
    {
        get => _isRoleEditorDirty;
        private set => SetProperty(ref _isRoleEditorDirty, value);
    }

    public int SelectedRolePermissionCount
    {
        get => _selectedRolePermissionCount;
        private set => SetProperty(ref _selectedRolePermissionCount, value);
    }

    public int VisibleRolePermissionCount
    {
        get => _visibleRolePermissionCount;
        private set => SetProperty(ref _visibleRolePermissionCount, value);
    }

    public int SelectedRoleAssignedUserCount
    {
        get => _selectedRoleAssignedUserCount;
        private set => SetProperty(ref _selectedRoleAssignedUserCount, value);
    }

    public string RoleEditorDirtyMessage =>
        IsRoleEditorDirty
            ? "Ada perubahan role yang belum disimpan."
            : "Semua perubahan role sudah tersimpan.";

    public ManagedUser? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (!SetProperty(ref _selectedUser, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedUser));
            SyncUserRoleSelection();
            SyncUserOrganizationSelection();
        }
    }

    public ManagedRole? SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (ReferenceEquals(_selectedRole, value))
            {
                return;
            }

            if (!_isRoleSelectionInternalChange && !CanChangeSelectedRole())
            {
                return;
            }

            if (_selectedRole is not null)
            {
                _selectedRole.PropertyChanged -= SelectedRoleOnPropertyChanged;
            }

            if (!SetProperty(ref _selectedRole, value))
            {
                if (_selectedRole is not null)
                {
                    _selectedRole.PropertyChanged += SelectedRoleOnPropertyChanged;
                }

                return;
            }

            if (_selectedRole is not null)
            {
                _selectedRole.PropertyChanged += SelectedRoleOnPropertyChanged;
            }

            OnPropertyChanged(nameof(HasSelectedRole));
            OnPropertyChanged(nameof(CanCloneSelectedRole));
            OnPropertyChanged(nameof(IsSelectedRoleProtected));
            SyncRoleAccessSelection();
            BuildRolePermissionTree();
            CaptureRoleEditorSnapshot();
            UpdateRolePermissionCounts();
            SelectedRoleAssignedUserCount = _selectedRole?.AssignedUserCount ?? 0;
            OnPropertyChanged(nameof(RoleEditorDirtyMessage));
        }
    }

    public long? SelectedUserRoleId
    {
        get => _selectedUserRoleId;
        set
        {
            if (!SetProperty(ref _selectedUserRoleId, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSelectedUserSuperRole));
            SyncUserOrganizationSelection();
        }
    }

    public long? SelectedUserDefaultCompanyId
    {
        get => _selectedUserDefaultCompanyId;
        set
        {
            if (!SetProperty(ref _selectedUserDefaultCompanyId, value))
            {
                return;
            }

            if (_suppressDefaultSelectionSync)
            {
                return;
            }

            RefreshDefaultLocationOptions();
        }
    }

    public long? SelectedUserDefaultLocationId
    {
        get => _selectedUserDefaultLocationId;
        set => SetProperty(ref _selectedUserDefaultLocationId, value);
    }

    public ManagedCompany? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (!SetProperty(ref _selectedCompany, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedCompany));
            RefreshFilteredLocations();
        }
    }

    public ManagedLocation? SelectedLocation
    {
        get => _selectedLocation;
        set
        {
            if (!SetProperty(ref _selectedLocation, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedLocation));
        }
    }

    public long? SelectedInventoryMasterCompanyId
    {
        get => _selectedInventoryMasterCompanyId;
        set => SetProperty(ref _selectedInventoryMasterCompanyId, value);
    }

    public long? SelectedInventoryCostingCompanyId
    {
        get => _selectedInventoryCostingCompanyId;
        set
        {
            if (!SetProperty(ref _selectedInventoryCostingCompanyId, value))
            {
                return;
            }

            if (_suppressInventoryCostingAutoLoad)
            {
                return;
            }

            if (value.HasValue && value.Value > 0)
            {
                _ = LoadInventoryCostingSettingsForSelectedCompanyAsync(value.Value);
                return;
            }

            ReplaceCollection(InventoryCostingLocationOptions, Array.Empty<ManagedLocation>());
            ReplaceCollection(InventoryCostingAccountOptions, Array.Empty<ManagedAccount>());
            _inventoryCompanyDefaultValuationMethod = "AVERAGE";
            _inventoryCompanyDefaultCogsAccountCode = string.Empty;
            SelectedInventoryCostingLocationId = null;
            UseInventoryLocationOverride = false;
            InventoryValuationMethod = "AVERAGE";
            InventoryCogsAccountCode = string.Empty;
            OnPropertyChanged(nameof(CanEditInventoryCostingLocation));
            OnPropertyChanged(nameof(CanRecalculateInventoryCostingCompany));
            OnPropertyChanged(nameof(CanRecalculateInventoryCostingLocation));
        }
    }

    public long? SelectedInventoryCostingLocationId
    {
        get => _selectedInventoryCostingLocationId;
        set
        {
            if (!SetProperty(ref _selectedInventoryCostingLocationId, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanEditInventoryCostingLocation));
            OnPropertyChanged(nameof(CanRecalculateInventoryCostingLocation));
            OnPropertyChanged(nameof(IsInventoryLocationOverrideEditorEnabled));

            if (_suppressInventoryCostingAutoLoad)
            {
                return;
            }

            if (!SelectedInventoryCostingCompanyId.HasValue || SelectedInventoryCostingCompanyId.Value <= 0)
            {
                return;
            }

            _ = LoadInventoryCostingSettingsForSelectedLocationAsync(
                SelectedInventoryCostingCompanyId.Value,
                value);
        }
    }

    public bool UseInventoryLocationOverride
    {
        get => _useInventoryLocationOverride;
        set
        {
            if (!SetProperty(ref _useInventoryLocationOverride, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsInventoryLocationOverrideEditorEnabled));
            if (!value)
            {
                ApplyCompanyCostingDefaults();
            }
        }
    }

    public bool CanEditInventoryCostingLocation =>
        SelectedInventoryCostingCompanyId.HasValue &&
        SelectedInventoryCostingCompanyId.Value > 0 &&
        SelectedInventoryCostingLocationId.HasValue &&
        SelectedInventoryCostingLocationId.Value > 0;

    public bool IsInventoryLocationOverrideEditorEnabled => CanEditInventoryCostingLocation && UseInventoryLocationOverride;

    public bool CanRecalculateInventoryCostingCompany =>
        SelectedInventoryCostingCompanyId.HasValue && SelectedInventoryCostingCompanyId.Value > 0;

    public bool CanRecalculateInventoryCostingLocation => CanEditInventoryCostingLocation;

    public string InventoryValuationMethod
    {
        get => _inventoryValuationMethod;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? "AVERAGE"
                : value.Trim().ToUpperInvariant();
            SetProperty(ref _inventoryValuationMethod, normalized);
        }
    }

    public string InventoryCogsAccountCode
    {
        get => _inventoryCogsAccountCode;
        set => SetProperty(ref _inventoryCogsAccountCode, (value ?? string.Empty).Trim().ToUpperInvariant());
    }

    public string CentralSyncBaseUrl
    {
        get => _centralSyncBaseUrl;
        set => SetProperty(ref _centralSyncBaseUrl, value);
    }

    public string CentralSyncApiKey
    {
        get => _centralSyncApiKey;
        set => SetProperty(ref _centralSyncApiKey, value);
    }

    public string CentralSyncUploadPath
    {
        get => _centralSyncUploadPath;
        set => SetProperty(ref _centralSyncUploadPath, value);
    }

    public string CentralSyncDownloadPath
    {
        get => _centralSyncDownloadPath;
        set => SetProperty(ref _centralSyncDownloadPath, value);
    }

    public int CentralSyncTimeoutSeconds
    {
        get => _centralSyncTimeoutSeconds;
        set => SetProperty(ref _centralSyncTimeoutSeconds, value > 0 ? value : 1);
    }

    public bool HasSelectedUser => SelectedUser is not null;

    public bool HasSelectedRole => SelectedRole is not null;

    public bool CanCloneSelectedRole => SelectedRole is not null && SelectedRole.Id > 0;

    public bool HasSelectedCompany => SelectedCompany is not null;

    public bool HasSelectedLocation => SelectedLocation is not null;

    public bool IsSelectedUserSuperRole =>
        SelectedUserRoleId.HasValue &&
        Roles.Any(x => x.Id == SelectedUserRoleId.Value && x.IsSuperRole);

    public bool CanManageMasterCompanySetting => _canManageMasterCompanySetting;

    public string MasterCompanyPermissionMessage => CanManageMasterCompanySetting
        ? "Perubahan master company berdampak ke seluruh company inventory."
        : "Role Anda tidak memiliki permission untuk mengubah master company inventory.";

    public bool IsSelectedRoleProtected =>
        SelectedRole is not null &&
        (SelectedRole.IsSuperRole || string.Equals(SelectedRole.Code, "SUPER_ADMIN", StringComparison.OrdinalIgnoreCase));

    public string SelectedSettingsSubTab
    {
        get => _selectedSettingsSubTab;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "companies" : value.Trim().ToLowerInvariant();
            if (!SetProperty(ref _selectedSettingsSubTab, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSettingsCompaniesTabSelected));
            OnPropertyChanged(nameof(IsSettingsApiInvTabSelected));
            OnPropertyChanged(nameof(IsSettingsPeriodsTabSelected));
            OnPropertyChanged(nameof(IsSettingsPeriodAuditTabSelected));
            OnPropertyChanged(nameof(IsSettingsDefaultAkunTabSelected));
            OnPropertyChanged(nameof(IsSettingsPenomoranJurnalTabSelected));
            OnPropertyChanged(nameof(IsSettingsMataUangKursTabSelected));
            OnPropertyChanged(nameof(IsSettingsTahunFiskalTabSelected));
            OnPropertyChanged(nameof(IsAnyAccountingConfigTabSelected));
            OnPropertyChanged(nameof(AccountingSettingsPlaceholderTitle));
            OnPropertyChanged(nameof(AccountingSettingsPlaceholderDescription));
        }
    }

    public bool IsSettingsCompaniesTabSelected =>
        string.Equals(SelectedSettingsSubTab, "companies", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsApiInvTabSelected =>
        string.Equals(SelectedSettingsSubTab, "api_inv", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsPeriodsTabSelected =>
        string.Equals(SelectedSettingsSubTab, "periods", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsPeriodAuditTabSelected =>
        string.Equals(SelectedSettingsSubTab, "period_audit", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsDefaultAkunTabSelected =>
        string.Equals(SelectedSettingsSubTab, "default_akun", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsPenomoranJurnalTabSelected =>
        string.Equals(SelectedSettingsSubTab, "penomoran_jurnal", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsMataUangKursTabSelected =>
        string.Equals(SelectedSettingsSubTab, "mata_uang_kurs", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsTahunFiskalTabSelected =>
        string.Equals(SelectedSettingsSubTab, "tahun_fiskal", StringComparison.OrdinalIgnoreCase);

    public bool IsAnyAccountingConfigTabSelected =>
        IsSettingsDefaultAkunTabSelected ||
        IsSettingsPenomoranJurnalTabSelected ||
        IsSettingsMataUangKursTabSelected ||
        IsSettingsTahunFiskalTabSelected;

    public string AccountingSettingsPlaceholderTitle => SelectedSettingsSubTab switch
    {
        "default_akun" => "Default Akun",
        "penomoran_jurnal" => "Penomoran Jurnal",
        "mata_uang_kurs" => "Mata Uang & Kurs",
        "tahun_fiskal" => "Tahun Fiskal",
        _ => "Pengaturan Accounting"
    };

    public string AccountingSettingsPlaceholderDescription => SelectedSettingsSubTab switch
    {
        "default_akun" => "Menu ini sudah tersedia pada struktur baru. Konfigurasi default akun akan ditambahkan pada iterasi berikutnya.",
        "penomoran_jurnal" => "Menu ini sudah tersedia pada struktur baru. Konfigurasi pola penomoran jurnal akan ditambahkan pada iterasi berikutnya.",
        "mata_uang_kurs" => "Menu ini sudah tersedia pada struktur baru. Manajemen mata uang dan kurs akan ditambahkan pada iterasi berikutnya.",
        "tahun_fiskal" => "Menu ini sudah tersedia pada struktur baru. Konfigurasi tahun fiskal akan ditambahkan pada iterasi berikutnya.",
        _ => "Fitur ini belum tersedia."
    };

    public string NewUserPassword
    {
        get => _newUserPassword;
        set => SetProperty(ref _newUserPassword, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded)
        {
            return;
        }

        await LoadDataAsync();
    }

    public bool TryLeaveRoleEditor()
    {
        return CanChangeSelectedRole();
    }

    private async Task LoadDataAsync(
        long? selectedUserId = null,
        long? selectedRoleId = null,
        long? selectedCompanyId = null,
        long? selectedLocationId = null,
        bool forceReload = false)
    {
        if (IsBusy && !forceReload)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Memuat data manajemen pengguna...";

            var data = await _accessControlService.GetUserManagementDataAsync();

            _userRoleMap.Clear();
            _roleScopeMap.Clear();
            _userCompanyMap.Clear();
            _userLocationMap.Clear();

            CopyMap(data.UserRoleIdsByUserId, _userRoleMap);
            CopyMap(data.RoleScopeIdsByRoleId, _roleScopeMap);
            CopyMap(data.UserCompanyIdsByUserId, _userCompanyMap);
            CopyMap(data.UserLocationIdsByUserId, _userLocationMap);
            PopulateUserGridColumns(data.Users, data.Roles, data.AccessScopes);

            ReplaceCollection(Users, data.Users.OrderBy(x => x.Username));
            ReplaceCollection(Roles, data.Roles.OrderBy(x => x.Code));
            ReplaceCollection(AccessScopes, data.AccessScopes);
            foreach (var role in Roles.Where(x => x.IsSuperRole))
            {
                role.PermissionCount = AccessScopes.Count;
            }
            ReplaceCollection(Companies, data.Companies.OrderBy(x => x.Code));
            ReplaceCollection(ActiveCompanyOptions, data.Companies.Where(x => x.IsActive).OrderBy(x => x.Code));
            ReplaceCollection(Locations, data.Locations.OrderBy(x => x.CompanyCode).ThenBy(x => x.Code));

            ReplaceCollection(UserRoleOptions, Roles.Select(r => new SelectableOption
            {
                Id = r.Id,
                Label = $"{r.Code} - {r.Name}",
                IsSelected = false
            }));

            foreach (var option in RoleScopeOptions)
            {
                option.PropertyChanged -= RoleScopeOptionOnPropertyChanged;
            }

            ReplaceCollection(RoleScopeOptions, AccessScopes.Select(m => new SelectableOption
            {
                Id = m.Id,
                Label = m.Name,
                ModuleCode = m.ModuleCode,
                ModuleName = m.ModuleName,
                SubmoduleCode = m.SubmoduleCode,
                SubmoduleName = m.SubmoduleName,
                ActionCode = m.ActionCode,
                IsSelected = false
            }));

            foreach (var option in RoleScopeOptions)
            {
                option.PropertyChanged += RoleScopeOptionOnPropertyChanged;
            }

            BuildRolePermissionTree();

            foreach (var option in UserCompanyOptions)
            {
                option.PropertyChanged -= UserCompanyOptionOnPropertyChanged;
            }

            ReplaceCollection(UserCompanyOptions, Companies.Where(x => x.IsActive).Select(c => new SelectableOption
            {
                Id = c.Id,
                Label = $"{c.Code} - {c.Name}",
                IsSelected = false
            }));

            foreach (var option in UserCompanyOptions)
            {
                option.PropertyChanged += UserCompanyOptionOnPropertyChanged;
            }

            foreach (var option in UserLocationOptions)
            {
                option.PropertyChanged -= UserLocationOptionOnPropertyChanged;
            }

            var activeCompanyIds = Companies.Where(x => x.IsActive).Select(x => x.Id).ToHashSet();
            ReplaceCollection(UserLocationOptions, Locations
                .Where(x => x.IsActive && activeCompanyIds.Contains(x.CompanyId))
                .Select(l => new SelectableOption
                {
                    Id = l.Id,
                    GroupId = l.CompanyId,
                    Label = $"{l.CompanyCode} • {l.Code} - {l.Name} ({l.LocationType})",
                    IsSelected = false,
                    IsEnabled = true
                }));

            foreach (var option in UserLocationOptions)
            {
                option.PropertyChanged += UserLocationOptionOnPropertyChanged;
            }

            ReplaceCollection(UserDefaultCompanyOptions, Companies.Where(x => x.IsActive).OrderBy(x => x.Code));
            ReplaceCollection(UserDefaultLocationOptions, Enumerable.Empty<ManagedLocation>());

            SelectedInventoryMasterCompanyId = await _accessControlService.GetInventoryMasterCompanyIdAsync();
            var centralSyncSettings = await _accessControlService.GetInventoryCentralSyncSettingsAsync();
            CentralSyncBaseUrl = centralSyncSettings.BaseUrl;
            CentralSyncApiKey = centralSyncSettings.ApiKey;
            CentralSyncUploadPath = centralSyncSettings.UploadPath;
            CentralSyncDownloadPath = centralSyncSettings.DownloadPath;
            CentralSyncTimeoutSeconds = centralSyncSettings.TimeoutSeconds;

            var preferredCostingCompanyId = SelectedInventoryCostingCompanyId;
            var preferredCostingLocationId = SelectedInventoryCostingLocationId;
            var activeCompanySet = ActiveCompanyOptions.Select(x => x.Id).ToHashSet();
            if (!preferredCostingCompanyId.HasValue || !activeCompanySet.Contains(preferredCostingCompanyId.Value))
            {
                preferredCostingCompanyId = SelectedInventoryMasterCompanyId.HasValue &&
                                            activeCompanySet.Contains(SelectedInventoryMasterCompanyId.Value)
                    ? SelectedInventoryMasterCompanyId.Value
                    : ActiveCompanyOptions.FirstOrDefault()?.Id;
            }

            _suppressInventoryCostingAutoLoad = true;
            SelectedInventoryCostingCompanyId = preferredCostingCompanyId;
            _suppressInventoryCostingAutoLoad = false;
            if (preferredCostingCompanyId.HasValue && preferredCostingCompanyId.Value > 0)
            {
                await LoadInventoryCostingSettingsForSelectedCompanyAsync(
                    preferredCostingCompanyId.Value,
                    preferredCostingLocationId);
            }
            else
            {
                ReplaceCollection(InventoryCostingLocationOptions, Array.Empty<ManagedLocation>());
                ReplaceCollection(InventoryCostingAccountOptions, Array.Empty<ManagedAccount>());
                _inventoryCompanyDefaultValuationMethod = "AVERAGE";
                _inventoryCompanyDefaultCogsAccountCode = string.Empty;
                SelectedInventoryCostingLocationId = null;
                UseInventoryLocationOverride = false;
                InventoryValuationMethod = "AVERAGE";
                InventoryCogsAccountCode = string.Empty;
            }

            SelectedUser = selectedUserId.HasValue
                ? Users.FirstOrDefault(x => x.Id == selectedUserId.Value) ?? Users.FirstOrDefault()
                : Users.FirstOrDefault();

            _isRoleSelectionInternalChange = true;
            SelectedRole = selectedRoleId.HasValue
                ? Roles.FirstOrDefault(x => x.Id == selectedRoleId.Value) ?? Roles.FirstOrDefault()
                : Roles.FirstOrDefault();
            _isRoleSelectionInternalChange = false;

            SelectedCompany = selectedCompanyId.HasValue
                ? Companies.FirstOrDefault(x => x.Id == selectedCompanyId.Value) ?? Companies.FirstOrDefault()
                : null;

            RefreshFilteredLocations(selectedLocationId);
            FilterRoles();
            UpdateRolePermissionCounts();
            IsRoleEditorDirty = false;
            OnPropertyChanged(nameof(RoleEditorDirtyMessage));

            _isLoaded = true;
            StatusMessage = "Data manajemen pengguna berhasil dimuat.";

            OnPropertyChanged(nameof(TotalRolesCount));
            OnPropertyChanged(nameof(ActiveRolesCount));
            OnPropertyChanged(nameof(TotalScopesCount));
            OnPropertyChanged(nameof(TotalUsersCount));
        }
        catch (Exception)
        {
            StatusMessage = "Gagal memuat data manajemen pengguna.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewUser()
    {
        SelectedUser = new ManagedUser
        {
            Id = 0,
            Username = string.Empty,
            FullName = string.Empty,
            Email = string.Empty,
            IsActive = true,
            RoleDisplay = "-",
            ModuleDisplay = "-",
            DefaultCompanyId = null,
            DefaultLocationId = null
        };

        SelectedUserRoleId = UserRoleOptions.FirstOrDefault()?.Id;
        SelectedUserDefaultCompanyId = null;
        SelectedUserDefaultLocationId = null;

        NewUserPassword = string.Empty;
        StatusMessage = "Membuat pengguna baru.";
    }

    private async Task SaveUserAsync()
    {
        if (SelectedUser is null)
        {
            StatusMessage = "Pilih atau buat pengguna terlebih dahulu.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            if (!SelectedUserRoleId.HasValue || SelectedUserRoleId.Value <= 0)
            {
                StatusMessage = "Pilih satu role untuk pengguna.";
                return;
            }

            var selectedRoleIds = new[] { SelectedUserRoleId.Value };
            var selectedCompanyIds = UserCompanyOptions
                .Where(x => x.IsSelected)
                .Select(x => x.Id)
                .ToArray();
            var selectedLocationIds = UserLocationOptions
                .Where(x => x.IsSelected && x.IsEnabled)
                .Select(x => x.Id)
                .ToArray();

            if (!IsSelectedUserSuperRole)
            {
                if (!SelectedUserDefaultCompanyId.HasValue || SelectedUserDefaultCompanyId.Value <= 0)
                {
                    StatusMessage = "Pilih company default untuk pengguna ini.";
                    return;
                }

                if (!SelectedUserDefaultLocationId.HasValue || SelectedUserDefaultLocationId.Value <= 0)
                {
                    StatusMessage = "Pilih lokasi default untuk pengguna ini.";
                    return;
                }
            }

            SelectedUser.DefaultCompanyId = IsSelectedUserSuperRole ? null : SelectedUserDefaultCompanyId;
            SelectedUser.DefaultLocationId = IsSelectedUserSuperRole ? null : SelectedUserDefaultLocationId;

            var result = await _accessControlService.SaveUserAsync(
                SelectedUser,
                string.IsNullOrWhiteSpace(NewUserPassword) ? null : NewUserPassword,
                selectedRoleIds,
                selectedCompanyIds,
                selectedLocationIds,
                _actorUsername);

            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                NewUserPassword = string.Empty;
                await LoadDataAsync(result.EntityId, SelectedRole?.Id, SelectedCompany?.Id, SelectedLocation?.Id, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewRole()
    {
        if (!CanChangeSelectedRole())
        {
            return;
        }

        SelectedRole = new ManagedRole
        {
            Id = 0,
            Code = string.Empty,
            Name = string.Empty,
            IsSuperRole = false,
            IsActive = true
        };

        foreach (var option in RoleScopeOptions)
        {
            option.IsSelected = false;
        }

        CaptureRoleEditorSnapshot();
        IsRoleEditorDirty = false;
        StatusMessage = "Membuat role baru.";
    }

    private async Task CloneRoleAsync()
    {
        if (SelectedRole is null || SelectedRole.Id <= 0)
        {
            StatusMessage = "Pilih role yang ingin diduplikasi.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var cloneCode = BuildUniqueRoleCloneCode(SelectedRole.Code);
            var cloneName = BuildUniqueRoleCloneName(SelectedRole.Name);
            var result = await _accessControlService.CloneRoleAsync(
                SelectedRole.Id,
                cloneCode,
                cloneName,
                _actorUsername);

            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(SelectedUser?.Id, result.EntityId, SelectedCompany?.Id, SelectedLocation?.Id, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string BuildUniqueRoleCloneCode(string sourceCode)
    {
        var baseCode = string.IsNullOrWhiteSpace(sourceCode)
            ? "ROLE_SALINAN"
            : $"{sourceCode.Trim().ToUpperInvariant()}_SALINAN";

        var existingCodes = Roles
            .Select(x => x.Code)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingCodes.Contains(baseCode))
        {
            return baseCode;
        }

        var index = 2;
        while (existingCodes.Contains($"{baseCode}_{index}"))
        {
            index++;
        }

        return $"{baseCode}_{index}";
    }

    private string BuildUniqueRoleCloneName(string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName)
            ? "Role Salinan"
            : $"{sourceName.Trim()} (Salinan)";

        var existingNames = Roles
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        var index = 2;
        while (existingNames.Contains($"{baseName} {index}"))
        {
            index++;
        }

        return $"{baseName} {index}";
    }

    private async Task SaveRoleAsync()
    {
        if (SelectedRole is null)
        {
            StatusMessage = "Pilih atau buat role terlebih dahulu.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var selectedScopeIds = RoleScopeOptions.Where(x => x.IsSelected).Select(x => x.Id).ToArray();

            var result = await _accessControlService.SaveRoleAsync(
                SelectedRole,
                selectedScopeIds,
                _actorUsername);

            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(SelectedUser?.Id, result.EntityId, SelectedCompany?.Id, SelectedLocation?.Id, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteRoleAsync()
    {
        if (SelectedRole is null)
        {
            StatusMessage = "Pilih role terlebih dahulu.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var roleId = SelectedRole.Id;
            var result = await _accessControlService.DeleteRoleAsync(roleId, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(SelectedUser?.Id, null, SelectedCompany?.Id, SelectedLocation?.Id, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewCompany()
    {
        SelectedCompany = new ManagedCompany
        {
            Id = 0,
            Code = string.Empty,
            Name = string.Empty,
            IsActive = true
        };

        StatusMessage = "Creating new company.";
    }

    private async Task SaveCompanyAsync()
    {
        if (SelectedCompany is null)
        {
            StatusMessage = "Select or create a company first.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var result = await _accessControlService.SaveCompanyAsync(SelectedCompany, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(SelectedUser?.Id, SelectedRole?.Id, result.EntityId, SelectedLocation?.Id, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteCompanyAsync()
    {
        if (SelectedCompany is null)
        {
            StatusMessage = "Select a company first.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var companyId = SelectedCompany.Id;
            var result = await _accessControlService.SoftDeleteCompanyAsync(companyId, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(SelectedUser?.Id, SelectedRole?.Id, companyId, SelectedLocation?.Id, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewLocation()
    {
        var defaultCompanyId = SelectedCompany?.Id ?? ActiveCompanyOptions.FirstOrDefault()?.Id ?? 0;
        var defaultCompany = Companies.FirstOrDefault(x => x.Id == defaultCompanyId);
        if (defaultCompany is not null)
        {
            SelectedCompany = defaultCompany;
        }

        SelectedLocation = new ManagedLocation
        {
            Id = 0,
            CompanyId = defaultCompanyId,
            CompanyCode = defaultCompany?.Code ?? string.Empty,
            CompanyName = defaultCompany?.Name ?? string.Empty,
            Code = string.Empty,
            Name = string.Empty,
            LocationType = "OFFICE",
            IsActive = true
        };

        StatusMessage = "Creating new location.";
    }

    private async Task SaveLocationAsync()
    {
        if (SelectedLocation is null)
        {
            StatusMessage = "Select or create a location first.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var selectedCompany = Companies.FirstOrDefault(x => x.Id == SelectedLocation.CompanyId);
            if (selectedCompany is not null)
            {
                SelectedLocation.CompanyCode = selectedCompany.Code;
                SelectedLocation.CompanyName = selectedCompany.Name;
            }

            var result = await _accessControlService.SaveLocationAsync(SelectedLocation, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(SelectedUser?.Id, SelectedRole?.Id, SelectedLocation.CompanyId, result.EntityId, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteLocationAsync()
    {
        if (SelectedLocation is null)
        {
            StatusMessage = "Select a location first.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var locationId = SelectedLocation.Id;
            var result = await _accessControlService.SoftDeleteLocationAsync(locationId, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(SelectedUser?.Id, SelectedRole?.Id, SelectedCompany?.Id, locationId, forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveInventoryMasterCompanyAsync()
    {
        if (!CanManageMasterCompanySetting)
        {
            StatusMessage = "Hanya SUPER_ADMIN yang dapat mengubah master company inventory.";
            return;
        }

        if (!SelectedInventoryMasterCompanyId.HasValue || SelectedInventoryMasterCompanyId.Value <= 0)
        {
            StatusMessage = "Pilih master company inventory terlebih dahulu.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _accessControlService.SetInventoryMasterCompanyIdAsync(
                SelectedInventoryMasterCompanyId.Value,
                _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(
                    SelectedUser?.Id,
                    SelectedRole?.Id,
                    SelectedCompany?.Id,
                    SelectedLocation?.Id,
                    forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadInventoryCostingSettingsForSelectedCompanyAsync(long companyId, long? preferredLocationId = null)
    {
        if (companyId <= 0)
        {
            ReplaceCollection(InventoryCostingLocationOptions, Array.Empty<ManagedLocation>());
            ReplaceCollection(InventoryCostingAccountOptions, Array.Empty<ManagedAccount>());
            _inventoryCompanyDefaultValuationMethod = "AVERAGE";
            _inventoryCompanyDefaultCogsAccountCode = string.Empty;
            SelectedInventoryCostingLocationId = null;
            UseInventoryLocationOverride = false;
            InventoryValuationMethod = "AVERAGE";
            InventoryCogsAccountCode = string.Empty;
            return;
        }

        try
        {
            var settings = await _accessControlService.GetInventoryCostingSettingsAsync(companyId);
            var accounts = await _accessControlService.GetAccountsAsync(companyId, includeInactive: false);

            ReplaceCollection(
                InventoryCostingAccountOptions,
                accounts
                    .Where(x => x.IsActive && x.IsPosting)
                    .OrderBy(x => x.Code));

            ReplaceCollection(
                InventoryCostingLocationOptions,
                Locations
                    .Where(x => x.IsActive && x.CompanyId == companyId)
                    .OrderBy(x => x.Code));

            _inventoryCompanyDefaultValuationMethod = settings.ValuationMethod;
            _inventoryCompanyDefaultCogsAccountCode = settings.CogsAccountCode;

            long? selectedLocationId = null;
            if (preferredLocationId.HasValue &&
                InventoryCostingLocationOptions.Any(x => x.Id == preferredLocationId.Value))
            {
                selectedLocationId = preferredLocationId.Value;
            }

            _suppressInventoryCostingAutoLoad = true;
            SelectedInventoryCostingLocationId = selectedLocationId;
            _suppressInventoryCostingAutoLoad = false;

            await LoadInventoryCostingSettingsForSelectedLocationAsync(companyId, selectedLocationId);
            OnPropertyChanged(nameof(CanRecalculateInventoryCostingCompany));
            OnPropertyChanged(nameof(CanEditInventoryCostingLocation));
            OnPropertyChanged(nameof(CanRecalculateInventoryCostingLocation));
            OnPropertyChanged(nameof(IsInventoryLocationOverrideEditorEnabled));
        }
        catch
        {
            ReplaceCollection(InventoryCostingLocationOptions, Array.Empty<ManagedLocation>());
            ReplaceCollection(InventoryCostingAccountOptions, Array.Empty<ManagedAccount>());
            _inventoryCompanyDefaultValuationMethod = "AVERAGE";
            _inventoryCompanyDefaultCogsAccountCode = string.Empty;
            SelectedInventoryCostingLocationId = null;
            UseInventoryLocationOverride = false;
            InventoryValuationMethod = "AVERAGE";
            InventoryCogsAccountCode = string.Empty;
        }
    }

    private async Task LoadInventoryCostingSettingsForSelectedLocationAsync(long companyId, long? locationId)
    {
        if (companyId <= 0 || !locationId.HasValue || locationId.Value <= 0)
        {
            UseInventoryLocationOverride = false;
            ApplyCompanyCostingDefaults();
            return;
        }

        try
        {
            var settings = await _accessControlService.GetInventoryLocationCostingSettingsAsync(companyId, locationId.Value);
            UseInventoryLocationOverride = !settings.UseCompanyDefault;
            if (settings.UseCompanyDefault)
            {
                ApplyCompanyCostingDefaults();
                return;
            }

            InventoryValuationMethod = settings.ValuationMethod;
            InventoryCogsAccountCode = settings.CogsAccountCode;
        }
        catch
        {
            UseInventoryLocationOverride = false;
            ApplyCompanyCostingDefaults();
        }
    }

    private async Task SaveInventoryCostingSettingsAsync()
    {
        if (!SelectedInventoryCostingCompanyId.HasValue || SelectedInventoryCostingCompanyId.Value <= 0)
        {
            StatusMessage = "Pilih company untuk pengaturan costing inventory.";
            return;
        }

        if (UseInventoryLocationOverride)
        {
            StatusMessage = "Mode override lokasi aktif. Nonaktifkan override lokasi untuk menyimpan default company.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var companyId = SelectedInventoryCostingCompanyId.Value;
            var result = await _accessControlService.SaveInventoryCostingSettingsAsync(
                companyId,
                new InventoryCostingSettings
                {
                    CompanyId = companyId,
                    ValuationMethod = InventoryValuationMethod,
                    CogsAccountCode = InventoryCogsAccountCode
                },
                _actorUsername);

            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadInventoryCostingSettingsForSelectedCompanyAsync(
                    companyId,
                    SelectedInventoryCostingLocationId);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveInventoryLocationCostingSettingsAsync()
    {
        if (!SelectedInventoryCostingCompanyId.HasValue || SelectedInventoryCostingCompanyId.Value <= 0)
        {
            StatusMessage = "Pilih company untuk pengaturan costing lokasi.";
            return;
        }

        if (!SelectedInventoryCostingLocationId.HasValue || SelectedInventoryCostingLocationId.Value <= 0)
        {
            StatusMessage = "Pilih lokasi untuk pengaturan override.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var companyId = SelectedInventoryCostingCompanyId.Value;
            var locationId = SelectedInventoryCostingLocationId.Value;
            var result = await _accessControlService.SaveInventoryLocationCostingSettingsAsync(
                companyId,
                new InventoryLocationCostingSettings
                {
                    CompanyId = companyId,
                    LocationId = locationId,
                    UseCompanyDefault = !UseInventoryLocationOverride,
                    ValuationMethod = InventoryValuationMethod,
                    CogsAccountCode = InventoryCogsAccountCode
                },
                _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadInventoryCostingSettingsForSelectedCompanyAsync(companyId, locationId);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RecalculateInventoryCostingCompanyAsync()
    {
        if (!SelectedInventoryCostingCompanyId.HasValue || SelectedInventoryCostingCompanyId.Value <= 0)
        {
            StatusMessage = "Pilih company untuk recalculation costing.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var companyId = SelectedInventoryCostingCompanyId.Value;
            var result = await _accessControlService.RecalculateInventoryCostingAsync(
                companyId,
                locationId: null,
                _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadInventoryCostingSettingsForSelectedCompanyAsync(companyId, SelectedInventoryCostingLocationId);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RecalculateInventoryCostingLocationAsync()
    {
        if (!SelectedInventoryCostingCompanyId.HasValue || SelectedInventoryCostingCompanyId.Value <= 0)
        {
            StatusMessage = "Pilih company untuk recalculation costing.";
            return;
        }

        if (!SelectedInventoryCostingLocationId.HasValue || SelectedInventoryCostingLocationId.Value <= 0)
        {
            StatusMessage = "Pilih lokasi untuk recalculation costing.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var companyId = SelectedInventoryCostingCompanyId.Value;
            var locationId = SelectedInventoryCostingLocationId.Value;
            var result = await _accessControlService.RecalculateInventoryCostingAsync(
                companyId,
                locationId,
                _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadInventoryCostingSettingsForSelectedCompanyAsync(companyId, locationId);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyCompanyCostingDefaults()
    {
        InventoryValuationMethod = _inventoryCompanyDefaultValuationMethod;
        InventoryCogsAccountCode = _inventoryCompanyDefaultCogsAccountCode;
    }

    private async Task SaveInventoryCentralSyncSettingsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;

            var result = await _accessControlService.SaveInventoryCentralSyncSettingsAsync(
                new InventoryCentralSyncSettings
                {
                    BaseUrl = CentralSyncBaseUrl,
                    ApiKey = CentralSyncApiKey,
                    UploadPath = CentralSyncUploadPath,
                    DownloadPath = CentralSyncDownloadPath,
                    TimeoutSeconds = CentralSyncTimeoutSeconds
                },
                _actorUsername);

            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
                await LoadDataAsync(
                    SelectedUser?.Id,
                    SelectedRole?.Id,
                    SelectedCompany?.Id,
                    SelectedLocation?.Id,
                    forceReload: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SyncUserRoleSelection()
    {
        SelectedUserRoleId = null;

        if (SelectedUser is null)
        {
            return;
        }

        if (!_userRoleMap.TryGetValue(SelectedUser.Id, out var roleIds))
        {
            return;
        }

        var preferredRoleId = UserRoleOptions
            .Select(x => x.Id)
            .FirstOrDefault(roleIds.Contains);

        if (preferredRoleId > 0)
        {
            SelectedUserRoleId = preferredRoleId;
            return;
        }

        var fallbackRoleId = roleIds.FirstOrDefault();
        SelectedUserRoleId = fallbackRoleId > 0 ? fallbackRoleId : null;
    }

    private void SyncRoleAccessSelection()
    {
        _suppressRoleEditorDirtyTracking = true;
        foreach (var option in RoleScopeOptions)
        {
            option.IsSelected = false;
        }

        if (SelectedRole is null)
        {
            _suppressRoleEditorDirtyTracking = false;
            UpdateRolePermissionCounts();
            return;
        }

        if (SelectedRole.IsSuperRole)
        {
            foreach (var option in RoleScopeOptions)
            {
                option.IsSelected = true;
            }
        }
        else if (_roleScopeMap.TryGetValue(SelectedRole.Id, out var scopeIds))
        {
            foreach (var option in RoleScopeOptions)
            {
                option.IsSelected = scopeIds.Contains(option.Id);
            }
        }

        _suppressRoleEditorDirtyTracking = false;
        UpdateRolePermissionCounts();
    }

    private bool CanChangeSelectedRole()
    {
        if (!IsRoleEditorDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            "Ada perubahan role yang belum disimpan. Batalkan perubahan tersebut dan lanjutkan?",
            "Perubahan Belum Disimpan",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return false;
        }

        RestoreRoleEditorFromSnapshot();
        return true;
    }

    private void RestoreRoleEditorFromSnapshot()
    {
        if (_roleEditorSnapshot is null || SelectedRole is null)
        {
            IsRoleEditorDirty = false;
            OnPropertyChanged(nameof(RoleEditorDirtyMessage));
            return;
        }

        _suppressRoleEditorDirtyTracking = true;
        SelectedRole.Code = _roleEditorSnapshot.Code;
        SelectedRole.Name = _roleEditorSnapshot.Name;
        SelectedRole.IsSuperRole = _roleEditorSnapshot.IsSuperRole;
        SelectedRole.IsActive = _roleEditorSnapshot.IsActive;

        foreach (var option in RoleScopeOptions)
        {
            option.IsSelected = _roleEditorSnapshot.SelectedScopeIds.Contains(option.Id);
        }

        _suppressRoleEditorDirtyTracking = false;
        IsRoleEditorDirty = false;
        BuildRolePermissionTree();
        UpdateRolePermissionCounts();
        OnPropertyChanged(nameof(RoleEditorDirtyMessage));
    }

    private void CaptureRoleEditorSnapshot()
    {
        if (SelectedRole is null)
        {
            _roleEditorSnapshot = null;
            IsRoleEditorDirty = false;
            OnPropertyChanged(nameof(RoleEditorDirtyMessage));
            return;
        }

        _roleEditorSnapshot = new RoleEditorSnapshot
        {
            RoleId = SelectedRole.Id,
            Code = (SelectedRole.Code ?? string.Empty).Trim(),
            Name = (SelectedRole.Name ?? string.Empty).Trim(),
            IsSuperRole = SelectedRole.IsSuperRole,
            IsActive = SelectedRole.IsActive,
            SelectedScopeIds = GetSelectedRoleScopeIds()
        };

        IsRoleEditorDirty = false;
        OnPropertyChanged(nameof(RoleEditorDirtyMessage));
    }

    private void EvaluateRoleEditorDirty()
    {
        if (_suppressRoleEditorDirtyTracking)
        {
            return;
        }

        if (SelectedRole is null || _roleEditorSnapshot is null || _roleEditorSnapshot.RoleId != SelectedRole.Id)
        {
            IsRoleEditorDirty = false;
            OnPropertyChanged(nameof(RoleEditorDirtyMessage));
            return;
        }

        var isDirty =
            !string.Equals((_roleEditorSnapshot.Code ?? string.Empty).Trim(), (SelectedRole.Code ?? string.Empty).Trim(), StringComparison.Ordinal) ||
            !string.Equals((_roleEditorSnapshot.Name ?? string.Empty).Trim(), (SelectedRole.Name ?? string.Empty).Trim(), StringComparison.Ordinal) ||
            _roleEditorSnapshot.IsSuperRole != SelectedRole.IsSuperRole ||
            _roleEditorSnapshot.IsActive != SelectedRole.IsActive ||
            !_roleEditorSnapshot.SelectedScopeIds.SetEquals(GetSelectedRoleScopeIds());

        IsRoleEditorDirty = isDirty;
        OnPropertyChanged(nameof(RoleEditorDirtyMessage));
    }

    private HashSet<long> GetSelectedRoleScopeIds()
    {
        return RoleScopeOptions
            .Where(x => x.IsSelected)
            .Select(x => x.Id)
            .ToHashSet();
    }

    private void UpdateRolePermissionCounts()
    {
        RefreshPermissionTreeSelectionCounts();
        SelectedRolePermissionCount = RoleScopeOptions.Count(x => x.IsSelected);
        VisibleRolePermissionCount = RolePermissionModules
            .SelectMany(x => x.Submodules)
            .SelectMany(x => x.Actions)
            .Select(x => x.Id)
            .Distinct()
            .Count();

        if (SelectedRole is not null)
        {
            SelectedRole.PermissionCount = SelectedRole.IsSuperRole
                ? TotalScopesCount
                : SelectedRolePermissionCount;
            SelectedRoleAssignedUserCount = SelectedRole.AssignedUserCount;
            return;
        }

        SelectedRoleAssignedUserCount = 0;
    }

    private void RefreshPermissionTreeSelectionCounts()
    {
        foreach (var module in RolePermissionModules)
        {
            module.NotifySelectionChanged();
        }
    }

    private void SelectedRoleOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManagedRole.IsSuperRole) && SelectedRole?.IsSuperRole == true)
        {
            _suppressRoleEditorDirtyTracking = true;
            SetAllOptions(RoleScopeOptions, selected: true);
            _suppressRoleEditorDirtyTracking = false;
        }

        UpdateRolePermissionCounts();
        EvaluateRoleEditorDirty();
    }

    private void RoleScopeOptionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableOption.IsSelected))
        {
            return;
        }

        if (ShowSelectedRolePermissionsOnly)
        {
            BuildRolePermissionTree();
        }
        else
        {
            UpdateRolePermissionCounts();
        }

        EvaluateRoleEditorDirty();
    }

    private void DiscardRoleChanges()
    {
        RestoreRoleEditorFromSnapshot();
        StatusMessage = "Perubahan role dibatalkan.";
    }

    private void SyncUserOrganizationSelection()
    {
        foreach (var option in UserCompanyOptions)
        {
            option.IsSelected = false;
        }

        foreach (var option in UserLocationOptions)
        {
            option.IsSelected = false;
            option.IsEnabled = true;
        }

        if (SelectedUser is null)
        {
            _suppressDefaultSelectionSync = true;
            SelectedUserDefaultCompanyId = null;
            SelectedUserDefaultLocationId = null;
            _suppressDefaultSelectionSync = false;
            ReplaceCollection(UserDefaultCompanyOptions, Enumerable.Empty<ManagedCompany>());
            ReplaceCollection(UserDefaultLocationOptions, Enumerable.Empty<ManagedLocation>());
            return;
        }

        if (_userCompanyMap.TryGetValue(SelectedUser.Id, out var companyIds))
        {
            foreach (var option in UserCompanyOptions)
            {
                option.IsSelected = companyIds.Contains(option.Id);
            }
        }

        ApplyUserLocationFilter();

        if (_userLocationMap.TryGetValue(SelectedUser.Id, out var locationIds))
        {
            foreach (var option in UserLocationOptions)
            {
                option.IsSelected = option.IsEnabled && locationIds.Contains(option.Id);
            }
        }

        if (!IsSelectedUserSuperRole)
        {
            _suppressDefaultSelectionSync = true;
            SelectedUserDefaultCompanyId = SelectedUser.DefaultCompanyId;
            _suppressDefaultSelectionSync = false;
            RefreshDefaultLocationOptions();
            _suppressDefaultSelectionSync = true;
            SelectedUserDefaultLocationId = SelectedUser.DefaultLocationId;
            _suppressDefaultSelectionSync = false;
            if (!SelectedUserDefaultLocationId.HasValue ||
                !UserDefaultLocationOptions.Any(x => x.Id == SelectedUserDefaultLocationId.Value))
            {
                SelectedUserDefaultLocationId = UserDefaultLocationOptions.FirstOrDefault()?.Id;
            }
            return;
        }

        foreach (var option in UserCompanyOptions)
        {
            option.IsSelected = true;
            option.IsEnabled = true;
        }

        foreach (var option in UserLocationOptions)
        {
            option.IsSelected = true;
            option.IsEnabled = true;
        }

        _suppressDefaultSelectionSync = true;
        SelectedUserDefaultCompanyId = null;
        SelectedUserDefaultLocationId = null;
        _suppressDefaultSelectionSync = false;
        ReplaceCollection(UserDefaultCompanyOptions, Enumerable.Empty<ManagedCompany>());
        ReplaceCollection(UserDefaultLocationOptions, Enumerable.Empty<ManagedLocation>());
    }

    private void ApplyUserLocationFilter()
    {
        if (IsSelectedUserSuperRole)
        {
            foreach (var locationOption in UserLocationOptions)
            {
                locationOption.IsEnabled = true;
            }

            ReplaceCollection(UserDefaultLocationOptions, Enumerable.Empty<ManagedLocation>());
            return;
        }

        var selectedCompanyIds = UserCompanyOptions
            .Where(x => x.IsSelected)
            .Select(x => x.Id)
            .ToHashSet();

        foreach (var locationOption in UserLocationOptions)
        {
            locationOption.IsEnabled = locationOption.GroupId.HasValue && selectedCompanyIds.Contains(locationOption.GroupId.Value);
        }

        RefreshDefaultLocationOptions();
    }

    private void UserCompanyOptionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableOption.IsSelected))
        {
            return;
        }

        ApplyUserLocationFilter();
    }

    private void UserLocationOptionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableOption.IsSelected))
        {
            return;
        }

        RefreshDefaultLocationOptions();
    }

    private void RefreshDefaultLocationOptions()
    {
        if (SelectedUser is null || IsSelectedUserSuperRole)
        {
            ReplaceCollection(UserDefaultCompanyOptions, Enumerable.Empty<ManagedCompany>());
            ReplaceCollection(UserDefaultLocationOptions, Enumerable.Empty<ManagedLocation>());
            return;
        }

        var selectedCompanyIds = UserCompanyOptions
            .Where(x => x.IsSelected)
            .Select(x => x.Id)
            .ToHashSet();

        var selectedLocationIds = UserLocationOptions
            .Where(x => x.IsSelected && x.IsEnabled)
            .Select(x => x.Id)
            .ToHashSet();

        if (selectedCompanyIds.Count == 0)
        {
            _suppressDefaultSelectionSync = true;
            SelectedUserDefaultCompanyId = null;
            SelectedUserDefaultLocationId = null;
            _suppressDefaultSelectionSync = false;
            ReplaceCollection(UserDefaultCompanyOptions, Enumerable.Empty<ManagedCompany>());
            ReplaceCollection(UserDefaultLocationOptions, Enumerable.Empty<ManagedLocation>());
            return;
        }

        ReplaceCollection(
            UserDefaultCompanyOptions,
            Companies
                .Where(x => x.IsActive && selectedCompanyIds.Contains(x.Id))
                .OrderBy(x => x.Code));

        if (!SelectedUserDefaultCompanyId.HasValue || !selectedCompanyIds.Contains(SelectedUserDefaultCompanyId.Value))
        {
            _suppressDefaultSelectionSync = true;
            SelectedUserDefaultCompanyId = selectedCompanyIds.OrderBy(x => x).FirstOrDefault();
            _suppressDefaultSelectionSync = false;
        }

        var companyId = SelectedUserDefaultCompanyId.GetValueOrDefault();
        var defaultLocations = Locations
            .Where(x => x.IsActive && x.CompanyId == companyId && selectedLocationIds.Contains(x.Id))
            .OrderBy(x => x.Code)
            .ToList();
        ReplaceCollection(UserDefaultLocationOptions, defaultLocations);

        if (!SelectedUserDefaultLocationId.HasValue ||
            !defaultLocations.Any(x => x.Id == SelectedUserDefaultLocationId.Value))
        {
            SelectedUserDefaultLocationId = defaultLocations.FirstOrDefault()?.Id;
        }
    }

    private void RefreshFilteredLocations(long? preferredLocationId = null)
    {
        if (SelectedCompany is null)
        {
            ReplaceCollection(FilteredLocations, Enumerable.Empty<ManagedLocation>());
            SelectedLocation = null;
            return;
        }

        var companyId = SelectedCompany.Id;
        ReplaceCollection(
            FilteredLocations,
            Locations
                .Where(x => x.CompanyId == companyId)
                .OrderBy(x => x.Code));

        var targetLocationId = preferredLocationId ?? SelectedLocation?.Id;
        if (targetLocationId.HasValue)
        {
            SelectedLocation = FilteredLocations.FirstOrDefault(x => x.Id == targetLocationId.Value) ?? FilteredLocations.FirstOrDefault();
            return;
        }

        SelectedLocation = FilteredLocations.FirstOrDefault();
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static void CopyMap(Dictionary<long, HashSet<long>> source, Dictionary<long, HashSet<long>> target)
    {
        target.Clear();
        foreach (var entry in source)
        {
            target[entry.Key] = new HashSet<long>(entry.Value);
        }
    }

    private void FilterRoles()
    {
        var search = _roleSearchText?.Trim() ?? string.Empty;
        var source = string.IsNullOrEmpty(search)
            ? Roles
            : new ObservableCollection<ManagedRole>(
                Roles.Where(r =>
                    r.Code.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));

        ReplaceCollection(FilteredRoles, source);
    }

    private static void SetAllOptions(ObservableCollection<SelectableOption> options, bool selected)
    {
        foreach (var option in options)
        {
            if (option.IsEnabled)
            {
                option.IsSelected = selected;
            }
        }
    }

    private void SetAllRoleScopes(bool selected)
    {
        _suppressRoleEditorDirtyTracking = true;
        SetAllOptions(RoleScopeOptions, selected);
        _suppressRoleEditorDirtyTracking = false;
        UpdateRolePermissionCounts();
        EvaluateRoleEditorDirty();
        if (ShowSelectedRolePermissionsOnly)
        {
            BuildRolePermissionTree();
        }
    }

    private void BuildRolePermissionTree()
    {
        var previousModuleExpansion = RolePermissionModules.ToDictionary(
            x => x.ModuleCode,
            x => x.IsExpanded,
            StringComparer.OrdinalIgnoreCase);

        var previousSubmoduleExpansion = RolePermissionModules
            .SelectMany(x => x.Submodules.Select(s => new KeyValuePair<string, bool>($"{x.ModuleCode}|{s.SubmoduleCode}", s.IsExpanded)))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        RolePermissionModules.Clear();

        var keyword = RolePermissionSearchText?.Trim() ?? string.Empty;
        var options = RoleScopeOptions.Where(option =>
        {
            if (ShowSelectedRolePermissionsOnly && !option.IsSelected)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(keyword))
            {
                return true;
            }

            return option.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   option.ModuleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   option.SubmoduleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   option.ActionCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   option.ModuleCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   option.SubmoduleCode.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        });

        var groupedModules = options
            .GroupBy(x => $"{x.ModuleCode}|{x.ModuleName}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Min(item => item.Id));

        foreach (var moduleGroup in groupedModules)
        {
            var firstModule = moduleGroup.First();
            var module = new PermissionModuleGroup
            {
                ModuleCode = firstModule.ModuleCode,
                ModuleName = firstModule.ModuleName,
                IsExpanded = previousModuleExpansion.TryGetValue(firstModule.ModuleCode, out var expanded) ? expanded : true
            };

            var groupedSubmodules = moduleGroup
                .GroupBy(x => $"{x.SubmoduleCode}|{x.SubmoduleName}", StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Min(item => item.Id));

            foreach (var submoduleGroup in groupedSubmodules)
            {
                var firstSubmodule = submoduleGroup.First();
                module.Submodules.Add(new PermissionSubmoduleGroup
                {
                    ModuleCode = module.ModuleCode,
                    ModuleName = module.ModuleName,
                    SubmoduleCode = firstSubmodule.SubmoduleCode,
                    SubmoduleName = firstSubmodule.SubmoduleName,
                    IsExpanded = previousSubmoduleExpansion.TryGetValue($"{module.ModuleCode}|{firstSubmodule.SubmoduleCode}", out var subExpanded)
                        ? subExpanded
                        : true,
                    Actions = new ObservableCollection<SelectableOption>(
                        submoduleGroup.OrderBy(x => x.Id))
                });
            }

            RolePermissionModules.Add(module);
        }

        UpdateRolePermissionCounts();
    }

    private void SetAllModuleActions(object? parameter, bool selected = true)
    {
        if (parameter is not PermissionModuleGroup module)
        {
            return;
        }

        _suppressRoleEditorDirtyTracking = true;
        foreach (var submodule in module.Submodules)
        {
            SetAllOptions(submodule.Actions, selected);
        }
        _suppressRoleEditorDirtyTracking = false;
        UpdateRolePermissionCounts();
        EvaluateRoleEditorDirty();
        if (ShowSelectedRolePermissionsOnly)
        {
            BuildRolePermissionTree();
        }
    }

    private void SetAllSubmoduleActions(object? parameter, bool selected = true)
    {
        if (parameter is not PermissionSubmoduleGroup submodule)
        {
            return;
        }

        _suppressRoleEditorDirtyTracking = true;
        SetAllOptions(submodule.Actions, selected);
        _suppressRoleEditorDirtyTracking = false;
        UpdateRolePermissionCounts();
        EvaluateRoleEditorDirty();
        if (ShowSelectedRolePermissionsOnly)
        {
            BuildRolePermissionTree();
        }
    }

    private void ExpandAllRolePermissionGroups()
    {
        foreach (var module in RolePermissionModules)
        {
            module.IsExpanded = true;
            foreach (var submodule in module.Submodules)
            {
                submodule.IsExpanded = true;
            }
        }
    }

    private void CollapseAllRolePermissionGroups()
    {
        foreach (var module in RolePermissionModules)
        {
            module.IsExpanded = false;
            foreach (var submodule in module.Submodules)
            {
                submodule.IsExpanded = false;
            }
        }
    }

    private void ClearRolePermissionFilter()
    {
        RolePermissionSearchText = string.Empty;
        ShowSelectedRolePermissionsOnly = false;
    }

    private void PopulateUserGridColumns(
        IReadOnlyCollection<ManagedUser> users,
        IReadOnlyCollection<ManagedRole> roles,
        IReadOnlyCollection<ManagedAccessScope> scopes)
    {
        var roleMap = roles.ToDictionary(x => x.Id);
        var scopeMap = scopes.ToDictionary(x => x.Id);
        var allModuleNames = scopes
            .Select(x => string.IsNullOrWhiteSpace(x.ModuleName) ? x.ModuleCode : x.ModuleName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var user in users)
        {
            if (!_userRoleMap.TryGetValue(user.Id, out var roleIds) || roleIds.Count == 0)
            {
                user.RoleDisplay = "-";
                user.ModuleDisplay = "-";
                continue;
            }

            var roleLabels = new List<string>();
            var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasSuperRole = false;

            foreach (var roleId in roleIds)
            {
                if (!roleMap.TryGetValue(roleId, out var role))
                {
                    continue;
                }

                roleLabels.Add($"{role.Code} - {role.Name}");

                if (role.IsSuperRole)
                {
                    hasSuperRole = true;
                    continue;
                }

                if (!_roleScopeMap.TryGetValue(role.Id, out var scopeIds))
                {
                    continue;
                }

                foreach (var scopeId in scopeIds)
                {
                    if (scopeMap.TryGetValue(scopeId, out var scope))
                    {
                        var moduleName = string.IsNullOrWhiteSpace(scope.ModuleName) ? scope.ModuleCode : scope.ModuleName;
                        if (!string.IsNullOrWhiteSpace(moduleName))
                        {
                            moduleNames.Add(moduleName);
                        }
                    }
                }
            }

            user.RoleDisplay = roleLabels.Count == 0
                ? "-"
                : string.Join(", ", roleLabels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            user.ModuleDisplay = hasSuperRole
                ? (allModuleNames.Length == 0 ? "ALL" : string.Join(", ", allModuleNames))
                : moduleNames.Count == 0
                    ? "-"
                    : string.Join(", ", moduleNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
    }

    public int GetRoleScopeCount(long roleId)
    {
        var role = Roles.FirstOrDefault(x => x.Id == roleId);
        if (role?.IsSuperRole == true)
        {
            return TotalScopesCount;
        }

        return _roleScopeMap.TryGetValue(roleId, out var scopeIds) ? scopeIds.Count : 0;
    }
}
