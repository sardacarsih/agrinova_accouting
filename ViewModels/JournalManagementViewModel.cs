using System.Collections.ObjectModel;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Accounting.Infrastructure;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class JournalManagementViewModel : ViewModelBase
{
    private readonly IAccessControlService _accessControlService;
    private readonly JournalImportExportWorkflow _importExportWorkflow;
    private readonly JournalLifecycleWorkflow _journalLifecycleWorkflow;
    private readonly JournalLineValidationService _lineValidationService;
    private readonly string _actorUsername;
    private readonly long _companyId;
    private readonly long _locationId;
    private readonly bool _canCreateJournalDraft;
    private readonly bool _canUpdateJournalDraft;
    private readonly bool _canSubmitJournal;
    private readonly bool _canApproveJournal;
    private readonly bool _canPostJournal;
    private readonly bool _canImportJournals;
    private readonly bool _canExportJournals;
    private readonly bool _canPullInventoryJournals;
    private readonly RelayCommand _newJournalCommand;
    private readonly RelayCommand _saveDraftCommand;
    private readonly RelayCommand _submitCommand;
    private readonly RelayCommand _approveCommand;
    private readonly RelayCommand _postCommand;
    private readonly RelayCommand _openSelectedJournalCommand;
    private readonly RelayCommand _browseImportFileCommand;
    private readonly RelayCommand _previewImportCommand;
    private readonly RelayCommand _commitImportCommand;
    private readonly RelayCommand _pullInventoryJournalsCommand;
    private readonly RelayCommand _loadPulledDraftsToSearchCommand;
    private readonly RelayCommand _openPulledDraftJournalCommand;
    private readonly RelayCommand _exportCurrentCommand;
    private readonly RelayCommand _exportSelectedCommand;
    private readonly RelayCommand _previewExportPeriodCommand;
    private readonly RelayCommand _exportPeriodCommand;
    private readonly RelayCommand _previousSearchPeriodCommand;
    private readonly RelayCommand _nextSearchPeriodCommand;

    private bool _isLoaded;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    private long _journalId;
    private string _journalNo = string.Empty;
    private DateTime _journalDate = DateTime.Today;
    private DateTime _journalPeriodMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string _journalPeriodText = DateTime.Today.ToString("MM/yyyy", CultureInfo.InvariantCulture);
    private string _referenceNo = string.Empty;
    private string _journalDescription = string.Empty;
    private string _journalStatus = "DRAFT";
    private decimal _totalDebit;
    private decimal _totalCredit;

    private JournalLineEditor? _selectedInputLine;
    private ManagedJournalSummary? _selectedBrowseJournal;
    private ManagedJournalSummary? _selectedJournal;

    private DateTime _searchPeriodMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private int _searchPeriodMonthNumber = DateTime.Today.Month;
    private int _searchPeriodYear = DateTime.Today.Year;
    private string _searchPeriodText = DateTime.Today.ToString("MM/yyyy", CultureInfo.InvariantCulture);
    private DateTime? _searchDateFrom;
    private DateTime? _searchDateTo;
    private string _searchKeyword = string.Empty;
    private string _searchStatus = string.Empty;

    private string _importFilePath = string.Empty;
    private string _importMessage = string.Empty;
    private DateTime _inventoryPullPeriodMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private string _inventoryPullPeriodText = DateTime.Today.ToString("MM/yyyy", CultureInfo.InvariantCulture);
    private int _exportPeriodMonth = DateTime.Today.Month;
    private int _exportPeriodYear = DateTime.Today.Year;
    private bool _exportPeriodUseLegacyFormat;
    private string _exportPeriodPreviewSummary = "Pilih periode lalu klik Tampilkan Detail.";
    private string _exportPeriodPreviewKey = string.Empty;
    private string _inventoryPullMessage = string.Empty;
    private int _selectedJournalTabIndex;
    private int _selectedBatchTabIndex;
    private string _selectedJournalScenarioCode = "jurnal_umum";
    private bool _isBrowseSearchActive;
    private bool _suppressBrowseFilterAutoSearch;
    private readonly string _inventoryPullLocationLabel;
    private long? _dashboardSearchCompanyId;
    private long? _dashboardSearchLocationId;
    private List<JournalImportBundleResult> _stagedImportBundles = new();
    private readonly Dictionary<string, ManagedAccount> _accountLookupByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<JournalLineEditor> _accountSyncInProgress = new();
    private readonly Dictionary<string, bool> _periodOpenByMonthKey = new(StringComparer.OrdinalIgnoreCase);
    private JournalAccountingPeriodOption? _selectedJournalAccountingPeriodOption;
    private JournalAccountingPeriodOption? _selectedSearchAccountingPeriodOption;
    private JournalAccountingPeriodOption? _selectedInventoryPullAccountingPeriodOption;
    private bool _isSynchronizingPeriodPickerState;
    private bool _isCurrentPeriodOpen = true;
    private string _currentPeriodStatusText = "OPEN";
    private string _currentPeriodMonthText = DateTime.Today.ToString("MM/yyyy");

    public JournalManagementViewModel(
        IAccessControlService accessControlService,
        UserAccessContext accessContext,
        string? locationDisplayName = null)
    {
        _accessControlService = accessControlService;
        _actorUsername = string.IsNullOrWhiteSpace(accessContext.Username) ? "SYSTEM" : accessContext.Username.Trim();
        _companyId = accessContext.SelectedCompanyId;
        _locationId = accessContext.SelectedLocationId;
        _inventoryPullLocationLabel = _locationId > 0
            ? string.IsNullOrWhiteSpace(locationDisplayName)
                ? $"Lokasi aktif profil user (ID: {_locationId})"
                : $"Lokasi aktif profil user: {locationDisplayName.Trim()}"
            : "Konteks lokasi user belum dipilih";
        _canCreateJournalDraft = accessContext.HasAction("accounting", "transactions", "create");
        _canUpdateJournalDraft = accessContext.HasAction("accounting", "transactions", "update");
        _canSubmitJournal = accessContext.HasAction("accounting", "transactions", "submit");
        _canApproveJournal = accessContext.HasAction("accounting", "transactions", "approve");
        _canPostJournal = accessContext.HasAction("accounting", "transactions", "post");
        _canImportJournals = accessContext.HasAction("accounting", "transactions", "import");
        _canExportJournals = accessContext.HasAction("accounting", "transactions", "export");
        _canPullInventoryJournals = accessContext.HasAction("inventory", "api_inv", "pull_journal");
        _importExportWorkflow = new JournalImportExportWorkflow(_accessControlService, new JournalXlsxService());
        _journalLifecycleWorkflow = new JournalLifecycleWorkflow(_accessControlService);
        _lineValidationService = new JournalLineValidationService();

        Accounts = new ObservableCollection<ManagedAccount>();
        InputLines = new ObservableCollection<JournalLineEditor>();
        JournalList = new ObservableCollection<ManagedJournalSummary>();
        SearchResults = new ObservableCollection<ManagedJournalSummary>();
        AccountingPeriodOptions = new ObservableCollection<JournalAccountingPeriodOption>();
        ImportPreviewItems = new ObservableCollection<JournalImportPreviewItem>();
        InventoryPullCreatedJournalNos = new ObservableCollection<string>();
        InventoryPullCreatedJournalNos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPulledInventoryDrafts));
            RaiseImportExportCommandCanExecuteChanged();
        };
        ExportPeriodPreviewJournals = new ObservableCollection<ManagedJournalSummary>();
        ExportPeriodPreviewLines = new ObservableCollection<JournalExportPreviewLine>();
        ExportPeriodPreviewLines.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasExportPeriodPreview));
            OnPropertyChanged(nameof(CanExportPeriod));
            OnPropertyChanged(nameof(ExportPeriodTooltip));
            RaiseImportExportCommandCanExecuteChanged();
        };
        StatusFilterOptions = new ObservableCollection<string> { "", "UNPOSTED", "DRAFT", "SUBMITTED", "APPROVED", "POSTED" };
        ExportMonthOptions = new ObservableCollection<KeyValuePair<int, string>>(BuildExportMonthOptions());
        ExportYearOptions = new ObservableCollection<int>(BuildExportYearOptions());

        if (!ExportYearOptions.Contains(_exportPeriodYear) && ExportYearOptions.Count > 0)
        {
            _exportPeriodYear = ExportYearOptions[0];
        }

        RefreshCommand = new RelayCommand(() => _ = RefreshBrowseAsync());
        _newJournalCommand = new RelayCommand(NewJournal, () => CanCreateNewJournal);
        NewJournalCommand = _newJournalCommand;
        AddLineCommand = new RelayCommand(AddLine);
        RemoveLineCommand = new RelayCommand(RemoveSelectedLine);
        RemoveLineByRowCommand = new RelayCommand(RemoveLineByRow);
        _saveDraftCommand = new RelayCommand(() => _ = SaveDraftAsync(), () => CanSaveDraft);
        SaveDraftCommand = _saveDraftCommand;
        _submitCommand = new RelayCommand(() => _ = SubmitCurrentAsync(), () => CanSubmitCurrentJournal);
        SubmitCommand = _submitCommand;
        _approveCommand = new RelayCommand(() => _ = ApproveCurrentAsync(), () => CanApproveCurrentJournal);
        ApproveCommand = _approveCommand;
        _postCommand = new RelayCommand(() => _ = PostCurrentAsync(), () => CanPostCurrentJournal);
        PostCommand = _postCommand;
        _openSelectedJournalCommand = new RelayCommand(() => _ = OpenSelectedJournalAsync(), () => CanOpenSelectedJournal);
        _previousSearchPeriodCommand = new RelayCommand(MoveToPreviousSearchPeriod, () => CanMoveToPreviousSearchPeriod);
        _nextSearchPeriodCommand = new RelayCommand(MoveToNextSearchPeriod, () => CanMoveToNextSearchPeriod);
        OpenSelectedJournalCommand = _openSelectedJournalCommand;
        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        ResetBrowseFiltersCommand = new RelayCommand(ResetBrowseFilters);
        _browseImportFileCommand = new RelayCommand(BrowseImportFile, () => CanBrowseImportFile);
        BrowseImportFileCommand = _browseImportFileCommand;
        _previewImportCommand = new RelayCommand(PreviewImport, () => CanPreviewImportFile);
        PreviewImportCommand = _previewImportCommand;
        _commitImportCommand = new RelayCommand(() => _ = CommitImportAsync(), () => CanCommitImportDrafts);
        CommitImportCommand = _commitImportCommand;
        _pullInventoryJournalsCommand = new RelayCommand(() => _ = PullInventoryJournalsAsync(), () => CanPullInventoryJournals);
        PullInventoryJournalsCommand = _pullInventoryJournalsCommand;
        _loadPulledDraftsToSearchCommand = new RelayCommand(() => _ = LoadPulledDraftsToSearchAsync(), () => CanPullInventoryJournals);
        LoadPulledDraftsToSearchCommand = _loadPulledDraftsToSearchCommand;
        _openPulledDraftJournalCommand = new RelayCommand(parameter => _ = OpenPulledDraftJournalAsync(parameter), _ => HasPulledInventoryDrafts);
        OpenPulledDraftJournalCommand = _openPulledDraftJournalCommand;
        _exportCurrentCommand = new RelayCommand(ExportCurrentJournal, () => CanExportCurrentJournal);
        ExportCurrentCommand = _exportCurrentCommand;
        _exportSelectedCommand = new RelayCommand(parameter => ExportSelectedJournal(parameter), _ => CanExportAnyJournal);
        ExportSelectedCommand = _exportSelectedCommand;
        _previewExportPeriodCommand = new RelayCommand(() => _ = PreviewExportPeriodAsync(), () => CanPreviewExportPeriod);
        PreviewExportPeriodCommand = _previewExportPeriodCommand;
        _exportPeriodCommand = new RelayCommand(() => _ = ExportPeriodAsync(), () => CanExportPeriod);
        ExportPeriodCommand = _exportPeriodCommand;
        OpenAccountPickerCommand = new RelayCommand(OpenAccountPicker);

        NavigateToJournalScenario("jurnal_umum");
        NewJournal();
    }

    public ObservableCollection<ManagedAccount> Accounts { get; }

    public ObservableCollection<JournalLineEditor> InputLines { get; }

    public ObservableCollection<ManagedJournalSummary> JournalList { get; }

    public ObservableCollection<ManagedJournalSummary> SearchResults { get; }

    public ObservableCollection<JournalAccountingPeriodOption> AccountingPeriodOptions { get; }

    public ObservableCollection<JournalImportPreviewItem> ImportPreviewItems { get; }

    public ObservableCollection<string> InventoryPullCreatedJournalNos { get; }

    public ObservableCollection<ManagedJournalSummary> ExportPeriodPreviewJournals { get; }

    public ObservableCollection<JournalExportPreviewLine> ExportPeriodPreviewLines { get; }

    public ObservableCollection<string> StatusFilterOptions { get; }

    public ObservableCollection<KeyValuePair<int, string>> ExportMonthOptions { get; }

    public ObservableCollection<int> ExportYearOptions { get; }

    public ICommand RefreshCommand { get; }

    public ICommand NewJournalCommand { get; }

    public ICommand AddLineCommand { get; }

    public ICommand RemoveLineCommand { get; }

    public ICommand RemoveLineByRowCommand { get; }

    public ICommand SaveDraftCommand { get; }

    public ICommand SubmitCommand { get; }

    public ICommand ApproveCommand { get; }

    public ICommand PostCommand { get; }

    public ICommand OpenSelectedJournalCommand { get; }

    public ICommand PreviousSearchPeriodCommand => _previousSearchPeriodCommand;

    public ICommand NextSearchPeriodCommand => _nextSearchPeriodCommand;

    public ICommand SearchCommand { get; }

    public ICommand BrowseImportFileCommand { get; }

    public ICommand PreviewImportCommand { get; }

    public ICommand CommitImportCommand { get; }

    public ICommand PullInventoryJournalsCommand { get; }

    public ICommand LoadPulledDraftsToSearchCommand { get; }

    public ICommand OpenPulledDraftJournalCommand { get; }

    public ICommand ExportCurrentCommand { get; }

    public ICommand ExportSelectedCommand { get; }

    public ICommand PreviewExportPeriodCommand { get; }

    public ICommand ExportPeriodCommand { get; }

    public ICommand OpenAccountPickerCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanPullInventoryJournals));
            _openSelectedJournalCommand.RaiseCanExecuteChanged();
            RaiseAllJournalActionStateChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public long JournalId
    {
        get => _journalId;
        private set
        {
            if (!SetProperty(ref _journalId, value))
            {
                return;
            }

            RaiseSubmitApprovePostStateChanged();
        }
    }

    public string JournalNo
    {
        get => _journalNo;
        set => SetProperty(ref _journalNo, value);
    }

    public DateTime JournalDate
    {
        get => _journalDate;
        set => SetProperty(ref _journalDate, value.Date);
    }

    public DateTime JournalPeriodMonth
    {
        get => _journalPeriodMonth;
        set
        {
            var normalized = new DateTime(value.Year, value.Month, 1);
            var previous = _journalPeriodMonth;
            if (!SetProperty(ref _journalPeriodMonth, normalized))
            {
                return;
            }

            if (previous.Year != normalized.Year || previous.Month != normalized.Month)
            {
                SyncJournalPeriodPickerState();
                _ = RefreshPeriodStatusForDateAsync(normalized, reloadFromService: true);
                return;
            }

            SyncJournalPeriodPickerState();
            UpdatePeriodStatusFromCache(normalized);
        }
    }

    public string JournalPeriodText
    {
        get => _journalPeriodText;
        set
        {
            if (!SetProperty(ref _journalPeriodText, value))
            {
                return;
            }

            ApplyJournalPeriodText(value);
        }
    }

    public string ReferenceNo
    {
        get => _referenceNo;
        set => SetProperty(ref _referenceNo, value);
    }

    public string JournalDescription
    {
        get => _journalDescription;
        set => SetProperty(ref _journalDescription, value);
    }

    public string JournalStatus
    {
        get => _journalStatus;
        private set
        {
            if (!SetProperty(ref _journalStatus, value))
            {
                return;
            }

            RaiseSubmitApprovePostStateChanged();
        }
    }

    public decimal TotalDebit
    {
        get => _totalDebit;
        private set
        {
            if (!SetProperty(ref _totalDebit, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DifferenceAmount));
            OnPropertyChanged(nameof(IsBalanced));
        }
    }

    public decimal TotalCredit
    {
        get => _totalCredit;
        private set
        {
            if (!SetProperty(ref _totalCredit, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DifferenceAmount));
            OnPropertyChanged(nameof(IsBalanced));
        }
    }

    public decimal DifferenceAmount => TotalDebit - TotalCredit;

    public bool IsBalanced => DifferenceAmount == 0m;

    public bool CanRemoveAnyLine => InputLines.Count > 1;

    public bool CanSaveDraft =>
        IsCurrentPeriodOpen &&
        !IsBusy &&
        (JournalId > 0 ? _canUpdateJournalDraft : _canCreateJournalDraft) &&
        (JournalId <= 0 || string.Equals(JournalStatus, "DRAFT", StringComparison.OrdinalIgnoreCase));

    public bool CanCreateNewJournal =>
        IsCurrentPeriodOpen &&
        !IsBusy &&
        _canCreateJournalDraft;

    public bool CanSubmitCurrentJournal =>
        IsCurrentPeriodOpen &&
        !IsBusy &&
        _canSubmitJournal &&
        JournalId > 0 &&
        string.Equals(JournalStatus, "DRAFT", StringComparison.OrdinalIgnoreCase);

    public bool CanApproveCurrentJournal =>
        IsCurrentPeriodOpen &&
        _canApproveJournal &&
        !IsBusy &&
        JournalId > 0 &&
        string.Equals(JournalStatus, "SUBMITTED", StringComparison.OrdinalIgnoreCase);

    public bool CanPostCurrentJournal =>
        IsCurrentPeriodOpen &&
        _canPostJournal &&
        !IsBusy &&
        JournalId > 0 &&
        string.Equals(JournalStatus, "APPROVED", StringComparison.OrdinalIgnoreCase);

    public bool CanCommitImportDrafts =>
        !IsBusy &&
        _canImportJournals &&
        _canCreateJournalDraft &&
        _stagedImportBundles.Count > 0;

    public bool CanBrowseImportFile => !IsBusy && _canImportJournals;

    public bool CanPreviewImportFile =>
        !IsBusy &&
        _canImportJournals &&
        !string.IsNullOrWhiteSpace(ImportFilePath);

    public bool CanExportCurrentJournal =>
        !IsBusy &&
        _canExportJournals &&
        JournalId > 0;

    public bool CanPreviewExportPeriod => !IsBusy && _canExportJournals;

    public bool CanExportAnyJournal => !IsBusy && _canExportJournals;

    public bool CanExportPeriod =>
        !IsBusy &&
        _canExportJournals &&
        HasExportPeriodPreview;

    public string NewJournalTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canCreateJournalDraft
                ? "Anda tidak memiliki izin membuat draft jurnal."
                : !IsCurrentPeriodOpen
                    ? $"Periode {CurrentPeriodMonthText} CLOSED. Pembuatan jurnal baru dinonaktifkan."
                    : "Buat draft jurnal baru.";

    public string SaveDraftTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : JournalId > 0 && !_canUpdateJournalDraft
                ? "Anda tidak memiliki izin memperbarui draft jurnal."
                : JournalId <= 0 && !_canCreateJournalDraft
                    ? "Anda tidak memiliki izin membuat draft jurnal."
                    : !IsCurrentPeriodOpen
                        ? $"Periode {CurrentPeriodMonthText} CLOSED. Simpan Draft dinonaktifkan."
                        : JournalId > 0 && !string.Equals(JournalStatus, "DRAFT", StringComparison.OrdinalIgnoreCase)
                            ? "Hanya jurnal DRAFT yang bisa diedit."
                            : "Simpan jurnal sebagai draft.";

    public string SubmitTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canSubmitJournal
                ? "Anda tidak memiliki izin submit jurnal."
                : !IsCurrentPeriodOpen
                    ? $"Periode {CurrentPeriodMonthText} CLOSED. Submit dinonaktifkan."
                    : JournalId <= 0
                        ? "Simpan draft terlebih dahulu sebelum submit."
                        : !string.Equals(JournalStatus, "DRAFT", StringComparison.OrdinalIgnoreCase)
                            ? "Hanya jurnal berstatus DRAFT yang bisa disubmit."
                            : "Submit jurnal untuk proses approval.";

    public string ApproveTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canApproveJournal
                ? "Anda tidak memiliki izin approve jurnal."
                : !IsCurrentPeriodOpen
                    ? $"Periode {CurrentPeriodMonthText} CLOSED. Approve dinonaktifkan."
                    : JournalId <= 0
                        ? "Pilih jurnal terlebih dahulu."
                        : !string.Equals(JournalStatus, "SUBMITTED", StringComparison.OrdinalIgnoreCase)
                            ? "Hanya jurnal berstatus SUBMITTED yang bisa di-approve."
                            : "Approve jurnal agar siap diposting.";

    public string PostTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canPostJournal
                ? "Anda tidak memiliki izin posting jurnal."
                : !IsCurrentPeriodOpen
                    ? $"Periode {CurrentPeriodMonthText} CLOSED. Post dinonaktifkan."
                    : JournalId <= 0
                        ? "Pilih jurnal yang sudah APPROVED terlebih dahulu."
                        : !string.Equals(JournalStatus, "APPROVED", StringComparison.OrdinalIgnoreCase)
                            ? "Hanya jurnal berstatus APPROVED yang bisa diposting."
                            : "Post jurnal agar terkunci dan final.";

    public string CommitImportTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canImportJournals
                ? "Anda tidak memiliki izin import jurnal."
                : !_canCreateJournalDraft
                    ? "Anda tidak memiliki izin membuat draft jurnal hasil import."
                    : _stagedImportBundles.Count == 0
                        ? "Tidak ada draft jurnal valid untuk diimport."
                        : "Import hasil preview menjadi draft jurnal.";

    public string BrowseImportTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canImportJournals
                ? "Anda tidak memiliki izin import jurnal."
                : "Pilih file Excel jurnal untuk diimport.";

    public string PreviewImportTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canImportJournals
                ? "Anda tidak memiliki izin import jurnal."
                : string.IsNullOrWhiteSpace(ImportFilePath)
                    ? "Pilih file import terlebih dahulu."
                    : "Validasi file import dan tampilkan preview jurnal.";

    public string ExportCurrentTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canExportJournals
                ? "Anda tidak memiliki izin export jurnal."
                : JournalId <= 0
                    ? "Simpan jurnal terlebih dahulu sebelum export."
                    : "Export jurnal aktif ke Excel.";

    public string ExportPeriodTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canExportJournals
                ? "Anda tidak memiliki izin export jurnal."
                : !HasExportPeriodPreview
                    ? "Tampilkan detail jurnal periode terlebih dahulu sebelum export."
                    : "Export jurnal periode terpilih ke Excel.";

    public string PreviewExportPeriodTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canExportJournals
                ? "Anda tidak memiliki izin export jurnal."
                : "Muat detail jurnal periode untuk preview export.";

    public string ExportSelectedTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canExportJournals
                ? "Anda tidak memiliki izin export jurnal."
                : "Export jurnal yang dipilih dari tab Daftar.";

    public bool IsCurrentPeriodOpen
    {
        get => _isCurrentPeriodOpen;
        private set
        {
            if (!SetProperty(ref _isCurrentPeriodOpen, value))
            {
                return;
            }

            RaiseAllJournalActionStateChanged();
        }
    }

    public string CurrentPeriodStatusText
    {
        get => _currentPeriodStatusText;
        private set => SetProperty(ref _currentPeriodStatusText, value);
    }

    public string CurrentPeriodMonthText
    {
        get => _currentPeriodMonthText;
        private set
        {
            if (!SetProperty(ref _currentPeriodMonthText, value))
            {
                return;
            }

            RaiseJournalActionTooltipChanged(includeSaveDraft: true);
        }
    }

    public JournalLineEditor? SelectedInputLine
    {
        get => _selectedInputLine;
        set => SetProperty(ref _selectedInputLine, value);
    }

    public ManagedJournalSummary? SelectedBrowseJournal
    {
        get => _selectedBrowseJournal;
        set
        {
            if (!SetProperty(ref _selectedBrowseJournal, value))
            {
                return;
            }

            _openSelectedJournalCommand.RaiseCanExecuteChanged();
        }
    }

    public JournalAccountingPeriodOption? SelectedJournalAccountingPeriodOption
    {
        get => _selectedJournalAccountingPeriodOption;
        set
        {
            if (!SetProperty(ref _selectedJournalAccountingPeriodOption, value))
            {
                return;
            }

            if (_isSynchronizingPeriodPickerState || value is null)
            {
                return;
            }

            JournalPeriodMonth = value.PeriodMonth;
        }
    }

    public ManagedJournalSummary? SelectedJournal
    {
        get => _selectedJournal;
        set
        {
            if (!SetProperty(ref _selectedJournal, value))
            {
                return;
            }

            if (!IsBrowseSearchActive)
            {
                SelectedBrowseJournal = value;
            }

            _openSelectedJournalCommand.RaiseCanExecuteChanged();
        }
    }

    public DateTime? SearchDateFrom
    {
        get => _searchDateFrom;
        set
        {
            if (!SetProperty(ref _searchDateFrom, value))
            {
                return;
            }

            OnBrowseFilterChanged(autoSearchRequested: true);
        }
    }

    public DateTime SearchPeriodMonth
    {
        get => _searchPeriodMonth;
        set
        {
            var normalized = new DateTime(value.Year, value.Month, 1);
            if (!SetProperty(ref _searchPeriodMonth, normalized))
            {
                return;
            }

            SyncSearchPeriodPickerState();
            SyncSearchPeriodCalendarState();
            OnPropertyChanged(nameof(SearchAccountingPeriodDisplayText));
            _previousSearchPeriodCommand.RaiseCanExecuteChanged();
            _nextSearchPeriodCommand.RaiseCanExecuteChanged();

            if (!_suppressBrowseFilterAutoSearch && !string.IsNullOrWhiteSpace(_searchStatus))
            {
                _searchStatus = string.Empty;
                OnPropertyChanged(nameof(SearchStatus));
            }

            OnBrowseFilterChanged(autoSearchRequested: true);
        }
    }

    public int SearchPeriodMonthNumber
    {
        get => _searchPeriodMonthNumber;
        set
        {
            var normalized = Math.Clamp(value, 1, 12);
            if (!SetProperty(ref _searchPeriodMonthNumber, normalized))
            {
                return;
            }

            ApplySearchPeriodCalendarSelection();
        }
    }

    public int SearchPeriodYear
    {
        get => _searchPeriodYear;
        set
        {
            var normalized = NormalizeSearchPeriodYear(value);
            if (!SetProperty(ref _searchPeriodYear, normalized))
            {
                return;
            }

            ApplySearchPeriodCalendarSelection();
        }
    }

    public string SearchPeriodText
    {
        get => _searchPeriodText;
        set
        {
            if (!SetProperty(ref _searchPeriodText, value))
            {
                return;
            }

            ApplySearchPeriodText(value);
        }
    }

    public JournalAccountingPeriodOption? SelectedSearchAccountingPeriodOption
    {
        get => _selectedSearchAccountingPeriodOption;
        set
        {
            if (!SetProperty(ref _selectedSearchAccountingPeriodOption, value))
            {
                return;
            }

            if (_isSynchronizingPeriodPickerState || value is null)
            {
                return;
            }

            SearchPeriodMonth = value.PeriodMonth;
        }
    }

    public string SearchAccountingPeriodDisplayText =>
        SelectedSearchAccountingPeriodOption?.DisplayText ?? $"{SearchPeriodMonth:MM/yyyy} - TIDAK TERDAFTAR";

    public bool CanMoveToPreviousSearchPeriod
    {
        get
        {
            var minimumYear = ExportYearOptions.Count > 0 ? ExportYearOptions.Min() : 2000;
            return SearchPeriodYear > minimumYear || (SearchPeriodYear == minimumYear && SearchPeriodMonthNumber > 1);
        }
    }

    public bool CanMoveToNextSearchPeriod
    {
        get
        {
            var maximumYear = ExportYearOptions.Count > 0 ? ExportYearOptions.Max() : DateTime.Today.Year;
            return SearchPeriodYear < maximumYear || (SearchPeriodYear == maximumYear && SearchPeriodMonthNumber < 12);
        }
    }

    public DateTime? SearchDateTo
    {
        get => _searchDateTo;
        set
        {
            if (!SetProperty(ref _searchDateTo, value))
            {
                return;
            }

            OnBrowseFilterChanged(autoSearchRequested: true);
        }
    }

    public string SearchKeyword
    {
        get => _searchKeyword;
        set => SetProperty(ref _searchKeyword, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        set
        {
            if (!SetProperty(ref _searchStatus, value))
            {
                return;
            }

            OnBrowseFilterChanged(autoSearchRequested: true);
        }
    }

    public string ImportFilePath
    {
        get => _importFilePath;
        set
        {
            if (!SetProperty(ref _importFilePath, value))
            {
                return;
            }

            RaiseImportExportStateChanged();
        }
    }

    public string ImportMessage
    {
        get => _importMessage;
        private set => SetProperty(ref _importMessage, value);
    }

    public DateTime InventoryPullPeriodMonth
    {
        get => _inventoryPullPeriodMonth;
        set
        {
            var normalized = new DateTime(value.Year, value.Month, 1);
            if (!SetProperty(ref _inventoryPullPeriodMonth, normalized))
            {
                return;
            }

            SyncInventoryPullPeriodPickerState();
        }
    }

    public string InventoryPullPeriodText
    {
        get => _inventoryPullPeriodText;
        set
        {
            if (!SetProperty(ref _inventoryPullPeriodText, value))
            {
                return;
            }

            ApplyInventoryPullPeriodText(value);
        }
    }

    public JournalAccountingPeriodOption? SelectedInventoryPullAccountingPeriodOption
    {
        get => _selectedInventoryPullAccountingPeriodOption;
        set
        {
            if (!SetProperty(ref _selectedInventoryPullAccountingPeriodOption, value))
            {
                return;
            }

            if (_isSynchronizingPeriodPickerState || value is null)
            {
                return;
            }

            InventoryPullPeriodMonth = value.PeriodMonth;
        }
    }

    public int ExportPeriodMonth
    {
        get => _exportPeriodMonth;
        set
        {
            var normalized = Math.Clamp(value, 1, 12);
            if (SetProperty(ref _exportPeriodMonth, normalized))
            {
                ResetExportPeriodPreview();
            }
        }
    }

    public int ExportPeriodYear
    {
        get => _exportPeriodYear;
        set
        {
            var normalized = Math.Max(2000, value);
            if (SetProperty(ref _exportPeriodYear, normalized))
            {
                ResetExportPeriodPreview();
            }
        }
    }

    public bool ExportPeriodUseLegacyFormat
    {
        get => _exportPeriodUseLegacyFormat;
        set => SetProperty(ref _exportPeriodUseLegacyFormat, value);
    }

    public string ExportPeriodPreviewSummary
    {
        get => _exportPeriodPreviewSummary;
        private set => SetProperty(ref _exportPeriodPreviewSummary, value);
    }

    public string InventoryPullLocationLabel => _inventoryPullLocationLabel;

    public bool HasValidInventoryPullLocationScope => _locationId > 0;

    public string InventoryPullLocationHelpText =>
        HasValidInventoryPullLocationScope
            ? "Mengikuti lokasi aktif pada Konteks Kerja user."
            : "Pilih Konteks Kerja dengan lokasi valid terlebih dahulu.";

    public string InventoryPullMessage
    {
        get => _inventoryPullMessage;
        private set => SetProperty(ref _inventoryPullMessage, value);
    }

    public int SelectedJournalTabIndex
    {
        get => _selectedJournalTabIndex;
        set => SetProperty(ref _selectedJournalTabIndex, Math.Clamp(value, 0, 2));
    }

    public int SelectedBatchTabIndex
    {
        get => _selectedBatchTabIndex;
        set => SetProperty(ref _selectedBatchTabIndex, Math.Clamp(value, 0, 1));
    }

    public bool IsBrowseSearchActive
    {
        get => _isBrowseSearchActive;
        private set
        {
            if (!SetProperty(ref _isBrowseSearchActive, value))
            {
                return;
            }

            OnPropertyChanged(nameof(BrowseJournals));
            OnPropertyChanged(nameof(BrowseResultSummary));
        }
    }

    public IEnumerable<ManagedJournalSummary> BrowseJournals => SearchResults;

    public string BrowseResultSummary => $"Periode {SearchPeriodMonth:MM/yyyy}: {SearchResults.Count} jurnal.";

    public bool HasBrowseResults => BrowseJournals.Any();

    public bool HasNoBrowseResults => !HasBrowseResults;

    public string BrowseEmptyStateTitle => "Tidak ada jurnal yang cocok";

    public string BrowseEmptyStateDescription => $"Tidak ada jurnal pada periode {SearchPeriodMonth:MM/yyyy}. Pilih periode lain lalu coba lagi.";

    public bool IsJournalPlaceholderSelected => false;

    public string JournalWorkspaceTitle => "Jurnal";

    public string JournalWorkspaceSubtitle => "Kelola editor, daftar, dan proses batch jurnal.";

    public string JournalEditorTitle => "Editor Jurnal";

    public string JournalEditorSubtitle => "Isi header jurnal, lengkapi baris akun, lalu simpan draft atau ajukan.";

    public string JournalPlaceholderTitle => "Fitur Jurnal";

    public string JournalPlaceholderDescription => "Fitur ini belum tersedia.";

    public bool CanPullInventoryJournals => !IsBusy && _canPullInventoryJournals && HasValidInventoryPullLocationScope;

    public string InventoryPullTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canPullInventoryJournals
                ? "Anda tidak memiliki izin menarik jurnal inventory."
                : !HasValidInventoryPullLocationScope
                    ? "Pilih Konteks Kerja dengan lokasi valid sebelum menarik jurnal inventory."
                    : "Tarik event inventory untuk lokasi aktif pada Konteks Kerja user menjadi draft jurnal accounting.";

    public bool HasPulledInventoryDrafts => InventoryPullCreatedJournalNos.Count > 0;

    public bool HasExportPeriodPreview => ExportPeriodPreviewLines.Count > 0;

    public bool CanOpenSelectedJournal => !IsBusy && (SelectedBrowseJournal ?? SelectedJournal) is not null;

    private long EffectiveSearchCompanyId => _dashboardSearchCompanyId ?? _companyId;

    private long EffectiveSearchLocationId => _dashboardSearchLocationId ?? _locationId;

    public ICommand ResetBrowseFiltersCommand { get; }

    private void RaiseAllJournalActionStateChanged()
    {
        RaiseJournalActionAvailabilityChanged(includeSaveDraft: true);
        RaiseJournalActionTooltipChanged(includeSaveDraft: true);
    }

    private void RaiseSubmitApprovePostStateChanged()
    {
        RaiseJournalActionAvailabilityChanged(includeSaveDraft: false);
        RaiseJournalActionTooltipChanged(includeSaveDraft: false);
    }

    private void RaiseJournalActionAvailabilityChanged(bool includeSaveDraft)
    {
        OnPropertyChanged(nameof(CanCreateNewJournal));
        if (includeSaveDraft)
        {
            OnPropertyChanged(nameof(CanSaveDraft));
        }

        OnPropertyChanged(nameof(CanSubmitCurrentJournal));
        OnPropertyChanged(nameof(CanApproveCurrentJournal));
        OnPropertyChanged(nameof(CanPostCurrentJournal));
        OnPropertyChanged(nameof(CanBrowseImportFile));
        OnPropertyChanged(nameof(CanPreviewImportFile));
        OnPropertyChanged(nameof(CanCommitImportDrafts));
        OnPropertyChanged(nameof(CanExportCurrentJournal));
        OnPropertyChanged(nameof(CanExportAnyJournal));
        OnPropertyChanged(nameof(CanPreviewExportPeriod));
        OnPropertyChanged(nameof(CanExportPeriod));
        OnPropertyChanged(nameof(CanPullInventoryJournals));
        RaiseJournalCommandCanExecuteChanged();
    }

    private void RaiseJournalActionTooltipChanged(bool includeSaveDraft)
    {
        OnPropertyChanged(nameof(NewJournalTooltip));
        if (includeSaveDraft)
        {
            OnPropertyChanged(nameof(SaveDraftTooltip));
        }

        OnPropertyChanged(nameof(SubmitTooltip));
        OnPropertyChanged(nameof(ApproveTooltip));
        OnPropertyChanged(nameof(PostTooltip));
        OnPropertyChanged(nameof(BrowseImportTooltip));
        OnPropertyChanged(nameof(PreviewImportTooltip));
        OnPropertyChanged(nameof(CommitImportTooltip));
        OnPropertyChanged(nameof(ExportCurrentTooltip));
        OnPropertyChanged(nameof(PreviewExportPeriodTooltip));
        OnPropertyChanged(nameof(ExportPeriodTooltip));
        OnPropertyChanged(nameof(ExportSelectedTooltip));
        OnPropertyChanged(nameof(InventoryPullTooltip));
    }

    private bool CanExportSelectedJournals(object? parameter)
    {
        return !IsBusy &&
               _canExportJournals &&
               ResolveSelectedJournals(parameter).Count > 0;
    }

    private void RaiseImportExportStateChanged()
    {
        RaiseJournalActionAvailabilityChanged(includeSaveDraft: false);
        RaiseJournalActionTooltipChanged(includeSaveDraft: false);
    }

    private void RaiseJournalCommandCanExecuteChanged()
    {
        _newJournalCommand.RaiseCanExecuteChanged();
        _saveDraftCommand.RaiseCanExecuteChanged();
        _submitCommand.RaiseCanExecuteChanged();
        _approveCommand.RaiseCanExecuteChanged();
        _postCommand.RaiseCanExecuteChanged();
        _openSelectedJournalCommand.RaiseCanExecuteChanged();
        _previousSearchPeriodCommand.RaiseCanExecuteChanged();
        _nextSearchPeriodCommand.RaiseCanExecuteChanged();
        RaiseImportExportCommandCanExecuteChanged();
    }

    private void RaiseImportExportCommandCanExecuteChanged()
    {
        _browseImportFileCommand.RaiseCanExecuteChanged();
        _previewImportCommand.RaiseCanExecuteChanged();
        _commitImportCommand.RaiseCanExecuteChanged();
        _pullInventoryJournalsCommand.RaiseCanExecuteChanged();
        _loadPulledDraftsToSearchCommand.RaiseCanExecuteChanged();
        _openPulledDraftJournalCommand.RaiseCanExecuteChanged();
        _exportCurrentCommand.RaiseCanExecuteChanged();
        _exportSelectedCommand.RaiseCanExecuteChanged();
        _previewExportPeriodCommand.RaiseCanExecuteChanged();
        _exportPeriodCommand.RaiseCanExecuteChanged();
    }

    private bool HasAnyBrowseFilters =>
        SearchPeriodMonth != default ||
        SearchDateFrom.HasValue ||
        SearchDateTo.HasValue ||
        !string.IsNullOrWhiteSpace(SearchStatus) ||
        !string.IsNullOrWhiteSpace(SearchKeyword);

    private bool HasAnyAutoBrowseFilters =>
        SearchPeriodMonth != default ||
        SearchDateFrom.HasValue ||
        SearchDateTo.HasValue ||
        !string.IsNullOrWhiteSpace(SearchStatus);

    private void OnBrowseFilterChanged(bool autoSearchRequested)
    {
        if (_suppressBrowseFilterAutoSearch)
        {
            return;
        }

        if (!HasAnyBrowseFilters)
        {
            if (IsBrowseSearchActive)
            {
                ResetBrowseFilters(silent: true);
            }

            return;
        }

        if (autoSearchRequested && HasAnyAutoBrowseFilters)
        {
            _ = SearchAsync();
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded)
        {
            return;
        }

        await LoadWorkspaceAsync();
    }

    private static IReadOnlyCollection<KeyValuePair<int, string>> BuildExportMonthOptions()
    {
        return new List<KeyValuePair<int, string>>
        {
            new(1, "Januari"),
            new(2, "Februari"),
            new(3, "Maret"),
            new(4, "April"),
            new(5, "Mei"),
            new(6, "Juni"),
            new(7, "Juli"),
            new(8, "Agustus"),
            new(9, "September"),
            new(10, "Oktober"),
            new(11, "November"),
            new(12, "Desember")
        };
    }

    private static IReadOnlyCollection<int> BuildExportYearOptions()
    {
        var currentYear = DateTime.Today.Year;
        var years = new List<int>();
        for (var year = currentYear; year >= 2000; year--)
        {
            years.Add(year);
        }

        return years;
    }

    private void SyncSearchPeriodCalendarState()
    {
        var monthNumber = SearchPeriodMonth.Month;
        if (_searchPeriodMonthNumber != monthNumber)
        {
            _searchPeriodMonthNumber = monthNumber;
            OnPropertyChanged(nameof(SearchPeriodMonthNumber));
        }

        var year = SearchPeriodMonth.Year;
        if (_searchPeriodYear != year)
        {
            _searchPeriodYear = year;
            OnPropertyChanged(nameof(SearchPeriodYear));
        }
    }

    private void ApplySearchPeriodCalendarSelection()
    {
        var normalized = new DateTime(NormalizeSearchPeriodYear(SearchPeriodYear), Math.Clamp(SearchPeriodMonthNumber, 1, 12), 1);
        if (normalized == SearchPeriodMonth)
        {
            return;
        }

        SearchPeriodMonth = normalized;
    }

    private void MoveToPreviousSearchPeriod()
    {
        if (!CanMoveToPreviousSearchPeriod)
        {
            return;
        }

        SearchPeriodMonth = SearchPeriodMonth.AddMonths(-1);
    }

    private void MoveToNextSearchPeriod()
    {
        if (!CanMoveToNextSearchPeriod)
        {
            return;
        }

        SearchPeriodMonth = SearchPeriodMonth.AddMonths(1);
    }

    private int NormalizeSearchPeriodYear(int value)
    {
        if (ExportYearOptions.Count == 0)
        {
            return value <= 0 ? DateTime.Today.Year : value;
        }

        var minimumYear = ExportYearOptions.Min();
        var maximumYear = ExportYearOptions.Max();
        if (value < minimumYear)
        {
            return minimumYear;
        }

        if (value > maximumYear)
        {
            return maximumYear;
        }

        return value;
    }

    public void NavigateToJournalScenario(string? subCode)
    {
        _dashboardSearchCompanyId = null;
        _dashboardSearchLocationId = null;

        var normalized = string.IsNullOrWhiteSpace(subCode)
            ? "jurnal_umum"
            : subCode.Trim().ToLowerInvariant();

        _selectedJournalScenarioCode = normalized;
        switch (normalized)
        {
            case "posting_jurnal":
                SelectedJournalTabIndex = 1;
                break;

            case "jurnal_belum_posting":
                SelectedJournalTabIndex = 1;
                SearchPeriodMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                SearchStatus = "UNPOSTED";
                SearchKeyword = string.Empty;
                SearchDateFrom = null;
                SearchDateTo = null;
                break;

            default:
                SelectedJournalTabIndex = 0;
                break;
        }

        OnPropertyChanged(nameof(IsJournalPlaceholderSelected));
        OnPropertyChanged(nameof(JournalWorkspaceTitle));
        OnPropertyChanged(nameof(JournalWorkspaceSubtitle));
        OnPropertyChanged(nameof(JournalEditorTitle));
        OnPropertyChanged(nameof(JournalEditorSubtitle));
        OnPropertyChanged(nameof(JournalPlaceholderTitle));
        OnPropertyChanged(nameof(JournalPlaceholderDescription));
    }

}

