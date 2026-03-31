using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<AccessOperationResult> SyncCostCentersFromBlocksAsync(
        long companyId,
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        if (companyId <= 0 || locationId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan/lokasi cost center tidak valid.");
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
                locationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk sinkronisasi master cost center.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using (var tempCommand = new NpgsqlCommand(@"
CREATE TEMP TABLE tmp_block_cost_center_source (
    source_id BIGINT NOT NULL,
    company_id BIGINT NOT NULL,
    location_id BIGINT NOT NULL,
    estate_code VARCHAR(40) NOT NULL,
    estate_name VARCHAR(120) NOT NULL,
    division_code VARCHAR(40) NOT NULL,
    division_name VARCHAR(120) NOT NULL,
    block_code VARCHAR(40) NOT NULL,
    block_name VARCHAR(120) NOT NULL,
    cost_center_code VARCHAR(80) NOT NULL,
    cost_center_name VARCHAR(200) NOT NULL,
    is_active BOOLEAN NOT NULL,
    source_table VARCHAR(40) NOT NULL
) ON COMMIT DROP;", connection, transaction))
            {
                await tempCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var sourceCommand = new NpgsqlCommand(@"
INSERT INTO tmp_block_cost_center_source (
    source_id,
    company_id,
    location_id,
    estate_code,
    estate_name,
    division_code,
    division_name,
    block_code,
    block_name,
    cost_center_code,
    cost_center_name,
    is_active,
    source_table)
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
       coalesce(e.is_active, FALSE) AND coalesce(d.is_active, FALSE) AND coalesce(b.is_active, FALSE) AS is_active,
       'BLOCKS' AS source_table
FROM blocks b
JOIN divisions d ON d.id = b.division_id
JOIN estates e ON e.id = d.estate_id
WHERE e.company_id = @company_id
  AND e.location_id = @location_id
  AND btrim(coalesce(e.code, '')) <> ''
  AND btrim(coalesce(d.code, '')) <> ''
  AND btrim(coalesce(b.code, '')) <> '';", connection, transaction))
            {
                sourceCommand.Parameters.AddWithValue("company_id", companyId);
                sourceCommand.Parameters.AddWithValue("location_id", locationId);
                await sourceCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var sourceRowCount = 0;
            await using (var countCommand = new NpgsqlCommand(
                "SELECT COUNT(1) FROM tmp_block_cost_center_source;",
                connection,
                transaction))
            {
                sourceRowCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
            }

            await using (var adoptCommand = new NpgsqlCommand(@"
UPDATE gl_cost_centers cc
SET parent_id = NULL,
    cost_center_code = src.cost_center_code,
    cost_center_name = src.cost_center_name,
    estate_code = src.estate_code,
    estate_name = src.estate_name,
    division_code = src.division_code,
    division_name = src.division_name,
    block_code = src.block_code,
    block_name = src.block_name,
    level = 'BLOCK',
    is_posting = TRUE,
    is_active = src.is_active,
    sync_managed = TRUE,
    source_table = src.source_table,
    source_id = src.source_id,
    updated_by = @actor,
    updated_at = NOW()
FROM tmp_block_cost_center_source src
WHERE cc.company_id = src.company_id
  AND cc.location_id = src.location_id
  AND (cc.sync_managed = FALSE OR cc.source_id IS NULL OR btrim(coalesce(cc.source_table, '')) = '')
  AND (
      upper(cc.cost_center_code) = src.cost_center_code
      OR (
          upper(cc.estate_code) = src.estate_code
          AND upper(cc.division_code) = src.division_code
          AND upper(cc.block_code) = src.block_code
      )
  );", connection, transaction))
            {
                adoptCommand.Parameters.AddWithValue("actor", actor);
                await adoptCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var mergeCommand = new NpgsqlCommand(@"
MERGE INTO gl_cost_centers AS target
USING tmp_block_cost_center_source AS src
ON target.company_id = src.company_id
   AND target.location_id = src.location_id
   AND upper(coalesce(target.source_table, '')) = src.source_table
   AND target.source_id = src.source_id
WHEN MATCHED THEN
    UPDATE SET
        parent_id = NULL,
        cost_center_code = src.cost_center_code,
        cost_center_name = src.cost_center_name,
        estate_code = src.estate_code,
        estate_name = src.estate_name,
        division_code = src.division_code,
        division_name = src.division_name,
        block_code = src.block_code,
        block_name = src.block_name,
        level = 'BLOCK',
        is_posting = TRUE,
        is_active = src.is_active,
        sync_managed = TRUE,
        source_table = src.source_table,
        source_id = src.source_id,
        updated_by = @actor,
        updated_at = NOW()
WHEN NOT MATCHED THEN
    INSERT (
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
        sync_managed,
        source_table,
        source_id,
        created_by,
        created_at,
        updated_by,
        updated_at
    )
    VALUES (
        src.company_id,
        src.location_id,
        NULL,
        src.cost_center_code,
        src.cost_center_name,
        src.estate_code,
        src.estate_name,
        src.division_code,
        src.division_name,
        src.block_code,
        src.block_name,
        'BLOCK',
        TRUE,
        src.is_active,
        TRUE,
        src.source_table,
        src.source_id,
        @actor,
        NOW(),
        @actor,
        NOW()
    );", connection, transaction))
            {
                mergeCommand.Parameters.AddWithValue("actor", actor);
                await mergeCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var deactivatedCount = 0;
            await using (var deactivateCommand = new NpgsqlCommand(@"
UPDATE gl_cost_centers cc
SET is_active = FALSE,
    updated_by = @actor,
    updated_at = NOW()
WHERE cc.company_id = @company_id
  AND cc.location_id = @location_id
  AND cc.sync_managed = TRUE
  AND upper(coalesce(cc.source_table, '')) = 'BLOCKS'
  AND cc.is_active = TRUE
  AND NOT EXISTS (
      SELECT 1
      FROM tmp_block_cost_center_source src
      WHERE src.source_id = cc.source_id
  );", connection, transaction))
            {
                deactivateCommand.Parameters.AddWithValue("company_id", companyId);
                deactivateCommand.Parameters.AddWithValue("location_id", locationId);
                deactivateCommand.Parameters.AddWithValue("actor", actor);
                deactivatedCount = await deactivateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var managedCount = 0;
            await using (var managedCountCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM gl_cost_centers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND sync_managed = TRUE
  AND upper(coalesce(source_table, '')) = 'BLOCKS';", connection, transaction))
            {
                managedCountCommand.Parameters.AddWithValue("company_id", companyId);
                managedCountCommand.Parameters.AddWithValue("location_id", locationId);
                managedCount = Convert.ToInt32(await managedCountCommand.ExecuteScalarAsync(cancellationToken));
            }

            var activeManagedCount = 0;
            await using (var activeManagedCountCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM gl_cost_centers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND sync_managed = TRUE
  AND upper(coalesce(source_table, '')) = 'BLOCKS'
  AND is_active = TRUE;", connection, transaction))
            {
                activeManagedCountCommand.Parameters.AddWithValue("company_id", companyId);
                activeManagedCountCommand.Parameters.AddWithValue("location_id", locationId);
                activeManagedCount = Convert.ToInt32(await activeManagedCountCommand.ExecuteScalarAsync(cancellationToken));
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "COST_CENTER_SYNC",
                0,
                "SYNC_FROM_BLOCKS",
                actor,
                $"company={companyId};location={locationId};source_rows={sourceRowCount};managed_rows={managedCount};active_rows={activeManagedCount};deactivated={deactivatedCount}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(
                true,
                $"Sinkronisasi cost center dari master blok berhasil. Sumber={sourceRowCount}, aktif={activeManagedCount}, terkelola={managedCount}, dinonaktifkan={deactivatedCount}.");
        }
        catch (PostgresException ex)
        {
            LogServiceError(
                "SyncCostCentersFromBlocksFailed",
                $"action=sync_cost_centers_from_blocks status=db_error company_id={companyId} location_id={locationId}",
                ex);
            return new AccessOperationResult(false, "Gagal sinkronisasi cost center dari master blok.");
        }
        catch (Exception ex)
        {
            LogServiceError(
                "SyncCostCentersFromBlocksFailed",
                $"action=sync_cost_centers_from_blocks status=failed company_id={companyId} location_id={locationId}",
                ex);
            return new AccessOperationResult(false, "Gagal sinkronisasi cost center dari master blok.");
        }
    }
}
