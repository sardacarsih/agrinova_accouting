using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<InventoryCostingSettings> GetInventoryCostingSettingsAsync(
        long companyId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new InventoryCostingSettings
            {
                CompanyId = companyId,
                ValuationMethod = InventoryValuationMethodAverage,
                CogsAccountCode = string.Empty
            };
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await GetInventoryCostingSettingsInternalAsync(connection, null, companyId, cancellationToken);
    }

    public async Task<InventoryLocationCostingSettings> GetInventoryLocationCostingSettingsAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new InventoryLocationCostingSettings
        {
            CompanyId = companyId,
            LocationId = locationId,
            UseCompanyDefault = true,
            ValuationMethod = InventoryValuationMethodAverage,
            CogsAccountCode = string.Empty
        };
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var companySettings = await GetInventoryCostingSettingsInternalAsync(connection, null, companyId, cancellationToken);
        output.ValuationMethod = companySettings.ValuationMethod;
        output.CogsAccountCode = companySettings.CogsAccountCode;

        var locationSetting = await GetInventoryLocationCostingSettingRowAsync(
            connection,
            null,
            companyId,
            locationId,
            cancellationToken);
        if (locationSetting is not null)
        {
            output.UseCompanyDefault = false;
            output.ValuationMethod = locationSetting.ValuationMethod;
            output.CogsAccountCode = locationSetting.CogsAccountCode;
        }

        return output;
    }

    public async Task<AccessOperationResult> SaveInventoryCostingSettingsAsync(
        long companyId,
        InventoryCostingSettings settings,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Company tidak valid.");
        }

        if (settings is null)
        {
            return new AccessOperationResult(false, "Pengaturan costing inventory tidak valid.");
        }

        var normalizedMethod = NormalizeValuationMethod(settings.ValuationMethod);
        var normalizedCogsAccountCode = NormalizeAccountCode(settings.CogsAccountCode);

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
                PermissionActionUpdateSettings,
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengubah pengaturan costing inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var companyExists = await IsActiveCompanyExistsAsync(connection, transaction, companyId, cancellationToken);
            if (!companyExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Company tidak ditemukan atau tidak aktif.");
            }

            await UpsertCompanyCostingSettingsAsync(
                connection,
                transaction,
                companyId,
                normalizedMethod,
                normalizedCogsAccountCode,
                actor,
                cancellationToken);

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INVENTORY_SETTING",
                companyId,
                "SAVE_COSTING_SETTING_COMPANY",
                actor,
                $"company_id={companyId};valuation_method={normalizedMethod};cogs_account={normalizedCogsAccountCode}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new AccessOperationResult(
                true,
                "Pengaturan costing company disimpan. Jalankan Recalculate untuk menerapkan perubahan ke histori.",
                companyId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan pengaturan costing inventory: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SaveInventoryLocationCostingSettingsAsync(
        long companyId,
        InventoryLocationCostingSettings settings,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Company tidak valid.");
        }

        if (settings is null || settings.LocationId <= 0)
        {
            return new AccessOperationResult(false, "Pengaturan costing per lokasi tidak valid.");
        }

        var normalizedMethod = NormalizeValuationMethod(settings.ValuationMethod);
        var normalizedCogsAccountCode = NormalizeAccountCode(settings.CogsAccountCode);

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
                PermissionActionUpdateSettings,
                companyId,
                settings.LocationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengubah pengaturan costing inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var companyExists = await IsActiveCompanyExistsAsync(connection, transaction, companyId, cancellationToken);
            if (!companyExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Company tidak ditemukan atau tidak aktif.");
            }

            var locationValid = await IsActiveLocationBelongsToCompanyAsync(
                connection,
                transaction,
                companyId,
                settings.LocationId,
                cancellationToken);
            if (!locationValid)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Lokasi tidak ditemukan, tidak aktif, atau bukan milik company terpilih.");
            }

            if (settings.UseCompanyDefault)
            {
                await using (var deleteCommand = new NpgsqlCommand(@"
DELETE FROM inv_location_costing_settings
WHERE company_id = @company_id
  AND location_id = @location_id;", connection, transaction))
                {
                    deleteCommand.Parameters.AddWithValue("company_id", companyId);
                    deleteCommand.Parameters.AddWithValue("location_id", settings.LocationId);
                    await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertAuditLogAsync(
                    connection,
                    transaction,
                    "INVENTORY_SETTING",
                    companyId,
                    "SAVE_COSTING_SETTING_LOCATION_RESET",
                    actor,
                    $"company_id={companyId};location_id={settings.LocationId};use_company_default=true",
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return new AccessOperationResult(true, "Override costing lokasi dihapus. Lokasi kembali menggunakan default company.", settings.LocationId);
            }

            await using (var upsertCommand = new NpgsqlCommand(@"
INSERT INTO inv_location_costing_settings (
    company_id,
    location_id,
    valuation_method,
    cogs_account_code,
    updated_by,
    updated_at
)
VALUES (
    @company_id,
    @location_id,
    @valuation_method,
    @cogs_account_code,
    @updated_by,
    NOW()
)
ON CONFLICT (company_id, location_id) DO UPDATE
SET valuation_method = EXCLUDED.valuation_method,
    cogs_account_code = EXCLUDED.cogs_account_code,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();", connection, transaction))
            {
                upsertCommand.Parameters.AddWithValue("company_id", companyId);
                upsertCommand.Parameters.AddWithValue("location_id", settings.LocationId);
                upsertCommand.Parameters.AddWithValue("valuation_method", normalizedMethod);
                upsertCommand.Parameters.AddWithValue("cogs_account_code", normalizedCogsAccountCode);
                upsertCommand.Parameters.AddWithValue("updated_by", actor);
                await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INVENTORY_SETTING",
                companyId,
                "SAVE_COSTING_SETTING_LOCATION",
                actor,
                $"company_id={companyId};location_id={settings.LocationId};valuation_method={normalizedMethod};cogs_account={normalizedCogsAccountCode}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(
                true,
                "Pengaturan costing lokasi disimpan. Jalankan Recalculate Location untuk menerapkan perubahan ke histori lokasi ini.",
                settings.LocationId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan pengaturan costing lokasi: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> RecalculateInventoryCostingAsync(
        long companyId,
        long? locationId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Company tidak valid.");
        }

        if (locationId.HasValue && locationId.Value <= 0)
        {
            return new AccessOperationResult(false, "Lokasi tidak valid.");
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
                PermissionActionUpdateSettings,
                companyId,
                locationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk menjalankan recalculate costing inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var companyExists = await IsActiveCompanyExistsAsync(connection, transaction, companyId, cancellationToken);
            if (!companyExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Company tidak ditemukan atau tidak aktif.");
            }

            if (locationId.HasValue)
            {
                var locationValid = await IsActiveLocationBelongsToCompanyAsync(
                    connection,
                    transaction,
                    companyId,
                    locationId.Value,
                    cancellationToken);
                if (!locationValid)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Lokasi tidak ditemukan, tidak aktif, atau bukan milik company terpilih.");
                }
            }

            var targetLocationIds = await LoadTargetLocationIdsAsync(
                connection,
                transaction,
                companyId,
                locationId,
                cancellationToken);
            if (targetLocationIds.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Tidak ada lokasi aktif yang bisa diproses untuk recalculation.");
            }

            var recalcRunId = await CreateInventoryCostRecalcRunAsync(
                connection,
                transaction,
                companyId,
                locationId,
                actor,
                cancellationToken);

            await EnsureInventoryCostingStateInitializedAsync(
                connection,
                transaction,
                companyId,
                actor,
                locationId,
                cancellationToken);

            var beforeSnapshot = await SnapshotInventoryValuationByLocationAccountAsync(
                connection,
                transaction,
                companyId,
                locationId,
                cancellationToken);

            await RebuildInventoryCostingStateAsync(
                connection,
                transaction,
                companyId,
                actor,
                locationId,
                cancellationToken);

            var afterSnapshot = await SnapshotInventoryValuationByLocationAccountAsync(
                connection,
                transaction,
                companyId,
                locationId,
                cancellationToken);

            var valuationDiffByLocation = BuildValuationDiffByLocation(beforeSnapshot, afterSnapshot);
            var effectiveSettingsByLocation = await LoadEffectiveCostingSettingsByLocationAsync(
                connection,
                transaction,
                companyId,
                targetLocationIds,
                cancellationToken);

            var pendingAdjustmentDocumentCount = 0;
            IReadOnlyCollection<string> pendingAdjustmentDocumentRefs = [];
            if (valuationDiffByLocation.Count > 0)
            {
                var adjustmentResult = await SaveInventoryValuationAdjustmentEventsAsync(
                    connection,
                    transaction,
                    companyId,
                    recalcRunId,
                    valuationDiffByLocation,
                    effectiveSettingsByLocation,
                    cancellationToken);
                if (!adjustmentResult.IsSuccess)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, adjustmentResult.Message);
                }

                pendingAdjustmentDocumentCount = adjustmentResult.DocumentCount;
                pendingAdjustmentDocumentRefs = adjustmentResult.ReferenceNos;
            }

            await UpdateInventoryCostRecalcRunAsync(
                connection,
                transaction,
                recalcRunId,
                status: "SUCCESS",
                targetLocationIds.Count,
                pendingAdjustmentDocumentCount,
                pendingAdjustmentDocumentRefs,
                "Recalculation completed.",
                cancellationToken);

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INVENTORY_SETTING",
                companyId,
                "RECALCULATE_COSTING",
                actor,
                $"company_id={companyId};scope={(locationId.HasValue ? "LOCATION" : "COMPANY")};location_id={(locationId ?? 0)};pending_adjustment_document_count={pendingAdjustmentDocumentCount};recalc_run_id={recalcRunId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var scopeLabel = locationId.HasValue ? "location" : "company";
            var successMessage = pendingAdjustmentDocumentCount > 0
                ? $"Recalculate costing {scopeLabel} selesai. Dokumen adjustment pending: {string.Join(", ", pendingAdjustmentDocumentRefs)}."
                : $"Recalculate costing {scopeLabel} selesai tanpa selisih nilai.";
            return new AccessOperationResult(true, successMessage, locationId ?? companyId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menjalankan recalculation costing inventory: {ex.Message}");
        }
    }

    private static async Task<InventoryCostingSettings> GetInventoryCostingSettingsInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long companyId,
        CancellationToken cancellationToken)
    {
        var output = new InventoryCostingSettings
        {
            CompanyId = companyId,
            ValuationMethod = InventoryValuationMethodAverage,
            CogsAccountCode = string.Empty
        };

        if (companyId <= 0)
        {
            return output;
        }

        await using var command = new NpgsqlCommand(@"
SELECT valuation_method, cogs_account_code
FROM inv_company_settings
WHERE company_id = @company_id
LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            output.ValuationMethod = NormalizeValuationMethod(reader.GetString(0));
            output.CogsAccountCode = reader.IsDBNull(1)
                ? string.Empty
                : NormalizeAccountCode(reader.GetString(1));
        }

        return output;
    }

    private static async Task<InventoryLocationCostingSettings?> GetInventoryLocationCostingSettingRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long companyId,
        long locationId,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0 || locationId <= 0)
        {
            return null;
        }

        await using var command = new NpgsqlCommand(@"
SELECT valuation_method, cogs_account_code
FROM inv_location_costing_settings
WHERE company_id = @company_id
  AND location_id = @location_id
LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InventoryLocationCostingSettings
        {
            CompanyId = companyId,
            LocationId = locationId,
            UseCompanyDefault = false,
            ValuationMethod = NormalizeValuationMethod(reader.GetString(0)),
            CogsAccountCode = reader.IsDBNull(1) ? string.Empty : NormalizeAccountCode(reader.GetString(1))
        };
    }

    private async Task<InventoryCostingSettings> GetEffectiveInventoryCostingSettingsInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long companyId,
        long locationId,
        CancellationToken cancellationToken)
    {
        var companySettings = await GetInventoryCostingSettingsInternalAsync(connection, transaction, companyId, cancellationToken);
        var locationSettings = await GetInventoryLocationCostingSettingRowAsync(
            connection,
            transaction,
            companyId,
            locationId,
            cancellationToken);
        if (locationSettings is null || locationSettings.UseCompanyDefault)
        {
            return companySettings;
        }

        return new InventoryCostingSettings
        {
            CompanyId = companyId,
            ValuationMethod = locationSettings.ValuationMethod,
            CogsAccountCode = locationSettings.CogsAccountCode
        };
    }

    private async Task<Dictionary<long, InventoryCostingSettings>> LoadEffectiveCostingSettingsByLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        IReadOnlyCollection<long> locationIds,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<long, InventoryCostingSettings>();
        if (locationIds.Count == 0)
        {
            return output;
        }

        var companySettings = await GetInventoryCostingSettingsInternalAsync(connection, transaction, companyId, cancellationToken);
        foreach (var locationId in locationIds)
        {
            output[locationId] = new InventoryCostingSettings
            {
                CompanyId = companyId,
                ValuationMethod = companySettings.ValuationMethod,
                CogsAccountCode = companySettings.CogsAccountCode
            };
        }

        await using var command = new NpgsqlCommand(@"
SELECT location_id, valuation_method, cogs_account_code
FROM inv_location_costing_settings
WHERE company_id = @company_id
  AND location_id = ANY(@location_ids);", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_ids", locationIds.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var locationId = reader.GetInt64(0);
            output[locationId] = new InventoryCostingSettings
            {
                CompanyId = companyId,
                ValuationMethod = NormalizeValuationMethod(reader.GetString(1)),
                CogsAccountCode = reader.IsDBNull(2) ? string.Empty : NormalizeAccountCode(reader.GetString(2))
            };
        }

        return output;
    }

    private static async Task UpsertCompanyCostingSettingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string valuationMethod,
        string cogsAccountCode,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var upsertCommand = new NpgsqlCommand(@"
INSERT INTO inv_company_settings (
    company_id,
    valuation_method,
    cogs_account_code,
    updated_by,
    updated_at
)
VALUES (
    @company_id,
    @valuation_method,
    @cogs_account_code,
    @updated_by,
    NOW()
)
ON CONFLICT (company_id) DO UPDATE
SET valuation_method = EXCLUDED.valuation_method,
    cogs_account_code = EXCLUDED.cogs_account_code,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();", connection, transaction);
        upsertCommand.Parameters.AddWithValue("company_id", companyId);
        upsertCommand.Parameters.AddWithValue("valuation_method", valuationMethod);
        upsertCommand.Parameters.AddWithValue("cogs_account_code", cogsAccountCode);
        upsertCommand.Parameters.AddWithValue("updated_by", actor);
        await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsActiveCompanyExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        CancellationToken cancellationToken)
    {
        await using var companyCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM org_companies
WHERE id = @company_id
  AND is_active = TRUE;", connection, transaction);
        companyCommand.Parameters.AddWithValue("company_id", companyId);
        return Convert.ToInt32(await companyCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> IsActiveLocationBelongsToCompanyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        CancellationToken cancellationToken)
    {
        await using var locationCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM org_locations
WHERE id = @location_id
  AND company_id = @company_id
  AND is_active = TRUE;", connection, transaction);
        locationCommand.Parameters.AddWithValue("location_id", locationId);
        locationCommand.Parameters.AddWithValue("company_id", companyId);
        return Convert.ToInt32(await locationCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<List<long>> LoadTargetLocationIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long? locationId,
        CancellationToken cancellationToken)
    {
        var output = new List<long>();
        await using var command = new NpgsqlCommand(@"
SELECT id
FROM org_locations
WHERE company_id = @company_id
  AND (@location_id IS NULL OR id = @location_id)
ORDER BY id;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
        {
            Value = locationId.HasValue ? locationId.Value : DBNull.Value
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(reader.GetInt64(0));
        }

        return output;
    }

    private static async Task<long> CreateInventoryCostRecalcRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long? locationId,
        string actor,
        CancellationToken cancellationToken)
    {
        var scope = locationId.HasValue ? "LOCATION" : "COMPANY";
        await using var command = new NpgsqlCommand(@"
INSERT INTO inv_cost_recalc_runs (
    company_id,
    location_id,
    scope,
    status,
    actor_username,
    started_at,
    affected_location_count,
    adjustment_journal_count,
    adjustment_journal_nos,
    message
)
VALUES (
    @company_id,
    @location_id,
    @scope,
    'RUNNING',
    @actor_username,
    NOW(),
    0,
    0,
    '',
    ''
)
RETURNING id;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
        {
            Value = locationId.HasValue ? locationId.Value : DBNull.Value
        });
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("actor_username", actor);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task UpdateInventoryCostRecalcRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long runId,
        string status,
        int affectedLocationCount,
        int adjustmentDocumentCount,
        IReadOnlyCollection<string> referenceNos,
        string message,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
UPDATE inv_cost_recalc_runs
SET status = @status,
    ended_at = NOW(),
    affected_location_count = @affected_location_count,
    adjustment_journal_count = @adjustment_document_count,
    adjustment_journal_nos = @reference_nos,
    message = @message
WHERE id = @id;", connection, transaction);
        command.Parameters.AddWithValue("id", runId);
        command.Parameters.AddWithValue("status", (status ?? string.Empty).Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("affected_location_count", affectedLocationCount);
        command.Parameters.AddWithValue("adjustment_document_count", adjustmentDocumentCount);
        command.Parameters.AddWithValue("reference_nos", string.Join(", ", referenceNos ?? []));
        command.Parameters.AddWithValue("message", (message ?? string.Empty).Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeValuationMethod(string? valuationMethod)
    {
        var normalized = (valuationMethod ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            InventoryValuationMethodAverage => InventoryValuationMethodAverage,
            InventoryValuationMethodFifo => InventoryValuationMethodFifo,
            InventoryValuationMethodLifo => InventoryValuationMethodLifo,
            _ => InventoryValuationMethodAverage
        };
    }

    private static string NormalizeAccountCode(string? accountCode)
    {
        return (accountCode ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static decimal RoundCost(decimal value)
    {
        return Math.Round(value, 4, MidpointRounding.AwayFromZero);
    }

    private static decimal RoundAmount(decimal value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }
}


