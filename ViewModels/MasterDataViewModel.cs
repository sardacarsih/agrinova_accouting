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

public sealed class MasterDataViewModel : ViewModelBase
{
    private const int AccountDefaultPageSize = 50;
    private const string AccountStatusActive = "Aktif";
    private const string AccountStatusAll = "Semua";
    private const string AccountStatusInactive = "Nonaktif";

    private readonly IAccessControlService _accessControlService;
    private readonly string _actorUsername;
    private readonly long _companyId;
    private readonly long _locationId;
    private readonly Func<string, bool> _confirmClosePeriod;
    private readonly RelayCommand _saveAccountCommand;
    private readonly RelayCommand _deactivateAccountCommand;
    private readonly RelayCommand _nextCloseWizardStepCommand;
    private readonly RelayCommand _previousCloseWizardStepCommand;
    private readonly RelayCommand _validateCloseWizardChecklistCommand;
    private readonly RelayCommand _confirmCloseWizardCommand;
    private readonly RelayCommand _cancelCloseWizardCommand;
    private readonly RelayCommand _previousAccountPageCommand;
    private readonly RelayCommand _nextAccountPageCommand;

    private ManagedAccount? _selectedAccount;
    private ManagedAccountingPeriod? _selectedAccountingPeriod;
    private DateTime _periodMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string _periodNote = string.Empty;
    private string _statusMessage = string.Empty;
    private string _accountSearchText = string.Empty;
    private string _selectedAccountStatusFilter = AccountStatusActive;
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
    private string _selectedMasterDataSubmenu = "coa";

    public MasterDataViewModel(
        IAccessControlService accessControlService,
        string actorUsername,
        long companyId,
        long locationId,
        string companyDisplayName,
        string locationDisplayName,
        bool canManageAccountingPeriod,
        Func<string, bool>? confirmClosePeriod = null)
    {
        _accessControlService = accessControlService;
        _actorUsername = string.IsNullOrWhiteSpace(actorUsername) ? "SYSTEM" : actorUsername.Trim();
        _companyId = companyId;
        _locationId = locationId;
        _confirmClosePeriod = confirmClosePeriod ?? (_ => true);

        CompanyDisplayName = string.IsNullOrWhiteSpace(companyDisplayName) ? "-" : companyDisplayName.Trim();
        LocationDisplayName = string.IsNullOrWhiteSpace(locationDisplayName) ? "-" : locationDisplayName.Trim();
        CanManageAccountingPeriod = canManageAccountingPeriod;

        Accounts = new ObservableCollection<ManagedAccount>();
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
        AccountStatusFilterOptions = new ObservableCollection<string>
        {
            AccountStatusActive,
            AccountStatusAll,
            AccountStatusInactive
        };
        ClosePeriodChecklistItems = new ObservableCollection<PeriodCloseChecklistItem>();

        RefreshCommand = new RelayCommand(() => _ = LoadDataAsync());

        NewAccountCommand = new RelayCommand(NewAccount);
        _saveAccountCommand = new RelayCommand(() => _ = SaveAccountAsync(), () => CanSaveAccount);
        SaveAccountCommand = _saveAccountCommand;
        _deactivateAccountCommand = new RelayCommand(() => _ = DeactivateAccountAsync(), () => CanDeactivateAccount);
        DeactivateAccountCommand = _deactivateAccountCommand;
        RebuildAccountHierarchyCommand = new RelayCommand(() => _ = RebuildAccountHierarchyAsync());
        _previousAccountPageCommand = new RelayCommand(() => _ = GoToPreviousAccountPageAsync(), () => CanGoToPreviousAccountPage && !IsBusy);
        PreviousAccountPageCommand = _previousAccountPageCommand;
        _nextAccountPageCommand = new RelayCommand(() => _ = GoToNextAccountPageAsync(), () => CanGoToNextAccountPage && !IsBusy);
        NextAccountPageCommand = _nextAccountPageCommand;

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

    public string CompanyDisplayName { get; }

    public string LocationDisplayName { get; }

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
            OnPropertyChanged(nameof(IsMasterPeriodSelected));
            OnPropertyChanged(nameof(IsMasterPlaceholderSelected));
            OnPropertyChanged(nameof(MasterPlaceholderTitle));
            OnPropertyChanged(nameof(MasterPlaceholderDescription));
        }
    }

    public bool IsMasterAccountListSelected => string.Equals(SelectedMasterDataSubmenu, "coa", StringComparison.OrdinalIgnoreCase);

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
    
    public ObservableCollection<ManagedAccount> ParentAccountOptions { get; }

    public ObservableCollection<string> AccountStatusFilterOptions { get; }

    public ObservableCollection<ManagedAccountingPeriod> AccountingPeriods { get; }

    public ObservableCollection<ManagedAuditLog> AccountingPeriodAuditLogs { get; }

    public ObservableCollection<PeriodCloseChecklistItem> ClosePeriodChecklistItems { get; }

    public ObservableCollection<string> AccountTypeOptions { get; }

    public ICommand RefreshCommand { get; }

    public ICommand NewAccountCommand { get; }

    public ICommand SaveAccountCommand { get; }

    public ICommand DeactivateAccountCommand { get; }

    public ICommand RebuildAccountHierarchyCommand { get; }

    public ICommand PreviousAccountPageCommand { get; }

    public ICommand NextAccountPageCommand { get; }

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

            if (SelectedAccount is null)
            {
                return;
            }

            if (value is null)
            {
                SelectedAccount.ParentAccountId = null;
                SelectedAccount.ParentAccountCode = string.Empty;
                SelectedAccount.HierarchyLevel = 1;
                SelectedAccount.IsPosting = false;
            }
            else
            {
                SelectedAccount.ParentAccountId = value.Id;
                SelectedAccount.ParentAccountCode = value.Code;
                SelectedAccount.HierarchyLevel = 2;
                SelectedAccount.IsPosting = true;
            }

            NotifyAccountFormChanged();
        }
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

    public bool IsAccountCreateMode => SelectedAccount is not null && SelectedAccount.Id <= 0;

    public bool IsAccountEditMode => SelectedAccount is not null && SelectedAccount.Id > 0;

    public string AccountEditorTitle =>
        SelectedAccount is null
            ? "Editor Master Akun"
            : IsAccountCreateMode
                ? "Buat Akun Baru"
                : "Edit Akun";

    public string AccountEditorSubtitle =>
        SelectedAccount is null
            ? "Pilih akun dari daftar atau klik Akun Baru untuk mulai input."
            : IsAccountCreateMode
                ? "Lengkapi kode, nama, tipe akun, dan parent jika akun turunan."
                : "Perubahan tersimpan pada akun terpilih. Gunakan Nonaktifkan jika akun tidak digunakan lagi.";

    public string DerivedParentAccountCode
    {
        get
        {
            if (SelectedAccount is null)
            {
                return "-";
            }

            if (!SelectedAccount.ParentAccountId.HasValue)
            {
                return "(Summary Root)";
            }

            return string.IsNullOrWhiteSpace(SelectedAccount.ParentAccountCode)
                ? "-"
                : SelectedAccount.ParentAccountCode;
        }
    }

    public string DerivedHierarchyLevelText
    {
        get
        {
            if (SelectedAccount is null)
            {
                return "-";
            }

            return SelectedAccount.HierarchyLevel <= 1
                ? "1 (Summary)"
                : "2 (Posting)";
        }
    }

    public string DerivedPostingModeText
    {
        get
        {
            if (SelectedAccount is null)
            {
                return "-";
            }

            return SelectedAccount.IsPosting ? "Posting" : "Non-Posting";
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
        SelectedAccount is { Id: > 0 } &&
        SelectedAccount.IsActive;

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
            NotifyAccountFormChanged();
            _previousAccountPageCommand.RaiseCanExecuteChanged();
            _nextAccountPageCommand.RaiseCanExecuteChanged();
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

        _saveAccountCommand.RaiseCanExecuteChanged();
        _deactivateAccountCommand.RaiseCanExecuteChanged();
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
            StatusMessage = "Memuat master data akun dan periode...";
            var accountTask = _accessControlService.SearchAccountsAsync(
                _companyId,
                BuildAccountSearchFilter(AccountPage));
            var parentTask = _accessControlService.GetAccountsAsync(_companyId, includeInactive: false);
            var periodTask = _accessControlService.GetAccountingPeriodsAsync(_companyId, _locationId);
            var auditTask = _accessControlService.GetAuditLogsAsync("ACCOUNTING_PERIOD", 300);
            await Task.WhenAll(accountTask, parentTask, periodTask, auditTask);

            ApplyAccountSearchResult(accountTask.Result, selectedAccountId);
            UpdateParentAccountOptions(parentTask.Result);
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
            StatusMessage = "Master data akun dan periode siap digunakan.";
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(MasterDataViewModel),
                "LoadDataFailed",
                $"action=load_master_data company_id={_companyId} location_id={_locationId}",
                ex);
            StatusMessage = "Gagal memuat master data akun/periode.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewAccount()
    {
        SelectedAccount = new ManagedAccount
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

        StatusMessage = "Input akun baru siap.";
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

        if (SelectedAccount is null)
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
            var result = await _accessControlService.SaveAccountAsync(_companyId, SelectedAccount, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
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

        if (SelectedAccount is null)
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
            var selectedId = SelectedAccount.Id;
            var periodMonth = SelectedAccountingPeriod?.PeriodMonth ?? PeriodMonth;
            var result = await _accessControlService.SoftDeleteAccountAsync(_companyId, selectedId, _actorUsername);
            StatusMessage = result.Message;

            if (result.IsSuccess)
            {
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
                BuildAccountSearchFilter(requestedPage));
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
            if (SelectedAccount?.ParentAccountId is not long parentId || parentId <= 0)
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
        if (SelectedAccount is null)
        {
            return;
        }

        if (SelectedParentAccountOption is null)
        {
            SelectedAccount.ParentAccountId = null;
            SelectedAccount.ParentAccountCode = string.Empty;
            SelectedAccount.HierarchyLevel = 1;
            SelectedAccount.IsPosting = false;
            return;
        }

        SelectedAccount.ParentAccountId = SelectedParentAccountOption.Id;
        SelectedAccount.ParentAccountCode = SelectedParentAccountOption.Code;
        SelectedAccount.HierarchyLevel = 2;
        SelectedAccount.IsPosting = true;
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
            var periods = await _accessControlService.GetAccountingPeriodsAsync(_companyId, _locationId);
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
                });
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
        if (SelectedAccount is null)
        {
            message = "Pilih akun dari daftar atau klik Akun Baru.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SelectedAccount.Code))
        {
            message = "Kode akun wajib diisi.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SelectedAccount.Name))
        {
            message = "Nama akun wajib diisi.";
            return false;
        }

        if (!IsSegmentedAccountCode(SelectedAccount.Code))
        {
            message = "Format kode akun harus XX.XXXXX.XXX.";
            return false;
        }

        if (SelectedParentAccountOption is not null)
        {
            if (SelectedAccount.Id > 0 && SelectedParentAccountOption.Id == SelectedAccount.Id)
            {
                message = "Parent akun tidak boleh akun itu sendiri.";
                return false;
            }

            if (SelectedParentAccountOption.ParentAccountId.HasValue)
            {
                message = "Parent akun harus akun level 1 (summary).";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool IsSegmentedAccountCode(string? accountCode)
    {
        var code = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length != 12 || code[2] != '.' || code[8] != '.')
        {
            return false;
        }

        if (!char.IsLetterOrDigit(code[0]) || !char.IsLetterOrDigit(code[1]))
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
