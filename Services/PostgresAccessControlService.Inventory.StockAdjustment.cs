using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<AccessOperationResult> SaveStockAdjustmentDraftAsync(
        ManagedStockAdjustment header,
        IReadOnlyCollection<ManagedStockAdjustmentLine> lines,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (header.CompanyId <= 0 || header.LocationId <= 0)
        {
            return new AccessOperationResult(false, "Perusahaan/lokasi stock adjustment tidak valid.");
        }

        if (!header.WarehouseId.HasValue || header.WarehouseId.Value <= 0)
        {
            return new AccessOperationResult(false, "Gudang stock adjustment wajib diisi.");
        }

        var adjustmentNo = (header.AdjustmentNo ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedLines = (lines ?? Array.Empty<ManagedStockAdjustmentLine>())
            .Where(line => line.ItemId > 0 && line.QtyAdjustment != 0)
            .Select((line, index) => new ManagedStockAdjustmentLine
            {
                LineNo = index + 1,
                ItemId = line.ItemId,
                QtyAdjustment = line.QtyAdjustment,
                UnitCost = line.UnitCost < 0 ? 0 : line.UnitCost,
                Notes = (line.Notes ?? string.Empty).Trim()
            })
            .ToList();

        if (normalizedLines.Count == 0)
        {
            return new AccessOperationResult(false, "Minimal satu baris item dengan qty adjustment tidak nol wajib diisi.");
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
                InventorySubmoduleStockAdjustment,
                ResolveWriteAction(header.Id),
                header.CompanyId,
                header.LocationId,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola stock adjustment.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var itemIds = normalizedLines.Select(x => x.ItemId).Distinct().ToArray();
            var itemMap = await LoadActiveItemMapAsync(connection, transaction, header.CompanyId, itemIds, cancellationToken);
            if (itemMap.Count != itemIds.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Terdapat item tidak valid atau nonaktif pada detail stock adjustment.");
            }

            var warehouseMap = await LoadActiveWarehouseMapAsync(connection, transaction, header.CompanyId, [header.WarehouseId.Value], cancellationToken);
            if (warehouseMap.Count != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Gudang stock adjustment tidak valid atau nonaktif.");
            }

            var warehouseValidationMessage = ValidateWarehouseLocationForTransaction(
                warehouseMap,
                header.WarehouseId,
                header.LocationId,
                "Gudang stock adjustment");
            if (!string.IsNullOrWhiteSpace(warehouseValidationMessage))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, warehouseValidationMessage);
            }

            var adjustmentDate = header.AdjustmentDate.Date;
            var referenceNo = (header.ReferenceNo ?? string.Empty).Trim();
            var description = (header.Description ?? string.Empty).Trim();

            long adjustmentId;
            if (header.Id <= 0)
            {
                if (string.IsNullOrWhiteSpace(adjustmentNo))
                {
                    adjustmentNo = await GenerateStockAdjustmentNoAsync(connection, transaction, header.CompanyId, adjustmentDate, cancellationToken);
                }

                await using var insertHeader = new NpgsqlCommand(@"
INSERT INTO inv_stock_adjustments (
    company_id, location_id, adjustment_no, adjustment_date, warehouse_id, reference_no, description,
    status, is_active, created_by, created_at, updated_by, updated_at)
VALUES (
    @company_id, @location_id, @adjustment_no, @adjustment_date, @warehouse_id, @reference_no, @description,
    'DRAFT', TRUE, @actor, NOW(), @actor, NOW())
RETURNING id;", connection, transaction);
                insertHeader.Parameters.AddWithValue("company_id", header.CompanyId);
                insertHeader.Parameters.AddWithValue("location_id", header.LocationId);
                insertHeader.Parameters.AddWithValue("adjustment_no", adjustmentNo);
                insertHeader.Parameters.AddWithValue("adjustment_date", adjustmentDate);
                insertHeader.Parameters.AddWithValue("warehouse_id", header.WarehouseId.Value);
                insertHeader.Parameters.AddWithValue("reference_no", referenceNo);
                insertHeader.Parameters.AddWithValue("description", description);
                insertHeader.Parameters.AddWithValue("actor", actor);
                adjustmentId = Convert.ToInt64(await insertHeader.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                string? currentStatus = null;
                string? currentAdjustmentNo = null;
                await using (var lockHeader = new NpgsqlCommand(@"
SELECT status, adjustment_no
FROM inv_stock_adjustments
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
                        currentAdjustmentNo = reader.GetString(1);
                    }
                }

                if (string.IsNullOrWhiteSpace(currentStatus))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Data stock adjustment tidak ditemukan.");
                }

                if (!string.Equals(currentStatus, "DRAFT", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Hanya stock adjustment DRAFT yang dapat diubah.");
                }

                if (string.IsNullOrWhiteSpace(adjustmentNo))
                {
                    adjustmentNo = (currentAdjustmentNo ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(adjustmentNo))
                    {
                        adjustmentNo = await GenerateStockAdjustmentNoAsync(connection, transaction, header.CompanyId, adjustmentDate, cancellationToken);
                    }
                }

                await using var updateHeader = new NpgsqlCommand(@"
UPDATE inv_stock_adjustments
SET adjustment_no = @adjustment_no,
    adjustment_date = @adjustment_date,
    warehouse_id = @warehouse_id,
    reference_no = @reference_no,
    description = @description,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                updateHeader.Parameters.AddWithValue("id", header.Id);
                updateHeader.Parameters.AddWithValue("adjustment_no", adjustmentNo);
                updateHeader.Parameters.AddWithValue("adjustment_date", adjustmentDate);
                updateHeader.Parameters.AddWithValue("warehouse_id", header.WarehouseId.Value);
                updateHeader.Parameters.AddWithValue("reference_no", referenceNo);
                updateHeader.Parameters.AddWithValue("description", description);
                updateHeader.Parameters.AddWithValue("updated_by", actor);
                await updateHeader.ExecuteNonQueryAsync(cancellationToken);

                adjustmentId = header.Id;
            }

            await using (var clearLines = new NpgsqlCommand(
                "DELETE FROM inv_stock_adjustment_lines WHERE adjustment_id = @adjustment_id;",
                connection,
                transaction))
            {
                clearLines.Parameters.AddWithValue("adjustment_id", adjustmentId);
                await clearLines.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in normalizedLines)
            {
                await using var insertLine = new NpgsqlCommand(@"
INSERT INTO inv_stock_adjustment_lines (
    adjustment_id, line_no, item_id, qty_adjustment, unit_cost, notes, created_at)
VALUES (
    @adjustment_id, @line_no, @item_id, @qty_adjustment, @unit_cost, @notes, NOW());", connection, transaction);
                insertLine.Parameters.AddWithValue("adjustment_id", adjustmentId);
                insertLine.Parameters.AddWithValue("line_no", line.LineNo);
                insertLine.Parameters.AddWithValue("item_id", line.ItemId);
                insertLine.Parameters.AddWithValue("qty_adjustment", line.QtyAdjustment);
                insertLine.Parameters.AddWithValue("unit_cost", line.UnitCost);
                insertLine.Parameters.AddWithValue("notes", line.Notes ?? string.Empty);
                await insertLine.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_STOCK_ADJ",
                adjustmentId,
                "SAVE_DRAFT",
                actor,
                $"adjustment_no={adjustmentNo};company={header.CompanyId};location={header.LocationId};lines={normalizedLines.Count}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Draft stock adjustment berhasil disimpan.", adjustmentId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new AccessOperationResult(false, "Nomor stock adjustment sudah digunakan pada perusahaan ini.");
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal menyimpan draft stock adjustment: {ex.Message}");
        }
    }

    public async Task<List<ManagedStockAdjustment>> SearchStockAdjustmentsAsync(
        long companyId,
        long locationId,
        string keyword,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new List<ManagedStockAdjustment>();
        if (companyId <= 0 || locationId <= 0)
        {
            return output;
        }

        var safeKeyword = (keyword ?? string.Empty).Trim();
        var keywordLike = string.IsNullOrWhiteSpace(safeKeyword) ? "%" : $"%{safeKeyword}%";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT a.id, a.company_id, a.location_id, a.adjustment_no, a.adjustment_date, a.warehouse_id,
       COALESCE(w.warehouse_name, '') AS warehouse_name, a.reference_no, a.description, a.status
FROM inv_stock_adjustments a
LEFT JOIN inv_warehouses w ON w.id = a.warehouse_id
WHERE a.company_id = @company_id
  AND a.location_id = @location_id
  AND a.is_active = TRUE
  AND (@keyword = '' OR a.adjustment_no ILIKE @keyword_like OR a.reference_no ILIKE @keyword_like OR a.description ILIKE @keyword_like)
ORDER BY a.adjustment_date DESC, a.id DESC
LIMIT 300;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("keyword", safeKeyword);
        command.Parameters.AddWithValue("keyword_like", keywordLike);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedStockAdjustment
            {
                Id = reader.GetInt64(0),
                CompanyId = reader.GetInt64(1),
                LocationId = reader.GetInt64(2),
                AdjustmentNo = reader.GetString(3),
                AdjustmentDate = reader.GetDateTime(4),
                WarehouseId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                WarehouseName = reader.GetString(6),
                ReferenceNo = reader.GetString(7),
                Description = reader.GetString(8),
                Status = reader.GetString(9)
            });
        }

        return output;
    }

    public async Task<StockAdjustmentBundle?> GetStockAdjustmentBundleAsync(
        long adjustmentId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (adjustmentId <= 0)
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        ManagedStockAdjustment? header = null;
        await using (var headerCommand = new NpgsqlCommand(@"
SELECT a.id, a.company_id, a.location_id, a.adjustment_no, a.adjustment_date, a.warehouse_id,
       COALESCE(w.warehouse_name, '') AS warehouse_name, a.reference_no, a.description, a.status
FROM inv_stock_adjustments a
LEFT JOIN inv_warehouses w ON w.id = a.warehouse_id
WHERE a.id = @id
  AND a.is_active = TRUE;", connection))
        {
            headerCommand.Parameters.AddWithValue("id", adjustmentId);
            await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                header = new ManagedStockAdjustment
                {
                    Id = reader.GetInt64(0),
                    CompanyId = reader.GetInt64(1),
                    LocationId = reader.GetInt64(2),
                    AdjustmentNo = reader.GetString(3),
                    AdjustmentDate = reader.GetDateTime(4),
                    WarehouseId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    WarehouseName = reader.GetString(6),
                    ReferenceNo = reader.GetString(7),
                    Description = reader.GetString(8),
                    Status = reader.GetString(9)
                };
            }
        }

        if (header is null)
        {
            return null;
        }

        var lines = new List<ManagedStockAdjustmentLine>();
        await using (var lineCommand = new NpgsqlCommand(@"
SELECT l.id, l.adjustment_id, l.line_no, l.item_id, i.item_code, i.item_name, i.uom, l.qty_adjustment, l.unit_cost, COALESCE(l.notes, '')
FROM inv_stock_adjustment_lines l
JOIN inv_items i ON i.id = l.item_id
WHERE l.adjustment_id = @adjustment_id
ORDER BY l.line_no;", connection))
        {
            lineCommand.Parameters.AddWithValue("adjustment_id", adjustmentId);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new ManagedStockAdjustmentLine
                {
                    Id = reader.GetInt64(0),
                    AdjustmentId = reader.GetInt64(1),
                    LineNo = reader.GetInt32(2),
                    ItemId = reader.GetInt64(3),
                    ItemCode = reader.GetString(4),
                    ItemName = reader.GetString(5),
                    Uom = reader.GetString(6),
                    QtyAdjustment = reader.GetDecimal(7),
                    UnitCost = reader.GetDecimal(8),
                    Notes = reader.GetString(9)
                });
            }
        }

        return new StockAdjustmentBundle
        {
            Header = header,
            Lines = lines
        };
    }

    public async Task<AccessOperationResult> SubmitStockAdjustmentAsync(
        long adjustmentId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockAdjustmentStatusAsync(adjustmentId, "DRAFT", "SUBMITTED", actorUsername, cancellationToken);
    }

    public async Task<AccessOperationResult> ApproveStockAdjustmentAsync(
        long adjustmentId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockAdjustmentStatusAsync(adjustmentId, "SUBMITTED", "APPROVED", actorUsername, cancellationToken);
    }

    public async Task<AccessOperationResult> PostStockAdjustmentAsync(
        long adjustmentId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        return await ChangeStockAdjustmentStatusAsync(adjustmentId, "APPROVED", "POSTED", actorUsername, cancellationToken);
    }

    private async Task<AccessOperationResult> ChangeStockAdjustmentStatusAsync(
        long adjustmentId,
        string expectedCurrentStatus,
        string targetStatus,
        string actorUsername,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (adjustmentId <= 0)
        {
            return new AccessOperationResult(false, "Data stock adjustment tidak valid.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var header = await GetStockAdjustmentHeaderForUpdateAsync(connection, transaction, adjustmentId, cancellationToken);
            if (header is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Data stock adjustment tidak ditemukan.");
            }

            var permissionAction = ResolveWorkflowAction(targetStatus);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                InventoryModuleCode,
                InventorySubmoduleStockAdjustment,
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
                return new AccessOperationResult(false, $"Hanya stock adjustment {expectedCurrentStatus} yang dapat diubah ke {targetStatus}.");
            }

            if (string.Equals(targetStatus, "POSTED", StringComparison.OrdinalIgnoreCase))
            {
                var postingResult = await ApplyStockAdjustmentPostingAsync(connection, transaction, header, actor, cancellationToken);
                if (!postingResult.IsSuccess)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return postingResult;
                }
            }

            await using (var updateCommand = new NpgsqlCommand(@"
UPDATE inv_stock_adjustments
SET status = @target_status,
    updated_by = @updated_by,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                updateCommand.Parameters.AddWithValue("id", adjustmentId);
                updateCommand.Parameters.AddWithValue("target_status", targetStatus);
                updateCommand.Parameters.AddWithValue("updated_by", actor);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "INV_STOCK_ADJ",
                adjustmentId,
                targetStatus,
                actor,
                $"adjustment_no={header.AdjustmentNo};company={header.CompanyId};location={header.LocationId}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, $"Stock adjustment {header.AdjustmentNo} berhasil di-{targetStatus.ToLowerInvariant()}.", adjustmentId);
        }
        catch (Exception ex)
        {
            return new AccessOperationResult(false, $"Gagal mengubah status stock adjustment: {ex.Message}");
        }
    }

    private async Task<AccessOperationResult> ApplyStockAdjustmentPostingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StockAdjustmentHeaderSnapshot header,
        string actor,
        CancellationToken cancellationToken)
    {
        var lines = new List<(long LineId, long ItemId, string ItemCode, decimal QtyAdjustment, decimal UnitCost, string InventoryAccountCode)>();
        await using (var lineCommand = new NpgsqlCommand(@"
SELECT l.id,
       l.item_id,
       i.item_code,
       l.qty_adjustment,
       l.unit_cost,
       COALESCE(NULLIF(trim(c.account_code), ''), '') AS inventory_account_code
FROM inv_stock_adjustment_lines l
JOIN inv_items i ON i.id = l.item_id
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE l.adjustment_id = @adjustment_id
ORDER BY l.line_no;", connection, transaction))
        {
            lineCommand.Parameters.AddWithValue("adjustment_id", header.Id);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetString(5)));
            }
        }

        if (lines.Count == 0)
        {
            return new AccessOperationResult(false, "Detail stock adjustment tidak ditemukan. Posting dibatalkan.");
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
        if (lines.Any(line => line.QtyAdjustment != 0) && string.IsNullOrWhiteSpace(cogsAccountCode))
        {
            return new AccessOperationResult(false, "Akun COGS belum diatur untuk lokasi ini.");
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.InventoryAccountCode))
            {
                return new AccessOperationResult(false, $"Akun inventory item {line.ItemCode} belum diisi (item/category).");
            }

            if (line.QtyAdjustment > 0)
            {
                var inboundUnitCost = line.UnitCost > 0
                    ? RoundCost(line.UnitCost)
                    : await GetCurrentAverageCostFromLayersAsync(connection, transaction, header.CompanyId, header.LocationId, line.ItemId, cancellationToken);
                inboundUnitCost = RoundCost(inboundUnitCost < 0 ? 0 : inboundUnitCost);

                await AddStockQtyAsync(connection, transaction, header.CompanyId, header.LocationId, line.ItemId, header.WarehouseId, line.QtyAdjustment, cancellationToken);
                await AddInboundCostLayerAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceAdjustmentPlus,
                    header.Id,
                    line.LineId,
                    header.AdjustmentDate,
                    line.QtyAdjustment,
                    inboundUnitCost,
                    method,
                    actor,
                    cancellationToken);
                await InsertOutboundCostEventAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceAdjustmentPlus,
                    header.Id,
                    line.LineId,
                    header.AdjustmentDate,
                    line.QtyAdjustment,
                    inboundUnitCost,
                    RoundCost(line.QtyAdjustment * inboundUnitCost),
                    method,
                    line.InventoryAccountCode,
                    cogsAccountCode,
                    cogsJournalId: null,
                    cancellationToken);
            }
            else if (line.QtyAdjustment < 0)
            {
                var qtyOut = Math.Abs(line.QtyAdjustment);
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
                    return new AccessOperationResult(false, $"Costing stock adjustment item {line.ItemCode} gagal: {costConsumption.Message}");
                }

                if (header.WarehouseId.HasValue && header.WarehouseId.Value > 0)
                {
                    var stockBucketState = await LoadWarehouseStockBucketStateAsync(
                        connection,
                        transaction,
                        header.CompanyId,
                        header.LocationId,
                        line.ItemId,
                        header.WarehouseId.Value,
                        cancellationToken);
                    if (stockBucketState.LegacyQty > 0 && stockBucketState.WarehouseQty < qtyOut)
                    {
                        return new AccessOperationResult(false, $"Stok item {line.ItemCode} masih berada di bucket legacy tanpa gudang. Lakukan rebucket atau stock opname sebelum posting stock adjustment.");
                    }
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
                    return new AccessOperationResult(false, $"Stok item {line.ItemCode} tidak mencukupi untuk posting stock adjustment.");
                }

                await InsertOutboundCostEventAsync(
                    connection,
                    transaction,
                    header.CompanyId,
                    header.LocationId,
                    line.ItemId,
                    InventoryCostSourceAdjustmentMinus,
                    header.Id,
                    line.LineId,
                    header.AdjustmentDate,
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

        return new AccessOperationResult(true, "Posting stock adjustment berhasil.", header.Id);
    }

    private static async Task<StockAdjustmentHeaderSnapshot?> GetStockAdjustmentHeaderForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long adjustmentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT id, company_id, location_id, adjustment_no, adjustment_date, warehouse_id, status
FROM inv_stock_adjustments
WHERE id = @id
  AND is_active = TRUE
FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("id", adjustmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StockAdjustmentHeaderSnapshot
        {
            Id = reader.GetInt64(0),
            CompanyId = reader.GetInt64(1),
            LocationId = reader.GetInt64(2),
            AdjustmentNo = reader.GetString(3),
            AdjustmentDate = reader.GetDateTime(4),
            WarehouseId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            Status = reader.GetString(6)
        };
    }

    private static async Task<string> GenerateStockAdjustmentNoAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        DateTime adjustmentDate,
        CancellationToken cancellationToken)
    {
        var prefix = $"ADJ-{adjustmentDate:yyyyMMdd}-";
        var nextSequence = await GetNextSequenceByPrefixAsync(
            connection,
            transaction,
            "inv_stock_adjustments",
            "adjustment_no",
            companyId,
            prefix,
            cancellationToken);
        return $"{prefix}{nextSequence:0000}";
    }

    private sealed class StockAdjustmentHeaderSnapshot
    {
        public long Id { get; init; }

        public long CompanyId { get; init; }

        public long LocationId { get; init; }

        public string AdjustmentNo { get; init; } = string.Empty;

        public DateTime AdjustmentDate { get; init; }

        public long? WarehouseId { get; init; }

        public string Status { get; init; } = string.Empty;
    }
}
