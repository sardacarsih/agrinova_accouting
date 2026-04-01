using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<List<ManagedAccount>> GetAccountsAsync(
        long companyId,
        bool includeInactive = false,
        string actorUsername = "",
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

        if (!await HasScopeAccessAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                companyId,
                locationId: null,
                cancellationToken))
        {
            return output;
        }

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
        a.is_active,
       COALESCE(a.requires_department, FALSE) AS requires_department,
       COALESCE(a.requires_project, FALSE) AS requires_project,
       COALESCE(a.requires_cost_center, FALSE) AS requires_cost_center,
       COALESCE(a.requires_partner, FALSE) AS requires_subledger,
       COALESCE(a.allowed_subledger_type, '') AS allowed_subledger_type
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
                AccountType = NormalizeAccountType(reader.GetString(4)),
                ParentAccountId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                ParentAccountCode = reader.GetString(6),
                HierarchyLevel = reader.GetInt32(7),
                IsPosting = !reader.IsDBNull(8) && reader.GetBoolean(8),
                IsActive = !reader.IsDBNull(9) && reader.GetBoolean(9),
                RequiresDepartment = !reader.IsDBNull(10) && reader.GetBoolean(10),
                RequiresProject = !reader.IsDBNull(11) && reader.GetBoolean(11),
                RequiresCostCenter = !reader.IsDBNull(12) && reader.GetBoolean(12),
                RequiresSubledger = !reader.IsDBNull(13) && reader.GetBoolean(13),
                AllowedSubledgerType = reader.IsDBNull(14) ? string.Empty : NormalizeSubledgerType(reader.GetString(14))
            });
        }

        return output;
    }

    public async Task<AccountSearchResult> SearchAccountsAsync(
        long companyId,
        AccountSearchFilter filter,
        string actorUsername = "",
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

        if (!await HasScopeAccessAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                companyId,
                locationId: null,
                cancellationToken))
        {
            return new AccountSearchResult
            {
                Page = 1,
                PageSize = pageSize,
                TotalCount = 0
            };
        }

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
       a.is_active,
       COALESCE(a.requires_department, FALSE) AS requires_department,
       COALESCE(a.requires_project, FALSE) AS requires_project,
       COALESCE(a.requires_cost_center, FALSE) AS requires_cost_center,
       COALESCE(a.requires_partner, FALSE) AS requires_subledger,
       COALESCE(a.allowed_subledger_type, '') AS allowed_subledger_type
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
                    AccountType = NormalizeAccountType(reader.GetString(4)),
                    ParentAccountId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    ParentAccountCode = reader.GetString(6),
                    HierarchyLevel = reader.GetInt32(7),
                    IsPosting = !reader.IsDBNull(8) && reader.GetBoolean(8),
                    IsActive = !reader.IsDBNull(9) && reader.GetBoolean(9),
                    RequiresDepartment = !reader.IsDBNull(10) && reader.GetBoolean(10),
                    RequiresProject = !reader.IsDBNull(11) && reader.GetBoolean(11),
                    RequiresCostCenter = !reader.IsDBNull(12) && reader.GetBoolean(12),
                    RequiresSubledger = !reader.IsDBNull(13) && reader.GetBoolean(13),
                    AllowedSubledgerType = reader.IsDBNull(14) ? string.Empty : NormalizeSubledgerType(reader.GetString(14))
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

            var normalizedCode = account.Code.Trim().ToUpperInvariant();
            var normalizedName = account.Name.Trim();
            if (!IsSegmentedAccountCode(normalizedCode))
            {
                return new AccessOperationResult(false, "Format kode akun harus XX.99999.999.");
            }

            var normalizedType = NormalizeAccountType(account.AccountType);
            var normalizedSubledgerType = NormalizeSubledgerType(account.AllowedSubledgerType);
            var requiresSubledger = account.RequiresSubledger || !string.IsNullOrWhiteSpace(normalizedSubledgerType);

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
       is_active,
       account_type
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
                var parentAccountType = NormalizeAccountType(parentReader.IsDBNull(4) ? null : parentReader.GetString(4));
                if (parentHasParent)
                {
                    return new AccessOperationResult(false, "Parent akun harus akun level 1 (summary).");
                }

                if (!parentIsActive)
                {
                    return new AccessOperationResult(false, "Parent akun nonaktif dan tidak dapat dipilih.");
                }

                parentAccountCode = parentReader.GetString(1);
                if (string.IsNullOrWhiteSpace(parentAccountType))
                {
                    return new AccessOperationResult(false, "Parent akun memiliki tipe akun tidak valid.");
                }

                if (!string.IsNullOrWhiteSpace(normalizedType) &&
                    !string.Equals(normalizedType, parentAccountType, StringComparison.OrdinalIgnoreCase))
                {
                    return new AccessOperationResult(false, "Tipe akun child harus sama dengan parent.");
                }

                normalizedType = parentAccountType;
            }
            else if (string.IsNullOrWhiteSpace(normalizedType))
            {
                return new AccessOperationResult(false, "Tipe akun wajib dipilih.");
            }

            var isPosting = parentAccountId.HasValue;
            var hierarchyLevel = isPosting ? 2 : 1;
            var normalBalance = normalizedType is "LIABILITY" or "EQUITY" or "REVENUE" ? "C" : "D";

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
    requires_department,
    requires_project,
    requires_cost_center,
    requires_partner,
    allowed_subledger_type,
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
    @requires_department,
    @requires_project,
    @requires_cost_center,
    @requires_partner,
    @allowed_subledger_type,
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
                insertCommand.Parameters.AddWithValue("requires_department", account.RequiresDepartment);
                insertCommand.Parameters.AddWithValue("requires_project", account.RequiresProject);
                insertCommand.Parameters.AddWithValue("requires_cost_center", account.RequiresCostCenter);
                insertCommand.Parameters.AddWithValue("requires_partner", requiresSubledger);
                insertCommand.Parameters.AddWithValue("allowed_subledger_type", normalizedSubledgerType);
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
    requires_department = @requires_department,
    requires_project = @requires_project,
    requires_cost_center = @requires_cost_center,
    requires_partner = @requires_partner,
    allowed_subledger_type = @allowed_subledger_type,
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
                updateCommand.Parameters.AddWithValue("requires_department", account.RequiresDepartment);
                updateCommand.Parameters.AddWithValue("requires_project", account.RequiresProject);
                updateCommand.Parameters.AddWithValue("requires_cost_center", account.RequiresCostCenter);
                updateCommand.Parameters.AddWithValue("requires_partner", requiresSubledger);
                updateCommand.Parameters.AddWithValue("allowed_subledger_type", normalizedSubledgerType);
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
                $"company={companyId};code={normalizedCode};name={normalizedName};type={normalizedType};normal_balance={normalBalance};active={account.IsActive};parent={parentAccountCode};requires_department={account.RequiresDepartment};requires_project={account.RequiresProject};requires_cost_center={account.RequiresCostCenter};requires_subledger={requiresSubledger};allowed_subledger_type={normalizedSubledgerType}",
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
        string actorUsername = "",
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

        if (!await HasScopeAccessAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                companyId,
                locationId,
                cancellationToken))
        {
            return output;
        }

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

    public async Task<List<ManagedCostCenter>> GetCostCentersAsync(
        long companyId,
        long locationId,
        bool includeInactive = false,
        string actorUsername = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedCostCenter>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HasScopeAccessAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                companyId,
                locationId,
                cancellationToken))
        {
            return output;
        }

        await using var command = new NpgsqlCommand(@"
SELECT id,
       company_id,
       location_id,
       parent_id,
       cost_center_code,
       COALESCE(cost_center_name, ''),
       estate_code,
       COALESCE(estate_name, ''),
       division_code,
       COALESCE(division_name, ''),
       block_code,
       COALESCE(block_name, ''),
       level,
       is_posting,
       is_active
FROM gl_cost_centers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND (@include_inactive = TRUE OR is_active = TRUE)
ORDER BY estate_code, division_code, block_code, cost_center_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedCostCenter
            {
                Id = reader.GetInt64(0),
                CompanyId = reader.GetInt64(1),
                LocationId = reader.GetInt64(2),
                ParentId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                CostCenterCode = reader.GetString(4),
                CostCenterName = reader.GetString(5),
                EstateCode = reader.GetString(6),
                EstateName = reader.GetString(7),
                DivisionCode = reader.GetString(8),
                DivisionName = reader.GetString(9),
                BlockCode = reader.GetString(10),
                BlockName = reader.GetString(11),
                Level = reader.GetString(12),
                IsPosting = !reader.IsDBNull(13) && reader.GetBoolean(13),
                IsActive = !reader.IsDBNull(14) && reader.GetBoolean(14)
            });
        }

        return output;
    }

    public async Task<List<ManagedCostCenter>> GetBlockCostCentersAsync(
        long companyId,
        long locationId,
        bool includeInactive = false,
        string actorUsername = "",
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedCostCenter>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HasScopeAccessAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                companyId,
                locationId,
                cancellationToken))
        {
            return output;
        }

        await using var command = new NpgsqlCommand(@"
SELECT b.id,
       e.company_id,
       e.location_id,
       upper(btrim(e.code)) AS estate_code,
       btrim(coalesce(e.name, '')) AS estate_name,
       upper(btrim(d.code)) AS division_code,
       btrim(coalesce(d.name, '')) AS division_name,
       upper(btrim(b.code)) AS block_code,
       btrim(coalesce(b.name, '')) AS block_name,
       upper(btrim(e.code)) || '-' || upper(btrim(d.code)) || '-' || upper(btrim(b.code)) AS cost_center_code,
       coalesce(nullif(btrim(b.name), ''), upper(btrim(e.code)) || '-' || upper(btrim(d.code)) || '-' || upper(btrim(b.code))) AS cost_center_name,
       coalesce(e.is_active, FALSE) AND coalesce(d.is_active, FALSE) AND coalesce(b.is_active, FALSE) AS is_active
FROM blocks b
JOIN divisions d ON d.id = b.division_id
JOIN estates e ON e.id = d.estate_id
WHERE e.company_id = @company_id
  AND e.location_id = @location_id
  AND btrim(coalesce(e.code, '')) <> ''
  AND btrim(coalesce(d.code, '')) <> ''
  AND btrim(coalesce(b.code, '')) <> ''
  AND (@include_inactive = TRUE OR (coalesce(e.is_active, FALSE) AND coalesce(d.is_active, FALSE) AND coalesce(b.is_active, FALSE)))
ORDER BY upper(btrim(e.code)),
         upper(btrim(d.code)),
         upper(btrim(b.code));", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedCostCenter
            {
                Id = reader.GetInt64(0),
                BlockId = reader.GetInt64(0),
                CompanyId = reader.GetInt64(1),
                LocationId = reader.GetInt64(2),
                ParentId = null,
                EstateCode = reader.GetString(3),
                EstateName = reader.GetString(4),
                DivisionCode = reader.GetString(5),
                DivisionName = reader.GetString(6),
                BlockCode = reader.GetString(7),
                BlockName = reader.GetString(8),
                CostCenterCode = reader.GetString(9),
                CostCenterName = reader.GetString(10),
                Level = "BLOCK",
                IsPosting = true,
                IsActive = !reader.IsDBNull(11) && reader.GetBoolean(11),
                IsDirectBlockSource = true,
                SourceTable = "BLOCKS"
            });
        }

        return output;
    }

    public async Task<AccessOperationResult> SaveCostCenterAsync(
        long companyId,
        long locationId,
        ManagedCostCenter costCenter,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0 || locationId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan/lokasi cost center tidak valid.");
        }

        var normalizedEstateCode = NormalizeDimensionCode(costCenter.EstateCode);
        var normalizedDivisionCode = NormalizeDimensionCode(costCenter.DivisionCode);
        var normalizedBlockCode = NormalizeDimensionCode(costCenter.BlockCode);
        var normalizedEstateName = NormalizeDimensionName(costCenter.EstateName);
        var normalizedDivisionName = NormalizeDimensionName(costCenter.DivisionName);
        var normalizedBlockName = NormalizeDimensionName(costCenter.BlockName);
        var level = ResolveCostCenterLevel(normalizedEstateCode, normalizedDivisionCode, normalizedBlockCode);
        if (level is null)
        {
            return new AccessOperationResult(false, "Kombinasi estate/division/block tidak valid.");
        }

        var normalizedCode = BuildCostCenterCode(normalizedEstateCode, normalizedDivisionCode, normalizedBlockCode);
        var normalizedName = BuildCostCenterName(
            level,
            costCenter.CostCenterName,
            normalizedEstateCode,
            normalizedEstateName,
            normalizedDivisionCode,
            normalizedDivisionName,
            normalizedBlockCode,
            normalizedBlockName);
        var isPosting = string.Equals(level, "BLOCK", StringComparison.OrdinalIgnoreCase);

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
                ResolveWriteAction(costCenter.Id),
                companyId,
                locationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola master cost center.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            long? parentId = null;
            if (string.Equals(level, "DIVISION", StringComparison.OrdinalIgnoreCase))
            {
                parentId = await FindCostCenterIdAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    normalizedEstateCode,
                    divisionCode: string.Empty,
                    blockCode: string.Empty,
                    cancellationToken);
                if (!parentId.HasValue)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Estate cost center '{normalizedEstateCode}' belum ada.");
                }
            }
            else if (string.Equals(level, "BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                parentId = await FindCostCenterIdAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    normalizedEstateCode,
                    normalizedDivisionCode,
                    blockCode: string.Empty,
                    cancellationToken);
                if (!parentId.HasValue)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Division cost center '{normalizedEstateCode}-{normalizedDivisionCode}' belum ada.");
                }
            }

            long entityId;
            if (costCenter.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO gl_cost_centers (
    company_id,
    location_id,
    parent_id,
    cost_center_code,
    cost_center_name,
    estate_code,
    estate_name,
    division_code,
    division_name,
    block_code,
    block_name,
    level,
    is_posting,
    is_active,
    created_by,
    created_at,
    updated_by,
    updated_at)
VALUES (
    @company_id,
    @location_id,
    @parent_id,
    @cost_center_code,
    @cost_center_name,
    @estate_code,
    @estate_name,
    @division_code,
    @division_name,
    @block_code,
    @block_name,
    @level,
    @is_posting,
    @is_active,
    @actor,
    NOW(),
    @actor,
    NOW())
RETURNING id;", connection, transaction);
                insertCommand.Parameters.AddWithValue("company_id", companyId);
                insertCommand.Parameters.AddWithValue("location_id", locationId);
                insertCommand.Parameters.AddWithValue("parent_id", NpgsqlTypes.NpgsqlDbType.Bigint, parentId.HasValue ? parentId.Value : DBNull.Value);
                insertCommand.Parameters.AddWithValue("cost_center_code", normalizedCode);
                insertCommand.Parameters.AddWithValue("cost_center_name", normalizedName);
                insertCommand.Parameters.AddWithValue("estate_code", normalizedEstateCode);
                insertCommand.Parameters.AddWithValue("estate_name", normalizedEstateName);
                insertCommand.Parameters.AddWithValue("division_code", normalizedDivisionCode);
                insertCommand.Parameters.AddWithValue("division_name", normalizedDivisionName);
                insertCommand.Parameters.AddWithValue("block_code", normalizedBlockCode);
                insertCommand.Parameters.AddWithValue("block_name", normalizedBlockName);
                insertCommand.Parameters.AddWithValue("level", level);
                insertCommand.Parameters.AddWithValue("is_posting", isPosting);
                insertCommand.Parameters.AddWithValue("is_active", costCenter.IsActive);
                insertCommand.Parameters.AddWithValue("actor", actor);
                entityId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE gl_cost_centers
SET parent_id = @parent_id,
    cost_center_code = @cost_center_code,
    cost_center_name = @cost_center_name,
    estate_code = @estate_code,
    estate_name = @estate_name,
    division_code = @division_code,
    division_name = @division_name,
    block_code = @block_code,
    block_name = @block_name,
    level = @level,
    is_posting = @is_posting,
    is_active = @is_active,
    updated_by = @actor,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id;", connection, transaction);
                updateCommand.Parameters.AddWithValue("id", costCenter.Id);
                updateCommand.Parameters.AddWithValue("company_id", companyId);
                updateCommand.Parameters.AddWithValue("location_id", locationId);
                updateCommand.Parameters.AddWithValue("parent_id", NpgsqlTypes.NpgsqlDbType.Bigint, parentId.HasValue ? parentId.Value : DBNull.Value);
                updateCommand.Parameters.AddWithValue("cost_center_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("cost_center_name", normalizedName);
                updateCommand.Parameters.AddWithValue("estate_code", normalizedEstateCode);
                updateCommand.Parameters.AddWithValue("estate_name", normalizedEstateName);
                updateCommand.Parameters.AddWithValue("division_code", normalizedDivisionCode);
                updateCommand.Parameters.AddWithValue("division_name", normalizedDivisionName);
                updateCommand.Parameters.AddWithValue("block_code", normalizedBlockCode);
                updateCommand.Parameters.AddWithValue("block_name", normalizedBlockName);
                updateCommand.Parameters.AddWithValue("level", level);
                updateCommand.Parameters.AddWithValue("is_posting", isPosting);
                updateCommand.Parameters.AddWithValue("is_active", costCenter.IsActive);
                updateCommand.Parameters.AddWithValue("actor", actor);
                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Cost center tidak ditemukan.");
                }

                entityId = costCenter.Id;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "COST_CENTER",
                entityId,
                costCenter.Id <= 0 ? "CREATE_OR_UPDATE" : "UPDATE",
                actor,
                $"company={companyId};location={locationId};code={normalizedCode};level={level};estate={normalizedEstateCode};division={normalizedDivisionCode};block={normalizedBlockCode};active={costCenter.IsActive}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Cost center berhasil disimpan.", entityId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("SaveCostCenterDuplicate", $"action=save_cost_center status=duplicate company_id={companyId} location_id={locationId} code={normalizedCode}", ex);
            return new AccessOperationResult(false, "Kode atau kombinasi natural key cost center sudah digunakan.");
        }
        catch (Exception ex)
        {
            LogServiceError("SaveCostCenterFailed", $"action=save_cost_center status=failed company_id={companyId} location_id={locationId} code={normalizedCode}", ex);
            return new AccessOperationResult(false, "Gagal menyimpan cost center.");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteCostCenterAsync(
        long companyId,
        long locationId,
        long costCenterId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0 || locationId <= 0 || costCenterId <= 0)
        {
            return new AccessOperationResult(false, "Cost center tidak valid.");
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
                locationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola master cost center.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            string? code = null;
            await using (var lookupCommand = new NpgsqlCommand(@"
SELECT cost_center_code
FROM gl_cost_centers
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id;", connection, transaction))
            {
                lookupCommand.Parameters.AddWithValue("id", costCenterId);
                lookupCommand.Parameters.AddWithValue("company_id", companyId);
                lookupCommand.Parameters.AddWithValue("location_id", locationId);
                await using var reader = await lookupCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    code = reader.GetString(0);
                }
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Cost center tidak ditemukan.");
            }

            await using (var childCountCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM gl_cost_centers
WHERE parent_id = @parent_id
  AND is_active = TRUE;", connection, transaction))
            {
                childCountCommand.Parameters.AddWithValue("parent_id", costCenterId);
                var activeChildCount = Convert.ToInt32(await childCountCommand.ExecuteScalarAsync(cancellationToken));
                if (activeChildCount > 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Cost center masih memiliki child aktif.");
                }
            }

            await using (var deactivateCommand = new NpgsqlCommand(@"
UPDATE gl_cost_centers
SET is_active = FALSE,
    updated_by = @actor,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id;", connection, transaction))
            {
                deactivateCommand.Parameters.AddWithValue("id", costCenterId);
                deactivateCommand.Parameters.AddWithValue("company_id", companyId);
                deactivateCommand.Parameters.AddWithValue("location_id", locationId);
                deactivateCommand.Parameters.AddWithValue("actor", actor);
                await deactivateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "COST_CENTER",
                costCenterId,
                "DEACTIVATE",
                actor,
                $"company={companyId};location={locationId};code={code}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Cost center berhasil dinonaktifkan.", costCenterId);
        }
        catch (Exception ex)
        {
            LogServiceError("SoftDeleteCostCenterFailed", $"action=soft_delete_cost_center status=failed company_id={companyId} location_id={locationId} cost_center_id={costCenterId}", ex);
            return new AccessOperationResult(false, "Gagal menonaktifkan cost center.");
        }
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
        await EnsureInventorySchemaAsync(cancellationToken);

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

            var hasScopeAccess = await HasScopeAccessAsync(
                connection,
                transaction,
                actor,
                companyId,
                locationId,
                cancellationToken);
            if (!hasScopeAccess)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Anda tidak memiliki akses ke company/lokasi untuk membuka/menutup periode.");
            }

            await EnsureAccountingPeriodRowAsync(
                connection,
                transaction,
                companyId,
                locationId,
                monthStart,
                cancellationToken);

            var nextMonth = monthStart.AddMonths(1);
            var nextPeriodAutoCreated = false;
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

                var pendingInventoryTransactions = await CountPendingInventoryTransactionsAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    monthStart,
                    cancellationToken);
                if (pendingInventoryTransactions > 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(
                        false,
                        $"Masih ada {pendingInventoryTransactions:N0} transaksi inventory belum POSTED pada periode {monthStart:yyyy-MM}.");
                }

                var pendingStockOpname = await CountPendingStockOpnameAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    monthStart,
                    cancellationToken);
                if (pendingStockOpname > 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(
                        false,
                        $"Masih ada {pendingStockOpname:N0} stock opname belum POSTED pada periode {monthStart:yyyy-MM}.");
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

                nextPeriodAutoCreated = await EnsureNextAccountingPeriodAvailableAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId,
                    nextMonth,
                    cancellationToken);
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
            var nextPeriodMessage = !isOpen
                ? nextPeriodAutoCreated
                    ? $" Periode berikutnya {nextMonth:yyyy-MM} otomatis dibuat dan dibuka."
                    : $" Periode berikutnya {nextMonth:yyyy-MM} sudah ada."
                : string.Empty;
            return new AccessOperationResult(
                true,
                isOpen
                    ? $"Periode {monthStart:yyyy-MM} berhasil dibuka."
                    : string.IsNullOrWhiteSpace(closingJournalNo)
                        ? $"Periode {monthStart:yyyy-MM} berhasil ditutup.{nextPeriodMessage}{closeWarning}"
                        : $"Periode {monthStart:yyyy-MM} berhasil ditutup. Jurnal penutup: {closingJournalNo}.{nextPeriodMessage}{closeWarning}");
        }
        catch (Exception ex)
        {
            LogServiceError("SetAccountingPeriodStateFailed", $"action=set_period_open_state status=failed period={periodMonth:yyyy-MM} is_open={isOpen} company_id={companyId} location_id={locationId}", ex);
            return new AccessOperationResult(false, "Gagal memperbarui status periode akuntansi.");
        }
    }

    private static async Task<bool> EnsureNextAccountingPeriodAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        DateTime nextPeriodMonth,
        CancellationToken cancellationToken)
    {
        var normalizedNextMonth = GetPeriodMonthStart(nextPeriodMonth);

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
    'AUTO_OPENED_NEXT_PERIOD',
    NOW(),
    NOW())
ON CONFLICT (company_id, location_id, period_month) DO NOTHING;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", normalizedNextMonth);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        return affectedRows > 0;
    }

    private static async Task<int> CountPendingInventoryTransactionsAsync(
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
FROM inv_stock_transactions
WHERE company_id = @company_id
  AND location_id = @location_id
  AND transaction_date BETWEEN @date_from AND @date_to
  AND upper(status) <> 'POSTED';", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", monthStart);
        command.Parameters.AddWithValue("date_to", monthEnd);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> CountPendingStockOpnameAsync(
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
FROM inv_stock_opname
WHERE company_id = @company_id
  AND location_id = @location_id
  AND opname_date BETWEEN @date_from AND @date_to
  AND upper(status) <> 'POSTED';", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", monthStart);
        command.Parameters.AddWithValue("date_to", monthEnd);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
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
        string actorUsername = "",
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

        if (!await HasAnyPermissionAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                AccountingModuleCode,
                AccountingSubmoduleTransactions,
                AccountingTransactionsReadActions,
                companyId,
                locationId,
                cancellationToken))
        {
            return data;
        }

        await using (var accountCommand = new NpgsqlCommand(@"
SELECT id,
       company_id,
       account_code,
       account_name,
       account_type,
       is_active,
       COALESCE(requires_department, FALSE),
       COALESCE(requires_project, FALSE),
       COALESCE(requires_cost_center, FALSE),
       COALESCE(requires_partner, FALSE),
       COALESCE(allowed_subledger_type, '')
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
                    AccountType = NormalizeAccountType(reader.GetString(4)),
                    IsActive = !reader.IsDBNull(5) && reader.GetBoolean(5),
                    RequiresDepartment = !reader.IsDBNull(6) && reader.GetBoolean(6),
                    RequiresProject = !reader.IsDBNull(7) && reader.GetBoolean(7),
                    RequiresCostCenter = !reader.IsDBNull(8) && reader.GetBoolean(8),
                    RequiresSubledger = !reader.IsDBNull(9) && reader.GetBoolean(9),
                    AllowedSubledgerType = reader.IsDBNull(10) ? string.Empty : NormalizeSubledgerType(reader.GetString(10))
                });
            }
        }

        var estateHierarchy = await GetEstateHierarchyAsync(
            companyId,
            locationId,
            includeInactive: false,
            actorUsername,
            cancellationToken);
        foreach (var costCenter in BuildJournalBlockReferences(estateHierarchy))
        {
            data.CostCenters.Add(costCenter);
        }

        foreach (var vendor in await GetVendorsAsync(companyId, includeInactive: false, actorUsername: actorUsername, cancellationToken: cancellationToken))
        {
            data.Vendors.Add(vendor);
        }

        foreach (var customer in await GetCustomersAsync(companyId, includeInactive: false, actorUsername: actorUsername, cancellationToken: cancellationToken))
        {
            data.Customers.Add(customer);
        }

        foreach (var employee in await GetEmployeesAsync(companyId, includeInactive: false, actorUsername: actorUsername, cancellationToken: cancellationToken))
        {
            data.Employees.Add(employee);
        }

        await using (var listCommand = new NpgsqlCommand(@"
SELECT h.id,
       h.journal_no,
       h.journal_date,
       h.status,
       COALESCE(h.created_by, '')
FROM gl_journal_headers h
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
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
                    Status = reader.GetString(3),
                    CreatedBy = reader.GetString(4)
                });
            }
        }

        return data;
    }

    private static IEnumerable<ManagedCostCenter> BuildJournalBlockReferences(EstateHierarchyWorkspace workspace)
    {
        foreach (var estate in workspace.Estates.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var division in estate.Divisions.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var block in division.Blocks.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
                {
                    yield return new ManagedCostCenter
                    {
                        Id = block.Id,
                        BlockId = block.Id,
                        CompanyId = block.CompanyId,
                        LocationId = block.LocationId,
                        ParentId = division.Id,
                        CostCenterCode = block.CostCenterCode,
                        CostCenterName = block.Name,
                        EstateCode = block.EstateCode,
                        EstateName = block.EstateName,
                        DivisionCode = block.DivisionCode,
                        DivisionName = block.DivisionName,
                        BlockCode = block.Code,
                        BlockName = block.Name,
                        Level = "BLOCK",
                        IsPosting = true,
                        IsActive = block.IsActive,
                        IsDirectBlockSource = true,
                        SourceTable = "BLOCKS"
                    };
                }
            }
        }
    }

    private static string NormalizeDimensionCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToUpperInvariant();
    }

    private static string NormalizeDimensionName(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : name.Trim();
    }

    private static string? ResolveCostCenterLevel(
        string estateCode,
        string divisionCode,
        string blockCode)
    {
        if (string.IsNullOrWhiteSpace(estateCode))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(divisionCode) && string.IsNullOrWhiteSpace(blockCode))
        {
            return "ESTATE";
        }

        if (!string.IsNullOrWhiteSpace(divisionCode) && string.IsNullOrWhiteSpace(blockCode))
        {
            return "DIVISION";
        }

        if (!string.IsNullOrWhiteSpace(divisionCode) && !string.IsNullOrWhiteSpace(blockCode))
        {
            return "BLOCK";
        }

        return null;
    }

    private static string BuildCostCenterCode(
        string estateCode,
        string divisionCode,
        string blockCode)
    {
        return string.Join(
            "-",
            new[] { estateCode, divisionCode, blockCode }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildCostCenterName(
        string level,
        string? explicitName,
        string estateCode,
        string estateName,
        string divisionCode,
        string divisionName,
        string blockCode,
        string blockName)
    {
        var normalizedExplicitName = NormalizeDimensionName(explicitName);
        if (!string.IsNullOrWhiteSpace(normalizedExplicitName))
        {
            return normalizedExplicitName;
        }

        return level switch
        {
            "ESTATE" => string.IsNullOrWhiteSpace(estateName) ? estateCode : estateName,
            "DIVISION" => string.IsNullOrWhiteSpace(divisionName) ? $"{estateCode}-{divisionCode}" : divisionName,
            "BLOCK" => string.IsNullOrWhiteSpace(blockName) ? $"{estateCode}-{divisionCode}-{blockCode}" : blockName,
            _ => BuildCostCenterCode(estateCode, divisionCode, blockCode)
        };
    }

    private static async Task<long?> FindCostCenterIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        string estateCode,
        string divisionCode,
        string blockCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT id
FROM gl_cost_centers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND estate_code = @estate_code
  AND division_code = @division_code
  AND block_code = @block_code
LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("estate_code", estateCode);
        command.Parameters.AddWithValue("division_code", divisionCode);
        command.Parameters.AddWithValue("block_code", blockCode);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null || scalar is DBNull ? null : Convert.ToInt64(scalar);
    }
}


