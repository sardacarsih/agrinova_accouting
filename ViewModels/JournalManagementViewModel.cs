using System.Collections.ObjectModel;
using System.Collections;
using System.ComponentModel;
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

    private bool _isLoaded;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    private long _journalId;
    private string _journalNo = string.Empty;
    private DateTime _journalDate = DateTime.Today;
    private string _referenceNo = string.Empty;
    private string _journalDescription = string.Empty;
    private string _journalStatus = "DRAFT";
    private decimal _totalDebit;
    private decimal _totalCredit;

    private JournalLineEditor? _selectedInputLine;
    private ManagedJournalSummary? _selectedJournal;
    private ManagedJournalSummary? _selectedSearchJournal;

    private DateTime? _searchDateFrom;
    private DateTime? _searchDateTo;
    private string _searchKeyword = string.Empty;
    private string _searchStatus = string.Empty;

    private string _importFilePath = string.Empty;
    private string _importMessage = string.Empty;
    private DateTime _inventoryPullPeriodMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private int _exportPeriodMonth = DateTime.Today.Month;
    private int _exportPeriodYear = DateTime.Today.Year;
    private bool _exportPeriodUseLegacyFormat;
    private string _exportPeriodPreviewSummary = "Pilih periode lalu klik Tampilkan Detail.";
    private string _exportPeriodPreviewKey = string.Empty;
    private string _inventoryPullMessage = string.Empty;
    private int _selectedJournalTabIndex;
    private string _selectedJournalScenarioCode = "jurnal_umum";
    private readonly string _inventoryPullLocationLabel;
    private List<JournalImportBundleResult> _stagedImportBundles = new();
    private readonly Dictionary<string, ManagedAccount> _accountLookupByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<JournalLineEditor> _accountSyncInProgress = new();
    private readonly Dictionary<string, bool> _periodOpenByMonthKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _isCurrentPeriodOpen = true;
    private string _currentPeriodStatusText = "OPEN";
    private string _currentPeriodMonthText = DateTime.Today.ToString("yyyy-MM");

    public JournalManagementViewModel(
        IAccessControlService accessControlService,
        UserAccessContext accessContext,
        string? locationDisplayName = null)
    {
        _accessControlService = accessControlService;
        _actorUsername = string.IsNullOrWhiteSpace(accessContext.Username) ? "SYSTEM" : accessContext.Username.Trim();
        _companyId = accessContext.SelectedCompanyId;
        _locationId = accessContext.SelectedLocationId;
        _inventoryPullLocationLabel = string.IsNullOrWhiteSpace(locationDisplayName)
            ? $"Lokasi aktif (ID: {_locationId})"
            : locationDisplayName.Trim();
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
        ImportPreviewItems = new ObservableCollection<JournalImportPreviewItem>();
        InventoryPullCreatedJournalNos = new ObservableCollection<string>();
        InventoryPullCreatedJournalNos.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPulledInventoryDrafts));
        ExportPeriodPreviewJournals = new ObservableCollection<ManagedJournalSummary>();
        ExportPeriodPreviewLines = new ObservableCollection<JournalExportPreviewLine>();
        ExportPeriodPreviewLines.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasExportPeriodPreview));
            OnPropertyChanged(nameof(CanExportPeriod));
            OnPropertyChanged(nameof(ExportPeriodTooltip));
        };
        StatusFilterOptions = new ObservableCollection<string> { "", "DRAFT", "SUBMITTED", "APPROVED", "POSTED" };
        ExportMonthOptions = new ObservableCollection<KeyValuePair<int, string>>(BuildExportMonthOptions());
        ExportYearOptions = new ObservableCollection<int>(BuildExportYearOptions());

        if (!ExportYearOptions.Contains(_exportPeriodYear) && ExportYearOptions.Count > 0)
        {
            _exportPeriodYear = ExportYearOptions[0];
        }

        RefreshCommand = new RelayCommand(() => _ = LoadWorkspaceAsync());
        NewJournalCommand = new RelayCommand(NewJournal);
        AddLineCommand = new RelayCommand(AddLine);
        RemoveLineCommand = new RelayCommand(RemoveSelectedLine);
        RemoveLineByRowCommand = new RelayCommand(RemoveLineByRow);
        SaveDraftCommand = new RelayCommand(() => _ = SaveDraftAsync());
        SubmitCommand = new RelayCommand(() => _ = SubmitCurrentAsync());
        ApproveCommand = new RelayCommand(() => _ = ApproveCurrentAsync());
        PostCommand = new RelayCommand(() => _ = PostCurrentAsync());
        OpenSelectedJournalCommand = new RelayCommand(() => _ = OpenSelectedJournalAsync());
        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        OpenSelectedSearchCommand = new RelayCommand(() => _ = OpenSelectedSearchAsync());
        BrowseImportFileCommand = new RelayCommand(BrowseImportFile, () => CanBrowseImportFile);
        PreviewImportCommand = new RelayCommand(PreviewImport, () => CanPreviewImportFile);
        CommitImportCommand = new RelayCommand(() => _ = CommitImportAsync(), () => CanCommitImportDrafts);
        PullInventoryJournalsCommand = new RelayCommand(() => _ = PullInventoryJournalsAsync());
        LoadPulledDraftsToSearchCommand = new RelayCommand(() => _ = LoadPulledDraftsToSearchAsync());
        OpenPulledDraftJournalCommand = new RelayCommand(parameter => _ = OpenPulledDraftJournalAsync(parameter));
        ExportCurrentCommand = new RelayCommand(ExportCurrentJournal, () => CanExportCurrentJournal);
        ExportSelectedCommand = new RelayCommand(parameter => ExportSelectedJournal(parameter), parameter => CanExportAnyJournal);
        PreviewExportPeriodCommand = new RelayCommand(() => _ = PreviewExportPeriodAsync(), () => CanPreviewExportPeriod);
        ExportPeriodCommand = new RelayCommand(() => _ = ExportPeriodAsync(), () => CanExportPeriod);
        OpenAccountPickerCommand = new RelayCommand(OpenAccountPicker);

        NavigateToJournalScenario("jurnal_umum");
        NewJournal();
    }

    public ObservableCollection<ManagedAccount> Accounts { get; }

    public ObservableCollection<JournalLineEditor> InputLines { get; }

    public ObservableCollection<ManagedJournalSummary> JournalList { get; }

    public ObservableCollection<ManagedJournalSummary> SearchResults { get; }

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

    public ICommand SearchCommand { get; }

    public ICommand OpenSelectedSearchCommand { get; }

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
        set
        {
            var previousMonthStart = new DateTime(_journalDate.Year, _journalDate.Month, 1);
            var newValue = value.Date;
            if (!SetProperty(ref _journalDate, newValue))
            {
                return;
            }

            var nextMonthStart = new DateTime(newValue.Year, newValue.Month, 1);
            if (previousMonthStart != nextMonthStart)
            {
                _ = RefreshPeriodStatusForDateAsync(newValue, reloadFromService: true);
                return;
            }

            UpdatePeriodStatusFromCache(newValue);
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

    public ManagedJournalSummary? SelectedJournal
    {
        get => _selectedJournal;
        set => SetProperty(ref _selectedJournal, value);
    }

    public ManagedJournalSummary? SelectedSearchJournal
    {
        get => _selectedSearchJournal;
        set => SetProperty(ref _selectedSearchJournal, value);
    }

    public DateTime? SearchDateFrom
    {
        get => _searchDateFrom;
        set => SetProperty(ref _searchDateFrom, value);
    }

    public DateTime? SearchDateTo
    {
        get => _searchDateTo;
        set => SetProperty(ref _searchDateTo, value);
    }

    public string SearchKeyword
    {
        get => _searchKeyword;
        set => SetProperty(ref _searchKeyword, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        set => SetProperty(ref _searchStatus, value);
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
        set => SetProperty(ref _inventoryPullPeriodMonth, new DateTime(value.Year, value.Month, 1));
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

    public string InventoryPullMessage
    {
        get => _inventoryPullMessage;
        private set => SetProperty(ref _inventoryPullMessage, value);
    }

    public int SelectedJournalTabIndex
    {
        get => _selectedJournalTabIndex;
        set => SetProperty(ref _selectedJournalTabIndex, Math.Clamp(value, 0, 4));
    }

    public bool IsJournalPlaceholderSelected =>
        string.Equals(_selectedJournalScenarioCode, "jurnal_penyesuaian", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_selectedJournalScenarioCode, "jurnal_penutup", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_selectedJournalScenarioCode, "jurnal_berulang", StringComparison.OrdinalIgnoreCase);

    public string JournalPlaceholderTitle => _selectedJournalScenarioCode switch
    {
        "jurnal_penyesuaian" => "Jurnal Penyesuaian",
        "jurnal_penutup" => "Jurnal Penutup",
        "jurnal_berulang" => "Jurnal Berulang",
        _ => "Fitur Jurnal"
    };

    public string JournalPlaceholderDescription => _selectedJournalScenarioCode switch
    {
        "jurnal_penyesuaian" => "Menu ini sudah tersedia pada struktur baru. Workflow jurnal penyesuaian terpisah akan ditambahkan pada iterasi berikutnya.",
        "jurnal_penutup" => "Menu ini sudah tersedia pada struktur baru. Workflow jurnal penutup terpisah akan ditambahkan pada iterasi berikutnya.",
        "jurnal_berulang" => "Menu ini sudah tersedia pada struktur baru. Workflow template dan auto-generate jurnal berulang akan ditambahkan pada iterasi berikutnya.",
        _ => "Fitur ini belum tersedia."
    };

    public bool CanPullInventoryJournals => !IsBusy && _canPullInventoryJournals;

    public string InventoryPullTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canPullInventoryJournals
                ? "Anda tidak memiliki izin menarik jurnal inventory."
                : "Tarik event inventory menjadi draft jurnal accounting.";

    public bool HasPulledInventoryDrafts => InventoryPullCreatedJournalNos.Count > 0;

    public bool HasExportPeriodPreview => ExportPeriodPreviewLines.Count > 0;

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

    public void NavigateToJournalScenario(string? subCode)
    {
        var normalized = string.IsNullOrWhiteSpace(subCode)
            ? "jurnal_umum"
            : subCode.Trim().ToLowerInvariant();

        _selectedJournalScenarioCode = normalized;
        switch (normalized)
        {
            case "import_jurnal":
                SelectedJournalTabIndex = 3;
                break;

            case "posting_jurnal":
                SelectedJournalTabIndex = 1;
                break;

            case "jurnal_belum_posting":
                SelectedJournalTabIndex = 2;
                SearchStatus = string.Empty;
                SearchKeyword = string.Empty;
                SearchDateFrom = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                SearchDateTo = DateTime.Today;
                break;

            default:
                SelectedJournalTabIndex = 0;
                break;
        }

        OnPropertyChanged(nameof(IsJournalPlaceholderSelected));
        OnPropertyChanged(nameof(JournalPlaceholderTitle));
        OnPropertyChanged(nameof(JournalPlaceholderDescription));
    }

}


