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

    private static async Task<long> CreateWarehouseAsync(
        PostgresAccessControlService service,
        long companyId,
        string warehouseCode,
        string warehouseName,
        long? locationId,
        string actorUsername)
    {
        var saveResult = await service.SaveWarehouseAsync(
            companyId,
            new ManagedWarehouse
            {
                Id = 0,
                CompanyId = companyId,
                Code = warehouseCode,
                Name = warehouseName,
                LocationId = locationId,
                IsActive = true
            },
            actorUsername);
        Assert(
            saveResult.IsSuccess && saveResult.EntityId.HasValue && saveResult.EntityId.Value > 0,
            $"Failed to save warehouse {warehouseCode}: {saveResult.Message}");

        return saveResult.EntityId!.Value;
    }

    private static string BuildTestAccountCode(
        string accountType,
        long seed,
        bool isPosting,
        int variant = 0)
    {
        var prefix = accountType.Trim().ToUpperInvariant() switch
        {
            "ASSET" => "20",
            "EXPENSE" => variant % 2 == 0 ? "80" : "81",
            "EQUITY" => "30",
            "REVENUE" => "40",
            _ => throw new InvalidOperationException($"Unsupported test account type: {accountType}.")
        };

        var middleSegment = (int)(Math.Abs((seed / 1000) + variant) % 100000);
        var suffixSegment = isPosting
            ? (int)(Math.Abs(seed + variant) % 999) + 1
            : 0;
        return $"{prefix}.{middleSegment:00000}.{suffixSegment:000}";
    }

    private static (ManagedAccount DebitAccount, ManagedAccount CreditAccount, string PostingCostCenterCode) ResolveSimpleJournalAccounts(
        JournalWorkspaceData workspace)
    {
        var postingCostCenterCode = workspace.CostCenters
            .FirstOrDefault(costCenter => costCenter.IsActive && costCenter.IsPosting)
            ?.CostCenterCode
            ?.Trim()
            .ToUpperInvariant()
            ?? string.Empty;

        var compatibleAccounts = workspace.Accounts
            .Where(account =>
                account.IsActive &&
                account.IsPosting &&
                !account.RequiresDepartment &&
                !account.RequiresProject &&
                !account.RequiresSubledger &&
                (!account.RequiresCostCenter || !string.IsNullOrWhiteSpace(postingCostCenterCode)))
            .ToList();
        Assert(compatibleAccounts.Count >= 2, "At least two compatible posting accounts are required for journal integration tests.");

        return (compatibleAccounts[0], compatibleAccounts[1], postingCostCenterCode);
    }

    private static ManagedJournalLine CreateJournalLine(
        int lineNo,
        ManagedAccount account,
        string description,
        decimal debit,
        decimal credit,
        string postingCostCenterCode)
    {
        return new ManagedJournalLine
        {
            LineNo = lineNo,
            AccountCode = account.Code,
            Description = description,
            Debit = debit,
            Credit = credit,
            CostCenterCode = account.RequiresCostCenter ? postingCostCenterCode : string.Empty
        };
    }

    private static ManagedJournalLine CreateJournalLine(
        int lineNo,
        string accountCode,
        string description,
        decimal debit,
        decimal credit)
    {
        return new ManagedJournalLine
        {
            LineNo = lineNo,
            AccountCode = accountCode,
            Description = description,
            Debit = debit,
            Credit = credit
        };
    }

    private static async Task<(string DebitAccountCode, string CreditAccountCode, List<string> CreatedAccountCodes)> EnsureSimpleJournalAccountCodesAsync(
        long companyId,
        long seed,
        string actorUsername)
    {
        var createdAccountCodes = new List<string>();

        var (debitAccountCode, createdDebitId) = await EnsurePostingAccountOfTypeAsync(companyId, "ASSET", seed, actorUsername);
        if (createdDebitId.HasValue)
        {
            createdAccountCodes.Add(debitAccountCode);
        }

        var (creditAccountCode, createdCreditId) = await EnsurePostingAccountOfTypeAsync(companyId, "EXPENSE", seed + 1, actorUsername);
        if (createdCreditId.HasValue)
        {
            createdAccountCodes.Add(creditAccountCode);
        }

        return (debitAccountCode, creditAccountCode, createdAccountCodes);
    }

    private static async Task<(string AccountCode, long? CreatedAccountId)> EnsurePostingAccountOfTypeAsync(
        long companyId,
        string accountType,
        long seed,
        string actorUsername,
        string? preferredCode = null,
        string? preferredName = null)
    {
        var normalizedAccountType = accountType.Trim().ToUpperInvariant();
        await using var connection = await OpenConnectionAsync();

        await using (var selectCommand = new NpgsqlCommand(
            @"SELECT id, account_code
 FROM gl_accounts
 WHERE company_id = @company_id
   AND is_active = TRUE
   AND is_posting = TRUE
   AND upper(account_type) = @account_type
   AND COALESCE(requires_department, FALSE) = FALSE
   AND COALESCE(requires_project, FALSE) = FALSE
   AND COALESCE(requires_cost_center, FALSE) = FALSE
   AND COALESCE(requires_partner, FALSE) = FALSE
   AND COALESCE(allowed_subledger_type, '') = ''
 ORDER BY account_code
 LIMIT 1;",
            connection))
        {
            selectCommand.Parameters.AddWithValue("company_id", companyId);
            selectCommand.Parameters.AddWithValue("account_type", normalizedAccountType);
            await using var reader = await selectCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (reader.GetString(1), null);
            }
        }

        var accountCode = !string.IsNullOrWhiteSpace(preferredCode)
            ? preferredCode.Trim().ToUpperInvariant()
            : BuildTestAccountCode(normalizedAccountType, seed, isPosting: true);
        var accountName = !string.IsNullOrWhiteSpace(preferredName)
            ? preferredName.Trim()
            : $"ITEST {normalizedAccountType} {seed}";
        var normalBalance = normalizedAccountType is "LIABILITY" or "EQUITY" or "REVENUE" ? "C" : "D";

        await using var insertCommand = new NpgsqlCommand(
            @"INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    is_posting,
    hierarchy_level,
    is_active,
    requires_department,
    requires_project,
    requires_cost_center,
    requires_partner,
    allowed_subledger_type,
    created_by,
    updated_by,
    created_at,
    updated_at)
VALUES (
    @company_id,
    @account_code,
    @account_name,
    @account_type,
    @normal_balance,
    NULL,
    TRUE,
    1,
    TRUE,
    FALSE,
    FALSE,
    FALSE,
    FALSE,
    '',
    @actor,
    @actor,
    NOW(),
    NOW())
RETURNING id;",
            connection);
        insertCommand.Parameters.AddWithValue("company_id", companyId);
        insertCommand.Parameters.AddWithValue("account_code", accountCode);
        insertCommand.Parameters.AddWithValue("account_name", accountName);
        insertCommand.Parameters.AddWithValue("account_type", normalizedAccountType);
        insertCommand.Parameters.AddWithValue("normal_balance", normalBalance);
        insertCommand.Parameters.AddWithValue("actor", actorUsername);
        var createdId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync());

        return (accountCode, createdId);
    }

    private static async Task<string> ResolvePostingAccountCodeAsync(
        long companyId,
        string preferredCode,
        string accountType)
    {
        await using var connection = await OpenConnectionAsync();

        if (!string.IsNullOrWhiteSpace(preferredCode))
        {
            await using var preferredCommand = new NpgsqlCommand(
                @"SELECT account_code
FROM gl_accounts
WHERE company_id = @company_id
  AND upper(account_code) = @account_code
  AND is_active = TRUE
  AND is_posting = TRUE
  AND upper(account_type) = @account_type
LIMIT 1;",
                connection);
            preferredCommand.Parameters.AddWithValue("company_id", companyId);
            preferredCommand.Parameters.AddWithValue("account_code", preferredCode.Trim().ToUpperInvariant());
            preferredCommand.Parameters.AddWithValue("account_type", accountType.Trim().ToUpperInvariant());

            var preferredScalar = await preferredCommand.ExecuteScalarAsync();
            if (preferredScalar is not null && preferredScalar is not DBNull)
            {
                return Convert.ToString(preferredScalar) ?? string.Empty;
            }
        }

        await using var fallbackCommand = new NpgsqlCommand(
            @"SELECT account_code
FROM gl_accounts
WHERE company_id = @company_id
  AND is_active = TRUE
  AND is_posting = TRUE
  AND upper(account_type) = @account_type
ORDER BY account_code
LIMIT 1;",
            connection);
        fallbackCommand.Parameters.AddWithValue("company_id", companyId);
        fallbackCommand.Parameters.AddWithValue("account_type", accountType.Trim().ToUpperInvariant());

        var fallbackScalar = await fallbackCommand.ExecuteScalarAsync();
        return fallbackScalar is not null && fallbackScalar is not DBNull
            ? Convert.ToString(fallbackScalar) ?? string.Empty
            : string.Empty;
    }

    private static async Task<(long CompanyId, long LocationId, string InventoryAccountCode, string CogsAccountCode)> FindInventoryReportReadyContextAsync(
        PostgresAccessControlService service,
        bool requireCogsAccount)
    {
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        foreach (var company in accessOptions.Companies)
        {
            var companyLocations = accessOptions.Locations
                .Where(location => location.CompanyId == company.Id)
                .ToArray();
            if (companyLocations.Length == 0)
            {
                continue;
            }

            var companyCostingSettings = await service.GetInventoryCostingSettingsAsync(company.Id);
            var companyCogsAccountCode = !string.IsNullOrWhiteSpace(companyCostingSettings.CogsAccountCode)
                ? companyCostingSettings.CogsAccountCode.Trim().ToUpperInvariant()
                : string.Empty;

            foreach (var location in companyLocations)
            {
                var workspace = await service.GetInventoryWorkspaceDataAsync(company.Id, location.Id);
                var categoryInventoryAccountCode = workspace.Categories
                    .FirstOrDefault(category => !string.IsNullOrWhiteSpace(category.AccountCode))
                    ?.AccountCode
                    ?.Trim()
                    .ToUpperInvariant();
                var inventoryAccountCode = await ResolvePostingAccountCodeAsync(
                    company.Id,
                    categoryInventoryAccountCode ?? string.Empty,
                    "ASSET");
                if (string.IsNullOrWhiteSpace(inventoryAccountCode))
                {
                    continue;
                }

                var locationCostingSettings = await service.GetInventoryLocationCostingSettingsAsync(company.Id, location.Id);
                var configuredCogsAccountCode = !string.IsNullOrWhiteSpace(locationCostingSettings.CogsAccountCode)
                    ? locationCostingSettings.CogsAccountCode.Trim().ToUpperInvariant()
                    : companyCogsAccountCode;
                var cogsAccountCode = await ResolvePostingAccountCodeAsync(
                    company.Id,
                    configuredCogsAccountCode,
                    "EXPENSE");
                if (requireCogsAccount && string.IsNullOrWhiteSpace(cogsAccountCode))
                {
                    continue;
                }

                return (company.Id, location.Id, inventoryAccountCode, cogsAccountCode);
            }
        }

        Assert(false, requireCogsAccount
            ? "No accessible inventory company/location has both inventory account and COGS account configured."
            : "No accessible inventory company/location has inventory account configured.");
        return default;
    }

    private static async Task CleanupPostedInventoryArtifactsAsync(
        NpgsqlConnection connection,
        long companyId,
        long itemId,
        IReadOnlyCollection<long> stockTransactionIds,
        IReadOnlyCollection<long> stockOpnameIds,
        IReadOnlyCollection<string> journalReferenceNos,
        IReadOnlyCollection<long>? stockAdjustmentIds = null)
    {
        if (itemId > 0)
        {
            await using (var deleteOutboundEvents = new NpgsqlCommand(
                @"DELETE FROM inv_cost_outbound_events
WHERE company_id = @company_id
  AND item_id = @item_id;",
                connection))
            {
                deleteOutboundEvents.Parameters.AddWithValue("company_id", companyId);
                deleteOutboundEvents.Parameters.AddWithValue("item_id", itemId);
                await deleteOutboundEvents.ExecuteNonQueryAsync();
            }

            await using (var deleteLayers = new NpgsqlCommand(
                @"DELETE FROM inv_cost_layers
WHERE company_id = @company_id
  AND item_id = @item_id;",
                connection))
            {
                deleteLayers.Parameters.AddWithValue("company_id", companyId);
                deleteLayers.Parameters.AddWithValue("item_id", itemId);
                await deleteLayers.ExecuteNonQueryAsync();
            }
        }

        var normalizedReferenceNos = (journalReferenceNos ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedReferenceNos.Length > 0)
        {
            var journalIds = new List<long>();
            await using (var selectJournalIds = new NpgsqlCommand(
                @"SELECT id
FROM gl_journal_headers
WHERE company_id = @company_id
  AND upper(reference_no) = ANY(@reference_nos);",
                connection))
            {
                selectJournalIds.Parameters.AddWithValue("company_id", companyId);
                selectJournalIds.Parameters.Add(new NpgsqlParameter("reference_nos", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = normalizedReferenceNos
                });

                await using var reader = await selectJournalIds.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    journalIds.Add(reader.GetInt64(0));
                }
            }

            if (journalIds.Count > 0)
            {
                var journalIdArray = journalIds.ToArray();

                await using (var deleteLedger = new NpgsqlCommand(
                    @"DELETE FROM gl_ledger_entries
WHERE journal_id = ANY(@journal_ids);",
                    connection))
                {
                    deleteLedger.Parameters.Add(new NpgsqlParameter("journal_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                    {
                        Value = journalIdArray
                    });
                    await deleteLedger.ExecuteNonQueryAsync();
                }

                await using (var deleteJournalDetails = new NpgsqlCommand(
                    @"DELETE FROM gl_journal_details
WHERE header_id = ANY(@journal_ids);",
                    connection))
                {
                    deleteJournalDetails.Parameters.Add(new NpgsqlParameter("journal_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                    {
                        Value = journalIdArray
                    });
                    await deleteJournalDetails.ExecuteNonQueryAsync();
                }

                await using (var deleteJournalHeaders = new NpgsqlCommand(
                    @"DELETE FROM gl_journal_headers
WHERE id = ANY(@journal_ids);",
                    connection))
                {
                    deleteJournalHeaders.Parameters.Add(new NpgsqlParameter("journal_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                    {
                        Value = journalIdArray
                    });
                    await deleteJournalHeaders.ExecuteNonQueryAsync();
                }
            }
        }

        foreach (var stockTransactionId in (stockTransactionIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct())
        {
            await using (var deleteTxLines = new NpgsqlCommand(
                "DELETE FROM inv_stock_transaction_lines WHERE transaction_id = @transaction_id;",
                connection))
            {
                deleteTxLines.Parameters.AddWithValue("transaction_id", stockTransactionId);
                await deleteTxLines.ExecuteNonQueryAsync();
            }

            await using (var deleteTxAudit = new NpgsqlCommand(
                "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_STOCK_TX' AND entity_id = @entity_id;",
                connection))
            {
                deleteTxAudit.Parameters.AddWithValue("entity_id", stockTransactionId);
                await deleteTxAudit.ExecuteNonQueryAsync();
            }

            await using (var deleteTx = new NpgsqlCommand(
                "DELETE FROM inv_stock_transactions WHERE id = @id;",
                connection))
            {
                deleteTx.Parameters.AddWithValue("id", stockTransactionId);
                await deleteTx.ExecuteNonQueryAsync();
            }
        }

        foreach (var stockOpnameId in (stockOpnameIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct())
        {
            await using (var deleteOpnameLines = new NpgsqlCommand(
                "DELETE FROM inv_stock_opname_lines WHERE opname_id = @opname_id;",
                connection))
            {
                deleteOpnameLines.Parameters.AddWithValue("opname_id", stockOpnameId);
                await deleteOpnameLines.ExecuteNonQueryAsync();
            }

            await using (var deleteOpnameAudit = new NpgsqlCommand(
                "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_OPNAME' AND entity_id = @entity_id;",
                connection))
            {
                deleteOpnameAudit.Parameters.AddWithValue("entity_id", stockOpnameId);
                await deleteOpnameAudit.ExecuteNonQueryAsync();
            }

            await using (var deleteOpname = new NpgsqlCommand(
                "DELETE FROM inv_stock_opname WHERE id = @id;",
                connection))
            {
                deleteOpname.Parameters.AddWithValue("id", stockOpnameId);
                await deleteOpname.ExecuteNonQueryAsync();
            }
        }

        foreach (var stockAdjustmentId in (stockAdjustmentIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct())
        {
            await using (var deleteAdjustmentLines = new NpgsqlCommand(
                "DELETE FROM inv_stock_adjustment_lines WHERE adjustment_id = @adjustment_id;",
                connection))
            {
                deleteAdjustmentLines.Parameters.AddWithValue("adjustment_id", stockAdjustmentId);
                await deleteAdjustmentLines.ExecuteNonQueryAsync();
            }

            await using (var deleteAdjustmentAudit = new NpgsqlCommand(
                "DELETE FROM sec_audit_logs WHERE entity_type = 'INV_STOCK_ADJ' AND entity_id = @entity_id;",
                connection))
            {
                deleteAdjustmentAudit.Parameters.AddWithValue("entity_id", stockAdjustmentId);
                await deleteAdjustmentAudit.ExecuteNonQueryAsync();
            }

            await using (var deleteAdjustment = new NpgsqlCommand(
                "DELETE FROM inv_stock_adjustments WHERE id = @id;",
                connection))
            {
                deleteAdjustment.Parameters.AddWithValue("id", stockAdjustmentId);
                await deleteAdjustment.ExecuteNonQueryAsync();
            }
        }
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

    private static async Task<decimal> GetStockQtyAsync(
        long companyId,
        long locationId,
        long itemId,
        long? warehouseId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            @"SELECT COALESCE(SUM(qty), 0)
FROM inv_stock
WHERE company_id = @company_id
  AND location_id = @location_id
  AND item_id = @item_id
  AND warehouse_id IS NOT DISTINCT FROM @warehouse_id;",
            connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("location_id", locationId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.Add(new NpgsqlParameter("warehouse_id", NpgsqlDbType.Bigint)
        {
            Value = warehouseId.HasValue && warehouseId.Value > 0 ? warehouseId.Value : DBNull.Value
        });

        return Convert.ToDecimal(await command.ExecuteScalarAsync());
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

    private static async Task<long> CreateAndPostStockAdjustmentAsync(
        PostgresAccessControlService service,
        ManagedStockAdjustment header,
        IReadOnlyCollection<ManagedStockAdjustmentLine> lines,
        string actorUsername)
    {
        var saveResult = await service.SaveStockAdjustmentDraftAsync(header, lines, actorUsername);
        Assert(
            saveResult.IsSuccess && saveResult.EntityId.HasValue && saveResult.EntityId.Value > 0,
            $"Failed to save stock adjustment draft ({header.AdjustmentNo}): {saveResult.Message}");

        var adjustmentId = saveResult.EntityId!.Value;

        var submitResult = await service.SubmitStockAdjustmentAsync(adjustmentId, actorUsername);
        Assert(submitResult.IsSuccess, $"Failed to submit stock adjustment {adjustmentId}: {submitResult.Message}");

        var approveResult = await service.ApproveStockAdjustmentAsync(adjustmentId, actorUsername);
        Assert(approveResult.IsSuccess, $"Failed to approve stock adjustment {adjustmentId}: {approveResult.Message}");

        var postResult = await service.PostStockAdjustmentAsync(adjustmentId, actorUsername);
        Assert(postResult.IsSuccess, $"Failed to post stock adjustment {adjustmentId}: {postResult.Message}");

        return adjustmentId;
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

        await using (var deleteWarehouses = new NpgsqlCommand(
            "DELETE FROM inv_warehouses WHERE company_id = @company_id;",
            connection))
        {
            deleteWarehouses.Parameters.AddWithValue("company_id", companyId);
            await deleteWarehouses.ExecuteNonQueryAsync();
        }

        await using (var deleteUnits = new NpgsqlCommand(
            "DELETE FROM inv_units WHERE company_id = @company_id;",
            connection))
        {
            deleteUnits.Parameters.AddWithValue("company_id", companyId);
            await deleteUnits.ExecuteNonQueryAsync();
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
            await using (var deleteOutboundEvents = new NpgsqlCommand(
                @"DELETE FROM inv_cost_outbound_events e
USING inv_items i
WHERE e.item_id = i.id
  AND e.company_id = @company_id
  AND upper(i.item_code) = ANY(@item_codes);",
                connection))
            {
                deleteOutboundEvents.Parameters.AddWithValue("company_id", companyId);
                deleteOutboundEvents.Parameters.Add(new NpgsqlParameter("item_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = normalizedItemCodes
                });
                await deleteOutboundEvents.ExecuteNonQueryAsync();
            }

            await using (var deleteCostLayers = new NpgsqlCommand(
                @"DELETE FROM inv_cost_layers l
USING inv_items i
WHERE l.item_id = i.id
  AND l.company_id = @company_id
  AND upper(i.item_code) = ANY(@item_codes);",
                connection))
            {
                deleteCostLayers.Parameters.AddWithValue("company_id", companyId);
                deleteCostLayers.Parameters.Add(new NpgsqlParameter("item_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = normalizedItemCodes
                });
                await deleteCostLayers.ExecuteNonQueryAsync();
            }

            await using (var deleteStockOpnameLines = new NpgsqlCommand(
                @"DELETE FROM inv_stock_opname_lines l
USING inv_items i
WHERE l.item_id = i.id
  AND upper(i.item_code) = ANY(@item_codes);",
                connection))
            {
                deleteStockOpnameLines.Parameters.Add(new NpgsqlParameter("item_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = normalizedItemCodes
                });
                await deleteStockOpnameLines.ExecuteNonQueryAsync();
            }

            await using (var deleteStockTransactionLines = new NpgsqlCommand(
                @"DELETE FROM inv_stock_transaction_lines l
USING inv_items i
WHERE l.item_id = i.id
  AND upper(i.item_code) = ANY(@item_codes);",
                connection))
            {
                deleteStockTransactionLines.Parameters.Add(new NpgsqlParameter("item_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = normalizedItemCodes
                });
                await deleteStockTransactionLines.ExecuteNonQueryAsync();
            }

            await using (var deleteStock = new NpgsqlCommand(
                @"DELETE FROM inv_stock s
USING inv_items i
WHERE s.item_id = i.id
  AND s.company_id = @company_id
  AND upper(i.item_code) = ANY(@item_codes);",
                connection))
            {
                deleteStock.Parameters.AddWithValue("company_id", companyId);
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

    private static async Task CleanupWarehousesByCodesAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> warehouseCodes)
    {
        var normalizedWarehouseCodes = (warehouseCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedWarehouseCodes.Length == 0)
        {
            return;
        }

        await using (var deleteStorageLocations = new NpgsqlCommand(
            @"DELETE FROM inv_storage_locations sl
USING inv_warehouses w
WHERE sl.warehouse_id = w.id
  AND upper(w.warehouse_code) = ANY(@warehouse_codes);",
            connection))
        {
            deleteStorageLocations.Parameters.Add(new NpgsqlParameter("warehouse_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = normalizedWarehouseCodes
            });
            await deleteStorageLocations.ExecuteNonQueryAsync();
        }

        await using (var deleteStock = new NpgsqlCommand(
            @"DELETE FROM inv_stock s
USING inv_warehouses w
WHERE s.warehouse_id = w.id
  AND upper(w.warehouse_code) = ANY(@warehouse_codes);",
            connection))
        {
            deleteStock.Parameters.Add(new NpgsqlParameter("warehouse_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = normalizedWarehouseCodes
            });
            await deleteStock.ExecuteNonQueryAsync();
        }

        await using (var deleteStockOpnameLines = new NpgsqlCommand(
            @"DELETE FROM inv_stock_opname_lines l
USING inv_stock_opname o, inv_warehouses w
WHERE l.opname_id = o.id
  AND o.warehouse_id = w.id
  AND upper(w.warehouse_code) = ANY(@warehouse_codes);",
            connection))
        {
            deleteStockOpnameLines.Parameters.Add(new NpgsqlParameter("warehouse_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = normalizedWarehouseCodes
            });
            await deleteStockOpnameLines.ExecuteNonQueryAsync();
        }

        await using (var deleteStockOpnameHeaders = new NpgsqlCommand(
            @"DELETE FROM inv_stock_opname o
USING inv_warehouses w
WHERE o.warehouse_id = w.id
  AND upper(w.warehouse_code) = ANY(@warehouse_codes);",
            connection))
        {
            deleteStockOpnameHeaders.Parameters.Add(new NpgsqlParameter("warehouse_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = normalizedWarehouseCodes
            });
            await deleteStockOpnameHeaders.ExecuteNonQueryAsync();
        }

        await using (var deleteStockTransactionLines = new NpgsqlCommand(
            @"DELETE FROM inv_stock_transaction_lines l
USING inv_stock_transactions h, inv_warehouses w
WHERE l.transaction_id = h.id
  AND (
        h.warehouse_id = w.id
        OR h.destination_warehouse_id = w.id
        OR l.warehouse_id = w.id
        OR l.destination_warehouse_id = w.id
      )
  AND upper(w.warehouse_code) = ANY(@warehouse_codes);",
            connection))
        {
            deleteStockTransactionLines.Parameters.Add(new NpgsqlParameter("warehouse_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = normalizedWarehouseCodes
            });
            await deleteStockTransactionLines.ExecuteNonQueryAsync();
        }

        await using (var deleteStockTransactionHeaders = new NpgsqlCommand(
            @"DELETE FROM inv_stock_transactions h
USING inv_warehouses w
WHERE (
        h.warehouse_id = w.id
        OR h.destination_warehouse_id = w.id
      )
  AND upper(w.warehouse_code) = ANY(@warehouse_codes);",
            connection))
        {
            deleteStockTransactionHeaders.Parameters.Add(new NpgsqlParameter("warehouse_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = normalizedWarehouseCodes
            });
            await deleteStockTransactionHeaders.ExecuteNonQueryAsync();
        }

        await using var deleteWarehouses = new NpgsqlCommand(
            @"DELETE FROM inv_warehouses
WHERE upper(warehouse_code) = ANY(@warehouse_codes);",
            connection);
        deleteWarehouses.Parameters.Add(new NpgsqlParameter("warehouse_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = normalizedWarehouseCodes
        });
        await deleteWarehouses.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAccountsByCodesAsync(
        NpgsqlConnection connection,
        long companyId,
        IReadOnlyCollection<string> accountCodes)
    {
        var normalizedAccountCodes = (accountCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedAccountCodes.Length == 0)
        {
            return;
        }

        var accountIds = new List<long>();
        await using (var selectAccounts = new NpgsqlCommand(
            @"SELECT id
FROM gl_accounts
WHERE company_id = @company_id
  AND upper(account_code) = ANY(@account_codes)
ORDER BY hierarchy_level DESC, id DESC;",
            connection))
        {
            selectAccounts.Parameters.AddWithValue("company_id", companyId);
            selectAccounts.Parameters.Add(new NpgsqlParameter("account_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = normalizedAccountCodes
            });

            await using var reader = await selectAccounts.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                accountIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var accountId in accountIds)
        {
            await using (var deleteAudit = new NpgsqlCommand(
                @"DELETE FROM sec_audit_logs
WHERE entity_type = 'ACCOUNT'
  AND entity_id = @entity_id;",
                connection))
            {
                deleteAudit.Parameters.AddWithValue("entity_id", accountId);
                await deleteAudit.ExecuteNonQueryAsync();
            }

            await using (var deleteAccount = new NpgsqlCommand(
                @"DELETE FROM gl_accounts
WHERE id = @id;",
                connection))
            {
                deleteAccount.Parameters.AddWithValue("id", accountId);
                await deleteAccount.ExecuteNonQueryAsync();
            }
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
