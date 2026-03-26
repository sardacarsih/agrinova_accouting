using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<List<ManagedTrialBalanceRow>> GetTrialBalanceAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedTrialBalanceRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var monthStart = GetPeriodMonthStart(periodMonth);

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT a.account_code,
       a.account_name,
       COALESCE(SUM(le.debit), 0) AS total_debit,
       COALESCE(SUM(le.credit), 0) AS total_credit
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month = @period_month
GROUP BY a.account_code, a.account_name
HAVING COALESCE(SUM(le.debit), 0) <> 0
    OR COALESCE(SUM(le.credit), 0) <> 0
ORDER BY a.account_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", monthStart);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedTrialBalanceRow
            {
                AccountCode = reader.GetString(0),
                AccountName = reader.GetString(1),
                TotalDebit = reader.GetDecimal(2),
                TotalCredit = reader.GetDecimal(3)
            });
        }

        return output;
    }

    public async Task<List<ManagedProfitLossRow>> GetProfitLossAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedProfitLossRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var monthStart = GetPeriodMonthStart(periodMonth);

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT CASE
           WHEN upper(a.account_type) = 'REVENUE' THEN 'Pendapatan'
           WHEN upper(a.account_type) = 'EXPENSE' THEN 'Beban'
           ELSE 'Lainnya'
       END AS section_name,
       a.account_code,
       a.account_name,
       CASE
           WHEN upper(a.account_type) = 'REVENUE' THEN COALESCE(SUM(le.credit - le.debit), 0)
           WHEN upper(a.account_type) = 'EXPENSE' THEN COALESCE(SUM(le.debit - le.credit), 0)
           ELSE 0
       END AS amount
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month = @period_month
  AND upper(a.account_type) IN ('REVENUE', 'EXPENSE')
GROUP BY upper(a.account_type), a.account_code, a.account_name
HAVING CASE
           WHEN upper(a.account_type) = 'REVENUE' THEN COALESCE(SUM(le.credit - le.debit), 0)
           WHEN upper(a.account_type) = 'EXPENSE' THEN COALESCE(SUM(le.debit - le.credit), 0)
           ELSE 0
       END <> 0
ORDER BY CASE
             WHEN upper(a.account_type) = 'REVENUE' THEN 1
             WHEN upper(a.account_type) = 'EXPENSE' THEN 2
             ELSE 3
         END,
         a.account_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", monthStart);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedProfitLossRow
            {
                Section = reader.GetString(0),
                AccountCode = reader.GetString(1),
                AccountName = reader.GetString(2),
                Amount = reader.GetDecimal(3)
            });
        }

        return output;
    }

    public async Task<List<ManagedBalanceSheetRow>> GetBalanceSheetAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedBalanceSheetRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var monthStart = GetPeriodMonthStart(periodMonth);

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var rebuildTransaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await RebuildAccountHierarchyInternalAsync(
                connection,
                rebuildTransaction,
                companyId,
                "SYSTEM",
                cancellationToken);
            await rebuildTransaction.CommitAsync(cancellationToken);
        }

        var nodesById = new Dictionary<long, BalanceSheetHierarchyNode>();
        await using (var accountCommand = new NpgsqlCommand(@"
SELECT a.id,
       a.account_code,
       a.account_name,
       upper(a.account_type) AS account_type,
       a.parent_account_id,
       COALESCE(p.account_code, '') AS parent_account_code,
       a.is_posting
FROM gl_accounts a
LEFT JOIN gl_accounts p ON p.id = a.parent_account_id
WHERE a.company_id = @company_id
  AND a.is_active = TRUE
  AND upper(a.account_type) IN ('ASSET', 'LIABILITY', 'EQUITY')
ORDER BY a.account_code;", connection))
        {
            accountCommand.Parameters.AddWithValue("company_id", companyId);
            await using var accountReader = await accountCommand.ExecuteReaderAsync(cancellationToken);
            while (await accountReader.ReadAsync(cancellationToken))
            {
                var node = new BalanceSheetHierarchyNode
                {
                    Id = accountReader.GetInt64(0),
                    AccountCode = accountReader.GetString(1),
                    AccountName = accountReader.GetString(2),
                    AccountType = NormalizeAccountType(accountReader.GetString(3), accountReader.GetString(1)),
                    ParentAccountId = accountReader.IsDBNull(4) ? null : accountReader.GetInt64(4),
                    ParentAccountCode = accountReader.GetString(5),
                    IsPosting = !accountReader.IsDBNull(6) && accountReader.GetBoolean(6)
                };
                nodesById[node.Id] = node;
            }
        }

        if (nodesById.Count == 0)
        {
            return output;
        }

        await using (var amountCommand = new NpgsqlCommand(@"
SELECT le.account_id,
       upper(a.account_type) AS account_type,
       COALESCE(SUM(le.debit), 0) AS total_debit,
       COALESCE(SUM(le.credit), 0) AS total_credit
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month <= @period_month
  AND upper(a.account_type) IN ('ASSET', 'LIABILITY', 'EQUITY')
GROUP BY le.account_id, upper(a.account_type);", connection))
        {
            amountCommand.Parameters.AddWithValue("company_id", companyId);
            amountCommand.Parameters.AddWithValue("location_id", locationId);
            amountCommand.Parameters.AddWithValue("period_month", monthStart);

            await using var amountReader = await amountCommand.ExecuteReaderAsync(cancellationToken);
            while (await amountReader.ReadAsync(cancellationToken))
            {
                var accountId = amountReader.GetInt64(0);
                if (!nodesById.TryGetValue(accountId, out var node))
                {
                    continue;
                }

                var accountType = amountReader.GetString(1);
                var totalDebit = amountReader.GetDecimal(2);
                var totalCredit = amountReader.GetDecimal(3);
                node.OwnAmount = accountType == "ASSET"
                    ? totalDebit - totalCredit
                    : totalCredit - totalDebit;
            }
        }

        foreach (var node in nodesById.Values)
        {
            if (node.ParentAccountId.HasValue &&
                nodesById.TryGetValue(node.ParentAccountId.Value, out var parent) &&
                !ReferenceEquals(parent, node))
            {
                parent.Children.Add(node);
            }
        }

        static int GetSectionOrder(string accountType)
        {
            return accountType switch
            {
                "ASSET" => 1,
                "LIABILITY" => 2,
                "EQUITY" => 3,
                _ => 4
            };
        }

        static string GetSectionName(string accountType)
        {
            return accountType switch
            {
                "ASSET" => "Aset",
                "LIABILITY" => "Kewajiban",
                "EQUITY" => "Ekuitas",
                _ => "Lainnya"
            };
        }

        static void SortChildren(BalanceSheetHierarchyNode node)
        {
            node.Children.Sort((left, right) => string.Compare(left.AccountCode, right.AccountCode, StringComparison.OrdinalIgnoreCase));
            foreach (var child in node.Children)
            {
                SortChildren(child);
            }
        }

        static void MarkVisibility(BalanceSheetHierarchyNode node)
        {
            var hasVisibleChild = false;
            var childTotal = 0m;

            foreach (var child in node.Children)
            {
                MarkVisibility(child);
                if (child.Visible)
                {
                    hasVisibleChild = true;
                }

                childTotal += child.RollupAmount;
            }

            node.RollupAmount = node.OwnAmount + childTotal;
            node.Visible = node.RollupAmount != 0m || hasVisibleChild;
        }

        static void AppendVisibleRows(
            BalanceSheetHierarchyNode node,
            int level,
            List<ManagedBalanceSheetRow> rows)
        {
            if (!node.Visible)
            {
                return;
            }

            rows.Add(new ManagedBalanceSheetRow
            {
                Section = GetSectionName(node.AccountType),
                AccountCode = node.AccountCode,
                ParentAccountCode = node.ParentAccountCode,
                AccountName = node.AccountName,
                Level = level,
                HasChildren = node.Children.Any(child => child.Visible),
                IsPosting = node.IsPosting,
                Amount = node.RollupAmount
            });

            foreach (var child in node.Children)
            {
                AppendVisibleRows(child, level + 1, rows);
            }
        }

        var roots = nodesById.Values
            .Where(node => !node.ParentAccountId.HasValue || !nodesById.ContainsKey(node.ParentAccountId.Value))
            .OrderBy(node => GetSectionOrder(node.AccountType))
            .ThenBy(node => node.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in roots)
        {
            SortChildren(root);
            MarkVisibility(root);
        }

        foreach (var root in roots)
        {
            AppendVisibleRows(root, level: 1, output);
        }

        return output;
    }

    public async Task<List<ManagedGeneralLedgerRow>> GetGeneralLedgerAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        string accountCode = "",
        string keyword = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedGeneralLedgerRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var monthStart = GetPeriodMonthStart(periodMonth);
        var normalizedAccountCode = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedKeyword = (keyword ?? string.Empty).Trim();
        var keywordLike = string.IsNullOrWhiteSpace(normalizedKeyword)
            ? string.Empty
            : $"%{normalizedKeyword}%";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT le.journal_date,
       le.journal_no,
       COALESCE(h.reference_no, '') AS reference_no,
       COALESCE(h.description, '') AS journal_description,
       a.account_code,
       a.account_name,
       COALESCE(le.description, '') AS line_description,
       le.debit,
       le.credit,
       SUM(
           CASE
               WHEN upper(a.account_type) IN ('ASSET', 'EXPENSE') THEN le.debit - le.credit
               ELSE le.credit - le.debit
           END
       ) OVER (
           PARTITION BY a.account_code
           ORDER BY le.journal_date, le.journal_no, le.journal_line_no, le.id
           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
       ) AS running_balance
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
LEFT JOIN gl_journal_headers h ON h.id = le.journal_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month = @period_month
  AND (@account_code = '' OR upper(a.account_code) = @account_code)
  AND (
      @keyword = ''
      OR le.journal_no ILIKE @keyword_like
      OR COALESCE(h.reference_no, '') ILIKE @keyword_like
      OR COALESCE(h.description, '') ILIKE @keyword_like
      OR COALESCE(le.description, '') ILIKE @keyword_like
      OR a.account_name ILIKE @keyword_like
  )
ORDER BY a.account_code, le.journal_date, le.journal_no, le.journal_line_no, le.id;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", monthStart);
        command.Parameters.AddWithValue("account_code", normalizedAccountCode);
        command.Parameters.AddWithValue("keyword", normalizedKeyword);
        command.Parameters.AddWithValue("keyword_like", keywordLike);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedGeneralLedgerRow
            {
                JournalDate = reader.GetDateTime(0),
                JournalNo = reader.GetString(1),
                ReferenceNo = reader.GetString(2),
                JournalDescription = reader.GetString(3),
                AccountCode = reader.GetString(4),
                AccountName = reader.GetString(5),
                LineDescription = reader.GetString(6),
                Debit = reader.GetDecimal(7),
                Credit = reader.GetDecimal(8),
                RunningBalance = reader.GetDecimal(9)
            });
        }

        return output;
    }

    public async Task<List<ManagedSubLedgerRow>> GetSubLedgerAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        string accountCode = "",
        string keyword = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedSubLedgerRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var monthStart = GetPeriodMonthStart(periodMonth);
        var normalizedAccountCode = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedKeyword = (keyword ?? string.Empty).Trim();
        var keywordLike = string.IsNullOrWhiteSpace(normalizedKeyword)
            ? string.Empty
            : $"%{normalizedKeyword}%";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT le.journal_date,
       le.journal_no,
       COALESCE(h.reference_no, '') AS reference_no,
       COALESCE(h.description, '') AS journal_description,
       a.account_code,
       a.account_name,
       COALESCE(le.department_code, '') AS department_code,
       COALESCE(le.project_code, '') AS project_code,
       COALESCE(le.cost_center_code, '') AS cost_center_code,
       COALESCE(le.description, '') AS line_description,
       le.debit,
       le.credit,
       SUM(
           CASE
               WHEN upper(a.account_type) IN ('ASSET', 'EXPENSE') THEN le.debit - le.credit
               ELSE le.credit - le.debit
           END
       ) OVER (
           PARTITION BY
               a.account_code,
               COALESCE(le.department_code, ''),
               COALESCE(le.project_code, ''),
               COALESCE(le.cost_center_code, '')
           ORDER BY le.journal_date, le.journal_no, le.journal_line_no, le.id
           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
       ) AS running_balance
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
LEFT JOIN gl_journal_headers h ON h.id = le.journal_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month = @period_month
  AND (@account_code = '' OR upper(a.account_code) = @account_code)
  AND (
      @keyword = ''
      OR le.journal_no ILIKE @keyword_like
      OR COALESCE(h.reference_no, '') ILIKE @keyword_like
      OR COALESCE(h.description, '') ILIKE @keyword_like
      OR COALESCE(le.description, '') ILIKE @keyword_like
      OR a.account_name ILIKE @keyword_like
      OR COALESCE(le.department_code, '') ILIKE @keyword_like
      OR COALESCE(le.project_code, '') ILIKE @keyword_like
      OR COALESCE(le.cost_center_code, '') ILIKE @keyword_like
  )
ORDER BY
    a.account_code,
    COALESCE(le.department_code, ''),
    COALESCE(le.project_code, ''),
    COALESCE(le.cost_center_code, ''),
    le.journal_date,
    le.journal_no,
    le.journal_line_no,
    le.id;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", monthStart);
        command.Parameters.AddWithValue("account_code", normalizedAccountCode);
        command.Parameters.AddWithValue("keyword", normalizedKeyword);
        command.Parameters.AddWithValue("keyword_like", keywordLike);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedSubLedgerRow
            {
                JournalDate = reader.GetDateTime(0),
                JournalNo = reader.GetString(1),
                ReferenceNo = reader.GetString(2),
                JournalDescription = reader.GetString(3),
                AccountCode = reader.GetString(4),
                AccountName = reader.GetString(5),
                DepartmentCode = reader.GetString(6),
                ProjectCode = reader.GetString(7),
                CostCenterCode = reader.GetString(8),
                LineDescription = reader.GetString(9),
                Debit = reader.GetDecimal(10),
                Credit = reader.GetDecimal(11),
                RunningBalance = reader.GetDecimal(12)
            });
        }

        return output;
    }

    public async Task<List<ManagedCashFlowRow>> GetCashFlowAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedCashFlowRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var monthStart = GetPeriodMonthStart(periodMonth);

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
WITH cash_accounts AS (
    SELECT a.id,
           a.account_code,
           a.account_name
    FROM gl_accounts a
    WHERE a.company_id = @company_id
      AND a.is_active = TRUE
      AND upper(a.account_type) = 'ASSET'
      AND (
          upper(a.account_code) LIKE '%11100%'
          OR a.account_name ILIKE '%kas%'
          OR a.account_name ILIKE '%bank%'
      )
),
cash_rollup AS (
    SELECT le.account_id,
           COALESCE(SUM(CASE WHEN le.period_month < @period_month THEN le.debit - le.credit ELSE 0 END), 0) AS opening_balance,
           COALESCE(SUM(CASE WHEN le.period_month = @period_month THEN le.debit ELSE 0 END), 0) AS cash_in,
           COALESCE(SUM(CASE WHEN le.period_month = @period_month THEN le.credit ELSE 0 END), 0) AS cash_out
    FROM gl_ledger_entries le
    JOIN cash_accounts ca ON ca.id = le.account_id
    WHERE le.company_id = @company_id
      AND le.location_id = @location_id
      AND le.period_month <= @period_month
    GROUP BY le.account_id
)
SELECT ca.account_code,
       ca.account_name,
       COALESCE(cr.opening_balance, 0) AS opening_balance,
       COALESCE(cr.cash_in, 0) AS cash_in,
       COALESCE(cr.cash_out, 0) AS cash_out,
       COALESCE(cr.opening_balance, 0) + COALESCE(cr.cash_in, 0) - COALESCE(cr.cash_out, 0) AS ending_balance
FROM cash_accounts ca
LEFT JOIN cash_rollup cr ON cr.account_id = ca.id
WHERE COALESCE(cr.opening_balance, 0) <> 0
   OR COALESCE(cr.cash_in, 0) <> 0
   OR COALESCE(cr.cash_out, 0) <> 0
ORDER BY ca.account_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", monthStart);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedCashFlowRow
            {
                AccountCode = reader.GetString(0),
                AccountName = reader.GetString(1),
                OpeningBalance = reader.GetDecimal(2),
                CashIn = reader.GetDecimal(3),
                CashOut = reader.GetDecimal(4),
                EndingBalance = reader.GetDecimal(5)
            });
        }

        return output;
    }

    public async Task<List<ManagedAccountMutationRow>> GetAccountMutationAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        string accountCode = "",
        string keyword = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedAccountMutationRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var monthStart = GetPeriodMonthStart(periodMonth);
        var normalizedAccountCode = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedKeyword = (keyword ?? string.Empty).Trim();
        var keywordLike = string.IsNullOrWhiteSpace(normalizedKeyword)
            ? string.Empty
            : $"%{normalizedKeyword}%";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
WITH ledger_rollup AS (
    SELECT le.account_id,
           COALESCE(SUM(CASE WHEN le.period_month < @period_month THEN le.debit ELSE 0 END), 0) AS opening_debit,
           COALESCE(SUM(CASE WHEN le.period_month < @period_month THEN le.credit ELSE 0 END), 0) AS opening_credit,
           COALESCE(SUM(CASE WHEN le.period_month = @period_month THEN le.debit ELSE 0 END), 0) AS mutation_debit,
           COALESCE(SUM(CASE WHEN le.period_month = @period_month THEN le.credit ELSE 0 END), 0) AS mutation_credit
    FROM gl_ledger_entries le
    WHERE le.company_id = @company_id
      AND le.location_id = @location_id
      AND le.period_month <= @period_month
    GROUP BY le.account_id
)
SELECT a.account_code,
       a.account_name,
       CASE
           WHEN upper(a.account_type) IN ('ASSET', 'EXPENSE') THEN lr.opening_debit - lr.opening_credit
           ELSE lr.opening_credit - lr.opening_debit
       END AS opening_balance,
       lr.mutation_debit,
       lr.mutation_credit,
       CASE
           WHEN upper(a.account_type) IN ('ASSET', 'EXPENSE') THEN
               (lr.opening_debit - lr.opening_credit) + (lr.mutation_debit - lr.mutation_credit)
           ELSE
               (lr.opening_credit - lr.opening_debit) + (lr.mutation_credit - lr.mutation_debit)
       END AS ending_balance
FROM ledger_rollup lr
JOIN gl_accounts a ON a.id = lr.account_id
WHERE a.company_id = @company_id
  AND (@account_code = '' OR upper(a.account_code) = @account_code)
  AND (
      @keyword = ''
      OR a.account_code ILIKE @keyword_like
      OR a.account_name ILIKE @keyword_like
  )
  AND (
      lr.opening_debit <> 0
      OR lr.opening_credit <> 0
      OR lr.mutation_debit <> 0
      OR lr.mutation_credit <> 0
  )
ORDER BY a.account_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", monthStart);
        command.Parameters.AddWithValue("account_code", normalizedAccountCode);
        command.Parameters.AddWithValue("keyword", normalizedKeyword);
        command.Parameters.AddWithValue("keyword_like", keywordLike);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedAccountMutationRow
            {
                AccountCode = reader.GetString(0),
                AccountName = reader.GetString(1),
                OpeningBalance = reader.GetDecimal(2),
                MutationDebit = reader.GetDecimal(3),
                MutationCredit = reader.GetDecimal(4),
                EndingBalance = reader.GetDecimal(5)
            });
        }

        return output;
    }

    private readonly record struct ClosingJournalResult(bool IsSuccess, string Message, string JournalNo);

    private readonly record struct AccountingEquationSnapshot(decimal EquationDifference);

    private sealed class ClosingLine
    {
        public long AccountId { get; init; }

        public string Description { get; init; } = string.Empty;

        public decimal Debit { get; init; }

        public decimal Credit { get; init; }
    }

    private static async Task<bool> HasDraftJournalInPeriodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken)
    {
        var monthStart = GetPeriodMonthStart(periodMonth);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        await using var command = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM gl_journal_headers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND journal_date >= @date_from
  AND journal_date <= @date_to
  AND upper(status) <> 'POSTED';", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", monthStart);
        command.Parameters.AddWithValue("date_to", monthEnd);

        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    private static async Task<long> EnsureRetainedEarningsAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        string actor,
        CancellationToken cancellationToken)
    {
        var retainedEarningsCode = await BuildRetainedEarningsCodeAsync(
            connection,
            transaction,
            companyId,
            locationId,
            cancellationToken);

        await using (var lookup = new NpgsqlCommand(@"
SELECT id
FROM gl_accounts
WHERE company_id = @company_id
  AND account_code = @account_code
FOR UPDATE;", connection, transaction))
        {
            lookup.Parameters.AddWithValue("company_id", companyId);
            lookup.Parameters.AddWithValue("account_code", retainedEarningsCode);
            var existing = await lookup.ExecuteScalarAsync(cancellationToken);
            if (existing is not null && existing != DBNull.Value)
            {
                var accountId = Convert.ToInt64(existing);
                await using var reactivate = new NpgsqlCommand(@"
UPDATE gl_accounts
SET is_active = TRUE,
    account_type = 'EQUITY',
    normal_balance = 'C',
    is_posting = TRUE,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                reactivate.Parameters.AddWithValue("id", accountId);
                reactivate.Parameters.AddWithValue("updated_by", actor);
                await reactivate.ExecuteNonQueryAsync(cancellationToken);
                return accountId;
            }
        }

        await using var insert = new NpgsqlCommand(@"
INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    is_posting,
    is_active,
    created_by,
    created_at,
    updated_by,
    updated_at)
VALUES (
    @company_id,
    @account_code,
    @account_name,
    'EQUITY',
    'C',
    TRUE,
    TRUE,
    @actor,
    NOW(),
    @actor,
    NOW())
RETURNING id;", connection, transaction);
        insert.Parameters.AddWithValue("company_id", companyId);
        insert.Parameters.AddWithValue("account_code", retainedEarningsCode);
        insert.Parameters.AddWithValue("account_name", "Laba Ditahan");
        insert.Parameters.AddWithValue("actor", actor);
        return Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> InsertLedgerEntriesForJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long journalId,
        long companyId,
        long locationId,
        DateTime periodMonth,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var insertLedger = new NpgsqlCommand(@"
INSERT INTO gl_ledger_entries (
    company_id,
    location_id,
    period_month,
    journal_id,
    journal_no,
    journal_date,
    journal_line_no,
    account_id,
    debit,
    credit,
    description,
    department_code,
    project_code,
    cost_center_code,
    posted_by,
    posted_at,
    created_at,
    updated_at)
SELECT h.company_id,
       h.location_id,
       @period_month,
       h.id,
       h.journal_no,
       h.journal_date,
       d.line_no,
       d.account_id,
       d.debit,
       d.credit,
       COALESCE(d.description, ''),
       COALESCE(d.department_code, ''),
       COALESCE(d.project_code, ''),
       COALESCE(d.cost_center_code, ''),
       @posted_by,
       NOW(),
       NOW(),
       NOW()
FROM gl_journal_headers h
JOIN gl_journal_details d ON d.header_id = h.id
WHERE h.id = @journal_id
  AND h.company_id = @company_id
  AND h.location_id = @location_id;", connection, transaction);
        insertLedger.Parameters.AddWithValue("period_month", GetPeriodMonthStart(periodMonth));
        insertLedger.Parameters.AddWithValue("posted_by", actor);
        insertLedger.Parameters.AddWithValue("journal_id", journalId);
        insertLedger.Parameters.AddWithValue("company_id", companyId);
        insertLedger.Parameters.AddWithValue("location_id", locationId);
        return await insertLedger.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ClosingJournalResult> EnsurePeriodClosingJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime periodMonth,
        string actor,
        CancellationToken cancellationToken)
    {
        var monthStart = GetPeriodMonthStart(periodMonth);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var journalNo = $"CLS-{monthStart:yyyyMM}-{companyId}-{locationId}";

        var existingPostedIds = new List<long>();
        await using (var checkExisting = new NpgsqlCommand(@"
SELECT id, status
FROM gl_journal_headers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND journal_no = @journal_no
FOR UPDATE;", connection, transaction))
        {
            checkExisting.Parameters.AddWithValue("company_id", companyId);
            checkExisting.Parameters.AddWithValue("location_id", locationId);
            checkExisting.Parameters.AddWithValue("journal_no", journalNo);
            await using var reader = await checkExisting.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var status = reader.GetString(1);
                if (!string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase))
                {
                    return new ClosingJournalResult(
                        false,
                        $"Jurnal penutup {journalNo} ditemukan namun belum POSTED. Perbaiki dulu sebelum tutup periode.",
                        string.Empty);
                }

                existingPostedIds.Add(reader.GetInt64(0));
            }
        }

        if (existingPostedIds.Count > 0)
        {
            await using var deleteExisting = new NpgsqlCommand(@"
DELETE FROM gl_journal_headers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND journal_no = @journal_no;",
                connection,
                transaction);
            deleteExisting.Parameters.AddWithValue("company_id", companyId);
            deleteExisting.Parameters.AddWithValue("location_id", locationId);
            deleteExisting.Parameters.AddWithValue("journal_no", journalNo);
            await deleteExisting.ExecuteNonQueryAsync(cancellationToken);
        }

        var closingLines = new List<ClosingLine>();
        await using (var loadNominal = new NpgsqlCommand(@"
SELECT a.id,
       upper(a.account_type) AS account_type,
       COALESCE(SUM(le.debit), 0) AS debit_total,
       COALESCE(SUM(le.credit), 0) AS credit_total
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month = @period_month
  AND upper(a.account_type) IN ('REVENUE', 'EXPENSE')
GROUP BY a.id, upper(a.account_type)
ORDER BY a.id;", connection, transaction))
        {
            loadNominal.Parameters.AddWithValue("company_id", companyId);
            loadNominal.Parameters.AddWithValue("location_id", locationId);
            loadNominal.Parameters.AddWithValue("period_month", monthStart);
            await using var reader = await loadNominal.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var accountId = reader.GetInt64(0);
                var accountType = reader.GetString(1);
                var debitTotal = reader.GetDecimal(2);
                var creditTotal = reader.GetDecimal(3);

                if (string.Equals(accountType, "REVENUE", StringComparison.OrdinalIgnoreCase))
                {
                    var closeAmount = Math.Round(creditTotal - debitTotal, 2);
                    if (closeAmount > 0)
                    {
                        closingLines.Add(new ClosingLine
                        {
                            AccountId = accountId,
                            Description = $"Closing Pendapatan {monthStart:yyyy-MM}",
                            Debit = closeAmount,
                            Credit = 0
                        });
                    }
                }
                else if (string.Equals(accountType, "EXPENSE", StringComparison.OrdinalIgnoreCase))
                {
                    var closeAmount = Math.Round(debitTotal - creditTotal, 2);
                    if (closeAmount > 0)
                    {
                        closingLines.Add(new ClosingLine
                        {
                            AccountId = accountId,
                            Description = $"Closing Beban {monthStart:yyyy-MM}",
                            Debit = 0,
                            Credit = closeAmount
                        });
                    }
                }
            }
        }

        if (closingLines.Count == 0)
        {
            return new ClosingJournalResult(true, "Tidak ada saldo nominal untuk ditutup.", string.Empty);
        }

        var retainedEarningsId = await EnsureRetainedEarningsAccountAsync(connection, transaction, companyId, locationId, actor, cancellationToken);

        var totalDebit = closingLines.Sum(x => x.Debit);
        var totalCredit = closingLines.Sum(x => x.Credit);
        var diff = Math.Round(totalDebit - totalCredit, 2);
        if (diff > 0)
        {
            closingLines.Add(new ClosingLine
            {
                AccountId = retainedEarningsId,
                Description = $"Laba Ditahan {monthStart:yyyy-MM}",
                Debit = 0,
                Credit = diff
            });
        }
        else if (diff < 0)
        {
            closingLines.Add(new ClosingLine
            {
                AccountId = retainedEarningsId,
                Description = $"Laba Ditahan {monthStart:yyyy-MM}",
                Debit = Math.Abs(diff),
                Credit = 0
            });
        }

        totalDebit = closingLines.Sum(x => x.Debit);
        totalCredit = closingLines.Sum(x => x.Credit);
        if (Math.Abs(totalDebit - totalCredit) > 0.01m)
        {
            return new ClosingJournalResult(
                false,
                $"Jurnal penutup tidak seimbang. Debit={totalDebit:N2}, Kredit={totalCredit:N2}.",
                string.Empty);
        }

        long closingJournalId;
        await using (var insertHeader = new NpgsqlCommand(@"
INSERT INTO gl_journal_headers (
    company_id,
    location_id,
    journal_no,
    journal_date,
    period_month,
    reference_no,
    description,
    status,
    posted_at,
    posted_by,
    created_by,
    created_at,
    updated_at)
VALUES (
    @company_id,
    @location_id,
    @journal_no,
    @journal_date,
    @period_month,
    @reference_no,
    @description,
    'POSTED',
    NOW(),
    @actor,
    @actor,
    NOW(),
    NOW())
RETURNING id;", connection, transaction))
        {
            insertHeader.Parameters.AddWithValue("company_id", companyId);
            insertHeader.Parameters.AddWithValue("location_id", locationId);
            insertHeader.Parameters.AddWithValue("journal_no", journalNo);
            insertHeader.Parameters.AddWithValue("journal_date", monthEnd);
            insertHeader.Parameters.AddWithValue("period_month", monthStart);
            insertHeader.Parameters.AddWithValue("reference_no", $"CLOSING-{monthStart:yyyyMM}");
            insertHeader.Parameters.AddWithValue("description", $"Jurnal penutup periode {monthStart:yyyy-MM}");
            insertHeader.Parameters.AddWithValue("actor", actor);
            closingJournalId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken));
        }

        for (var i = 0; i < closingLines.Count; i++)
        {
            var line = closingLines[i];
            await using var insertDetail = new NpgsqlCommand(@"
INSERT INTO gl_journal_details (
    header_id,
    line_no,
    account_id,
    description,
    debit,
    credit,
    department_code,
    project_code,
    cost_center_code,
    created_at,
    updated_at)
VALUES (
    @header_id,
    @line_no,
    @account_id,
    @description,
    @debit,
    @credit,
    '',
    '',
    '',
    NOW(),
    NOW());", connection, transaction);
            insertDetail.Parameters.AddWithValue("header_id", closingJournalId);
            insertDetail.Parameters.AddWithValue("line_no", i + 1);
            insertDetail.Parameters.AddWithValue("account_id", line.AccountId);
            insertDetail.Parameters.AddWithValue("description", line.Description);
            insertDetail.Parameters.AddWithValue("debit", line.Debit);
            insertDetail.Parameters.AddWithValue("credit", line.Credit);
            await insertDetail.ExecuteNonQueryAsync(cancellationToken);
        }

        var ledgerRows = await InsertLedgerEntriesForJournalAsync(
            connection,
            transaction,
            closingJournalId,
            companyId,
            locationId,
            monthStart,
            actor,
            cancellationToken);
        if (ledgerRows <= 0)
        {
            return new ClosingJournalResult(false, "Gagal membentuk ledger jurnal penutup.", string.Empty);
        }

        await InsertAuditLogAsync(
            connection,
            transaction,
            "JOURNAL",
            closingJournalId,
            "CLOSE_PERIOD",
            actor,
            $"journal_no={journalNo};company={companyId};location={locationId};period={monthStart:yyyy-MM};lines={closingLines.Count};debit={totalDebit};credit={totalCredit}",
            cancellationToken);

        return new ClosingJournalResult(true, "Jurnal penutup berhasil dibuat.", journalNo);
    }

    private static async Task<decimal> ComputeBalanceSheetDifferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT COALESCE(SUM(CASE WHEN upper(a.account_type) = 'ASSET' THEN le.debit - le.credit ELSE 0 END), 0) AS assets,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'LIABILITY' THEN le.credit - le.debit ELSE 0 END), 0) AS liabilities,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'EQUITY' THEN le.credit - le.debit ELSE 0 END), 0) AS equity
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month <= @period_month;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", GetPeriodMonthStart(periodMonth));

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
}
