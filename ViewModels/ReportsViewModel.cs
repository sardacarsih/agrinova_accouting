using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class BalanceSheetTreeNode : ViewModelBase
{
    public string Section { get; init; } = string.Empty;

    public string AccountCode { get; init; } = string.Empty;

    public string AccountName { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public int Level { get; init; }

    public bool IsPosting { get; init; } = true;

    public ObservableCollection<BalanceSheetTreeNode> Children { get; } = new();
}

public sealed partial class ReportsViewModel : ViewModelBase
{
    private readonly IAccessControlService _accessControlService;
    private readonly FinancialReportXlsxService _xlsxService;
    private readonly RelayCommand _exportCommand;
    private readonly long _companyId;
    private readonly long _locationId;
    private readonly string _actorUsername;
    private readonly bool _canViewReports;
    private readonly bool _canExportReports;

    private bool _isLoaded;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private DateTime _periodMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private decimal _totalDebit;
    private decimal _totalCredit;
    private decimal _totalRevenue;
    private decimal _totalExpense;
    private decimal _totalAssets;
    private decimal _totalLiabilities;
    private decimal _totalEquity;
    private string _selectedGeneralLedgerAccountCode = string.Empty;
    private string _generalLedgerKeyword = string.Empty;
    private decimal _generalLedgerTotalDebit;
    private decimal _generalLedgerTotalCredit;
    private decimal _subLedgerTotalDebit;
    private decimal _subLedgerTotalCredit;
    private decimal _cashFlowTotalOpening;
    private decimal _cashFlowTotalIn;
    private decimal _cashFlowTotalOut;
    private decimal _cashFlowTotalEnding;
    private decimal _accountMutationTotalOpening;
    private decimal _accountMutationTotalDebit;
    private decimal _accountMutationTotalCredit;
    private decimal _accountMutationTotalEnding;
    private int _selectedReportTabIndex;
    private string _selectedReportSubCode = "trial_balance";
    private long? _dashboardCompanyId;
    private long? _dashboardLocationId;

    public ReportsViewModel(
        IAccessControlService accessControlService,
        UserAccessContext accessContext,
        long companyId,
        long locationId)
    {
        _accessControlService = accessControlService;
        _xlsxService = new FinancialReportXlsxService();
        _companyId = companyId;
        _locationId = locationId;
        _actorUsername = string.IsNullOrWhiteSpace(accessContext.Username) ? "SYSTEM" : accessContext.Username.Trim();
        _canViewReports = accessContext.HasAction("accounting", "reports", "view");
        _canExportReports = accessContext.HasAction("accounting", "reports", "export");

        TrialBalanceRows = new ObservableCollection<ManagedTrialBalanceRow>();
        ProfitLossRows = new ObservableCollection<ManagedProfitLossRow>();
        BalanceSheetRows = new ObservableCollection<ManagedBalanceSheetRow>();
        GeneralLedgerRows = new ObservableCollection<ManagedGeneralLedgerRow>();
        SubLedgerRows = new ObservableCollection<ManagedSubLedgerRow>();
        CashFlowRows = new ObservableCollection<ManagedCashFlowRow>();
        AccountMutationRows = new ObservableCollection<ManagedAccountMutationRow>();
        GeneralLedgerAccountOptions = new ObservableCollection<ManagedAccount>();
        BalanceSheetTreeRoots = new ObservableCollection<BalanceSheetTreeNode>();

        RefreshCommand = new RelayCommand(() => _ = LoadReportsAsync());
        ApplyGeneralLedgerFilterCommand = new RelayCommand(() => _ = LoadReportsAsync());
        _exportCommand = new RelayCommand(ExportReports, () => CanExportReports);
        ExportCommand = _exportCommand;
        PreviousMonthCommand = new RelayCommand(() =>
        {
            PeriodMonth = PeriodMonth.AddMonths(-1);
            _ = LoadReportsAsync();
        });
        NextMonthCommand = new RelayCommand(() =>
        {
            PeriodMonth = PeriodMonth.AddMonths(1);
            _ = LoadReportsAsync();
        });

        NavigateToReportSubmenu("trial_balance");
    }

    public ObservableCollection<ManagedTrialBalanceRow> TrialBalanceRows { get; }

    public ObservableCollection<ManagedProfitLossRow> ProfitLossRows { get; }

    public ObservableCollection<ManagedBalanceSheetRow> BalanceSheetRows { get; }

    public ObservableCollection<ManagedGeneralLedgerRow> GeneralLedgerRows { get; }

    public ObservableCollection<ManagedSubLedgerRow> SubLedgerRows { get; }

    public ObservableCollection<ManagedCashFlowRow> CashFlowRows { get; }

    public ObservableCollection<ManagedAccountMutationRow> AccountMutationRows { get; }

    public ObservableCollection<ManagedAccount> GeneralLedgerAccountOptions { get; }

    public ObservableCollection<BalanceSheetTreeNode> BalanceSheetTreeRoots { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ApplyGeneralLedgerFilterCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand PreviousMonthCommand { get; }

    public ICommand NextMonthCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanExportReports));
            OnPropertyChanged(nameof(ExportTooltip));
            _exportCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public DateTime PeriodMonth
    {
        get => _periodMonth;
        set
        {
            var monthStart = new DateTime(value.Year, value.Month, 1);
            SetProperty(ref _periodMonth, monthStart);
        }
    }

    public decimal TotalDebit
    {
        get => _totalDebit;
        private set => SetProperty(ref _totalDebit, value);
    }

    public decimal TotalCredit
    {
        get => _totalCredit;
        private set => SetProperty(ref _totalCredit, value);
    }

    public decimal DifferenceAmount => TotalDebit - TotalCredit;

    public decimal TotalRevenue
    {
        get => _totalRevenue;
        private set => SetProperty(ref _totalRevenue, value);
    }

    public decimal TotalExpense
    {
        get => _totalExpense;
        private set => SetProperty(ref _totalExpense, value);
    }

    public decimal NetIncome => TotalRevenue - TotalExpense;

    public decimal TotalAssets
    {
        get => _totalAssets;
        private set => SetProperty(ref _totalAssets, value);
    }

    public decimal TotalLiabilities
    {
        get => _totalLiabilities;
        private set => SetProperty(ref _totalLiabilities, value);
    }

    public decimal TotalEquity
    {
        get => _totalEquity;
        private set => SetProperty(ref _totalEquity, value);
    }

    public decimal BalanceSheetDifference => TotalAssets - (TotalLiabilities + TotalEquity);

    public bool IsGeneralLedgerMode =>
        string.Equals(_selectedReportSubCode, "buku_besar", StringComparison.OrdinalIgnoreCase);

    public bool IsSubLedgerMode =>
        string.Equals(_selectedReportSubCode, "sub_ledger", StringComparison.OrdinalIgnoreCase);

    public bool IsCashFlowMode =>
        string.Equals(_selectedReportSubCode, "laporan_arus_kas", StringComparison.OrdinalIgnoreCase);

    public bool IsAccountMutationMode =>
        string.Equals(_selectedReportSubCode, "mutasi_akun", StringComparison.OrdinalIgnoreCase);

    public bool IsTrialBalanceMode => !IsGeneralLedgerMode && !IsSubLedgerMode && !IsAccountMutationMode && !IsCashFlowMode;

    public bool IsInquiryFilterMode => IsGeneralLedgerMode || IsSubLedgerMode || IsAccountMutationMode;

    public bool CanExportReports => !IsBusy && _canExportReports;

    private long EffectiveCompanyId => _dashboardCompanyId ?? _companyId;

    private long EffectiveLocationId => _dashboardLocationId ?? _locationId;

    public string ExportTooltip =>
        IsBusy
            ? "Sedang memproses data. Tunggu hingga selesai."
            : !_canExportReports
                ? "Anda tidak memiliki izin export laporan."
                : "Export laporan aktif ke Excel.";

    public string SelectedGeneralLedgerAccountCode
    {
        get => _selectedGeneralLedgerAccountCode;
        set => SetProperty(ref _selectedGeneralLedgerAccountCode, (value ?? string.Empty).Trim().ToUpperInvariant());
    }

    public string GeneralLedgerKeyword
    {
        get => _generalLedgerKeyword;
        set => SetProperty(ref _generalLedgerKeyword, value ?? string.Empty);
    }

    public decimal GeneralLedgerTotalDebit
    {
        get => _generalLedgerTotalDebit;
        private set => SetProperty(ref _generalLedgerTotalDebit, value);
    }

    public decimal GeneralLedgerTotalCredit
    {
        get => _generalLedgerTotalCredit;
        private set => SetProperty(ref _generalLedgerTotalCredit, value);
    }

    public decimal GeneralLedgerNetBalance => GeneralLedgerTotalDebit - GeneralLedgerTotalCredit;

    public decimal SubLedgerTotalDebit
    {
        get => _subLedgerTotalDebit;
        private set => SetProperty(ref _subLedgerTotalDebit, value);
    }

    public decimal SubLedgerTotalCredit
    {
        get => _subLedgerTotalCredit;
        private set => SetProperty(ref _subLedgerTotalCredit, value);
    }

    public decimal SubLedgerNetBalance => SubLedgerTotalDebit - SubLedgerTotalCredit;

    public decimal CashFlowTotalOpening
    {
        get => _cashFlowTotalOpening;
        private set => SetProperty(ref _cashFlowTotalOpening, value);
    }

    public decimal CashFlowTotalIn
    {
        get => _cashFlowTotalIn;
        private set => SetProperty(ref _cashFlowTotalIn, value);
    }

    public decimal CashFlowTotalOut
    {
        get => _cashFlowTotalOut;
        private set => SetProperty(ref _cashFlowTotalOut, value);
    }

    public decimal CashFlowTotalEnding
    {
        get => _cashFlowTotalEnding;
        private set => SetProperty(ref _cashFlowTotalEnding, value);
    }

    public decimal CashFlowNetChange => CashFlowTotalIn - CashFlowTotalOut;

    public decimal AccountMutationTotalOpening
    {
        get => _accountMutationTotalOpening;
        private set => SetProperty(ref _accountMutationTotalOpening, value);
    }

    public decimal AccountMutationTotalDebit
    {
        get => _accountMutationTotalDebit;
        private set => SetProperty(ref _accountMutationTotalDebit, value);
    }

    public decimal AccountMutationTotalCredit
    {
        get => _accountMutationTotalCredit;
        private set => SetProperty(ref _accountMutationTotalCredit, value);
    }

    public decimal AccountMutationTotalEnding
    {
        get => _accountMutationTotalEnding;
        private set => SetProperty(ref _accountMutationTotalEnding, value);
    }

    public decimal AccountMutationNetMovement => AccountMutationTotalDebit - AccountMutationTotalCredit;

    public int SelectedReportTabIndex
    {
        get => _selectedReportTabIndex;
        set => SetProperty(ref _selectedReportTabIndex, Math.Clamp(value, 0, 4));
    }

    public bool IsReportPlaceholderSelected =>
        string.Equals(_selectedReportSubCode, "laporan_kustom", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_selectedReportSubCode, "setup_anggaran", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_selectedReportSubCode, "realisasi_vs_anggaran", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_selectedReportSubCode, "kontrol_anggaran", StringComparison.OrdinalIgnoreCase);

    public string ReportPlaceholderTitle => _selectedReportSubCode switch
    {
        "laporan_kustom" => "Laporan Kustom",
        "setup_anggaran" => "Setup Anggaran",
        "realisasi_vs_anggaran" => "Realisasi vs Anggaran",
        "kontrol_anggaran" => "Kontrol Anggaran",
        _ => "Laporan"
    };

    public string ReportPlaceholderDescription => _selectedReportSubCode switch
    {
        "laporan_kustom" => "Menu ini sudah tersedia pada struktur baru. Builder laporan kustom akan ditambahkan pada iterasi berikutnya.",
        "setup_anggaran" => "Menu ini sudah tersedia pada struktur baru. Setup anggaran akan ditambahkan pada iterasi berikutnya.",
        "realisasi_vs_anggaran" => "Menu ini sudah tersedia pada struktur baru. Laporan realisasi vs anggaran akan ditambahkan pada iterasi berikutnya.",
        "kontrol_anggaran" => "Menu ini sudah tersedia pada struktur baru. Kontrol anggaran akan ditambahkan pada iterasi berikutnya.",
        _ => "Fitur ini belum tersedia."
    };

    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded)
        {
            return;
        }

        await LoadReportsAsync();
    }

    public void NavigateToReportSubmenu(string? subCode)
    {
        _dashboardCompanyId = null;
        _dashboardLocationId = null;

        var normalized = string.IsNullOrWhiteSpace(subCode)
            ? "trial_balance"
            : subCode.Trim().ToLowerInvariant();

        _selectedReportSubCode = normalized;
        SelectedReportTabIndex = normalized switch
        {
            "laporan_laba_rugi" => 1,
            "laporan_neraca" => 2,
            "laporan_arus_kas" => 3,
            "laporan_kustom" => 4,
            "trial_balance" => 0,
            "buku_besar" => 0,
            "sub_ledger" => 0,
            "mutasi_akun" => 0,
            "setup_anggaran" => 0,
            "realisasi_vs_anggaran" => 0,
            "kontrol_anggaran" => 0,
            _ => 0
        };

        OnPropertyChanged(nameof(IsGeneralLedgerMode));
        OnPropertyChanged(nameof(IsSubLedgerMode));
        OnPropertyChanged(nameof(IsCashFlowMode));
        OnPropertyChanged(nameof(IsAccountMutationMode));
        OnPropertyChanged(nameof(IsTrialBalanceMode));
        OnPropertyChanged(nameof(IsInquiryFilterMode));
        OnPropertyChanged(nameof(IsReportPlaceholderSelected));
        OnPropertyChanged(nameof(ReportPlaceholderTitle));
        OnPropertyChanged(nameof(ReportPlaceholderDescription));

        _ = LoadReportsAsync();
    }

    private async Task LoadReportsAsync()
    {
        if (!_canViewReports)
        {
            StatusMessage = "Anda tidak memiliki izin melihat laporan accounting.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Memuat laporan keuangan...";

            var trialBalanceTask = _accessControlService.GetTrialBalanceAsync(EffectiveCompanyId, EffectiveLocationId, PeriodMonth, _actorUsername);
            var profitLossTask = _accessControlService.GetProfitLossAsync(EffectiveCompanyId, EffectiveLocationId, PeriodMonth, _actorUsername);
            var balanceSheetTask = _accessControlService.GetBalanceSheetAsync(EffectiveCompanyId, EffectiveLocationId, PeriodMonth, _actorUsername);
            var accountOptionsTask = IsInquiryFilterMode
                ? _accessControlService.GetAccountsAsync(EffectiveCompanyId, actorUsername: _actorUsername)
                : Task.FromResult(new List<ManagedAccount>());
            var generalLedgerTask = IsGeneralLedgerMode
                ? _accessControlService.GetGeneralLedgerAsync(
                    EffectiveCompanyId,
                    EffectiveLocationId,
                    PeriodMonth,
                    SelectedGeneralLedgerAccountCode,
                    GeneralLedgerKeyword,
                    _actorUsername)
                : Task.FromResult(new List<ManagedGeneralLedgerRow>());
            var subLedgerTask = IsSubLedgerMode
                ? _accessControlService.GetSubLedgerAsync(
                    EffectiveCompanyId,
                    EffectiveLocationId,
                    PeriodMonth,
                    SelectedGeneralLedgerAccountCode,
                    GeneralLedgerKeyword,
                    _actorUsername)
                : Task.FromResult(new List<ManagedSubLedgerRow>());
            var cashFlowTask = IsCashFlowMode
                ? _accessControlService.GetCashFlowAsync(EffectiveCompanyId, EffectiveLocationId, PeriodMonth, _actorUsername)
                : Task.FromResult(new List<ManagedCashFlowRow>());
            var accountMutationTask = IsAccountMutationMode
                ? _accessControlService.GetAccountMutationAsync(
                    EffectiveCompanyId,
                    EffectiveLocationId,
                    PeriodMonth,
                    SelectedGeneralLedgerAccountCode,
                    GeneralLedgerKeyword,
                    _actorUsername)
                : Task.FromResult(new List<ManagedAccountMutationRow>());

            await Task.WhenAll(
                trialBalanceTask,
                profitLossTask,
                balanceSheetTask,
                accountOptionsTask,
                generalLedgerTask,
                subLedgerTask,
                cashFlowTask,
                accountMutationTask);

            var trialBalanceRows = trialBalanceTask.Result;
            var profitLossRows = profitLossTask.Result;
            var balanceSheetRows = balanceSheetTask.Result;
            var generalLedgerRows = generalLedgerTask.Result;
            var subLedgerRows = subLedgerTask.Result;
            var cashFlowRows = cashFlowTask.Result;
            var accountMutationRows = accountMutationTask.Result;

            ReplaceCollection(TrialBalanceRows, trialBalanceRows);
            ReplaceCollection(ProfitLossRows, profitLossRows);
            ReplaceCollection(BalanceSheetRows, balanceSheetRows);
            BuildBalanceSheetTree(balanceSheetRows);

            TotalDebit = trialBalanceRows.Sum(x => x.TotalDebit);
            TotalCredit = trialBalanceRows.Sum(x => x.TotalCredit);
            OnPropertyChanged(nameof(DifferenceAmount));

            TotalRevenue = profitLossRows
                .Where(x => string.Equals(x.Section, "Pendapatan", StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Amount);
            TotalExpense = profitLossRows
                .Where(x => string.Equals(x.Section, "Beban", StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Amount);
            OnPropertyChanged(nameof(NetIncome));

            TotalAssets = balanceSheetRows
                .Where(x => string.Equals(x.Section, "Aset", StringComparison.OrdinalIgnoreCase) && x.IsPosting)
                .Sum(x => x.Amount);
            TotalLiabilities = balanceSheetRows
                .Where(x => string.Equals(x.Section, "Kewajiban", StringComparison.OrdinalIgnoreCase) && x.IsPosting)
                .Sum(x => x.Amount);
            TotalEquity = balanceSheetRows
                .Where(x => string.Equals(x.Section, "Ekuitas", StringComparison.OrdinalIgnoreCase) && x.IsPosting)
                .Sum(x => x.Amount);
            OnPropertyChanged(nameof(BalanceSheetDifference));

            if (IsGeneralLedgerMode)
            {
                SyncGeneralLedgerAccountOptions(accountOptionsTask.Result);
                ReplaceCollection(GeneralLedgerRows, generalLedgerRows);
                SubLedgerRows.Clear();
                CashFlowRows.Clear();
                AccountMutationRows.Clear();
                GeneralLedgerTotalDebit = generalLedgerRows.Sum(x => x.Debit);
                GeneralLedgerTotalCredit = generalLedgerRows.Sum(x => x.Credit);
                OnPropertyChanged(nameof(GeneralLedgerNetBalance));
                SubLedgerTotalDebit = 0;
                SubLedgerTotalCredit = 0;
                OnPropertyChanged(nameof(SubLedgerNetBalance));
                CashFlowTotalOpening = 0;
                CashFlowTotalIn = 0;
                CashFlowTotalOut = 0;
                CashFlowTotalEnding = 0;
                OnPropertyChanged(nameof(CashFlowNetChange));
                AccountMutationTotalOpening = 0;
                AccountMutationTotalDebit = 0;
                AccountMutationTotalCredit = 0;
                AccountMutationTotalEnding = 0;
                OnPropertyChanged(nameof(AccountMutationNetMovement));
                StatusMessage = $"Buku Besar periode {PeriodMonth:yyyy-MM}: {GeneralLedgerRows.Count} baris.";
            }
            else if (IsSubLedgerMode)
            {
                SyncGeneralLedgerAccountOptions(accountOptionsTask.Result);
                GeneralLedgerRows.Clear();
                ReplaceCollection(SubLedgerRows, subLedgerRows);
                CashFlowRows.Clear();
                AccountMutationRows.Clear();
                GeneralLedgerTotalDebit = 0;
                GeneralLedgerTotalCredit = 0;
                OnPropertyChanged(nameof(GeneralLedgerNetBalance));
                SubLedgerTotalDebit = subLedgerRows.Sum(x => x.Debit);
                SubLedgerTotalCredit = subLedgerRows.Sum(x => x.Credit);
                OnPropertyChanged(nameof(SubLedgerNetBalance));
                CashFlowTotalOpening = 0;
                CashFlowTotalIn = 0;
                CashFlowTotalOut = 0;
                CashFlowTotalEnding = 0;
                OnPropertyChanged(nameof(CashFlowNetChange));
                AccountMutationTotalOpening = 0;
                AccountMutationTotalDebit = 0;
                AccountMutationTotalCredit = 0;
                AccountMutationTotalEnding = 0;
                OnPropertyChanged(nameof(AccountMutationNetMovement));
                StatusMessage = $"Sub Ledger periode {PeriodMonth:yyyy-MM}: {SubLedgerRows.Count} baris.";
            }
            else if (IsCashFlowMode)
            {
                GeneralLedgerRows.Clear();
                SubLedgerRows.Clear();
                ReplaceCollection(CashFlowRows, cashFlowRows);
                AccountMutationRows.Clear();
                GeneralLedgerTotalDebit = 0;
                GeneralLedgerTotalCredit = 0;
                OnPropertyChanged(nameof(GeneralLedgerNetBalance));
                SubLedgerTotalDebit = 0;
                SubLedgerTotalCredit = 0;
                OnPropertyChanged(nameof(SubLedgerNetBalance));
                CashFlowTotalOpening = cashFlowRows.Sum(x => x.OpeningBalance);
                CashFlowTotalIn = cashFlowRows.Sum(x => x.CashIn);
                CashFlowTotalOut = cashFlowRows.Sum(x => x.CashOut);
                CashFlowTotalEnding = cashFlowRows.Sum(x => x.EndingBalance);
                OnPropertyChanged(nameof(CashFlowNetChange));
                AccountMutationTotalOpening = 0;
                AccountMutationTotalDebit = 0;
                AccountMutationTotalCredit = 0;
                AccountMutationTotalEnding = 0;
                OnPropertyChanged(nameof(AccountMutationNetMovement));
                StatusMessage = $"Arus Kas periode {PeriodMonth:yyyy-MM}: {CashFlowRows.Count} akun kas/bank.";
            }
            else if (IsAccountMutationMode)
            {
                SyncGeneralLedgerAccountOptions(accountOptionsTask.Result);
                GeneralLedgerRows.Clear();
                SubLedgerRows.Clear();
                CashFlowRows.Clear();
                ReplaceCollection(AccountMutationRows, accountMutationRows);
                GeneralLedgerTotalDebit = 0;
                GeneralLedgerTotalCredit = 0;
                OnPropertyChanged(nameof(GeneralLedgerNetBalance));
                SubLedgerTotalDebit = 0;
                SubLedgerTotalCredit = 0;
                OnPropertyChanged(nameof(SubLedgerNetBalance));
                CashFlowTotalOpening = 0;
                CashFlowTotalIn = 0;
                CashFlowTotalOut = 0;
                CashFlowTotalEnding = 0;
                OnPropertyChanged(nameof(CashFlowNetChange));
                AccountMutationTotalOpening = accountMutationRows.Sum(x => x.OpeningBalance);
                AccountMutationTotalDebit = accountMutationRows.Sum(x => x.MutationDebit);
                AccountMutationTotalCredit = accountMutationRows.Sum(x => x.MutationCredit);
                AccountMutationTotalEnding = accountMutationRows.Sum(x => x.EndingBalance);
                OnPropertyChanged(nameof(AccountMutationNetMovement));
                StatusMessage = $"Mutasi Akun periode {PeriodMonth:yyyy-MM}: {AccountMutationRows.Count} akun.";
            }
            else
            {
                GeneralLedgerRows.Clear();
                SubLedgerRows.Clear();
                CashFlowRows.Clear();
                AccountMutationRows.Clear();
                GeneralLedgerTotalDebit = 0;
                GeneralLedgerTotalCredit = 0;
                OnPropertyChanged(nameof(GeneralLedgerNetBalance));
                SubLedgerTotalDebit = 0;
                SubLedgerTotalCredit = 0;
                OnPropertyChanged(nameof(SubLedgerNetBalance));
                CashFlowTotalOpening = 0;
                CashFlowTotalIn = 0;
                CashFlowTotalOut = 0;
                CashFlowTotalEnding = 0;
                OnPropertyChanged(nameof(CashFlowNetChange));
                AccountMutationTotalOpening = 0;
                AccountMutationTotalDebit = 0;
                AccountMutationTotalCredit = 0;
                AccountMutationTotalEnding = 0;
                OnPropertyChanged(nameof(AccountMutationNetMovement));
                StatusMessage =
                    $"Laporan periode {PeriodMonth:yyyy-MM}: Neraca Saldo {TrialBalanceRows.Count} akun, Laba Rugi {ProfitLossRows.Count} akun, Neraca {BalanceSheetRows.Count} akun.";
            }

            _isLoaded = true;
        }
        catch (Exception)
        {
            StatusMessage = "Gagal memuat laporan keuangan.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ExportReports()
    {
        if (!CanExportReports)
        {
            StatusMessage = ExportTooltip;
            return;
        }

        if (IsGeneralLedgerMode)
        {
            if (GeneralLedgerRows.Count == 0)
            {
                StatusMessage = "Tidak ada data Buku Besar untuk diexport.";
                return;
            }

            var generalLedgerDialog = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DefaultExt = "xlsx",
                AddExtension = true,
                FileName = $"BUKU_BESAR_{PeriodMonth:yyyyMM}.xlsx"
            };

            if (generalLedgerDialog.ShowDialog() != true)
            {
                return;
            }

            _xlsxService.ExportGeneralLedger(
                generalLedgerDialog.FileName,
                PeriodMonth,
                GeneralLedgerRows,
                SelectedGeneralLedgerAccountCode,
                GeneralLedgerKeyword);
            StatusMessage = $"Export Buku Besar berhasil: {Path.GetFileName(generalLedgerDialog.FileName)}";
            return;
        }

        if (IsSubLedgerMode)
        {
            if (SubLedgerRows.Count == 0)
            {
                StatusMessage = "Tidak ada data Sub Ledger untuk diexport.";
                return;
            }

            var subLedgerDialog = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DefaultExt = "xlsx",
                AddExtension = true,
                FileName = $"SUB_LEDGER_{PeriodMonth:yyyyMM}.xlsx"
            };

            if (subLedgerDialog.ShowDialog() != true)
            {
                return;
            }

            _xlsxService.ExportSubLedger(
                subLedgerDialog.FileName,
                PeriodMonth,
                SubLedgerRows,
                SelectedGeneralLedgerAccountCode,
                GeneralLedgerKeyword);
            StatusMessage = $"Export Sub Ledger berhasil: {Path.GetFileName(subLedgerDialog.FileName)}";
            return;
        }

        if (IsCashFlowMode)
        {
            if (CashFlowRows.Count == 0)
            {
                StatusMessage = "Tidak ada data Arus Kas untuk diexport.";
                return;
            }

            var cashFlowDialog = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DefaultExt = "xlsx",
                AddExtension = true,
                FileName = $"ARUS_KAS_{PeriodMonth:yyyyMM}.xlsx"
            };

            if (cashFlowDialog.ShowDialog() != true)
            {
                return;
            }

            _xlsxService.ExportCashFlow(
                cashFlowDialog.FileName,
                PeriodMonth,
                CashFlowRows);
            StatusMessage = $"Export Arus Kas berhasil: {Path.GetFileName(cashFlowDialog.FileName)}";
            return;
        }

        if (IsAccountMutationMode)
        {
            if (AccountMutationRows.Count == 0)
            {
                StatusMessage = "Tidak ada data Mutasi Akun untuk diexport.";
                return;
            }

            var accountMutationDialog = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DefaultExt = "xlsx",
                AddExtension = true,
                FileName = $"MUTASI_AKUN_{PeriodMonth:yyyyMM}.xlsx"
            };

            if (accountMutationDialog.ShowDialog() != true)
            {
                return;
            }

            _xlsxService.ExportAccountMutation(
                accountMutationDialog.FileName,
                PeriodMonth,
                AccountMutationRows,
                SelectedGeneralLedgerAccountCode,
                GeneralLedgerKeyword);
            StatusMessage = $"Export Mutasi Akun berhasil: {Path.GetFileName(accountMutationDialog.FileName)}";
            return;
        }

        if (TrialBalanceRows.Count == 0 && ProfitLossRows.Count == 0 && BalanceSheetRows.Count == 0)
        {
            StatusMessage = "Tidak ada data laporan untuk diexport.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"LAPORAN_KEUANGAN_{PeriodMonth:yyyyMM}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _xlsxService.Export(dialog.FileName, PeriodMonth, TrialBalanceRows, ProfitLossRows, BalanceSheetRows);
        StatusMessage = $"Export laporan berhasil: {Path.GetFileName(dialog.FileName)}";
    }

    private void SyncGeneralLedgerAccountOptions(IReadOnlyCollection<ManagedAccount> accounts)
    {
        var previousSelected = SelectedGeneralLedgerAccountCode;
        GeneralLedgerAccountOptions.Clear();
        GeneralLedgerAccountOptions.Add(new ManagedAccount
        {
            Id = 0,
            CompanyId = EffectiveCompanyId,
            Code = string.Empty,
            Name = "Semua Akun",
            IsPosting = true,
            IsActive = true
        });

        foreach (var account in (accounts ?? Array.Empty<ManagedAccount>())
                     .Where(x => x.IsActive && x.IsPosting)
                     .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
        {
            GeneralLedgerAccountOptions.Add(account);
        }

        var selectedExists = GeneralLedgerAccountOptions
            .Any(x => string.Equals(x.Code, previousSelected, StringComparison.OrdinalIgnoreCase));
        SelectedGeneralLedgerAccountCode = selectedExists ? previousSelected : string.Empty;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void BuildBalanceSheetTree(IReadOnlyList<ManagedBalanceSheetRow> rows)
    {
        BalanceSheetTreeRoots.Clear();
        if (rows.Count == 0)
        {
            return;
        }

        var byCode = new Dictionary<string, BalanceSheetTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.AccountCode))
            {
                continue;
            }

            byCode[row.AccountCode] = new BalanceSheetTreeNode
            {
                Section = row.Section,
                AccountCode = row.AccountCode,
                AccountName = row.AccountName,
                Amount = row.Amount,
                Level = row.Level,
                IsPosting = row.IsPosting
            };
        }

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.AccountCode) || !byCode.TryGetValue(row.AccountCode, out var node))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.ParentAccountCode) &&
                byCode.TryGetValue(row.ParentAccountCode, out var parent))
            {
                parent.Children.Add(node);
                continue;
            }

            BalanceSheetTreeRoots.Add(node);
        }
    }
}

