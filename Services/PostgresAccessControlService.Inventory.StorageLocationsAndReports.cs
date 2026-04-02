using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<AccessOperationResult> SaveStorageLocationAsync(
        long companyId,
        ManagedStorageLocation storageLocation,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan tidak valid.");
        }

        if (storageLocation is null ||
            storageLocation.WarehouseId <= 0 ||
            string.IsNullOrWhiteSpace(storageLocation.Code) ||
            string.IsNullOrWhiteSpace(storageLocation.Name))
        {
            return new AccessOperationResult(false, "Gudang, kode, dan nama lokasi penyimpanan wajib diisi.");
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
                ResolveWriteAction(storageLocation.Id),
                companyId,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola lokasi penyimpanan inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var warehouseMap = await LoadActiveWarehouseMapAsync(
                connection,
                transaction,
                companyId,
                new[] { storageLocation.WarehouseId },
                cancellationToken);
            if (!warehouseMap.TryGetValue(storageLocation.WarehouseId, out var warehouse))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Gudang lokasi penyimpanan tidak valid atau nonaktif.");
            }

            var normalizedCode = storageLocation.Code.Trim().ToUpperInvariant();
            var normalizedName = storageLocation.Name.Trim();
            object locationIdValue = storageLocation.LocationId.HasValue && storageLocation.LocationId.Value > 0
                ? storageLocation.LocationId.Value
                : warehouse.LocationId.HasValue && warehouse.LocationId.Value > 0
                    ? warehouse.LocationId.Value
                    : DBNull.Value;

            if (warehouse.LocationId.HasValue &&
                locationIdValue is long locationId &&
                locationId != warehouse.LocationId.Value)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Lokasi penyimpanan harus mengikuti lokasi gudang yang dipilih.");
            }

            long storageLocationId;
            if (storageLocation.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO inv_storage_locations (
    company_id,
    location_id,
    warehouse_id,
    storage_code,
    storage_name,
    is_active,
    created_by,
    created_at,
    updated_at)
VALUES (
    @company_id,
    @location_id,
    @warehouse_id,
    @storage_code,
    @storage_name,
    @is_active,
    @created_by,
    NOW(),
    NOW())
RETURNING id;", connection, transaction);
                insertCommand.Parameters.AddWithValue("company_id", companyId);
                insertCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationIdValue });
                insertCommand.Parameters.AddWithValue("warehouse_id", storageLocation.WarehouseId);
                insertCommand.Parameters.AddWithValue("storage_code", normalizedCode);
                insertCommand.Parameters.AddWithValue("storage_name", normalizedName);
                insertCommand.Parameters.AddWithValue("is_active", storageLocation.IsActive);
                insertCommand.Parameters.AddWithValue("created_by", actor);
                storageLocationId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE inv_storage_locations
SET location_id = @location_id,
    warehouse_id = @warehouse_id,
    storage_code = @storage_code,
    storage_name = @storage_name,
    is_active = @is_active,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
                updateCommand.Parameters.AddWithValue("id", storageLocation.Id);
                updateCommand.Parameters.AddWithValue("company_id", companyId);
                updateCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint) { Value = locationIdValue });
                updateCommand.Parameters.AddWithValue("warehouse_id", storageLocation.WarehouseId);
                updateCommand.Parameters.AddWithValue("storage_code", normalizedCode);
                updateCommand.Parameters.AddWithValue("storage_name", normalizedName);
                updateCommand.Parameters.AddWithValue("is_active", storageLocation.IsActive);
                updateCommand.Parameters.AddWithValue("updated_by", actor);
                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Data lokasi penyimpanan tidak ditemukan.");
                }

                storageLocationId = storageLocation.Id;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_STORAGE_LOCATION",
                storageLocationId,
                storageLocation.Id <= 0 ? "CREATE" : "UPDATE",
                actor,
                $"company={companyId};warehouse_id={storageLocation.WarehouseId};code={normalizedCode};name={normalizedName};active={storageLocation.IsActive}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, $"Lokasi penyimpanan '{normalizedCode}' berhasil disimpan.", storageLocationId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, $"Kode lokasi penyimpanan '{storageLocation.Code.Trim().ToUpperInvariant()}' sudah digunakan pada gudang ini.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan lokasi penyimpanan: {ex.Message}");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteStorageLocationAsync(
        long companyId,
        long storageLocationId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0 || storageLocationId <= 0)
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
                "Anda tidak memiliki izin untuk mengelola lokasi penyimpanan inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using var command = new NpgsqlCommand(@"
UPDATE inv_storage_locations
SET is_active = FALSE,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id
  AND company_id = @company_id;", connection, transaction);
            command.Parameters.AddWithValue("id", storageLocationId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("updated_by", actor);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected <= 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data lokasi penyimpanan tidak ditemukan.");
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_STORAGE_LOCATION",
                storageLocationId,
                "DEACTIVATE",
                actor,
                $"company={companyId};storage_location_id={storageLocationId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Lokasi penyimpanan berhasil dinonaktifkan.", storageLocationId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menonaktifkan lokasi penyimpanan: {ex.Message}");
        }
    }

    public async Task<List<ManagedStockEntry>> GetStockPositionReportAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<ManagedStockEntry>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
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
  AND s.location_id = @location_id
  AND i.is_active = TRUE
ORDER BY i.item_code, w.warehouse_name, l.code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedStockEntry
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

        return output;
    }

    public async Task<List<InventoryTransactionHistoryRow>> GetInventoryTransactionHistoryAsync(
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<InventoryTransactionHistoryRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var from = dateFrom.Date;
        var to = dateTo.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT history.tx_date,
       history.document_no,
       history.document_type,
       history.status,
       history.warehouse_name,
       history.destination_warehouse_name,
       history.item_code,
       history.item_name,
       history.uom,
       history.qty,
       history.effect_qty,
       history.reference_no,
       history.description
FROM (
    SELECT h.transaction_date AS tx_date,
           h.transaction_no AS document_no,
           CASE h.transaction_type
               WHEN 'STOCK_IN' THEN 'Barang Masuk'
               WHEN 'STOCK_OUT' THEN 'Barang Keluar'
               ELSE 'Transfer'
           END AS document_type,
           h.status,
           COALESCE(ws.warehouse_name, '') AS warehouse_name,
           COALESCE(wd.warehouse_name, '') AS destination_warehouse_name,
           i.item_code,
           i.item_name,
           i.uom,
           l.qty,
           CASE
               WHEN h.transaction_type = 'STOCK_IN' THEN l.qty
               WHEN h.transaction_type = 'STOCK_OUT' THEN -l.qty
               ELSE 0
           END AS effect_qty,
           COALESCE(h.reference_no, '') AS reference_no,
           COALESCE(NULLIF(h.description, ''), COALESCE(l.notes, '')) AS description,
           h.id AS sort_id,
           l.line_no AS sort_line
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    JOIN inv_items i ON i.id = l.item_id
    LEFT JOIN inv_warehouses ws ON ws.id = l.warehouse_id
    LEFT JOIN inv_warehouses wd ON wd.id = l.destination_warehouse_id
    WHERE h.company_id = @company_id
      AND h.location_id = @location_id
      AND h.is_active = TRUE
      AND h.transaction_date BETWEEN @date_from AND @date_to

    UNION ALL

    SELECT o.opname_date AS tx_date,
           o.opname_no AS document_no,
           'Stock Opname' AS document_type,
           o.status,
           COALESCE(w.warehouse_name, '') AS warehouse_name,
           '' AS destination_warehouse_name,
           i.item_code,
           i.item_name,
           i.uom,
           l.actual_qty AS qty,
           l.difference_qty AS effect_qty,
           '' AS reference_no,
           COALESCE(NULLIF(o.description, ''), COALESCE(l.notes, '')) AS description,
           o.id AS sort_id,
           l.line_no AS sort_line
    FROM inv_stock_opname o
    JOIN inv_stock_opname_lines l ON l.opname_id = o.id
    JOIN inv_items i ON i.id = l.item_id
    LEFT JOIN inv_warehouses w ON w.id = o.warehouse_id
    WHERE o.company_id = @company_id
      AND o.location_id = @location_id
      AND o.is_active = TRUE
      AND o.opname_date BETWEEN @date_from AND @date_to

    UNION ALL

    SELECT a.adjustment_date AS tx_date,
           a.adjustment_no AS document_no,
           'Stock Adjustment' AS document_type,
           a.status,
           COALESCE(w.warehouse_name, '') AS warehouse_name,
           '' AS destination_warehouse_name,
           i.item_code,
           i.item_name,
           i.uom,
           ABS(l.qty_adjustment) AS qty,
           l.qty_adjustment AS effect_qty,
           COALESCE(a.reference_no, '') AS reference_no,
           COALESCE(NULLIF(a.description, ''), COALESCE(l.notes, '')) AS description,
           a.id AS sort_id,
           l.line_no AS sort_line
    FROM inv_stock_adjustments a
    JOIN inv_stock_adjustment_lines l ON l.adjustment_id = a.id
    JOIN inv_items i ON i.id = l.item_id
    LEFT JOIN inv_warehouses w ON w.id = a.warehouse_id
    WHERE a.company_id = @company_id
      AND a.location_id = @location_id
      AND a.is_active = TRUE
      AND a.adjustment_date BETWEEN @date_from AND @date_to
) history
ORDER BY history.tx_date DESC, history.document_no DESC, history.sort_id DESC, history.sort_line DESC;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", from);
        command.Parameters.AddWithValue("date_to", to);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new InventoryTransactionHistoryRow
            {
                TxDate = reader.GetDateTime(0),
                DocumentNo = reader.GetString(1),
                DocumentType = reader.GetString(2),
                Status = reader.GetString(3),
                WarehouseName = reader.GetString(4),
                DestinationWarehouseName = reader.GetString(5),
                ItemCode = reader.GetString(6),
                ItemName = reader.GetString(7),
                Uom = reader.GetString(8),
                Qty = reader.GetDecimal(9),
                EffectQty = reader.GetDecimal(10),
                ReferenceNo = reader.GetString(11),
                Description = reader.GetString(12)
            });
        }

        return output;
    }

    public async Task<List<InventoryStockCardRow>> GetInventoryStockCardAsync(
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<InventoryStockCardRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var from = dateFrom.Date;
        var to = dateTo.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
WITH movement_rows AS (
    SELECT h.transaction_date AS tx_date,
           h.transaction_no AS document_no,
           'Barang Masuk' AS document_type,
           l.item_id,
           l.warehouse_id,
           l.line_no,
           10 AS sort_order,
           l.qty AS in_qty,
           0::numeric AS out_qty,
           0::numeric AS adjustment_qty,
           COALESCE(h.reference_no, '') AS reference_no,
           COALESCE(NULLIF(h.description, ''), COALESCE(l.notes, '')) AS description
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    WHERE h.company_id = @company_id
      AND h.location_id = @location_id
      AND h.status = 'POSTED'
      AND h.transaction_type = 'STOCK_IN'
      AND l.warehouse_id IS NOT NULL

    UNION ALL

    SELECT h.transaction_date AS tx_date,
           h.transaction_no AS document_no,
           'Barang Keluar' AS document_type,
           l.item_id,
           l.warehouse_id,
           l.line_no,
           20 AS sort_order,
           0::numeric AS in_qty,
           l.qty AS out_qty,
           0::numeric AS adjustment_qty,
           COALESCE(h.reference_no, '') AS reference_no,
           COALESCE(NULLIF(h.description, ''), COALESCE(l.notes, '')) AS description
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    WHERE h.company_id = @company_id
      AND h.location_id = @location_id
      AND h.status = 'POSTED'
      AND h.transaction_type = 'STOCK_OUT'
      AND l.warehouse_id IS NOT NULL

    UNION ALL

    SELECT h.transaction_date AS tx_date,
           h.transaction_no AS document_no,
           'Transfer Keluar' AS document_type,
           l.item_id,
           l.warehouse_id,
           l.line_no,
           30 AS sort_order,
           0::numeric AS in_qty,
           l.qty AS out_qty,
           0::numeric AS adjustment_qty,
           COALESCE(h.reference_no, '') AS reference_no,
           COALESCE(NULLIF(h.description, ''), COALESCE(l.notes, '')) AS description
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    WHERE h.company_id = @company_id
      AND h.location_id = @location_id
      AND h.status = 'POSTED'
      AND h.transaction_type = 'TRANSFER'
      AND l.warehouse_id IS NOT NULL

    UNION ALL

    SELECT h.transaction_date AS tx_date,
           h.transaction_no AS document_no,
           'Transfer Masuk' AS document_type,
           l.item_id,
           l.destination_warehouse_id AS warehouse_id,
           l.line_no,
           40 AS sort_order,
           l.qty AS in_qty,
           0::numeric AS out_qty,
           0::numeric AS adjustment_qty,
           COALESCE(h.reference_no, '') AS reference_no,
           COALESCE(NULLIF(h.description, ''), COALESCE(l.notes, '')) AS description
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    WHERE h.company_id = @company_id
      AND h.location_id = @location_id
      AND h.status = 'POSTED'
      AND h.transaction_type = 'TRANSFER'
      AND l.destination_warehouse_id IS NOT NULL

    UNION ALL

    SELECT o.opname_date AS tx_date,
           o.opname_no AS document_no,
           'Stock Opname' AS document_type,
           l.item_id,
           o.warehouse_id,
           l.line_no,
           50 AS sort_order,
           0::numeric AS in_qty,
           0::numeric AS out_qty,
           l.difference_qty AS adjustment_qty,
           '' AS reference_no,
           COALESCE(NULLIF(o.description, ''), COALESCE(l.notes, '')) AS description
    FROM inv_stock_opname o
    JOIN inv_stock_opname_lines l ON l.opname_id = o.id
    WHERE o.company_id = @company_id
      AND o.location_id = @location_id
      AND o.status = 'POSTED'
      AND o.warehouse_id IS NOT NULL
      AND l.difference_qty <> 0

    UNION ALL

    SELECT a.adjustment_date AS tx_date,
           a.adjustment_no AS document_no,
           'Stock Adjustment' AS document_type,
           l.item_id,
           a.warehouse_id,
           l.line_no,
           60 AS sort_order,
           0::numeric AS in_qty,
           0::numeric AS out_qty,
           l.qty_adjustment AS adjustment_qty,
           COALESCE(a.reference_no, '') AS reference_no,
           COALESCE(NULLIF(a.description, ''), COALESCE(l.notes, '')) AS description
    FROM inv_stock_adjustments a
    JOIN inv_stock_adjustment_lines l ON l.adjustment_id = a.id
    WHERE a.company_id = @company_id
      AND a.location_id = @location_id
      AND a.status = 'POSTED'
      AND a.warehouse_id IS NOT NULL
      AND l.qty_adjustment <> 0
),
opening_balance AS (
    SELECT item_id,
           warehouse_id,
           SUM(in_qty - out_qty + adjustment_qty) AS opening_qty
    FROM movement_rows
    WHERE tx_date < @date_from
    GROUP BY item_id, warehouse_id
),
period_rows AS (
    SELECT *
    FROM movement_rows
    WHERE tx_date BETWEEN @date_from AND @date_to
)
SELECT pr.tx_date,
       pr.document_no,
       pr.document_type,
       COALESCE(w.warehouse_name, '') AS warehouse_name,
       i.item_code,
       i.item_name,
       i.uom,
       COALESCE(ob.opening_qty, 0)
           + COALESCE(SUM(pr.in_qty - pr.out_qty + pr.adjustment_qty) OVER (
               PARTITION BY pr.item_id, pr.warehouse_id
               ORDER BY pr.tx_date, pr.document_no, pr.sort_order, pr.line_no
               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS opening_qty,
       pr.in_qty,
       pr.out_qty,
       pr.adjustment_qty,
       COALESCE(ob.opening_qty, 0)
           + SUM(pr.in_qty - pr.out_qty + pr.adjustment_qty) OVER (
               PARTITION BY pr.item_id, pr.warehouse_id
               ORDER BY pr.tx_date, pr.document_no, pr.sort_order, pr.line_no
               ROWS UNBOUNDED PRECEDING) AS balance_qty,
       pr.reference_no,
       pr.description
FROM period_rows pr
JOIN inv_items i ON i.id = pr.item_id
LEFT JOIN inv_warehouses w ON w.id = pr.warehouse_id
LEFT JOIN opening_balance ob
       ON ob.item_id = pr.item_id
      AND ob.warehouse_id IS NOT DISTINCT FROM pr.warehouse_id
WHERE i.is_active = TRUE
ORDER BY i.item_code, warehouse_name, pr.tx_date, pr.document_no, pr.sort_order, pr.line_no;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", from);
        command.Parameters.AddWithValue("date_to", to);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new InventoryStockCardRow
            {
                TxDate = reader.GetDateTime(0),
                DocumentNo = reader.GetString(1),
                DocumentType = reader.GetString(2),
                WarehouseName = reader.GetString(3),
                ItemCode = reader.GetString(4),
                ItemName = reader.GetString(5),
                Uom = reader.GetString(6),
                OpeningQty = reader.GetDecimal(7),
                InQty = reader.GetDecimal(8),
                OutQty = reader.GetDecimal(9),
                AdjustmentQty = reader.GetDecimal(10),
                BalanceQty = reader.GetDecimal(11),
                ReferenceNo = reader.GetString(12),
                Description = reader.GetString(13)
            });
        }

        return output;
    }

    public async Task<List<InventoryStockOpnameReportRow>> GetStockOpnameReportAsync(
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<InventoryStockOpnameReportRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var from = dateFrom.Date;
        var to = dateTo.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT o.opname_date,
       o.opname_no,
       o.status,
       COALESCE(w.warehouse_name, '') AS warehouse_name,
       i.item_code,
       i.item_name,
       i.uom,
       l.system_qty,
       l.actual_qty,
       l.difference_qty,
       COALESCE(o.description, '') AS description,
       COALESCE(l.notes, '') AS notes
FROM inv_stock_opname o
JOIN inv_stock_opname_lines l ON l.opname_id = o.id
JOIN inv_items i ON i.id = l.item_id
LEFT JOIN inv_warehouses w ON w.id = o.warehouse_id
WHERE o.company_id = @company_id
  AND o.location_id = @location_id
  AND o.is_active = TRUE
  AND o.opname_date BETWEEN @date_from AND @date_to
ORDER BY o.opname_date DESC, o.opname_no DESC, l.line_no;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", from);
        command.Parameters.AddWithValue("date_to", to);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new InventoryStockOpnameReportRow
            {
                OpnameDate = reader.GetDateTime(0),
                OpnameNo = reader.GetString(1),
                Status = reader.GetString(2),
                WarehouseName = reader.GetString(3),
                ItemCode = reader.GetString(4),
                ItemName = reader.GetString(5),
                Uom = reader.GetString(6),
                SystemQty = reader.GetDecimal(7),
                ActualQty = reader.GetDecimal(8),
                DifferenceQty = reader.GetDecimal(9),
                Description = reader.GetString(10),
                Notes = reader.GetString(11)
            });
        }

        return output;
    }
}
