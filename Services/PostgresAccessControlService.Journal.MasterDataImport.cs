using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<AccountImportExecutionResult> ImportAccountMasterDataAsync(
        long companyId,
        AccountImportBundle bundle,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccountImportExecutionResult
            {
                IsSuccess = false,
                Message = "Perusahaan akun tidak valid."
            };
        }

        if (bundle?.Accounts is null || bundle.Accounts.Count == 0)
        {
            return new AccountImportExecutionResult
            {
                IsSuccess = false,
                Message = "Tidak ada data akun yang dapat diimpor."
            };
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
                "Anda tidak memiliki izin untuk mengimpor master akun.");
            if (permissionFailure is not null)
            {
                return new AccountImportExecutionResult
                {
                    IsSuccess = false,
                    Message = permissionFailure.Message
                };
            }

            var existingAccounts = new List<ExistingImportedAccountState>();
            await using (var command = new NpgsqlCommand(@"
SELECT id,
       account_code,
       account_type,
       parent_account_id,
       is_active
FROM gl_accounts
WHERE company_id = @company_id;", connection, transaction))
            {
                command.Parameters.AddWithValue("company_id", companyId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingAccounts.Add(new ExistingImportedAccountState(
                        reader.GetInt64(0),
                        reader.GetString(1).Trim().ToUpperInvariant(),
                        NormalizeAccountType(reader.IsDBNull(2) ? null : reader.GetString(2)),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        !reader.IsDBNull(4) && reader.GetBoolean(4)));
                }
            }

            var existingByCode = existingAccounts.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
            var existingChildrenByParentId = existingAccounts
                .Where(x => x.ParentAccountId.HasValue)
                .GroupBy(x => x.ParentAccountId!.Value)
                .ToDictionary(x => x.Key, x => x.ToList());

            var importRows = bundle.Accounts
                .Select(x => new NormalizedImportAccountRow(
                    x.RowNumber,
                    (x.Code ?? string.Empty).Trim().ToUpperInvariant(),
                    (x.Name ?? string.Empty).Trim(),
                    NormalizeAccountType(x.AccountType),
                    (x.ParentAccountCode ?? string.Empty).Trim().ToUpperInvariant(),
                    x.IsActive,
                    x.RequiresDepartment,
                    x.RequiresProject,
                    x.RequiresCostCenter,
                    x.RequiresSubledger,
                    NormalizeSubledgerType(x.AllowedSubledgerType)))
                .ToList();

            var fileByCode = importRows.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
            var errors = new List<InventoryImportError>();

            foreach (var row in importRows)
            {
                if (string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Name))
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, "Code dan Name wajib diisi."));
                    continue;
                }

                if (!IsSegmentedAccountCode(row.Code))
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, $"Format kode akun tidak valid: {row.Code}. Gunakan XX.99999.999."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.AccountType))
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, $"AccountType tidak valid untuk akun {row.Code}."));
                    continue;
                }

                if (row.RequiresSubledger && string.IsNullOrWhiteSpace(row.AllowedSubledgerType))
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, $"AllowedSubledgerType wajib diisi untuk akun {row.Code} yang memerlukan buku bantu."));
                    continue;
                }

                if (string.Equals(row.Code, row.ParentAccountCode, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, $"ParentAccountCode tidak boleh sama dengan Code untuk akun {row.Code}."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.ParentAccountCode))
                {
                    if (existingByCode.TryGetValue(row.Code, out var existingRoot) &&
                        existingChildrenByParentId.TryGetValue(existingRoot.Id, out var children))
                    {
                        foreach (var child in children.Where(x => !fileByCode.ContainsKey(x.Code)))
                        {
                            if (!string.Equals(child.AccountType, row.AccountType, StringComparison.OrdinalIgnoreCase))
                            {
                                errors.Add(BuildAccountImportError(
                                    row.RowNumber,
                                    $"Perubahan AccountType untuk akun root {row.Code} bentrok dengan child existing {child.Code}."));
                                break;
                            }
                        }
                    }

                    continue;
                }

                if (fileByCode.TryGetValue(row.ParentAccountCode, out var parentFromFile))
                {
                    if (!string.IsNullOrWhiteSpace(parentFromFile.ParentAccountCode))
                    {
                        errors.Add(BuildAccountImportError(row.RowNumber, $"Parent akun {row.ParentAccountCode} harus akun summary/root."));
                        continue;
                    }

                    if (!parentFromFile.IsActive)
                    {
                        errors.Add(BuildAccountImportError(row.RowNumber, $"Parent akun {row.ParentAccountCode} nonaktif dan tidak dapat dipilih."));
                        continue;
                    }

                    if (!string.Equals(parentFromFile.AccountType, row.AccountType, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(BuildAccountImportError(row.RowNumber, $"Tipe akun child {row.Code} harus sama dengan parent {row.ParentAccountCode}."));
                    }

                    continue;
                }

                if (!existingByCode.TryGetValue(row.ParentAccountCode, out var parentFromDb))
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, $"Parent akun {row.ParentAccountCode} tidak ditemukan."));
                    continue;
                }

                if (parentFromDb.ParentAccountId.HasValue)
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, $"Parent akun {row.ParentAccountCode} harus akun level 1 (summary)."));
                    continue;
                }

                if (!parentFromDb.IsActive)
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, $"Parent akun {row.ParentAccountCode} nonaktif dan tidak dapat dipilih."));
                    continue;
                }

                if (!string.Equals(parentFromDb.AccountType, row.AccountType, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(BuildAccountImportError(row.RowNumber, $"Tipe akun child {row.Code} harus sama dengan parent {row.ParentAccountCode}."));
                }
            }

            if (errors.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccountImportExecutionResult
                {
                    IsSuccess = false,
                    Message = $"Validasi import master akun gagal. Terdapat {errors.Count} error.",
                    Errors = errors
                };
            }

            var importedIdsByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var createdCount = 0;
            var updatedCount = 0;

            foreach (var row in importRows.Where(x => string.IsNullOrWhiteSpace(x.ParentAccountCode)).OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
            {
                var accountId = await UpsertImportedAccountAsync(
                    connection,
                    transaction,
                    companyId,
                    actor,
                    row,
                    parentAccountId: null,
                    isPosting: false,
                    hierarchyLevel: 1,
                    existingByCode,
                    cancellationToken);

                importedIdsByCode[row.Code] = accountId;
                if (existingByCode[row.Code].WasCreated)
                {
                    createdCount++;
                }
                else
                {
                    updatedCount++;
                }
            }

            foreach (var row in importRows.Where(x => !string.IsNullOrWhiteSpace(x.ParentAccountCode)).OrderBy(x => x.ParentAccountCode, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
            {
                long? parentAccountId = importedIdsByCode.TryGetValue(row.ParentAccountCode, out var importedParentId)
                    ? importedParentId
                    : existingByCode.TryGetValue(row.ParentAccountCode, out var parentFromDb)
                        ? parentFromDb.Id
                        : null;

                if (!parentAccountId.HasValue)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccountImportExecutionResult
                    {
                        IsSuccess = false,
                        Message = $"Parent akun {row.ParentAccountCode} tidak berhasil di-resolve.",
                        Errors = [BuildAccountImportError(row.RowNumber, $"Parent akun {row.ParentAccountCode} tidak berhasil di-resolve.")]
                    };
                }

                var accountId = await UpsertImportedAccountAsync(
                    connection,
                    transaction,
                    companyId,
                    actor,
                    row,
                    parentAccountId,
                    isPosting: true,
                    hierarchyLevel: 2,
                    existingByCode,
                    cancellationToken);

                importedIdsByCode[row.Code] = accountId;
                if (existingByCode[row.Code].WasCreated)
                {
                    createdCount++;
                }
                else
                {
                    updatedCount++;
                }
            }

            await RebuildAccountHierarchyInternalAsync(
                connection,
                transaction,
                companyId,
                actor,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccountImportExecutionResult
            {
                IsSuccess = true,
                Message = $"Import master akun berhasil. Create {createdCount}, update {updatedCount}.",
                CreatedCount = createdCount,
                UpdatedCount = updatedCount
            };
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("ImportAccountMasterDuplicate", $"action=import_account_master status=duplicate company_id={companyId}", ex);
            return new AccountImportExecutionResult
            {
                IsSuccess = false,
                Message = "Import master akun gagal karena ada kode akun duplikat."
            };
        }
        catch (Exception ex)
        {
            LogServiceError("ImportAccountMasterFailed", $"action=import_account_master status=failed company_id={companyId}", ex);
            return new AccountImportExecutionResult
            {
                IsSuccess = false,
                Message = "Gagal mengimpor master akun."
            };
        }
    }

    private static InventoryImportError BuildAccountImportError(int rowNumber, string message)
    {
        return new InventoryImportError
        {
            SheetName = "Accounts",
            RowNumber = rowNumber <= 0 ? 1 : rowNumber,
            Message = message
        };
    }

    private async Task<long> UpsertImportedAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string actor,
        NormalizedImportAccountRow row,
        long? parentAccountId,
        bool isPosting,
        int hierarchyLevel,
        IDictionary<string, ExistingImportedAccountState> existingByCode,
        CancellationToken cancellationToken)
    {
        var normalBalance = row.AccountType is "LIABILITY" or "EQUITY" or "REVENUE" ? "C" : "D";
        if (existingByCode.TryGetValue(row.Code, out var existing))
        {
            await using var updateCommand = new NpgsqlCommand(@"
UPDATE gl_accounts
SET account_name = @account_name,
    account_type = @account_type,
    normal_balance = @normal_balance,
    parent_account_id = @parent_account_id,
    is_posting = @is_posting,
    hierarchy_level = @hierarchy_level,
    is_active = @is_active,
    requires_department = @requires_department,
    requires_project = @requires_project,
    requires_cost_center = @requires_cost_center,
    requires_partner = @requires_subledger,
    allowed_subledger_type = @allowed_subledger_type,
    updated_by = @actor,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
            updateCommand.Parameters.AddWithValue("id", existing.Id);
            updateCommand.Parameters.AddWithValue("company_id", companyId);
            updateCommand.Parameters.AddWithValue("account_name", row.Name);
            updateCommand.Parameters.AddWithValue("account_type", row.AccountType);
            updateCommand.Parameters.AddWithValue("normal_balance", normalBalance);
            updateCommand.Parameters.AddWithValue("parent_account_id", NpgsqlDbType.Bigint, parentAccountId.HasValue ? parentAccountId.Value : DBNull.Value);
            updateCommand.Parameters.AddWithValue("is_posting", isPosting);
            updateCommand.Parameters.AddWithValue("hierarchy_level", hierarchyLevel);
            updateCommand.Parameters.AddWithValue("is_active", row.IsActive);
            updateCommand.Parameters.AddWithValue("requires_department", row.RequiresDepartment);
            updateCommand.Parameters.AddWithValue("requires_project", row.RequiresProject);
            updateCommand.Parameters.AddWithValue("requires_cost_center", row.RequiresCostCenter);
            updateCommand.Parameters.AddWithValue("requires_subledger", row.RequiresSubledger);
            updateCommand.Parameters.AddWithValue("allowed_subledger_type", row.AllowedSubledgerType);
            updateCommand.Parameters.AddWithValue("actor", actor);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            await InsertAuditLogAsync(
                connection,
                transaction,
                "ACCOUNT",
                existing.Id,
                "IMPORT_UPDATE",
                actor,
                $"company={companyId};code={row.Code};name={row.Name};type={row.AccountType};normal_balance={normalBalance};active={row.IsActive};parent={row.ParentAccountCode};requires_department={row.RequiresDepartment};requires_project={row.RequiresProject};requires_cost_center={row.RequiresCostCenter};requires_subledger={row.RequiresSubledger};allowed_subledger_type={row.AllowedSubledgerType}",
                cancellationToken);

            existingByCode[row.Code] = existing with
            {
                AccountType = row.AccountType,
                ParentAccountId = parentAccountId,
                IsActive = row.IsActive,
                WasCreated = false
            };
            return existing.Id;
        }

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
    @requires_subledger,
    @allowed_subledger_type,
    @actor,
    NOW(),
    @actor,
    NOW())
RETURNING id;", connection, transaction);
        insertCommand.Parameters.AddWithValue("company_id", companyId);
        insertCommand.Parameters.AddWithValue("account_code", row.Code);
        insertCommand.Parameters.AddWithValue("account_name", row.Name);
        insertCommand.Parameters.AddWithValue("account_type", row.AccountType);
        insertCommand.Parameters.AddWithValue("normal_balance", normalBalance);
        insertCommand.Parameters.AddWithValue("parent_account_id", NpgsqlDbType.Bigint, parentAccountId.HasValue ? parentAccountId.Value : DBNull.Value);
        insertCommand.Parameters.AddWithValue("is_posting", isPosting);
        insertCommand.Parameters.AddWithValue("hierarchy_level", hierarchyLevel);
        insertCommand.Parameters.AddWithValue("is_active", row.IsActive);
        insertCommand.Parameters.AddWithValue("requires_department", row.RequiresDepartment);
        insertCommand.Parameters.AddWithValue("requires_project", row.RequiresProject);
        insertCommand.Parameters.AddWithValue("requires_cost_center", row.RequiresCostCenter);
        insertCommand.Parameters.AddWithValue("requires_subledger", row.RequiresSubledger);
        insertCommand.Parameters.AddWithValue("allowed_subledger_type", row.AllowedSubledgerType);
        insertCommand.Parameters.AddWithValue("actor", actor);
        var createdId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));

        await InsertAuditLogAsync(
            connection,
            transaction,
            "ACCOUNT",
            createdId,
            "IMPORT_CREATE",
            actor,
            $"company={companyId};code={row.Code};name={row.Name};type={row.AccountType};normal_balance={normalBalance};active={row.IsActive};parent={row.ParentAccountCode};requires_department={row.RequiresDepartment};requires_project={row.RequiresProject};requires_cost_center={row.RequiresCostCenter};requires_subledger={row.RequiresSubledger};allowed_subledger_type={row.AllowedSubledgerType}",
            cancellationToken);

        existingByCode[row.Code] = new ExistingImportedAccountState(
            createdId,
            row.Code,
            row.AccountType,
            parentAccountId,
            row.IsActive,
            true);
        return createdId;
    }

    private sealed record ExistingImportedAccountState(
        long Id,
        string Code,
        string AccountType,
        long? ParentAccountId,
        bool IsActive,
        bool WasCreated = false);

    private sealed record NormalizedImportAccountRow(
        int RowNumber,
        string Code,
        string Name,
        string AccountType,
        string ParentAccountCode,
        bool IsActive,
        bool RequiresDepartment,
        bool RequiresProject,
        bool RequiresCostCenter,
        bool RequiresSubledger,
        string AllowedSubledgerType);
}
