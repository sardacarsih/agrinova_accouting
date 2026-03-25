using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private static async Task<AccountingEquationSnapshot> ComputeAccountingEquationSnapshotAsync(
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
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'EQUITY' THEN le.credit - le.debit ELSE 0 END), 0) AS equity,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'REVENUE' THEN le.credit - le.debit ELSE 0 END), 0) AS revenue,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'EXPENSE' THEN le.debit - le.credit ELSE 0 END), 0) AS expense
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
            return new AccountingEquationSnapshot(0);
        }

        var assets = reader.GetDecimal(0);
        var liabilities = reader.GetDecimal(1);
        var equity = reader.GetDecimal(2);
        var revenue = reader.GetDecimal(3);
        var expense = reader.GetDecimal(4);
        var lhs = assets + expense;
        var rhs = liabilities + equity + revenue;
        return new AccountingEquationSnapshot(Math.Round(lhs - rhs, 2));
    }

    private static string NormalizeAccountType(string? accountType, string? accountCode = null)
    {
        var normalized = (accountType ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return InferAccountTypeFromCode(accountCode);
        }

        if (AllowedAccountTypes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return InferAccountTypeFromCode(accountCode);
    }

    private static string InferAccountTypeFromCode(string? accountCode)
    {
        var code = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        var first = code.Length >= 9 && code[2] == '.' && code[8] == '.' && char.IsDigit(code[3])
            ? code[3]
            : (code.Length > 0 ? code[0] : '\0');

        return first switch
        {
            '1' => "ASSET",
            '2' => "LIABILITY",
            '3' => "EQUITY",
            '4' => "REVENUE",
            '5' => "EXPENSE",
            _ => "ASSET"
        };
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

    private static bool TrySplitSegmentedAccountCode(
        string accountCode,
        out string locationCode,
        out string majorCode,
        out string minorCode)
    {
        locationCode = string.Empty;
        majorCode = string.Empty;
        minorCode = string.Empty;

        var code = (accountCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!IsSegmentedAccountCode(code))
        {
            return false;
        }

        locationCode = code[..2];
        majorCode = code.Substring(3, 5);
        minorCode = code.Substring(9, 3);
        return true;
    }

    private static bool IsSummaryAccountCode(string accountCode)
    {
        return TrySplitSegmentedAccountCode(accountCode, out _, out _, out var minorCode) &&
               string.Equals(minorCode, "000", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildParentAccountCode(string locationCode, string majorCode)
    {
        return $"{locationCode}.{majorCode}.000";
    }

    private sealed class AccountHierarchyEntry
    {
        public long Id { get; init; }

        public string Code { get; init; } = string.Empty;

        public string AccountType { get; set; } = "ASSET";

        public bool IsPosting { get; set; } = true;

        public int HierarchyLevel { get; set; } = 1;

        public long? ParentAccountId { get; set; }
    }

    private sealed class BalanceSheetHierarchyNode
    {
        public long Id { get; init; }

        public string AccountCode { get; init; } = string.Empty;

        public string AccountName { get; init; } = string.Empty;

        public string AccountType { get; init; } = "ASSET";

        public long? ParentAccountId { get; init; }

        public string ParentAccountCode { get; init; } = string.Empty;

        public bool IsPosting { get; init; } = true;

        public List<BalanceSheetHierarchyNode> Children { get; } = new();

        public decimal OwnAmount { get; set; }

        public decimal RollupAmount { get; set; }

        public bool Visible { get; set; }
    }

    private async Task<int> RebuildAccountHierarchyInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string actor,
        CancellationToken cancellationToken)
    {
        var accounts = new List<AccountHierarchyEntry>();
        await using (var load = new NpgsqlCommand(@"
SELECT id,
       is_posting,
       COALESCE(hierarchy_level, 1) AS hierarchy_level,
       parent_account_id
FROM gl_accounts
WHERE company_id = @company_id;", connection, transaction))
        {
            load.Parameters.AddWithValue("company_id", companyId);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts.Add(new AccountHierarchyEntry
                {
                    Id = reader.GetInt64(0),
                    IsPosting = !reader.IsDBNull(1) && reader.GetBoolean(1),
                    HierarchyLevel = reader.GetInt32(2),
                    ParentAccountId = reader.IsDBNull(3) ? null : reader.GetInt64(3)
                });
            }
        }

        var byId = new Dictionary<long, AccountHierarchyEntry>();
        foreach (var account in accounts)
        {
            byId[account.Id] = account;
        }

        var updatedCount = 0;
        foreach (var account in accounts)
        {
            long? targetParentId = null;
            if (account.ParentAccountId.HasValue &&
                account.ParentAccountId.Value != account.Id &&
                byId.TryGetValue(account.ParentAccountId.Value, out var parent))
            {
                if (!parent.ParentAccountId.HasValue)
                {
                    targetParentId = parent.Id;
                }
            }

            var targetLevel = targetParentId.HasValue ? 2 : 1;
            var targetIsPosting = targetParentId.HasValue;

            if (account.ParentAccountId == targetParentId &&
                account.HierarchyLevel == targetLevel &&
                account.IsPosting == targetIsPosting)
            {
                continue;
            }

            await using var update = new NpgsqlCommand(@"
UPDATE gl_accounts
SET parent_account_id = @parent_account_id,
    hierarchy_level = @hierarchy_level,
    is_posting = @is_posting,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
            update.Parameters.AddWithValue("id", account.Id);
            update.Parameters.AddWithValue("company_id", companyId);
            update.Parameters.AddWithValue("parent_account_id", NpgsqlTypes.NpgsqlDbType.Bigint, targetParentId.HasValue ? targetParentId.Value : DBNull.Value);
            update.Parameters.AddWithValue("hierarchy_level", targetLevel);
            update.Parameters.AddWithValue("is_posting", targetIsPosting);
            update.Parameters.AddWithValue("updated_by", actor);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            if (affected > 0)
            {
                updatedCount += affected;
            }
        }

        return updatedCount;
    }

    private static async Task<string> BuildRetainedEarningsCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT code
FROM org_locations
WHERE id = @location_id
  AND company_id = @company_id;", connection, transaction);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("company_id", companyId);

        var locationCode = await command.ExecuteScalarAsync(cancellationToken);
        var prefix = NormalizeLocationCoaSegment(locationCode as string);
        return $"{prefix}.{RetainedEarningsSuffix}";
    }

    private static string NormalizeLocationCoaSegment(string? locationCode)
    {
        var normalized = (locationCode ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "HO" => "HO",
            "HQ" => "HO",
            "PK" => "PK",
            "PKS" => "PK",
            "KB" => "KB",
            "KEBUN" => "KB",
            _ when normalized.Length >= 2 => normalized[..2],
            _ when normalized.Length == 1 => normalized + "X",
            _ => "HO"
        };
    }

    private static DateTime GetPeriodMonthStart(DateTime value)
    {
        return new DateTime(value.Year, value.Month, 1);
    }

    private static async Task EnsureAccountingPeriodRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime journalDate,
        CancellationToken cancellationToken)
    {
        var periodMonth = GetPeriodMonthStart(journalDate);

        await using var command = new NpgsqlCommand(@"
INSERT INTO gl_accounting_periods (
    company_id,
    location_id,
    period_month,
    is_open,
    note,
    created_at,
    updated_at)
VALUES (
    @company_id,
    @location_id,
    @period_month,
    TRUE,
    'AUTO_OPENED',
    NOW(),
    NOW())
ON CONFLICT (company_id, location_id, period_month) DO NOTHING;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", periodMonth);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsAccountingPeriodOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime journalDate,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await EnsureAccountingPeriodRowAsync(connection, transaction, companyId, locationId, journalDate, cancellationToken);
        var periodMonth = GetPeriodMonthStart(journalDate);
        var sql = forUpdate
            ? @"
SELECT is_open
FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month
FOR UPDATE;"
            : @"
SELECT is_open
FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", periodMonth);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is bool isOpen)
        {
            return isOpen;
        }

        return false;
    }

    private async Task<bool> HasAnyRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string username,
        IReadOnlyCollection<string> roleCodes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || roleCodes is null || roleCodes.Count == 0)
        {
            return false;
        }

        if (!TryBuildQualifiedTableName(_options.UsersTable, out var usersTable))
        {
            return false;
        }

        var normalizedCodes = roleCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedCodes.Length == 0)
        {
            return false;
        }

        await using var command = new NpgsqlCommand($@"
SELECT COUNT(1)
FROM {usersTable} u
JOIN sec_user_roles ur ON ur.user_id = u.id
JOIN sec_roles r ON r.id = ur.role_id
WHERE lower(u.username) = lower(@username)
  AND u.is_active = TRUE
  AND r.is_active = TRUE
  AND upper(r.code) = ANY(@role_codes);", connection, transaction);
        command.Parameters.AddWithValue("username", username.Trim());
        command.Parameters.AddWithValue("role_codes", normalizedCodes);

        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

}
