using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Npgsql;
using Accounting.Services;

internal static partial class Program
{
    private static async Task TestInventoryCategoryCrudReactivationAndValidationAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var baseCode = $"ITCAT{Math.Abs(stamp % 1000000):000000}";
        var secondCode = $"{baseCode}B";
        var missingId = long.MaxValue - 123;
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);

        long? primaryCategoryId = null;
        long? secondaryCategoryId = null;

        try
        {
            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.ToString(), "admin");

            var createPrimaryResult = await service.SaveInventoryCategoryAsync(
                companyId,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = companyId,
                    Code = baseCode,
                    Name = $"Integration Category {stamp}",
                    AccountCode = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(
                createPrimaryResult.IsSuccess && createPrimaryResult.EntityId.HasValue,
                $"Create primary category failed: {createPrimaryResult.Message}");
            primaryCategoryId = createPrimaryResult.EntityId!.Value;

            var updatePrimaryResult = await service.SaveInventoryCategoryAsync(
                companyId,
                new ManagedInventoryCategory
                {
                    Id = primaryCategoryId.Value,
                    CompanyId = companyId,
                    Code = baseCode,
                    Name = $"Integration Category Updated {stamp}",
                    AccountCode = "HO.11000.001",
                    IsActive = true
                },
                "admin");
            Assert(updatePrimaryResult.IsSuccess, $"Update primary category failed: {updatePrimaryResult.Message}");

            var deactivatePrimaryResult = await service.SoftDeleteInventoryCategoryAsync(companyId, primaryCategoryId.Value, "admin");
            Assert(deactivatePrimaryResult.IsSuccess, $"Deactivate primary category failed: {deactivatePrimaryResult.Message}");

            var afterDeactivate = await service.GetInventoryWorkspaceDataAsync(companyId, locationId);
            var deactivatedCategory = afterDeactivate.Categories.FirstOrDefault(x => x.Id == primaryCategoryId.Value);
            Assert(deactivatedCategory is not null, "Deactivated category should still be queryable.");
            Assert(!deactivatedCategory!.IsActive, "Category should be inactive after soft delete.");

            var reactivateResult = await service.SaveInventoryCategoryAsync(
                companyId,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = companyId,
                    Code = baseCode,
                    Name = $"Integration Category Reactivated {stamp}",
                    AccountCode = "HO.11000.002",
                    IsActive = true
                },
                "admin");
            Assert(reactivateResult.IsSuccess, $"Reactivate category failed: {reactivateResult.Message}");
            Assert(
                reactivateResult.EntityId.HasValue && reactivateResult.EntityId.Value == primaryCategoryId.Value,
                "Reactivation should reuse the same category id.");

            var afterReactivate = await service.GetInventoryWorkspaceDataAsync(companyId, locationId);
            var reactivatedCategory = afterReactivate.Categories.FirstOrDefault(x => x.Id == primaryCategoryId.Value);
            Assert(reactivatedCategory is not null, "Reactivated category should be queryable.");
            Assert(reactivatedCategory!.IsActive, "Category should be active after reactivation.");
            Assert(
                string.Equals(reactivatedCategory.Name, $"Integration Category Reactivated {stamp}", StringComparison.Ordinal),
                "Reactivated category should persist the latest name.");
            Assert(
                string.Equals(reactivatedCategory.AccountCode, "HO.11000.002", StringComparison.OrdinalIgnoreCase),
                "Reactivated category should persist the latest account code.");

            var createSecondaryResult = await service.SaveInventoryCategoryAsync(
                companyId,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = companyId,
                    Code = secondCode,
                    Name = $"Integration Category Secondary {stamp}",
                    AccountCode = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(
                createSecondaryResult.IsSuccess && createSecondaryResult.EntityId.HasValue,
                $"Create secondary category failed: {createSecondaryResult.Message}");
            secondaryCategoryId = createSecondaryResult.EntityId!.Value;

            var duplicateActiveResult = await service.SaveInventoryCategoryAsync(
                companyId,
                new ManagedInventoryCategory
                {
                    Id = secondaryCategoryId.Value,
                    CompanyId = companyId,
                    Code = baseCode,
                    Name = "Should fail duplicate active code",
                    AccountCode = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(!duplicateActiveResult.IsSuccess, "Updating to an already active code should fail.");
            Assert(
                duplicateActiveResult.Message.Contains("sudah digunakan", StringComparison.OrdinalIgnoreCase),
                "Duplicate active code should return duplicate message.");

            var invalidUpdateResult = await service.SaveInventoryCategoryAsync(
                companyId,
                new ManagedInventoryCategory
                {
                    Id = missingId,
                    CompanyId = companyId,
                    Code = $"{baseCode}X",
                    Name = "Missing category update",
                    AccountCode = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(!invalidUpdateResult.IsSuccess, "Updating missing category should fail.");
            Assert(
                invalidUpdateResult.Message.Contains("tidak ditemukan", StringComparison.OrdinalIgnoreCase),
                "Missing category update should return not-found message.");

            var invalidDeactivateResult = await service.SoftDeleteInventoryCategoryAsync(companyId, missingId, "admin");
            Assert(!invalidDeactivateResult.IsSuccess, "Deactivating missing category should fail.");
            Assert(
                invalidDeactivateResult.Message.Contains("tidak ditemukan", StringComparison.OrdinalIgnoreCase),
                $"Missing category deactivate should return not-found message. Actual: {invalidDeactivateResult.Message}");

            await using var connection = await OpenConnectionAsync();
            await using var auditCommand = new NpgsqlCommand(
                @"SELECT action, COUNT(1)
FROM sec_audit_logs
WHERE entity_type = 'INV_CATEGORY'
  AND entity_id = @entity_id
GROUP BY action;",
                connection);
            auditCommand.Parameters.AddWithValue("entity_id", primaryCategoryId.Value);
            await using var reader = await auditCommand.ExecuteReaderAsync();

            var actions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
            {
                actions[reader.GetString(0)] = reader.GetInt64(1);
            }

            Assert(actions.ContainsKey("CREATE"), "Category audit should contain CREATE action.");
            Assert(actions.ContainsKey("UPDATE"), "Category audit should contain UPDATE action.");
            Assert(actions.ContainsKey("DEACTIVATE"), "Category audit should contain DEACTIVATE action.");
            Assert(actions.ContainsKey("REACTIVATE"), "Category audit should contain REACTIVATE action.");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (secondaryCategoryId.HasValue)
            {
                await using (var deleteSecondaryAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_CATEGORY' AND entity_id = @entity_id;",
                    connection))
                {
                    deleteSecondaryAudit.Parameters.AddWithValue("entity_id", secondaryCategoryId.Value);
                    await deleteSecondaryAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteSecondary = new NpgsqlCommand(
                    "DELETE FROM inv_categories WHERE id = @id;",
                    connection))
                {
                    deleteSecondary.Parameters.AddWithValue("id", secondaryCategoryId.Value);
                    await deleteSecondary.ExecuteNonQueryAsync();
                }
            }

            if (primaryCategoryId.HasValue)
            {
                await using (var deletePrimaryAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_CATEGORY' AND entity_id = @entity_id;",
                    connection))
                {
                    deletePrimaryAudit.Parameters.AddWithValue("entity_id", primaryCategoryId.Value);
                    await deletePrimaryAudit.ExecuteNonQueryAsync();
                }

                await using (var deletePrimary = new NpgsqlCommand(
                    "DELETE FROM inv_categories WHERE id = @id;",
                    connection))
                {
                    deletePrimary.Parameters.AddWithValue("id", primaryCategoryId.Value);
                    await deletePrimary.ExecuteNonQueryAsync();
                }
            }

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
        }
    }

    private static async Task TestInventoryCategoryRejectsUnauthorizedActorAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var roleCode = $"ITEST_NO_INV_CAT_{stamp}";
        var username = $"itest_noinvcat_{stamp}";
        var categoryCode = $"ITDENY{Math.Abs(stamp % 1000000):000000}";
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);

        long? roleId = null;
        long? userId = null;

        try
        {
            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.ToString(), "admin");

            var roleSaveResult = await service.SaveRoleAsync(
                new ManagedRole
                {
                    Id = 0,
                    Code = roleCode,
                    Name = "No Inventory Category Permission Role",
                    IsSuperRole = false,
                    IsActive = true
                },
                Array.Empty<long>(),
                "admin");
            Assert(roleSaveResult.IsSuccess && roleSaveResult.EntityId.HasValue, "Failed to create restricted inventory role.");
            roleId = roleSaveResult.EntityId!.Value;

            var userSaveResult = await service.SaveUserAsync(
                new ManagedUser
                {
                    Id = 0,
                    Username = username,
                    FullName = "No Inventory Category User",
                    Email = $"{username}@local",
                    IsActive = true,
                    DefaultCompanyId = companyId,
                    DefaultLocationId = locationId
                },
                "Admin@123",
                [roleId.Value],
                [companyId],
                [locationId],
                "admin");
            Assert(userSaveResult.IsSuccess && userSaveResult.EntityId.HasValue, "Failed to create restricted inventory user.");
            userId = userSaveResult.EntityId!.Value;

            var saveResult = await service.SaveInventoryCategoryAsync(
                companyId,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = companyId,
                    Code = categoryCode,
                    Name = $"Unauthorized Inventory Category {stamp}",
                    AccountCode = string.Empty,
                    IsActive = true
                },
                username);

            Assert(!saveResult.IsSuccess, "Unauthorized actor should be rejected for inventory category save.");
            Assert(
                saveResult.Message.Contains("izin", StringComparison.OrdinalIgnoreCase),
                "Expected unauthorized inventory message containing izin.");
        }
        finally
        {
            if (userId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();
                await using (var deleteRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @id;", connection))
                {
                    deleteRoles.Parameters.AddWithValue("id", userId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand("DELETE FROM app_users WHERE id = @id;", connection))
                {
                    deleteUser.Parameters.AddWithValue("id", userId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }
            }

            if (roleId.HasValue)
            {
                await service.DeleteRoleAsync(roleId.Value, "admin");
            }

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
        }
    }

    private static async Task TestInventoryImportAllowsDedicatedApiPermissionAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        var managementData = await service.GetUserManagementDataAsync();

        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var importScope = managementData.AccessScopes.FirstOrDefault(scope =>
            string.Equals(scope.ModuleCode, "inventory", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(scope.SubmoduleCode, "api_inv", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(scope.ActionCode, "import_master_data", StringComparison.OrdinalIgnoreCase));
        Assert(importScope is not null, "Required inventory.api_inv.import_master_data scope was not found.");

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var roleCode = $"ITEST_INV_IMPORT_{stamp}";
        var username = $"itest_invimport_{stamp}";
        var categoryCode = $"ITIMPCT{Math.Abs(stamp % 1000000):000000}";
        var itemCode = $"ITIMPIT{Math.Abs(stamp % 1000000):000000}";
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);

        long? roleId = null;
        long? userId = null;

        try
        {
            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.ToString(), "admin");

            var roleSaveResult = await service.SaveRoleAsync(
                new ManagedRole
                {
                    Id = 0,
                    Code = roleCode,
                    Name = "Dedicated Inventory Import Role",
                    IsSuperRole = false,
                    IsActive = true
                },
                [importScope!.Id],
                "admin");
            Assert(roleSaveResult.IsSuccess && roleSaveResult.EntityId.HasValue, "Failed to create dedicated inventory import role.");
            roleId = roleSaveResult.EntityId!.Value;

            var userSaveResult = await service.SaveUserAsync(
                new ManagedUser
                {
                    Id = 0,
                    Username = username,
                    FullName = "Dedicated Inventory Import User",
                    Email = $"{username}@local",
                    IsActive = true,
                    DefaultCompanyId = companyId,
                    DefaultLocationId = locationId
                },
                "Admin@123",
                [roleId.Value],
                [companyId],
                [locationId],
                "admin");
            Assert(userSaveResult.IsSuccess && userSaveResult.EntityId.HasValue, "Failed to create dedicated inventory import user.");
            userId = userSaveResult.EntityId!.Value;

            var categorySaveResult = await service.SaveInventoryCategoryAsync(
                companyId,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = companyId,
                    Code = categoryCode,
                    Name = "Should fail direct category save",
                    AccountCode = string.Empty,
                    IsActive = true
                },
                username);
            Assert(!categorySaveResult.IsSuccess, "Dedicated import actor should not gain category update/create permission.");
            Assert(
                categorySaveResult.Message.Contains("izin", StringComparison.OrdinalIgnoreCase),
                $"Expected direct category save denial, got: {categorySaveResult.Message}");

            var importResult = await service.ImportInventoryMasterDataAsync(
                companyId,
                new InventoryImportBundle
                {
                    Categories =
                    [
                        new InventoryImportCategoryRow
                        {
                            RowNumber = 2,
                            Code = categoryCode,
                            Name = $"Imported Category {stamp}",
                            AccountCode = string.Empty,
                            IsActive = true
                        }
                    ],
                    Items =
                    [
                        new InventoryImportItemRow
                        {
                            RowNumber = 2,
                            Code = itemCode,
                            Name = $"Imported Item {stamp}",
                            Uom = "PCS",
                            CategoryCode = categoryCode,
                            IsActive = true
                        }
                    ]
                },
                username);

            Assert(importResult.IsSuccess, $"Dedicated import permission should allow import: {importResult.Message}");
            Assert(importResult.ImportedCategoryCount == 1, $"Expected 1 imported category, got {importResult.ImportedCategoryCount}.");
            Assert(importResult.ImportedItemCount == 1, $"Expected 1 imported item, got {importResult.ImportedItemCount}.");

            var workspace = await service.GetInventoryWorkspaceDataAsync(companyId, locationId);
            Assert(
                workspace.Categories.Any(x => string.Equals(x.Code, categoryCode, StringComparison.OrdinalIgnoreCase)),
                "Imported category should exist in workspace data.");
            Assert(
                workspace.Items.Any(x => string.Equals(x.Code, itemCode, StringComparison.OrdinalIgnoreCase)),
                "Imported item should exist in workspace data.");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();
            await CleanupInventoryArtifactsByCodesAsync(connection, companyId, [categoryCode], [itemCode]);

            await using (var deleteAudit = new NpgsqlCommand(
                @"DELETE FROM sec_audit_logs
WHERE details ILIKE @category_code
   OR details ILIKE @item_code;",
                connection))
            {
                deleteAudit.Parameters.AddWithValue("category_code", $"%{categoryCode}%");
                deleteAudit.Parameters.AddWithValue("item_code", $"%{itemCode}%");
                await deleteAudit.ExecuteNonQueryAsync();
            }

            if (userId.HasValue)
            {
                await using (var deleteRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @id;", connection))
                {
                    deleteRoles.Parameters.AddWithValue("id", userId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand("DELETE FROM app_users WHERE id = @id;", connection))
                {
                    deleteUser.Parameters.AddWithValue("id", userId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }
            }

            if (roleId.HasValue)
            {
                await service.DeleteRoleAsync(roleId.Value, "admin");
            }

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
        }
    }

    private static async Task TestInventoryImportWritesAggregateAuditLogAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");

        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");

        var companyId = accessOptions.Companies[0].Id;
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var categoryCode = $"ITAUDC{Math.Abs(stamp % 1000000):000000}";
        var itemCode = $"ITAUDI{Math.Abs(stamp % 1000000):000000}";
        var auditActor = "admin";
        var expectedDetails = $"actor={auditActor};company={companyId};category_rows=1;item_rows=1;categories_imported=1;items_imported=1";
        var startedAt = DateTime.UtcNow.AddSeconds(-1);

        try
        {
            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.ToString(), "admin");

            var importResult = await service.ImportInventoryMasterDataAsync(
                companyId,
                new InventoryImportBundle
                {
                    Categories =
                    [
                        new InventoryImportCategoryRow
                        {
                            RowNumber = 2,
                            Code = categoryCode,
                            Name = $"Audit Category {stamp}",
                            AccountCode = string.Empty,
                            IsActive = true
                        }
                    ],
                    Items =
                    [
                        new InventoryImportItemRow
                        {
                            RowNumber = 2,
                            Code = itemCode,
                            Name = $"Audit Item {stamp}",
                            Uom = "PCS",
                            CategoryCode = categoryCode,
                            IsActive = true
                        }
                    ]
                },
                auditActor);

            Assert(importResult.IsSuccess, $"Inventory import should succeed for audit test: {importResult.Message}");

            await using var connection = await OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT actor_username, details
                FROM sec_audit_logs
                WHERE entity_type = 'INV_IMPORT'
                  AND entity_id = @company_id
                  AND action = 'MASTER_IMPORT'
                  AND created_at >= @started_at
                ORDER BY created_at DESC
                LIMIT 1;
                """,
                connection);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("started_at", startedAt);

            await using var reader = await command.ExecuteReaderAsync();
            Assert(await reader.ReadAsync(), "Inventory import should create an aggregate audit log row.");
            Assert(
                string.Equals(reader.GetString(0), auditActor, StringComparison.OrdinalIgnoreCase),
                $"Expected audit actor '{auditActor}', got '{reader.GetString(0)}'.");
            Assert(
                string.Equals(reader.GetString(1), expectedDetails, StringComparison.Ordinal),
                $"Unexpected aggregate import audit details. Expected '{expectedDetails}', got '{reader.GetString(1)}'.");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();
            await CleanupInventoryArtifactsByCodesAsync(connection, companyId, [categoryCode], [itemCode]);

            await using var deleteAudit = new NpgsqlCommand(
                """
                DELETE FROM sec_audit_logs
                WHERE entity_type = 'INV_IMPORT'
                  AND entity_id = @company_id
                  AND action = 'MASTER_IMPORT'
                  AND details = @details;
                """,
                connection);
            deleteAudit.Parameters.AddWithValue("company_id", companyId);
            deleteAudit.Parameters.AddWithValue("details", expectedDetails);
            await deleteAudit.ExecuteNonQueryAsync();

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
        }
    }

    private static async Task TestInventoryCostingRecalcCompanyAndLocationAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");

        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tempCompanyCode = $"ITCOST{Math.Abs(stamp % 1000000):000000}";
        var tempLocationCodeA = $"L1{Math.Abs(stamp % 10000):0000}";
        var tempLocationCodeB = $"L2{Math.Abs((stamp + 17) % 10000):0000}";
        var warehouseCodeA = $"WA{Math.Abs(stamp % 1000000):000000}";
        var warehouseCodeB = $"WB{Math.Abs((stamp + 29) % 1000000):000000}";
        var categoryCode = $"ITCCT{Math.Abs(stamp % 1000000):000000}";
        var itemCode = $"ITCIT{Math.Abs(stamp % 1000000):000000}";

        long? companyId = null;
        long? locationIdA = null;
        long? locationIdB = null;
        long? warehouseIdA = null;
        long? warehouseIdB = null;
        long? categoryId = null;
        long? itemId = null;
        long? stockInTxA = null;
        long? stockOutTxA = null;
        long? stockOutTxB = null;
        long? opnamePlusA = null;

        try
        {
            var createCompanyResult = await service.SaveCompanyAsync(
                new ManagedCompany
                {
                    Id = 0,
                    Code = tempCompanyCode,
                    Name = $"Integration Costing Company {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createCompanyResult.IsSuccess && createCompanyResult.EntityId.HasValue,
                $"Failed to create temporary company for costing test: {createCompanyResult.Message}");
            companyId = createCompanyResult.EntityId!.Value;

            var createLocationAResult = await service.SaveLocationAsync(
                new ManagedLocation
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = tempLocationCodeA,
                    Name = $"Integration Location A {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createLocationAResult.IsSuccess && createLocationAResult.EntityId.HasValue,
                $"Failed to create location A: {createLocationAResult.Message}");
            locationIdA = createLocationAResult.EntityId!.Value;

            var createLocationBResult = await service.SaveLocationAsync(
                new ManagedLocation
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = tempLocationCodeB,
                    Name = $"Integration Location B {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createLocationBResult.IsSuccess && createLocationBResult.EntityId.HasValue,
                $"Failed to create location B: {createLocationBResult.Message}");
            locationIdB = createLocationBResult.EntityId!.Value;

            warehouseIdA = await CreateWarehouseAsync(
                service,
                companyId.Value,
                warehouseCodeA,
                $"Warehouse A {stamp}",
                locationIdA.Value,
                "admin");
            warehouseIdB = await CreateWarehouseAsync(
                service,
                companyId.Value,
                warehouseCodeB,
                $"Warehouse B {stamp}",
                locationIdB.Value,
                "admin");

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.Value.ToString(), "admin");

            var accountPrefix = tempLocationCodeA.Length >= 2
                ? tempLocationCodeA[..2].ToUpperInvariant()
                : "IT";
            var inventoryParentCode = $"{accountPrefix}.11000.000";
            var inventoryAccountCode = $"{accountPrefix}.11000.001";
            var cogsParentCode = $"{accountPrefix}.51000.000";
            var cogsAccountCode = $"{accountPrefix}.51000.001";

            var saveInventoryParentResult = await service.SaveAccountAsync(
                companyId.Value,
                new ManagedAccount
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = inventoryParentCode,
                    Name = $"Inventory Asset Parent {stamp}",
                    AccountType = "ASSET",
                    IsActive = true
                },
                "admin");
            Assert(
                saveInventoryParentResult.IsSuccess && saveInventoryParentResult.EntityId.HasValue,
                $"Failed to create inventory parent account: {saveInventoryParentResult.Message}");

            var saveInventoryAccountResult = await service.SaveAccountAsync(
                companyId.Value,
                new ManagedAccount
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = inventoryAccountCode,
                    Name = $"Inventory Asset {stamp}",
                    ParentAccountId = saveInventoryParentResult.EntityId,
                    AccountType = "ASSET",
                    IsActive = true
                },
                "admin");
            Assert(saveInventoryAccountResult.IsSuccess, $"Failed to create inventory account: {saveInventoryAccountResult.Message}");

            var saveCogsParentResult = await service.SaveAccountAsync(
                companyId.Value,
                new ManagedAccount
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = cogsParentCode,
                    Name = $"Inventory COGS Parent {stamp}",
                    AccountType = "EXPENSE",
                    IsActive = true
                },
                "admin");
            Assert(
                saveCogsParentResult.IsSuccess && saveCogsParentResult.EntityId.HasValue,
                $"Failed to create COGS parent account: {saveCogsParentResult.Message}");

            var saveCogsAccountResult = await service.SaveAccountAsync(
                companyId.Value,
                new ManagedAccount
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = cogsAccountCode,
                    Name = $"Inventory COGS {stamp}",
                    ParentAccountId = saveCogsParentResult.EntityId,
                    AccountType = "EXPENSE",
                    IsActive = true
                },
                "admin");
            Assert(saveCogsAccountResult.IsSuccess, $"Failed to create COGS account: {saveCogsAccountResult.Message}");

            var saveCategoryResult = await service.SaveInventoryCategoryAsync(
                companyId.Value,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = categoryCode,
                    Name = $"Costing Category {stamp}",
                    AccountCode = inventoryAccountCode,
                    IsActive = true
                },
                "admin");
            Assert(
                saveCategoryResult.IsSuccess && saveCategoryResult.EntityId.HasValue,
                $"Failed to create costing category: {saveCategoryResult.Message}");
            categoryId = saveCategoryResult.EntityId!.Value;

            var saveItemResult = await service.SaveInventoryItemAsync(
                companyId.Value,
                new ManagedInventoryItem
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    CategoryId = categoryId.Value,
                    Code = itemCode,
                    Name = $"Costing Item {stamp}",
                    Uom = "PCS",
                    Category = categoryCode,
                    IsActive = true
                },
                "admin");
            Assert(
                saveItemResult.IsSuccess && saveItemResult.EntityId.HasValue,
                $"Failed to create costing item: {saveItemResult.Message}");
            itemId = saveItemResult.EntityId!.Value;

            var saveCompanyCostingResult = await service.SaveInventoryCostingSettingsAsync(
                companyId.Value,
                new InventoryCostingSettings
                {
                    CompanyId = companyId.Value,
                    ValuationMethod = "AVERAGE",
                    CogsAccountCode = cogsAccountCode
                },
                "admin");
            Assert(saveCompanyCostingResult.IsSuccess, $"Failed to save company costing settings: {saveCompanyCostingResult.Message}");

            var saveLocationCostingResult = await service.SaveInventoryLocationCostingSettingsAsync(
                companyId.Value,
                new InventoryLocationCostingSettings
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdB.Value,
                    UseCompanyDefault = false,
                    ValuationMethod = "FIFO",
                    CogsAccountCode = cogsAccountCode
                },
                "admin");
            Assert(saveLocationCostingResult.IsSuccess, $"Failed to save location costing override (FIFO): {saveLocationCostingResult.Message}");

            // Keep the 3-day test window inside a single accounting month so period-based
            // journal pulls behave consistently even when the suite runs on the 1st/2nd.
            var today = DateTime.Today;
            var baseDate = today.Day >= 3
                ? today.AddDays(-2)
                : new DateTime(today.AddMonths(-1).Year, today.AddMonths(-1).Month, 15);

            stockInTxA = await CreateAndPostStockTransactionAsync(
                service,
                new ManagedStockTransaction
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdA.Value,
                    TransactionType = "STOCK_IN",
                    TransactionDate = baseDate,
                    ReferenceNo = "ITCOST-IN-A-1",
                    Description = "Costing test IN A #1"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        ItemId = itemId.Value,
                        Qty = 6,
                        UnitCost = 10,
                        WarehouseId = warehouseIdA.Value
                    },
                    new ManagedStockTransactionLine
                    {
                        ItemId = itemId.Value,
                        Qty = 4,
                        UnitCost = 10,
                        WarehouseId = warehouseIdA.Value
                    }
                },
                "admin");

            _ = await CreateAndPostStockTransactionAsync(
                service,
                new ManagedStockTransaction
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdA.Value,
                    TransactionType = "STOCK_IN",
                    TransactionDate = baseDate.AddDays(1),
                    ReferenceNo = "ITCOST-IN-A-2",
                    Description = "Costing test IN A #2"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        ItemId = itemId.Value,
                        Qty = 10,
                        UnitCost = 20,
                        WarehouseId = warehouseIdA.Value
                    }
                },
                "admin");

            stockOutTxA = await CreateAndPostStockTransactionAsync(
                service,
                new ManagedStockTransaction
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdA.Value,
                    TransactionType = "STOCK_OUT",
                    TransactionDate = baseDate.AddDays(2),
                    ReferenceNo = "ITCOST-OUT-A",
                    Description = "Costing test OUT A"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        ItemId = itemId.Value,
                        Qty = 6,
                        UnitCost = 0,
                        WarehouseId = warehouseIdA.Value,
                        ExpenseAccountCode = cogsAccountCode
                    },
                    new ManagedStockTransactionLine
                    {
                        ItemId = itemId.Value,
                        Qty = 4,
                        UnitCost = 0,
                        WarehouseId = warehouseIdA.Value,
                        ExpenseAccountCode = cogsAccountCode
                    }
                },
                "admin");

            opnamePlusA = await CreateAndPostStockOpnameAsync(
                service,
                new ManagedStockOpname
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdA.Value,
                    OpnameDate = baseDate.AddDays(2),
                    Description = "Costing test OPNAME PLUS A"
                },
                new[]
                {
                    new ManagedStockOpnameLine
                    {
                        ItemId = itemId.Value,
                        SystemQty = 10,
                        ActualQty = 12
                    }
                },
                "admin");

            _ = await CreateAndPostStockTransactionAsync(
                service,
                new ManagedStockTransaction
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdB.Value,
                    TransactionType = "STOCK_IN",
                    TransactionDate = baseDate,
                    ReferenceNo = "ITCOST-IN-B-1",
                    Description = "Costing test IN B #1"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        ItemId = itemId.Value,
                        Qty = 10,
                        UnitCost = 10,
                        WarehouseId = warehouseIdB.Value
                    }
                },
                "admin");

            _ = await CreateAndPostStockTransactionAsync(
                service,
                new ManagedStockTransaction
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdB.Value,
                    TransactionType = "STOCK_IN",
                    TransactionDate = baseDate.AddDays(1),
                    ReferenceNo = "ITCOST-IN-B-2",
                    Description = "Costing test IN B #2"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        ItemId = itemId.Value,
                        Qty = 10,
                        UnitCost = 20,
                        WarehouseId = warehouseIdB.Value
                    }
                },
                "admin");

            stockOutTxB = await CreateAndPostStockTransactionAsync(
                service,
                new ManagedStockTransaction
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdB.Value,
                    TransactionType = "STOCK_OUT",
                    TransactionDate = baseDate.AddDays(2),
                    ReferenceNo = "ITCOST-OUT-B",
                    Description = "Costing test OUT B"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        ItemId = itemId.Value,
                        Qty = 10,
                        UnitCost = 0,
                        WarehouseId = warehouseIdB.Value,
                        ExpenseAccountCode = cogsAccountCode
                    }
                },
                "admin");

            var recalcCompanyResult = await service.RecalculateInventoryCostingAsync(companyId.Value, null, "admin");
            Assert(recalcCompanyResult.IsSuccess, $"Company-level recalc failed: {recalcCompanyResult.Message}");

            var unitCostAAfterCompany = await GetPostedStockTransactionLineUnitCostAsync(stockOutTxA.Value);
            var unitCostBAfterCompany = await GetPostedStockTransactionLineUnitCostAsync(stockOutTxB.Value);
            Assert(Math.Abs(unitCostAAfterCompany - 15m) < 0.0001m, $"Expected location A stock-out cost 15.0000 (AVERAGE), got {unitCostAAfterCompany:N4}.");
            Assert(Math.Abs(unitCostBAfterCompany - 10m) < 0.0001m, $"Expected location B stock-out cost 10.0000 (FIFO), got {unitCostBAfterCompany:N4}.");

            var saveLocationLifoResult = await service.SaveInventoryLocationCostingSettingsAsync(
                companyId.Value,
                new InventoryLocationCostingSettings
                {
                    CompanyId = companyId.Value,
                    LocationId = locationIdB.Value,
                    UseCompanyDefault = false,
                    ValuationMethod = "LIFO",
                    CogsAccountCode = cogsAccountCode
                },
                "admin");
            Assert(saveLocationLifoResult.IsSuccess, $"Failed to save location costing override (LIFO): {saveLocationLifoResult.Message}");

            var recalcLocationResult = await service.RecalculateInventoryCostingAsync(companyId.Value, locationIdB.Value, "admin");
            Assert(recalcLocationResult.IsSuccess, $"Location-level recalc failed: {recalcLocationResult.Message}");

            var unitCostAAfterLocation = await GetPostedStockTransactionLineUnitCostAsync(stockOutTxA.Value);
            var unitCostBAfterLocation = await GetPostedStockTransactionLineUnitCostAsync(stockOutTxB.Value);
            Assert(Math.Abs(unitCostAAfterLocation - 15m) < 0.0001m, $"Location A should stay 15.0000 after location-only recalc, got {unitCostAAfterLocation:N4}.");
            Assert(Math.Abs(unitCostBAfterLocation - 20m) < 0.0001m, $"Expected location B stock-out cost 20.0000 after LIFO recalc, got {unitCostBAfterLocation:N4}.");

            await using (var connection = await OpenConnectionAsync())
            {
                await using var outboundMethodCheck = new NpgsqlCommand(
                    @"SELECT source_id, valuation_method
FROM inv_cost_outbound_events
WHERE company_id = @company_id
  AND source_type = 'STOCK_OUT'
  AND source_id IN (@source_id_a, @source_id_b)
ORDER BY source_id;",
                    connection);
                outboundMethodCheck.Parameters.AddWithValue("company_id", companyId.Value);
                outboundMethodCheck.Parameters.AddWithValue("source_id_a", stockOutTxA.Value);
                outboundMethodCheck.Parameters.AddWithValue("source_id_b", stockOutTxB.Value);
                await using var reader = await outboundMethodCheck.ExecuteReaderAsync();

                var methodBySourceId = new Dictionary<long, string>();
                while (await reader.ReadAsync())
                {
                    methodBySourceId[reader.GetInt64(0)] = reader.GetString(1);
                }

                Assert(
                    methodBySourceId.TryGetValue(stockOutTxA.Value, out var methodA) &&
                    methodA.Equals("AVERAGE", StringComparison.OrdinalIgnoreCase),
                    "Location A outbound event should keep valuation method AVERAGE.");
                Assert(
                    methodBySourceId.TryGetValue(stockOutTxB.Value, out var methodB) &&
                    methodB.Equals("LIFO", StringComparison.OrdinalIgnoreCase),
                    "Location B outbound event should be rewritten to valuation method LIFO.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var pendingAdjustmentCountCommand = new NpgsqlCommand(
                    @"SELECT COUNT(1)
FROM inv_cost_adjustment_events
WHERE company_id = @company_id
  AND location_id = @location_id
  AND cogs_journal_id IS NULL;",
                    connection);
                pendingAdjustmentCountCommand.Parameters.AddWithValue("company_id", companyId.Value);
                pendingAdjustmentCountCommand.Parameters.AddWithValue("location_id", locationIdB.Value);
                var pendingAdjustmentCount = Convert.ToInt32(await pendingAdjustmentCountCommand.ExecuteScalarAsync());
                Assert(pendingAdjustmentCount > 0, "Location recalc should create pending valuation adjustment events.");
            }

            var pullResult = await service.PullInventoryJournalsForPeriodAsync(
                companyId.Value,
                locationId: null,
                periodMonth: baseDate,
                actorUsername: "admin");
            Assert(pullResult.IsSuccess, $"Inventory journal pull failed: {pullResult.Message}");

            await using (var connection = await OpenConnectionAsync())
            {
                await using var stockInLinkedCommand = new NpgsqlCommand(
                    @"SELECT COUNT(1)
FROM inv_cost_outbound_events
WHERE company_id = @company_id
  AND source_type = 'STOCK_IN'
  AND source_id = @source_id
  AND cogs_journal_id IS NOT NULL;",
                    connection);
                stockInLinkedCommand.Parameters.AddWithValue("company_id", companyId.Value);
                stockInLinkedCommand.Parameters.AddWithValue("source_id", stockInTxA!.Value);
                var stockInLinkedCount = Convert.ToInt32(await stockInLinkedCommand.ExecuteScalarAsync());
                Assert(stockInLinkedCount > 0, "Stock-in events should be linked to pulled draft journals.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var stockInJournalAmountCommand = new NpgsqlCommand(
                    @"SELECT
    COUNT(1) AS line_count,
    COALESCE(SUM(CASE WHEN a.account_code = @inventory_account THEN d.debit ELSE 0 END), 0) AS inventory_debit,
    COALESCE(SUM(CASE WHEN a.account_code = @inventory_account THEN d.credit ELSE 0 END), 0) AS inventory_credit,
    COALESCE(SUM(CASE WHEN a.account_code = @cogs_account THEN d.debit ELSE 0 END), 0) AS cogs_debit,
    COALESCE(SUM(CASE WHEN a.account_code = @cogs_account THEN d.credit ELSE 0 END), 0) AS cogs_credit
FROM gl_journal_headers h
JOIN gl_journal_details d ON d.header_id = h.id
JOIN gl_accounts a ON a.id = d.account_id
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
  AND h.reference_no = @reference_no
  AND h.status = 'DRAFT';",
                    connection);
                stockInJournalAmountCommand.Parameters.AddWithValue("inventory_account", inventoryAccountCode);
                stockInJournalAmountCommand.Parameters.AddWithValue("cogs_account", cogsAccountCode);
                stockInJournalAmountCommand.Parameters.AddWithValue("company_id", companyId.Value);
                stockInJournalAmountCommand.Parameters.AddWithValue("location_id", locationIdA.Value);
                stockInJournalAmountCommand.Parameters.AddWithValue("reference_no", "ITCOST-IN-A-1");
                await using var reader = await stockInJournalAmountCommand.ExecuteReaderAsync();
                Assert(await reader.ReadAsync(), "Stock-in journal row must exist.");
                var lineCount = reader.GetInt64(0);
                var inventoryDebit = reader.GetDecimal(1);
                var inventoryCredit = reader.GetDecimal(2);
                var cogsDebit = reader.GetDecimal(3);
                var cogsCredit = reader.GetDecimal(4);
                Assert(lineCount == 4, $"Expected stock-in journal keep detail lines (4), got {lineCount}.");
                Assert(inventoryDebit > 0, $"Expected stock-in inventory debit > 0, got {inventoryDebit:N2}.");
                Assert(inventoryCredit == 0, $"Expected stock-in inventory credit = 0, got {inventoryCredit:N2}.");
                Assert(cogsDebit == 0, $"Expected stock-in setting-account debit = 0, got {cogsDebit:N2}.");
                Assert(cogsCredit > 0, $"Expected stock-in setting-account credit > 0, got {cogsCredit:N2}.");
                Assert(Math.Abs(inventoryDebit - cogsCredit) <= 0.01m, $"Stock-in journal should be balanced per reference. Debit={inventoryDebit:N2}, Credit={cogsCredit:N2}.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var stockOutJournalAmountCommand = new NpgsqlCommand(
                    @"SELECT
    COUNT(1) AS line_count,
    COALESCE(SUM(CASE WHEN a.account_code = @inventory_account THEN d.debit ELSE 0 END), 0) AS inventory_debit,
    COALESCE(SUM(CASE WHEN a.account_code = @inventory_account THEN d.credit ELSE 0 END), 0) AS inventory_credit,
    COALESCE(SUM(CASE WHEN a.account_code = @expense_account THEN d.debit ELSE 0 END), 0) AS expense_debit,
    COALESCE(SUM(CASE WHEN a.account_code = @expense_account THEN d.credit ELSE 0 END), 0) AS expense_credit
FROM gl_journal_headers h
JOIN gl_journal_details d ON d.header_id = h.id
JOIN gl_accounts a ON a.id = d.account_id
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
  AND h.reference_no = @reference_no
  AND h.status = 'DRAFT';",
                    connection);
                stockOutJournalAmountCommand.Parameters.AddWithValue("inventory_account", inventoryAccountCode);
                stockOutJournalAmountCommand.Parameters.AddWithValue("expense_account", cogsAccountCode);
                stockOutJournalAmountCommand.Parameters.AddWithValue("company_id", companyId.Value);
                stockOutJournalAmountCommand.Parameters.AddWithValue("location_id", locationIdA.Value);
                stockOutJournalAmountCommand.Parameters.AddWithValue("reference_no", "ITCOST-OUT-A");
                await using var reader = await stockOutJournalAmountCommand.ExecuteReaderAsync();
                Assert(await reader.ReadAsync(), "Stock-out journal row must exist.");
                var lineCount = reader.GetInt64(0);
                var inventoryDebit = reader.GetDecimal(1);
                var inventoryCredit = reader.GetDecimal(2);
                var expenseDebit = reader.GetDecimal(3);
                var expenseCredit = reader.GetDecimal(4);
                Assert(lineCount == 4, $"Expected stock-out journal keep detail lines (4), got {lineCount}.");
                Assert(inventoryDebit == 0, $"Expected stock-out inventory debit = 0, got {inventoryDebit:N2}.");
                Assert(inventoryCredit > 0, $"Expected stock-out inventory credit > 0, got {inventoryCredit:N2}.");
                Assert(expenseDebit > 0, $"Expected stock-out expense debit > 0, got {expenseDebit:N2}.");
                Assert(expenseCredit == 0, $"Expected stock-out expense credit = 0, got {expenseCredit:N2}.");
                Assert(Math.Abs(inventoryCredit - expenseDebit) <= 0.01m, $"Stock-out journal should be balanced per reference. Debit={expenseDebit:N2}, Credit={inventoryCredit:N2}.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var outboundLinkedCommand = new NpgsqlCommand(
                    @"SELECT COUNT(1)
FROM inv_cost_outbound_events
WHERE company_id = @company_id
  AND source_type = 'STOCK_OUT'
  AND source_id IN (@source_id_a, @source_id_b)
  AND cogs_journal_id IS NOT NULL;",
                    connection);
                outboundLinkedCommand.Parameters.AddWithValue("company_id", companyId.Value);
                outboundLinkedCommand.Parameters.AddWithValue("source_id_a", stockOutTxA.Value);
                outboundLinkedCommand.Parameters.AddWithValue("source_id_b", stockOutTxB.Value);
                var outboundLinkedCount = Convert.ToInt32(await outboundLinkedCommand.ExecuteScalarAsync());
                Assert(outboundLinkedCount >= 2, "Stock-out outbound events should be linked to pulled draft journals.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var opnamePlusLinkedCommand = new NpgsqlCommand(
                    @"SELECT COUNT(1)
FROM inv_cost_outbound_events
WHERE company_id = @company_id
  AND source_type = 'OPNAME_PLUS'
  AND source_id = @source_id
  AND cogs_journal_id IS NOT NULL;",
                    connection);
                opnamePlusLinkedCommand.Parameters.AddWithValue("company_id", companyId.Value);
                opnamePlusLinkedCommand.Parameters.AddWithValue("source_id", opnamePlusA!.Value);
                var opnamePlusLinkedCount = Convert.ToInt32(await opnamePlusLinkedCommand.ExecuteScalarAsync());
                Assert(opnamePlusLinkedCount > 0, "Opname-plus events should be linked to pulled draft journals.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var opnamePlusJournalCommand = new NpgsqlCommand(
                    @"WITH target_journal AS (
    SELECT DISTINCT cogs_journal_id AS journal_id
    FROM inv_cost_outbound_events
    WHERE company_id = @company_id
      AND source_type = 'OPNAME_PLUS'
      AND source_id = @source_id
      AND cogs_journal_id IS NOT NULL
)
SELECT
    COUNT(1) AS line_count,
    COALESCE(SUM(CASE WHEN a.account_code = @inventory_account THEN d.debit ELSE 0 END), 0) AS inventory_debit,
    COALESCE(SUM(CASE WHEN a.account_code = @inventory_account THEN d.credit ELSE 0 END), 0) AS inventory_credit,
    COALESCE(SUM(CASE WHEN a.account_code = @cogs_account THEN d.debit ELSE 0 END), 0) AS cogs_debit,
    COALESCE(SUM(CASE WHEN a.account_code = @cogs_account THEN d.credit ELSE 0 END), 0) AS cogs_credit
FROM gl_journal_details d
JOIN gl_accounts a ON a.id = d.account_id
JOIN target_journal j ON j.journal_id = d.header_id;",
                    connection);
                opnamePlusJournalCommand.Parameters.AddWithValue("company_id", companyId.Value);
                opnamePlusJournalCommand.Parameters.AddWithValue("source_id", opnamePlusA!.Value);
                opnamePlusJournalCommand.Parameters.AddWithValue("inventory_account", inventoryAccountCode);
                opnamePlusJournalCommand.Parameters.AddWithValue("cogs_account", cogsAccountCode);
                await using var reader = await opnamePlusJournalCommand.ExecuteReaderAsync();
                Assert(await reader.ReadAsync(), "Opname-plus journal row must exist.");
                var lineCount = reader.GetInt64(0);
                var inventoryDebit = reader.GetDecimal(1);
                var inventoryCredit = reader.GetDecimal(2);
                var cogsDebit = reader.GetDecimal(3);
                var cogsCredit = reader.GetDecimal(4);
                Assert(lineCount == 2, $"Expected opname-plus journal keep detail lines (2), got {lineCount}.");
                Assert(inventoryDebit > 0, $"Expected opname-plus inventory debit > 0, got {inventoryDebit:N2}.");
                Assert(inventoryCredit == 0, $"Expected opname-plus inventory credit = 0, got {inventoryCredit:N2}.");
                Assert(cogsDebit == 0, $"Expected opname-plus setting-account debit = 0, got {cogsDebit:N2}.");
                Assert(cogsCredit > 0, $"Expected opname-plus setting-account credit > 0, got {cogsCredit:N2}.");
                Assert(Math.Abs(inventoryDebit - cogsCredit) <= 0.01m, $"Opname-plus journal should be balanced. Debit={inventoryDebit:N2}, Credit={cogsCredit:N2}.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var adjustmentLinkedCommand = new NpgsqlCommand(
                    @"SELECT COUNT(1)
FROM inv_cost_adjustment_events
WHERE company_id = @company_id
  AND location_id = @location_id
  AND cogs_journal_id IS NOT NULL;",
                    connection);
                adjustmentLinkedCommand.Parameters.AddWithValue("company_id", companyId.Value);
                adjustmentLinkedCommand.Parameters.AddWithValue("location_id", locationIdB.Value);
                var adjustmentLinkedCount = Convert.ToInt32(await adjustmentLinkedCommand.ExecuteScalarAsync());
                Assert(adjustmentLinkedCount > 0, "Adjustment events should be linked to pulled draft journals.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var draftStatusCommand = new NpgsqlCommand(
                    @"WITH pulled_journal_ids AS (
    SELECT DISTINCT cogs_journal_id AS journal_id
    FROM inv_cost_outbound_events
    WHERE company_id = @company_id
      AND source_type = 'STOCK_OUT'
      AND source_id IN (@source_id_a, @source_id_b)
      AND cogs_journal_id IS NOT NULL
    UNION
    SELECT DISTINCT cogs_journal_id AS journal_id
    FROM inv_cost_adjustment_events
    WHERE company_id = @company_id
      AND location_id = @location_id
      AND cogs_journal_id IS NOT NULL
)
SELECT COUNT(1)
FROM gl_journal_headers h
JOIN pulled_journal_ids p ON p.journal_id = h.id
WHERE h.status = 'DRAFT';",
                    connection);
                draftStatusCommand.Parameters.AddWithValue("company_id", companyId.Value);
                draftStatusCommand.Parameters.AddWithValue("location_id", locationIdB.Value);
                draftStatusCommand.Parameters.AddWithValue("source_id_a", stockOutTxA.Value);
                draftStatusCommand.Parameters.AddWithValue("source_id_b", stockOutTxB.Value);
                var draftCount = Convert.ToInt32(await draftStatusCommand.ExecuteScalarAsync());
                Assert(draftCount > 0, "Pulled inventory journals should be created as DRAFT.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var ledgerCountCommand = new NpgsqlCommand(
                    @"WITH pulled_journal_ids AS (
    SELECT DISTINCT cogs_journal_id AS journal_id
    FROM inv_cost_outbound_events
    WHERE company_id = @company_id
      AND source_type = 'STOCK_OUT'
      AND source_id IN (@source_id_a, @source_id_b)
      AND cogs_journal_id IS NOT NULL
    UNION
    SELECT DISTINCT cogs_journal_id AS journal_id
    FROM inv_cost_adjustment_events
    WHERE company_id = @company_id
      AND location_id = @location_id
      AND cogs_journal_id IS NOT NULL
)
SELECT COUNT(1)
FROM gl_ledger_entries le
JOIN pulled_journal_ids p ON p.journal_id = le.journal_id;",
                    connection);
                ledgerCountCommand.Parameters.AddWithValue("company_id", companyId.Value);
                ledgerCountCommand.Parameters.AddWithValue("location_id", locationIdB.Value);
                ledgerCountCommand.Parameters.AddWithValue("source_id_a", stockOutTxA.Value);
                ledgerCountCommand.Parameters.AddWithValue("source_id_b", stockOutTxB.Value);
                var ledgerCount = Convert.ToInt32(await ledgerCountCommand.ExecuteScalarAsync());
                Assert(ledgerCount == 0, "Pulled inventory journals should not create ledger rows automatically.");
            }
        }
        finally
        {
            try
            {
                if (companyId.HasValue)
                {
                    await CleanupTemporaryInventoryCostingCompanyAsync(companyId.Value);
                    await using var connection = await OpenConnectionAsync();
                    await CleanupInventoryArtifactsByCodesAsync(connection, companyId.Value, [categoryCode], [itemCode]);
                }
            }
            finally
            {
                await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
            }
        }
    }

    private static async Task TestInventoryDraftAutoNumberingAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var itemCode = $"ITINV{Math.Abs(stamp % 1000000):000000}";
        var warehouseCode = $"ITWH{Math.Abs((stamp + 11) % 1000000):000000}";

        long? itemId = null;
        long? warehouseId = null;
        long? stockTxId = null;
        long? opnameId = null;

        try
        {
            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.ToString(), "admin");

            var saveItemResult = await service.SaveInventoryItemAsync(
                companyId,
                new ManagedInventoryItem
                {
                    Id = 0,
                    CompanyId = companyId,
                    Code = itemCode,
                    Name = $"Integration Inventory Item {stamp}",
                    Uom = "PCS",
                    Category = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(saveItemResult.IsSuccess && saveItemResult.EntityId.HasValue, $"Failed to create test item: {saveItemResult.Message}");
            itemId = saveItemResult.EntityId!.Value;

            warehouseId = await CreateWarehouseAsync(
                service,
                companyId,
                warehouseCode,
                $"Integration Warehouse {stamp}",
                locationId,
                "admin");

            var saveTxResult = await service.SaveStockTransactionDraftAsync(
                new ManagedStockTransaction
                {
                    Id = 0,
                    CompanyId = companyId,
                    LocationId = locationId,
                    TransactionNo = string.Empty,
                    TransactionType = "STOCK_IN",
                    TransactionDate = DateTime.Today,
                    Status = "DRAFT",
                    ReferenceNo = "ITEST-AUTO-NO",
                    Description = "Inventory draft auto numbering test"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        LineNo = 1,
                        ItemId = itemId.Value,
                        Qty = 5,
                        UnitCost = 1000,
                        WarehouseId = warehouseId.Value,
                        Notes = "ITEST line"
                    }
                },
                "admin");
            Assert(saveTxResult.IsSuccess && saveTxResult.EntityId.HasValue, $"Failed to save stock tx draft: {saveTxResult.Message}");
            stockTxId = saveTxResult.EntityId!.Value;

            var txBundle = await service.GetStockTransactionBundleAsync(stockTxId.Value);
            Assert(txBundle is not null, "Saved stock transaction should be loadable.");
            Assert(!string.IsNullOrWhiteSpace(txBundle!.Header.TransactionNo), "Transaction number should be auto-generated.");
            Assert(txBundle.Header.TransactionNo.StartsWith("SIN-", StringComparison.OrdinalIgnoreCase), $"Unexpected stock tx prefix: {txBundle.Header.TransactionNo}");

            var saveOpnameResult = await service.SaveStockOpnameDraftAsync(
                new ManagedStockOpname
                {
                    Id = 0,
                    CompanyId = companyId,
                    LocationId = locationId,
                    OpnameNo = string.Empty,
                    OpnameDate = DateTime.Today,
                    Description = "Inventory opname auto numbering test",
                    Status = "DRAFT"
                },
                new[]
                {
                    new ManagedStockOpnameLine
                    {
                        LineNo = 1,
                        ItemId = itemId.Value,
                        SystemQty = 0,
                        ActualQty = 0,
                        DifferenceQty = 0,
                        Notes = "ITEST opname line"
                    }
                },
                "admin");
            Assert(saveOpnameResult.IsSuccess && saveOpnameResult.EntityId.HasValue, $"Failed to save stock opname draft: {saveOpnameResult.Message}");
            opnameId = saveOpnameResult.EntityId!.Value;

            var opnameBundle = await service.GetStockOpnameBundleAsync(opnameId.Value);
            Assert(opnameBundle is not null, "Saved stock opname should be loadable.");
            Assert(!string.IsNullOrWhiteSpace(opnameBundle!.Header.OpnameNo), "Opname number should be auto-generated.");
            Assert(opnameBundle.Header.OpnameNo.StartsWith("OPN-", StringComparison.OrdinalIgnoreCase), $"Unexpected opname prefix: {opnameBundle.Header.OpnameNo}");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (stockTxId.HasValue)
            {
                await using (var deleteTxAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_STOCK_TX' AND entity_id = @entity_id;",
                    connection))
                {
                    deleteTxAudit.Parameters.AddWithValue("entity_id", stockTxId.Value);
                    await deleteTxAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteTx = new NpgsqlCommand(
                    "DELETE FROM inv_stock_transactions WHERE id = @id;",
                    connection))
                {
                    deleteTx.Parameters.AddWithValue("id", stockTxId.Value);
                    await deleteTx.ExecuteNonQueryAsync();
                }
            }

            if (opnameId.HasValue)
            {
                await using (var deleteOpnameAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_OPNAME' AND entity_id = @entity_id;",
                    connection))
                {
                    deleteOpnameAudit.Parameters.AddWithValue("entity_id", opnameId.Value);
                    await deleteOpnameAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteOpname = new NpgsqlCommand(
                    "DELETE FROM inv_stock_opname WHERE id = @id;",
                    connection))
                {
                    deleteOpname.Parameters.AddWithValue("id", opnameId.Value);
                    await deleteOpname.ExecuteNonQueryAsync();
                }
            }

            if (itemId.HasValue)
            {
                await using (var deleteItemAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_ITEM' AND entity_id = @entity_id;",
                    connection))
                {
                    deleteItemAudit.Parameters.AddWithValue("entity_id", itemId.Value);
                    await deleteItemAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteStock = new NpgsqlCommand(
                    @"DELETE FROM inv_stock
WHERE company_id = @company_id
  AND location_id = @location_id
  AND item_id = @item_id;",
                    connection))
                {
                    deleteStock.Parameters.AddWithValue("company_id", companyId);
                    deleteStock.Parameters.AddWithValue("location_id", locationId);
                    deleteStock.Parameters.AddWithValue("item_id", itemId.Value);
                    await deleteStock.ExecuteNonQueryAsync();
                }

                await using (var deleteItem = new NpgsqlCommand(
                    "DELETE FROM inv_items WHERE id = @id;",
                    connection))
                {
                    deleteItem.Parameters.AddWithValue("id", itemId.Value);
                    await deleteItem.ExecuteNonQueryAsync();
                }
            }

            if (warehouseId.HasValue)
            {
                await using (var deleteWarehouseAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_WAREHOUSE' AND entity_id = @entity_id;",
                    connection))
                {
                    deleteWarehouseAudit.Parameters.AddWithValue("entity_id", warehouseId.Value);
                    await deleteWarehouseAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteWarehouse = new NpgsqlCommand(
                    "DELETE FROM inv_warehouses WHERE id = @id;",
                    connection))
                {
                    deleteWarehouse.Parameters.AddWithValue("id", warehouseId.Value);
                    await deleteWarehouse.ExecuteNonQueryAsync();
                }
            }

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
        }
    }

    private static async Task TestInventoryStockTransactionRejectsWarehouseLocationMismatchAsync()
    {
        var service = CreateService();
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tempCompanyCode = $"ITWLM{Math.Abs(stamp % 1000000):000000}";
        var locationCodeA = $"MA{Math.Abs(stamp % 10000):0000}";
        var locationCodeB = $"MB{Math.Abs((stamp + 13) % 10000):0000}";
        var warehouseCodeB = $"MWB{Math.Abs(stamp % 1000000):000000}";
        var itemCode = $"MWI{Math.Abs((stamp + 7) % 1000000):000000}";

        long? companyId = null;

        try
        {
            var createCompanyResult = await service.SaveCompanyAsync(
                new ManagedCompany
                {
                    Id = 0,
                    Code = tempCompanyCode,
                    Name = $"Warehouse Mismatch Company {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createCompanyResult.IsSuccess && createCompanyResult.EntityId.HasValue,
                $"Failed to create mismatch company: {createCompanyResult.Message}");
            companyId = createCompanyResult.EntityId!.Value;

            var createLocationAResult = await service.SaveLocationAsync(
                new ManagedLocation
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = locationCodeA,
                    Name = $"Mismatch Location A {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createLocationAResult.IsSuccess && createLocationAResult.EntityId.HasValue,
                $"Failed to create mismatch location A: {createLocationAResult.Message}");
            var locationIdA = createLocationAResult.EntityId!.Value;

            var createLocationBResult = await service.SaveLocationAsync(
                new ManagedLocation
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = locationCodeB,
                    Name = $"Mismatch Location B {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createLocationBResult.IsSuccess && createLocationBResult.EntityId.HasValue,
                $"Failed to create mismatch location B: {createLocationBResult.Message}");
            var locationIdB = createLocationBResult.EntityId!.Value;

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.Value.ToString(), "admin");

            var warehouseIdB = await CreateWarehouseAsync(
                service,
                companyId.Value,
                warehouseCodeB,
                $"Mismatch Warehouse B {stamp}",
                locationIdB,
                "admin");

            var saveItemResult = await service.SaveInventoryItemAsync(
                companyId.Value,
                new ManagedInventoryItem
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = itemCode,
                    Name = $"Mismatch Item {stamp}",
                    Uom = "PCS",
                    Category = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(
                saveItemResult.IsSuccess && saveItemResult.EntityId.HasValue,
                $"Failed to create mismatch item: {saveItemResult.Message}");
            var itemId = saveItemResult.EntityId!.Value;

            var saveTxResult = await service.SaveStockTransactionDraftAsync(
                new ManagedStockTransaction
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    LocationId = locationIdA,
                    TransactionType = "STOCK_IN",
                    TransactionDate = DateTime.Today,
                    ReferenceNo = "ITEST-WH-MISMATCH",
                    Description = "Warehouse mismatch validation"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        LineNo = 1,
                        ItemId = itemId,
                        Qty = 2,
                        UnitCost = 100,
                        WarehouseId = warehouseIdB
                    }
                },
                "admin");

            Assert(!saveTxResult.IsSuccess, "Stock transaction save should reject warehouse bound to another location.");
            Assert(
                saveTxResult.Message.Contains("tidak terdaftar pada location transaksi", StringComparison.OrdinalIgnoreCase),
                $"Unexpected warehouse mismatch message: {saveTxResult.Message}");
        }
        finally
        {
            try
            {
                if (companyId.HasValue)
                {
                    await CleanupTemporaryInventoryCostingCompanyAsync(companyId.Value);
                    await using var connection = await OpenConnectionAsync();
                    await CleanupInventoryArtifactsByCodesAsync(connection, companyId.Value, Array.Empty<string>(), [itemCode]);
                }
            }
            finally
            {
                await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
            }
        }
    }

    private static async Task TestInventoryStockTransactionAllowsGlobalWarehouseAsync()
    {
        var service = CreateService();
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tempCompanyCode = $"ITWGL{Math.Abs(stamp % 1000000):000000}";
        var locationCode = $"GL{Math.Abs(stamp % 10000):0000}";
        var warehouseCode = $"GW{Math.Abs((stamp + 19) % 1000000):000000}";
        var itemCode = $"GWI{Math.Abs((stamp + 23) % 1000000):000000}";

        long? companyId = null;

        try
        {
            var createCompanyResult = await service.SaveCompanyAsync(
                new ManagedCompany
                {
                    Id = 0,
                    Code = tempCompanyCode,
                    Name = $"Global Warehouse Company {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createCompanyResult.IsSuccess && createCompanyResult.EntityId.HasValue,
                $"Failed to create global warehouse company: {createCompanyResult.Message}");
            companyId = createCompanyResult.EntityId!.Value;

            var createLocationResult = await service.SaveLocationAsync(
                new ManagedLocation
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = locationCode,
                    Name = $"Global Warehouse Location {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createLocationResult.IsSuccess && createLocationResult.EntityId.HasValue,
                $"Failed to create global warehouse location: {createLocationResult.Message}");
            var locationId = createLocationResult.EntityId!.Value;

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.Value.ToString(), "admin");

            var globalWarehouseId = await CreateWarehouseAsync(
                service,
                companyId.Value,
                warehouseCode,
                $"Global Warehouse {stamp}",
                locationId: null,
                "admin");

            var saveItemResult = await service.SaveInventoryItemAsync(
                companyId.Value,
                new ManagedInventoryItem
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = itemCode,
                    Name = $"Global Warehouse Item {stamp}",
                    Uom = "PCS",
                    Category = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(
                saveItemResult.IsSuccess && saveItemResult.EntityId.HasValue,
                $"Failed to create global warehouse item: {saveItemResult.Message}");
            var itemId = saveItemResult.EntityId!.Value;

            var saveTxResult = await service.SaveStockTransactionDraftAsync(
                new ManagedStockTransaction
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    LocationId = locationId,
                    TransactionType = "STOCK_IN",
                    TransactionDate = DateTime.Today,
                    ReferenceNo = "ITEST-WH-GLOBAL",
                    Description = "Global warehouse validation"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        LineNo = 1,
                        ItemId = itemId,
                        Qty = 3,
                        UnitCost = 125,
                        WarehouseId = globalWarehouseId
                    }
                },
                "admin");

            Assert(
                saveTxResult.IsSuccess && saveTxResult.EntityId.HasValue,
                $"Global warehouse should be accepted for stock transaction save: {saveTxResult.Message}");

            var bundle = await service.GetStockTransactionBundleAsync(saveTxResult.EntityId!.Value);
            Assert(bundle is not null, "Global warehouse draft should be loadable.");
            Assert(bundle!.Lines.Count == 1, "Expected one line in global warehouse draft.");
            Assert(bundle.Lines[0].WarehouseId == globalWarehouseId, "Global warehouse id should persist on saved draft.");
        }
        finally
        {
            try
            {
                if (companyId.HasValue)
                {
                    await CleanupTemporaryInventoryCostingCompanyAsync(companyId.Value);
                    await using var connection = await OpenConnectionAsync();
                    await CleanupInventoryArtifactsByCodesAsync(connection, companyId.Value, Array.Empty<string>(), [itemCode]);
                }
            }
            finally
            {
                await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
            }
        }
    }

    private static async Task TestInventoryTransferMovesWarehouseBucketsAsync()
    {
        var service = CreateService();
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tempCompanyCode = $"ITTRN{Math.Abs(stamp % 1000000):000000}";
        var locationCode = $"TR{Math.Abs(stamp % 10000):0000}";
        var sourceWarehouseCode = $"TS{Math.Abs(stamp % 1000000):000000}";
        var destinationWarehouseCode = $"TD{Math.Abs((stamp + 31) % 1000000):000000}";
        var itemCode = $"TTI{Math.Abs((stamp + 37) % 1000000):000000}";

        long? companyId = null;

        try
        {
            var createCompanyResult = await service.SaveCompanyAsync(
                new ManagedCompany
                {
                    Id = 0,
                    Code = tempCompanyCode,
                    Name = $"Transfer Warehouse Company {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createCompanyResult.IsSuccess && createCompanyResult.EntityId.HasValue,
                $"Failed to create transfer company: {createCompanyResult.Message}");
            companyId = createCompanyResult.EntityId!.Value;

            var createLocationResult = await service.SaveLocationAsync(
                new ManagedLocation
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = locationCode,
                    Name = $"Transfer Location {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createLocationResult.IsSuccess && createLocationResult.EntityId.HasValue,
                $"Failed to create transfer location: {createLocationResult.Message}");
            var locationId = createLocationResult.EntityId!.Value;

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, companyId.Value.ToString(), "admin");

            var sourceWarehouseId = await CreateWarehouseAsync(
                service,
                companyId.Value,
                sourceWarehouseCode,
                $"Transfer Source Warehouse {stamp}",
                locationId,
                "admin");
            var destinationWarehouseId = await CreateWarehouseAsync(
                service,
                companyId.Value,
                destinationWarehouseCode,
                $"Transfer Destination Warehouse {stamp}",
                locationId,
                "admin");

            var saveItemResult = await service.SaveInventoryItemAsync(
                companyId.Value,
                new ManagedInventoryItem
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    Code = itemCode,
                    Name = $"Transfer Item {stamp}",
                    Uom = "PCS",
                    Category = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(
                saveItemResult.IsSuccess && saveItemResult.EntityId.HasValue,
                $"Failed to create transfer item: {saveItemResult.Message}");
            var itemId = saveItemResult.EntityId!.Value;

            await using (var connection = await OpenConnectionAsync())
            {
                await using var seedStockCommand = new NpgsqlCommand(
                    @"INSERT INTO inv_stock (company_id, location_id, item_id, qty, warehouse_id, updated_at)
VALUES (@company_id, @location_id, @item_id, @qty, @warehouse_id, NOW());",
                    connection);
                seedStockCommand.Parameters.AddWithValue("company_id", companyId.Value);
                seedStockCommand.Parameters.AddWithValue("location_id", locationId);
                seedStockCommand.Parameters.AddWithValue("item_id", itemId);
                seedStockCommand.Parameters.AddWithValue("qty", 10m);
                seedStockCommand.Parameters.AddWithValue("warehouse_id", sourceWarehouseId);
                await seedStockCommand.ExecuteNonQueryAsync();
            }

            var saveTxResult = await service.SaveStockTransactionDraftAsync(
                new ManagedStockTransaction
                {
                    Id = 0,
                    CompanyId = companyId.Value,
                    LocationId = locationId,
                    TransactionType = "TRANSFER",
                    TransactionDate = DateTime.Today,
                    ReferenceNo = "ITEST-TRANSFER",
                    Description = "Transfer bucket movement validation"
                },
                new[]
                {
                    new ManagedStockTransactionLine
                    {
                        LineNo = 1,
                        ItemId = itemId,
                        Qty = 4,
                        UnitCost = 0,
                        WarehouseId = sourceWarehouseId,
                        DestinationWarehouseId = destinationWarehouseId
                    }
                },
                "admin");
            Assert(
                saveTxResult.IsSuccess && saveTxResult.EntityId.HasValue,
                $"Failed to save transfer draft: {saveTxResult.Message}");

            var transactionId = saveTxResult.EntityId!.Value;
            var submitResult = await service.SubmitStockTransactionAsync(transactionId, "admin");
            Assert(submitResult.IsSuccess, $"Failed to submit transfer draft: {submitResult.Message}");

            var approveResult = await service.ApproveStockTransactionAsync(transactionId, "admin");
            Assert(approveResult.IsSuccess, $"Failed to approve transfer draft: {approveResult.Message}");

            var postResult = await service.PostStockTransactionAsync(transactionId, "admin");
            Assert(postResult.IsSuccess, $"Failed to post transfer draft: {postResult.Message}");

            var bundle = await service.GetStockTransactionBundleAsync(transactionId);
            Assert(bundle is not null, "Posted transfer should be loadable.");
            Assert(
                string.Equals(bundle!.Header.Status, "POSTED", StringComparison.OrdinalIgnoreCase),
                $"Expected posted transfer status, got {bundle.Header.Status}.");

            var sourceQty = await GetStockQtyAsync(companyId.Value, locationId, itemId, sourceWarehouseId);
            var destinationQty = await GetStockQtyAsync(companyId.Value, locationId, itemId, destinationWarehouseId);
            Assert(Math.Abs(sourceQty - 6m) < 0.0001m, $"Expected source warehouse qty 6.0000 after transfer, got {sourceQty:N4}.");
            Assert(Math.Abs(destinationQty - 4m) < 0.0001m, $"Expected destination warehouse qty 4.0000 after transfer, got {destinationQty:N4}.");
            Assert(Math.Abs((sourceQty + destinationQty) - 10m) < 0.0001m, "Transfer should preserve total location stock.");
        }
        finally
        {
            try
            {
                if (companyId.HasValue)
                {
                    await CleanupTemporaryInventoryCostingCompanyAsync(companyId.Value);
                    await using var connection = await OpenConnectionAsync();
                    await CleanupInventoryArtifactsByCodesAsync(connection, companyId.Value, Array.Empty<string>(), [itemCode]);
                }
            }
            finally
            {
                await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
            }
        }
    }

    private static async Task TestInventoryMasterCompanyPolicySyncAndWriteGuardAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");

        if (accessOptions.Companies.Count < 2)
        {
            Console.WriteLine("[INFO] Inventory master policy sync test skipped: requires at least 2 accessible companies.");
            return;
        }

        var masterCompanyId = accessOptions.Companies[0].Id;
        var targetCompanyId = accessOptions.Companies[1].Id;
        var previousMasterCompanySetting = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var categoryCode = $"ITSYNCCAT{Math.Abs(stamp % 1000000):000000}";
        var itemCode = $"ITSYNCITM{Math.Abs(stamp % 1000000):000000}";
        var targetBlockedCode = $"ITSYNCBLK{Math.Abs(stamp % 1000000):000000}";

        long? masterCategoryId = null;
        long? masterItemId = null;

        try
        {
            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, masterCompanyId.ToString(), "admin");

            var createCategoryResult = await service.SaveInventoryCategoryAsync(
                masterCompanyId,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = masterCompanyId,
                    Code = categoryCode,
                    Name = $"Integration Sync Category {stamp}",
                    AccountCode = "HO.11000.001",
                    IsActive = true
                },
                "admin");
            Assert(
                createCategoryResult.IsSuccess && createCategoryResult.EntityId.HasValue,
                $"Create master category failed: {createCategoryResult.Message}");
            masterCategoryId = createCategoryResult.EntityId!.Value;

            var createItemResult = await service.SaveInventoryItemAsync(
                masterCompanyId,
                new ManagedInventoryItem
                {
                    Id = 0,
                    CompanyId = masterCompanyId,
                    CategoryId = masterCategoryId,
                    Code = itemCode,
                    Name = $"Integration Sync Item {stamp}",
                    Uom = "PCS",
                    IsActive = true
                },
                "admin");
            Assert(
                createItemResult.IsSuccess && createItemResult.EntityId.HasValue,
                $"Create master item failed: {createItemResult.Message}");
            masterItemId = createItemResult.EntityId!.Value;

            var blockedWriteResult = await service.SaveInventoryCategoryAsync(
                targetCompanyId,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = targetCompanyId,
                    Code = targetBlockedCode,
                    Name = $"Should Block {stamp}",
                    AccountCode = string.Empty,
                    IsActive = true
                },
                "admin");
            Assert(!blockedWriteResult.IsSuccess, "Target company CRUD should be blocked by master-company policy.");
            Assert(
                blockedWriteResult.Message.Contains("hanya dapat sync", StringComparison.OrdinalIgnoreCase),
                $"Unexpected blocked-write message: {blockedWriteResult.Message}");

            var syncResult = await service.SyncInventoryMasterDataAsync(targetCompanyId, "admin");
            Assert(syncResult.IsSuccess, $"Sync from master failed: {syncResult.Message}");

            await using (var connection = await OpenConnectionAsync())
            {
                await using var targetCategoryQuery = new NpgsqlCommand(
                    @"SELECT id, category_name, account_code, is_active
FROM inv_categories
WHERE upper(category_code) = upper(@category_code)
LIMIT 1;",
                    connection);
                targetCategoryQuery.Parameters.AddWithValue("category_code", categoryCode);
                await using var categoryReader = await targetCategoryQuery.ExecuteReaderAsync();
                Assert(await categoryReader.ReadAsync(), "Synced category should exist globally.");
                Assert(
                    string.Equals(categoryReader.GetString(1), $"Integration Sync Category {stamp}", StringComparison.Ordinal),
                    "Synced category should copy master name.");
                Assert(
                    string.Equals(categoryReader.GetString(2), "HO.11000.001", StringComparison.OrdinalIgnoreCase),
                    "Synced category should copy master account code.");
                Assert(categoryReader.GetBoolean(3), "Synced category should be active.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var targetItemQuery = new NpgsqlCommand(
                    @"SELECT i.item_name,
       COALESCE(c.account_code, '') AS account_code,
       i.is_active
FROM inv_items i
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE upper(item_code) = upper(@item_code)
LIMIT 1;",
                    connection);
                targetItemQuery.Parameters.AddWithValue("item_code", itemCode);
                await using var itemReader = await targetItemQuery.ExecuteReaderAsync();
                Assert(await itemReader.ReadAsync(), "Synced item should exist globally.");
                Assert(
                    string.Equals(itemReader.GetString(0), $"Integration Sync Item {stamp}", StringComparison.Ordinal),
                    "Synced item should copy master name.");
                Assert(
                    string.Equals(itemReader.GetString(1), "HO.11000.001", StringComparison.OrdinalIgnoreCase),
                    "Synced item should follow category account code.");
                Assert(itemReader.GetBoolean(2), "Synced item should be active.");
            }

            var updateCategoryResult = await service.SaveInventoryCategoryAsync(
                masterCompanyId,
                new ManagedInventoryCategory
                {
                    Id = masterCategoryId.Value,
                    CompanyId = masterCompanyId,
                    Code = categoryCode,
                    Name = $"Integration Sync Category Updated {stamp}",
                    AccountCode = "HO.11000.002",
                    IsActive = true
                },
                "admin");
            Assert(updateCategoryResult.IsSuccess, $"Update master category failed: {updateCategoryResult.Message}");

            var updateItemResult = await service.SaveInventoryItemAsync(
                masterCompanyId,
                new ManagedInventoryItem
                {
                    Id = masterItemId.Value,
                    CompanyId = masterCompanyId,
                    CategoryId = masterCategoryId,
                    Code = itemCode,
                    Name = $"Integration Sync Item Updated {stamp}",
                    Uom = "PCS",
                    IsActive = true
                },
                "admin");
            Assert(updateItemResult.IsSuccess, $"Update master item failed: {updateItemResult.Message}");

            var resyncResult = await service.SyncInventoryMasterDataAsync(targetCompanyId, "admin");
            Assert(resyncResult.IsSuccess, $"Second sync failed: {resyncResult.Message}");

            await using (var connection = await OpenConnectionAsync())
            {
                await using var targetCategoryAfterResync = new NpgsqlCommand(
                    @"SELECT category_name, account_code
FROM inv_categories
WHERE upper(category_code) = upper(@category_code)
LIMIT 1;",
                    connection);
                targetCategoryAfterResync.Parameters.AddWithValue("category_code", categoryCode);
                await using var categoryReader = await targetCategoryAfterResync.ExecuteReaderAsync();
                Assert(await categoryReader.ReadAsync(), "Category should still exist after second sync.");
                Assert(
                    string.Equals(categoryReader.GetString(0), $"Integration Sync Category Updated {stamp}", StringComparison.Ordinal),
                    "Master update should overwrite target category name.");
                Assert(
                    string.Equals(categoryReader.GetString(1), "HO.11000.002", StringComparison.OrdinalIgnoreCase),
                    "Master update should overwrite target category account code.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var targetItemAfterResync = new NpgsqlCommand(
                    @"SELECT i.item_name,
       COALESCE(c.account_code, '') AS account_code
FROM inv_items i
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE upper(item_code) = upper(@item_code)
LIMIT 1;",
                    connection);
                targetItemAfterResync.Parameters.AddWithValue("item_code", itemCode);
                await using var itemReader = await targetItemAfterResync.ExecuteReaderAsync();
                Assert(await itemReader.ReadAsync(), "Item should still exist after second sync.");
                Assert(
                    string.Equals(itemReader.GetString(0), $"Integration Sync Item Updated {stamp}", StringComparison.Ordinal),
                    "Master update should overwrite target item name.");
                Assert(
                    string.Equals(itemReader.GetString(1), "HO.11000.002", StringComparison.OrdinalIgnoreCase),
                    "Master update should overwrite target item account code.");
            }
        }
        finally
        {
            try
            {
                await using var connection = await OpenConnectionAsync();

                foreach (var companyId in new[] { targetCompanyId, masterCompanyId })
                {
                    await CleanupInventoryArtifactsByCodesAsync(
                        connection,
                        companyId,
                        new[] { categoryCode },
                        new[] { itemCode });
                }

                await using (var deleteCategoryAudit = new NpgsqlCommand(
                    @"DELETE FROM sec_audit_logs
WHERE entity_type = 'INV_CATEGORY'
  AND details LIKE @details;",
                    connection))
                {
                    deleteCategoryAudit.Parameters.AddWithValue("details", $"%code={categoryCode};%");
                    await deleteCategoryAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteItemAudit = new NpgsqlCommand(
                    @"DELETE FROM sec_audit_logs
WHERE entity_type = 'INV_ITEM'
  AND details LIKE @details;",
                    connection))
                {
                    deleteItemAudit.Parameters.AddWithValue("details", $"%code={itemCode};%");
                    await deleteItemAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteSyncAudit = new NpgsqlCommand(
                    @"DELETE FROM sec_audit_logs
WHERE entity_type = 'INV_SYNC'
  AND details LIKE @details;",
                    connection))
                {
                    deleteSyncAudit.Parameters.AddWithValue("details", $"%target_company_id={targetCompanyId};%");
                    await deleteSyncAudit.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await SetSystemSettingAsync(InventoryMasterCompanySettingKey, previousMasterCompanySetting, "admin");
            }
        }
    }

    private static async Task TestInventoryCentralSyncMockUploadDownloadAndLogsAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        if (accessOptions.Companies.Count < 2)
        {
            Console.WriteLine("[INFO] Central sync mock test skipped: requires at least 2 accessible companies.");
            return;
        }

        var masterCompanyId = accessOptions.Companies[0].Id;
        var targetCompanyId = accessOptions.Companies[1].Id;
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var uploadCategoryCode = $"ITUPCAT{Math.Abs(stamp % 1000000):000000}";
        var uploadItemCode = $"ITUPITM{Math.Abs(stamp % 1000000):000000}";
        var downloadCategoryCode = $"ITDLCAT{Math.Abs(stamp % 1000000):000000}";
        var downloadItemCode = $"ITDLITM{Math.Abs(stamp % 1000000):000000}";
        var apiKey = $"itest-key-{stamp}";

        var oldMaster = await GetSystemSettingAsync(InventoryMasterCompanySettingKey);
        var oldBaseUrl = await GetSystemSettingAsync(CentralSyncBaseUrlSettingKey);
        var oldApiKey = await GetSystemSettingAsync(CentralSyncApiKeySettingKey);
        var oldUploadPath = await GetSystemSettingAsync(CentralSyncUploadPathSettingKey);
        var oldDownloadPath = await GetSystemSettingAsync(CentralSyncDownloadPathSettingKey);
        var oldTimeout = await GetSystemSettingAsync(CentralSyncTimeoutSettingKey);

        var mockState = new CentralSyncMockState
        {
            ExpectedApiKey = apiKey,
            DownloadCategoryCode = downloadCategoryCode,
            DownloadItemCode = downloadItemCode
        };

        TcpListener? listener = null;
        CancellationTokenSource? serverCts = null;
        Task? serverTask = null;

        try
        {
            var port = GetFreeTcpPort();
            var baseUrl = $"http://127.0.0.1:{port}/";
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            serverCts = new CancellationTokenSource();
            serverTask = RunCentralSyncMockServerAsync(listener, mockState, serverCts.Token);

            await SetSystemSettingAsync(InventoryMasterCompanySettingKey, masterCompanyId.ToString(), "admin");
            var saveSettingsResult = await service.SaveInventoryCentralSyncSettingsAsync(
                new InventoryCentralSyncSettings
                {
                    BaseUrl = baseUrl.TrimEnd('/'),
                    ApiKey = apiKey,
                    UploadPath = "/api/inventory/sync/upload",
                    DownloadPath = "/api/inventory/sync/download",
                    TimeoutSeconds = 15
                },
                "admin");
            Assert(saveSettingsResult.IsSuccess, $"Save central sync settings failed: {saveSettingsResult.Message}");

            await using (var connection = await OpenConnectionAsync())
            {
                await using (var clearWatermarkUpload = new NpgsqlCommand(
                    @"DELETE FROM inv_sync_watermarks
WHERE company_id = @company_id AND direction = 'UPLOAD';",
                    connection))
                {
                    clearWatermarkUpload.Parameters.AddWithValue("company_id", masterCompanyId);
                    await clearWatermarkUpload.ExecuteNonQueryAsync();
                }

                await using (var clearWatermarkDownload = new NpgsqlCommand(
                    @"DELETE FROM inv_sync_watermarks
WHERE company_id = @company_id AND direction = 'DOWNLOAD';",
                    connection))
                {
                    clearWatermarkDownload.Parameters.AddWithValue("company_id", targetCompanyId);
                    await clearWatermarkDownload.ExecuteNonQueryAsync();
                }
            }

            var createCategoryResult = await service.SaveInventoryCategoryAsync(
                masterCompanyId,
                new ManagedInventoryCategory
                {
                    Id = 0,
                    CompanyId = masterCompanyId,
                    Code = uploadCategoryCode,
                    Name = $"Central Upload Category {stamp}",
                    AccountCode = "HO.11000.001",
                    IsActive = true
                },
                "admin");
            Assert(createCategoryResult.IsSuccess && createCategoryResult.EntityId.HasValue, $"Create upload category failed: {createCategoryResult.Message}");
            var uploadCategoryId = createCategoryResult.EntityId ?? 0;

            var createItemResult = await service.SaveInventoryItemAsync(
                masterCompanyId,
                new ManagedInventoryItem
                {
                    Id = 0,
                    CompanyId = masterCompanyId,
                    CategoryId = uploadCategoryId,
                    Code = uploadItemCode,
                    Name = $"Central Upload Item {stamp}",
                    Uom = "PCS",
                    IsActive = true
                },
                "admin");
            Assert(createItemResult.IsSuccess, $"Create upload item failed: {createItemResult.Message}");

            var uploadResult = await service.UploadInventoryToCentralAsync(masterCompanyId, "admin");
            Assert(uploadResult.IsSuccess, $"Upload to central failed: {uploadResult.Message}");
            Assert(uploadResult.EntityId.HasValue && uploadResult.EntityId.Value > 0, "Upload run id should be returned.");
            var uploadRunId = uploadResult.EntityId ?? 0;

            Assert(mockState.UploadCallCount > 0, "Mock upload endpoint should be called.");
            Assert(
                !string.IsNullOrWhiteSpace(mockState.LastUploadBody) &&
                mockState.LastUploadBody!.Contains(uploadCategoryCode, StringComparison.OrdinalIgnoreCase),
                "Upload payload should contain created category.");

            var downloadResult = await service.DownloadInventoryFromCentralAsync(targetCompanyId, "admin");
            Assert(downloadResult.IsSuccess, $"Download from central failed: {downloadResult.Message}");
            Assert(downloadResult.EntityId.HasValue && downloadResult.EntityId.Value > 0, "Download run id should be returned.");
            var downloadRunId = downloadResult.EntityId ?? 0;
            Assert(mockState.DownloadCallCount > 0, "Mock download endpoint should be called.");

            await using (var connection = await OpenConnectionAsync())
            {
                await using var uploadRunCheck = new NpgsqlCommand(
                    @"SELECT status, total_items, success_items, failed_items
FROM inv_sync_runs
WHERE id = @id;",
                    connection);
                uploadRunCheck.Parameters.AddWithValue("id", uploadRunId);
                await using var uploadReader = await uploadRunCheck.ExecuteReaderAsync();
                Assert(await uploadReader.ReadAsync(), "Upload run should exist.");
                Assert(
                    string.Equals(uploadReader.GetString(0), "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uploadReader.GetString(0), "PARTIAL", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected upload run status: {uploadReader.GetString(0)}");
                Assert(uploadReader.GetInt32(1) > 0, "Upload run should contain item count.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var uploadItemLogCheck = new NpgsqlCommand(
                    @"SELECT COUNT(1)
FROM inv_sync_item_logs
WHERE sync_run_id = @run_id
  AND upper(category_code) = upper(@category_code);",
                    connection);
                uploadItemLogCheck.Parameters.AddWithValue("run_id", uploadRunId);
                uploadItemLogCheck.Parameters.AddWithValue("category_code", uploadCategoryCode);
                var uploadItemLogCount = Convert.ToInt32(await uploadItemLogCheck.ExecuteScalarAsync());
                Assert(uploadItemLogCount > 0, "Upload category log should be written.");
            }

            await using (var connection = await OpenConnectionAsync())
            {
                await using var targetCategoryCheck = new NpgsqlCommand(
                    @"SELECT category_name, is_active
FROM inv_categories
WHERE upper(category_code) = upper(@category_code)
LIMIT 1;",
                    connection);
                targetCategoryCheck.Parameters.AddWithValue("category_code", downloadCategoryCode);
                await using var targetCategoryReader = await targetCategoryCheck.ExecuteReaderAsync();
                Assert(await targetCategoryReader.ReadAsync(), "Downloaded category should exist globally.");
                Assert(
                    string.Equals(targetCategoryReader.GetString(0), mockState.DownloadCategoryName, StringComparison.Ordinal),
                    "Downloaded category name should match mock response.");
                Assert(!targetCategoryReader.GetBoolean(1), "Downloaded inactive category should propagate is_active = false.");
            }

            var runHistory = await service.GetInventorySyncRunHistoryAsync(targetCompanyId, 20);
            Assert(runHistory.Any(x => x.Id == downloadRunId && x.Direction.Equals("DOWNLOAD", StringComparison.OrdinalIgnoreCase)), "Download run history should be available.");

            var itemHistory = await service.GetInventorySyncItemLogHistoryAsync(targetCompanyId, 50);
            Assert(itemHistory.Any(x => x.CategoryCode.Equals(downloadCategoryCode, StringComparison.OrdinalIgnoreCase)), "Download item log history should include downloaded category.");
        }
        finally
        {
            if (serverCts is not null)
            {
                serverCts.Cancel();
            }

            if (listener is not null)
            {
                try { listener.Stop(); } catch { }
            }

            if (serverTask is not null)
            {
                try { await serverTask; } catch { }
            }

            try
            {
                await using var connection = await OpenConnectionAsync();

                foreach (var companyId in new[] { masterCompanyId, targetCompanyId })
                {
                    await CleanupInventoryArtifactsByCodesAsync(
                        connection,
                        companyId,
                        new[] { uploadCategoryCode, downloadCategoryCode },
                        new[] { uploadItemCode, downloadItemCode });
                }
            }
            finally
            {
                await RestoreCentralSyncSystemSettingsAsync(
                    oldMaster,
                    oldBaseUrl,
                    oldApiKey,
                    oldUploadPath,
                    oldDownloadPath,
                    oldTimeout);
            }
        }
    }

}

