namespace Accounting.Services;

public interface IAccessControlService
{
    Task<UserAccessContext?> GetUserAccessContextAsync(string username, CancellationToken cancellationToken = default);

    Task<LoginAccessOptions?> GetLoginAccessOptionsAsync(string username, CancellationToken cancellationToken = default);

    Task<UserManagementData> GetUserManagementDataAsync(CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveUserAsync(
        ManagedUser user,
        string? plainPassword,
        IReadOnlyCollection<long> roleIds,
        IReadOnlyCollection<long> companyIds,
        IReadOnlyCollection<long> locationIds,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveRoleAsync(
        ManagedRole role,
        IReadOnlyCollection<long> scopeIds,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> CloneRoleAsync(
        long sourceRoleId,
        string newCode,
        string newName,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> DeleteRoleAsync(
        long roleId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveCompanyAsync(
        ManagedCompany company,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SoftDeleteCompanyAsync(
        long companyId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveLocationAsync(
        ManagedLocation location,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SoftDeleteLocationAsync(
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<List<ManagedAccount>> GetAccountsAsync(
        long companyId,
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    Task<AccountSearchResult> SearchAccountsAsync(
        long companyId,
        AccountSearchFilter filter,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveAccountAsync(
        long companyId,
        ManagedAccount account,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SoftDeleteAccountAsync(
        long companyId,
        long accountId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> RebuildAccountHierarchyAsync(
        long companyId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<List<ManagedAccountingPeriod>> GetAccountingPeriodsAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SetAccountingPeriodOpenStateAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        bool isOpen,
        string actorUsername,
        string note = "",
        CancellationToken cancellationToken = default);

    Task<List<ManagedAuditLog>> GetAuditLogsAsync(
        string entityType,
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task<JournalWorkspaceData> GetJournalWorkspaceDataAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default);

    Task<ManagedJournalBundle?> GetJournalBundleAsync(
        long journalId,
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveJournalDraftAsync(
        ManagedJournalHeader header,
        IReadOnlyCollection<ManagedJournalLine> lines,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SubmitJournalAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> ApproveJournalAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> PostJournalAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<List<ManagedJournalSummary>> SearchJournalsAsync(
        long companyId,
        long locationId,
        JournalSearchFilter filter,
        CancellationToken cancellationToken = default);

    Task<InventoryWorkspaceData> GetInventoryWorkspaceDataAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default);

    Task<long?> GetInventoryMasterCompanyIdAsync(
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SetInventoryMasterCompanyIdAsync(
        long masterCompanyId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SyncInventoryMasterDataAsync(
        long targetCompanyId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<InventoryCentralSyncSettings> GetInventoryCentralSyncSettingsAsync(
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveInventoryCentralSyncSettingsAsync(
        InventoryCentralSyncSettings settings,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<InventoryCostingSettings> GetInventoryCostingSettingsAsync(
        long companyId,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveInventoryCostingSettingsAsync(
        long companyId,
        InventoryCostingSettings settings,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<InventoryLocationCostingSettings> GetInventoryLocationCostingSettingsAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveInventoryLocationCostingSettingsAsync(
        long companyId,
        InventoryLocationCostingSettings settings,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> RecalculateInventoryCostingAsync(
        long companyId,
        long? locationId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> PullInventoryJournalsForPeriodAsync(
        long companyId,
        long? locationId,
        DateTime periodMonth,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> UploadInventoryToCentralAsync(
        long companyId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> DownloadInventoryFromCentralAsync(
        long companyId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<List<ManagedInventorySyncRun>> GetInventorySyncRunHistoryAsync(
        long companyId,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<List<ManagedInventorySyncItemLog>> GetInventorySyncItemLogHistoryAsync(
        long companyId,
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveInventoryCategoryAsync(
        long companyId,
        ManagedInventoryCategory category,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SoftDeleteInventoryCategoryAsync(
        long companyId,
        long categoryId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveInventoryItemAsync(
        long companyId,
        ManagedInventoryItem item,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SoftDeleteInventoryItemAsync(
        long companyId,
        long itemId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<InventoryImportExecutionResult> ImportInventoryMasterDataAsync(
        long companyId,
        InventoryImportBundle bundle,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<InventoryOpeningBalanceExecutionResult> ImportInventoryOpeningBalanceAsync(
        long companyId,
        InventoryOpeningBalanceBundle bundle,
        string actorUsername,
        bool validateOnly = false,
        bool replaceExistingBatch = false,
        CancellationToken cancellationToken = default);

    Task<InventoryItemSearchResult> SearchInventoryItemsAsync(
        long companyId,
        InventoryItemSearchFilter filter,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveInventoryUnitAsync(
        long companyId,
        ManagedInventoryUnit unit,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SoftDeleteInventoryUnitAsync(
        long companyId,
        long unitId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveWarehouseAsync(
        long companyId,
        ManagedWarehouse warehouse,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SoftDeleteWarehouseAsync(
        long companyId,
        long warehouseId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveStockTransactionDraftAsync(
        ManagedStockTransaction header,
        IReadOnlyCollection<ManagedStockTransactionLine> lines,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<List<ManagedStockTransactionSummary>> SearchStockTransactionsAsync(
        long companyId,
        long locationId,
        InventoryTransactionSearchFilter filter,
        CancellationToken cancellationToken = default);

    Task<StockTransactionBundle?> GetStockTransactionBundleAsync(
        long transactionId,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SubmitStockTransactionAsync(
        long transactionId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> ApproveStockTransactionAsync(
        long transactionId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> PostStockTransactionAsync(
        long transactionId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<List<ManagedStockOpnameLine>> GenerateOpnameLinesFromStockAsync(
        long companyId,
        long locationId,
        long warehouseId,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SaveStockOpnameDraftAsync(
        ManagedStockOpname header,
        IReadOnlyCollection<ManagedStockOpnameLine> lines,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<List<ManagedStockOpname>> SearchStockOpnameAsync(
        long companyId,
        long locationId,
        string keyword,
        CancellationToken cancellationToken = default);

    Task<StockOpnameBundle?> GetStockOpnameBundleAsync(
        long opnameId,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> SubmitStockOpnameAsync(
        long opnameId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> ApproveStockOpnameAsync(
        long opnameId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<AccessOperationResult> PostStockOpnameAsync(
        long opnameId,
        string actorUsername,
        CancellationToken cancellationToken = default);

    Task<InventoryDashboardData> GetInventoryDashboardDataAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default);

    Task<List<StockMovementReportRow>> GetStockMovementReportAsync(
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken = default);

    Task<List<StockValuationRow>> GetStockValuationReportAsync(
        long companyId,
        long locationId,
        CancellationToken cancellationToken = default);

    Task<Dictionary<long, decimal>> GetOutboundAutoUnitCostLookupAsync(
        long companyId,
        long locationId,
        IReadOnlyCollection<long> itemIds,
        CancellationToken cancellationToken = default);

    Task<List<ManagedStockEntry>> GetLowStockAlertAsync(
        long companyId,
        long locationId,
        decimal threshold,
        CancellationToken cancellationToken = default);

    Task<List<InventoryOutboundCompareRow>> GetInventoryOutboundCompareReportAsync(
        long companyId,
        long locationId,
        DateTime dateFrom,
        DateTime dateTo,
        CancellationToken cancellationToken = default);

    Task<List<ManagedTrialBalanceRow>> GetTrialBalanceAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken = default);

    Task<List<ManagedProfitLossRow>> GetProfitLossAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken = default);

    Task<List<ManagedBalanceSheetRow>> GetBalanceSheetAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken = default);

    Task<List<ManagedGeneralLedgerRow>> GetGeneralLedgerAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        string accountCode = "",
        string keyword = "",
        CancellationToken cancellationToken = default);

    Task<List<ManagedSubLedgerRow>> GetSubLedgerAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        string accountCode = "",
        string keyword = "",
        CancellationToken cancellationToken = default);

    Task<List<ManagedCashFlowRow>> GetCashFlowAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        CancellationToken cancellationToken = default);

    Task<List<ManagedAccountMutationRow>> GetAccountMutationAsync(
        long companyId,
        long locationId,
        DateTime periodMonth,
        string accountCode = "",
        string keyword = "",
        CancellationToken cancellationToken = default);
}
