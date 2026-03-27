using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class AccountingDashboardViewModel : ViewModelBase, IDisposable
{
    private readonly IAccessControlService _accessControlService;
    private readonly FinancialReportXlsxService _xlsxService = new();
    private readonly UserAccessContext _accessContext;
    private readonly bool _canExportReports;
    private readonly bool _canViewReports;
    private readonly bool _canViewTransactions;
    private readonly RelayCommand _previousPeriodCommand;
    private readonly RelayCommand _nextPeriodCommand;
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<ManagedLocation> _availableLocations = new();
    private readonly List<ManagedAccountingPeriod> _availableAccountingPeriods = new();

    private CancellationTokenSource? _refreshCts;
    private AccountingDashboardData? _currentData;
    private ManagedCompany? _selectedCompany;
    private DashboardLocationOption? _selectedLocation;
    private DashboardGranularityOption? _selectedGranularity;
    private int _selectedPeriodMonth = DateTime.Today.Month;
    private int _selectedPeriodYear = DateTime.Today.Year;
    private DateTime _selectedPeriod = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _isUpdatingPeriodSelection;
    private bool _isBusy;
    private bool _isLoaded;
    private string _statusMessage = "Dashboard siap dimuat.";
    private DateTime _lastUpdatedAt;

    public AccountingDashboardViewModel(IAccessControlService accessControlService, UserAccessContext accessContext)
    {
        _accessControlService = accessControlService;
        _accessContext = accessContext;
        _canExportReports = accessContext.HasAction("accounting", "reports", "export");
        _canViewReports = accessContext.HasAction("accounting", "reports", "view");
        _canViewTransactions = accessContext.HasAction("accounting", "transactions", "view");
        Header = new DashboardHeaderSectionViewModel();
        Kpi = new DashboardKpiSectionViewModel();
        Charts = new DashboardChartsSectionViewModel();
        Gl = new DashboardGlSectionViewModel();
        Cash = new DashboardCashSectionViewModel();
        Inventory = new DashboardInventorySectionViewModel();
        Alerts = new DashboardAlertSectionViewModel();

        CompanyOptions = new ObservableCollection<ManagedCompany>();
        LocationOptions = new ObservableCollection<DashboardLocationOption>();
        GranularityOptions = new ObservableCollection<DashboardGranularityOption>
        {
            new(DashboardPeriodGranularity.Daily, "Daily"),
            new(DashboardPeriodGranularity.Monthly, "Monthly"),
            new(DashboardPeriodGranularity.Yearly, "Yearly")
        };
        MonthOptions = new ObservableCollection<KeyValuePair<int, string>>(BuildMonthOptions());
        YearOptions = new ObservableCollection<int>(BuildYearOptions());
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(forceReload: true));
        ApplyFilterCommand = new RelayCommand(() => _ = RefreshAsync(forceReload: true));
        ExportCommand = new RelayCommand(ExportDashboard, () => CanExport);
        _previousPeriodCommand = new RelayCommand(MoveToPreviousPeriod, () => CanMoveToPreviousPeriod);
        _nextPeriodCommand = new RelayCommand(MoveToNextPeriod, () => CanMoveToNextPeriod);
        SelectedGranularity = GranularityOptions.FirstOrDefault();
        SyncPeriodPickerFieldsFromSelectedPeriod();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync(forceReload: true);

        Header.UserDisplayName = string.IsNullOrWhiteSpace(accessContext.Username) ? "User" : accessContext.Username.Trim();
        Header.RoleDisplayName = string.IsNullOrWhiteSpace(accessContext.SelectedRoleCode)
            ? "-"
            : $"{accessContext.SelectedRoleCode} - {accessContext.SelectedRoleName}";
    }

    public DashboardHeaderSectionViewModel Header { get; }

    public DashboardKpiSectionViewModel Kpi { get; }

    public DashboardChartsSectionViewModel Charts { get; }

    public DashboardGlSectionViewModel Gl { get; }

    public DashboardCashSectionViewModel Cash { get; }

    public DashboardInventorySectionViewModel Inventory { get; }

    public DashboardAlertSectionViewModel Alerts { get; }

    public ObservableCollection<ManagedCompany> CompanyOptions { get; }

    public ObservableCollection<DashboardLocationOption> LocationOptions { get; }

    public ObservableCollection<DashboardGranularityOption> GranularityOptions { get; }

    public ObservableCollection<KeyValuePair<int, string>> MonthOptions { get; }

    public ObservableCollection<int> YearOptions { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ApplyFilterCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand PreviousPeriodCommand => _previousPeriodCommand;

    public ICommand NextPeriodCommand => _nextPeriodCommand;

    public event Action<DashboardDrillRequest>? DrillDownRequested;

    public ManagedCompany? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (!SetProperty(ref _selectedCompany, value))
            {
                return;
            }

            RefreshLocationOptions();
        }
    }

    public DashboardLocationOption? SelectedLocation
    {
        get => _selectedLocation;
        set
        {
            if (!SetProperty(ref _selectedLocation, value))
            {
                return;
            }

            _ = RefreshPeriodOptionsFromSourceAsync();
        }
    }

    public DashboardGranularityOption? SelectedGranularity
    {
        get => _selectedGranularity;
        set
        {
            if (!SetProperty(ref _selectedGranularity, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CashTodayInflowLabel));
            OnPropertyChanged(nameof(CashTodayOutflowLabel));
            OnPropertyChanged(nameof(CashPeriodInflowLabel));
            OnPropertyChanged(nameof(CashPeriodOutflowLabel));
            OnPropertyChanged(nameof(IsYearlyGranularity));
            SelectedPeriod = NormalizePeriod(_selectedPeriod, value?.Value ?? DashboardPeriodGranularity.Monthly);
            _ = RefreshPeriodOptionsFromSourceAsync();
        }
    }

    public int SelectedPeriodMonth
    {
        get => _selectedPeriodMonth;
        set
        {
            var normalized = Math.Clamp(value, 1, 12);
            if (!SetProperty(ref _selectedPeriodMonth, normalized))
            {
                return;
            }

            if (_isUpdatingPeriodSelection)
            {
                return;
            }

            ApplyPeriodPickerSelection();
        }
    }

    public int SelectedPeriodYear
    {
        get => _selectedPeriodYear;
        set
        {
            var normalized = NormalizeYear(value);
            if (!SetProperty(ref _selectedPeriodYear, normalized))
            {
                return;
            }

            if (_isUpdatingPeriodSelection)
            {
                return;
            }

            ApplyPeriodPickerSelection();
        }
    }

    public DateTime SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            var normalized = NormalizePeriod(value == default ? DateTime.Today : value.Date, SelectedGranularity?.Value ?? DashboardPeriodGranularity.Monthly);
            if (!SetProperty(ref _selectedPeriod, normalized))
            {
                return;
            }

            SyncPeriodPickerFieldsFromSelectedPeriod();
            UpdateSelectedAccountingPeriodState();
        }
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

            OnPropertyChanged(nameof(CanExport));
            if (ExportCommand is RelayCommand relayCommand)
            {
                relayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool CanExport => !IsBusy && _canExportReports && SelectedCompany is not null;

    public bool CanDrillToReports => _canViewReports;

    public bool CanDrillToTransactions => _canViewTransactions;

    public string LastUpdatedText => _lastUpdatedAt == default ? "-" : _lastUpdatedAt.ToString("dd MMM yyyy HH:mm:ss");

    public string CashTodayInflowLabel => SelectedGranularity?.Value == DashboardPeriodGranularity.Daily ? "Selected Day Inflow" : "Today Inflow";

    public string CashTodayOutflowLabel => SelectedGranularity?.Value == DashboardPeriodGranularity.Daily ? "Selected Day Outflow" : "Today Outflow";

    public string CashPeriodInflowLabel => SelectedGranularity?.Value switch
    {
        DashboardPeriodGranularity.Daily => "Day Inflow",
        DashboardPeriodGranularity.Yearly => "Year Inflow",
        _ => "Month Inflow"
    };

    public string CashPeriodOutflowLabel => SelectedGranularity?.Value switch
    {
        DashboardPeriodGranularity.Daily => "Day Outflow",
        DashboardPeriodGranularity.Yearly => "Year Outflow",
        _ => "Month Outflow"
    };

    public bool IsYearlyGranularity => SelectedGranularity?.Value == DashboardPeriodGranularity.Yearly;

    public string SelectedAccountingPeriodDisplayText
    {
        get
        {
            var periodMonth = new DateTime(SelectedPeriodYear, SelectedPeriodMonth, 1);
            var registered = _availableAccountingPeriods
                .FirstOrDefault(x => x.PeriodMonth.Year == periodMonth.Year && x.PeriodMonth.Month == periodMonth.Month);
            return new JournalAccountingPeriodOption(periodMonth, registered?.IsOpen ?? false, registered is not null).DisplayText;
        }
    }

    public bool CanMoveToPreviousPeriod => IsYearlyGranularity
        ? SelectedPeriodYear > YearOptions.DefaultIfEmpty(DateTime.Today.Year).Min()
        : SelectedPeriodYear > YearOptions.DefaultIfEmpty(DateTime.Today.Year).Min() || SelectedPeriodMonth > 1;

    public bool CanMoveToNextPeriod => IsYearlyGranularity
        ? SelectedPeriodYear < YearOptions.DefaultIfEmpty(DateTime.Today.Year).Max()
        : SelectedPeriodYear < YearOptions.DefaultIfEmpty(DateTime.Today.Year).Max() || SelectedPeriodMonth < 12;

    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded)
        {
            return;
        }

        await LoadFilterOptionsAsync();
        await RefreshPeriodOptionsFromSourceAsync();
        await RefreshAsync(forceReload: true);
        _isLoaded = true;
    }

    public void SetIsActive(bool isActive)
    {
        if (isActive)
        {
            if (!_isLoaded)
            {
                _ = EnsureLoadedAsync();
            }

            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }

            return;
        }

        _refreshTimer.Stop();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
    }

    public void RequestRevenueDrillDown(int index)
    {
        if (!_canViewReports || index < 0 || index >= Charts.RevenueExpensePoints.Count)
        {
            return;
        }

        var point = Charts.RevenueExpensePoints[index];
        DrillDownRequested?.Invoke(new DashboardDrillRequest
        {
            TargetModule = "reports",
            TargetSubCode = "laporan_laba_rugi",
            CompanyId = SelectedCompany?.Id ?? _accessContext.SelectedCompanyId,
            LocationId = SelectedLocation?.Id,
            PeriodStart = point.PeriodStart,
            PeriodGranularity = DashboardPeriodGranularity.Monthly
        });
    }

    public void RequestTrendDrillDown()
    {
        RequestRevenueDrillDown(Math.Max(0, Charts.RevenueExpensePoints.Count - 1));
    }

    public void RequestExpenseAccountDrillDown(int index)
    {
        if (!_canViewReports || index < 0 || index >= Charts.TopExpenseAccounts.Count)
        {
            return;
        }

        var point = Charts.TopExpenseAccounts[index];
        DrillDownRequested?.Invoke(new DashboardDrillRequest
        {
            TargetModule = "reports",
            TargetSubCode = "mutasi_akun",
            CompanyId = SelectedCompany?.Id ?? _accessContext.SelectedCompanyId,
            LocationId = SelectedLocation?.Id,
            PeriodStart = SelectedPeriod,
            PeriodGranularity = SelectedGranularity?.Value ?? DashboardPeriodGranularity.Monthly,
            AccountCode = point.AccountCode
        });
    }

    public void RequestExpenseAccountDrillDown(ExpenseAccountBarItem? item)
    {
        if (item is null)
        {
            return;
        }

        RequestExpenseAccountDrillDown(Charts.TopExpenseAccounts.IndexOf(item));
    }

    public void RequestAssetCompositionDrillDown(int index)
    {
        if (!_canViewReports || index < 0 || index >= Charts.AssetComposition.Count)
        {
            return;
        }

        DrillDownRequested?.Invoke(new DashboardDrillRequest
        {
            TargetModule = "reports",
            TargetSubCode = "laporan_neraca",
            CompanyId = SelectedCompany?.Id ?? _accessContext.SelectedCompanyId,
            LocationId = SelectedLocation?.Id,
            PeriodStart = SelectedPeriod,
            PeriodGranularity = SelectedGranularity?.Value ?? DashboardPeriodGranularity.Monthly
        });
    }

    public void RequestAssetDrillDown()
    {
        RequestAssetCompositionDrillDown(0);
    }

    public void ExecuteAlertAction(DashboardAlertRowViewModel? alert)
    {
        if (alert?.DrillRequest is null || !CanExecuteDrillRequest(alert.DrillRequest))
        {
            return;
        }

        DrillDownRequested?.Invoke(alert.DrillRequest);
    }

    public void OpenJournal(DashboardJournalItem journal)
    {
        if (!_canViewTransactions || journal is null)
        {
            return;
        }

        DrillDownRequested?.Invoke(new DashboardDrillRequest
        {
            TargetModule = "transactions",
            TargetSubCode = "jurnal_umum",
            CompanyId = SelectedCompany?.Id ?? _accessContext.SelectedCompanyId,
            LocationId = SelectedLocation?.Id,
            PeriodStart = SelectedPeriod,
            PeriodGranularity = SelectedGranularity?.Value ?? DashboardPeriodGranularity.Monthly,
            Keyword = journal.JournalNo
        });
    }

    private bool CanExecuteDrillRequest(DashboardDrillRequest request)
    {
        return request.TargetModule switch
        {
            "reports" => _canViewReports,
            "transactions" => _canViewTransactions,
            _ => true
        };
    }

    private async Task LoadFilterOptionsAsync()
    {
        try
        {
            var options = await _accessControlService.GetDashboardFilterOptionsAsync(_accessContext.UserId, _accessContext.SelectedRoleId);
            CompanyOptions.Clear();
            foreach (var company in options.Companies)
            {
                CompanyOptions.Add(company);
            }

            _availableLocations.Clear();
            _availableLocations.AddRange(options.Locations);

            SelectedCompany = CompanyOptions.FirstOrDefault(x => x.Id == (options.DefaultCompanyId ?? _accessContext.SelectedCompanyId))
                ?? CompanyOptions.FirstOrDefault(x => x.Id == _accessContext.SelectedCompanyId)
                ?? CompanyOptions.FirstOrDefault();

            RefreshLocationOptions(options.DefaultLocationId);
        }
        catch
        {
            CompanyOptions.Clear();
            CompanyOptions.Add(new ManagedCompany
            {
                Id = _accessContext.SelectedCompanyId,
                Code = _accessContext.SelectedCompanyCode,
                Name = _accessContext.SelectedCompanyName,
                IsActive = true
            });
            _availableLocations.Clear();
            SelectedCompany = CompanyOptions.FirstOrDefault();
            RefreshLocationOptions(_accessContext.SelectedLocationId);
        }
    }

    private void RefreshLocationOptions(long? preferredLocationId = null)
    {
        var previousLocationId = SelectedLocation?.Id;
        var previousCompanyId = SelectedLocation?.CompanyId;
        LocationOptions.Clear();

        if (SelectedCompany is null)
        {
            SelectedLocation = null;
            return;
        }

        LocationOptions.Add(new DashboardLocationOption(null, SelectedCompany.Id, "Semua Lokasi"));
        foreach (var location in BuildLocationOptionsForSelectedCompany())
        {
            LocationOptions.Add(location);
        }

        SelectedLocation = LocationOptions.FirstOrDefault(x => x.Id == preferredLocationId)
            ?? LocationOptions.FirstOrDefault(x => x.Id == _accessContext.SelectedLocationId && x.CompanyId == SelectedCompany.Id)
            ?? LocationOptions.FirstOrDefault();

        if (previousLocationId == SelectedLocation?.Id && previousCompanyId == SelectedLocation?.CompanyId)
        {
            _ = RefreshPeriodOptionsFromSourceAsync();
        }
    }

    private async Task RefreshPeriodOptionsFromSourceAsync()
    {
        _availableAccountingPeriods.Clear();

        var lookupLocationId = ResolvePeriodLookupLocationId();
        if (SelectedCompany is not null && lookupLocationId.HasValue && lookupLocationId.Value > 0)
        {
            try
            {
                var periods = await _accessControlService.GetAccountingPeriodsAsync(SelectedCompany.Id, lookupLocationId.Value);
                _availableAccountingPeriods.AddRange(periods.OrderByDescending(x => x.PeriodMonth));
            }
            catch
            {
                // Fallback to local option generation when accounting period lookup is unavailable.
            }
        }

        UpdateSelectedAccountingPeriodState();
    }

    private void UpdateSelectedAccountingPeriodState()
    {
        SyncPeriodPickerFieldsFromSelectedPeriod();
        OnPropertyChanged(nameof(SelectedAccountingPeriodDisplayText));
        OnPropertyChanged(nameof(CanMoveToPreviousPeriod));
        OnPropertyChanged(nameof(CanMoveToNextPeriod));
        _previousPeriodCommand?.RaiseCanExecuteChanged();
        _nextPeriodCommand?.RaiseCanExecuteChanged();
    }

    private void SyncPeriodPickerFieldsFromSelectedPeriod()
    {
        _isUpdatingPeriodSelection = true;
        try
        {
            var targetMonth = Math.Clamp(_selectedPeriod.Month, 1, 12);
            var targetYear = NormalizeYear(_selectedPeriod.Year);

            SetProperty(ref _selectedPeriodMonth, targetMonth, nameof(SelectedPeriodMonth));
            SetProperty(ref _selectedPeriodYear, targetYear, nameof(SelectedPeriodYear));
        }
        finally
        {
            _isUpdatingPeriodSelection = false;
        }
    }

    private void ApplyPeriodPickerSelection()
    {
        var granularity = SelectedGranularity?.Value ?? DashboardPeriodGranularity.Monthly;
        var nextPeriod = ComposeSelectedPeriod(granularity, SelectedPeriodYear, SelectedPeriodMonth, _selectedPeriod);
        if (_selectedPeriod != nextPeriod)
        {
            SetProperty(ref _selectedPeriod, nextPeriod, nameof(SelectedPeriod));
        }

        UpdateSelectedAccountingPeriodState();
    }

    private void MoveToPreviousPeriod()
    {
        if (!CanMoveToPreviousPeriod)
        {
            return;
        }

        if (IsYearlyGranularity)
        {
            SelectedPeriodYear -= 1;
            return;
        }

        if (SelectedPeriodMonth == 1)
        {
            SelectedPeriodMonth = 12;
            SelectedPeriodYear -= 1;
            return;
        }

        SelectedPeriodMonth -= 1;
    }

    private void MoveToNextPeriod()
    {
        if (!CanMoveToNextPeriod)
        {
            return;
        }

        if (IsYearlyGranularity)
        {
            SelectedPeriodYear += 1;
            return;
        }

        if (SelectedPeriodMonth == 12)
        {
            SelectedPeriodMonth = 1;
            SelectedPeriodYear += 1;
            return;
        }

        SelectedPeriodMonth += 1;
    }

    private static DateTime ComposeSelectedPeriod(
        DashboardPeriodGranularity granularity,
        int year,
        int month,
        DateTime referenceDate)
    {
        var normalizedYear = NormalizeYear(year);
        var normalizedMonth = Math.Clamp(month, 1, 12);
        return granularity switch
        {
            DashboardPeriodGranularity.Daily => new DateTime(
                normalizedYear,
                normalizedMonth,
                Math.Min(
                    referenceDate.Year == normalizedYear && referenceDate.Month == normalizedMonth
                        ? Math.Max(referenceDate.Day, 1)
                        : Math.Max(DateTime.Today.Day, 1),
                    DateTime.DaysInMonth(normalizedYear, normalizedMonth))),
            DashboardPeriodGranularity.Yearly => new DateTime(normalizedYear, 1, 1),
            _ => new DateTime(normalizedYear, normalizedMonth, 1)
        };
    }

    private static DateTime NormalizePeriod(DateTime value, DashboardPeriodGranularity granularity)
    {
        return granularity switch
        {
            DashboardPeriodGranularity.Daily => value.Date,
            DashboardPeriodGranularity.Yearly => new DateTime(value.Year, 1, 1),
            _ => new DateTime(value.Year, value.Month, 1)
        };
    }

    private static string FormatPeriodLabel(DateTime value, DashboardPeriodGranularity granularity)
    {
        return granularity switch
        {
            DashboardPeriodGranularity.Daily => value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            DashboardPeriodGranularity.Yearly => value.ToString("yyyy", CultureInfo.InvariantCulture),
            _ => value.ToString("MM/yyyy", CultureInfo.InvariantCulture)
        };
    }

    private static IReadOnlyCollection<KeyValuePair<int, string>> BuildMonthOptions()
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

    private static IReadOnlyCollection<int> BuildYearOptions()
    {
        var currentYear = DateTime.Today.Year;
        var years = new List<int>();
        for (var year = currentYear; year >= 2000; year--)
        {
            years.Add(year);
        }

        return years;
    }

    private static int NormalizeYear(int value)
    {
        return Math.Clamp(value, 2000, DateTime.Today.Year);
    }

    private DateTime? GetActiveAccountingPeriodMonth()
    {
        var activePeriod = _availableAccountingPeriods
            .Where(x => x.IsOpen)
            .OrderByDescending(x => x.PeriodMonth)
            .FirstOrDefault();

        if (activePeriod is not null)
        {
            return new DateTime(activePeriod.PeriodMonth.Year, activePeriod.PeriodMonth.Month, 1);
        }

        var latestPeriod = _availableAccountingPeriods
            .OrderByDescending(x => x.PeriodMonth)
            .FirstOrDefault();

        return latestPeriod is null
            ? null
            : new DateTime(latestPeriod.PeriodMonth.Year, latestPeriod.PeriodMonth.Month, 1);
    }

    private long? ResolvePeriodLookupLocationId()
    {
        if (SelectedLocation?.Id is > 0)
        {
            return SelectedLocation.Id.Value;
        }

        if (_accessContext.SelectedCompanyId == SelectedCompany?.Id && _accessContext.SelectedLocationId > 0)
        {
            return _accessContext.SelectedLocationId;
        }

        return BuildLocationOptionsForSelectedCompany()
            .FirstOrDefault(x => x.Id is > 0)
            ?.Id;
    }

    private IEnumerable<DashboardLocationOption> BuildLocationOptionsForSelectedCompany()
    {
        if (SelectedCompany is null)
        {
            return Enumerable.Empty<DashboardLocationOption>();
        }

        return _availableLocations.Count > 0
            ? _availableLocations
                .Where(x => x.CompanyId == SelectedCompany.Id)
                .Select(x => new DashboardLocationOption(x.Id, x.CompanyId, x.ToString()))
                .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            : LocationOptionsFromShell()
            .Where(x => x.CompanyId == SelectedCompany.Id)
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<DashboardLocationOption> LocationOptionsFromShell()
    {
        var currentLocationDisplay = string.IsNullOrWhiteSpace(_accessContext.SelectedLocationCode)
            ? _accessContext.SelectedLocationName
            : $"{_accessContext.SelectedLocationCode} - {_accessContext.SelectedLocationName}";

        if (_accessContext.SelectedLocationId > 0)
        {
            yield return new DashboardLocationOption(_accessContext.SelectedLocationId, _accessContext.SelectedCompanyId, currentLocationDisplay);
        }
    }

    private async Task RefreshAsync(bool forceReload)
    {
        if (IsBusy && !forceReload)
        {
            return;
        }

        if (SelectedCompany is null)
        {
            StatusMessage = "Company dashboard belum tersedia.";
            return;
        }

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();

        try
        {
            IsBusy = true;
            StatusMessage = "Memuat dashboard accounting...";

            var request = BuildRequest();
            AccountingDashboardData data;
            try
            {
                data = await _accessControlService.GetAccountingDashboardDataAsync(request, _refreshCts.Token);
            }
            catch
            {
                data = DashboardPreviewDataFactory.Create(
                    request,
                    SelectedCompany.ToString() ?? "Company",
                    SelectedLocation?.DisplayName ?? "Semua Lokasi");
                StatusMessage = "Dashboard dimuat dengan data preview.";
                ApplyData(data);
                return;
            }

            ApplyData(data);
            StatusMessage = "Dashboard accounting siap.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Pemuatan dashboard dibatalkan.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AccountingDashboardRequest BuildRequest()
    {
        return new AccountingDashboardRequest
        {
            UserId = _accessContext.UserId,
            RoleId = _accessContext.SelectedRoleId,
            CompanyId = SelectedCompany?.Id ?? _accessContext.SelectedCompanyId,
            LocationId = SelectedLocation?.Id,
            PeriodStart = SelectedPeriod,
            PeriodGranularity = SelectedGranularity?.Value ?? DashboardPeriodGranularity.Monthly,
            CurrencyCode = Header.CurrencyCode
        };
    }

    private void ApplyData(AccountingDashboardData data)
    {
        _currentData = data;
        Header.CompanyDisplayName = data.HeaderContext.CompanyDisplayName;
        Header.LocationDisplayName = data.HeaderContext.LocationDisplayName;
        Header.CurrencyCode = data.HeaderContext.CurrencyCode;
        Header.LastUpdatedText = data.LastUpdatedAt.ToString("dd MMM yyyy HH:mm:ss");

        _lastUpdatedAt = data.LastUpdatedAt;
        OnPropertyChanged(nameof(LastUpdatedText));

        Kpi.Apply(data.Kpis);
        Charts.Apply(data.RevenueExpenseTrend, data.TopExpenseAccounts, data.AssetComposition);
        Gl.Apply(data.GlSnapshot);
        Cash.Apply(data.CashBank);
        Inventory.Apply(data.Inventory);
        Alerts.Apply(data.Alerts, CanExecuteDrillRequest);
    }

    private void ExportDashboard()
    {
        if (!CanExport)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"DASHBOARD_ACCOUNTING_{SelectedPeriod:yyyyMM}.xlsx"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (_currentData is null)
        {
            return;
        }

        _xlsxService.ExportDashboard(dialog.FileName, BuildRequest(), _currentData);
        StatusMessage = $"Export dashboard berhasil: {System.IO.Path.GetFileName(dialog.FileName)}";
    }
}

public sealed class DashboardHeaderSectionViewModel : ViewModelBase
{
    private string _companyDisplayName = "-";
    private string _locationDisplayName = "Semua Lokasi";
    private string _userDisplayName = "User";
    private string _roleDisplayName = "-";
    private string _currencyCode = "IDR";
    private string _lastUpdatedText = "-";

    public string CompanyDisplayName { get => _companyDisplayName; set => SetProperty(ref _companyDisplayName, value); }
    public string LocationDisplayName { get => _locationDisplayName; set => SetProperty(ref _locationDisplayName, value); }
    public string UserDisplayName { get => _userDisplayName; set => SetProperty(ref _userDisplayName, value); }
    public string RoleDisplayName { get => _roleDisplayName; set => SetProperty(ref _roleDisplayName, value); }
    public string CurrencyCode { get => _currencyCode; set => SetProperty(ref _currencyCode, value); }
    public string LastUpdatedText { get => _lastUpdatedText; set => SetProperty(ref _lastUpdatedText, value); }
}

public sealed class DashboardKpiSectionViewModel
{
    public ObservableCollection<DashboardKpiCardViewModel> Items { get; } = new();

    public void Apply(IEnumerable<KpiMetricItem> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(new DashboardKpiCardViewModel(item));
        }
    }
}

public sealed class DashboardChartsSectionViewModel : ViewModelBase
{
    private const double TrendWidth = 720d;
    private const double TrendHeight = 232d;
    private const double TrendPaddingLeft = 18d;
    private const double TrendPaddingRight = 18d;
    private const double TrendPaddingTop = 16d;
    private const double TrendPaddingBottom = 34d;
    private const double MarkerSize = 12d;
    private const double ExpenseBarMaxHeight = 150d;
    private const double DonutSizeValue = 220d;
    private const double DonutThickness = 54d;

    private string _revenueLineGeometry = string.Empty;
    private string _expenseLineGeometry = string.Empty;
    private string _donutCenterValueText = "0";
    private bool _hasTrendData;
    private bool _hasExpenseData;
    private bool _hasAssetData;

    public ObservableCollection<TrendPoint> RevenueExpensePoints { get; } = new();
    public ObservableCollection<ExpenseAccountBarItem> TopExpenseAccounts { get; } = new();
    public ObservableCollection<CompositionSlice> AssetComposition { get; } = new();
    public ObservableCollection<DashboardLineMarkerPoint> RevenueMarkers { get; } = new();
    public ObservableCollection<DashboardLineMarkerPoint> ExpenseMarkers { get; } = new();
    public ObservableCollection<DashboardAxisLabelPoint> TrendAxisLabels { get; } = new();
    public ObservableCollection<DashboardBarPoint> ExpenseBars { get; } = new();
    public ObservableCollection<DashboardDonutSlice> DonutSlices { get; } = new();

    public double TrendChartWidth => TrendWidth;
    public double TrendChartHeight => TrendHeight;
    public double BarChartHeight => ExpenseBarMaxHeight;
    public double DonutSize => DonutSizeValue;

    public string RevenueLineGeometry
    {
        get => _revenueLineGeometry;
        private set => SetProperty(ref _revenueLineGeometry, value);
    }

    public string ExpenseLineGeometry
    {
        get => _expenseLineGeometry;
        private set => SetProperty(ref _expenseLineGeometry, value);
    }

    public string DonutCenterValueText
    {
        get => _donutCenterValueText;
        private set => SetProperty(ref _donutCenterValueText, value);
    }

    public bool HasTrendData
    {
        get => _hasTrendData;
        private set => SetProperty(ref _hasTrendData, value);
    }

    public bool HasExpenseData
    {
        get => _hasExpenseData;
        private set => SetProperty(ref _hasExpenseData, value);
    }

    public bool HasAssetData
    {
        get => _hasAssetData;
        private set => SetProperty(ref _hasAssetData, value);
    }

    public void Apply(
        IEnumerable<TrendPoint> revenueExpense,
        IEnumerable<ExpenseAccountBarItem> topExpenseAccounts,
        IEnumerable<CompositionSlice> assetComposition)
    {
        ReplaceCollection(RevenueExpensePoints, revenueExpense);
        ReplaceCollection(TopExpenseAccounts, topExpenseAccounts);
        ReplaceCollection(AssetComposition, assetComposition);

        BuildTrendChart();
        BuildExpenseBars();
        BuildDonutChart();
    }

    private void BuildTrendChart()
    {
        RevenueMarkers.Clear();
        ExpenseMarkers.Clear();
        TrendAxisLabels.Clear();

        HasTrendData = RevenueExpensePoints.Count > 0;
        if (!HasTrendData)
        {
            RevenueLineGeometry = string.Empty;
            ExpenseLineGeometry = string.Empty;
            return;
        }

        var maxValue = RevenueExpensePoints
            .SelectMany(x => new[] { x.Revenue, x.Expense })
            .DefaultIfEmpty(0m)
            .Max();
        if (maxValue <= 0m)
        {
            maxValue = 1m;
        }

        var usableWidth = TrendWidth - TrendPaddingLeft - TrendPaddingRight;
        var usableHeight = TrendHeight - TrendPaddingTop - TrendPaddingBottom;
        var step = RevenueExpensePoints.Count > 1 ? usableWidth / (RevenueExpensePoints.Count - 1) : 0d;
        var revenuePoints = new List<(double X, double Y)>(RevenueExpensePoints.Count);
        var expensePoints = new List<(double X, double Y)>(RevenueExpensePoints.Count);

        for (var index = 0; index < RevenueExpensePoints.Count; index++)
        {
            var point = RevenueExpensePoints[index];
            var x = TrendPaddingLeft + (step * index);
            var revenueY = ScaleY(point.Revenue, maxValue, usableHeight);
            var expenseY = ScaleY(point.Expense, maxValue, usableHeight);

            revenuePoints.Add((x, revenueY));
            expensePoints.Add((x, expenseY));
            RevenueMarkers.Add(new DashboardLineMarkerPoint(
                index,
                x - (MarkerSize / 2d),
                revenueY - (MarkerSize / 2d),
                point.Label,
                $"Revenue {point.Revenue:N0}",
                "#0E596A"));
            ExpenseMarkers.Add(new DashboardLineMarkerPoint(
                index,
                x - (MarkerSize / 2d),
                expenseY - (MarkerSize / 2d),
                point.Label,
                $"Expense {point.Expense:N0}",
                "#B91C1C"));
            TrendAxisLabels.Add(new DashboardAxisLabelPoint(x - 18d, TrendHeight - TrendPaddingBottom + 8d, point.Label));
        }

        RevenueLineGeometry = BuildPolylineGeometry(revenuePoints);
        ExpenseLineGeometry = BuildPolylineGeometry(expensePoints);
    }

    private void BuildExpenseBars()
    {
        ExpenseBars.Clear();

        HasExpenseData = TopExpenseAccounts.Count > 0;
        if (!HasExpenseData)
        {
            return;
        }

        var maxAmount = TopExpenseAccounts.Select(x => x.Amount).DefaultIfEmpty(0m).Max();
        if (maxAmount <= 0m)
        {
            maxAmount = 1m;
        }

        foreach (var item in TopExpenseAccounts)
        {
            var ratio = (double)(item.Amount / maxAmount);
            ExpenseBars.Add(new DashboardBarPoint(
                item,
                Math.Max(12d, ExpenseBarMaxHeight * ratio),
                item.AccountCode,
                item.AccountName,
                $"{item.AccountCode} - {item.AccountName}{Environment.NewLine}{item.Amount:N0}"));
        }
    }

    private void BuildDonutChart()
    {
        DonutSlices.Clear();

        HasAssetData = AssetComposition.Count > 0;
        if (!HasAssetData)
        {
            DonutCenterValueText = "0";
            return;
        }

        var total = AssetComposition.Sum(x => x.Amount);
        DonutCenterValueText = total.ToString("N0", CultureInfo.InvariantCulture);

        if (total <= 0m)
        {
            return;
        }

        var colors = new[] { "#0E596A", "#1D4ED8", "#15803D", "#B45309", "#B91C1C" };
        var startAngle = -90d;

        for (var index = 0; index < AssetComposition.Count; index++)
        {
            var item = AssetComposition[index];
            var sweepAngle = (double)(item.Amount / total) * 360d;
            DonutSlices.Add(new DashboardDonutSlice(
                item,
                CreateDonutGeometry(startAngle, sweepAngle),
                colors[index % colors.Length],
                $"{item.Label}{Environment.NewLine}{item.Amount:N0} ({(item.Amount / total):P1})"));
            startAngle += sweepAngle;
        }
    }

    private static double ScaleY(decimal value, decimal maxValue, double usableHeight)
    {
        var normalized = maxValue == 0m ? 0d : (double)(value / maxValue);
        return TrendPaddingTop + ((1d - normalized) * usableHeight);
    }

    private static string BuildPolylineGeometry(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        var segments = new List<string>(points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            var prefix = index == 0 ? "M" : "L";
            segments.Add(FormattableString.Invariant($"{prefix} {points[index].X:0.##},{points[index].Y:0.##}"));
        }

        return string.Join(" ", segments);
    }

    private static string CreateDonutGeometry(double startAngle, double sweepAngle)
    {
        if (sweepAngle <= 0.01d)
        {
            return string.Empty;
        }

        if (sweepAngle >= 359.99d)
        {
            return CreateFullDonutGeometry();
        }

        var outerRadius = (DonutSizeValue / 2d) - 8d;
        var innerRadius = outerRadius - DonutThickness;
        var center = DonutSizeValue / 2d;
        var endAngle = startAngle + sweepAngle;
        var largeArc = sweepAngle > 180d ? 1 : 0;

        var outerStart = PolarToCartesian(center, center, outerRadius, startAngle);
        var outerEnd = PolarToCartesian(center, center, outerRadius, endAngle);
        var innerStart = PolarToCartesian(center, center, innerRadius, startAngle);
        var innerEnd = PolarToCartesian(center, center, innerRadius, endAngle);

        return FormattableString.Invariant(
            $"F0 M {outerStart.X:0.##},{outerStart.Y:0.##} A {outerRadius:0.##},{outerRadius:0.##} 0 {largeArc} 1 {outerEnd.X:0.##},{outerEnd.Y:0.##} L {innerEnd.X:0.##},{innerEnd.Y:0.##} A {innerRadius:0.##},{innerRadius:0.##} 0 {largeArc} 0 {innerStart.X:0.##},{innerStart.Y:0.##} Z");
    }

    private static string CreateFullDonutGeometry()
    {
        var outerRadius = (DonutSizeValue / 2d) - 8d;
        var innerRadius = outerRadius - DonutThickness;
        var center = DonutSizeValue / 2d;

        return FormattableString.Invariant(
            $"F0 M {center:0.##},{center - outerRadius:0.##} A {outerRadius:0.##},{outerRadius:0.##} 0 1 1 {center - 0.01:0.##},{center - outerRadius:0.##} M {center:0.##},{center - innerRadius:0.##} A {innerRadius:0.##},{innerRadius:0.##} 0 1 0 {center - 0.01:0.##},{center - innerRadius:0.##}");
    }

    private static (double X, double Y) PolarToCartesian(double centerX, double centerY, double radius, double angleInDegrees)
    {
        var angleInRadians = angleInDegrees * Math.PI / 180d;
        return (centerX + (radius * Math.Cos(angleInRadians)), centerY + (radius * Math.Sin(angleInRadians)));
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}

public sealed class DashboardGlSectionViewModel : ViewModelBase
{
    private int _draftCount;
    private int _postedCount;
    private int _pendingCount;

    public ObservableCollection<DashboardJournalItem> RecentTransactions { get; } = new();
    public int DraftCount { get => _draftCount; private set => SetProperty(ref _draftCount, value); }
    public int PostedCount { get => _postedCount; private set => SetProperty(ref _postedCount, value); }
    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (!SetProperty(ref _pendingCount, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasPendingCount));
        }
    }

    public bool HasPendingCount => PendingCount > 0;

    public void Apply(GlSnapshotData data)
    {
        DraftCount = data.DraftCount;
        PostedCount = data.PostedCount;
        PendingCount = data.PendingPostingCount;
        RecentTransactions.Clear();
        foreach (var item in data.RecentTransactions)
        {
            RecentTransactions.Add(item);
        }
    }
}

public sealed class DashboardCashSectionViewModel : ViewModelBase
{
    private decimal _totalBalance;
    private decimal _todayInflow;
    private decimal _todayOutflow;
    private decimal _periodInflow;
    private decimal _periodOutflow;

    public ObservableCollection<CashBankBalanceItem> Accounts { get; } = new();
    public decimal TotalBalance { get => _totalBalance; private set => SetProperty(ref _totalBalance, value); }
    public decimal TodayInflow { get => _todayInflow; private set => SetProperty(ref _todayInflow, value); }
    public decimal TodayOutflow { get => _todayOutflow; private set => SetProperty(ref _todayOutflow, value); }
    public decimal PeriodInflow { get => _periodInflow; private set => SetProperty(ref _periodInflow, value); }
    public decimal PeriodOutflow { get => _periodOutflow; private set => SetProperty(ref _periodOutflow, value); }

    public void Apply(CashBankSnapshotData data)
    {
        TotalBalance = data.TotalBalance;
        TodayInflow = data.TodayInflow;
        TodayOutflow = data.TodayOutflow;
        PeriodInflow = data.PeriodInflow;
        PeriodOutflow = data.PeriodOutflow;
        Accounts.Clear();
        foreach (var item in data.Accounts)
        {
            Accounts.Add(item);
        }
    }
}

public sealed class DashboardInventorySectionViewModel : ViewModelBase
{
    private decimal _totalValue;
    private int _lowStockCount;

    public ObservableCollection<InventoryMovementItem> TopMovingItems { get; } = new();
    public ObservableCollection<ManagedStockEntry> LowStockItems { get; } = new();
    public decimal TotalValue { get => _totalValue; private set => SetProperty(ref _totalValue, value); }
    public int LowStockCount { get => _lowStockCount; private set => SetProperty(ref _lowStockCount, value); }

    public void Apply(InventorySnapshotData data)
    {
        TotalValue = data.TotalValue;
        LowStockCount = data.LowStockCount;
        TopMovingItems.Clear();
        foreach (var item in data.TopMovingItems)
        {
            TopMovingItems.Add(item);
        }

        LowStockItems.Clear();
        foreach (var item in data.LowStockItems)
        {
            LowStockItems.Add(item);
        }
    }
}

public sealed class DashboardAlertSectionViewModel
{
    public ObservableCollection<DashboardAlertRowViewModel> Items { get; } = new();

    public void Apply(IEnumerable<DashboardAlertItem> alerts, Func<DashboardDrillRequest, bool> canExecuteDrillRequest)
    {
        Items.Clear();
        foreach (var alert in alerts)
        {
            Items.Add(new DashboardAlertRowViewModel(alert, canExecuteDrillRequest));
        }
    }
}

public sealed class DashboardKpiCardViewModel
{
    public DashboardKpiCardViewModel(KpiMetricItem item)
    {
        Source = item;
        PrimaryValueText = item.PrimaryValue.ToString("N0", CultureInfo.InvariantCulture);
        SecondaryValueText = item.SecondaryValue.HasValue ? $"YTD {item.SecondaryValue.Value:N0}" : string.Empty;
        DeltaText = item.DeltaPercent.HasValue ? $"{item.DeltaPercent.Value:+0.##;-0.##;0}%" : "0%";
        IsPositive = item.DeltaPercent.GetValueOrDefault() > 0m == item.IsPositiveWhenUp;
        IsNegative = item.DeltaPercent.HasValue && !IsPositive && item.DeltaPercent.Value != 0m;
        IsNeutral = !item.DeltaPercent.HasValue || item.DeltaPercent.Value == 0m;
    }

    public KpiMetricItem Source { get; }
    public string PrimaryValueText { get; }
    public string SecondaryValueText { get; }
    public string DeltaText { get; }
    public bool IsPositive { get; }
    public bool IsNegative { get; }
    public bool IsNeutral { get; }
    public bool HasSecondaryValue => !string.IsNullOrWhiteSpace(SecondaryValueText);
}

public sealed class DashboardAlertRowViewModel
{
    private readonly bool _canExecuteAction;

    public DashboardAlertRowViewModel(DashboardAlertItem item, Func<DashboardDrillRequest, bool> canExecuteDrillRequest)
    {
        Source = item;
        IsCritical = item.Severity == DashboardAlertSeverity.Critical;
        IsWarning = item.Severity == DashboardAlertSeverity.Warning;
        IsInfo = item.Severity == DashboardAlertSeverity.Info;
        _canExecuteAction = item.DrillRequest is not null && canExecuteDrillRequest(item.DrillRequest);
        IconGlyph = item.Severity switch
        {
            DashboardAlertSeverity.Critical => "\uEA39",
            DashboardAlertSeverity.Warning => "\uE7BA",
            _ => "\uE946"
        };
    }

    public DashboardAlertItem Source { get; }
    public DashboardDrillRequest? DrillRequest => Source.DrillRequest;
    public string IconGlyph { get; }
    public bool IsCritical { get; }
    public bool IsWarning { get; }
    public bool IsInfo { get; }
    public string ActionLabel => Source.ActionLabel;
    public bool HasActionLabel => !string.IsNullOrWhiteSpace(Source.ActionLabel);
    public bool CanExecuteAction => HasActionLabel && _canExecuteAction;
    public bool HasCount => Source.Count > 0;
    public string CountText => Source.Count.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed record DashboardGranularityOption(DashboardPeriodGranularity Value, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}

public sealed record DashboardLocationOption(long? Id, long CompanyId, string DisplayName)
{
    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed record DashboardPeriodOption(DateTime Value, string Label)
{
    public override string ToString()
    {
        return Label;
    }
}

public sealed record DashboardLineMarkerPoint(int Index, double Left, double Top, string Label, string ValueText, string FillHex)
{
    public string ToolTipText => $"{Label}{Environment.NewLine}{ValueText}";
}

public sealed record DashboardAxisLabelPoint(double Left, double Top, string Label);

public sealed record DashboardBarPoint(
    ExpenseAccountBarItem Source,
    double Height,
    string Label,
    string Subtitle,
    string ToolTipText)
{
    public string ValueText => Source.Amount.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed record DashboardDonutSlice(
    CompositionSlice Source,
    string GeometryData,
    string FillHex,
    string ToolTipText)
{
    public string Label => Source.Label;
    public string ValueText => Source.Amount.ToString("N0", CultureInfo.InvariantCulture);
}
