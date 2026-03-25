using System.Data.Common;
using System.Globalization;
using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private const string MatchStatusMatch = "MATCH";
    private const string MatchStatusMissingTransfer = "MISSING_TRANSFER";
    private const string MatchStatusMissingLk = "MISSING_LK";
    private const string MatchStatusQtyMismatch = "QTY_MISMATCH";

    private const string DefaultOracleOutboundSql = @"
SELECT
    TRUNC(t.transaction_date) AS tx_date,
    TRIM(t.item_code) AS item_code,
    TRIM(NVL(t.item_name, '')) AS item_name,
    TRIM(t.destination_warehouse_code) AS warehouse_code,
    SUM(NVL(t.qty, 0)) AS qty
FROM lk_barang_keluar t
WHERE TRUNC(t.transaction_date) BETWEEN :date_from AND :date_to
GROUP BY
    TRUNC(t.transaction_date),
    TRIM(t.item_code),
    TRIM(NVL(t.item_name, '')),
    TRIM(t.destination_warehouse_code)";

    public async Task<List<InventoryOutboundCompareRow>> GetInventoryOutboundCompareReportAsync(
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureInventorySchemaAsync(cancellationToken);

        if (companyId <= 0 || locationId <= 0)
        {
            return [];
        }

        var safeFrom = dateFrom.Date;
        var safeTo = dateTo.Date;
        if (safeTo < safeFrom)
        {
            (safeFrom, safeTo) = (safeTo, safeFrom);
        }

        await using var pgConnection = new NpgsqlConnection(_options.ConnectionString);
        await pgConnection.OpenAsync(cancellationToken);

        var settings = await GetInventoryCentralSyncSettingsInternalAsync(pgConnection, null, cancellationToken);
        var internalRows = await LoadPostedTransferCompareSnapshotsAsync(
            pgConnection,
            companyId,
            locationId,
            safeFrom,
            safeTo,
            cancellationToken);
        var lkRows = await LoadLkOracleCompareSnapshotsAsync(
            settings,
            companyId,
            locationId,
            safeFrom,
            safeTo,
            cancellationToken);

        var internalMap = AggregateSnapshots(internalRows);
        var lkMap = AggregateSnapshots(lkRows);

        var keys = internalMap.Keys
            .Union(lkMap.Keys)
            .OrderBy(x => x.TxDate)
            .ThenBy(x => x.WarehouseCode, StringComparer.Ordinal)
            .ThenBy(x => x.ItemCode, StringComparer.Ordinal)
            .ToList();

        var output = new List<InventoryOutboundCompareRow>(keys.Count);
        foreach (var key in keys)
        {
            var hasInternal = internalMap.TryGetValue(key, out var internalSnapshot);
            var hasLk = lkMap.TryGetValue(key, out var lkSnapshot);

            var qtyInternal = hasInternal ? internalSnapshot.Qty : 0m;
            var qtyLk = hasLk ? lkSnapshot.Qty : 0m;
            var qtyDiff = qtyInternal - qtyLk;

            var status = MatchStatusQtyMismatch;
            if (hasInternal && hasLk)
            {
                status = qtyDiff == 0m ? MatchStatusMatch : MatchStatusQtyMismatch;
            }
            else if (hasLk)
            {
                status = MatchStatusMissingTransfer;
            }
            else if (hasInternal)
            {
                status = MatchStatusMissingLk;
            }

            output.Add(new InventoryOutboundCompareRow
            {
                TxDate = key.TxDate,
                ItemCode = key.ItemCode,
                ItemName = FirstNonEmpty(internalSnapshot.ItemName, lkSnapshot.ItemName, key.ItemCode),
                WarehouseCode = key.WarehouseCode,
                WarehouseName = FirstNonEmpty(internalSnapshot.WarehouseName, lkSnapshot.WarehouseName, key.WarehouseCode),
                QtyLkOracle = qtyLk,
                QtyTransferInternal = qtyInternal,
                QtyDiff = qtyDiff,
                MatchStatus = status
            });
        }

        return output;
    }

    private static Dictionary<OutboundCompareKey, OutboundCompareSnapshot> AggregateSnapshots(
        IReadOnlyCollection<OutboundCompareSnapshot> rows)
    {
        var output = new Dictionary<OutboundCompareKey, OutboundCompareSnapshot>();
        foreach (var row in rows)
        {
            var key = new OutboundCompareKey(
                row.TxDate.Date,
                NormalizeCompareCode(row.ItemCode),
                NormalizeCompareCode(row.WarehouseCode));

            if (output.TryGetValue(key, out var existing))
            {
                output[key] = new OutboundCompareSnapshot(
                    key.TxDate,
                    key.ItemCode,
                    FirstNonEmpty(existing.ItemName, row.ItemName, key.ItemCode),
                    key.WarehouseCode,
                    FirstNonEmpty(existing.WarehouseName, row.WarehouseName, key.WarehouseCode),
                    existing.Qty + row.Qty);
                continue;
            }

            output[key] = new OutboundCompareSnapshot(
                key.TxDate,
                key.ItemCode,
                FirstNonEmpty(row.ItemName, key.ItemCode),
                key.WarehouseCode,
                FirstNonEmpty(row.WarehouseName, key.WarehouseCode),
                row.Qty);
        }

        return output;
    }

    private async Task<List<OutboundCompareSnapshot>> LoadPostedTransferCompareSnapshotsAsync(
        NpgsqlConnection connection,
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken)
    {
        var output = new List<OutboundCompareSnapshot>();
        await using var command = new NpgsqlCommand(@"
SELECT h.transaction_date AS tx_date,
       upper(trim(i.item_code)) AS item_code,
       max(COALESCE(NULLIF(trim(i.item_name), ''), trim(i.item_code))) AS item_name,
       upper(trim(COALESCE(w.warehouse_code, ''))) AS warehouse_code,
       max(COALESCE(NULLIF(trim(w.warehouse_name), ''), trim(COALESCE(w.warehouse_code, '')))) AS warehouse_name,
       COALESCE(SUM(l.qty), 0) AS qty
FROM inv_stock_transactions h
JOIN inv_stock_transaction_lines l ON l.transaction_id = h.id
JOIN inv_items i ON i.id = l.item_id
LEFT JOIN inv_warehouses w ON w.id = h.destination_warehouse_id
WHERE h.company_id = @company_id
  AND h.location_id = @location_id
  AND h.is_active = TRUE
  AND upper(h.transaction_type) = 'TRANSFER'
  AND upper(h.status) = 'POSTED'
  AND h.transaction_date >= @date_from
  AND h.transaction_date <= @date_to
GROUP BY h.transaction_date, upper(trim(i.item_code)), upper(trim(COALESCE(w.warehouse_code, '')))
ORDER BY h.transaction_date, upper(trim(COALESCE(w.warehouse_code, ''))), upper(trim(i.item_code));", connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("date_from", dateFrom);
        command.Parameters.AddWithValue("date_to", dateTo);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new OutboundCompareSnapshot(
                reader.GetDateTime(0).Date,
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? 0m : reader.GetDecimal(5)));
        }

        return output;
    }

    private static async Task<List<OutboundCompareSnapshot>> LoadLkOracleCompareSnapshotsAsync(
        InventoryCentralSyncSettings settings,
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken)
    {
        var connectionString = (settings.BaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Koneksi Oracle LK belum dikonfigurasi. Isi Central Sync Base URL dengan connection string Oracle.");
        }

        await using var connection = CreateOracleDbConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = ResolveOracleOutboundSql(settings);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 30;

        AddCommandParameterIfReferenced(command, sql, "date_from", dateFrom);
        AddCommandParameterIfReferenced(command, sql, "date_to", dateTo);
        AddCommandParameterIfReferenced(command, sql, "company_id", companyId);
        AddCommandParameterIfReferenced(command, sql, "location_id", locationId);

        var output = new List<OutboundCompareSnapshot>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var txDateOrdinal = GetRequiredOrdinal(reader, "tx_date");
            var itemCodeOrdinal = GetRequiredOrdinal(reader, "item_code");
            var itemNameOrdinal = GetOptionalOrdinal(reader, "item_name");
            var warehouseCodeOrdinal = GetRequiredOrdinal(reader, "warehouse_code");
            var warehouseNameOrdinal = GetOptionalOrdinal(reader, "warehouse_name");
            var qtyOrdinal = GetRequiredOrdinal(reader, "qty");

            while (await reader.ReadAsync(cancellationToken))
            {
                output.Add(new OutboundCompareSnapshot(
                    ReadDate(reader, txDateOrdinal),
                    ReadString(reader, itemCodeOrdinal),
                    ReadString(reader, itemNameOrdinal),
                    ReadString(reader, warehouseCodeOrdinal),
                    ReadString(reader, warehouseNameOrdinal),
                    ReadDecimal(reader, qtyOrdinal)));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Gagal membaca data barang keluar dari Oracle LK: {ex.Message}. Pastikan SQL menghasilkan kolom tx_date, item_code, warehouse_code, qty.",
                ex);
        }

        return output;
    }

    private static DbConnection CreateOracleDbConnection(string connectionString)
    {
        var connectionType = Type.GetType(
            "Oracle.ManagedDataAccess.Client.OracleConnection, Oracle.ManagedDataAccess",
            throwOnError: false);
        if (connectionType is null)
        {
            throw new InvalidOperationException(
                "Provider Oracle.ManagedDataAccess belum tersedia. Pastikan assembly Oracle.ManagedDataAccess terpasang pada aplikasi.");
        }

        if (Activator.CreateInstance(connectionType) is not DbConnection connection)
        {
            throw new InvalidOperationException("Gagal membuat koneksi Oracle.");
        }

        connection.ConnectionString = connectionString;
        return connection;
    }

    private static string ResolveOracleOutboundSql(InventoryCentralSyncSettings settings)
    {
        var sqlFromEnv = (Environment.GetEnvironmentVariable("LK_ORACLE_OUTBOUND_SQL") ?? string.Empty).Trim();
        if (LooksLikeSelectSql(sqlFromEnv))
        {
            return sqlFromEnv;
        }

        var sqlFromSettings = (settings.DownloadPath ?? string.Empty).Trim();
        if (LooksLikeSelectSql(sqlFromSettings))
        {
            return sqlFromSettings;
        }

        return DefaultOracleOutboundSql;
    }

    private static bool LooksLikeSelectSql(string sql)
    {
        return !string.IsNullOrWhiteSpace(sql) &&
               sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCommandParameterIfReferenced(DbCommand command, string sql, string parameterName, object parameterValue)
    {
        if (!ContainsParameterToken(sql, parameterName))
        {
            return;
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = parameterValue;
        command.Parameters.Add(parameter);
    }

    private static bool ContainsParameterToken(string sql, string parameterName)
    {
        return sql.Contains($":{parameterName}", StringComparison.OrdinalIgnoreCase) ||
               sql.Contains($"@{parameterName}", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetRequiredOrdinal(DbDataReader reader, string columnName)
    {
        var ordinal = GetOptionalOrdinal(reader, columnName);
        if (ordinal >= 0)
        {
            return ordinal;
        }

        throw new InvalidOperationException($"Kolom '{columnName}' tidak ditemukan.");
    }

    private static int GetOptionalOrdinal(DbDataReader reader, string columnName)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (string.Equals(reader.GetName(index), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static DateTime ReadDate(DbDataReader reader, int ordinal)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal))
        {
            return DateTime.MinValue.Date;
        }

        return Convert.ToDateTime(reader.GetValue(ordinal), CultureInfo.InvariantCulture).Date;
    }

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal))
        {
            return 0m;
        }

        return Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static string ReadString(DbDataReader reader, int ordinal)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal))
        {
            return string.Empty;
        }

        return (reader.GetValue(ordinal)?.ToString() ?? string.Empty).Trim();
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeCompareCode(string value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private readonly record struct OutboundCompareKey(
        DateTime TxDate,
        string ItemCode,
        string WarehouseCode);

    private readonly record struct OutboundCompareSnapshot(
        DateTime TxDate,
        string ItemCode,
        string ItemName,
        string WarehouseCode,
        string WarehouseName,
        decimal Qty);
}
