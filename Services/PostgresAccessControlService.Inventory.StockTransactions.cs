using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<AccessOperationResult> SaveStockTransactionDraftAsync(
        ManagedStockTransaction header,
        IReadOnlyCollection<ManagedStockTransactionLine> lines,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (header.CompanyId <= 0 || header.LocationId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan/lokasi transaksi tidak valid.");
        }

        var transactionType = NormalizeTransactionType(header.TransactionType);
        if (string.IsNullOrWhiteSpace(transactionType))
        {
            return new AccessOperationResult(false, "Jenis transaksi tidak valid.");
        }

        var transactionNo = (header.TransactionNo ?? string.Empty).Trim().ToUpperInvariant();

        var normalizedLines = (lines ?? Array.Empty<ManagedStockTransactionLine>())
            .Where(line => line.ItemId > 0 && line.Qty > 0)
            .Select((line, index) => new ManagedStockTransactionLine
            {
                LineNo = index + 1,
                ItemId = line.ItemId,
                Qty = line.Qty,
                UnitCost = line.UnitCost,
                WarehouseId = line.WarehouseId.HasValue && line.WarehouseId.Value > 0 ? line.WarehouseId.Value : null,
                DestinationWarehouseId = line.DestinationWarehouseId.HasValue && line.DestinationWarehouseId.Value > 0 ? line.DestinationWarehouseId.Value : null,
                ExpenseAccountCode = (line.ExpenseAccountCode ?? string.Empty).Trim().ToUpperInvariant(),
                Notes = (line.Notes ?? string.Empty).Trim()
            })
            .ToList();

        if (normalizedLines.Count == 0)
        {
            return new AccessOperationResult(false, "Minimal satu baris item dengan qty > 0 wajib diisi.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var transactionSubmodule = ResolveInventoryTransactionSubmodule(transactionType);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                InventoryModuleCode,
                transactionSubmodule,
                ResolveWriteAction(header.Id),
                header.CompanyId,
                header.LocationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola transaksi inventory.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var itemIds = normalizedLines.Select(x => x.ItemId).Distinct().ToArray();
            var itemMap = await LoadActiveItemMapAsync(connection, transaction, header.CompanyId, itemIds, cancellationToken);
            if (itemMap.Count != itemIds.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Terdapat item tidak valid atau nonaktif pada detail transaksi.");
            }

            var warehouseIds = normalizedLines
                .SelectMany(line => new[] { line.WarehouseId, line.DestinationWarehouseId })
                .Where(warehouseId => warehouseId.HasValue && warehouseId.Value > 0)
                .Select(warehouseId => warehouseId.GetValueOrDefault())
                .Distinct()
                .ToArray();
            var warehouseMap = await LoadActiveWarehouseMapAsync(connection, transaction, header.CompanyId, warehouseIds, cancellationToken);
            if (warehouseMap.Count != warehouseIds.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Terdapat gudang tidak valid atau nonaktif pada detail transaksi.");
            }

            foreach (var line in normalizedLines)
            {
                var itemLabel = itemMap.TryGetValue(line.ItemId, out var itemInfo) ? itemInfo.Code : $"#{line.ItemId}";

                if (string.Equals(transactionType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
                {
                    if (!line.WarehouseId.HasValue || line.WarehouseId.Value <= 0)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Gudang asal item {itemLabel} wajib diisi.");
                    }

                    if (!line.DestinationWarehouseId.HasValue || line.DestinationWarehouseId.Value <= 0)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Gudang tujuan item {itemLabel} wajib diisi.");
                    }

                    if (line.WarehouseId.Value == line.DestinationWarehouseId.Value)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Gudang asal dan tujuan item {itemLabel} tidak boleh sama.");
                    }
                }
                else if (!line.WarehouseId.HasValue || line.WarehouseId.Value <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, $"Gudang item {itemLabel} wajib diisi.");
                }
            }

            if (string.Equals(transactionType, "STOCK_OUT", StringComparison.OrdinalIgnoreCase))
            {
                var expenseAccountCodeSet = await LoadExpensePostingAccountCodeSetAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    cancellationToken);
                if (expenseAccountCodeSet.Count == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "COA akun beban posting tidak ditemukan untuk company ini.");
                }

                foreach (var line in normalizedLines)
                {
                    var itemLabel = itemMap.TryGetValue(line.ItemId, out var itemInfo) ? itemInfo.Code : $"#{line.ItemId}";
                    if (string.IsNullOrWhiteSpace(line.ExpenseAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Akun beban GL item {itemLabel} wajib diisi.");
                    }

                    if (!expenseAccountCodeSet.Contains(line.ExpenseAccountCode))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, $"Akun beban GL '{line.ExpenseAccountCode}' untuk item {itemLabel} tidak valid/aktif.");
                    }
                }
            }
            var transactionDate = header.TransactionDate.Date;
            var referenceNo = (header.ReferenceNo ?? string.Empty).Trim();
            var description = (header.Description ?? string.Empty).Trim();
            object warehouseIdParam = DBNull.Value;
            object destinationWarehouseIdParam = DBNull.Value;

            long transactionId;
            if (header.Id <= 0)
            {
                if (string.IsNullOrWhiteSpace(transactionNo))
                {
                    transactionNo = await GenerateStockTransactionNoAsync(
                        connection,
                        transaction,
                        header.CompanyId,
                        transactionType,
                        transactionDate,
                        cancellationToken);
                }

                await using var insertHeader = new NpgsqlCommand(@"
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
    @transaction_type,
    @transaction_date,
    @warehouse_id,
    @destination_warehouse_id,
    @reference_no,
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
                insertHeader.Parameters.AddWithValue("transaction_no", transactionNo);
                insertHeader.Parameters.AddWithValue("transaction_type", transactionType);
                insertHeader.Parameters.AddWithValue("transaction_date", transactionDate);
                insertHeader.Parameters.Add(new NpgsqlParameter("warehouse_id", NpgsqlDbType.Bigint) { Value = warehouseIdParam });
                insertHeader.Parameters.Add(new NpgsqlParameter("destination_warehouse_id", NpgsqlDbType.Bigint) { Value = destinationWarehouseIdParam });
                insertHeader.Parameters.AddWithValue("reference_no", referenceNo);
                insertHeader.Parameters.AddWithValue("description", description);
                insertHeader.Parameters.AddWithValue("actor", actor);
                transactionId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                string? currentStatus = null;
                string? currentTransactionNo = null;
                await using (var lockHeader = new NpgsqlCommand(@"
SELECT status, transaction_no
FROM inv_stock_transactions
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
                        currentTransactionNo = reader.GetString(1);
                    }
                }

                if (string.IsNullOrWhiteSpace(currentStatus))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Data transaksi tidak ditemukan.");
                }

                if (!string.Equals(currentStatus, "DRAFT", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Hanya transaksi DRAFT yang dapat diubah.");
                }

                if (string.IsNullOrWhiteSpace(transactionNo))
                {
                    transactionNo = (currentTransactionNo ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(transactionNo))
                    {
                        transactionNo = await GenerateStockTransactionNoAsync(
                            connection,
                            transaction,
                            header.CompanyId,
                            transactionType,
                            transactionDate,
                            cancellationToken);
                    }
                }

                await using var updateHeader = new NpgsqlCommand(@"
UPDATE inv_stock_transactions
SET transaction_no = @transaction_no,
    transaction_type = @transaction_type,
    transaction_date = @transaction_date,
    warehouse_id = @warehouse_id,
    destination_warehouse_id = @destination_warehouse_id,
    reference_no = @reference_no,
    description = @description,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                updateHeader.Parameters.AddWithValue("id", header.Id);
                updateHeader.Parameters.AddWithValue("transaction_no", transactionNo);
                updateHeader.Parameters.AddWithValue("transaction_type", transactionType);
                updateHeader.Parameters.AddWithValue("transaction_date", transactionDate);
                updateHeader.Parameters.Add(new NpgsqlParameter("warehouse_id", NpgsqlDbType.Bigint) { Value = warehouseIdParam });
                updateHeader.Parameters.Add(new NpgsqlParameter("destination_warehouse_id", NpgsqlDbType.Bigint) { Value = destinationWarehouseIdParam });
                updateHeader.Parameters.AddWithValue("reference_no", referenceNo);
                updateHeader.Parameters.AddWithValue("description", description);
                updateHeader.Parameters.AddWithValue("updated_by", actor);
                await updateHeader.ExecuteNonQueryAsync(cancellationToken);

                transactionId = header.Id;
            }

            await using (var clearLines = new NpgsqlCommand(
                "DELETE FROM inv_stock_transaction_lines WHERE transaction_id = @transaction_id;",
                connection,
                transaction))
            {
                clearLines.Parameters.AddWithValue("transaction_id", transactionId);
                await clearLines.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in normalizedLines)
            {
                await using var insertLine = new NpgsqlCommand(@"
INSERT INTO inv_stock_transaction_lines (
    transaction_id,
    line_no,
    item_id,
    qty,
    unit_cost,
    warehouse_id,
    destination_warehouse_id,
    expense_account_code,
    notes,
    created_at)
VALUES (
    @transaction_id,
    @line_no,
    @item_id,
    @qty,
    @unit_cost,
    @warehouse_id,
    @destination_warehouse_id,
    @expense_account_code,
    @notes,
    NOW());", connection, transaction);
                insertLine.Parameters.AddWithValue("transaction_id", transactionId);
                insertLine.Parameters.AddWithValue("line_no", line.LineNo);
                insertLine.Parameters.AddWithValue("item_id", line.ItemId);
                insertLine.Parameters.AddWithValue("qty", line.Qty);
                insertLine.Parameters.AddWithValue("unit_cost", line.UnitCost);
                insertLine.Parameters.Add(new NpgsqlParameter("warehouse_id", NpgsqlDbType.Bigint) { Value = line.WarehouseId.HasValue ? line.WarehouseId.Value : DBNull.Value });
                insertLine.Parameters.Add(new NpgsqlParameter("destination_warehouse_id", NpgsqlDbType.Bigint) { Value = line.DestinationWarehouseId.HasValue ? line.DestinationWarehouseId.Value : DBNull.Value });
                insertLine.Parameters.AddWithValue("expense_account_code", line.ExpenseAccountCode ?? string.Empty);
                insertLine.Parameters.AddWithValue("notes", line.Notes ?? string.Empty);
                await insertLine.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_STOCK_TX",
                transactionId,
                "SAVE_DRAFT",
                actor,
                $"transaction_no={transactionNo};type={transactionType};company={header.CompanyId};location={header.LocationId};lines={normalizedLines.Count}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Draft transaksi stok berhasil disimpan.", transactionId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, "Nomor transaksi sudah digunakan pada perusahaan ini.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan draft transaksi stok: {ex.Message}");
        }
    }

    public async Task<List<ManagedStockTransactionSummary>> SearchStockTransactionsAsync(
        long companyId,
        long locationId,
        InventoryTransactionSearchFilter filter,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<ManagedStockTransactionSummary>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var safeFilter = filter ?? new InventoryTransactionSearchFilter();
        var keyword = (safeFilter.Keyword ?? string.Empty).Trim();
        var keywordLike = string.IsNullOrWhiteSpace(keyword) ? "%" : $"%{keyword}%";
        var statusFilter = (safeFilter.Status ?? string.Empty).Trim().ToUpperInvariant();
        var typeFilter = (safeFilter.TransactionType ?? string.Empty).Trim().ToUpperInvariant();
        var dateFrom = safeFilter.DateFrom?.Date;
        var dateTo = safeFilter.DateTo?.Date;

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT h.id,
       h.transaction_no,
       h.transaction_type,
       h.transaction_date,
       COALESCE(warehouse_summary.warehouse_name, '') AS warehouse_name,
       h.reference_no,
       h.status,
       COALESCE(SUM(l.qty), 0) AS total_qty
FROM inv_stock_transactions h
LEFT JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
LEFT JOIN LATERAL (
    SELECT CASE
               WHEN COUNT(DISTINCT x.display_name) > 1 THEN 'Multi Gudang'
               ELSE COALESCE(MAX(x.display_name), '')
           END AS warehouse_name,
           COALESCE(string_agg(DISTINCT x.search_text, ' '), '') AS warehouse_search
    FROM (
        SELECT COALESCE(NULLIF(trim(w.warehouse_name), ''), trim(COALESCE(w.warehouse_code, ''))) AS display_name,
               trim(concat_ws(' ', COALESCE(w.warehouse_name, ''), COALESCE(w.warehouse_code, ''))) AS search_text
        FROM inv_stock_transaction_lines line
        LEFT JOIN inv_warehouses w ON w.id = line.warehouse_id
        WHERE line.transaction_id = h.id
          AND line.warehouse_id IS NOT NULL
        UNION
        SELECT COALESCE(NULLIF(trim(dw.warehouse_name), ''), trim(COALESCE(dw.warehouse_code, ''))) AS display_name,
               trim(concat_ws(' ', COALESCE(dw.warehouse_name, ''), COALESCE(dw.warehouse_code, ''))) AS search_text
        FROM inv_stock_transaction_lines line
        LEFT JOIN inv_warehouses dw ON dw.id = line.destination_warehouse_id
        WHERE line.transaction_id = h.id
          AND line.destination_warehouse_id IS NOT NULL
    ) x
) warehouse_summary ON TRUE
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
  AND h.is_active = TRUE
  AND (@type_filter = '' OR upper(h.transaction_type) = @type_filter)
  AND (@status_filter = '' OR upper(h.status) = @status_filter)
  AND (@date_from IS NULL OR h.transaction_date >= @date_from)
  AND (@date_to IS NULL OR h.transaction_date <= @date_to)
  AND (
        @keyword = ''
        OR h.transaction_no ILIKE @keyword_like
        OR h.reference_no ILIKE @keyword_like
        OR h.description ILIKE @keyword_like
        OR warehouse_summary.warehouse_search ILIKE @keyword_like
      )
GROUP BY h.id, h.transaction_no, h.transaction_type, h.transaction_date, warehouse_summary.warehouse_name, h.reference_no, h.status
ORDER BY h.transaction_date DESC, h.id DESC
LIMIT 300;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("type_filter", typeFilter);
        command.Parameters.AddWithValue("status_filter", statusFilter);
        command.Parameters.Add(new NpgsqlParameter("date_from", NpgsqlDbType.Date) { Value = (object?)dateFrom ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("date_to", NpgsqlDbType.Date) { Value = (object?)dateTo ?? DBNull.Value });
        command.Parameters.AddWithValue("keyword", keyword);
        command.Parameters.AddWithValue("keyword_like", keywordLike);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedStockTransactionSummary
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

        return output;
    }

    public async Task<StockTransactionBundle?> GetStockTransactionBundleAsync(
        long transactionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (transactionId <= 0)
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        ManagedStockTransaction? header = null;
        await using (var headerCommand = new NpgsqlCommand(@"
SELECT h.id,
       h.company_id,
       h.location_id,
       h.transaction_no,
       h.transaction_type,
       h.transaction_date,
       h.warehouse_id,
       COALESCE(w.warehouse_name, '') AS warehouse_name,
       h.destination_warehouse_id,
       COALESCE(dw.warehouse_name, '') AS destination_warehouse_name,
       h.reference_no,
       h.description,
       h.status
FROM inv_stock_transactions h
LEFT JOIN inv_warehouses w ON w.id = h.warehouse_id
LEFT JOIN inv_warehouses dw ON dw.id = h.destination_warehouse_id
WHERE h.id = @id
  AND h.is_active = TRUE;", connection))
        {
            headerCommand.Parameters.AddWithValue("id", transactionId);
            await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                header = new ManagedStockTransaction
                {
                    Id = reader.GetInt64(0),
                    CompanyId = reader.GetInt64(1),
                    LocationId = reader.GetInt64(2),
                    TransactionNo = reader.GetString(3),
                    TransactionType = reader.GetString(4),
                    TransactionDate = reader.GetDateTime(5),
                    WarehouseId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    WarehouseName = reader.GetString(7),
                    DestinationWarehouseId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    DestinationWarehouseName = reader.GetString(9),
                    ReferenceNo = reader.GetString(10),
                    Description = reader.GetString(11),
                    Status = reader.GetString(12)
                };
            }
        }

        if (header is null)
        {
            return null;
        }

        var lines = new List<ManagedStockTransactionLine>();
        await using (var lineCommand = new NpgsqlCommand(@"
SELECT l.id,
       l.transaction_id,
       l.line_no,
       l.item_id,
       i.item_code,
       i.item_name,
       i.uom,
       l.qty,
       l.unit_cost,
       l.warehouse_id,
       COALESCE(w.warehouse_name, '') AS warehouse_name,
       l.destination_warehouse_id,
       COALESCE(dw.warehouse_name, '') AS destination_warehouse_name,
       COALESCE(l.expense_account_code, ''),
       COALESCE(l.notes, '')
FROM inv_stock_transaction_lines l
JOIN inv_items i ON i.id = l.item_id
LEFT JOIN inv_warehouses w ON w.id = l.warehouse_id
LEFT JOIN inv_warehouses dw ON dw.id = l.destination_warehouse_id
WHERE l.transaction_id = @transaction_id
ORDER BY l.line_no;", connection))
        {
            lineCommand.Parameters.AddWithValue("transaction_id", transactionId);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new ManagedStockTransactionLine
                {
                    Id = reader.GetInt64(0),
                    TransactionId = reader.GetInt64(1),
                    LineNo = reader.GetInt32(2),
                    ItemId = reader.GetInt64(3),
                    ItemCode = reader.GetString(4),
                    ItemName = reader.GetString(5),
                    Uom = reader.GetString(6),
                    Qty = reader.GetDecimal(7),
                    UnitCost = reader.GetDecimal(8),
                    WarehouseId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    WarehouseName = reader.GetString(10),
                    DestinationWarehouseId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    DestinationWarehouseName = reader.GetString(12),
                    ExpenseAccountCode = reader.GetString(13),
                    Notes = reader.GetString(14)
                });
            }
        }

        return new StockTransactionBundle
        {
            Header = header,
            Lines = lines
        };
    }

    public async Task<AccessOperationResult> SubmitStockTransactionAsync(
        long transactionId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockTransactionStatusAsync(transactionId, "DRAFT", "SUBMITTED", actorUsername, cancellationToken);
    }

    public async Task<AccessOperationResult> ApproveStockTransactionAsync(
        long transactionId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockTransactionStatusAsync(transactionId, "SUBMITTED", "APPROVED", actorUsername, cancellationToken);
    }

    public async Task<AccessOperationResult> PostStockTransactionAsync(
        long transactionId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockTransactionStatusAsync(transactionId, "APPROVED", "POSTED", actorUsername, cancellationToken);
    }

    private async Task<AccessOperationResult> ChangeStockTransactionStatusAsync(
        long transactionId,
        string expectedCurrentStatus,
        string targetStatus,
        string actorUsername,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (transactionId <= 0)
        {
            return new AccessOperationResult(false, "Data transaksi tidak valid.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);

            var header = await GetStockTransactionHeaderForUpdateAsync(connection, transaction, transactionId, cancellationToken);
            if (header is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data transaksi tidak ditemukan.");
            }

            var permissionAction = ResolveWorkflowAction(targetStatus);
            var permissionSubmodule = ResolveInventoryTransactionSubmodule(header.TransactionType);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                InventoryModuleCode,
                permissionSubmodule,
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
                return new AccessOperationResult(false, $"Hanya transaksi {expectedCurrentStatus} yang dapat diubah ke {targetStatus}.");
            }

            if (string.Equals(targetStatus, "POSTED", StringComparison.OrdinalIgnoreCase))
            {
                var postingResult = await ApplyStockTransactionPostingAsync(connection, transaction, header, actor, cancellationToken);
                if (!postingResult.IsSuccess)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return postingResult;
                }
            }

            await using (var updateCommand = new NpgsqlCommand(@"
UPDATE inv_stock_transactions
SET status = @target_status,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                updateCommand.Parameters.AddWithValue("id", transactionId);
                updateCommand.Parameters.AddWithValue("target_status", targetStatus);
                updateCommand.Parameters.AddWithValue("updated_by", actor);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_STOCK_TX",
                transactionId,
                targetStatus,
                actor,
                $"transaction_no={header.TransactionNo};type={header.TransactionType};company={header.CompanyId};location={header.LocationId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, $"Transaksi {header.TransactionNo} berhasil di-{targetStatus.ToLowerInvariant()}.", transactionId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal mengubah status transaksi: {ex.Message}");
        }
    }

    private async Task<AccessOperationResult> ApplyStockTransactionPostingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StockTransactionHeaderSnapshot header,
        string actor,
        CancellationToken cancellationToken)
    {
        var lines = new List<(long LineId, long ItemId, string ItemCode, decimal Qty, decimal UnitCost, long? WarehouseId, long? DestinationWarehouseId, string InventoryAccountCode, string ExpenseAccountCode)>();
        await using (var lineCommand = new NpgsqlCommand(@"
SELECT l.id,
       l.item_id,
       i.item_code,
       l.qty,
       l.unit_cost,
       l.warehouse_id,
       l.destination_warehouse_id,
       COALESCE(NULLIF(trim(c.account_code), ''), '') AS inventory_account_code,
       COALESCE(NULLIF(trim(l.expense_account_code), ''), '') AS expense_account_code
FROM inv_stock_transaction_lines l
JOIN inv_items i ON i.id = l.item_id
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE l.transaction_id = @transaction_id
ORDER BY l.line_no;", connection, transaction))
        {
            lineCommand.Parameters.AddWithValue("transaction_id", header.Id);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetString(8)));
            }
        }

        if (lines.Count == 0)
        {
            return new AccessOperationResult(false, "Detail transaksi tidak ditemukan. Posting dibatalkan.");
        }

        if (string.Equals(header.TransactionType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
        {
            return new AccessOperationResult(true, "Transfer dicatat tanpa perubahan qty stok pada model lokasi saat ini.", header.Id);
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
        var normalizedStockInCounterAccount = NormalizeAccountCode(costingSettings.CogsAccountCode);
        HashSet<string>? expenseAccountCodeSet = null;
        if (string.Equals(header.TransactionType, "STOCK_OUT", StringComparison.OrdinalIgnoreCase))
        {
            expenseAccountCodeSet = await LoadExpensePostingAccountCodeSetAsync(
                connection,
                transaction,
                header.CompanyId,
                cancellationToken);
            if (expenseAccountCodeSet.Count == 0)
            {
                return new AccessOperationResult(false, "COA akun beban posting tidak ditemukan untuk company ini.");
            }
        }

        foreach (var line in lines)
        {
            if (line.Qty <= 0)
            {
                return new AccessOperationResult(false, $"Qty item {line.ItemCode} harus lebih besar dari 0.");
            }

            if (string.Equals(header.TransactionType, "STOCK_IN", StringComparison.OrdinalIgnoreCase))
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
                    line.WarehouseId,
                    line.Qty,
                    cancellationToken);

                await AddInboundCostLayerAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceStockIn,
                    header.Id,
                    line.LineId,
                    header.TransactionDate,
                    line.Qty,
                    line.UnitCost,
                    method,
                    actor,
                    cancellationToken);

                var inboundUnitCost = RoundCost(line.UnitCost < 0 ? 0 : line.UnitCost);
                var inboundTotalCost = RoundCost(line.Qty * inboundUnitCost);
                await InsertOutboundCostEventAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceStockIn,
                    header.Id,
                    line.LineId,
                    header.TransactionDate,
                    line.Qty,
                    inboundUnitCost,
                    inboundTotalCost,
                    method,
                    line.InventoryAccountCode,
                    normalizedStockInCounterAccount,
                    cogsJournalId: null,
                    cancellationToken);
            }
            else if (string.Equals(header.TransactionType, "STOCK_OUT", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(line.InventoryAccountCode))
                {
                    return new AccessOperationResult(
                        false,
                        $"Akun inventory item {line.ItemCode} belum diisi (item/category).");
                }

                if (string.IsNullOrWhiteSpace(line.ExpenseAccountCode))
                {
                    return new AccessOperationResult(
                        false,
                        $"Akun beban GL item {line.ItemCode} belum diisi.");
                }

                if (expenseAccountCodeSet is null || !expenseAccountCodeSet.Contains(line.ExpenseAccountCode))
                {
                    return new AccessOperationResult(
                        false,
                        $"Akun beban GL '{line.ExpenseAccountCode}' untuk item {line.ItemCode} tidak valid/aktif.");
                }

                var costConsumption = await ConsumeCostFromLayersAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    line.Qty,
                    method,
                    allowShortage: false,
                    cancellationToken);
                if (!costConsumption.IsSuccess)
                {
                    return new AccessOperationResult(
                        false,
                        $"Costing stok item {line.ItemCode} gagal: {costConsumption.Message}");
                }

                if (line.WarehouseId.HasValue && line.WarehouseId.Value > 0)
                {
                    var stockBucketState = await LoadWarehouseStockBucketStateAsync(
                        connection,
                        transaction,
                        header.CompanyId,
                        header.LocationId,
                        line.ItemId,
                        line.WarehouseId.Value,
                        cancellationToken);
                    if (stockBucketState.LegacyQty > 0 && stockBucketState.WarehouseQty < line.Qty)
                    {
                        return new AccessOperationResult(
                            false,
                            $"Stok item {line.ItemCode} masih berada di bucket legacy tanpa gudang. Lakukan rebucket atau stock opname sebelum posting transaksi keluar.");
                    }
                }

                var reduced = await ReduceStockQtyAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    line.WarehouseId,
                    line.Qty,
                    cancellationToken);
                if (!reduced)
                {
                    return new AccessOperationResult(false, $"Stok item {line.ItemCode} tidak mencukupi untuk posting transaksi keluar.");
                }

                await using (var updateLine = new NpgsqlCommand(@"
UPDATE inv_stock_transaction_lines
SET unit_cost = @unit_cost
WHERE id = @id;", connection, transaction))
                {
                    updateLine.Parameters.AddWithValue("id", line.LineId);
                    updateLine.Parameters.AddWithValue("unit_cost", RoundCost(costConsumption.UnitCost));
                    await updateLine.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertOutboundCostEventAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceStockOut,
                    header.Id,
                    line.LineId,
                    header.TransactionDate,
                    line.Qty,
                    costConsumption.UnitCost,
                    costConsumption.TotalCost,
                    method,
                    line.InventoryAccountCode,
                    line.ExpenseAccountCode,
                    cogsJournalId: null,
                    cancellationToken);
            }
            else
            {
                return new AccessOperationResult(false, "Tipe transaksi tidak didukung untuk posting.");
            }
        }

        return new AccessOperationResult(true, "Posting transaksi stok berhasil.", header.Id);
    }

    private async Task AddStockQtyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long itemId,
        long? warehouseId,
        decimal qty,
        CancellationToken cancellationToken)
    {
        object warehouseIdParam = warehouseId.HasValue && warehouseId.Value > 0
            ? warehouseId.Value
            : DBNull.Value;

        await using var command = new NpgsqlCommand(@"
INSERT INTO inv_stock (company_id, location_id, item_id, qty, warehouse_id, updated_at)
VALUES (@company_id, @location_id, @item_id, @qty, @warehouse_id, NOW())
ON CONFLICT (company_id, location_id, item_id, warehouse_id)
DO UPDATE
SET qty = inv_stock.qty + EXCLUDED.qty,
    updated_at = NOW();", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("qty", qty);
        command.Parameters.Add(new NpgsqlParameter("warehouse_id", NpgsqlDbType.Bigint) { Value = warehouseIdParam });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ReduceStockQtyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long itemId,
        long? warehouseId,
        decimal qty,
        CancellationToken cancellationToken)
    {
        object warehouseIdParam = warehouseId.HasValue && warehouseId.Value > 0
            ? warehouseId.Value
            : DBNull.Value;

        await using var command = new NpgsqlCommand(@"
UPDATE inv_stock
SET qty = qty - @qty,
    updated_at = NOW()
WHERE company_id = @company_id
  AND location_id = @location_id
  AND item_id = @item_id
  AND warehouse_id IS NOT DISTINCT FROM @warehouse_id
  AND qty >= @qty;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("qty", qty);
        command.Parameters.Add(new NpgsqlParameter("warehouse_id", NpgsqlDbType.Bigint) { Value = warehouseIdParam });
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    private static async Task<WarehouseStockBucketState> LoadWarehouseStockBucketStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long itemId,
        long warehouseId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT COALESCE(SUM(CASE
                        WHEN warehouse_id IS NOT DISTINCT FROM @warehouse_id THEN qty
                        ELSE 0
                    END), 0) AS warehouse_qty,
       COALESCE(SUM(CASE
                        WHEN warehouse_id IS NULL THEN qty
                        ELSE 0
                    END), 0) AS legacy_qty
FROM inv_stock
WHERE company_id = @company_id
  AND location_id = @location_id
  AND item_id = @item_id
  AND (warehouse_id IS NULL OR warehouse_id IS NOT DISTINCT FROM @warehouse_id);", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return default;
        }

        return new WarehouseStockBucketState(
            reader.IsDBNull(0) ? 0m : reader.GetDecimal(0),
            reader.IsDBNull(1) ? 0m : reader.GetDecimal(1));
    }

    private static async Task<Dictionary<long, (string Code, string Name, string Uom)>> LoadActiveItemMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        IReadOnlyCollection<long> itemIds,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<long, (string Code, string Name, string Uom)>();
        if (itemIds.Count == 0)
        {
            return output;
        }

        await using var command = new NpgsqlCommand(@"
SELECT id, item_code, item_name, uom
FROM inv_items
WHERE is_active = TRUE
  AND id = ANY(@item_ids);", connection, transaction);
        command.Parameters.AddWithValue("item_ids", itemIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }

        return output;
    }

    private static async Task<Dictionary<long, string>> LoadActiveWarehouseMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        IReadOnlyCollection<long> warehouseIds,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<long, string>();
        if (warehouseIds.Count == 0)
        {
            return output;
        }

        await using var command = new NpgsqlCommand(@"
SELECT id, warehouse_name
FROM inv_warehouses
WHERE company_id = @company_id
  AND is_active = TRUE
  AND id = ANY(@warehouse_ids);", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("warehouse_ids", warehouseIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output[reader.GetInt64(0)] = reader.GetString(1);
        }

        return output;
    }

    private static async Task<string> GenerateStockTransactionNoAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string transactionType,
        DateTime transactionDate,
        CancellationToken cancellationToken)
    {
        var prefix = $"{GetStockTransactionPrefix(transactionType)}-{transactionDate:yyyyMMdd}-";
        var nextSequence = await GetNextSequenceByPrefixAsync(
            connection,
            transaction,
            "inv_stock_transactions",
            "transaction_no",
            companyId,
            prefix,
            cancellationToken);
        return $"{prefix}{nextSequence:0000}";
    }

    private static async Task<string> GenerateStockOpnameNoAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        DateTime opnameDate,
        CancellationToken cancellationToken)
    {
        var prefix = $"OPN-{opnameDate:yyyyMMdd}-";
        var nextSequence = await GetNextSequenceByPrefixAsync(
            connection,
            transaction,
            "inv_stock_opname",
            "opname_no",
            companyId,
            prefix,
            cancellationToken);
        return $"{prefix}{nextSequence:0000}";
    }

    private static async Task<int> GetNextSequenceByPrefixAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        string numberColumn,
        long companyId,
        string prefix,
        CancellationToken cancellationToken)
    {
        var expectedLength = prefix.Length + 4;
        var sql = $@"
SELECT COALESCE(MAX(CAST(right({numberColumn}, 4) AS INT)), 0)
FROM {tableName}
WHERE company_id = @company_id
  AND {numberColumn} LIKE @prefix_like
  AND length({numberColumn}) = @expected_length
  AND right({numberColumn}, 4) ~ '^[0-9]{{4}}$';";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("prefix_like", prefix + "%");
        command.Parameters.AddWithValue("expected_length", expectedLength);

        var currentMax = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return currentMax + 1;
    }

    private static string GetStockTransactionPrefix(string transactionType)
    {
        return transactionType switch
        {
            "STOCK_IN" => "SIN",
            "STOCK_OUT" => "SOU",
            "TRANSFER" => "TRF",
            _ => "INV"
        };
    }

    private static string NormalizeTransactionType(string transactionType)
    {
        var normalized = (transactionType ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is "STOCK_IN" or "STOCK_OUT" or "TRANSFER"
            ? normalized
            : string.Empty;
    }

    private static async Task<StockTransactionHeaderSnapshot?> GetStockTransactionHeaderForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long transactionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT id,
       company_id,
       location_id,
       transaction_no,
       transaction_type,
       transaction_date,
       warehouse_id,
       destination_warehouse_id,
       status
FROM inv_stock_transactions
WHERE id = @id
  AND is_active = TRUE
FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("id", transactionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StockTransactionHeaderSnapshot
        {
            Id = reader.GetInt64(0),
            CompanyId = reader.GetInt64(1),
            LocationId = reader.GetInt64(2),
            TransactionNo = reader.GetString(3),
            TransactionType = reader.GetString(4),
            TransactionDate = reader.GetDateTime(5),
            WarehouseId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            DestinationWarehouseId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            Status = reader.GetString(8)
        };
    }

    private readonly record struct WarehouseStockBucketState(decimal WarehouseQty, decimal LegacyQty);
}


