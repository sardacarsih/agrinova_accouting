using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public async Task<Dictionary<long, decimal>> GetOutboundAutoUnitCostLookupAsync(
        long companyId,
        long locationId,
        IReadOnlyCollection<long> itemIds,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        var output = new Dictionary<long, decimal>();
        if (companyId <= 0 || locationId <= 0 || itemIds is null || itemIds.Count == 0)
        {
            return output;
        }

        var distinctItemIds = itemIds
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
        if (distinctItemIds.Length == 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(@"
SELECT item_id,
       CASE
           WHEN COALESCE(SUM(remaining_qty), 0) = 0 THEN 0
           ELSE COALESCE(SUM(remaining_qty * unit_cost), 0) / SUM(remaining_qty)
       END AS avg_cost
FROM inv_cost_layers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND item_id = ANY(@item_ids)
GROUP BY item_id;", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.Add(new NpgsqlParameter("item_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = distinctItemIds
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemId = reader.GetInt64(0);
            var avgCost = reader.IsDBNull(1) ? 0m : RoundCost(reader.GetDecimal(1));
            output[itemId] = avgCost;
        }

        foreach (var itemId in distinctItemIds)
        {
            if (!output.ContainsKey(itemId))
            {
                output[itemId] = 0m;
            }
        }

        return output;
    }
}
