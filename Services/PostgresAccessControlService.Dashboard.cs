using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private const decimal DashboardLowBalanceThreshold = 1000000m;
    private const decimal DashboardLowStockThreshold = 10m;

    public async Task<DashboardFilterOptions> GetDashboardFilterOptionsAsync(
        long userId,
        long roleId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        var options = new DashboardFilterOptions();
        if (userId <= 0 || roleId <= 0)
        {
            return options;
        }

        if (!TryBuildQualifiedTableName(_options.UsersTable, out var usersTable))
        {
            return options;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        long? defaultCompanyId = null;
        long? defaultLocationId = null;
        await using (var userCommand = new NpgsqlCommand($@"
SELECT default_company_id,
       default_location_id
FROM {usersTable}
WHERE id = @user_id
LIMIT 1;", connection))
        {
            userCommand.Parameters.AddWithValue("user_id", userId);
            await using var reader = await userCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                defaultCompanyId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                defaultLocationId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }

        var isSuperRole = false;
        await using (var roleCommand = new NpgsqlCommand(@"
SELECT COALESCE(is_super_role, FALSE)
FROM sec_roles
WHERE id = @role_id
LIMIT 1;", connection))
        {
            roleCommand.Parameters.AddWithValue("role_id", roleId);
            var scalar = await roleCommand.ExecuteScalarAsync(cancellationToken);
            isSuperRole = scalar is bool flag && flag;
        }

        var companies = await LoadActiveCompaniesAsync(connection, cancellationToken);
        var locations = await LoadActiveLocationsAsync(connection, cancellationToken);

        HashSet<long> allowedCompanyIds;
        HashSet<long> allowedLocationIds;
        if (isSuperRole)
        {
            allowedCompanyIds = companies.Select(x => x.Id).ToHashSet();
            allowedLocationIds = locations.Select(x => x.Id).ToHashSet();
        }
        else
        {
            allowedCompanyIds = new HashSet<long>();
            allowedLocationIds = new HashSet<long>();

            await FillSingleSetAsync(
                connection,
                "SELECT company_id FROM sec_user_company_access WHERE user_id = @user_id;",
                "user_id",
                userId,
                allowedCompanyIds,
                cancellationToken);

            await FillSingleSetAsync(
                connection,
                "SELECT location_id FROM sec_user_location_access WHERE user_id = @user_id;",
                "user_id",
                userId,
                allowedLocationIds,
                cancellationToken);
        }

        var filteredCompanies = companies
            .Where(x => allowedCompanyIds.Contains(x.Id))
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var filteredLocations = locations
            .Where(x => allowedLocationIds.Contains(x.Id) && allowedCompanyIds.Contains(x.CompanyId))
            .OrderBy(x => x.CompanyCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (defaultCompanyId.HasValue && filteredCompanies.All(x => x.Id != defaultCompanyId.Value))
        {
            defaultCompanyId = filteredCompanies.FirstOrDefault()?.Id;
        }

        if (defaultLocationId.HasValue && filteredLocations.All(x => x.Id != defaultLocationId.Value))
        {
            var fallbackLocation = filteredLocations.FirstOrDefault(x => x.CompanyId == defaultCompanyId)
                ?? filteredLocations.FirstOrDefault();
            defaultLocationId = fallbackLocation?.Id;
        }

        return new DashboardFilterOptions
        {
            Companies = filteredCompanies,
            Locations = filteredLocations,
            DefaultCompanyId = defaultCompanyId ?? filteredCompanies.FirstOrDefault()?.Id,
            DefaultLocationId = defaultLocationId,
            BaseCurrencyCode = "IDR"
        };
    }

    public async Task<AccountingDashboardData> GetAccountingDashboardDataAsync(
        AccountingDashboardRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (request.CompanyId <= 0)
        {
            return new AccountingDashboardData();
        }

        var periodDate = request.PeriodStart == default ? DateTime.Today : request.PeriodStart.Date;
        var monthStart = GetPeriodMonthStart(periodDate);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var previousMonthStart = monthStart.AddMonths(-1);
        var previousMonthEnd = request.PeriodGranularity == DashboardPeriodGranularity.Daily
            ? previousMonthStart.AddDays(Math.Min(periodDate.Day, DateTime.DaysInMonth(previousMonthStart.Year, previousMonthStart.Month)) - 1)
            : previousMonthStart.AddMonths(1).AddDays(-1);
        var currentMonthWindowEnd = request.PeriodGranularity == DashboardPeriodGranularity.Daily ? periodDate : monthEnd;
        var activePeriodStart = request.PeriodGranularity switch
        {
            DashboardPeriodGranularity.Daily => periodDate,
            DashboardPeriodGranularity.Yearly => new DateTime(periodDate.Year, 1, 1),
            _ => monthStart
        };
        var activePeriodEnd = request.PeriodGranularity switch
        {
            DashboardPeriodGranularity.Daily => periodDate,
            DashboardPeriodGranularity.Yearly => new DateTime(periodDate.Year, 12, 31),
            _ => monthEnd
        };
        var balanceAsOfDate = request.PeriodGranularity switch
        {
            DashboardPeriodGranularity.Daily => periodDate,
            DashboardPeriodGranularity.Yearly => new DateTime(periodDate.Year, 12, 31),
            _ => monthEnd
        };
        var previousBalanceAsOfDate = request.PeriodGranularity switch
        {
            DashboardPeriodGranularity.Daily => previousMonthEnd,
            DashboardPeriodGranularity.Yearly => new DateTime(periodDate.Year - 1, 12, 31),
            _ => previousMonthStart.AddMonths(1).AddDays(-1)
        };
        var yearStart = new DateTime(periodDate.Year, 1, 1);
        var ytdEndDate = request.PeriodGranularity == DashboardPeriodGranularity.Yearly ? balanceAsOfDate : currentMonthWindowEnd;
        var previousYearStart = new DateTime(periodDate.Year - 1, 1, 1);
        var previousYearYtdEndDate = request.PeriodGranularity switch
        {
            DashboardPeriodGranularity.Daily => periodDate.AddYears(-1),
            DashboardPeriodGranularity.Yearly => new DateTime(periodDate.Year - 1, 12, 31),
            _ => currentMonthWindowEnd.AddYears(-1)
        };

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var headerContext = await LoadDashboardHeaderContextAsync(
            connection,
            request.CompanyId,
            request.LocationId,
            request.CurrencyCode,
            cancellationToken);

        var kpis = await LoadDashboardKpisAsync(
            connection,
            request.CompanyId,
            request.LocationId,
            monthStart,
            previousMonthStart,
            yearStart,
            currentMonthWindowEnd,
            previousMonthEnd,
            ytdEndDate,
            previousYearStart,
            previousYearYtdEndDate,
            balanceAsOfDate,
            previousBalanceAsOfDate,
            cancellationToken);

        var trend = await LoadRevenueExpenseTrendAsync(
            connection,
            request.CompanyId,
            request.LocationId,
            monthStart,
            cancellationToken);

        var topExpenseAccounts = await LoadTopExpenseAccountsAsync(
            connection,
            request.CompanyId,
            request.LocationId,
            activePeriodStart,
            activePeriodEnd,
            cancellationToken);

        var assetComposition = await LoadAssetCompositionAsync(
            connection,
            request.CompanyId,
            request.LocationId,
            balanceAsOfDate,
            cancellationToken);

        var glSnapshot = await LoadGlSnapshotAsync(
            connection,
            request.CompanyId,
            request.LocationId,
            activePeriodStart,
            activePeriodEnd,
            cancellationToken);

        var cashBank = await LoadCashBankSnapshotAsync(
            connection,
            request.CompanyId,
            request.LocationId,
            periodDate,
            activePeriodStart,
            activePeriodEnd,
            balanceAsOfDate,
            cancellationToken);

        var inventory = await LoadInventorySnapshotAsync(
            connection,
            request.CompanyId,
            request.LocationId,
            activePeriodStart,
            activePeriodEnd,
            cancellationToken);

        var alerts = BuildDashboardAlerts(
            glSnapshot,
            assetComposition,
            cashBank,
            inventory,
            await LoadBalanceSheetDifferenceAsync(connection, request.CompanyId, request.LocationId, balanceAsOfDate, cancellationToken),
            await LoadAbnormalTransactionCountAsync(connection, request.CompanyId, request.LocationId, activePeriodStart, activePeriodEnd, cancellationToken),
            request);

        return new AccountingDashboardData
        {
            HeaderContext = headerContext,
            Kpis = kpis,
            RevenueExpenseTrend = trend,
            TopExpenseAccounts = topExpenseAccounts,
            AssetComposition = assetComposition,
            GlSnapshot = glSnapshot,
            CashBank = cashBank,
            Inventory = inventory,
            Alerts = alerts,
            LastUpdatedAt = DateTime.Now
        };
    }

    private static async Task<DashboardHeaderContext> LoadDashboardHeaderContextAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        string companyDisplayName = $"Company {companyId}";
        await using (var companyCommand = new NpgsqlCommand(@"
SELECT code, name
FROM org_companies
WHERE id = @company_id
LIMIT 1;", connection))
        {
            companyCommand.Parameters.AddWithValue("company_id", companyId);
            await using var reader = await companyCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var code = reader.GetString(0);
                var name = reader.GetString(1);
                companyDisplayName = string.IsNullOrWhiteSpace(code) ? name : $"{code} - {name}";
            }
        }

        var locationDisplayName = "Semua Lokasi";
        if (locationId.HasValue && locationId.Value > 0)
        {
            await using var locationCommand = new NpgsqlCommand(@"
SELECT code, name
FROM org_locations
WHERE id = @location_id
LIMIT 1;", connection);
            locationCommand.Parameters.AddWithValue("location_id", locationId.Value);
            await using var reader = await locationCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var code = reader.GetString(0);
                var name = reader.GetString(1);
                locationDisplayName = string.IsNullOrWhiteSpace(code) ? name : $"{code} - {name}";
            }
        }

        return new DashboardHeaderContext
        {
            CompanyDisplayName = companyDisplayName,
            LocationDisplayName = locationDisplayName,
            CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "IDR" : currencyCode.Trim().ToUpperInvariant()
        };
    }

    private static async Task<List<KpiMetricItem>> LoadDashboardKpisAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime monthStart,
        DateTime previousMonthStart,
        DateTime yearStart,
        DateTime monthEndDate,
        DateTime previousMonthEndDate,
        DateTime ytdEndDate,
        DateTime previousYearStart,
        DateTime previousYearEndDate,
        DateTime balanceAsOfDate,
        DateTime previousBalanceAsOfDate,
        CancellationToken cancellationToken)
    {
        var kpiValues = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var periodPairs = new[]
        {
            ("mtd", monthStart, monthEndDate),
            ("mtd_prev", previousMonthStart, previousMonthEndDate),
            ("ytd", yearStart, ytdEndDate),
            ("ytd_prev", previousYearStart, previousYearEndDate)
        };

        foreach (var (key, fromMonth, toMonth) in periodPairs)
        {
            await using var command = new NpgsqlCommand(@"
SELECT upper(a.account_type) AS account_type,
       COALESCE(SUM(le.debit), 0) AS total_debit,
       COALESCE(SUM(le.credit), 0) AS total_credit
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date >= @date_from
  AND le.journal_date <= @date_to
  AND upper(a.account_type) IN ('REVENUE', 'EXPENSE')
GROUP BY upper(a.account_type);", connection);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            command.Parameters.AddWithValue("date_from", fromMonth);
            command.Parameters.AddWithValue("date_to", toMonth);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var accountType = reader.GetString(0);
                var totalDebit = reader.GetDecimal(1);
                var totalCredit = reader.GetDecimal(2);
                var amount = accountType == "REVENUE"
                    ? totalCredit - totalDebit
                    : totalDebit - totalCredit;
                kpiValues[$"{key}_{accountType.ToLowerInvariant()}"] = amount;
            }
        }

        await using (var balanceCommand = new NpgsqlCommand(@"
SELECT upper(a.account_type) AS account_type,
       COALESCE(SUM(le.debit), 0) AS total_debit,
       COALESCE(SUM(le.credit), 0) AS total_credit
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date <= @as_of_date
  AND upper(a.account_type) IN ('ASSET', 'LIABILITY', 'EQUITY')
GROUP BY upper(a.account_type);", connection))
        {
            balanceCommand.Parameters.AddWithValue("company_id", companyId);
            balanceCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            balanceCommand.Parameters.AddWithValue("as_of_date", balanceAsOfDate);
            await using var reader = await balanceCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var accountType = reader.GetString(0);
                var totalDebit = reader.GetDecimal(1);
                var totalCredit = reader.GetDecimal(2);
                kpiValues[accountType.ToLowerInvariant()] = accountType == "ASSET"
                    ? totalDebit - totalCredit
                    : totalCredit - totalDebit;
            }
        }

        await using (var previousBalanceCommand = new NpgsqlCommand(@"
SELECT upper(a.account_type) AS account_type,
       COALESCE(SUM(le.debit), 0) AS total_debit,
       COALESCE(SUM(le.credit), 0) AS total_credit
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date <= @as_of_date
  AND upper(a.account_type) IN ('ASSET', 'LIABILITY', 'EQUITY')
GROUP BY upper(a.account_type);", connection))
        {
            previousBalanceCommand.Parameters.AddWithValue("company_id", companyId);
            previousBalanceCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            previousBalanceCommand.Parameters.AddWithValue("as_of_date", previousBalanceAsOfDate);
            await using var reader = await previousBalanceCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var accountType = reader.GetString(0);
                var totalDebit = reader.GetDecimal(1);
                var totalCredit = reader.GetDecimal(2);
                kpiValues[$"{accountType.ToLowerInvariant()}_prev"] = accountType == "ASSET"
                    ? totalDebit - totalCredit
                    : totalCredit - totalDebit;
            }
        }

        var revenueMtd = GetValue(kpiValues, "mtd_revenue");
        var revenueYtd = GetValue(kpiValues, "ytd_revenue");
        var revenuePrev = GetValue(kpiValues, "mtd_prev_revenue");
        var expenseMtd = GetValue(kpiValues, "mtd_expense");
        var expenseYtd = GetValue(kpiValues, "ytd_expense");
        var expensePrev = GetValue(kpiValues, "mtd_prev_expense");
        var netIncome = revenueMtd - expenseMtd;
        var netIncomePrev = revenuePrev - expensePrev;

        return new List<KpiMetricItem>
        {
            BuildKpi("assets", "Total Assets", "\uE81E", GetValue(kpiValues, "asset"), null, GetValue(kpiValues, "asset_prev"), true),
            BuildKpi("liabilities", "Total Liabilities", "\uE8F1", GetValue(kpiValues, "liability"), null, GetValue(kpiValues, "liability_prev"), false),
            BuildKpi("equity", "Equity", "\uE8C7", GetValue(kpiValues, "equity"), null, GetValue(kpiValues, "equity_prev"), true),
            BuildKpi("revenue", "Revenue", "\uEAFD", revenueMtd, revenueYtd, revenuePrev, true),
            BuildKpi("expenses", "Expenses", "\uE91A", expenseMtd, expenseYtd, expensePrev, false),
            BuildKpi("net_profit", "Net Profit / Loss", "\uE9D2", netIncome, null, netIncomePrev, true)
        };
    }

    private static KpiMetricItem BuildKpi(
        string key,
        string label,
        string iconGlyph,
        decimal primaryValue,
        decimal? secondaryValue,
        decimal? previousValue,
        bool isPositiveWhenUp)
    {
        return new KpiMetricItem
        {
            Key = key,
            Label = label,
            IconGlyph = iconGlyph,
            PrimaryValue = primaryValue,
            SecondaryValue = secondaryValue,
            PreviousValue = previousValue,
            DeltaPercent = previousValue.HasValue && previousValue.Value != 0
                ? Math.Round(((primaryValue - previousValue.Value) / Math.Abs(previousValue.Value)) * 100m, 2)
                : null,
            IsPositiveWhenUp = isPositiveWhenUp
        };
    }

    private static async Task<List<TrendPoint>> LoadRevenueExpenseTrendAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime monthStart,
        CancellationToken cancellationToken)
    {
        var output = new List<TrendPoint>();
        var startMonth = monthStart.AddMonths(-11);

        await using var command = new NpgsqlCommand(@"
SELECT le.period_month,
       upper(a.account_type) AS account_type,
       COALESCE(SUM(le.debit), 0) AS total_debit,
       COALESCE(SUM(le.credit), 0) AS total_credit
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.period_month >= @start_month
  AND le.period_month <= @end_month
  AND upper(a.account_type) IN ('REVENUE', 'EXPENSE')
GROUP BY le.period_month, upper(a.account_type)
ORDER BY le.period_month;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
        command.Parameters.AddWithValue("start_month", startMonth);
        command.Parameters.AddWithValue("end_month", monthStart);

        var map = new Dictionary<DateTime, TrendPointBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var pointMonth = reader.GetDateTime(0).Date;
            if (!map.TryGetValue(pointMonth, out var builder))
            {
                builder = new TrendPointBuilder();
                map[pointMonth] = builder;
            }

            var accountType = reader.GetString(1);
            var totalDebit = reader.GetDecimal(2);
            var totalCredit = reader.GetDecimal(3);
            if (accountType == "REVENUE")
            {
                builder.Revenue = totalCredit - totalDebit;
            }
            else if (accountType == "EXPENSE")
            {
                builder.Expense = totalDebit - totalCredit;
            }
        }

        for (var offset = 0; offset < 12; offset++)
        {
            var current = startMonth.AddMonths(offset);
            map.TryGetValue(current, out var builder);
            output.Add(new TrendPoint
            {
                Label = current.ToString("MMM yy", CultureInfo.InvariantCulture),
                PeriodStart = current,
                Revenue = builder?.Revenue ?? 0m,
                Expense = builder?.Expense ?? 0m
            });
        }

        return output;
    }

    private static async Task<List<ExpenseAccountBarItem>> LoadTopExpenseAccountsAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime monthStart,
        DateTime periodEndDate,
        CancellationToken cancellationToken)
    {
        var output = new List<ExpenseAccountBarItem>();

        await using var command = new NpgsqlCommand(@"
SELECT a.account_code,
       a.account_name,
       COALESCE(SUM(le.debit - le.credit), 0) AS amount
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date >= @date_from
  AND le.journal_date <= @date_to
  AND upper(a.account_type) = 'EXPENSE'
GROUP BY a.account_code, a.account_name
HAVING COALESCE(SUM(le.debit - le.credit), 0) <> 0
ORDER BY amount DESC, a.account_code
LIMIT 10;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
        command.Parameters.AddWithValue("date_from", monthStart);
        command.Parameters.AddWithValue("date_to", periodEndDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ExpenseAccountBarItem
            {
                AccountCode = reader.GetString(0),
                AccountName = reader.GetString(1),
                Amount = reader.GetDecimal(2)
            });
        }

        return output;
    }

    private static async Task<List<CompositionSlice>> LoadAssetCompositionAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime asOfDate,
        CancellationToken cancellationToken)
    {
        var output = new List<CompositionSlice>();

        await using var command = new NpgsqlCommand(@"
SELECT CASE
           WHEN upper(COALESCE(a.report_group, '')) IN ('CURRENT_ASSET', 'CURRENT', 'CURRENT ASSET', 'ASET_LANCAR')
             OR a.account_code = '10.00101.000'
             OR a.account_code LIKE '10.011%'
           THEN 'Current Asset'
           ELSE 'Fixed Asset'
       END AS group_name,
       COALESCE(SUM(le.debit - le.credit), 0) AS amount
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date <= @as_of_date
  AND upper(a.account_type) = 'ASSET'
GROUP BY 1
HAVING COALESCE(SUM(le.debit - le.credit), 0) <> 0
ORDER BY 1;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
        command.Parameters.AddWithValue("as_of_date", asOfDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var label = reader.GetString(0);
            output.Add(new CompositionSlice
            {
                Label = label,
                GroupCode = label.Replace(" ", string.Empty),
                Amount = reader.GetDecimal(1)
            });
        }

        return output;
    }

    private static async Task<GlSnapshotData> LoadGlSnapshotAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime periodStartDate,
        DateTime periodEndDate,
        CancellationToken cancellationToken)
    {
        int draftCount;
        int postedCount;
        int pendingPostingCount;

        await using (var countCommand = new NpgsqlCommand(@"
SELECT
    COALESCE(SUM(CASE WHEN upper(status) = 'DRAFT' THEN 1 ELSE 0 END), 0) AS draft_count,
    COALESCE(SUM(CASE WHEN upper(status) = 'POSTED' THEN 1 ELSE 0 END), 0) AS posted_count,
    COALESCE(SUM(CASE WHEN upper(status) IN ('DRAFT', 'SUBMITTED', 'APPROVED') THEN 1 ELSE 0 END), 0) AS pending_count
FROM gl_journal_headers
WHERE company_id = @company_id
  AND (@location_id IS NULL OR location_id = @location_id)
  AND journal_date >= @date_from
  AND journal_date <= @date_to;", connection))
        {
            countCommand.Parameters.AddWithValue("company_id", companyId);
            countCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            countCommand.Parameters.AddWithValue("date_from", periodStartDate);
            countCommand.Parameters.AddWithValue("date_to", periodEndDate);
            await using var reader = await countCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            draftCount = reader.GetInt32(0);
            postedCount = reader.GetInt32(1);
            pendingPostingCount = reader.GetInt32(2);
        }

        var recentTransactions = new List<DashboardJournalItem>();
        await using (var listCommand = new NpgsqlCommand(@"
SELECT h.id,
       h.journal_no,
       h.journal_date,
       COALESCE(h.reference_no, '') AS reference_no,
       COALESCE(h.description, '') AS description,
       h.status,
       COALESCE(SUM(d.debit), 0) AS total_amount
FROM gl_journal_headers h
LEFT JOIN gl_journal_details d ON d.header_id = h.id
WHERE h.company_id = @company_id
  AND (@location_id IS NULL OR h.location_id = @location_id)
  AND h.journal_date >= @date_from
  AND h.journal_date <= @date_to
GROUP BY h.id, h.journal_no, h.journal_date, h.reference_no, h.description, h.status
ORDER BY h.journal_date DESC, h.id DESC
LIMIT 10;", connection))
        {
            listCommand.Parameters.AddWithValue("company_id", companyId);
            listCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            listCommand.Parameters.AddWithValue("date_from", periodStartDate);
            listCommand.Parameters.AddWithValue("date_to", periodEndDate);
            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                recentTransactions.Add(new DashboardJournalItem
                {
                    JournalId = reader.GetInt64(0),
                    JournalNo = reader.GetString(1),
                    JournalDate = reader.GetDateTime(2),
                    ReferenceNo = reader.GetString(3),
                    Description = reader.GetString(4),
                    Status = reader.GetString(5),
                    TotalAmount = reader.GetDecimal(6)
                });
            }
        }

        return new GlSnapshotData
        {
            DraftCount = draftCount,
            PostedCount = postedCount,
            PendingPostingCount = pendingPostingCount,
            RecentTransactions = recentTransactions
        };
    }

    private static async Task<CashBankSnapshotData> LoadCashBankSnapshotAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime periodDate,
        DateTime periodStartDate,
        DateTime periodEndDate,
        DateTime balanceAsOfDate,
        CancellationToken cancellationToken)
    {
        var accounts = new List<CashBankBalanceItem>();

        await using (var balanceCommand = new NpgsqlCommand(@"
SELECT a.account_code,
       a.account_name,
       COALESCE(SUM(le.debit - le.credit), 0) AS ending_balance
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date <= @as_of_date
  AND upper(a.account_type) = 'ASSET'
  AND (
      upper(COALESCE(a.report_group, '')) IN ('CASH_BANK', 'KAS_BANK', 'KAS', 'BANK')
      OR upper(COALESCE(a.cashflow_category, '')) LIKE '%CASH%'
      OR a.account_code = '10.01101.000'
      OR a.account_code LIKE '10.01101.%'
  )
GROUP BY a.account_code, a.account_name
HAVING COALESCE(SUM(le.debit - le.credit), 0) <> 0
ORDER BY a.account_code;", connection))
        {
            balanceCommand.Parameters.AddWithValue("company_id", companyId);
            balanceCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            balanceCommand.Parameters.AddWithValue("as_of_date", balanceAsOfDate);
            await using var reader = await balanceCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts.Add(new CashBankBalanceItem
                {
                    AccountCode = reader.GetString(0),
                    AccountName = reader.GetString(1),
                    EndingBalance = reader.GetDecimal(2)
                });
            }
        }

        decimal todayInflow;
        decimal todayOutflow;
        await using (var dailyCommand = new NpgsqlCommand(@"
SELECT COALESCE(SUM(le.debit), 0) AS inflow,
       COALESCE(SUM(le.credit), 0) AS outflow
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date = @journal_date
  AND upper(a.account_type) = 'ASSET'
  AND (
      upper(COALESCE(a.report_group, '')) IN ('CASH_BANK', 'KAS_BANK', 'KAS', 'BANK')
      OR upper(COALESCE(a.cashflow_category, '')) LIKE '%CASH%'
      OR a.account_code = '10.01101.000'
      OR a.account_code LIKE '10.01101.%'
  );", connection))
        {
            dailyCommand.Parameters.AddWithValue("company_id", companyId);
            dailyCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            dailyCommand.Parameters.AddWithValue("journal_date", periodDate);
            await using var reader = await dailyCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            todayInflow = reader.GetDecimal(0);
            todayOutflow = reader.GetDecimal(1);
        }

        decimal periodInflow;
        decimal periodOutflow;
        await using (var monthlyCommand = new NpgsqlCommand(@"
SELECT COALESCE(SUM(le.debit), 0) AS inflow,
       COALESCE(SUM(le.credit), 0) AS outflow
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date >= @date_from
  AND le.journal_date <= @date_to
  AND upper(a.account_type) = 'ASSET'
  AND (
      upper(COALESCE(a.report_group, '')) IN ('CASH_BANK', 'KAS_BANK', 'KAS', 'BANK')
      OR upper(COALESCE(a.cashflow_category, '')) LIKE '%CASH%'
      OR a.account_code = '10.01101.000'
      OR a.account_code LIKE '10.01101.%'
  );", connection))
        {
            monthlyCommand.Parameters.AddWithValue("company_id", companyId);
            monthlyCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            monthlyCommand.Parameters.AddWithValue("date_from", periodStartDate);
            monthlyCommand.Parameters.AddWithValue("date_to", periodEndDate);
            await using var reader = await monthlyCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            periodInflow = reader.GetDecimal(0);
            periodOutflow = reader.GetDecimal(1);
        }

        return new CashBankSnapshotData
        {
            TotalBalance = accounts.Sum(x => x.EndingBalance),
            TodayInflow = todayInflow,
            TodayOutflow = todayOutflow,
            PeriodInflow = periodInflow,
            PeriodOutflow = periodOutflow,
            Accounts = accounts
        };
    }

    private static async Task<InventorySnapshotData> LoadInventorySnapshotAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime periodStartDate,
        DateTime periodEndDate,
        CancellationToken cancellationToken)
    {
        decimal totalValue;
        int lowStockCount;
        await using (var valueCommand = new NpgsqlCommand(@"
SELECT COALESCE(SUM(remaining_qty * unit_cost), 0)
FROM inv_cost_layers
WHERE company_id = @company_id
  AND (@location_id IS NULL OR location_id = @location_id)
  AND remaining_qty > 0;", connection))
        {
            valueCommand.Parameters.AddWithValue("company_id", companyId);
            valueCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            totalValue = Convert.ToDecimal(await valueCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using (var lowStockCountCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM inv_stock s
JOIN inv_items i ON i.id = s.item_id
WHERE s.company_id = @company_id
  AND (@location_id IS NULL OR s.location_id = @location_id)
  AND i.is_active = TRUE
  AND s.qty < @threshold;", connection))
        {
            lowStockCountCommand.Parameters.AddWithValue("company_id", companyId);
            lowStockCountCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            lowStockCountCommand.Parameters.AddWithValue("threshold", DashboardLowStockThreshold);
            lowStockCount = Convert.ToInt32(await lowStockCountCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        var lowStockItems = new List<ManagedStockEntry>();
        await using (var lowStockCommand = new NpgsqlCommand(@"
SELECT s.id,
       s.item_id,
       i.item_code,
       i.item_name,
       i.uom,
       l.code AS location_code,
       l.name AS location_name,
       s.warehouse_id,
       COALESCE(w.warehouse_name, '') AS warehouse_name,
       s.qty
FROM inv_stock s
JOIN inv_items i ON i.id = s.item_id
JOIN org_locations l ON l.id = s.location_id
LEFT JOIN inv_warehouses w ON w.id = s.warehouse_id
WHERE s.company_id = @company_id
  AND (@location_id IS NULL OR s.location_id = @location_id)
  AND i.is_active = TRUE
  AND s.qty < @threshold
ORDER BY s.qty ASC, i.item_code, w.warehouse_name
LIMIT 8;", connection))
        {
            lowStockCommand.Parameters.AddWithValue("company_id", companyId);
            lowStockCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            lowStockCommand.Parameters.AddWithValue("threshold", DashboardLowStockThreshold);
            await using var reader = await lowStockCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lowStockItems.Add(new ManagedStockEntry
                {
                    Id = reader.GetInt64(0),
                    ItemId = reader.GetInt64(1),
                    ItemCode = reader.GetString(2),
                    ItemName = reader.GetString(3),
                    Uom = reader.GetString(4),
                    LocationCode = reader.GetString(5),
                    LocationName = reader.GetString(6),
                    WarehouseId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    WarehouseName = reader.GetString(8),
                    Qty = reader.GetDecimal(9)
                });
            }
        }

        var topMovingItems = new List<InventoryMovementItem>();
        await using (var movementCommand = new NpgsqlCommand(@"
SELECT i.item_code,
       i.item_name,
       i.uom,
       SUM(ABS(l.qty)) AS total_qty
FROM inv_stock_transactions h
JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
JOIN inv_items i ON i.id = l.item_id
WHERE h.company_id = @company_id
  AND (@location_id IS NULL OR h.location_id = @location_id)
  AND h.transaction_date >= @date_from
  AND h.transaction_date <= @date_to
  AND upper(h.status) = 'POSTED'
GROUP BY i.item_code, i.item_name, i.uom
ORDER BY total_qty DESC, i.item_code
LIMIT 5;", connection))
        {
            movementCommand.Parameters.AddWithValue("company_id", companyId);
            movementCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
            movementCommand.Parameters.AddWithValue("date_from", periodStartDate);
            movementCommand.Parameters.AddWithValue("date_to", periodEndDate);
            await using var reader = await movementCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                topMovingItems.Add(new InventoryMovementItem
                {
                    ItemCode = reader.GetString(0),
                    ItemName = reader.GetString(1),
                    Uom = reader.GetString(2),
                    Qty = reader.GetDecimal(3)
                });
            }
        }

        return new InventorySnapshotData
        {
            TotalValue = totalValue,
            LowStockCount = lowStockCount,
            TopMovingItems = topMovingItems,
            LowStockItems = lowStockItems,
            AgingBuckets = new List<InventoryAgingBucket>()
        };
    }

    private static async Task<decimal> LoadBalanceSheetDifferenceAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime asOfDate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT COALESCE(SUM(CASE WHEN upper(a.account_type) = 'ASSET' THEN le.debit - le.credit ELSE 0 END), 0) AS assets,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'LIABILITY' THEN le.credit - le.debit ELSE 0 END), 0) AS liabilities,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'EQUITY' THEN le.credit - le.debit ELSE 0 END), 0) AS equity
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND (@location_id IS NULL OR le.location_id = @location_id)
  AND le.journal_date <= @as_of_date;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
        command.Parameters.AddWithValue("as_of_date", asOfDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return 0m;
        }

        var assets = reader.GetDecimal(0);
        var liabilities = reader.GetDecimal(1);
        var equity = reader.GetDecimal(2);
        return Math.Round(assets - (liabilities + equity), 2);
    }

    private static async Task<int> LoadAbnormalTransactionCountAsync(
        NpgsqlConnection connection,
        long companyId,
        long? locationId,
        DateTime monthStart,
        DateTime periodEndDate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
WITH header_totals AS (
    SELECT h.id,
           COALESCE(SUM(d.debit), 0) AS total_amount
    FROM gl_journal_headers h
    JOIN gl_journal_details d ON d.header_id = h.id
    WHERE h.company_id = @company_id
      AND (@location_id IS NULL OR h.location_id = @location_id)
      AND h.journal_date >= @date_from
      AND h.journal_date <= @date_to
      AND upper(h.status) = 'POSTED'
    GROUP BY h.id
),
stats AS (
    SELECT COALESCE(AVG(total_amount), 0) AS avg_amount,
           COALESCE(STDDEV_POP(total_amount), 0) AS std_amount
    FROM header_totals
)
SELECT COUNT(1)
FROM header_totals ht
CROSS JOIN stats s
WHERE ht.total_amount > (s.avg_amount + (2 * s.std_amount));", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationId.HasValue && locationId.Value > 0 ? locationId.Value : DBNull.Value });
        command.Parameters.AddWithValue("date_from", monthStart);
        command.Parameters.AddWithValue("date_to", periodEndDate);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static List<DashboardAlertItem> BuildDashboardAlerts(
        GlSnapshotData glSnapshot,
        IReadOnlyCollection<CompositionSlice> assetComposition,
        CashBankSnapshotData cashBank,
        InventorySnapshotData inventory,
        decimal balanceSheetDifference,
        int abnormalTransactionCount,
        AccountingDashboardRequest request)
    {
        var alerts = new List<DashboardAlertItem>();
        if (glSnapshot.PendingPostingCount > 0)
        {
            alerts.Add(new DashboardAlertItem
            {
                Key = "unposted_journals",
                Title = "Jurnal Belum Posting",
                Message = $"{glSnapshot.PendingPostingCount:N0} jurnal masih draft/submitted/approved.",
                ActionLabel = "Buka jurnal",
                Severity = DashboardAlertSeverity.Warning,
                Count = glSnapshot.PendingPostingCount,
                DrillRequest = new DashboardDrillRequest
                {
                    TargetModule = "transactions",
                    TargetSubCode = "jurnal_belum_posting",
                    CompanyId = request.CompanyId,
                    LocationId = request.LocationId,
                    PeriodStart = request.PeriodStart,
                    PeriodGranularity = request.PeriodGranularity,
                    Status = "UNPOSTED"
                }
            });
        }

        if (balanceSheetDifference != 0m)
        {
            alerts.Add(new DashboardAlertItem
            {
                Key = "balance_difference",
                Title = "Selisih Neraca",
                Message = $"Persamaan neraca selisih {balanceSheetDifference:N2}.",
                ActionLabel = "Lihat neraca",
                Severity = DashboardAlertSeverity.Critical,
                Count = 1,
                DrillRequest = new DashboardDrillRequest
                {
                    TargetModule = "reports",
                    TargetSubCode = "laporan_neraca",
                    CompanyId = request.CompanyId,
                    LocationId = request.LocationId,
                    PeriodStart = request.PeriodStart,
                    PeriodGranularity = request.PeriodGranularity
                }
            });
        }

        if (abnormalTransactionCount > 0)
        {
            alerts.Add(new DashboardAlertItem
            {
                Key = "abnormal_transactions",
                Title = "Transaksi Abnormal",
                Message = $"{abnormalTransactionCount:N0} jurnal posted melebihi ambang statistik periode ini.",
                ActionLabel = "Review mutasi akun",
                Severity = DashboardAlertSeverity.Warning,
                Count = abnormalTransactionCount,
                DrillRequest = new DashboardDrillRequest
                {
                    TargetModule = "reports",
                    TargetSubCode = "mutasi_akun",
                    CompanyId = request.CompanyId,
                    LocationId = request.LocationId,
                    PeriodStart = request.PeriodStart,
                    PeriodGranularity = request.PeriodGranularity
                }
            });
        }

        if (cashBank.TotalBalance < 0m || cashBank.Accounts.Any(x => x.EndingBalance < 0m))
        {
            alerts.Add(new DashboardAlertItem
            {
                Key = "negative_cash",
                Title = "Saldo Kas / Bank Negatif",
                Message = "Ada akun kas/bank dengan saldo negatif.",
                ActionLabel = "Lihat arus kas",
                Severity = DashboardAlertSeverity.Critical,
                Count = cashBank.Accounts.Count(x => x.EndingBalance < 0m),
                DrillRequest = new DashboardDrillRequest
                {
                    TargetModule = "reports",
                    TargetSubCode = "laporan_arus_kas",
                    CompanyId = request.CompanyId,
                    LocationId = request.LocationId,
                    PeriodStart = request.PeriodStart,
                    PeriodGranularity = request.PeriodGranularity
                }
            });
        }
        else if (cashBank.Accounts.Count > 0 && cashBank.TotalBalance <= DashboardLowBalanceThreshold)
        {
            alerts.Add(new DashboardAlertItem
            {
                Key = "low_cash",
                Title = "Saldo Kas Rendah",
                Message = $"Total kas dan bank berada di bawah ambang {DashboardLowBalanceThreshold:N0}.",
                ActionLabel = "Lihat arus kas",
                Severity = DashboardAlertSeverity.Warning,
                Count = 1,
                DrillRequest = new DashboardDrillRequest
                {
                    TargetModule = "reports",
                    TargetSubCode = "laporan_arus_kas",
                    CompanyId = request.CompanyId,
                    LocationId = request.LocationId,
                    PeriodStart = request.PeriodStart,
                    PeriodGranularity = request.PeriodGranularity
                }
            });
        }

        if (inventory.LowStockCount > 0)
        {
            alerts.Add(new DashboardAlertItem
            {
                Key = "inventory_low_stock",
                Title = "Low Stock Alert",
                Message = $"{inventory.LowStockCount:N0} item berada di bawah batas minimum dashboard.",
                ActionLabel = "Buka alert stok",
                Severity = DashboardAlertSeverity.Warning,
                Count = inventory.LowStockCount,
                DrillRequest = new DashboardDrillRequest
                {
                    TargetModule = "inventory",
                    TargetSubCode = "low_stock",
                    CompanyId = request.CompanyId,
                    LocationId = request.LocationId,
                    PeriodStart = request.PeriodStart,
                    PeriodGranularity = request.PeriodGranularity
                }
            });
        }

        if (!assetComposition.Any())
        {
            alerts.Add(new DashboardAlertItem
            {
                Key = "missing_assets",
                Title = "Komposisi Aset Belum Tersedia",
                Message = "Belum ada saldo aset yang dapat divisualisasikan untuk filter saat ini.",
                ActionLabel = "Lihat neraca",
                Severity = DashboardAlertSeverity.Info,
                Count = 0,
                DrillRequest = new DashboardDrillRequest
                {
                    TargetModule = "reports",
                    TargetSubCode = "laporan_neraca",
                    CompanyId = request.CompanyId,
                    LocationId = request.LocationId,
                    PeriodStart = request.PeriodStart,
                    PeriodGranularity = request.PeriodGranularity
                }
            });
        }

        return alerts;
    }

    private static decimal GetValue(IReadOnlyDictionary<string, decimal> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : 0m;
    }

    private sealed class TrendPointBuilder
    {
        public decimal Revenue { get; set; }

        public decimal Expense { get; set; }
    }
}
