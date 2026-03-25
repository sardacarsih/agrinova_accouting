using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private async Task EnsureInventoryCostingStateInitializedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string actor,
        long? locationId,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0)
        {
            return;
        }

        var hasPostedDocuments = false;
        await using (var postedCommand = new NpgsqlCommand(@"
SELECT (
    SELECT COUNT(1)
    FROM inv_stock_transactions
    WHERE company_id = @company_id
      AND (@location_id IS NULL OR location_id = @location_id)
      AND status = 'POSTED'
) +
(
    SELECT COUNT(1)
    FROM inv_stock_opname
    WHERE company_id = @company_id
      AND (@location_id IS NULL OR location_id = @location_id)
      AND status = 'POSTED'
);", connection, transaction))
        {
            postedCommand.Parameters.AddWithValue("company_id", companyId);
            postedCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
            {
                Value = locationId.HasValue ? locationId.Value : DBNull.Value
            });
            hasPostedDocuments = Convert.ToInt64(await postedCommand.ExecuteScalarAsync(cancellationToken)) > 0;
        }

        if (!hasPostedDocuments)
        {
            return;
        }

        var hasLayers = false;
        await using (var layerCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM inv_cost_layers
WHERE company_id = @company_id
  AND (@location_id IS NULL OR location_id = @location_id);", connection, transaction))
        {
            layerCommand.Parameters.AddWithValue("company_id", companyId);
            layerCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
            {
                Value = locationId.HasValue ? locationId.Value : DBNull.Value
            });
            hasLayers = Convert.ToInt64(await layerCommand.ExecuteScalarAsync(cancellationToken)) > 0;
        }

        if (hasLayers)
        {
            return;
        }

        await RebuildInventoryCostingStateAsync(
            connection,
            transaction,
            companyId,
            actor,
            locationId,
            cancellationToken);
    }

    private async Task RebuildInventoryCostingStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        string actor,
        long? locationId,
        CancellationToken cancellationToken)
    {
        var companySettings = await GetInventoryCostingSettingsInternalAsync(connection, transaction, companyId, cancellationToken);
        var defaultMethod = NormalizeValuationMethod(companySettings.ValuationMethod);
        var locationMethodMap = new Dictionary<long, string>();
        await using (var locationSettingCommand = new NpgsqlCommand(@"
SELECT location_id, valuation_method
FROM inv_location_costing_settings
WHERE company_id = @company_id
  AND (@location_id IS NULL OR location_id = @location_id);", connection, transaction))
        {
            locationSettingCommand.Parameters.AddWithValue("company_id", companyId);
            locationSettingCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
            {
                Value = locationId.HasValue ? locationId.Value : DBNull.Value
            });
            await using var reader = await locationSettingCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                locationMethodMap[reader.GetInt64(0)] = NormalizeValuationMethod(reader.GetString(1));
            }
        }

        await using (var clearOutbound = new NpgsqlCommand(
            @"DELETE FROM inv_cost_outbound_events
WHERE company_id = @company_id
  AND (@location_id IS NULL OR location_id = @location_id);",
            connection,
            transaction))
        {
            clearOutbound.Parameters.AddWithValue("company_id", companyId);
            clearOutbound.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
            {
                Value = locationId.HasValue ? locationId.Value : DBNull.Value
            });
            await clearOutbound.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearLayers = new NpgsqlCommand(
            @"DELETE FROM inv_cost_layers
WHERE company_id = @company_id
  AND (@location_id IS NULL OR location_id = @location_id);",
            connection,
            transaction))
        {
            clearLayers.Parameters.AddWithValue("company_id", companyId);
            clearLayers.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
            {
                Value = locationId.HasValue ? locationId.Value : DBNull.Value
            });
            await clearLayers.ExecuteNonQueryAsync(cancellationToken);
        }

        var events = new List<InventoryCostFlowEvent>();
        await using (var eventCommand = new NpgsqlCommand(@"
SELECT e.source_type,
       e.source_id,
       e.source_line_id,
       e.line_no,
       e.location_id,
       e.item_id,
       e.item_code,
       e.event_date,
       e.qty,
       e.unit_cost,
       e.inventory_account_code,
       e.cogs_account_code,
       e.cogs_journal_id
FROM (
    SELECT 'STOCK_IN'::VARCHAR AS source_type,
           h.id AS source_id,
           l.id AS source_line_id,
           l.line_no,
           h.location_id,
           l.item_id,
           i.item_code,
           h.transaction_date AS event_date,
           l.qty AS qty,
           COALESCE(l.unit_cost, 0) AS unit_cost,
           COALESCE(NULLIF(trim(c.account_code), ''), '') AS inventory_account_code,
           COALESCE(NULLIF(trim(ls.cogs_account_code), ''), NULLIF(trim(cs.cogs_account_code), ''), '') AS cogs_account_code,
           h.cogs_journal_id AS cogs_journal_id,
           1 AS sort_order
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    JOIN inv_items i ON i.id = l.item_id
    LEFT JOIN inv_categories c ON c.id = i.category_id
    LEFT JOIN inv_location_costing_settings ls ON ls.company_id = h.company_id AND ls.location_id = h.location_id
    LEFT JOIN inv_company_settings cs ON cs.company_id = h.company_id
    WHERE h.company_id = @company_id
      AND (@location_id IS NULL OR h.location_id = @location_id)
      AND h.status = 'POSTED'
      AND h.transaction_type = 'STOCK_IN'

    UNION ALL

    SELECT 'STOCK_OUT'::VARCHAR AS source_type,
           h.id AS source_id,
           l.id AS source_line_id,
           l.line_no,
           h.location_id,
           l.item_id,
           i.item_code,
           h.transaction_date AS event_date,
           l.qty AS qty,
           COALESCE(l.unit_cost, 0) AS unit_cost,
           COALESCE(NULLIF(trim(c.account_code), ''), '') AS inventory_account_code,
           COALESCE(NULLIF(trim(l.expense_account_code), ''), NULLIF(trim(ls.cogs_account_code), ''), NULLIF(trim(cs.cogs_account_code), ''), '') AS cogs_account_code,
           h.cogs_journal_id AS cogs_journal_id,
           2 AS sort_order
    FROM inv_stock_transactions h
    JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
    JOIN inv_items i ON i.id = l.item_id
    LEFT JOIN inv_categories c ON c.id = i.category_id
    LEFT JOIN inv_location_costing_settings ls ON ls.company_id = h.company_id AND ls.location_id = h.location_id
    LEFT JOIN inv_company_settings cs ON cs.company_id = h.company_id
    WHERE h.company_id = @company_id
      AND (@location_id IS NULL OR h.location_id = @location_id)
      AND h.status = 'POSTED'
      AND h.transaction_type = 'STOCK_OUT'

    UNION ALL

    SELECT 'OPNAME_PLUS'::VARCHAR AS source_type,
           o.id AS source_id,
           l.id AS source_line_id,
           l.line_no,
           o.location_id,
           l.item_id,
           i.item_code,
           o.opname_date AS event_date,
           l.difference_qty AS qty,
           0::NUMERIC AS unit_cost,
           COALESCE(NULLIF(trim(c.account_code), ''), '') AS inventory_account_code,
           COALESCE(NULLIF(trim(ls.cogs_account_code), ''), NULLIF(trim(cs.cogs_account_code), ''), '') AS cogs_account_code,
           o.cogs_journal_id AS cogs_journal_id,
           3 AS sort_order
    FROM inv_stock_opname o
    JOIN inv_stock_opname_lines l ON l.opname_id = o.id
    JOIN inv_items i ON i.id = l.item_id
    LEFT JOIN inv_categories c ON c.id = i.category_id
    LEFT JOIN inv_location_costing_settings ls ON ls.company_id = o.company_id AND ls.location_id = o.location_id
    LEFT JOIN inv_company_settings cs ON cs.company_id = o.company_id
    WHERE o.company_id = @company_id
      AND (@location_id IS NULL OR o.location_id = @location_id)
      AND o.status = 'POSTED'
      AND l.difference_qty > 0

    UNION ALL

    SELECT 'OPNAME_MINUS'::VARCHAR AS source_type,
           o.id AS source_id,
           l.id AS source_line_id,
           l.line_no,
           o.location_id,
           l.item_id,
           i.item_code,
           o.opname_date AS event_date,
           ABS(l.difference_qty) AS qty,
           0::NUMERIC AS unit_cost,
           COALESCE(NULLIF(trim(c.account_code), ''), '') AS inventory_account_code,
           COALESCE(NULLIF(trim(ls.cogs_account_code), ''), NULLIF(trim(cs.cogs_account_code), ''), '') AS cogs_account_code,
           o.cogs_journal_id AS cogs_journal_id,
           4 AS sort_order
    FROM inv_stock_opname o
    JOIN inv_stock_opname_lines l ON l.opname_id = o.id
    JOIN inv_items i ON i.id = l.item_id
    LEFT JOIN inv_categories c ON c.id = i.category_id
    LEFT JOIN inv_location_costing_settings ls ON ls.company_id = o.company_id AND ls.location_id = o.location_id
    LEFT JOIN inv_company_settings cs ON cs.company_id = o.company_id
    WHERE o.company_id = @company_id
      AND (@location_id IS NULL OR o.location_id = @location_id)
      AND o.status = 'POSTED'
      AND l.difference_qty < 0
) e
ORDER BY e.event_date, e.sort_order, e.source_id, e.line_no;", connection, transaction))
        {
            eventCommand.Parameters.AddWithValue("company_id", companyId);
            eventCommand.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
            {
                Value = locationId.HasValue ? locationId.Value : DBNull.Value
            });
            await using var reader = await eventCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new InventoryCostFlowEvent
                {
                    SourceType = reader.GetString(0),
                    SourceId = reader.GetInt64(1),
                    SourceLineId = reader.GetInt64(2),
                    LineNo = reader.GetInt32(3),
                    LocationId = reader.GetInt64(4),
                    ItemId = reader.GetInt64(5),
                    ItemCode = reader.GetString(6),
                    EventDate = reader.GetDateTime(7),
                    Qty = reader.GetDecimal(8),
                    UnitCost = reader.GetDecimal(9),
                    InventoryAccountCode = reader.GetString(10),
                    CogsAccountCode = reader.GetString(11),
                    CogsJournalId = reader.IsDBNull(12) ? null : reader.GetInt64(12)
                });
            }
        }

        var lastUnitCostByKey = new Dictionary<InventoryCostKey, decimal>();
        foreach (var evt in events)
        {
            if (evt.Qty <= 0)
            {
                continue;
            }

            var key = new InventoryCostKey(evt.LocationId, evt.ItemId);
            var method = locationMethodMap.TryGetValue(evt.LocationId, out var locationMethod)
                ? locationMethod
                : defaultMethod;
            if (string.Equals(evt.SourceType, InventoryCostSourceStockIn, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(evt.SourceType, InventoryCostSourceOpnamePlus, StringComparison.OrdinalIgnoreCase))
            {
                var inboundCost = RoundCost(evt.UnitCost);
                if (string.Equals(evt.SourceType, InventoryCostSourceOpnamePlus, StringComparison.OrdinalIgnoreCase) || inboundCost <= 0)
                {
                    inboundCost = await GetCurrentAverageCostFromLayersAsync(
                        connection,
                        transaction,
                        companyId,
                        evt.LocationId,
                        evt.ItemId,
                        cancellationToken);
                    if (inboundCost <= 0 &&
                        lastUnitCostByKey.TryGetValue(key, out var cachedUnitCost) &&
                        cachedUnitCost > 0)
                    {
                        inboundCost = cachedUnitCost;
                    }
                }

                await AddInboundCostLayerAsync(
                    connection,
                    transaction,
                    companyId,
                    evt.LocationId,
                    evt.ItemId,
                    evt.SourceType,
                    evt.SourceId,
                    evt.SourceLineId,
                    evt.EventDate,
                    evt.Qty,
                    inboundCost,
                    method,
                    actor,
                    cancellationToken);

                if (inboundCost > 0)
                {
                    lastUnitCostByKey[key] = inboundCost;
                }

                if (string.Equals(evt.SourceType, InventoryCostSourceStockIn, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(evt.SourceType, InventoryCostSourceOpnamePlus, StringComparison.OrdinalIgnoreCase))
                {
                    await InsertOutboundCostEventAsync(
                        connection,
                        transaction,
                        companyId,
                        evt.LocationId,
                        evt.ItemId,
                        evt.SourceType,
                        evt.SourceId,
                        evt.SourceLineId,
                        evt.EventDate,
                        evt.Qty,
                        inboundCost,
                        RoundCost(evt.Qty * inboundCost),
                        method,
                        evt.InventoryAccountCode,
                        evt.CogsAccountCode,
                        evt.CogsJournalId,
                        cancellationToken);
                }

                continue;
            }

            var consumption = await ConsumeCostFromLayersAsync(
                connection,
                transaction,
                companyId,
                evt.LocationId,
                evt.ItemId,
                evt.Qty,
                method,
                allowShortage: true,
                cancellationToken);
            if (!consumption.IsSuccess)
            {
                throw new InvalidOperationException(consumption.Message);
            }

            if (consumption.UnitCost > 0)
            {
                lastUnitCostByKey[key] = consumption.UnitCost;
            }

            if (string.Equals(evt.SourceType, InventoryCostSourceStockOut, StringComparison.OrdinalIgnoreCase))
            {
                await using var updateLine = new NpgsqlCommand(@"
UPDATE inv_stock_transaction_lines
SET unit_cost = @unit_cost
WHERE id = @line_id;", connection, transaction);
                updateLine.Parameters.AddWithValue("line_id", evt.SourceLineId);
                updateLine.Parameters.AddWithValue("unit_cost", RoundCost(consumption.UnitCost));
                await updateLine.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertOutboundCostEventAsync(
                connection,
                transaction,
                companyId,
                evt.LocationId,
                evt.ItemId,
                evt.SourceType,
                evt.SourceId,
                evt.SourceLineId,
                evt.EventDate,
                evt.Qty,
                consumption.UnitCost,
                consumption.TotalCost,
                method,
                evt.InventoryAccountCode,
                evt.CogsAccountCode,
                evt.CogsJournalId,
                cancellationToken);
        }
    }

    private static async Task AddInboundCostLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long itemId,
        string sourceType,
        long sourceId,
        long sourceLineId,
        DateTime layerDate,
        decimal qty,
        decimal unitCost,
        string valuationMethod,
        string actor,
        CancellationToken cancellationToken)
    {
        if (qty <= 0)
        {
            return;
        }

        var roundedQty = RoundCost(qty);
        var roundedUnitCost = RoundCost(unitCost < 0 ? 0 : unitCost);
        var method = NormalizeValuationMethod(valuationMethod);
        var normalizedSourceType = string.IsNullOrWhiteSpace(sourceType)
            ? InventoryCostSourceStockIn
            : sourceType.Trim().ToUpperInvariant();

        if (string.Equals(method, InventoryValuationMethodAverage, StringComparison.OrdinalIgnoreCase))
        {
            decimal existingQty;
            decimal existingValue;
            await using (var aggregate = new NpgsqlCommand(@"
WITH locked_layers AS (
    SELECT remaining_qty, unit_cost
    FROM inv_cost_layers
    WHERE company_id = @company_id
      AND location_id = @location_id
      AND item_id = @item_id
    FOR UPDATE
)
SELECT COALESCE(SUM(remaining_qty), 0) AS qty,
       COALESCE(SUM(remaining_qty * unit_cost), 0) AS value
FROM locked_layers;", connection, transaction))
            {
                aggregate.Parameters.AddWithValue("company_id", companyId);
                aggregate.Parameters.AddWithValue("location_id", locationId);
                aggregate.Parameters.AddWithValue("item_id", itemId);
                await using var reader = await aggregate.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                existingQty = reader.GetDecimal(0);
                existingValue = reader.GetDecimal(1);
            }

            await using (var clear = new NpgsqlCommand(@"
DELETE FROM inv_cost_layers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND item_id = @item_id;", connection, transaction))
            {
                clear.Parameters.AddWithValue("company_id", companyId);
                clear.Parameters.AddWithValue("location_id", locationId);
                clear.Parameters.AddWithValue("item_id", itemId);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            var totalQty = existingQty + roundedQty;
            if (totalQty <= 0)
            {
                return;
            }

            var totalValue = existingValue + (roundedQty * roundedUnitCost);
            var averageCost = totalQty <= 0 ? 0 : RoundCost(totalValue / totalQty);

            await using var insertAverage = new NpgsqlCommand(@"
INSERT INTO inv_cost_layers (
    company_id,
    location_id,
    item_id,
    source_type,
    source_id,
    source_line_id,
    layer_date,
    remaining_qty,
    unit_cost,
    created_by,
    created_at,
    updated_at
)
VALUES (
    @company_id,
    @location_id,
    @item_id,
    @source_type,
    @source_id,
    @source_line_id,
    @layer_date,
    @remaining_qty,
    @unit_cost,
    @created_by,
    NOW(),
    NOW()
);", connection, transaction);
            insertAverage.Parameters.AddWithValue("company_id", companyId);
            insertAverage.Parameters.AddWithValue("location_id", locationId);
            insertAverage.Parameters.AddWithValue("item_id", itemId);
            insertAverage.Parameters.AddWithValue("source_type", normalizedSourceType);
            insertAverage.Parameters.AddWithValue("source_id", sourceId);
            insertAverage.Parameters.AddWithValue("source_line_id", sourceLineId);
            insertAverage.Parameters.AddWithValue("layer_date", layerDate.Date);
            insertAverage.Parameters.AddWithValue("remaining_qty", RoundCost(totalQty));
            insertAverage.Parameters.AddWithValue("unit_cost", averageCost);
            insertAverage.Parameters.AddWithValue("created_by", actor);
            await insertAverage.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var insertLayer = new NpgsqlCommand(@"
INSERT INTO inv_cost_layers (
    company_id,
    location_id,
    item_id,
    source_type,
    source_id,
    source_line_id,
    layer_date,
    remaining_qty,
    unit_cost,
    created_by,
    created_at,
    updated_at
)
VALUES (
    @company_id,
    @location_id,
    @item_id,
    @source_type,
    @source_id,
    @source_line_id,
    @layer_date,
    @remaining_qty,
    @unit_cost,
    @created_by,
    NOW(),
    NOW()
);", connection, transaction);
        insertLayer.Parameters.AddWithValue("company_id", companyId);
        insertLayer.Parameters.AddWithValue("location_id", locationId);
        insertLayer.Parameters.AddWithValue("item_id", itemId);
        insertLayer.Parameters.AddWithValue("source_type", normalizedSourceType);
        insertLayer.Parameters.AddWithValue("source_id", sourceId);
        insertLayer.Parameters.AddWithValue("source_line_id", sourceLineId);
        insertLayer.Parameters.AddWithValue("layer_date", layerDate.Date);
        insertLayer.Parameters.AddWithValue("remaining_qty", roundedQty);
        insertLayer.Parameters.AddWithValue("unit_cost", roundedUnitCost);
        insertLayer.Parameters.AddWithValue("created_by", actor);
        await insertLayer.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<InventoryCostConsumptionResult> ConsumeCostFromLayersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long itemId,
        decimal qty,
        string valuationMethod,
        bool allowShortage,
        CancellationToken cancellationToken)
    {
        if (qty <= 0)
        {
            return InventoryCostConsumptionResult.Success(0, 0);
        }

        var method = NormalizeValuationMethod(valuationMethod);
        var orderBy = string.Equals(method, InventoryValuationMethodLifo, StringComparison.OrdinalIgnoreCase)
            ? "ORDER BY layer_date DESC, id DESC"
            : "ORDER BY layer_date ASC, id ASC";
        var sql = $@"
SELECT id, remaining_qty, unit_cost
FROM inv_cost_layers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND item_id = @item_id
  AND remaining_qty > 0
{orderBy}
FOR UPDATE;";

        var layerRows = new List<(long Id, decimal RemainingQty, decimal UnitCost)>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("location_id", locationId);
            command.Parameters.AddWithValue("item_id", itemId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                layerRows.Add((reader.GetInt64(0), reader.GetDecimal(1), reader.GetDecimal(2)));
            }
        }

        var remainingToConsume = RoundCost(qty);
        decimal consumedQty = 0;
        decimal consumedValue = 0;

        foreach (var layer in layerRows)
        {
            if (remainingToConsume <= 0)
            {
                break;
            }

            var consumeQty = Math.Min(remainingToConsume, layer.RemainingQty);
            if (consumeQty <= 0)
            {
                continue;
            }

            var newRemainingQty = RoundCost(layer.RemainingQty - consumeQty);
            await using (var update = new NpgsqlCommand(@"
UPDATE inv_cost_layers
SET remaining_qty = @remaining_qty,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                update.Parameters.AddWithValue("id", layer.Id);
                update.Parameters.AddWithValue("remaining_qty", newRemainingQty);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            consumedQty += consumeQty;
            consumedValue += consumeQty * layer.UnitCost;
            remainingToConsume = RoundCost(remainingToConsume - consumeQty);
        }

        if (remainingToConsume > 0)
        {
            if (!allowShortage)
            {
                return InventoryCostConsumptionResult.Failure(
                    $"Cost layer tidak mencukupi (shortage qty {remainingToConsume:N4}).");
            }

            var fallbackUnitCost = consumedQty > 0
                ? consumedValue / consumedQty
                : await GetCurrentAverageCostFromLayersAsync(connection, transaction, companyId, locationId, itemId, cancellationToken);
            fallbackUnitCost = RoundCost(fallbackUnitCost);
            consumedQty += remainingToConsume;
            consumedValue += remainingToConsume * fallbackUnitCost;
        }

        var unitCost = consumedQty <= 0 ? 0 : RoundCost(consumedValue / consumedQty);
        var totalCost = RoundCost(consumedValue);
        return InventoryCostConsumptionResult.Success(unitCost, totalCost);
    }

    private static async Task<decimal> GetCurrentAverageCostFromLayersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
SELECT CASE
           WHEN COALESCE(SUM(remaining_qty), 0) = 0 THEN 0
           ELSE COALESCE(SUM(remaining_qty * unit_cost), 0) / SUM(remaining_qty)
       END AS avg_cost
FROM inv_cost_layers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND item_id = @item_id
  AND remaining_qty > 0;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("item_id", itemId);
        return RoundCost(Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture));
    }

    private static async Task InsertOutboundCostEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long locationId,
        long itemId,
        string sourceType,
        long sourceId,
        long sourceLineId,
        DateTime eventDate,
        decimal qty,
        decimal unitCost,
        decimal totalCost,
        string valuationMethod,
        string inventoryAccountCode,
        string cogsAccountCode,
        long? cogsJournalId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
INSERT INTO inv_cost_outbound_events (
    company_id,
    location_id,
    item_id,
    source_type,
    source_id,
    source_line_id,
    event_date,
    qty,
    unit_cost,
    total_cost,
    valuation_method,
    inventory_account_code,
    cogs_account_code,
    cogs_journal_id,
    created_at
)
VALUES (
    @company_id,
    @location_id,
    @item_id,
    @source_type,
    @source_id,
    @source_line_id,
    @event_date,
    @qty,
    @unit_cost,
    @total_cost,
    @valuation_method,
    @inventory_account_code,
    @cogs_account_code,
    @cogs_journal_id,
    NOW()
);", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("source_type", sourceType.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("source_line_id", sourceLineId);
        command.Parameters.AddWithValue("event_date", eventDate.Date);
        command.Parameters.AddWithValue("qty", RoundCost(qty));
        command.Parameters.AddWithValue("unit_cost", RoundCost(unitCost));
        command.Parameters.AddWithValue("total_cost", RoundCost(totalCost));
        command.Parameters.AddWithValue("valuation_method", NormalizeValuationMethod(valuationMethod));
        command.Parameters.AddWithValue("inventory_account_code", (inventoryAccountCode ?? string.Empty).Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("cogs_account_code", (cogsAccountCode ?? string.Empty).Trim().ToUpperInvariant());
        command.Parameters.Add(new NpgsqlParameter("cogs_journal_id", NpgsqlDbType.Bigint)
        {
            Value = cogsJournalId.HasValue ? cogsJournalId.Value : DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<InventoryValuationSnapshotRow>> SnapshotInventoryValuationByLocationAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long companyId,
        long? locationId,
        CancellationToken cancellationToken)
    {
        var output = new List<InventoryValuationSnapshotRow>();
        await using var command = new NpgsqlCommand(@"
SELECT l.location_id,
       COALESCE(NULLIF(trim(c.account_code), ''), '') AS account_code,
       COALESCE(SUM(l.remaining_qty * l.unit_cost), 0) AS total_value
FROM inv_cost_layers l
JOIN inv_items i ON i.id = l.item_id
LEFT JOIN inv_categories c ON c.id = i.category_id
WHERE l.company_id = @company_id
  AND (@location_id IS NULL OR l.location_id = @location_id)
  AND l.remaining_qty > 0
GROUP BY l.location_id, COALESCE(NULLIF(trim(c.account_code), ''), '')
HAVING ABS(COALESCE(SUM(l.remaining_qty * l.unit_cost), 0)) > 0.0001;", connection, transaction);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
        {
            Value = locationId.HasValue ? locationId.Value : DBNull.Value
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new InventoryValuationSnapshotRow
            {
                LocationId = reader.GetInt64(0),
                AccountCode = reader.GetString(1).Trim().ToUpperInvariant(),
                TotalValue = RoundCost(reader.GetDecimal(2))
            });
        }

        return output;
    }

    private static Dictionary<long, Dictionary<string, decimal>> BuildValuationDiffByLocation(
        IReadOnlyCollection<InventoryValuationSnapshotRow> beforeSnapshot,
        IReadOnlyCollection<InventoryValuationSnapshotRow> afterSnapshot)
    {
        var balanceByKey = new Dictionary<(long LocationId, string AccountCode), decimal>();
        foreach (var row in beforeSnapshot)
        {
            var key = (row.LocationId, row.AccountCode ?? string.Empty);
            if (!balanceByKey.TryAdd(key, -row.TotalValue))
            {
                balanceByKey[key] -= row.TotalValue;
            }
        }

        foreach (var row in afterSnapshot)
        {
            var key = (row.LocationId, row.AccountCode ?? string.Empty);
            if (!balanceByKey.TryAdd(key, row.TotalValue))
            {
                balanceByKey[key] += row.TotalValue;
            }
        }

        var output = new Dictionary<long, Dictionary<string, decimal>>();
        foreach (var entry in balanceByKey)
        {
            var diff = RoundAmount(entry.Value);
            if (Math.Abs(diff) <= 0.009m)
            {
                continue;
            }

            if (!output.TryGetValue(entry.Key.LocationId, out var accountMap))
            {
                accountMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                output[entry.Key.LocationId] = accountMap;
            }

            accountMap[entry.Key.AccountCode] = diff;
        }

        return output;
    }
}
