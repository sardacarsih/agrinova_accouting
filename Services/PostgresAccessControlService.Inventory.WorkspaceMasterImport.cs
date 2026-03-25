using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<InventoryWorkspaceData> GetInventoryWorkspaceDataAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var data = new InventoryWorkspaceData();
        if (companyId <= 0 || locationId <= 0)
        {
            return data;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var initCosting = await connection.BeginTransactionAsync(cancellationToken))
        {
            await EnsureInventoryCostingStateInitializedAsync(
                connection,
                initCosting,
                companyId,
                "SYSTEM",
                locationId,
                cancellationToken);
            await initCosting.CommitAsync(cancellationToken);
        }

        var masterCompanyId = await GetInventoryMasterCompanyIdInternalAsync(connection, null, cancellationToken);
        data.MasterCompanyId = masterCompanyId;
        data.CanMaintainMasterInventoryData = masterCompanyId.HasValue && masterCompanyId.Value == companyId;
        if (masterCompanyId.HasValue)
        {
            await using var masterCompanyCommand = new NpgsqlCommand(@"
SELECT code, name
FROM org_companies
WHERE id = @id;", connection);
            masterCompanyCommand.Parameters.AddWithValue("id", masterCompanyId.Value);
            await using var masterReader = await masterCompanyCommand.ExecuteReaderAsync(cancellationToken);
            if (await masterReader.ReadAsync(cancellationToken))
            {
                data.MasterCompanyCode = masterReader.GetString(0);
                data.MasterCompanyName = masterReader.GetString(1);
            }
        }

        // Load categories
        await using var catCommand = new NpgsqlCommand(@"
SELECT id, category_code, category_name, account_code, is_active
FROM inv_categories
ORDER BY category_code;", connection);

        await using var catReader = await catCommand.ExecuteReaderAsync(cancellationToken);
        while (await catReader.ReadAsync(cancellationToken))
        {
            data.Categories.Add(new ManagedInventoryCategory
            {
                Id = catReader.GetInt64(0),
                CompanyId = companyId,
                Code = catReader.GetString(1),
                Name = catReader.GetString(2),
                AccountCode = catReader.GetString(3),
                IsActive = catReader.GetBoolean(4)
            });
        }

        await catReader.CloseAsync();

        // Load items with category name
        await using var itemsCommand = new NpgsqlCommand(@"
SELECT i.id, i.category_id, COALESCE(c.category_name, '') AS category_name,
       i.item_code, i.item_name, i.uom, i.category, i.is_active
FROM inv_items i
LEFT JOIN inv_categories c ON c.id = i.category_id
ORDER BY i.item_code;", connection);

        await using var itemsReader = await itemsCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemsReader.ReadAsync(cancellationToken))
        {
            data.Items.Add(new ManagedInventoryItem
            {
                Id = itemsReader.GetInt64(0),
                CompanyId = companyId,
                CategoryId = itemsReader.IsDBNull(1) ? null : itemsReader.GetInt64(1),
                CategoryName = itemsReader.GetString(2),
                Code = itemsReader.GetString(3),
                Name = itemsReader.GetString(4),
                Uom = itemsReader.GetString(5),
                Category = itemsReader.GetString(6),
                IsActive = itemsReader.GetBoolean(7)
            });
        }

        await itemsReader.CloseAsync();

        // Load stock entries
        await using var stockCommand = new NpgsqlCommand(@"
SELECT s.id, s.item_id, i.item_code, i.item_name, i.uom,
       l.code AS location_code, l.name AS location_name, s.qty
FROM inv_stock s
JOIN inv_items i ON i.id = s.item_id
JOIN org_locations l ON l.id = s.location_id
WHERE s.company_id = @company_id
  AND s.location_id = @location_id
ORDER BY i.item_code, l.code;", connection);
        stockCommand.Parameters.AddWithValue("company_id", companyId);
        stockCommand.Parameters.AddWithValue("location_id", locationId);

        await using var stockReader = await stockCommand.ExecuteReaderAsync(cancellationToken);
        while (await stockReader.ReadAsync(cancellationToken))
        {
            data.StockEntries.Add(new ManagedStockEntry
            {
                Id = stockReader.GetInt64(0),
                ItemId = stockReader.GetInt64(1),
                ItemCode = stockReader.GetString(2),
                ItemName = stockReader.GetString(3),
                Uom = stockReader.GetString(4),
                LocationCode = stockReader.GetString(5),
                LocationName = stockReader.GetString(6),
                Qty = stockReader.GetDecimal(7)
            });
        }

        await stockReader.CloseAsync();

        // Load units
        await using var unitsCommand = new NpgsqlCommand(@"
SELECT id, company_id, unit_code, unit_name, is_active
FROM inv_units
WHERE company_id = @company_id
ORDER BY unit_code;", connection);
        unitsCommand.Parameters.AddWithValue("company_id", companyId);

        await using var unitsReader = await unitsCommand.ExecuteReaderAsync(cancellationToken);
        while (await unitsReader.ReadAsync(cancellationToken))
        {
            data.Units.Add(new ManagedInventoryUnit
            {
                Id = unitsReader.GetInt64(0),
                CompanyId = unitsReader.GetInt64(1),
                Code = unitsReader.GetString(2),
                Name = unitsReader.GetString(3),
                IsActive = unitsReader.GetBoolean(4)
            });
        }

        await unitsReader.CloseAsync();

        // Load warehouses
        await using var warehouseCommand = new NpgsqlCommand(@"
SELECT w.id,
       w.company_id,
       w.warehouse_code,
       w.warehouse_name,
       w.location_id,
       COALESCE(l.name, '') AS location_name,
       w.is_active
FROM inv_warehouses w
LEFT JOIN org_locations l ON l.id = w.location_id
WHERE w.company_id = @company_id
ORDER BY w.warehouse_code;", connection);
        warehouseCommand.Parameters.AddWithValue("company_id", companyId);

        await using var warehouseReader = await warehouseCommand.ExecuteReaderAsync(cancellationToken);
        while (await warehouseReader.ReadAsync(cancellationToken))
        {
            data.Warehouses.Add(new ManagedWarehouse
            {
                Id = warehouseReader.GetInt64(0),
                CompanyId = warehouseReader.GetInt64(1),
                Code = warehouseReader.GetString(2),
                Name = warehouseReader.GetString(3),
                LocationId = warehouseReader.IsDBNull(4) ? null : warehouseReader.GetInt64(4),
                LocationName = warehouseReader.GetString(5),
                IsActive = warehouseReader.GetBoolean(6)
            });
        }

        await warehouseReader.CloseAsync();

        // Load posting accounts for account code picker
        await using var accCommand = new NpgsqlCommand(@"
SELECT id, company_id, account_code, account_name, account_type,
       parent_account_id, COALESCE('', '') AS parent_account_code,
       COALESCE(hierarchy_level, 1), is_posting, is_active
FROM gl_accounts
WHERE company_id = @company_id AND is_active = TRUE AND is_posting = TRUE
ORDER BY account_code;", connection);
        accCommand.Parameters.AddWithValue("company_id", companyId);

        await using var accReader = await accCommand.ExecuteReaderAsync(cancellationToken);
        while (await accReader.ReadAsync(cancellationToken))
        {
            data.Accounts.Add(new ManagedAccount
            {
                Id = accReader.GetInt64(0),
                CompanyId = accReader.GetInt64(1),
                Code = accReader.GetString(2),
                Name = accReader.GetString(3),
                AccountType = accReader.GetString(4),
                ParentAccountId = accReader.IsDBNull(5) ? null : accReader.GetInt64(5),
                ParentAccountCode = accReader.GetString(6),
                HierarchyLevel = accReader.GetInt32(7),
                IsPosting = accReader.GetBoolean(8),
                IsActive = accReader.GetBoolean(9)
            });
        }

        return data;
    }

    public async Task<long?> GetInventoryMasterCompanyIdAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await GetInventoryMasterCompanyIdInternalAsync(connection, null, cancellationToken);
    }

    public async Task<AccessOperationResult> SetInventoryMasterCompanyIdAsync(
        long masterCompanyId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (masterCompanyId <= 0)
        {
            return new AccessOperationResult(false, "Master company tidak valid.");
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
                InventoryModuleCode,
                InventorySubmoduleApiInv,
                PermissionActionManageMasterCompany,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengatur master company inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using (var companyCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM org_companies
WHERE id = @id
  AND is_active = TRUE;", connection, transaction))
            {
                companyCommand.Parameters.AddWithValue("id", masterCompanyId);
                var exists = Convert.ToInt32(await companyCommand.ExecuteScalarAsync(cancellationToken));
                if (exists <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Master company tidak ditemukan atau tidak aktif.");
                }
            }

            await using (var upsertCommand = new NpgsqlCommand(@"
INSERT INTO app_system_settings(setting_key, setting_value, updated_by, updated_at)
VALUES (@setting_key, @setting_value, @updated_by, NOW())
ON CONFLICT (setting_key) DO UPDATE
SET setting_value = EXCLUDED.setting_value,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();", connection, transaction))
            {
                upsertCommand.Parameters.AddWithValue("setting_key", InventoryMasterCompanySettingKey);
                upsertCommand.Parameters.AddWithValue("setting_value", masterCompanyId.ToString(CultureInfo.InvariantCulture));
                upsertCommand.Parameters.AddWithValue("updated_by", actor);
                await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INVENTORY_SETTING",
                masterCompanyId,
                "SET_MASTER_COMPANY",
                actor,
                $"setting_key={InventoryMasterCompanySettingKey};master_company_id={masterCompanyId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Master company inventory berhasil diperbarui.", masterCompanyId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan master company inventory: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SyncInventoryMasterDataAsync(
        long targetCompanyId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (targetCompanyId <= 0)
        {
            return new AccessOperationResult(false, "Company target tidak valid.");
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
                InventoryModuleCode,
                InventorySubmoduleApiInv,
                PermissionActionSyncDownload,
                targetCompanyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk sinkronisasi master inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var masterCompanyId = await GetInventoryMasterCompanyIdInternalAsync(connection, transaction, cancellationToken);
            if (!masterCompanyId.HasValue || masterCompanyId.Value <= 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Master company inventory belum dikonfigurasi di Settings.");
            }

            await using (var targetExistsCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM org_companies
WHERE id = @company_id
  AND is_active = TRUE;", connection, transaction))
            {
                targetExistsCommand.Parameters.AddWithValue("company_id", targetCompanyId);
                var targetExists = Convert.ToInt32(await targetExistsCommand.ExecuteScalarAsync(cancellationToken));
                if (targetExists <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Company target tidak ditemukan atau tidak aktif.");
                }
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_SYNC",
                targetCompanyId,
                "SYNC_MASTER_GLOBAL_NOOP",
                actor,
                $"master_company_id={masterCompanyId.Value};target_company_id={targetCompanyId};note=inventory_master_global_noop",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(
                true,
                "Sync master dilewati karena master inventory (kategori/item) sudah global.",
                targetCompanyId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal sync master inventory: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SaveInventoryCategoryAsync(
        long companyId,
        ManagedInventoryCategory category,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan tidak valid.");
        }

        if (category is null)
        {
            return new AccessOperationResult(false, "Data kategori tidak valid.");
        }

        if (string.IsNullOrWhiteSpace(category.Code) || string.IsNullOrWhiteSpace(category.Name))
        {
            return new AccessOperationResult(false, "Kode dan nama kategori wajib diisi.");
        }

        var normalizedCode = category.Code.Trim().ToUpperInvariant();
        var normalizedName = category.Name.Trim();
        var normalizedAccountCode = (category.AccountCode ?? string.Empty).Trim().ToUpperInvariant();

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
                InventoryModuleCode,
                InventorySubmoduleCategory,
                ResolveWriteAction(category.Id),
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola kategori inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var writeGuard = await ValidateInventoryMasterWriteAccessAsync(connection, transaction, companyId, cancellationToken);
            if (!writeGuard.IsAllowed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, writeGuard.Message);
            }

            long? existingByCodeId = null;
            var existingByCodeIsActive = false;
            await using (var existingByCodeCommand = new NpgsqlCommand(@"
SELECT id, is_active
FROM inv_categories
WHERE upper(category_code) = @category_code
FOR UPDATE;", connection, transaction))
            {
                existingByCodeCommand.Parameters.AddWithValue("category_code", normalizedCode);

                await using var existingReader = await existingByCodeCommand.ExecuteReaderAsync(cancellationToken);
                if (await existingReader.ReadAsync(cancellationToken))
                {
                    existingByCodeId = existingReader.GetInt64(0);
                    existingByCodeIsActive = !existingReader.IsDBNull(1) && existingReader.GetBoolean(1);
                }
            }

            long categoryId;
            string auditAction;
            string successMessage;
            if (existingByCodeId.HasValue && (category.Id <= 0 || existingByCodeId.Value != category.Id))
            {
                if (existingByCodeIsActive)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Kode kategori '{normalizedCode}' sudah digunakan.");
                }

                await using var reactivateCommand = new NpgsqlCommand(@"
UPDATE inv_categories
SET category_code = @category_code,
    category_name = @category_name,
    account_code = @account_code,
    is_active = TRUE,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
;", connection, transaction);
                reactivateCommand.Parameters.AddWithValue("id", existingByCodeId.Value);
                reactivateCommand.Parameters.AddWithValue("category_code", normalizedCode);
                reactivateCommand.Parameters.AddWithValue("category_name", normalizedName);
                reactivateCommand.Parameters.AddWithValue("account_code", normalizedAccountCode);
                reactivateCommand.Parameters.AddWithValue("updated_by", actor);

                var reactivated = await reactivateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (reactivated <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Kategori tidak ditemukan untuk diaktifkan kembali.");
                }

                categoryId = existingByCodeId.Value;
                auditAction = "REACTIVATE";
                successMessage = $"Kategori '{normalizedCode}' berhasil diaktifkan kembali.";
            }
            else if (category.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO inv_categories (category_code, category_name, account_code, is_active, created_by, created_at, updated_at)
VALUES (@category_code, @category_name, @account_code, @is_active, @created_by, NOW(), NOW())
RETURNING id;", connection, transaction);
                insertCommand.Parameters.AddWithValue("category_code", normalizedCode);
                insertCommand.Parameters.AddWithValue("category_name", normalizedName);
                insertCommand.Parameters.AddWithValue("account_code", normalizedAccountCode);
                insertCommand.Parameters.AddWithValue("is_active", category.IsActive);
                insertCommand.Parameters.AddWithValue("created_by", actor);

                categoryId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
                auditAction = "CREATE";
                successMessage = $"Kategori '{normalizedCode}' berhasil disimpan.";
            }
            else
            {
                await using (var checkTargetCommand = new NpgsqlCommand(@"
SELECT id
FROM inv_categories
WHERE id = @id
FOR UPDATE;", connection, transaction))
                {
                    checkTargetCommand.Parameters.AddWithValue("id", category.Id);
                    var targetExists = await checkTargetCommand.ExecuteScalarAsync(cancellationToken);
                    if (targetExists is null || targetExists is DBNull)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, "Kategori tidak ditemukan.");
                    }
                }

                await using var updateCommand = new NpgsqlCommand(@"
UPDATE inv_categories
SET category_code = @category_code,
    category_name = @category_name,
    account_code = @account_code,
    is_active = @is_active,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                updateCommand.Parameters.AddWithValue("id", category.Id);
                updateCommand.Parameters.AddWithValue("category_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("category_name", normalizedName);
                updateCommand.Parameters.AddWithValue("account_code", normalizedAccountCode);
                updateCommand.Parameters.AddWithValue("is_active", category.IsActive);
                updateCommand.Parameters.AddWithValue("updated_by", actor);

                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Kategori tidak ditemukan.");
                }

                categoryId = category.Id;
                auditAction = "UPDATE";
                successMessage = $"Kategori '{normalizedCode}' berhasil disimpan.";
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_CATEGORY",
                categoryId,
                auditAction,
                actor,
                $"company={companyId};code={normalizedCode};name={normalizedName};account={normalizedAccountCode}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, successMessage, categoryId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, $"Kode kategori '{normalizedCode}' sudah digunakan.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan kategori: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteInventoryCategoryAsync(
        long companyId,
        long categoryId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0 || categoryId <= 0)
        {
            return new AccessOperationResult(false, "Parameter tidak valid.");
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
                InventoryModuleCode,
                InventorySubmoduleCategory,
                PermissionActionDelete,
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola kategori inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var writeGuard = await ValidateInventoryMasterWriteAccessAsync(connection, transaction, companyId, cancellationToken);
            if (!writeGuard.IsAllowed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, writeGuard.Message);
            }
            bool? isActive = null;
            await using (var checkCommand = new NpgsqlCommand(@"
SELECT is_active
FROM inv_categories
WHERE id = @id
FOR UPDATE;", connection, transaction))
            {
                checkCommand.Parameters.AddWithValue("id", categoryId);
                await using var reader = await checkCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    isActive = !reader.IsDBNull(0) && reader.GetBoolean(0);
                }
            }

            if (!isActive.HasValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Kategori tidak ditemukan.");
            }

            if (!isActive.Value)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Kategori sudah nonaktif.");
            }

            await using var command = new NpgsqlCommand(@"
UPDATE inv_categories
SET is_active = FALSE, updated_by = @updated_by, updated_at = NOW()
WHERE id = @id AND is_active = TRUE;", connection, transaction);
            command.Parameters.AddWithValue("id", categoryId);
            command.Parameters.AddWithValue("updated_by", actor);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected <= 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Kategori tidak dapat dinonaktifkan.");
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_CATEGORY",
                categoryId,
                "DEACTIVATE",
                actor,
                $"company={companyId};category_id={categoryId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Kategori berhasil dinonaktifkan.", categoryId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menonaktifkan kategori: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SaveInventoryItemAsync(
        long companyId,
        ManagedInventoryItem item,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan tidak valid.");
        }

        if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name))
        {
            return new AccessOperationResult(false, "Kode dan nama item wajib diisi.");
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
                InventoryModuleCode,
                InventorySubmoduleItem,
                ResolveWriteAction(item.Id),
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola item inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var writeGuard = await ValidateInventoryMasterWriteAccessAsync(connection, transaction, companyId, cancellationToken);
            if (!writeGuard.IsAllowed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, writeGuard.Message);
            }

            var normalizedCode = item.Code.Trim().ToUpperInvariant();
            var normalizedName = item.Name.Trim();
            var normalizedUom = string.IsNullOrWhiteSpace(item.Uom) ? "PCS" : item.Uom.Trim().ToUpperInvariant();
            var normalizedCategory = string.Empty;
            long? normalizedCategoryId = item.CategoryId.HasValue && item.CategoryId.Value > 0
                ? item.CategoryId.Value
                : null;

            if (normalizedCategoryId.HasValue)
            {
                await using var categoryCommand = new NpgsqlCommand(@"
SELECT category_name
FROM inv_categories
WHERE id = @category_id;", connection, transaction);
                categoryCommand.Parameters.AddWithValue("category_id", normalizedCategoryId.Value);

                await using var categoryReader = await categoryCommand.ExecuteReaderAsync(cancellationToken);
                if (!await categoryReader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Kategori item tidak ditemukan.");
                }

                normalizedCategory = categoryReader.IsDBNull(0) ? string.Empty : categoryReader.GetString(0).Trim();
            }

            object categoryIdParam = normalizedCategoryId.HasValue
                ? normalizedCategoryId.Value
                : DBNull.Value;

            long itemId;
            if (item.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO inv_items (category_id, item_code, item_name, uom, category, is_active, created_by, created_at, updated_at)
VALUES (@category_id, @item_code, @item_name, @uom, @category, @is_active, @created_by, NOW(), NOW())
RETURNING id;", connection, transaction);
                insertCommand.Parameters.Add(new NpgsqlParameter("category_id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = categoryIdParam });
                insertCommand.Parameters.AddWithValue("item_code", normalizedCode);
                insertCommand.Parameters.AddWithValue("item_name", normalizedName);
                insertCommand.Parameters.AddWithValue("uom", normalizedUom);
                insertCommand.Parameters.AddWithValue("category", normalizedCategory);
                insertCommand.Parameters.AddWithValue("is_active", item.IsActive);
                insertCommand.Parameters.AddWithValue("created_by", actor);

                itemId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE inv_items
SET category_id = @category_id,
    item_code = @item_code,
    item_name = @item_name,
    uom = @uom,
    category = @category,
    is_active = @is_active,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                updateCommand.Parameters.AddWithValue("id", item.Id);
                updateCommand.Parameters.Add(new NpgsqlParameter("category_id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = categoryIdParam });
                updateCommand.Parameters.AddWithValue("item_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("item_name", normalizedName);
                updateCommand.Parameters.AddWithValue("uom", normalizedUom);
                updateCommand.Parameters.AddWithValue("category", normalizedCategory);
                updateCommand.Parameters.AddWithValue("is_active", item.IsActive);
                updateCommand.Parameters.AddWithValue("updated_by", actor);

                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                itemId = item.Id;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_ITEM",
                itemId,
                item.Id <= 0 ? "CREATE" : "UPDATE",
                actor,
                $"company={companyId};code={normalizedCode};name={normalizedName};uom={normalizedUom}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, $"Item '{normalizedCode}' berhasil disimpan.", itemId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, $"Kode item '{item.Code.Trim().ToUpperInvariant()}' sudah digunakan.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan item: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteInventoryItemAsync(
        long companyId,
        long itemId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0 || itemId <= 0)
        {
            return new AccessOperationResult(false, "Parameter tidak valid.");
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
                InventoryModuleCode,
                InventorySubmoduleItem,
                PermissionActionDelete,
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola item inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var writeGuard = await ValidateInventoryMasterWriteAccessAsync(connection, transaction, companyId, cancellationToken);
            if (!writeGuard.IsAllowed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, writeGuard.Message);
            }

            await using var command = new NpgsqlCommand(@"
UPDATE inv_items
SET is_active = FALSE, updated_by = @updated_by, updated_at = NOW()
WHERE id = @id;", connection, transaction);
            command.Parameters.AddWithValue("id", itemId);
            command.Parameters.AddWithValue("updated_by", actor);

            await command.ExecuteNonQueryAsync(cancellationToken);

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_ITEM",
                itemId,
                "DEACTIVATE",
                actor,
                $"company={companyId};item_id={itemId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Item berhasil dinonaktifkan.", itemId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menonaktifkan item: {ex.Message}");
        }
    }

    public async Task<InventoryImportExecutionResult> ImportInventoryMasterDataAsync(
        long companyId,
        InventoryImportBundle bundle,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new InventoryImportExecutionResult
            {
                IsSuccess = false,
                Message = "Perusahaan tidak valid."
            };
        }

        bundle ??= new InventoryImportBundle();
        if (bundle.Categories.Count == 0 && bundle.Items.Count == 0)
        {
            return new InventoryImportExecutionResult
            {
                IsSuccess = false,
                Message = "Tidak ada data import untuk diproses."
            };
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
              var permissionFailure = await EnsureInventoryImportPermissionAsync(
                  connection,
                  transaction,
                  actor,
                  InventoryModuleCode,
                  InventorySubmoduleApiInv,
                  PermissionActionImportMasterData,
                  companyId,
                  null,
                  cancellationToken,
                  "Anda tidak memiliki izin untuk import master data inventory.");
              if (permissionFailure is not null)
              {
                  return permissionFailure;
              }

            var writeGuard = await ValidateInventoryMasterWriteAccessAsync(connection, transaction, companyId, cancellationToken);
            if (!writeGuard.IsAllowed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new InventoryImportExecutionResult
                {
                    IsSuccess = false,
                    Message = writeGuard.Message
                };
            }

            var categoryIdByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var categoryNameByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await using (var categoryLookupCommand = new NpgsqlCommand(@"
SELECT id, category_code, category_name, account_code
FROM inv_categories;", connection, transaction))
            {
                await using var reader = await categoryLookupCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var code = reader.GetString(1).Trim().ToUpperInvariant();
                    categoryIdByCode[code] = reader.GetInt64(0);
                    categoryNameByCode[code] = reader.GetString(2).Trim();
                }
            }

            var importedCategoryCount = 0;
            var categoryCodesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in bundle.Categories)
            {
                var code = row.Code.Trim().ToUpperInvariant();
                var name = row.Name.Trim();
                var accountCode = row.AccountCode.Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new InventoryImportExecutionResult
                    {
                        IsSuccess = false,
                        Message = $"Baris kategori {row.RowNumber}: CategoryCode/CategoryName wajib diisi."
                    };
                }

                if (!categoryCodesInFile.Add(code))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new InventoryImportExecutionResult
                    {
                        IsSuccess = false,
                        Message = $"CategoryCode duplikat dalam file: {code}."
                    };
                }

                await using var upsertCategoryCommand = new NpgsqlCommand(@"
INSERT INTO inv_categories (category_code, category_name, account_code, is_active, created_by, created_at, updated_at)
VALUES (@category_code, @category_name, @account_code, @is_active, @actor, NOW(), NOW())
ON CONFLICT (category_code) DO UPDATE
SET category_name = EXCLUDED.category_name,
    account_code = EXCLUDED.account_code,
    is_active = EXCLUDED.is_active,
    updated_by = @actor,
    updated_at = NOW()
RETURNING id;", connection, transaction);
                upsertCategoryCommand.Parameters.AddWithValue("category_code", code);
                upsertCategoryCommand.Parameters.AddWithValue("category_name", name);
                upsertCategoryCommand.Parameters.AddWithValue("account_code", accountCode);
                upsertCategoryCommand.Parameters.AddWithValue("is_active", row.IsActive);
                upsertCategoryCommand.Parameters.AddWithValue("actor", actor);
                var categoryId = Convert.ToInt64(await upsertCategoryCommand.ExecuteScalarAsync(cancellationToken));

                categoryIdByCode[code] = categoryId;
                categoryNameByCode[code] = name;
                importedCategoryCount++;
            }

            var importedItemCount = 0;
            var itemCodesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in bundle.Items)
            {
                var code = row.Code.Trim().ToUpperInvariant();
                var name = row.Name.Trim();
                var uom = string.IsNullOrWhiteSpace(row.Uom) ? "PCS" : row.Uom.Trim().ToUpperInvariant();
                var categoryCode = row.CategoryCode.Trim().ToUpperInvariant();

                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(categoryCode))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new InventoryImportExecutionResult
                    {
                        IsSuccess = false,
                        Message = $"Baris item {row.RowNumber}: ItemCode/ItemName/CategoryCode wajib diisi."
                    };
                }

                if (!itemCodesInFile.Add(code))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new InventoryImportExecutionResult
                    {
                        IsSuccess = false,
                        Message = $"ItemCode duplikat dalam file: {code}."
                    };
                }

                if (!categoryIdByCode.TryGetValue(categoryCode, out var categoryId))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new InventoryImportExecutionResult
                    {
                        IsSuccess = false,
                        Message = $"Baris item {row.RowNumber}: CategoryCode '{categoryCode}' tidak ditemukan."
                    };
                }

                var categoryName = categoryNameByCode.TryGetValue(categoryCode, out var resolvedCategoryName)
                    ? resolvedCategoryName
                    : string.Empty;

                await using var upsertItemCommand = new NpgsqlCommand(@"
INSERT INTO inv_items (category_id, item_code, item_name, uom, category, is_active, created_by, created_at, updated_at)
VALUES (@category_id, @item_code, @item_name, @uom, @category, @is_active, @actor, NOW(), NOW())
ON CONFLICT (item_code) DO UPDATE
SET category_id = EXCLUDED.category_id,
    item_name = EXCLUDED.item_name,
    uom = EXCLUDED.uom,
    category = EXCLUDED.category,
    is_active = EXCLUDED.is_active,
    updated_by = @actor,
    updated_at = NOW();", connection, transaction);
                upsertItemCommand.Parameters.AddWithValue("category_id", categoryId);
                upsertItemCommand.Parameters.AddWithValue("item_code", code);
                upsertItemCommand.Parameters.AddWithValue("item_name", name);
                upsertItemCommand.Parameters.AddWithValue("uom", uom);
                upsertItemCommand.Parameters.AddWithValue("category", categoryName);
                upsertItemCommand.Parameters.AddWithValue("is_active", row.IsActive);
                upsertItemCommand.Parameters.AddWithValue("actor", actor);
                await upsertItemCommand.ExecuteNonQueryAsync(cancellationToken);
                importedItemCount++;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_IMPORT",
                companyId,
                "MASTER_IMPORT",
                actor,
                $"actor={actor};company={companyId};category_rows={bundle.Categories.Count};item_rows={bundle.Items.Count};categories_imported={importedCategoryCount};items_imported={importedItemCount}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new InventoryImportExecutionResult
            {
                IsSuccess = true,
                Message = $"Import berhasil. Kategori: {importedCategoryCount}, Item: {importedItemCount}.",
                ImportedCategoryCount = importedCategoryCount,
                ImportedItemCount = importedItemCount
            };
        }
        catch (Exception ex)
        {
            return new InventoryImportExecutionResult
            {
                IsSuccess = false,
                Message = $"Import inventory gagal: {ex.Message}"
            };
        }
    }

    public async Task<InventoryOpeningBalanceExecutionResult> ImportInventoryOpeningBalanceAsync(
        long companyId,
        InventoryOpeningBalanceBundle bundle,
        string actorUsername,
        bool validateOnly = false,
        bool replaceExistingBatch = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new InventoryOpeningBalanceExecutionResult
            {
                IsSuccess = false,
                IsValidationOnly = validateOnly,
                Message = "Perusahaan tidak valid."
            };
        }

        bundle ??= new InventoryOpeningBalanceBundle();
        if (bundle.Rows.Count == 0)
        {
            return new InventoryOpeningBalanceExecutionResult
            {
                IsSuccess = false,
                IsValidationOnly = validateOnly,
                Message = "Tidak ada data saldo awal untuk diproses."
            };
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var hasAdminRole = await HasAnyRoleAsync(
                connection,
                transaction,
                actor,
                InventoryOpeningBalanceAdminRoles,
                cancellationToken);
            if (!hasAdminRole)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new InventoryOpeningBalanceExecutionResult
                {
                    IsSuccess = false,
                    IsValidationOnly = validateOnly,
                    Message = "Hanya role SUPER_ADMIN yang diizinkan untuk import saldo awal inventory."
                };
            }

            string? companyCode = null;
            await using (var companyCommand = new NpgsqlCommand(@"
SELECT code
FROM org_companies
WHERE id = @id
  AND is_active = TRUE;", connection, transaction))
            {
                companyCommand.Parameters.AddWithValue("id", companyId);
                var scalar = await companyCommand.ExecuteScalarAsync(cancellationToken);
                if (scalar is null || scalar is DBNull)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new InventoryOpeningBalanceExecutionResult
                    {
                        IsSuccess = false,
                        IsValidationOnly = validateOnly,
                        Message = "Perusahaan tidak ditemukan atau tidak aktif."
                    };
                }

                companyCode = Convert.ToString(scalar, CultureInfo.InvariantCulture)?.Trim().ToUpperInvariant();
            }

            if (string.IsNullOrWhiteSpace(companyCode))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new InventoryOpeningBalanceExecutionResult
                {
                    IsSuccess = false,
                    IsValidationOnly = validateOnly,
                    Message = "Kode perusahaan tidak valid."
                };
            }

            var errors = new List<InventoryImportError>();
            var normalizedRows = new List<OpeningBalanceImportRowBuffer>();
            var uniqueLocationItemPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in bundle.Rows)
            {
                var locationCode = (row.LocationCode ?? string.Empty).Trim().ToUpperInvariant();
                var itemCode = (row.ItemCode ?? string.Empty).Trim().ToUpperInvariant();
                var sourceCompanyCode = (row.CompanyCode ?? string.Empty).Trim().ToUpperInvariant();
                var cutoffDate = row.CutoffDate.Date;
                var qty = Math.Round(row.Qty, 4);
                var unitCost = Math.Round(row.UnitCost, 4);
                var referenceNo = (row.ReferenceNo ?? string.Empty).Trim().ToUpperInvariant();
                var notes = (row.Notes ?? string.Empty).Trim();
                var rowNumber = row.RowNumber <= 0 ? 1 : row.RowNumber;

                if (string.IsNullOrWhiteSpace(locationCode))
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = rowNumber,
                        Message = "LocationCode wajib diisi."
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(itemCode))
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = rowNumber,
                        Message = "ItemCode wajib diisi."
                    });
                    continue;
                }

                if (qty <= 0)
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = rowNumber,
                        Message = "Qty harus lebih besar dari 0."
                    });
                    continue;
                }

                if (unitCost < 0)
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = rowNumber,
                        Message = "UnitCost tidak boleh negatif."
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(referenceNo))
                {
                    referenceNo = $"OB-{cutoffDate:yyyyMMdd}";
                }

                if (referenceNo.Length > 200)
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = rowNumber,
                        Message = "ReferenceNo melebihi batas 200 karakter."
                    });
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(sourceCompanyCode) &&
                    !string.Equals(sourceCompanyCode, companyCode, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = rowNumber,
                        Message = $"CompanyCode '{sourceCompanyCode}' tidak sesuai dengan context company '{companyCode}'."
                    });
                    continue;
                }

                var duplicateKey = $"{locationCode}|{itemCode}";
                if (!uniqueLocationItemPairs.Add(duplicateKey))
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = rowNumber,
                        Message = $"Duplikat item pada lokasi yang sama: {locationCode}/{itemCode}."
                    });
                    continue;
                }

                normalizedRows.Add(new OpeningBalanceImportRowBuffer
                {
                    RowNumber = rowNumber,
                    LocationCode = locationCode,
                    ItemCode = itemCode,
                    Qty = qty,
                    UnitCost = unitCost,
                    CutoffDate = cutoffDate,
                    ReferenceNo = referenceNo,
                    Notes = notes
                });
            }

            if (errors.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new InventoryOpeningBalanceExecutionResult
                {
                    IsSuccess = false,
                    IsValidationOnly = validateOnly,
                    Message = BuildOpeningBalanceValidationMessage(errors),
                    Errors = errors
                };
            }

            var locationCodes = normalizedRows
                .Select(x => x.LocationCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var locationIdByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using (var locationCommand = new NpgsqlCommand(@"
SELECT id, upper(code)
FROM org_locations
WHERE company_id = @company_id
  AND is_active = TRUE
  AND upper(code) = ANY(@location_codes);", connection, transaction))
            {
                locationCommand.Parameters.AddWithValue("company_id", companyId);
                locationCommand.Parameters.AddWithValue("location_codes", locationCodes);
                await using var reader = await locationCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    locationIdByCode[reader.GetString(1)] = reader.GetInt64(0);
                }
            }

            var itemCodes = normalizedRows
                .Select(x => x.ItemCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var itemIdByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using (var itemCommand = new NpgsqlCommand(@"
SELECT id, upper(item_code)
FROM inv_items
WHERE is_active = TRUE
  AND upper(item_code) = ANY(@item_codes);", connection, transaction))
            {
                itemCommand.Parameters.AddWithValue("item_codes", itemCodes);
                await using var reader = await itemCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    itemIdByCode[reader.GetString(1)] = reader.GetInt64(0);
                }
            }

            var resolvedRows = new List<OpeningBalanceImportResolvedRow>(normalizedRows.Count);
            foreach (var row in normalizedRows)
            {
                if (!locationIdByCode.TryGetValue(row.LocationCode, out var locationId))
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = row.RowNumber,
                        Message = $"LocationCode '{row.LocationCode}' tidak ditemukan atau nonaktif."
                    });
                    continue;
                }

                if (!itemIdByCode.TryGetValue(row.ItemCode, out var itemId))
                {
                    errors.Add(new InventoryImportError
                    {
                        SheetName = "OpeningBalance",
                        RowNumber = row.RowNumber,
                        Message = $"ItemCode '{row.ItemCode}' tidak ditemukan atau nonaktif."
                    });
                    continue;
                }

                resolvedRows.Add(new OpeningBalanceImportResolvedRow
                {
                    RowNumber = row.RowNumber,
                    LocationId = locationId,
                    ItemId = itemId,
                    ItemCode = row.ItemCode,
                    Qty = row.Qty,
                    UnitCost = row.UnitCost,
                    CutoffDate = row.CutoffDate,
                    ReferenceNo = row.ReferenceNo,
                    Notes = row.Notes
                });
            }

            if (errors.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new InventoryOpeningBalanceExecutionResult
                {
                    IsSuccess = false,
                    IsValidationOnly = validateOnly,
                    Message = BuildOpeningBalanceValidationMessage(errors),
                    Errors = errors
                };
            }

            var totalQty = resolvedRows.Sum(x => x.Qty);
            var totalValue = resolvedRows.Sum(x => x.Qty * x.UnitCost);
            if (validateOnly)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new InventoryOpeningBalanceExecutionResult
                {
                    IsSuccess = true,
                    IsValidationOnly = true,
                    Message = $"Validasi saldo awal berhasil. Baris valid: {resolvedRows.Count}, total qty: {totalQty:N2}, total nilai: {totalValue:N2}.",
                    ValidRowCount = resolvedRows.Count,
                    TotalQty = totalQty,
                    TotalValue = totalValue
                };
            }

            var descriptionPrefix = "OPENING_BALANCE_IMPORT";
            var groupedRows = resolvedRows
                .GroupBy(x => new { x.LocationId, x.CutoffDate, x.ReferenceNo })
                .OrderBy(x => x.Key.CutoffDate)
                .ThenBy(x => x.Key.LocationId)
                .ThenBy(x => x.Key.ReferenceNo, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var replacedTransactionCount = 0;
            foreach (var group in groupedRows)
            {
                var existingHeaders = new List<(long Id, long LocationId, long? WarehouseId, string TransactionNo)>();
                await using (var existingHeaderCommand = new NpgsqlCommand(@"
SELECT id, location_id, warehouse_id, transaction_no
FROM inv_stock_transactions
WHERE company_id = @company_id
  AND location_id = @location_id
  AND transaction_type = 'STOCK_IN'
  AND status = 'POSTED'
  AND transaction_date = @transaction_date
  AND upper(reference_no) = @reference_no
  AND description LIKE @description_like
FOR UPDATE;", connection, transaction))
                {
                    existingHeaderCommand.Parameters.AddWithValue("company_id", companyId);
                    existingHeaderCommand.Parameters.AddWithValue("location_id", group.Key.LocationId);
                    existingHeaderCommand.Parameters.AddWithValue("transaction_date", group.Key.CutoffDate);
                    existingHeaderCommand.Parameters.AddWithValue("reference_no", group.Key.ReferenceNo);
                    existingHeaderCommand.Parameters.AddWithValue("description_like", $"{descriptionPrefix}%");
                    await using var reader = await existingHeaderCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        existingHeaders.Add((
                            reader.GetInt64(0),
                            reader.GetInt64(1),
                            reader.IsDBNull(2) ? null : reader.GetInt64(2),
                            reader.GetString(3)));
                    }
                }

                if (existingHeaders.Count == 0)
                {
                    continue;
                }

                if (!replaceExistingBatch)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new InventoryOpeningBalanceExecutionResult
                    {
                        IsSuccess = false,
                        IsValidationOnly = false,
                        Message = $"Batch saldo awal sudah ada untuk reference '{group.Key.ReferenceNo}' pada tanggal {group.Key.CutoffDate:yyyy-MM-dd}. Aktifkan mode replace untuk rerun."
                    };
                }

                foreach (var existingHeader in existingHeaders)
                {
                    var existingLines = new List<(long ItemId, decimal Qty, string ItemCode)>();
                    await using (var lineCommand = new NpgsqlCommand(@"
SELECT l.item_id, l.qty, i.item_code
FROM inv_stock_transaction_lines l
JOIN inv_items i ON i.id = l.item_id
WHERE l.transaction_id = @transaction_id
ORDER BY l.line_no;", connection, transaction))
                    {
                        lineCommand.Parameters.AddWithValue("transaction_id", existingHeader.Id);
                        await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            existingLines.Add((reader.GetInt64(0), reader.GetDecimal(1), reader.GetString(2)));
                        }
                    }

                    foreach (var line in existingLines)
                    {
                        var reduced = await ReduceStockQtyAsync(
                            connection,
                            transaction,
                            companyId,
                            existingHeader.LocationId,
                            line.ItemId,
                            existingHeader.WarehouseId,
                            line.Qty,
                            cancellationToken);
                        if (!reduced)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                            return new InventoryOpeningBalanceExecutionResult
                            {
                                IsSuccess = false,
                                IsValidationOnly = false,
                                Message = $"Gagal replace batch {existingHeader.TransactionNo}. Stok item {line.ItemCode} sudah terpakai transaksi lain."
                            };
                        }
                    }

                    await using (var disableCommand = new NpgsqlCommand(@"
UPDATE inv_stock_transactions
SET status = 'DRAFT',
    is_active = FALSE,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
                    {
                        disableCommand.Parameters.AddWithValue("id", existingHeader.Id);
                        disableCommand.Parameters.AddWithValue("updated_by", actor);
                        await disableCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await InsertAuditLogAsync(
                        connection,
                        transaction,
                        "INV_STOCK_TX",
                        existingHeader.Id,
                        "VOID_OB_IMPORT",
                        actor,
                        $"company={companyId};location={existingHeader.LocationId};transaction_no={existingHeader.TransactionNo}",
                        cancellationToken);

                    replacedTransactionCount++;
                }
            }

            var importedTransactionCount = 0;
            var importedLineCount = 0;
            foreach (var group in groupedRows)
            {
                var transactionDate = group.Key.CutoffDate;
                var transactionNo = await GenerateStockTransactionNoAsync(
                    connection,
                    transaction,
                    companyId,
                    "STOCK_IN",
                    transactionDate,
                    cancellationToken);
                var description = $"{descriptionPrefix};cutoff={transactionDate:yyyy-MM-dd};reference={group.Key.ReferenceNo}";

                long transactionId;
                await using (var insertHeaderCommand = new NpgsqlCommand(@"
INSERT INTO inv_stock_transactions (
    company_id,
    location_id,
    transaction_no,
    transaction_type,
    transaction_date,
    warehouse_id,
    destination_warehouse_id,
    reference_no,
    description,
    status,
    is_active,
    created_by,
    created_at,
    updated_by,
    updated_at)
VALUES (
    @company_id,
    @location_id,
    @transaction_no,
    'STOCK_IN',
    @transaction_date,
    NULL,
    NULL,
    @reference_no,
    @description,
    'POSTED',
    TRUE,
    @actor,
    NOW(),
    @actor,
    NOW())
RETURNING id;", connection, transaction))
                {
                    insertHeaderCommand.Parameters.AddWithValue("company_id", companyId);
                    insertHeaderCommand.Parameters.AddWithValue("location_id", group.Key.LocationId);
                    insertHeaderCommand.Parameters.AddWithValue("transaction_no", transactionNo);
                    insertHeaderCommand.Parameters.AddWithValue("transaction_date", transactionDate);
                    insertHeaderCommand.Parameters.AddWithValue("reference_no", group.Key.ReferenceNo);
                    insertHeaderCommand.Parameters.AddWithValue("description", description);
                    insertHeaderCommand.Parameters.AddWithValue("actor", actor);
                    transactionId = Convert.ToInt64(await insertHeaderCommand.ExecuteScalarAsync(cancellationToken));
                }

                var lineNo = 1;
                foreach (var line in group.OrderBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase))
                {
                    await using (var insertLineCommand = new NpgsqlCommand(@"
INSERT INTO inv_stock_transaction_lines (
    transaction_id,
    line_no,
    item_id,
    qty,
    unit_cost,
    notes,
    created_at)
VALUES (
    @transaction_id,
    @line_no,
    @item_id,
    @qty,
    @unit_cost,
    @notes,
    NOW());", connection, transaction))
                    {
                        insertLineCommand.Parameters.AddWithValue("transaction_id", transactionId);
                        insertLineCommand.Parameters.AddWithValue("line_no", lineNo++);
                        insertLineCommand.Parameters.AddWithValue("item_id", line.ItemId);
                        insertLineCommand.Parameters.AddWithValue("qty", line.Qty);
                        insertLineCommand.Parameters.AddWithValue("unit_cost", line.UnitCost);
                        insertLineCommand.Parameters.AddWithValue("notes", line.Notes);
                        await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await AddStockQtyAsync(
                        connection,
                        transaction,
                        companyId,
                        group.Key.LocationId,
                        line.ItemId,
                        null,
                        line.Qty,
                        cancellationToken);

                    importedLineCount++;
                }

                importedTransactionCount++;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_IMPORT",
                companyId,
                "OPENING_BALANCE_IMPORT",
                actor,
                $"company={companyId};rows={resolvedRows.Count};transactions={importedTransactionCount};replaced={replacedTransactionCount};total_qty={totalQty.ToString(CultureInfo.InvariantCulture)};total_value={totalValue.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var replaceInfo = replacedTransactionCount > 0
                ? $" Batch lama direplace: {replacedTransactionCount}."
                : string.Empty;
            return new InventoryOpeningBalanceExecutionResult
            {
                IsSuccess = true,
                IsValidationOnly = false,
                Message = $"Import saldo awal berhasil. Transaksi: {importedTransactionCount}, baris: {importedLineCount}, total qty: {totalQty:N2}, total nilai: {totalValue:N2}.{replaceInfo}",
                ValidRowCount = resolvedRows.Count,
                TransactionCount = importedTransactionCount,
                ImportedLineCount = importedLineCount,
                TotalQty = totalQty,
                TotalValue = totalValue
            };
        }
        catch (Exception ex)
        {
            return new InventoryOpeningBalanceExecutionResult
            {
                IsSuccess = false,
                IsValidationOnly = validateOnly,
                Message = $"Import saldo awal gagal: {ex.Message}"
            };
        }
    }

    public async Task<InventoryItemSearchResult> SearchInventoryItemsAsync(
        long companyId,
        InventoryItemSearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var keyword = filter.Keyword?.Trim() ?? string.Empty;
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var requestedPage = Math.Max(1, filter.Page);

        if (companyId <= 0)
        {
            return new InventoryItemSearchResult
            {
                Page = 1,
                PageSize = pageSize,
                TotalCount = 0,
                Items = new List<ManagedInventoryItem>()
            };
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var keywordPattern = string.IsNullOrWhiteSpace(keyword)
            ? string.Empty
            : $"%{keyword}%";

        var totalCount = 0;
        await using (var countCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM inv_items i
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE (
        @keyword = ''
        OR i.item_code ILIKE @keyword_pattern
        OR i.item_name ILIKE @keyword_pattern
        OR COALESCE(c.category_name, '') ILIKE @keyword_pattern
      );", connection))
        {
            countCommand.Parameters.AddWithValue("keyword", keyword);
            countCommand.Parameters.AddWithValue("keyword_pattern", keywordPattern);
            totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        if (totalCount <= 0)
        {
            return new InventoryItemSearchResult
            {
                Page = 1,
                PageSize = pageSize,
                TotalCount = 0,
                Items = new List<ManagedInventoryItem>()
            };
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var page = Math.Clamp(requestedPage, 1, totalPages);
        var offset = (page - 1) * pageSize;

        var items = new List<ManagedInventoryItem>();
        await using (var queryCommand = new NpgsqlCommand(@"
SELECT i.id,
       i.category_id,
       COALESCE(c.category_name, '') AS category_name,
       i.item_code,
       i.item_name,
       i.uom,
       i.category,
       i.is_active
FROM inv_items i
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE (
        @keyword = ''
        OR i.item_code ILIKE @keyword_pattern
        OR i.item_name ILIKE @keyword_pattern
        OR COALESCE(c.category_name, '') ILIKE @keyword_pattern
      )
ORDER BY i.item_code, i.id
LIMIT @limit
OFFSET @offset;", connection))
        {
            queryCommand.Parameters.AddWithValue("keyword", keyword);
            queryCommand.Parameters.AddWithValue("keyword_pattern", keywordPattern);
            queryCommand.Parameters.AddWithValue("limit", pageSize);
            queryCommand.Parameters.AddWithValue("offset", offset);

            await using var reader = await queryCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new ManagedInventoryItem
                {
                    Id = reader.GetInt64(0),
                    CompanyId = companyId,
                    CategoryId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    CategoryName = reader.GetString(2),
                    Code = reader.GetString(3),
                    Name = reader.GetString(4),
                    Uom = reader.GetString(5),
                    Category = reader.GetString(6),
                    IsActive = reader.GetBoolean(7)
                });
            }
        }

        return new InventoryItemSearchResult
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Items = items
        };
    }

    public async Task<AccessOperationResult> SaveInventoryUnitAsync(
        long companyId,
        ManagedInventoryUnit unit,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan tidak valid.");
        }

        if (string.IsNullOrWhiteSpace(unit.Code) || string.IsNullOrWhiteSpace(unit.Name))
        {
            return new AccessOperationResult(false, "Kode dan nama satuan wajib diisi.");
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
                InventoryModuleCode,
                InventorySubmoduleUnit,
                ResolveWriteAction(unit.Id),
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola satuan inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var normalizedCode = unit.Code.Trim().ToUpperInvariant();
            var normalizedName = unit.Name.Trim();

            long unitId;
            if (unit.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO inv_units (company_id, unit_code, unit_name, is_active, created_by, created_at, updated_at)
VALUES (@company_id, @unit_code, @unit_name, @is_active, @created_by, NOW(), NOW())
RETURNING id;", connection, transaction);
                insertCommand.Parameters.AddWithValue("company_id", companyId);
                insertCommand.Parameters.AddWithValue("unit_code", normalizedCode);
                insertCommand.Parameters.AddWithValue("unit_name", normalizedName);
                insertCommand.Parameters.AddWithValue("is_active", unit.IsActive);
                insertCommand.Parameters.AddWithValue("created_by", actor);
                unitId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE inv_units
SET unit_code = @unit_code,
    unit_name = @unit_name,
    is_active = @is_active,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
                updateCommand.Parameters.AddWithValue("id", unit.Id);
                updateCommand.Parameters.AddWithValue("company_id", companyId);
                updateCommand.Parameters.AddWithValue("unit_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("unit_name", normalizedName);
                updateCommand.Parameters.AddWithValue("is_active", unit.IsActive);
                updateCommand.Parameters.AddWithValue("updated_by", actor);
                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Data satuan tidak ditemukan.");
                }

                unitId = unit.Id;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_UNIT",
                unitId,
                unit.Id <= 0 ? "CREATE" : "UPDATE",
                actor,
                $"company={companyId};code={normalizedCode};name={normalizedName};active={unit.IsActive}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, $"Satuan '{normalizedCode}' berhasil disimpan.", unitId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, $"Kode satuan '{unit.Code.Trim().ToUpperInvariant()}' sudah digunakan.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan satuan: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteInventoryUnitAsync(
        long companyId,
        long unitId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0 || unitId <= 0)
        {
            return new AccessOperationResult(false, "Parameter tidak valid.");
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
                InventoryModuleCode,
                InventorySubmoduleUnit,
                PermissionActionDelete,
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola satuan inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using var command = new NpgsqlCommand(@"
UPDATE inv_units
SET is_active = FALSE,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
            command.Parameters.AddWithValue("id", unitId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("updated_by", actor);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected <= 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data satuan tidak ditemukan.");
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_UNIT",
                unitId,
                "DEACTIVATE",
                actor,
                $"company={companyId};unit_id={unitId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Satuan berhasil dinonaktifkan.", unitId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menonaktifkan satuan: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SaveWarehouseAsync(
        long companyId,
        ManagedWarehouse warehouse,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan tidak valid.");
        }

        if (string.IsNullOrWhiteSpace(warehouse.Code) || string.IsNullOrWhiteSpace(warehouse.Name))
        {
            return new AccessOperationResult(false, "Kode dan nama gudang wajib diisi.");
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
                InventoryModuleCode,
                InventorySubmoduleWarehouse,
                ResolveWriteAction(warehouse.Id),
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola gudang inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var normalizedCode = warehouse.Code.Trim().ToUpperInvariant();
            var normalizedName = warehouse.Name.Trim();
            object locationIdParam = warehouse.LocationId.HasValue && warehouse.LocationId.Value > 0
                ? warehouse.LocationId.Value
                : DBNull.Value;

            long warehouseId;
            if (warehouse.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO inv_warehouses (company_id, warehouse_code, warehouse_name, location_id, is_active, created_by, created_at, updated_at)
VALUES (@company_id, @warehouse_code, @warehouse_name, @location_id, @is_active, @created_by, NOW(), NOW())
RETURNING id;", connection, transaction);
                insertCommand.Parameters.AddWithValue("company_id", companyId);
                insertCommand.Parameters.AddWithValue("warehouse_code", normalizedCode);
                insertCommand.Parameters.AddWithValue("warehouse_name", normalizedName);
                insertCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationIdParam });
                insertCommand.Parameters.AddWithValue("is_active", warehouse.IsActive);
                insertCommand.Parameters.AddWithValue("created_by", actor);

                warehouseId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE inv_warehouses
SET warehouse_code = @warehouse_code,
    warehouse_name = @warehouse_name,
    location_id = @location_id,
    is_active = @is_active,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
                updateCommand.Parameters.AddWithValue("id", warehouse.Id);
                updateCommand.Parameters.AddWithValue("company_id", companyId);
                updateCommand.Parameters.AddWithValue("warehouse_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("warehouse_name", normalizedName);
                updateCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationIdParam });
                updateCommand.Parameters.AddWithValue("is_active", warehouse.IsActive);
                updateCommand.Parameters.AddWithValue("updated_by", actor);

                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Data gudang tidak ditemukan.");
                }

                warehouseId = warehouse.Id;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_WAREHOUSE",
                warehouseId,
                warehouse.Id <= 0 ? "CREATE" : "UPDATE",
                actor,
                $"company={companyId};code={normalizedCode};name={normalizedName};location_id={warehouse.LocationId};active={warehouse.IsActive}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, $"Gudang '{normalizedCode}' berhasil disimpan.", warehouseId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, $"Kode gudang '{warehouse.Code.Trim().ToUpperInvariant()}' sudah digunakan.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan gudang: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteWarehouseAsync(
        long companyId,
        long warehouseId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0 || warehouseId <= 0)
        {
            return new AccessOperationResult(false, "Parameter tidak valid.");
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
                InventoryModuleCode,
                InventorySubmoduleWarehouse,
                PermissionActionDelete,
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola gudang inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using var command = new NpgsqlCommand(@"
UPDATE inv_warehouses
SET is_active = FALSE,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
            command.Parameters.AddWithValue("id", warehouseId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("updated_by", actor);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected <= 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data gudang tidak ditemukan.");
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_WAREHOUSE",
                warehouseId,
                "DEACTIVATE",
                actor,
                $"company={companyId};warehouse_id={warehouseId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Gudang berhasil dinonaktifkan.", warehouseId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menonaktifkan gudang: {ex.Message}");
        }
    }
}


