using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<List<ManagedStockOpnameLine>> GenerateOpnameLinesFromStockAsync(
        long companyId,
        long locationId,
        long warehouseId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<ManagedStockOpnameLine>();
        if (companyId <= 0 || locationId <= 0 || warehouseId <= 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT s.item_id,
       i.item_code,
       i.item_name,
       i.uom,
       s.qty
FROM inv_stock s
JOIN inv_items i ON i.id = s.item_id
WHERE s.company_id = @company_id
  AND s.location_id = @location_id
  AND i.is_active = TRUE
  AND (s.warehouse_id = @warehouse_id OR s.warehouse_id IS NULL)
ORDER BY i.item_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var lineNo = 1;
        while (await reader.ReadAsync(cancellationToken))
        {
            var systemQty = reader.GetDecimal(4);
            output.Add(new ManagedStockOpnameLine
            {
                LineNo = lineNo++,
                ItemId = reader.GetInt64(0),
                ItemCode = reader.GetString(1),
                ItemName = reader.GetString(2),
                Uom = reader.GetString(3),
                SystemQty = systemQty,
                ActualQty = systemQty,
                DifferenceQty = 0
            });
        }

        return output;
    }

    public async Task<AccessOperationResult> SaveStockOpnameDraftAsync(
        ManagedStockOpname header,
        IReadOnlyCollection<ManagedStockOpnameLine> lines,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (header.CompanyId <= 0 || header.LocationId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan/lokasi stock opname tidak valid.");
        }

        var opnameNo = (header.OpnameNo ?? string.Empty).Trim().ToUpperInvariant();

        var normalizedLines = (lines ?? Array.Empty<ManagedStockOpnameLine>())
            .Where(line => line.ItemId > 0)
            .Select((line, index) =>
            {
                var systemQty = line.SystemQty;
                var actualQty = line.ActualQty;
                return new ManagedStockOpnameLine
                {
                    LineNo = index + 1,
                    ItemId = line.ItemId,
                    SystemQty = systemQty,
                    ActualQty = actualQty,
                    DifferenceQty = actualQty - systemQty,
                    Notes = (line.Notes ?? string.Empty).Trim()
                };
            })
            .ToList();

        if (normalizedLines.Count == 0)
        {
            return new AccessOperationResult(false, "Minimal satu baris item wajib diisi.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var itemIds = normalizedLines.Select(x => x.ItemId).Distinct().ToArray();
            var itemMap = await LoadActiveItemMapAsync(connection, transaction, header.CompanyId, itemIds, cancellationToken);
            if (itemMap.Count != itemIds.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Terdapat item tidak valid atau nonaktif pada detail opname.");
            }

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                InventoryModuleCode,
                InventorySubmoduleStockOpname,
                ResolveWriteAction(header.Id),
                header.CompanyId,
                header.LocationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola stock opname.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var opnameDate = header.OpnameDate.Date;
            var description = (header.Description ?? string.Empty).Trim();
            object warehouseIdParam = header.WarehouseId.HasValue && header.WarehouseId.Value > 0
                ? header.WarehouseId.Value
                : DBNull.Value;

            long opnameId;
            if (header.Id <= 0)
            {
                if (string.IsNullOrWhiteSpace(opnameNo))
                {
                    opnameNo = await GenerateStockOpnameNoAsync(
                        connection,
                        transaction,
                        header.CompanyId,
                        opnameDate,
                        cancellationToken);
                }

                await using var insertHeader = new NpgsqlCommand(@"
INSERT INTO inv_stock_opname (
    company_id,
    location_id,
    opname_no,
    opname_date,
    warehouse_id,
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
    @opname_no,
    @opname_date,
    @warehouse_id,
    @description,
    'DRAFT',
    TRUE,
    @actor,
    NOW(),
    @actor,
    NOW())
RETURNING id;", connection, transaction);
                insertHeader.Parameters.AddWithValue("company_id", header.CompanyId);
                insertHeader.Parameters.AddWithValue("location_id", header.LocationId);
                insertHeader.Parameters.AddWithValue("opname_no", opnameNo);
                insertHeader.Parameters.AddWithValue("opname_date", opnameDate);
                insertHeader.Parameters.Add(new NpgsqlParameter("warehouse_id", NpgsqlDbType.Bigint) { Value = warehouseIdParam });
                insertHeader.Parameters.AddWithValue("description", description);
                insertHeader.Parameters.AddWithValue("actor", actor);
                opnameId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                string? currentStatus = null;
                string? currentOpnameNo = null;
                await using (var lockHeader = new NpgsqlCommand(@"
SELECT status, opname_no
FROM inv_stock_opname
WHERE id = @id
  AND company_id = @company_id
  AND location_id = @location_id
FOR UPDATE;", connection, transaction))
                {
                    lockHeader.Parameters.AddWithValue("id", header.Id);
                    lockHeader.Parameters.AddWithValue("company_id", header.CompanyId);
                    lockHeader.Parameters.AddWithValue("location_id", header.LocationId);
                    await using var reader = await lockHeader.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        currentStatus = reader.GetString(0);
                        currentOpnameNo = reader.GetString(1);
                    }
                }

                if (string.IsNullOrWhiteSpace(currentStatus))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Data stock opname tidak ditemukan.");
                }

                if (!string.Equals(currentStatus, "DRAFT", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Hanya stock opname DRAFT yang dapat diubah.");
                }

                if (string.IsNullOrWhiteSpace(opnameNo))
                {
                    opnameNo = (currentOpnameNo ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(opnameNo))
                    {
                        opnameNo = await GenerateStockOpnameNoAsync(
                            connection,
                            transaction,
                            header.CompanyId,
                            opnameDate,
                            cancellationToken);
                    }
                }

                await using var updateHeader = new NpgsqlCommand(@"
UPDATE inv_stock_opname
SET opname_no = @opname_no,
    opname_date = @opname_date,
    warehouse_id = @warehouse_id,
    description = @description,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                updateHeader.Parameters.AddWithValue("id", header.Id);
                updateHeader.Parameters.AddWithValue("opname_no", opnameNo);
                updateHeader.Parameters.AddWithValue("opname_date", opnameDate);
                updateHeader.Parameters.Add(new NpgsqlParameter("warehouse_id", NpgsqlDbType.Bigint) { Value = warehouseIdParam });
                updateHeader.Parameters.AddWithValue("description", description);
                updateHeader.Parameters.AddWithValue("updated_by", actor);
                await updateHeader.ExecuteNonQueryAsync(cancellationToken);

                opnameId = header.Id;
            }

            await using (var clearLines = new NpgsqlCommand(
                "DELETE FROM inv_stock_opname_lines WHERE opname_id = @opname_id;",
                connection,
                transaction))
            {
                clearLines.Parameters.AddWithValue("opname_id", opnameId);
                await clearLines.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in normalizedLines)
            {
                await using var insertLine = new NpgsqlCommand(@"
INSERT INTO inv_stock_opname_lines (
    opname_id,
    line_no,
    item_id,
    system_qty,
    actual_qty,
    difference_qty,
    notes,
    created_at)
VALUES (
    @opname_id,
    @line_no,
    @item_id,
    @system_qty,
    @actual_qty,
    @difference_qty,
    @notes,
    NOW());", connection, transaction);
                insertLine.Parameters.AddWithValue("opname_id", opnameId);
                insertLine.Parameters.AddWithValue("line_no", line.LineNo);
                insertLine.Parameters.AddWithValue("item_id", line.ItemId);
                insertLine.Parameters.AddWithValue("system_qty", line.SystemQty);
                insertLine.Parameters.AddWithValue("actual_qty", line.ActualQty);
                insertLine.Parameters.AddWithValue("difference_qty", line.DifferenceQty);
                insertLine.Parameters.AddWithValue("notes", line.Notes ?? string.Empty);
                await insertLine.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_OPNAME",
                opnameId,
                "SAVE_DRAFT",
                actor,
                $"opname_no={opnameNo};company={header.CompanyId};location={header.LocationId};lines={normalizedLines.Count}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Draft stock opname berhasil disimpan.", opnameId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, "Nomor stock opname sudah digunakan pada perusahaan ini.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan draft stock opname: {ex.Message}");
        }
    }

    public async Task<List<ManagedStockOpname>> SearchStockOpnameAsync(
        long companyId,
        long locationId,
        string keyword,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<ManagedStockOpname>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var safeKeyword = (keyword ?? string.Empty).Trim();
        var keywordLike = string.IsNullOrWhiteSpace(safeKeyword) ? "%" : $"%{safeKeyword}%";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT o.id,
       o.company_id,
       o.location_id,
       o.opname_no,
       o.opname_date,
       o.warehouse_id,
       COALESCE(w.warehouse_name, '') AS warehouse_name,
       o.description,
       o.status
FROM inv_stock_opname o
LEFT JOIN inv_warehouses w ON w.id = o.warehouse_id
WHERE o.company_id = @company_id
  AND o.location_id = @location_id
  AND o.is_active = TRUE
  AND (
      @keyword = ''
      OR o.opname_no ILIKE @keyword_like
      OR o.description ILIKE @keyword_like
  )
ORDER BY o.opname_date DESC, o.id DESC
LIMIT 300;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("keyword", safeKeyword);
        command.Parameters.AddWithValue("keyword_like", keywordLike);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedStockOpname
            {
                Id = reader.GetInt64(0),
                CompanyId = reader.GetInt64(1),
                LocationId = reader.GetInt64(2),
                OpnameNo = reader.GetString(3),
                OpnameDate = reader.GetDateTime(4),
                WarehouseId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                WarehouseName = reader.GetString(6),
                Description = reader.GetString(7),
                Status = reader.GetString(8)
            });
        }

        return output;
    }

    public async Task<StockOpnameBundle?> GetStockOpnameBundleAsync(
        long opnameId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (opnameId <= 0)
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        ManagedStockOpname? header = null;
        await using (var headerCommand = new NpgsqlCommand(@"
SELECT o.id,
       o.company_id,
       o.location_id,
       o.opname_no,
       o.opname_date,
       o.warehouse_id,
       COALESCE(w.warehouse_name, '') AS warehouse_name,
       o.description,
       o.status
FROM inv_stock_opname o
LEFT JOIN inv_warehouses w ON w.id = o.warehouse_id
WHERE o.id = @id
  AND o.is_active = TRUE;", connection))
        {
            headerCommand.Parameters.AddWithValue("id", opnameId);
            await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                header = new ManagedStockOpname
                {
                    Id = reader.GetInt64(0),
                    CompanyId = reader.GetInt64(1),
                    LocationId = reader.GetInt64(2),
                    OpnameNo = reader.GetString(3),
                    OpnameDate = reader.GetDateTime(4),
                    WarehouseId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    WarehouseName = reader.GetString(6),
                    Description = reader.GetString(7),
                    Status = reader.GetString(8)
                };
            }
        }

        if (header is null)
        {
            return null;
        }

        var lines = new List<ManagedStockOpnameLine>();
        await using (var lineCommand = new NpgsqlCommand(@"
SELECT l.id,
       l.opname_id,
       l.line_no,
       l.item_id,
       i.item_code,
       i.item_name,
       i.uom,
       l.system_qty,
       l.actual_qty,
       l.difference_qty,
       COALESCE(l.notes, '')
FROM inv_stock_opname_lines l
JOIN inv_items i ON i.id = l.item_id
WHERE l.opname_id = @opname_id
ORDER BY l.line_no;", connection))
        {
            lineCommand.Parameters.AddWithValue("opname_id", opnameId);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new ManagedStockOpnameLine
                {
                    Id = reader.GetInt64(0),
                    OpnameId = reader.GetInt64(1),
                    LineNo = reader.GetInt32(2),
                    ItemId = reader.GetInt64(3),
                    ItemCode = reader.GetString(4),
                    ItemName = reader.GetString(5),
                    Uom = reader.GetString(6),
                    SystemQty = reader.GetDecimal(7),
                    ActualQty = reader.GetDecimal(8),
                    DifferenceQty = reader.GetDecimal(9),
                    Notes = reader.GetString(10)
                });
            }
        }

        return new StockOpnameBundle
        {
            Header = header,
            Lines = lines
        };
    }

    public async Task<AccessOperationResult> SubmitStockOpnameAsync(
        long opnameId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockOpnameStatusAsync(opnameId, "DRAFT", "SUBMITTED", actorUsername, cancellationToken);
    }

    public async Task<AccessOperationResult> ApproveStockOpnameAsync(
        long opnameId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockOpnameStatusAsync(opnameId, "SUBMITTED", "APPROVED", actorUsername, cancellationToken);
    }

    public async Task<AccessOperationResult> PostStockOpnameAsync(
        long opnameId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockOpnameStatusAsync(opnameId, "APPROVED", "POSTED", actorUsername, cancellationToken);
    }

    public async Task<InventoryDashboardData> GetInventoryDashboardDataAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0 || locationId <= 0)
        {
            return new InventoryDashboardData();
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        int totalItemCount;
        await using (var command = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM inv_items
WHERE is_active = TRUE;", connection))
        {
            totalItemCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        decimal totalStockValue;
        await using (var command = new NpgsqlCommand(@"
SELECT COALESCE(SUM(remaining_qty * unit_cost), 0)
FROM inv_cost_layers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND remaining_qty > 0;", connection))
        {
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("location_id", locationId);
            totalStockValue = Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
        }

        int lowStockCount;
        await using (var command = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM inv_stock s
JOIN inv_items i ON i.id = s.item_id
WHERE s.company_id = @company_id
  AND s.location_id = @location_id
  AND i.is_active = TRUE
  AND s.qty < @threshold;", connection))
        {
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("location_id", locationId);
            command.Parameters.AddWithValue("threshold", DefaultLowStockThreshold);
            lowStockCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        int pendingTransactionCount;
        await using (var command = new NpgsqlCommand(@"
SELECT
    (SELECT COUNT(1)
     FROM inv_stock_transactions
     WHERE company_id = @company_id
       AND location_id = @location_id
       AND is_active = TRUE
       AND status IN ('DRAFT','SUBMITTED','APPROVED'))
    +
    (SELECT COUNT(1)
     FROM inv_stock_opname
     WHERE company_id = @company_id
       AND location_id = @location_id
       AND is_active = TRUE
       AND status IN ('DRAFT','SUBMITTED','APPROVED'));", connection))
        {
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("location_id", locationId);
            pendingTransactionCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        var recentTransactions = new List<ManagedStockTransactionSummary>();
        await using (var command = new NpgsqlCommand(@"
SELECT h.id,
       h.transaction_no,
       h.transaction_type,
       h.transaction_date,
       COALESCE(w.warehouse_name, '') AS warehouse_name,
       h.reference_no,
       h.status,
       COALESCE(SUM(l.qty), 0) AS total_qty
FROM inv_stock_transactions h
LEFT JOIN inv_warehouses w ON w.id = h.warehouse_id
LEFT JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
  AND h.is_active = TRUE
GROUP BY h.id, h.transaction_no, h.transaction_type, h.transaction_date, w.warehouse_name, h.reference_no, h.status
ORDER BY h.transaction_date DESC, h.id DESC
LIMIT 12;", connection))
        {
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("location_id", locationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                recentTransactions.Add(new ManagedStockTransactionSummary
                {
                    Id = reader.GetInt64(0),
                    TransactionNo = reader.GetString(1),
                    TransactionType = reader.GetString(2),
                    TransactionDate = reader.GetDateTime(3),
                    WarehouseName = reader.GetString(4),
                    ReferenceNo = reader.GetString(5),
                    Status = reader.GetString(6),
                    TotalQty = reader.GetDecimal(7)
                });
            }
        }

        var lowStockItems = await GetLowStockAlertAsync(companyId, locationId, DefaultLowStockThreshold, cancellationToken);

        return new InventoryDashboardData
        {
            TotalItemCount = totalItemCount,
            TotalStockValue = totalStockValue,
            LowStockCount = lowStockCount,
            PendingTransactionCount = pendingTransactionCount,
            RecentTransactions = recentTransactions,
            LowStockItems = lowStockItems
        };
    }

    public async Task<List<StockMovementReportRow>> GetStockMovementReportAsync(
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<StockMovementReportRow>();
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
WITH tx_before AS (
    SELECT l.item_id,
           SUM(CASE
                 WHEN h.transaction_type = 'STOCK_IN' THEN l.qty
                 WHEN h.transaction_type = 'STOCK_OUT' THEN -l.qty
                 ELSE 0
               END) AS qty
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    WHERE h.company_id = @company_id
      AND h.location_id = @location_id
      AND h.status = 'POSTED'
      AND h.transaction_date < @date_from
    GROUP BY l.item_id
),
tx_period AS (
    SELECT l.item_id,
           SUM(CASE WHEN h.transaction_type = 'STOCK_IN' THEN l.qty ELSE 0 END) AS in_qty,
           SUM(CASE WHEN h.transaction_type = 'STOCK_OUT' THEN l.qty ELSE 0 END) AS out_qty
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    WHERE h.company_id = @company_id
      AND h.location_id = @location_id
      AND h.status = 'POSTED'
      AND h.transaction_date BETWEEN @date_from AND @date_to
    GROUP BY l.item_id
),
op_before AS (
    SELECT l.item_id,
           SUM(l.difference_qty) AS adjustment_qty
    FROM inv_stock_opname o
    JOIN inv_stock_opname_lines l ON l.opname_id = o.id
    WHERE o.company_id = @company_id
      AND o.location_id = @location_id
      AND o.status = 'POSTED'
      AND o.opname_date < @date_from
    GROUP BY l.item_id
),
op_period AS (
    SELECT l.item_id,
           SUM(l.difference_qty) AS adjustment_qty
    FROM inv_stock_opname o
    JOIN inv_stock_opname_lines l ON l.opname_id = o.id
    WHERE o.company_id = @company_id
      AND o.location_id = @location_id
      AND o.status = 'POSTED'
      AND o.opname_date BETWEEN @date_from AND @date_to
    GROUP BY l.item_id
)
SELECT i.item_code,
       i.item_name,
       i.uom,
       COALESCE(tb.qty, 0) + COALESCE(ob.adjustment_qty, 0) AS opening_qty,
       COALESCE(tp.in_qty, 0) AS in_qty,
       COALESCE(tp.out_qty, 0) AS out_qty,
       COALESCE(op.adjustment_qty, 0) AS adjustment_qty,
       (COALESCE(tb.qty, 0) + COALESCE(ob.adjustment_qty, 0) + COALESCE(tp.in_qty, 0) - COALESCE(tp.out_qty, 0) + COALESCE(op.adjustment_qty, 0)) AS closing_qty
FROM inv_items i
LEFT JOIN tx_before tb ON tb.item_id = i.id
LEFT JOIN tx_period tp ON tp.item_id = i.id
LEFT JOIN op_before ob ON ob.item_id = i.id
LEFT JOIN op_period op ON op.item_id = i.id
WHERE i.is_active = TRUE
  AND (
      COALESCE(tb.qty, 0) <> 0
      OR COALESCE(ob.adjustment_qty, 0) <> 0
      OR COALESCE(tp.in_qty, 0) <> 0
      OR COALESCE(tp.out_qty, 0) <> 0
      OR COALESCE(op.adjustment_qty, 0) <> 0
  )
ORDER BY i.item_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", from);
        command.Parameters.AddWithValue("date_to", to);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new StockMovementReportRow
            {
                ItemCode = reader.GetString(0),
                ItemName = reader.GetString(1),
                Uom = reader.GetString(2),
                OpeningQty = reader.GetDecimal(3),
                InQty = reader.GetDecimal(4),
                OutQty = reader.GetDecimal(5),
                AdjustmentQty = reader.GetDecimal(6),
                ClosingQty = reader.GetDecimal(7)
            });
        }

        return output;
    }

    public async Task<List<StockValuationRow>> GetStockValuationReportAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<StockValuationRow>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
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

        await using var command = new NpgsqlCommand(@"
WITH item_cost AS (
    SELECT item_id,
           SUM(remaining_qty) AS qty,
           SUM(remaining_qty * unit_cost) AS total_value
    FROM inv_cost_layers
    WHERE company_id = @company_id
      AND location_id = @location_id
      AND remaining_qty > 0
    GROUP BY item_id
)
SELECT i.item_code,
       i.item_name,
       i.uom,
       c.qty,
       CASE WHEN c.qty = 0 THEN 0 ELSE c.total_value / c.qty END AS avg_cost,
       c.total_value
FROM item_cost c
JOIN inv_items i ON i.id = c.item_id
WHERE i.is_active = TRUE
  AND c.qty <> 0
ORDER BY i.item_code;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new StockValuationRow
            {
                ItemCode = reader.GetString(0),
                ItemName = reader.GetString(1),
                Uom = reader.GetString(2),
                Qty = reader.GetDecimal(3),
                AvgCost = reader.GetDecimal(4),
                TotalValue = reader.GetDecimal(5)
            });
        }

        return output;
    }

    public async Task<List<ManagedStockEntry>> GetLowStockAlertAsync(
        long companyId,
        long locationId,
        decimal threshold,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<ManagedStockEntry>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var safeThreshold = threshold <= 0 ? DefaultLowStockThreshold : threshold;

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
       s.qty
FROM inv_stock s
JOIN inv_items i ON i.id = s.item_id
JOIN org_locations l ON l.id = s.location_id
WHERE s.company_id = @company_id
  AND s.location_id = @location_id
  AND i.is_active = TRUE
  AND s.qty < @threshold
ORDER BY s.qty ASC, i.item_code
LIMIT 200;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("threshold", safeThreshold);

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
                Qty = reader.GetDecimal(7)
            });
        }

        return output;
    }

    private async Task<AccessOperationResult> ChangeStockOpnameStatusAsync(
        long opnameId,
        string expectedCurrentStatus,
        string targetStatus,
        string actorUsername,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (opnameId <= 0)
        {
            return new AccessOperationResult(false, "Data stock opname tidak valid.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);

            var header = await GetStockOpnameHeaderForUpdateAsync(connection, transaction, opnameId, cancellationToken);
            if (header is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data stock opname tidak ditemukan.");
            }

            var permissionAction = ResolveWorkflowAction(targetStatus);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                InventoryModuleCode,
                InventorySubmoduleStockOpname,
                permissionAction,
                header.CompanyId,
                header.LocationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk aksi ini.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            if (!string.Equals(header.Status, expectedCurrentStatus, StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, $"Hanya stock opname {expectedCurrentStatus} yang dapat diubah ke {targetStatus}.");
            }

            if (string.Equals(targetStatus, "POSTED", StringComparison.OrdinalIgnoreCase))
            {
                var postingResult = await ApplyStockOpnamePostingAsync(connection, transaction, header, actor, cancellationToken);
                if (!postingResult.IsSuccess)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return postingResult;
                }
            }

            await using (var updateCommand = new NpgsqlCommand(@"
UPDATE inv_stock_opname
SET status = @target_status,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                updateCommand.Parameters.AddWithValue("id", opnameId);
                updateCommand.Parameters.AddWithValue("target_status", targetStatus);
                updateCommand.Parameters.AddWithValue("updated_by", actor);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_OPNAME",
                opnameId,
                targetStatus,
                actor,
                $"opname_no={header.OpnameNo};company={header.CompanyId};location={header.LocationId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, $"Stock opname {header.OpnameNo} berhasil di-{targetStatus.ToLowerInvariant()}.", opnameId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal mengubah status stock opname: {ex.Message}");
        }
    }

    private async Task<AccessOperationResult> ApplyStockOpnamePostingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StockOpnameHeaderSnapshot header,
        string actor,
        CancellationToken cancellationToken)
    {
        var lines = new List<(long LineId, long ItemId, string ItemCode, decimal DifferenceQty, string InventoryAccountCode)>();
        await using (var lineCommand = new NpgsqlCommand(@"
SELECT l.id,
       l.item_id,
       i.item_code,
       l.difference_qty,
       COALESCE(NULLIF(trim(c.account_code), ''), '') AS inventory_account_code
FROM inv_stock_opname_lines l
JOIN inv_items i ON i.id = l.item_id
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE l.opname_id = @opname_id
ORDER BY l.line_no;", connection, transaction))
        {
            lineCommand.Parameters.AddWithValue("opname_id", header.Id);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetDecimal(3), reader.GetString(4)));
            }
        }

        if (lines.Count == 0)
        {
            return new AccessOperationResult(false, "Detail stock opname tidak ditemukan. Posting dibatalkan.");
        }

        var costingSettings = await GetEffectiveInventoryCostingSettingsInternalAsync(
            connection,
            transaction,
            header.CompanyId,
            header.LocationId,
            cancellationToken);
        await EnsureInventoryCostingStateInitializedAsync(
            connection,
            transaction,
            header.CompanyId,
            actor,
            header.LocationId,
            cancellationToken);

        var method = NormalizeValuationMethod(costingSettings.ValuationMethod);
        var cogsAccountCode = NormalizeAccountCode(costingSettings.CogsAccountCode);
        if (lines.Any(line => line.DifferenceQty != 0) && string.IsNullOrWhiteSpace(cogsAccountCode))
        {
            return new AccessOperationResult(false, "Akun COGS belum diatur untuk lokasi ini.");
        }

        foreach (var line in lines)
        {
            if (line.DifferenceQty > 0)
            {
                if (string.IsNullOrWhiteSpace(line.InventoryAccountCode))
                {
                    return new AccessOperationResult(
                        false,
                        $"Akun inventory item {line.ItemCode} belum diisi (item/category).");
                }

                await AddStockQtyAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    header.WarehouseId,
                    line.DifferenceQty,
                    cancellationToken);

                var inboundUnitCost = await GetCurrentAverageCostFromLayersAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    cancellationToken);

                await AddInboundCostLayerAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceOpnamePlus,
                    header.Id,
                    line.LineId,
                    header.OpnameDate,
                    line.DifferenceQty,
                    inboundUnitCost,
                    method,
                    actor,
                    cancellationToken);

                var inboundUnitCostRounded = RoundCost(inboundUnitCost < 0 ? 0 : inboundUnitCost);
                await InsertOutboundCostEventAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceOpnamePlus,
                    header.Id,
                    line.LineId,
                    header.OpnameDate,
                    line.DifferenceQty,
                    inboundUnitCostRounded,
                    RoundCost(line.DifferenceQty * inboundUnitCostRounded),
                    method,
                    line.InventoryAccountCode,
                    cogsAccountCode,
                    cogsJournalId: null,
                    cancellationToken);
            }
            else if (line.DifferenceQty < 0)
            {
                if (string.IsNullOrWhiteSpace(line.InventoryAccountCode))
                {
                    return new AccessOperationResult(
                        false,
                        $"Akun inventory item {line.ItemCode} belum diisi (item/category).");
                }

                var qtyOut = Math.Abs(line.DifferenceQty);
                var costConsumption = await ConsumeCostFromLayersAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    qtyOut,
                    method,
                    allowShortage: false,
                    cancellationToken);
                if (!costConsumption.IsSuccess)
                {
                    return new AccessOperationResult(
                        false,
                        $"Costing opname item {line.ItemCode} gagal: {costConsumption.Message}");
                }

                var reduced = await ReduceStockQtyAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    header.WarehouseId,
                    qtyOut,
                    cancellationToken);
                if (!reduced)
                {
                    return new AccessOperationResult(false, $"Stok item {line.ItemCode} tidak mencukupi untuk posting penyesuaian opname.");
                }

                await InsertOutboundCostEventAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceOpnameMinus,
                    header.Id,
                    line.LineId,
                    header.OpnameDate,
                    qtyOut,
                    costConsumption.UnitCost,
                    costConsumption.TotalCost,
                    method,
                    line.InventoryAccountCode,
                    cogsAccountCode,
                    cogsJournalId: null,
                    cancellationToken);
            }
        }

        return new AccessOperationResult(true, "Posting stock opname berhasil.", header.Id);
    }

    private static async Task<StockOpnameHeaderSnapshot?> GetStockOpnameHeaderForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long opnameId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT id,
       company_id,
       location_id,
       opname_no,
       opname_date,
       warehouse_id,
       status
FROM inv_stock_opname
WHERE id = @id
  AND is_active = TRUE
FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("id", opnameId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StockOpnameHeaderSnapshot
        {
            Id = reader.GetInt64(0),
            CompanyId = reader.GetInt64(1),
            LocationId = reader.GetInt64(2),
            OpnameNo = reader.GetString(3),
            OpnameDate = reader.GetDateTime(4),
            WarehouseId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            Status = reader.GetString(6)
        };
    }

    private readonly record struct InventoryMasterWriteAccess(bool IsAllowed, string Message);

    private static async Task<long?> GetInventoryMasterCompanyIdInternalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT setting_value
FROM app_system_settings
WHERE setting_key = @setting_key
LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("setting_key", InventoryMasterCompanySettingKey);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar is DBNull)
        {
            return null;
        }

        var rawValue = Convert.ToString(scalar, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static async Task<InventoryMasterWriteAccess> ValidateInventoryMasterWriteAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        CancellationToken cancellationToken)
    {
        var masterCompanyId = await GetInventoryMasterCompanyIdInternalAsync(connection, transaction, cancellationToken);
        if (!masterCompanyId.HasValue || masterCompanyId.Value <= 0)
        {
            return new InventoryMasterWriteAccess(false, "Master company inventory belum dikonfigurasi di Settings.");
        }

        if (masterCompanyId.Value != companyId)
        {
            return new InventoryMasterWriteAccess(
                false,
                $"Company ini hanya dapat sync. CRUD item/kategori hanya di master company ({masterCompanyId.Value}).");
        }

        return new InventoryMasterWriteAccess(true, string.Empty);
    }

    private static string BuildOpeningBalanceValidationMessage(IReadOnlyCollection<InventoryImportError> errors)
    {
        var preview = errors
            .Take(5)
            .Select(x => $"{x.SheetName} r{(x.RowNumber <= 0 ? 1 : x.RowNumber)}: {x.Message}")
            .ToList();
        return $"Validasi saldo awal gagal ({errors.Count} error). {string.Join(" | ", preview)}";
    }

    private sealed class StockTransactionHeaderSnapshot
    {
        public long Id { get; init; }

        public long CompanyId { get; init; }

        public long LocationId { get; init; }

        public string TransactionNo { get; init; } = string.Empty;

        public string TransactionType { get; init; } = string.Empty;

        public DateTime TransactionDate { get; init; }

        public long? WarehouseId { get; init; }

        public long? DestinationWarehouseId { get; init; }

        public string Status { get; init; } = string.Empty;
    }

    private sealed class StockOpnameHeaderSnapshot
    {
        public long Id { get; init; }

        public long CompanyId { get; init; }

        public long LocationId { get; init; }

        public string OpnameNo { get; init; } = string.Empty;

        public DateTime OpnameDate { get; init; }

        public long? WarehouseId { get; init; }

        public string Status { get; init; } = string.Empty;
    }

    private sealed class OpeningBalanceImportRowBuffer
    {
        public int RowNumber { get; init; }

        public string LocationCode { get; init; } = string.Empty;

        public string ItemCode { get; init; } = string.Empty;

        public decimal Qty { get; init; }

        public decimal UnitCost { get; init; }

        public DateTime CutoffDate { get; init; }

        public string ReferenceNo { get; init; } = string.Empty;

        public string Notes { get; init; } = string.Empty;
    }

    private sealed class OpeningBalanceImportResolvedRow
    {
        public int RowNumber { get; init; }

        public long LocationId { get; init; }

        public long ItemId { get; init; }

        public string ItemCode { get; init; } = string.Empty;

        public decimal Qty { get; init; }

        public decimal UnitCost { get; init; }

        public DateTime CutoffDate { get; init; }

        public string ReferenceNo { get; init; } = string.Empty;

        public string Notes { get; init; } = string.Empty;
    }
}


