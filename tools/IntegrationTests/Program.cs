using Accounting.Services;

internal static partial class Program
{
    private const string InventoryMasterCompanySettingKey = "inventory_master_company_id";
    private const string CentralSyncBaseUrlSettingKey = "inventory_central_base_url";
    private const string CentralSyncApiKeySettingKey = "inventory_central_api_key";
    private const string CentralSyncUploadPathSettingKey = "inventory_central_upload_path";
    private const string CentralSyncDownloadPathSettingKey = "inventory_central_download_path";
    private const string CentralSyncTimeoutSettingKey = "inventory_central_timeout_seconds";

    private static async Task<int> Main()
    {
        var testCases = new (string Name, Func<Task> Test)[]
        {
            ("GetLoginAccessOptions_ReturnsConsistentUserScopedData", TestGetLoginAccessOptionsAsync),
            ("InventoryApiInv_RuntimeMigrationVerifier", TestInventoryApiInvRuntimeMigrationVerifierAsync),
            ("RBACDb_ViewAndFunctionSmoke", TestRbacDbViewAndFunctionSmokeAsync),
            ("RBACDb_SeedCoverage", TestRbacPermissionSeedCoverageAsync),
            ("SaveUser_RejectsMultiRoleAndPersistsSingleRole", TestSaveUserSingleRoleRuleAsync),
            ("UserManagementViewModel_RoleEditorDirtyDiscardFlow", TestUserManagementRoleEditorDirtyDiscardFlowAsync),
            ("UserManagementViewModel_RolePermissionFilterAndSelectedOnly", TestUserManagementRolePermissionFilterAndSelectedOnlyAsync),
            ("UserManagementViewModel_CloneRoleCommandCreatesRoleCopy", TestUserManagementCloneRoleCommandCreatesRoleCopyAsync),
            ("UserManagementViewModel_TryLeaveRoleEditorReturnsTrueWhenClean", TestUserManagementTryLeaveRoleEditorReturnsTrueWhenCleanAsync),
            ("UserManagementViewModel_RolePermissionTreeIncludesInventoryImportActions", TestUserManagementRolePermissionTreeIncludesInventoryImportActionsAsync),
            ("UserManagementViewModel_LocationOptionsFollowSelectedCompanies", TestUserManagementLocationOptionsFollowSelectedCompaniesAsync),
            ("InventoryViewModel_MasterDataActionsReflectExactPermissions", TestInventoryViewModel_MasterDataActionsReflectExactPermissionsAsync),
            ("JournalManagementViewModel_ActionTooltipsReflectPermissions", TestJournalManagementViewModel_ActionTooltipsReflectPermissionsAsync),
            ("JournalManagementViewModel_BrowseRowsMirrorSearchResults", TestJournalManagementViewModel_BrowseRowsMirrorSearchResultsAsync),
            ("JournalManagementViewModel_BrowseDetailLoadsOnceAndCaches", TestJournalManagementViewModel_BrowseDetailLoadsOnceAndCachesAsync),
            ("MainShellViewModel_GeneralLedgerNavigationRequiresViewActions", TestMainShellViewModel_GeneralLedgerNavigationRequiresViewActionsAsync),
            ("JournalManagementViewModel_ImportExportRequireDedicatedActions", TestJournalManagementViewModel_ImportExportRequireDedicatedActionsAsync),
            ("JournalManagementViewModel_SelectedBrowseRowsDriveExportResolution", TestJournalManagementViewModel_SelectedBrowseRowsDriveExportResolutionAsync),
            ("ReportsViewModel_ExportRequiresDedicatedPermission", TestReportsViewModel_ExportRequiresDedicatedPermissionAsync),
            ("MasterDataViewModel_AccountImportExportCommandsReflectBusyState", TestMasterDataViewModel_AccountImportExportCommandsReflectBusyStateAsync),
            ("MasterDataViewModel_AccountImportValidationFailureUpdatesStatusAndPanel", TestMasterDataViewModel_AccountImportValidationFailureUpdatesStatusAndPanelAsync),
            ("MasterDataViewModel_AccountImportExecutionResultUpdatesStatusAndErrors", TestMasterDataViewModel_AccountImportExecutionResultUpdatesStatusAndErrorsAsync),
            ("MasterDataViewModel_EstateHierarchyCommandsReflectSelectionAndBusyState", TestMasterDataViewModel_EstateHierarchyCommandsReflectSelectionAndBusyStateAsync),
            ("MasterDataViewModel_EstateHierarchyImportFailureUpdatesStatusAndPanel", TestMasterDataViewModel_EstateHierarchyImportFailureUpdatesStatusAndPanelAsync),
            ("InventoryViewModel_TransactionWorkflowStateReflectsDocumentStatus", TestInventoryViewModel_TransactionWorkflowStateReflectsDocumentStatusAsync),
            ("InventoryViewModel_TransactionActionsReflectClosedPeriod", TestInventoryViewModel_TransactionActionsReflectClosedPeriodAsync),
            ("Journal_ReadApis_RespectScopeAccess", TestJournalReadApisRespectScopeAccessAsync),
            ("Journal_SaveDraftPostAndLockBehavior", TestJournalDraftPostFlowAsync),
            ("Journal_AllowsSameNumberAcrossDifferentPeriods", TestJournalAllowsSameNumberAcrossDifferentPeriodsAsync),
            ("Journal_RejectsApprovePostWithoutApproveScope", TestJournalRejectApprovePostWithoutApproveScopeAsync),
            ("Journal_RejectsWhenPeriodClosed", TestJournalRejectWhenPeriodClosedAsync),
            ("Journal_SubmitApproveRejectWhenPeriodClosed", TestJournalSubmitApproveRejectWhenPeriodClosedAsync),
            ("AccountingPeriod_OpenCloseApi", TestAccountingPeriodOpenCloseApiAsync),
            ("AccountingPeriod_CloseCreatesClosingEntry", TestAccountingPeriodCloseCreatesClosingEntryAsync),
            ("AccountingPeriod_CloseAllowsEquationBalancedDespiteBalanceSheetDiff", TestAccountingPeriodCloseAllowsEquationBalancedDespiteBalanceSheetDiffAsync),
            ("AccountingPeriod_RejectsUnauthorizedActor", TestAccountingPeriodRejectsUnauthorizedActorAsync),
            ("AccountingPeriod_RejectsOutOfScopeFinanceAdmin", TestAccountingPeriodRejectsOutOfScopeFinanceAdminAsync),
            ("AccountingPeriod_CloseRejectsPendingStockOpname", TestAccountingPeriodCloseRejectsPendingStockOpnameAsync),
            ("Account_RejectsUnauthorizedActor", TestAccountRejectsUnauthorizedActorAsync),
            ("AccountImport_CreatesAndRoundTripsXlsx", TestAccountImportCreatesAndRoundTripsXlsxAsync),
            ("AccountImport_UpdatesExistingHierarchy", TestAccountImportUpdatesExistingAccountsWithoutBreakingHierarchyAsync),
            ("AccountImport_RejectsInvalidParentRows", TestAccountImportRejectsInvalidParentRowsAsync),
            ("AccountImport_RejectsUnauthorizedActor", TestAccountImportRejectsUnauthorizedActorAsync),
            ("Journal_RequiresActiveBlock", TestJournalRequiresActiveBlockAsync),
            ("EstateHierarchy_ImportRoundTripsAndFeedsJournalWorkspace", TestEstateHierarchyImportRoundTripsAndFeedsJournalWorkspaceAsync),
            ("Account_SaveAndSoftDelete", TestAccountSaveAndSoftDeleteAsync),
            ("Reports_CashFlowHonorsCashMetadata", TestReportsCashFlowHonorsCashMetadataAsync),
            ("InventoryCategory_CrudReactivationAndValidation", TestInventoryCategoryCrudReactivationAndValidationAsync),
            ("InventoryCategory_RejectsUnauthorizedActor", TestInventoryCategoryRejectsUnauthorizedActorAsync),
            ("InventoryImport_AllowsDedicatedApiPermission", TestInventoryImportAllowsDedicatedApiPermissionAsync),
            ("InventoryImport_WritesAggregateAuditLog", TestInventoryImportWritesAggregateAuditLogAsync),
            ("Inventory_MasterCompanyPolicy_SyncAndWriteGuard", TestInventoryMasterCompanyPolicySyncAndWriteGuardAsync),
            ("Inventory_CentralSync_MockUploadDownloadAndLogs", TestInventoryCentralSyncMockUploadDownloadAndLogsAsync),
            ("Inventory_Costing_RecalcCompanyAndLocation", TestInventoryCostingRecalcCompanyAndLocationAsync),
            ("Inventory_DraftAutoNumbering", TestInventoryDraftAutoNumberingAsync),
            ("Inventory_StockTransaction_RejectsWarehouseLocationMismatch", TestInventoryStockTransactionRejectsWarehouseLocationMismatchAsync),
            ("Inventory_StockTransaction_AllowsGlobalWarehouse", TestInventoryStockTransactionAllowsGlobalWarehouseAsync),
            ("Inventory_Transfer_MovesWarehouseBuckets", TestInventoryTransferMovesWarehouseBucketsAsync)
        };

        var failed = 0;
        foreach (var testCase in testCases)
        {
            try
            {
                await testCase.Test();
                Console.WriteLine($"[PASS] {testCase.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[FAIL] {testCase.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Integration tests finished. Passed={testCases.Length - failed}, Failed={failed}");
        return failed == 0 ? 0 : 1;
    }

}
