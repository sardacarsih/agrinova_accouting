using Npgsql;
using NpgsqlTypes;
using Accounting.Services;

internal static partial class Program
{
    private static async Task<long> CreateAndPostStockTransactionAsync(
        PostgresAccessControlService service,
        ManagedStockTransaction header,
        IReadOnlyCollection<ManagedStockTransactionLine> lines,
        string actorUsername)
    {
        var saveResult = await service.SaveStockTransactionDraftAsync(header, lines, actorUsername);
        Assert(
            saveResult.IsSuccess && saveResult.EntityId.HasValue && saveResult.EntityId.Value > 0,
            $"Failed to save stock transaction draft ({header.ReferenceNo}): {saveResult.Message}");

        var transactionId = saveResult.EntityId!.Value;

        var submitResult = await service.SubmitStockTransactionAsync(transactionId, actorUsername);
        Assert(submitResult.IsSuccess, $"Failed to submit stock transaction {transactionId}: {submitResult.Message}");

        var approveResult = await service.ApproveStockTransactionAsync(transactionId, actorUsername);
        Assert(approveResult.IsSuccess, $"Failed to approve stock transaction {transactionId}: {approveResult.Message}");

        var postResult = await service.PostStockTransactionAsync(transactionId, actorUsername);
        Assert(postResult.IsSuccess, $"Failed to post stock transaction {transactionId}: {postResult.Message}");

        return transactionId;
    }

    private static async Task<decimal> GetPostedStockTransactionLineUnitCostAsync(long transactionId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            @"SELECT unit_cost
FROM inv_stock_transaction_lines
WHERE transaction_id = @transaction_id
ORDER BY line_no
LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("transaction_id", transactionId);

        var scalar = await command.ExecuteScalarAsync();
        Assert(
            scalar is not null && scalar is not DBNull,
            $"Posted stock transaction {transactionId} has no line to read unit_cost.");

        return Convert.ToDecimal(scalar);
    }

    private static async Task<long> CreateAndPostStockOpnameAsync(
        PostgresAccessControlService service,
        ManagedStockOpname header,
        IReadOnlyCollection<ManagedStockOpnameLine> lines,
        string actorUsername)
    {
        var saveResult = await service.SaveStockOpnameDraftAsync(header, lines, actorUsername);
        Assert(
            saveResult.IsSuccess && saveResult.EntityId.HasValue && saveResult.EntityId.Value > 0,
            $"Failed to save stock opname draft ({header.OpnameNo}): {saveResult.Message}");

        var opnameId = saveResult.EntityId!.Value;

        var submitResult = await service.SubmitStockOpnameAsync(opnameId, actorUsername);
        Assert(submitResult.IsSuccess, $"Failed to submit stock opname {opnameId}: {submitResult.Message}");

        var approveResult = await service.ApproveStockOpnameAsync(opnameId, actorUsername);
        Assert(approveResult.IsSuccess, $"Failed to approve stock opname {opnameId}: {approveResult.Message}");

        var postResult = await service.PostStockOpnameAsync(opnameId, actorUsername);
        Assert(postResult.IsSuccess, $"Failed to post stock opname {opnameId}: {postResult.Message}");

        return opnameId;
    }

    private static PostgresAccessControlService CreateService()
    {
        var options = DatabaseAuthOptions.FromConfiguration();
        Assert(!string.IsNullOrWhiteSpace(options.ConnectionString), "Connection string is not configured.");
        Assert(!options.ConnectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase),
            "Set AGRINOVA_PG_CONNECTION before running integration tests.");
        return new PostgresAccessControlService(options);
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var options = DatabaseAuthOptions.FromConfiguration();
        var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<(bool Exists, bool IsOpen)> GetAccountingPeriodStateAsync(
        long companyId,
        long locationId,
        DateTime periodMonth)
    {
        var monthStart = new DateTime(periodMonth.Year, periodMonth.Month, 1);
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            @"SELECT is_open
FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
            connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", monthStart);

        var scalar = await command.ExecuteScalarAsync();
        if (scalar is bool isOpen)
        {
            return (true, isOpen);
        }

        return (false, true);
    }

    private static async Task SetAccountingPeriodStateAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        bool isOpen,
        string note)
    {
        var monthStart = new DateTime(periodMonth.Year, periodMonth.Month, 1);
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            @"
INSERT INTO gl_accounting_periods (
    company_id,
    location_id,
    period_month,
    is_open,
    closed_at,
    closed_by,
    note,
    created_at,
    updated_at)
VALUES (
    @company_id,
    @location_id,
    @period_month,
    @is_open,
    CASE WHEN @is_open THEN NULL ELSE NOW() END,
    CASE WHEN @is_open THEN NULL ELSE 'itest' END,
    @note,
    NOW(),
    NOW())
ON CONFLICT (company_id, location_id, period_month) DO UPDATE
SET is_open = EXCLUDED.is_open,
    closed_at = CASE WHEN EXCLUDED.is_open THEN NULL ELSE NOW() END,
    closed_by = CASE WHEN EXCLUDED.is_open THEN NULL ELSE 'itest' END,
    note = EXCLUDED.note,
    updated_at = NOW();",
            connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("period_month", monthStart);
        command.Parameters.AddWithValue("is_open", isOpen);
        command.Parameters.AddWithValue("note", note);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> GetSystemSettingAsync(string settingKey)
    {
        var service = CreateService();
        _ = await service.GetInventoryMasterCompanyIdAsync();

        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            @"SELECT setting_value
FROM app_system_settings
WHERE setting_key = @setting_key
LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("setting_key", settingKey);
        var scalar = await command.ExecuteScalarAsync();
        if (scalar is null || scalar is DBNull)
        {
            return null;
        }

        return Convert.ToString(scalar)?.Trim();
    }

    private static async Task SetSystemSettingAsync(string settingKey, string? settingValue, string updatedBy)
    {
        var service = CreateService();
        _ = await service.GetInventoryMasterCompanyIdAsync();

        await using var connection = await OpenConnectionAsync();
        if (string.IsNullOrWhiteSpace(settingValue))
        {
            await using var deleteCommand = new NpgsqlCommand(
                @"DELETE FROM app_system_settings
WHERE setting_key = @setting_key;",
                connection);
            deleteCommand.Parameters.AddWithValue("setting_key", settingKey);
            await deleteCommand.ExecuteNonQueryAsync();
            return;
        }

        await using var upsertCommand = new NpgsqlCommand(
            @"INSERT INTO app_system_settings(setting_key, setting_value, updated_by, updated_at)
VALUES (@setting_key, @setting_value, @updated_by, NOW())
ON CONFLICT (setting_key) DO UPDATE
SET setting_value = EXCLUDED.setting_value,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();",
            connection);
        upsertCommand.Parameters.AddWithValue("setting_key", settingKey);
        upsertCommand.Parameters.AddWithValue("setting_value", settingValue.Trim());
        upsertCommand.Parameters.AddWithValue("updated_by", string.IsNullOrWhiteSpace(updatedBy) ? "itest" : updatedBy.Trim());
        await upsertCommand.ExecuteNonQueryAsync();
    }

    private static async Task CleanupTemporaryInventoryCostingCompanyAsync(long companyId)
    {
        await using var connection = await OpenConnectionAsync();

        await using (var deleteLedger = new NpgsqlCommand(
            "DELETE FROM gl_ledger_entries WHERE company_id = @company_id;",
            connection))
        {
            deleteLedger.Parameters.AddWithValue("company_id", companyId);
            await deleteLedger.ExecuteNonQueryAsync();
        }

        await using (var deleteJournalDetails = new NpgsqlCommand(
            @"DELETE FROM gl_journal_details
WHERE header_id IN (SELECT id FROM gl_journal_headers WHERE company_id = @company_id);",
            connection))
        {
            deleteJournalDetails.Parameters.AddWithValue("company_id", companyId);
            await deleteJournalDetails.ExecuteNonQueryAsync();
        }

        await using (var deleteJournalHeaders = new NpgsqlCommand(
            "DELETE FROM gl_journal_headers WHERE company_id = @company_id;",
            connection))
        {
            deleteJournalHeaders.Parameters.AddWithValue("company_id", companyId);
            await deleteJournalHeaders.ExecuteNonQueryAsync();
        }

        await using (var deleteOutbound = new NpgsqlCommand(
            "DELETE FROM inv_cost_outbound_events WHERE company_id = @company_id;",
            connection))
        {
            deleteOutbound.Parameters.AddWithValue("company_id", companyId);
            await deleteOutbound.ExecuteNonQueryAsync();
        }

        await using (var deleteLayers = new NpgsqlCommand(
            "DELETE FROM inv_cost_layers WHERE company_id = @company_id;",
            connection))
        {
            deleteLayers.Parameters.AddWithValue("company_id", companyId);
            await deleteLayers.ExecuteNonQueryAsync();
        }

        await using (var deleteStock = new NpgsqlCommand(
            "DELETE FROM inv_stock WHERE company_id = @company_id;",
            connection))
        {
            deleteStock.Parameters.AddWithValue("company_id", companyId);
            await deleteStock.ExecuteNonQueryAsync();
        }

        await using (var deleteStockTx = new NpgsqlCommand(
            "DELETE FROM inv_stock_transactions WHERE company_id = @company_id;",
            connection))
        {
            deleteStockTx.Parameters.AddWithValue("company_id", companyId);
            await deleteStockTx.ExecuteNonQueryAsync();
        }

        await using (var deleteOpname = new NpgsqlCommand(
            "DELETE FROM inv_stock_opname WHERE company_id = @company_id;",
            connection))
        {
            deleteOpname.Parameters.AddWithValue("company_id", companyId);
            await deleteOpname.ExecuteNonQueryAsync();
        }

        await using (var deleteLocationSettings = new NpgsqlCommand(
            "DELETE FROM inv_location_costing_settings WHERE company_id = @company_id;",
            connection))
        {
            deleteLocationSettings.Parameters.AddWithValue("company_id", companyId);
            await deleteLocationSettings.ExecuteNonQueryAsync();
        }

        await using (var deleteCompanySettings = new NpgsqlCommand(
            "DELETE FROM inv_company_settings WHERE company_id = @company_id;",
            connection))
        {
            deleteCompanySettings.Parameters.AddWithValue("company_id", companyId);
            await deleteCompanySettings.ExecuteNonQueryAsync();
        }

        await using (var deleteAccounts = new NpgsqlCommand(
            "DELETE FROM gl_accounts WHERE company_id = @company_id;",
            connection))
        {
            deleteAccounts.Parameters.AddWithValue("company_id", companyId);
            await deleteAccounts.ExecuteNonQueryAsync();
        }

        await using (var deleteLocations = new NpgsqlCommand(
            "DELETE FROM org_locations WHERE company_id = @company_id;",
            connection))
        {
            deleteLocations.Parameters.AddWithValue("company_id", companyId);
            await deleteLocations.ExecuteNonQueryAsync();
        }

        await using (var deleteCompanyAudit = new NpgsqlCommand(
            @"DELETE FROM sec_audit_logs
WHERE details ILIKE @company_mark
   OR details ILIKE @company_mark_alt;",
            connection))
        {
            deleteCompanyAudit.Parameters.AddWithValue("company_mark", $"%company_id={companyId}%");
            deleteCompanyAudit.Parameters.AddWithValue("company_mark_alt", $"%company={companyId};%");
            await deleteCompanyAudit.ExecuteNonQueryAsync();
        }

        await using (var deleteCompany = new NpgsqlCommand(
            "DELETE FROM org_companies WHERE id = @company_id;",
            connection))
        {
            deleteCompany.Parameters.AddWithValue("company_id", companyId);
            await deleteCompany.ExecuteNonQueryAsync();
        }
    }

    private static async Task CleanupInventoryArtifactsByCodesAsync(
        NpgsqlConnection connection,
        long companyId,
        IReadOnlyCollection<string> categoryCodes,
        IReadOnlyCollection<string> itemCodes)
    {
        var normalizedCategoryCodes = (categoryCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var normalizedItemCodes = (itemCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedItemCodes.Length > 0)
        {
            await using (var deleteStock = new NpgsqlCommand(
                @"DELETE FROM inv_stock s
USING inv_items i
WHERE s.item_id = i.id
  AND upper(i.item_code) = ANY(@item_codes);",
                connection))
            {
                deleteStock.Parameters.Add(new NpgsqlParameter("item_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = normalizedItemCodes
                });
                await deleteStock.ExecuteNonQueryAsync();
            }

            await using (var deleteItems = new NpgsqlCommand(
                @"DELETE FROM inv_items
WHERE upper(item_code) = ANY(@item_codes);",
                connection))
            {
                deleteItems.Parameters.Add(new NpgsqlParameter("item_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = normalizedItemCodes
                });
                await deleteItems.ExecuteNonQueryAsync();
            }
        }

        if (normalizedCategoryCodes.Length > 0)
        {
            await using var deleteCategories = new NpgsqlCommand(
                @"DELETE FROM inv_categories
WHERE upper(category_code) = ANY(@category_codes);",
                connection);
            deleteCategories.Parameters.Add(new NpgsqlParameter("category_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = normalizedCategoryCodes
            });
            await deleteCategories.ExecuteNonQueryAsync();
        }
    }

    private static async Task RestoreCentralSyncSystemSettingsAsync(
        string? masterCompanyId,
        string? baseUrl,
        string? apiKey,
        string? uploadPath,
        string? downloadPath,
        string? timeout)
    {
        await SetSystemSettingAsync(InventoryMasterCompanySettingKey, masterCompanyId, "itest");
        await SetSystemSettingAsync(CentralSyncBaseUrlSettingKey, baseUrl, "itest");
        await SetSystemSettingAsync(CentralSyncApiKeySettingKey, apiKey, "itest");
        await SetSystemSettingAsync(CentralSyncUploadPathSettingKey, uploadPath, "itest");
        await SetSystemSettingAsync(CentralSyncDownloadPathSettingKey, downloadPath, "itest");
        await SetSystemSettingAsync(CentralSyncTimeoutSettingKey, timeout, "itest");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
