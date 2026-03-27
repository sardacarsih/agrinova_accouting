using System.Collections.ObjectModel;
using System.Reflection;
using Accounting.Services;
using Accounting.ViewModels;

internal static partial class Program
{
    private static async Task TestInventoryViewModel_MasterDataActionsReflectExactPermissionsAsync()
    {
        var service = CreateService();
        var accessContext = await BuildTestAccessContextAsync(
            "inventory.kategori.create",
            "inventory.item.update",
            "inventory.gudang.delete");

        var viewModel = new InventoryViewModel(service, accessContext);
        SetInventoryMasterPolicy(viewModel, canMaintainMasterData: true, masterCompanyLabel: "ITEST-HQ");

        Assert(viewModel.CanCreateCategory, "Category create should be enabled when create permission exists.");
        Assert(
            string.Equals(viewModel.NewCategoryTooltip, "Buat kategori baru.", StringComparison.Ordinal),
            $"Unexpected category new tooltip: {viewModel.NewCategoryTooltip}");

        viewModel.NewCategoryCommand.Execute(null);
        Assert(viewModel.SelectedCategory is not null, "New category command should initialize a category editor.");
        Assert(viewModel.CanSaveCategory, "Category save should be enabled for a new category with create permission.");

        viewModel.SelectedCategory = new ManagedInventoryCategory
        {
            Id = 99,
            CompanyId = accessContext.SelectedCompanyId,
            Code = "CAT-ITEST",
            Name = "Kategori ITest",
            AccountCode = "1100",
            IsActive = true
        };

        Assert(!viewModel.CanSaveCategory, "Category save should be disabled when update permission is missing.");
        Assert(
            viewModel.SaveCategoryTooltip.Contains("izin memperbarui kategori", StringComparison.OrdinalIgnoreCase),
            $"Unexpected category save tooltip: {viewModel.SaveCategoryTooltip}");
        Assert(!viewModel.CanDeactivateCategory, "Category deactivate should be disabled when delete permission is missing.");
        Assert(
            viewModel.DeactivateCategoryTooltip.Contains("izin menonaktifkan kategori", StringComparison.OrdinalIgnoreCase),
            $"Unexpected category deactivate tooltip: {viewModel.DeactivateCategoryTooltip}");

        Assert(!viewModel.CanCreateItem, "Item create should be disabled without create permission.");
        Assert(
            viewModel.NewItemTooltip.Contains("izin membuat item", StringComparison.OrdinalIgnoreCase),
            $"Unexpected item new tooltip: {viewModel.NewItemTooltip}");
        Assert(!viewModel.CanImportInventoryMasterData, "Inventory import should be disabled without dedicated import permission.");
        Assert(
            viewModel.ImportInventoryMasterDataTooltip.Contains("izin import master data inventory", StringComparison.OrdinalIgnoreCase),
            $"Unexpected inventory import tooltip: {viewModel.ImportInventoryMasterDataTooltip}");
        Assert(!viewModel.CanDownloadInventoryImportTemplate, "Inventory template download should follow import permission gate.");

        viewModel.SelectedItem = new ManagedInventoryItem
        {
            Id = 41,
            CompanyId = accessContext.SelectedCompanyId,
            Code = "ITEM-ITEST",
            Name = "Item ITest",
            Uom = "PCS",
            Category = "Kategori ITest",
            CategoryId = 99,
            IsActive = true
        };

        Assert(viewModel.CanSaveItem, "Item save should be enabled when update permission exists for an existing item.");
        Assert(
            string.Equals(viewModel.SaveItemTooltip, "Simpan perubahan item.", StringComparison.Ordinal),
            $"Unexpected item save tooltip: {viewModel.SaveItemTooltip}");
        Assert(!viewModel.CanDeactivateItem, "Item deactivate should be disabled when delete permission is missing.");

        viewModel.SelectedUnit = new ManagedInventoryUnit
        {
            Id = 5,
            CompanyId = accessContext.SelectedCompanyId,
            Code = "PCS",
            Name = "Pieces",
            IsActive = true
        };

        Assert(!viewModel.CanCreateUnit, "Unit create should be disabled without create permission.");
        Assert(!viewModel.CanSaveUnit, "Unit save should be disabled without update permission.");
        Assert(
            viewModel.SaveUnitTooltip.Contains("izin memperbarui satuan", StringComparison.OrdinalIgnoreCase),
            $"Unexpected unit save tooltip: {viewModel.SaveUnitTooltip}");

        viewModel.SelectedWarehouse = new ManagedWarehouse
        {
            Id = 7,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            Code = "WH-ITEST",
            Name = "Warehouse ITest",
            LocationName = "ITEST Location",
            IsActive = true
        };

        Assert(!viewModel.CanCreateWarehouse, "Warehouse create should be disabled without create permission.");
        Assert(!viewModel.CanSaveWarehouse, "Warehouse save should be disabled without update permission.");
        Assert(viewModel.CanDeactivateWarehouse, "Warehouse deactivate should be enabled when delete permission exists.");
        Assert(
            string.Equals(viewModel.DeactivateWarehouseTooltip, "Nonaktifkan gudang yang dipilih.", StringComparison.Ordinal),
            $"Unexpected warehouse deactivate tooltip: {viewModel.DeactivateWarehouseTooltip}");

        var importAccessContext = await BuildTestAccessContextAsync(
            "inventory.api_inv.import_master_data",
            "inventory.api_inv.download_import_template");
        var importViewModel = new InventoryViewModel(service, importAccessContext);
        SetInventoryMasterPolicy(importViewModel, canMaintainMasterData: true, masterCompanyLabel: "ITEST-HQ");

        Assert(importViewModel.CanImportInventoryMasterData, "Inventory import should be enabled when dedicated import permission exists.");
        Assert(importViewModel.CanDownloadInventoryImportTemplate, "Inventory template download should be enabled when import permission is satisfied.");
    }

    private static async Task TestJournalManagementViewModel_ActionTooltipsReflectPermissionsAsync()
    {
        var service = CreateService();
        var accessContext = await BuildTestAccessContextAsync();
        var viewModel = new JournalManagementViewModel(service, accessContext, "ITEST Location");

        Assert(!viewModel.CanCreateNewJournal, "New journal should be disabled without create permission.");
        Assert(
            viewModel.NewJournalTooltip.Contains("izin membuat draft jurnal", StringComparison.OrdinalIgnoreCase),
            $"Unexpected journal new tooltip: {viewModel.NewJournalTooltip}");
        Assert(!viewModel.CanPullInventoryJournals, "Inventory pull should be disabled without pull_journal permission.");
        Assert(
            viewModel.InventoryPullTooltip.Contains("izin menarik jurnal inventory", StringComparison.OrdinalIgnoreCase),
            $"Unexpected inventory pull tooltip: {viewModel.InventoryPullTooltip}");
        Assert(!viewModel.CanCommitImportDrafts, "Import commit should be disabled without create permission.");
        Assert(
            viewModel.CommitImportTooltip.Contains("izin import jurnal", StringComparison.OrdinalIgnoreCase),
            $"Unexpected import commit tooltip: {viewModel.CommitImportTooltip}");
    }

    private static async Task TestMainShellViewModel_GeneralLedgerNavigationRequiresViewActionsAsync()
    {
        var service = CreateService();
        var exportOnlyContext = await BuildTestAccessContextAsync("accounting.reports.export");
        var exportOnlyNavigation = BuildMainShellNavigation(exportOnlyContext);
        Assert(
            !ContainsNavigationLabel(exportOnlyNavigation, "General Ledger"),
            "General Ledger navigation should stay hidden for reports.export without reports.view.");

        var settingsUpdateOnlyContext = await BuildTestAccessContextAsync("accounting.settings.update");
        var settingsUpdateNavigation = BuildMainShellNavigation(settingsUpdateOnlyContext);
        Assert(
            !ContainsNavigationLabel(settingsUpdateNavigation, "General Ledger"),
            "General Ledger navigation should stay hidden for settings.update without settings.view.");

        var reportsViewContext = await BuildTestAccessContextAsync("accounting.reports.view");
        var reportsViewNavigation = BuildMainShellNavigation(reportsViewContext);
        Assert(
            ContainsNavigationLabel(reportsViewNavigation, "General Ledger"),
            "General Ledger navigation should be visible when reports.view exists.");
        Assert(
            ContainsNavigationLabel(reportsViewNavigation, "Inquiry"),
            "Inquiry navigation should be visible when reports.view exists.");
        Assert(
            ContainsNavigationLabel(reportsViewNavigation, "Laporan"),
            "Report navigation should be visible when reports.view exists.");

        var dashboardOnlyContext = await BuildTestAccessContextAsync("accounting.dashboard.view");
        var dashboardOnlyNavigation = BuildMainShellNavigation(dashboardOnlyContext);
        Assert(
            ContainsNavigationLabel(dashboardOnlyNavigation, "General Ledger"),
            "General Ledger navigation should be visible when accounting.dashboard exists.");
        Assert(
            !ContainsRootNavigationLabel(dashboardOnlyNavigation, "Dashboard"),
            "Dashboard should no longer appear as a root navigation item.");

        var generalLedgerRoot = FindNavigationItemByLabel(dashboardOnlyNavigation, "General Ledger");
        Assert(generalLedgerRoot is not null, "General Ledger root item should be present.");
        Assert(generalLedgerRoot!.Children is { Count: > 0 }, "General Ledger should expose child navigation items.");
        Assert(
            string.Equals(generalLedgerRoot.Children![0].Label, "Dashboard", StringComparison.Ordinal),
            "Dashboard should be the first child under General Ledger.");

        var dashboardShell = CreateMainShellViewModel(dashboardOnlyContext, service);
        try
        {
            Assert(
                string.Equals(dashboardShell.SelectedNavigationItem?.Label, "Dashboard", StringComparison.Ordinal),
                "Dashboard should be the default selected leaf for accounting dashboard access.");
            Assert(dashboardShell.IsDashboardSelected, "Dashboard leaf should activate dashboard workspace.");
            Assert(!dashboardShell.IsScopePlaceholderSelected, "Dashboard leaf should not activate placeholder workspace.");
        }
        finally
        {
            dashboardShell.Dispose();
        }

        var reportsOnlyShell = CreateMainShellViewModel(reportsViewContext, service);
        try
        {
            Assert(
                !string.Equals(reportsOnlyShell.SelectedNavigationItem?.Label, "Dashboard", StringComparison.Ordinal),
                "Dashboard should not be auto-selected when dashboard permission is missing.");
            Assert(!reportsOnlyShell.IsDashboardSelected, "Reports-only access should not activate dashboard workspace.");
        }
        finally
        {
            reportsOnlyShell.Dispose();
        }
    }

    private static async Task TestJournalManagementViewModel_ImportExportRequireDedicatedActionsAsync()
    {
        var service = CreateService();

        var createOnlyContext = await BuildTestAccessContextAsync("accounting.transactions.create");
        var createOnlyViewModel = new JournalManagementViewModel(service, createOnlyContext, "ITEST Location")
        {
            ImportFilePath = "D:\\temp\\journal-import.xlsx"
        };

        Assert(!createOnlyViewModel.CanBrowseImportFile, "Browse import should be disabled without transactions.import.");
        Assert(!createOnlyViewModel.CanPreviewImportFile, "Preview import should be disabled without transactions.import.");
        Assert(
            createOnlyViewModel.BrowseImportTooltip.Contains("izin import jurnal", StringComparison.OrdinalIgnoreCase),
            $"Unexpected browse import tooltip: {createOnlyViewModel.BrowseImportTooltip}");
        Assert(!createOnlyViewModel.CanExportAnyJournal, "Journal export should be disabled without transactions.export.");
        Assert(!createOnlyViewModel.CanPreviewExportPeriod, "Period export preview should be disabled without transactions.export.");

        var importOnlyContext = await BuildTestAccessContextAsync("accounting.transactions.import");
        var importOnlyViewModel = new JournalManagementViewModel(service, importOnlyContext, "ITEST Location")
        {
            ImportFilePath = "D:\\temp\\journal-import.xlsx"
        };
        SetJournalImportBundles(importOnlyViewModel, new[]
        {
            new JournalImportBundleResult
            {
                Header = new ManagedJournalHeader { JournalNo = "JR-ITEST-001", JournalDate = DateTime.Today },
                Lines = new List<ManagedJournalLine>
                {
                    new() { LineNo = 1, AccountCode = "1100", Debit = 100m, Credit = 0m }
                },
                IsValid = true
            }
        });

        Assert(importOnlyViewModel.CanBrowseImportFile, "Browse import should be enabled when transactions.import exists.");
        Assert(importOnlyViewModel.CanPreviewImportFile, "Preview import should be enabled when transactions.import exists.");
        Assert(
            !importOnlyViewModel.CanCommitImportDrafts,
            "Import commit should stay disabled without transactions.create even when transactions.import exists.");
        Assert(
            importOnlyViewModel.CommitImportTooltip.Contains("membuat draft jurnal hasil import", StringComparison.OrdinalIgnoreCase),
            $"Unexpected import commit tooltip for import-only role: {importOnlyViewModel.CommitImportTooltip}");

        var exportOnlyContext = await BuildTestAccessContextAsync("accounting.transactions.export");
        var exportOnlyViewModel = new JournalManagementViewModel(service, exportOnlyContext, "ITEST Location");
        SetJournalId(exportOnlyViewModel, 321);

        Assert(exportOnlyViewModel.CanExportAnyJournal, "Journal export should be enabled when transactions.export exists.");
        Assert(exportOnlyViewModel.CanPreviewExportPeriod, "Period export preview should be enabled when transactions.export exists.");
        Assert(exportOnlyViewModel.CanExportCurrentJournal, "Current journal export should be enabled when journal exists and transactions.export exists.");

        var viewOnlyContext = await BuildTestAccessContextAsync("accounting.transactions.view");
        var viewOnlyViewModel = new JournalManagementViewModel(service, viewOnlyContext, "ITEST Location");
        SetJournalId(viewOnlyViewModel, 654);

        Assert(!viewOnlyViewModel.CanExportAnyJournal, "Journal export should be disabled for transactions.view without transactions.export.");
        Assert(!viewOnlyViewModel.CanPreviewExportPeriod, "Period export preview should be disabled for transactions.view without transactions.export.");
        Assert(!viewOnlyViewModel.CanExportCurrentJournal, "Current journal export should be disabled for transactions.view without transactions.export.");
        Assert(
            viewOnlyViewModel.ExportCurrentTooltip.Contains("izin export jurnal", StringComparison.OrdinalIgnoreCase),
            $"Unexpected export current tooltip: {viewOnlyViewModel.ExportCurrentTooltip}");
    }

    private static async Task TestReportsViewModel_ExportRequiresDedicatedPermissionAsync()
    {
        var service = CreateService();

        var viewOnlyContext = await BuildTestAccessContextAsync("accounting.reports.view");
        var viewOnlyViewModel = new ReportsViewModel(
            service,
            viewOnlyContext,
            viewOnlyContext.SelectedCompanyId,
            viewOnlyContext.SelectedLocationId);

        Assert(!viewOnlyViewModel.CanExportReports, "Report export should be disabled for reports.view without reports.export.");
        Assert(
            !viewOnlyViewModel.ExportCommand.CanExecute(null),
            "Export command should be disabled for reports.view without reports.export.");

        var exportOnlyContext = await BuildTestAccessContextAsync("accounting.reports.export");
        var exportOnlyViewModel = new ReportsViewModel(
            service,
            exportOnlyContext,
            exportOnlyContext.SelectedCompanyId,
            exportOnlyContext.SelectedLocationId);

        Assert(exportOnlyViewModel.CanExportReports, "Report export should be enabled when reports.export exists.");
        Assert(
            exportOnlyViewModel.ExportCommand.CanExecute(null),
            "Export command should be enabled when reports.export exists.");
    }

    private static async Task TestInventoryViewModel_TransactionWorkflowStateReflectsDocumentStatusAsync()
    {
        var service = CreateService();
        var accessContext = await BuildTestAccessContextAsync(
            "inventory.stock_in.create",
            "inventory.stock_in.update",
            "inventory.stock_in.submit",
            "inventory.stock_in.approve",
            "inventory.stock_in.post",
            "inventory.stock_out.create",
            "inventory.stock_out.update",
            "inventory.stock_out.submit",
            "inventory.stock_out.approve",
            "inventory.stock_out.post",
            "inventory.transfer.create",
            "inventory.transfer.update",
            "inventory.transfer.submit",
            "inventory.transfer.approve",
            "inventory.transfer.post",
            "inventory.stock_opname.create",
            "inventory.stock_opname.update",
            "inventory.stock_opname.submit",
            "inventory.stock_opname.approve",
            "inventory.stock_opname.post");
        var viewModel = new InventoryViewModel(service, accessContext);
        viewModel.ApplyCurrentAccountingPeriodState(DateTime.Today, isOpen: true);

        viewModel.StockInHeader = new ManagedStockTransaction
        {
            Id = 0,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            TransactionType = "STOCK_IN",
            TransactionDate = DateTime.Today,
            Status = "DRAFT"
        };
        Assert(viewModel.CanCreateStockIn, "Stock-in create should be enabled with create permission.");
        Assert(viewModel.CanSaveStockInDraft, "Stock-in draft save should be enabled for a new draft.");
        Assert(!viewModel.CanSubmitStockIn, "Stock-in submit should require a persisted draft.");
        Assert(
            viewModel.SubmitStockInTooltip.Contains("Simpan transaksi stok masuk terlebih dahulu", StringComparison.OrdinalIgnoreCase),
            $"Unexpected stock-in submit tooltip: {viewModel.SubmitStockInTooltip}");

        viewModel.StockInHeader = new ManagedStockTransaction
        {
            Id = 101,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            TransactionType = "STOCK_IN",
            TransactionDate = DateTime.Today,
            Status = "SUBMITTED"
        };
        Assert(!viewModel.CanSaveStockInDraft, "Stock-in save should be disabled outside DRAFT status.");
        Assert(viewModel.CanApproveStockIn, "Stock-in approve should be enabled for SUBMITTED status.");
        Assert(!viewModel.CanPostStockIn, "Stock-in post should be disabled before APPROVED status.");

        viewModel.StockOutHeader = new ManagedStockTransaction
        {
            Id = 202,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            TransactionType = "STOCK_OUT",
            TransactionDate = DateTime.Today,
            Status = "DRAFT"
        };
        Assert(viewModel.CanSubmitStockOut, "Stock-out submit should be enabled for persisted DRAFT status.");
        Assert(!viewModel.CanApproveStockOut, "Stock-out approve should be disabled before SUBMITTED status.");
        Assert(
            viewModel.ApproveStockOutTooltip.Contains("berstatus SUBMITTED", StringComparison.OrdinalIgnoreCase),
            $"Unexpected stock-out approve tooltip: {viewModel.ApproveStockOutTooltip}");

        viewModel.TransferHeader = new ManagedStockTransaction
        {
            Id = 303,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            TransactionType = "TRANSFER",
            TransactionDate = DateTime.Today,
            Status = "APPROVED"
        };
        Assert(viewModel.CanPostTransfer, "Transfer post should be enabled for APPROVED status.");
        Assert(!viewModel.CanSubmitTransfer, "Transfer submit should be disabled once status leaves DRAFT.");
        Assert(
            viewModel.SubmitTransferTooltip.Contains("berstatus DRAFT", StringComparison.OrdinalIgnoreCase),
            $"Unexpected transfer submit tooltip: {viewModel.SubmitTransferTooltip}");

        viewModel.StockOpnameHeader = new ManagedStockOpname
        {
            Id = 404,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            WarehouseId = 55,
            WarehouseName = "Warehouse ITest",
            OpnameDate = DateTime.Today,
            Status = "DRAFT"
        };
        Assert(viewModel.CanGenerateStockOpnameLines, "Stock opname line generation should be enabled for draft with warehouse.");
        Assert(viewModel.CanSubmitStockOpname, "Stock opname submit should be enabled for persisted DRAFT status.");

        viewModel.StockOpnameHeader = new ManagedStockOpname
        {
            Id = 405,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            WarehouseId = 55,
            WarehouseName = "Warehouse ITest",
            OpnameDate = DateTime.Today,
            Status = "APPROVED"
        };
        Assert(viewModel.CanPostStockOpname, "Stock opname post should be enabled for APPROVED status.");
        Assert(!viewModel.CanGenerateStockOpnameLines, "Stock opname line generation should be disabled outside DRAFT status.");
        Assert(
            viewModel.GenerateStockOpnameLinesTooltip.Contains("berstatus DRAFT", StringComparison.OrdinalIgnoreCase),
            $"Unexpected stock opname generate tooltip: {viewModel.GenerateStockOpnameLinesTooltip}");
    }

    private static async Task TestInventoryViewModel_TransactionActionsReflectClosedPeriodAsync()
    {
        var service = CreateService();
        var accessContext = await BuildTestAccessContextAsync(
            "inventory.stock_in.create",
            "inventory.stock_in.update",
            "inventory.stock_in.submit",
            "inventory.stock_in.approve",
            "inventory.stock_in.post",
            "inventory.stock_out.create",
            "inventory.stock_out.update",
            "inventory.stock_out.submit",
            "inventory.stock_out.approve",
            "inventory.stock_out.post",
            "inventory.transfer.create",
            "inventory.transfer.update",
            "inventory.transfer.submit",
            "inventory.transfer.approve",
            "inventory.transfer.post",
            "inventory.stock_opname.create",
            "inventory.stock_opname.update",
            "inventory.stock_opname.submit",
            "inventory.stock_opname.approve",
            "inventory.stock_opname.post");
        var viewModel = new InventoryViewModel(service, accessContext);

        viewModel.StockInHeader = new ManagedStockTransaction
        {
            Id = 501,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            TransactionType = "STOCK_IN",
            TransactionDate = DateTime.Today,
            Status = "DRAFT"
        };
        viewModel.StockOutHeader = new ManagedStockTransaction
        {
            Id = 502,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            TransactionType = "STOCK_OUT",
            TransactionDate = DateTime.Today,
            Status = "SUBMITTED"
        };
        viewModel.TransferHeader = new ManagedStockTransaction
        {
            Id = 503,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            TransactionType = "TRANSFER",
            TransactionDate = DateTime.Today,
            Status = "APPROVED"
        };
        viewModel.StockOpnameHeader = new ManagedStockOpname
        {
            Id = 504,
            CompanyId = accessContext.SelectedCompanyId,
            LocationId = accessContext.SelectedLocationId,
            WarehouseId = 77,
            WarehouseName = "Warehouse Closed Period",
            OpnameDate = DateTime.Today,
            Status = "DRAFT"
        };

        viewModel.ApplyCurrentAccountingPeriodState(DateTime.Today, isOpen: false);

        Assert(!viewModel.CanCreateStockIn, "Stock-in create must be disabled when period is closed.");
        Assert(!viewModel.CanSaveStockInDraft, "Stock-in save must be disabled when period is closed.");
        Assert(
            viewModel.NewStockInTooltip.Contains("CLOSED", StringComparison.OrdinalIgnoreCase),
            $"Unexpected stock-in new tooltip in closed period: {viewModel.NewStockInTooltip}");
        Assert(
            viewModel.SaveStockInDraftTooltip.Contains("CLOSED", StringComparison.OrdinalIgnoreCase),
            $"Unexpected stock-in save tooltip in closed period: {viewModel.SaveStockInDraftTooltip}");

        Assert(!viewModel.CanApproveStockOut, "Stock-out approve must be disabled when period is closed.");
        Assert(
            viewModel.ApproveStockOutTooltip.Contains("CLOSED", StringComparison.OrdinalIgnoreCase),
            $"Unexpected stock-out approve tooltip in closed period: {viewModel.ApproveStockOutTooltip}");

        Assert(!viewModel.CanPostTransfer, "Transfer post must be disabled when period is closed.");
        Assert(
            viewModel.PostTransferTooltip.Contains("CLOSED", StringComparison.OrdinalIgnoreCase),
            $"Unexpected transfer post tooltip in closed period: {viewModel.PostTransferTooltip}");

        Assert(!viewModel.CanGenerateStockOpnameLines, "Stock opname line generation must be disabled when period is closed.");
        Assert(!viewModel.CanSubmitStockOpname, "Stock opname submit must be disabled when period is closed.");
        Assert(
            viewModel.GenerateStockOpnameLinesTooltip.Contains("CLOSED", StringComparison.OrdinalIgnoreCase),
            $"Unexpected stock opname generate tooltip in closed period: {viewModel.GenerateStockOpnameLinesTooltip}");
        Assert(
            viewModel.SubmitStockOpnameTooltip.Contains("CLOSED", StringComparison.OrdinalIgnoreCase),
            $"Unexpected stock opname submit tooltip in closed period: {viewModel.SubmitStockOpnameTooltip}");
    }

    private static async Task<UserAccessContext> BuildTestAccessContextAsync(params string[] actionCodes)
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");

        Assert(accessOptions is not null, "Admin login access options are required for viewmodel tests.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required for viewmodel tests.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required for viewmodel tests.");

        var company = accessOptions.Companies[0];
        var location = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == company.Id) ?? accessOptions.Locations[0];
        var actionCodeSet = new HashSet<string>(actionCodes, StringComparer.OrdinalIgnoreCase);
        var moduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var submoduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var actionCode in actionCodeSet)
        {
            var parts = actionCode.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            moduleCodes.Add(parts[0]);
            submoduleCodes.Add($"{parts[0]}.{parts[1]}");
        }

        return new UserAccessContext
        {
            UserId = accessOptions.UserId,
            Username = "admin",
            SelectedRoleId = accessOptions.Roles.FirstOrDefault()?.Id ?? 0,
            SelectedRoleCode = accessOptions.Roles.FirstOrDefault()?.Code ?? "ITEST",
            SelectedRoleName = accessOptions.Roles.FirstOrDefault()?.Name ?? "ITest",
            SelectedCompanyId = company.Id,
            SelectedCompanyCode = company.Code,
            SelectedCompanyName = company.Name,
            SelectedLocationId = location.Id,
            SelectedLocationCode = location.Code,
            SelectedLocationName = location.Name,
            AllowedCompanyIds = new HashSet<long> { company.Id },
            AllowedLocationIds = new HashSet<long> { location.Id },
            CompanyIds = new HashSet<long> { company.Id },
            LocationIds = new HashSet<long> { location.Id },
            ModuleCodes = moduleCodes,
            SubmoduleCodes = submoduleCodes,
            ScopeCodes = new HashSet<string>(submoduleCodes, StringComparer.OrdinalIgnoreCase),
            ActionCodes = actionCodeSet
        };
    }

    private static ObservableCollection<MainShellNavigationItem> BuildMainShellNavigation(UserAccessContext accessContext)
    {
        var method = typeof(MainShellViewModel).GetMethod("BuildNavigation", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(method is not null, "MainShellViewModel.BuildNavigation was not found.");

        var result = method!.Invoke(null, new object?[] { accessContext }) as ObservableCollection<MainShellNavigationItem>;
        Assert(result is not null, "MainShell navigation result was null.");
        return result!;
    }

    private static MainShellViewModel CreateMainShellViewModel(UserAccessContext accessContext, IAccessControlService service)
    {
        return new MainShellViewModel(
            accessContext,
            "IntegrationTest",
            service,
            () => { },
            () => { });
    }

    private static bool ContainsRootNavigationLabel(IEnumerable<MainShellNavigationItem> items, string label)
    {
        return items.Any(item => string.Equals(item.Label, label, StringComparison.Ordinal));
    }

    private static bool ContainsNavigationLabel(IEnumerable<MainShellNavigationItem> items, string label)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Label, label, StringComparison.Ordinal))
            {
                return true;
            }

            if (item.Children is not null && ContainsNavigationLabel(item.Children, label))
            {
                return true;
            }
        }

        return false;
    }

    private static MainShellNavigationItem? FindNavigationItemByLabel(IEnumerable<MainShellNavigationItem> items, string label)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Label, label, StringComparison.Ordinal))
            {
                return item;
            }

            if (item.Children is null)
            {
                continue;
            }

            var child = FindNavigationItemByLabel(item.Children, label);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static void SetJournalImportBundles(JournalManagementViewModel viewModel, IReadOnlyCollection<JournalImportBundleResult> bundles)
    {
        var stagedBundlesField = typeof(JournalManagementViewModel).GetField(
            "_stagedImportBundles",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert(stagedBundlesField is not null, "JournalManagementViewModel staged import bundle field was not found.");
        stagedBundlesField!.SetValue(viewModel, bundles.ToList());
    }

    private static void SetJournalId(JournalManagementViewModel viewModel, long journalId)
    {
        var journalIdField = typeof(JournalManagementViewModel).GetField(
            "_journalId",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert(journalIdField is not null, "JournalManagementViewModel journal id field was not found.");
        journalIdField!.SetValue(viewModel, journalId);
    }

    private static void SetInventoryMasterPolicy(
        InventoryViewModel viewModel,
        bool canMaintainMasterData,
        string masterCompanyLabel)
    {
        var canMaintainField = typeof(InventoryViewModel).GetField(
            "_canMaintainMasterInventoryData",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var masterCompanyLabelField = typeof(InventoryViewModel).GetField(
            "_masterCompanyLabel",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert(canMaintainField is not null, "InventoryViewModel master-data policy field was not found.");
        Assert(masterCompanyLabelField is not null, "InventoryViewModel master-company label field was not found.");

        canMaintainField!.SetValue(viewModel, canMaintainMasterData);
        masterCompanyLabelField!.SetValue(viewModel, masterCompanyLabel);
    }
}
