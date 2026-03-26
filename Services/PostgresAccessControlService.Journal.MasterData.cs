using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<List<ManagedAccount>> GetAccountsAsync(
        long companyId,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedAccount>();
        if (companyId <= 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT a.id,
       a.company_id,
       a.account_code,
       a.account_name,
       a.account_type,
       a.parent_account_id,
       COALESCE(p.account_code, '') AS parent_account_code,
       COALESCE(a.hierarchy_level, 1) AS hierarchy_level,
       a.is_posting,
       a.is_active
FROM gl_accounts a
LEFT JOIN gl_accounts p ON p.id = a.parent_account_id
WHERE a.company_id = @company_id
  AND (@include_inactive = TRUE OR a.is_active = TRUE)
ORDER BY a.account_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedAccount
            {
                Id = reader.GetInt64(0),
                CompanyId = reader.GetInt64(1),
                Code = reader.GetString(2),
                Name = reader.GetString(3),
                AccountType = NormalizeAccountType(reader.GetString(4), reader.GetString(2)),
                ParentAccountId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                ParentAccountCode = reader.GetString(6),
                HierarchyLevel = reader.GetInt32(7),
                IsPosting = !reader.IsDBNull(8) && reader.GetBoolean(8),
                IsActive = !reader.IsDBNull(9) && reader.GetBoolean(9)
            });
        }

        return output;
    }

    public async Task<AccountSearchResult> SearchAccountsAsync(
        long companyId,
        AccountSearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var pageSize = Math.Clamp(filter?.PageSize ?? 50, 1, 200);
        var requestedPage = Math.Max(1, filter?.Page ?? 1);
        if (companyId <= 0)
        {
            return new AccountSearchResult
            {
                Page = 1,
                PageSize = pageSize,
                TotalCount = 0
            };
        }

        var keyword = (filter?.Keyword ?? string.Empty).Trim();
        var keywordPattern = $"%{keyword}%";
        var status = (filter?.Status ?? "Aktif").Trim();
        var onlyInactive = string.Equals(status, "Nonaktif", StringComparison.OrdinalIgnoreCase);
        var includeInactive = !string.Equals(status, "Aktif", StringComparison.OrdinalIgnoreCase);

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var totalCount = 0;
        await using (var countCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM gl_accounts a
WHERE a.company_id = @company_id
  AND (@include_inactive = TRUE OR a.is_active = TRUE)
  AND (@only_inactive = FALSE OR a.is_active = FALSE)
  AND (@keyword = '' OR a.account_code ILIKE @keyword_pattern OR a.account_name ILIKE @keyword_pattern);", connection))
        {
            countCommand.Parameters.AddWithValue("company_id", companyId);
            countCommand.Parameters.AddWithValue("include_inactive", includeInactive);
            countCommand.Parameters.AddWithValue("only_inactive", onlyInactive);
            countCommand.Parameters.AddWithValue("keyword", keyword);
            countCommand.Parameters.AddWithValue("keyword_pattern", keywordPattern);
            totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        if (totalCount <= 0)
        {
            return new AccountSearchResult
            {
                Page = 1,
                PageSize = pageSize,
                TotalCount = 0
            };
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = Math.Clamp(requestedPage, 1, totalPages);
        var offset = (page - 1) * pageSize;

        var items = new List<ManagedAccount>();
        await using (var command = new NpgsqlCommand(@"
SELECT a.id,
       a.company_id,
       a.account_code,
       a.account_name,
       a.account_type,
       a.parent_account_id,
       COALESCE(p.account_code, '') AS parent_account_code,
       COALESCE(a.hierarchy_level, 1) AS hierarchy_level,
       a.is_posting,
       a.is_active
FROM gl_accounts a
LEFT JOIN gl_accounts p ON p.id = a.parent_account_id
WHERE a.company_id = @company_id
  AND (@include_inactive = TRUE OR a.is_active = TRUE)
  AND (@only_inactive = FALSE OR a.is_active = FALSE)
  AND (@keyword = '' OR a.account_code ILIKE @keyword_pattern OR a.account_name ILIKE @keyword_pattern)
ORDER BY a.account_code
LIMIT @limit OFFSET @offset;", connection))
        {
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("include_inactive", includeInactive);
            command.Parameters.AddWithValue("only_inactive", onlyInactive);
            command.Parameters.AddWithValue("keyword", keyword);
            command.Parameters.AddWithValue("keyword_pattern", keywordPattern);
            command.Parameters.AddWithValue("limit", pageSize);
            command.Parameters.AddWithValue("offset", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new ManagedAccount
                {
                    Id = reader.GetInt64(0),
                    CompanyId = reader.GetInt64(1),
                    Code = reader.GetString(2),
                    Name = reader.GetString(3),
                    AccountType = NormalizeAccountType(reader.GetString(4), reader.GetString(2)),
                    ParentAccountId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    ParentAccountCode = reader.GetString(6),
                    HierarchyLevel = reader.GetInt32(7),
                    IsPosting = !reader.IsDBNull(8) && reader.GetBoolean(8),
                    IsActive = !reader.IsDBNull(9) && reader.GetBoolean(9)
                });
            }
        }

        return new AccountSearchResult
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Items = items
        };
    }

    public async Task<AccessOperationResult> SaveAccountAsync(
        long companyId,
        ManagedAccount account,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan akun tidak valid.");
        }

        if (string.IsNullOrWhiteSpace(account.Code) || string.IsNullOrWhiteSpace(account.Name))
        {
            return new AccessOperationResult(false, "Kode dan nama akun wajib diisi.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var normalizedCode = account.Code.Trim().ToUpperInvariant();
            var normalizedName = account.Name.Trim();
            if (!IsSegmentedAccountCode(normalizedCode))
            {
                return new AccessOperationResult(false, "Format kode akun harus XX.XXXXX.XXX.");
            }

            var normalizedType = NormalizeAccountType(account.AccountType, normalizedCode);
            var normalBalance = normalizedType is "LIABILITY" or "EQUITY" or "REVENUE" ? "C" : "D";
            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleMasterData,
                ResolveWriteAction(account.Id),
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola master akun.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            long? parentAccountId = account.ParentAccountId is > 0 ? account.ParentAccountId : null;
            var parentAccountCode = string.Empty;

            if (parentAccountId.HasValue)
            {
                if (account.Id > 0 && parentAccountId.Value == account.Id)
                {
                    return new AccessOperationResult(false, "Parent akun tidak boleh akun itu sendiri.");
                }

                await using var parentLookup = new NpgsqlCommand(@"
SELECT id,
       account_code,
       parent_account_id,
       is_active
FROM gl_accounts
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
                parentLookup.Parameters.AddWithValue("id", parentAccountId.Value);
                parentLookup.Parameters.AddWithValue("company_id", companyId);
                await using var parentReader = await parentLookup.ExecuteReaderAsync(cancellationToken);
                if (!await parentReader.ReadAsync(cancellationToken))
                {
                    return new AccessOperationResult(false, "Parent akun tidak ditemukan.");
                }

                var parentHasParent = !parentReader.IsDBNull(2);
                var parentIsActive = !parentReader.IsDBNull(3) && parentReader.GetBoolean(3);
                if (parentHasParent)
                {
                    return new AccessOperationResult(false, "Parent akun harus akun level 1 (summary).");
                }

                if (!parentIsActive)
                {
                    return new AccessOperationResult(false, "Parent akun nonaktif dan tidak dapat dipilih.");
                }

                parentAccountCode = parentReader.GetString(1);
            }

            var isPosting = parentAccountId.HasValue;
            var hierarchyLevel = isPosting ? 2 : 1;

            long accountId;
            if (account.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    is_posting,
    hierarchy_level,
    is_active,
    created_by,
    created_at,
    updated_by,
    updated_at)
VALUES (
    @company_id,
    @account_code,
    @account_name,
    @account_type,
    @normal_balance,
    @parent_account_id,
    @is_posting,
    @hierarchy_level,
    @is_active,
    @actor,
    NOW(),
    @actor,
    NOW())
RETURNING id;", connection, transaction);
                insertCommand.Parameters.AddWithValue("company_id", companyId);
                insertCommand.Parameters.AddWithValue("account_code", normalizedCode);
                insertCommand.Parameters.AddWithValue("account_name", normalizedName);
                insertCommand.Parameters.AddWithValue("account_type", normalizedType);
                insertCommand.Parameters.AddWithValue("normal_balance", normalBalance);
                insertCommand.Parameters.AddWithValue("parent_account_id", NpgsqlTypes.NpgsqlDbType.Bigint, parentAccountId.HasValue ? parentAccountId.Value : DBNull.Value);
                insertCommand.Parameters.AddWithValue("is_posting", isPosting);
                insertCommand.Parameters.AddWithValue("hierarchy_level", hierarchyLevel);
                insertCommand.Parameters.AddWithValue("is_active", account.IsActive);
                insertCommand.Parameters.AddWithValue("actor", actor);
                accountId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE gl_accounts
SET account_code = @account_code,
    account_name = @account_name,
    account_type = @account_type,
    normal_balance = @normal_balance,
    parent_account_id = @parent_account_id,
    is_posting = @is_posting,
    hierarchy_level = @hierarchy_level,
    is_active = @is_active,
    updated_by = @actor,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
                updateCommand.Parameters.AddWithValue("id", account.Id);
                updateCommand.Parameters.AddWithValue("company_id", companyId);
                updateCommand.Parameters.AddWithValue("account_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("account_name", normalizedName);
                updateCommand.Parameters.AddWithValue("account_type", normalizedType);
                updateCommand.Parameters.AddWithValue("normal_balance", normalBalance);
                updateCommand.Parameters.AddWithValue("parent_account_id", NpgsqlTypes.NpgsqlDbType.Bigint, parentAccountId.HasValue ? parentAccountId.Value : DBNull.Value);
                updateCommand.Parameters.AddWithValue("is_posting", isPosting);
                updateCommand.Parameters.AddWithValue("hierarchy_level", hierarchyLevel);
                updateCommand.Parameters.AddWithValue("is_active", account.IsActive);
                updateCommand.Parameters.AddWithValue("actor", actor);
                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Akun tidak ditemukan.");
                }

                accountId = account.Id;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "ACCOUNT",
                accountId,
                account.Id <= 0 ? "CREATE_OR_UPDATE" : "UPDATE",
                actor,
                $"company={companyId};code={normalizedCode};name={normalizedName};type={normalizedType};normal_balance={normalBalance};active={account.IsActive};parent={parentAccountCode}",
                cancellationToken);

            await RebuildAccountHierarchyInternalAsync(
                connection,
                transaction,
                companyId,
                actor,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Akun berhasil disimpan.", accountId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("SaveAccountDuplicate", $"action=save_account status=duplicate company_id={companyId} account_code={account.Code}", ex);
            return new AccessOperationResult(false, "Kode akun sudah digunakan.");
        }
        catch (Exception ex)
        {
            LogServiceError("SaveAccountFailed", $"action=save_account status=failed company_id={companyId} account_code={account.Code}", ex);
            return new AccessOperationResult(false, "Gagal menyimpan akun.");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteAccountAsync(
        long companyId,
        long accountId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0 || accountId <= 0)
        {
            return new AccessOperationResult(false, "Akun tidak valid.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleMasterData,
                PermissionActionDelete,
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola master akun.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            string? code = null;
            string? name = null;
            await using (var lookup = new NpgsqlCommand(@"
SELECT account_code, account_name
FROM gl_accounts
WHERE id = @id
  AND company_id = @company_id;", connection, transaction))
            {
                lookup.Parameters.AddWithValue("id", accountId);
                lookup.Parameters.AddWithValue("company_id", companyId);
                await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    code = reader.GetString(0);
                    name = reader.GetString(1);
                }
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Akun tidak ditemukan.");
            }

            await using (var deactivate = new NpgsqlCommand(@"
UPDATE gl_accounts
SET is_active = FALSE,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction))
            {
                deactivate.Parameters.AddWithValue("id", accountId);
                deactivate.Parameters.AddWithValue("company_id", companyId);
                deactivate.Parameters.AddWithValue("updated_by", NormalizeActor(actorUsername));
                await deactivate.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "ACCOUNT",
                accountId,
                "SOFT_DELETE",
                actor,
                $"company={companyId};code={code};name={name}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Akun dinonaktifkan.", accountId);
        }
        catch (Exception ex)
        {
            LogServiceError("SoftDeleteAccountFailed", $"action=deactivate_account status=failed company_id={companyId} account_id={accountId}", ex);
            return new AccessOperationResult(false, "Gagal menonaktifkan akun.");
        }
    }

    public async Task<AccessOperationResult> RebuildAccountHierarchyAsync(
        long companyId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan akun tidak valid.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleMasterData,
                PermissionActionUpdate,
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk membangun ulang hierarki akun.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var affected = await RebuildAccountHierarchyInternalAsync(
                connection,
                transaction,
                companyId,
                actor,
                cancellationToken);

            await InsertAuditLogAsync(
                connection,
                transaction,
                "ACCOUNT",
                companyId,
                "REBUILD_HIERARCHY",
                actor,
                $"company={companyId};updated={affected}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, $"Hierarki akun berhasil dibangun ulang. {affected} akun tersinkron.", affected);
        }
        catch (Exception ex)
        {
            LogServiceError("RebuildAccountHierarchyFailed", $"action=rebuild_account_hierarchy status=failed company_id={companyId}", ex);
            return new AccessOperationResult(false, "Gagal membangun ulang hierarki akun.");
        }
    }

    public async Task<List<ManagedAccountingPeriod>> GetAccountingPeriodsAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedAccountingPeriod>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT id,
       company_id,
       location_id,
       period_month,
       is_open,
       closed_at,
       COALESCE(closed_by, ''),
       COALESCE(note, '')
FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
ORDER BY period_month DESC;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedAccountingPeriod
            {
                Id = reader.GetInt64(0),
                CompanyId = reader.GetInt64(1),
                LocationId = reader.GetInt64(2),
                PeriodMonth = reader.GetDateTime(3),
                IsOpen = !reader.IsDBNull(4) && reader.GetBoolean(4),
                ClosedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                ClosedBy = reader.GetString(6),
                Note = reader.GetString(7)
            });
        }

        return output;
    }

    public async Task<AccessOperationResult> SetAccountingPeriodOpenStateAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        bool isOpen,
        string actorUsername,
        string note = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0 || locationId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan/lokasi periode tidak valid.");
        }

        var monthStart = GetPeriodMonthStart(periodMonth);
        var actor = NormalizeActor(actorUsername);
        var normalizedNote = note?.Trim() ?? string.Empty;

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var hasPermission = await HasAnyRoleAsync(
                connection,
                transaction,
                actor,
                AccountingPeriodManagerRoles,
                cancellationToken);
            if (!hasPermission)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Anda tidak memiliki izin untuk membuka/menutup periode.");
            }

            await EnsureAccountingPeriodRowAsync(
                connection,
                transaction,
                companyId,
                locationId,
                monthStart,
                cancellationToken);

            string closingJournalNo = string.Empty;
            PostedZeroCostOutboundSummary zeroCostOutboundSummary = default;
            if (!isOpen)
            {
                var hasUnposted = await HasDraftJournalInPeriodAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    monthStart,
                    cancellationToken);
                if (hasUnposted)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(
                        false,
                        $"Masih ada jurnal belum POSTED pada periode {monthStart:yyyy-MM}. Posting seluruh jurnal sebelum tutup periode.");
                }

                var closingResult = await EnsurePeriodClosingJournalAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    monthStart,
                    actor,
                    cancellationToken);
                if (!closingResult.IsSuccess)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, closingResult.Message);
                }

                closingJournalNo = closingResult.JournalNo;
                zeroCostOutboundSummary = await GetPostedZeroCostOutboundSummaryAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    monthStart,
                    cancellationToken);
            }

            await using (var command = new NpgsqlCommand(@"
UPDATE gl_accounting_periods
SET is_open = @is_open,
    closed_at = CASE WHEN @is_open THEN NULL ELSE NOW() END,
    closed_by = CASE WHEN @is_open THEN NULL ELSE @actor END,
    note = @note,
    updated_at = NOW()
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;", connection, transaction))
            {
                command.Parameters.AddWithValue("is_open", isOpen);
                command.Parameters.AddWithValue("actor", actor);
                command.Parameters.AddWithValue("note", normalizedNote);
                command.Parameters.AddWithValue("company_id", companyId);
                command.Parameters.AddWithValue("location_id", locationId);
                command.Parameters.AddWithValue("period_month", monthStart);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!isOpen)
            {
                var equationSnapshot = await ComputeAccountingEquationSnapshotAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    monthStart,
                    cancellationToken);
                if (Math.Abs(equationSnapshot.EquationDifference) > 0.01m)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(
                        false,
                        $"Gagal menutup periode {monthStart:yyyy-MM}. Selisih persamaan akuntansi setelah closing masih {equationSnapshot.EquationDifference:N2}.");
                }
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "ACCOUNTING_PERIOD",
                0,
                isOpen ? "OPEN" : "CLOSE",
                actor,
                $"company={companyId};location={locationId};period={monthStart:yyyy-MM};note={normalizedNote};closing_journal={closingJournalNo};zero_cost_tx={zeroCostOutboundSummary.TransactionCount};zero_cost_lines={zeroCostOutboundSummary.LineCount};zero_cost_items={zeroCostOutboundSummary.ItemCount}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            var closeWarning = !isOpen && zeroCostOutboundSummary.LineCount > 0
                ? $" PERINGATAN: Ditemukan outbound cost 0 (transaksi={zeroCostOutboundSummary.TransactionCount}, baris={zeroCostOutboundSummary.LineCount}, item={zeroCostOutboundSummary.ItemCount}) pada periode ini. Review dan koreksi costing segera."
                : string.Empty;
            return new AccessOperationResult(
                true,
                isOpen
                    ? $"Periode {monthStart:yyyy-MM} berhasil dibuka."
                    : string.IsNullOrWhiteSpace(closingJournalNo)
                        ? $"Periode {monthStart:yyyy-MM} berhasil ditutup.{closeWarning}"
                        : $"Periode {monthStart:yyyy-MM} berhasil ditutup. Jurnal penutup: {closingJournalNo}.{closeWarning}");
        }
        catch (Exception ex)
        {
            LogServiceError("SetAccountingPeriodStateFailed", $"action=set_period_open_state status=failed period={periodMonth:yyyy-MM} is_open={isOpen} company_id={companyId} location_id={locationId}", ex);
            return new AccessOperationResult(false, "Gagal memperbarui status periode akuntansi.");
        }
    }

    private static async Task<PostedZeroCostOutboundSummary> GetPostedZeroCostOutboundSummaryAsync(
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
SELECT COUNT(DISTINCT h.id) AS tx_count,
       COUNT(1) AS line_count,
       COUNT(DISTINCT l.item_id) AS item_count
FROM inv_stock_transactions h
JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
  AND h.status = 'POSTED'
  AND h.transaction_type IN ('STOCK_OUT', 'TRANSFER')
  AND h.transaction_date BETWEEN @date_from AND @date_to
  AND l.qty > 0
  AND COALESCE(l.unit_cost, 0) <= 0;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", monthStart);
        command.Parameters.AddWithValue("date_to", monthEnd);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new PostedZeroCostOutboundSummary(
                TransactionCount: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                LineCount: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                ItemCount: reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
        }

        return default;
    }

    private readonly record struct PostedZeroCostOutboundSummary(int TransactionCount, int LineCount, int ItemCount);

    public async Task<List<ManagedAuditLog>> GetAuditLogsAsync(
        string entityType,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var normalizedType = (entityType ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedType))
        {
            return new List<ManagedAuditLog>();
        }

        var safeLimit = Math.Clamp(limit, 1, 1000);
        var output = new List<ManagedAuditLog>();

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT id,
       entity_type,
       entity_id,
       action,
       actor_username,
       details,
       created_at
FROM sec_audit_logs
WHERE upper(entity_type) = @entity_type
ORDER BY created_at DESC, id DESC
LIMIT @limit;", connection);
        command.Parameters.AddWithValue("entity_type", normalizedType);
        command.Parameters.AddWithValue("limit", safeLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedAuditLog
            {
                Id = reader.GetInt64(0),
                EntityType = reader.GetString(1),
                EntityId = reader.GetInt64(2),
                Action = reader.GetString(3),
                ActorUsername = reader.GetString(4),
                Details = reader.GetString(5),
                CreatedAt = reader.GetDateTime(6)
            });
        }

        return output;
    }

    public async Task<JournalWorkspaceData> GetJournalWorkspaceDataAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var data = new JournalWorkspaceData();
        if (companyId <= 0 || locationId <= 0)
        {
            return data;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var accountCommand = new NpgsqlCommand(@"
SELECT id, company_id, account_code, account_name, account_type, is_active
FROM gl_accounts
WHERE company_id = @company_id
  AND is_active = TRUE
  AND is_posting = TRUE
ORDER BY account_code;", connection))
        {
            accountCommand.Parameters.AddWithValue("company_id", companyId);
            await using var reader = await accountCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                data.Accounts.Add(new ManagedAccount
                {
                    Id = reader.GetInt64(0),
                    CompanyId = reader.GetInt64(1),
                    Code = reader.GetString(2),
                    Name = reader.GetString(3),
                    AccountType = NormalizeAccountType(reader.GetString(4), reader.GetString(2)),
                    IsActive = !reader.IsDBNull(5) && reader.GetBoolean(5)
                });
            }
        }

        await using (var listCommand = new NpgsqlCommand(@"
SELECT h.id,
       h.journal_no,
       h.journal_date,
       h.period_month,
       COALESCE(h.reference_no, ''),
       COALESCE(h.description, ''),
       h.status,
       COALESCE(SUM(d.debit), 0),
       COALESCE(SUM(d.credit), 0)
FROM gl_journal_headers h
LEFT JOIN gl_journal_details d ON d.header_id = h.id
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
GROUP BY h.id, h.journal_no, h.journal_date, h.period_month, h.reference_no, h.description, h.status
ORDER BY h.journal_date DESC, h.id DESC
LIMIT 300;", connection))
        {
            listCommand.Parameters.AddWithValue("company_id", companyId);
            listCommand.Parameters.AddWithValue("location_id", locationId);

            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                data.Journals.Add(new ManagedJournalSummary
                {
                    Id = reader.GetInt64(0),
                    JournalNo = reader.GetString(1),
                    JournalDate = reader.GetDateTime(2),
                    ReferenceNo = reader.GetString(4),
                    Description = reader.GetString(5),
                    Status = reader.GetString(6),
                    TotalDebit = reader.GetDecimal(7),
                    TotalCredit = reader.GetDecimal(8)
                });
            }
        }

        return data;
    }
}


