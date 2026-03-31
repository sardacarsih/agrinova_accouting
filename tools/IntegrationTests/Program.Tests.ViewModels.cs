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

    private static async Task TestJournalManagementViewModel_BrowseRowsMirrorSearchResultsAsync()
    {
        var service = CreateService();
        var accessContext = await BuildTestAccessContextAsync("accounting.transactions.view");
        var viewModel = new JournalManagementViewModel(service, accessContext, "ITEST Location");

        var summaries = new[]
        {
            new ManagedJournalSummary
            {
                Id = 501,
                JournalNo = "JR-ITEST-501",
                JournalDate = new DateTime(2026, 3, 25),
                ReferenceNo = "REF-501",
                Description = "Jurnal ITest 501",
                Status = "DRAFT",
                TotalDebit = 125000m,
                TotalCredit = 125000m
            },
            new ManagedJournalSummary
            {
                Id = 502,
                JournalNo = "JR-ITEST-502",
                JournalDate = new DateTime(2026, 3, 26),
                ReferenceNo = "REF-502",
                Description = "Jurnal ITest 502",
                Status = "POSTED",
                TotalDebit = 98000m,
                TotalCredit = 98000m
            }
        };

        InvokePrivateInstanceMethod(viewModel, "ApplyBrowseSearchResult", new object?[] { summaries });

        Assert(viewModel.SearchResults.Count == 2, "SearchResults should keep browse search payload.");
        Assert(viewModel.BrowseJournalRows.Count == 2, "BrowseJournalRows should mirror browse search payload.");
        Assert(
            string.Equals(viewModel.BrowseJournalRows[0].JournalNo, "JR-ITEST-501", StringComparison.Ordinal),
            $"Unexpected browse row journal no: {viewModel.BrowseJournalRows[0].JournalNo}");
        Assert(
            string.Equals(viewModel.BrowseJournalRows[1].Status, "POSTED", StringComparison.Ordinal),
            $"Unexpected browse row status: {viewModel.BrowseJournalRows[1].Status}");
        Assert(
            viewModel.BrowseResultSummary.Contains("2 jurnal", StringComparison.Ordinal),
            $"Unexpected browse result summary: {viewModel.BrowseResultSummary}");
    }

    private static async Task TestJournalManagementViewModel_BrowseDetailLoadsOnceAndCachesAsync()
    {
        var bundleCallCount = 0;
        var service = CreateAccessControlServiceProxy((method, _) =>
        {
            if (string.Equals(method.Name, nameof(IAccessControlService.GetJournalBundleAsync), StringComparison.Ordinal))
            {
                bundleCallCount++;
                return Task.FromResult<ManagedJournalBundle?>(new ManagedJournalBundle
                {
                    Header = new ManagedJournalHeader
                    {
                        Id = 701,
                        CompanyId = 77,
                        LocationId = 88,
                        JournalNo = "JR-CACHE-701",
                        JournalDate = new DateTime(2026, 3, 27),
                        PeriodMonth = new DateTime(2026, 3, 1),
                        Status = "DRAFT"
                    },
                    Lines = new List<ManagedJournalLine>
                    {
                        new() { LineNo = 1, AccountCode = "1100", AccountName = "Kas", Description = "Line 1", Debit = 150m, Credit = 0m },
                        new() { LineNo = 2, AccountCode = "4100", AccountName = "Pendapatan", Description = "Line 2", Debit = 0m, Credit = 150m }
                    }
                });
            }

            throw new NotSupportedException($"Unexpected method call: {method.Name}");
        });

        var accessContext = new UserAccessContext
        {
            Username = "itest",
            SelectedCompanyId = 77,
            SelectedLocationId = 88
        };
        var viewModel = new JournalManagementViewModel(service, accessContext, "ITEST Location");
        var row = new JournalBrowseRowViewModel(new ManagedJournalSummary
        {
            Id = 701,
            JournalNo = "JR-CACHE-701",
            JournalDate = new DateTime(2026, 3, 27),
            ReferenceNo = "CACHE-701",
            Description = "Jurnal cache test",
            Status = "DRAFT",
            TotalDebit = 150m,
            TotalCredit = 150m
        });

        await viewModel.EnsureBrowseJournalDetailLoadedAsync(row);
        await viewModel.EnsureBrowseJournalDetailLoadedAsync(row);

        Assert(bundleCallCount == 1, $"Browse detail should be loaded once, actual calls={bundleCallCount}.");
        Assert(row.IsDetailLoaded, "Browse detail row should be marked loaded after first fetch.");
        Assert(!row.IsDetailLoading, "Browse detail row should not stay in loading state.");
        Assert(row.Lines.Count == 2, $"Browse detail row should expose 2 lines, actual={row.Lines.Count}.");
        Assert(string.IsNullOrWhiteSpace(row.DetailErrorMessage), $"Unexpected browse detail error: {row.DetailErrorMessage}");
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

    private static async Task TestJournalManagementViewModel_SelectedBrowseRowsDriveExportResolutionAsync()
    {
        var service = CreateService();
        var accessContext = await BuildTestAccessContextAsync("accounting.transactions.export");
        var viewModel = new JournalManagementViewModel(service, accessContext, "ITEST Location");

        var firstRow = new JournalBrowseRowViewModel(new ManagedJournalSummary
        {
            Id = 801,
            JournalNo = "JR-EXPORT-801",
            JournalDate = new DateTime(2026, 3, 1),
            ReferenceNo = "EXP-801",
            Description = "Export 801",
            Status = "DRAFT",
            TotalDebit = 200m,
            TotalCredit = 200m
        });
        var secondRow = new JournalBrowseRowViewModel(new ManagedJournalSummary
        {
            Id = 802,
            JournalNo = "JR-EXPORT-802",
            JournalDate = new DateTime(2026, 3, 2),
            ReferenceNo = "EXP-802",
            Description = "Export 802",
            Status = "APPROVED",
            TotalDebit = 350m,
            TotalCredit = 350m
        });

        viewModel.SetSelectedBrowseJournalRows(new[] { firstRow, secondRow });

        var resolved = InvokePrivateInstanceMethod<List<ManagedJournalSummary>>(viewModel, "ResolveSelectedJournals", new object?[] { null });

        Assert(viewModel.SelectedBrowseJournalRows.Count == 2, "SelectedBrowseJournalRows should keep master-row selection.");
        Assert(resolved.Count == 2, $"Resolved selected journals should return 2 items, actual={resolved.Count}.");
        Assert(
            resolved.Any(summary => summary.Id == 801) && resolved.Any(summary => summary.Id == 802),
            "Resolved selected journals should include both selected master rows.");
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

    private static Task TestMasterDataViewModel_AccountImportExportCommandsReflectBusyStateAsync()
    {
        var service = CreateAccessControlServiceProxy((method, _) =>
        {
            throw new NotSupportedException($"Unexpected method call: {method.Name}");
        });

        var viewModel = new MasterDataViewModel(
            service,
            "admin",
            companyId: 77,
            locationId: 88,
            canManageAccountingPeriod: false);

        Assert(viewModel.CanExportAccounts, "Account export should be enabled when company id is valid and viewmodel is idle.");
        Assert(viewModel.CanImportAccounts, "Account import should be enabled when company id is valid and viewmodel is idle.");
        Assert(
            string.Equals(viewModel.ExportAccountsTooltip, "Export seluruh master akun company aktif ke XLSX.", StringComparison.Ordinal),
            $"Unexpected account export tooltip: {viewModel.ExportAccountsTooltip}");
        Assert(
            string.Equals(viewModel.ImportAccountsTooltip, "Import master akun dari XLSX dengan mode upsert only.", StringComparison.Ordinal),
            $"Unexpected account import tooltip: {viewModel.ImportAccountsTooltip}");
        Assert(viewModel.ExportAccountsCommand.CanExecute(null), "Account export command should be executable when idle.");
        Assert(viewModel.ImportAccountsCommand.CanExecute(null), "Account import command should be executable when idle.");

        SetPrivateField(viewModel, "_isBusy", true);

        Assert(!viewModel.CanExportAccounts, "Account export should be disabled while viewmodel is busy.");
        Assert(!viewModel.CanImportAccounts, "Account import should be disabled while viewmodel is busy.");
        Assert(
            string.Equals(viewModel.ExportAccountsTooltip, "Master akun sedang sibuk atau company tidak valid.", StringComparison.Ordinal),
            $"Unexpected busy export tooltip: {viewModel.ExportAccountsTooltip}");
        Assert(
            string.Equals(viewModel.ImportAccountsTooltip, "Master akun sedang sibuk atau company tidak valid.", StringComparison.Ordinal),
            $"Unexpected busy import tooltip: {viewModel.ImportAccountsTooltip}");
        Assert(!viewModel.ExportAccountsCommand.CanExecute(null), "Account export command should be disabled while busy.");
        Assert(!viewModel.ImportAccountsCommand.CanExecute(null), "Account import command should be disabled while busy.");

        var invalidCompanyViewModel = new MasterDataViewModel(
            service,
            "admin",
            companyId: 0,
            locationId: 88,
            canManageAccountingPeriod: false);
        Assert(!invalidCompanyViewModel.CanExportAccounts, "Account export should be disabled when company id is invalid.");
        Assert(!invalidCompanyViewModel.CanImportAccounts, "Account import should be disabled when company id is invalid.");

        return Task.CompletedTask;
    }

    private static Task TestMasterDataViewModel_AccountImportValidationFailureUpdatesStatusAndPanelAsync()
    {
        var service = CreateAccessControlServiceProxy((method, _) =>
        {
            throw new NotSupportedException($"Unexpected method call: {method.Name}");
        });
        var viewModel = new MasterDataViewModel(
            service,
            "admin",
            companyId: 77,
            locationId: 88,
            canManageAccountingPeriod: false);

        InvokePrivateInstanceMethod(
            viewModel,
            "ApplyAccountImportValidationFailure",
            new AccountImportParseResult
            {
                IsSuccess = false,
                Message = "Format import tidak valid.",
                Errors =
                [
                    new InventoryImportError
                    {
                        SheetName = "Accounts",
                        RowNumber = 3,
                        Message = "Parent akun tidak ditemukan."
                    }
                ]
            });

        Assert(
            viewModel.StatusMessage.Contains("Validasi file import master akun gagal.", StringComparison.Ordinal),
            $"Unexpected validation failure status: {viewModel.StatusMessage}");
        Assert(
            viewModel.StatusMessage.Contains("1 error", StringComparison.Ordinal),
            $"Validation failure status should include error count: {viewModel.StatusMessage}");
        Assert(viewModel.AccountImportErrorPanel.HasErrors, "Validation failure should populate the account import error panel.");
        Assert(viewModel.AccountImportErrorPanel.Errors.Count == 1, $"Expected one validation error, got {viewModel.AccountImportErrorPanel.Errors.Count}.");
        Assert(
            viewModel.AccountImportErrorPanel.Summary.Contains("Format import tidak valid.", StringComparison.Ordinal),
            $"Unexpected validation error summary: {viewModel.AccountImportErrorPanel.Summary}");

        return Task.CompletedTask;
    }

    private static async Task TestMasterDataViewModel_AccountImportExecutionResultUpdatesStatusAndErrorsAsync()
    {
        var service = CreateAccessControlServiceProxy((method, _) =>
        {
            throw new NotSupportedException($"Unexpected method call: {method.Name}");
        });
        var viewModel = new MasterDataViewModel(
            service,
            "admin",
            companyId: 77,
            locationId: 88,
            canManageAccountingPeriod: false);

        await InvokePrivateInstanceTaskAsync(
            viewModel,
            "ApplyAccountImportExecutionResultAsync",
            new AccountImportExecutionResult
            {
                IsSuccess = false,
                Message = "Import master akun gagal.",
                Errors = []
            },
            false);

        Assert(
            string.Equals(viewModel.StatusMessage, "Import master akun gagal.", StringComparison.Ordinal),
            $"Unexpected import failure status: {viewModel.StatusMessage}");
        Assert(viewModel.AccountImportErrorPanel.HasErrors, "Import failure should populate the account import error panel.");
        Assert(viewModel.AccountImportErrorPanel.Errors.Count == 1, "Import failure without row errors should add one generic error.");
        Assert(
            string.Equals(viewModel.AccountImportErrorPanel.Errors[0].SheetName, "Accounts", StringComparison.Ordinal),
            $"Unexpected generic error sheet name: {viewModel.AccountImportErrorPanel.Errors[0].SheetName}");
        Assert(
            string.Equals(viewModel.AccountImportErrorPanel.Errors[0].Message, "Import master akun gagal.", StringComparison.Ordinal),
            $"Unexpected generic import error message: {viewModel.AccountImportErrorPanel.Errors[0].Message}");

        await InvokePrivateInstanceTaskAsync(
            viewModel,
            "ApplyAccountImportExecutionResultAsync",
            new AccountImportExecutionResult
            {
                IsSuccess = true,
                Message = "Import master akun berhasil. Create 1, update 0."
            },
            false);

        Assert(
            string.Equals(viewModel.StatusMessage, "Import master akun berhasil. Create 1, update 0.", StringComparison.Ordinal),
            $"Unexpected import success status: {viewModel.StatusMessage}");
        Assert(!viewModel.AccountImportErrorPanel.HasErrors, "Successful account import should clear previous errors.");
        Assert(
            string.IsNullOrWhiteSpace(viewModel.AccountImportErrorPanel.Summary),
            $"Successful account import should clear the panel summary, got: {viewModel.AccountImportErrorPanel.Summary}");
    }

    private static Task TestMasterDataViewModel_EstateHierarchyCommandsReflectSelectionAndBusyStateAsync()
    {
        var service = CreateAccessControlServiceProxy((method, _) =>
        {
            throw new NotSupportedException($"Unexpected method call: {method.Name}");
        });

        var viewModel = new MasterDataViewModel(
            service,
            "admin",
            companyId: 77,
            locationId: 88,
            canManageAccountingPeriod: false);

        Assert(viewModel.CanCreateEstate, "Estate create should be enabled when company/location are valid and viewmodel is idle.");
        Assert(viewModel.CanImportEstateHierarchy, "Estate hierarchy import should be enabled when idle.");
        Assert(viewModel.CanExportEstateHierarchy, "Estate hierarchy export should be enabled when idle.");
        Assert(!viewModel.CanCreateDivision, "Division create should require an estate selection.");
        Assert(!viewModel.CanCreateBlock, "Block create should require a division selection.");
        Assert(
            string.Equals(viewModel.ExportEstateHierarchyTooltip, "Export seluruh master estate/division/blok ke XLSX.", StringComparison.Ordinal),
            $"Unexpected estate hierarchy export tooltip: {viewModel.ExportEstateHierarchyTooltip}");
        Assert(
            string.Equals(viewModel.ImportEstateHierarchyTooltip, "Import hierarchy estate/division/blok dari workbook XLSX 3 sheet.", StringComparison.Ordinal),
            $"Unexpected estate hierarchy import tooltip: {viewModel.ImportEstateHierarchyTooltip}");

        viewModel.SetSelectedEstateHierarchyItem(new ManagedEstate
        {
            Id = 1,
            Code = "EST01",
            Name = "Estate 01",
            IsActive = true
        });
        Assert(viewModel.CanCreateDivision, "Division create should be enabled when an estate is selected.");
        Assert(!viewModel.CanCreateBlock, "Block create should stay disabled when only an estate is selected.");
        Assert(viewModel.CanEditEstateHierarchy, "Hierarchy edit should be enabled when an estate is selected.");
        Assert(viewModel.CanDeactivateEstateHierarchy, "Hierarchy deactivate should be enabled when an estate is selected.");

        viewModel.SetSelectedEstateHierarchyItem(new ManagedDivision
        {
            Id = 2,
            EstateId = 1,
            EstateCode = "EST01",
            Code = "DIV01",
            Name = "Division 01",
            IsActive = true
        });
        Assert(viewModel.CanCreateBlock, "Block create should be enabled when a division is selected.");
        Assert(
            string.Equals(viewModel.SelectedEstateHierarchyCode, "EST01-DIV01", StringComparison.Ordinal),
            $"Unexpected selected hierarchy code for division: {viewModel.SelectedEstateHierarchyCode}");

        SetPrivateField(viewModel, "_isBusy", true);

        Assert(!viewModel.CanCreateEstate, "Estate create should be disabled while viewmodel is busy.");
        Assert(!viewModel.CanCreateDivision, "Division create should be disabled while viewmodel is busy.");
        Assert(!viewModel.CanCreateBlock, "Block create should be disabled while viewmodel is busy.");
        Assert(!viewModel.CanImportEstateHierarchy, "Estate hierarchy import should be disabled while viewmodel is busy.");
        Assert(!viewModel.CanExportEstateHierarchy, "Estate hierarchy export should be disabled while viewmodel is busy.");
        Assert(
            string.Equals(viewModel.ExportEstateHierarchyTooltip, "Hierarchy estate/division/blok sedang sibuk atau company/lokasi tidak valid.", StringComparison.Ordinal),
            $"Unexpected busy estate hierarchy export tooltip: {viewModel.ExportEstateHierarchyTooltip}");
        Assert(
            string.Equals(viewModel.ImportEstateHierarchyTooltip, "Hierarchy estate/division/blok sedang sibuk atau company/lokasi tidak valid.", StringComparison.Ordinal),
            $"Unexpected busy estate hierarchy import tooltip: {viewModel.ImportEstateHierarchyTooltip}");

        var invalidScopeViewModel = new MasterDataViewModel(
            service,
            "admin",
            companyId: 0,
            locationId: 0,
            canManageAccountingPeriod: false);
        Assert(!invalidScopeViewModel.CanCreateEstate, "Estate create should be disabled for invalid company/location.");
        Assert(!invalidScopeViewModel.CanImportEstateHierarchy, "Estate hierarchy import should be disabled for invalid company/location.");
        Assert(!invalidScopeViewModel.CanExportEstateHierarchy, "Estate hierarchy export should be disabled for invalid company/location.");

        return Task.CompletedTask;
    }

    private static Task TestMasterDataViewModel_EstateHierarchyImportFailureUpdatesStatusAndPanelAsync()
    {
        var service = CreateAccessControlServiceProxy((method, _) =>
        {
            throw new NotSupportedException($"Unexpected method call: {method.Name}");
        });
        var viewModel = new MasterDataViewModel(
            service,
            "admin",
            companyId: 77,
            locationId: 88,
            canManageAccountingPeriod: false);

        InvokePrivateInstanceMethod(
            viewModel,
            "ApplyEstateHierarchyImportFailure",
            "Format hierarchy tidak valid.",
            new InventoryImportError[]
            {
                new()
                {
                    SheetName = "Blocks",
                    RowNumber = 4,
                    Message = "DivisionCode tidak ditemukan."
                }
            });

        Assert(
            viewModel.StatusMessage.Contains("Format hierarchy tidak valid.", StringComparison.Ordinal),
            $"Unexpected hierarchy import failure status: {viewModel.StatusMessage}");
        Assert(
            viewModel.StatusMessage.Contains("1 error", StringComparison.Ordinal),
            $"Hierarchy import failure status should include error count: {viewModel.StatusMessage}");
        Assert(viewModel.EstateHierarchyImportErrorPanel.HasErrors, "Hierarchy import failure should populate the hierarchy error panel.");
        Assert(viewModel.EstateHierarchyImportErrorPanel.Errors.Count == 1, $"Expected one hierarchy import error, got {viewModel.EstateHierarchyImportErrorPanel.Errors.Count}.");
        Assert(
            viewModel.EstateHierarchyImportErrorPanel.Summary.Contains("Format hierarchy tidak valid.", StringComparison.Ordinal),
            $"Unexpected hierarchy import error summary: {viewModel.EstateHierarchyImportErrorPanel.Summary}");

        return Task.CompletedTask;
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

    private static void InvokePrivateInstanceMethod(object instance, string methodName, params object?[]? arguments)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, $"{instance.GetType().Name}.{methodName} was not found.");
        method!.Invoke(instance, arguments);
    }

    private static T InvokePrivateInstanceMethod<T>(object instance, string methodName, params object?[]? arguments)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, $"{instance.GetType().Name}.{methodName} was not found.");
        var result = method!.Invoke(instance, arguments);
        Assert(result is T, $"{instance.GetType().Name}.{methodName} returned unexpected result.");
        return (T)result!;
    }

    private static async Task InvokePrivateInstanceTaskAsync(object instance, string methodName, params object?[]? arguments)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, $"{instance.GetType().Name}.{methodName} was not found.");
        var result = method!.Invoke(instance, arguments);
        Assert(result is Task, $"{instance.GetType().Name}.{methodName} did not return Task.");
        await ((Task)result!);
    }

    private static void SetPrivateField<T>(object instance, string fieldName, T value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(field is not null, $"{instance.GetType().Name}.{fieldName} field was not found.");
        field!.SetValue(instance, value);
    }

    private static IAccessControlService CreateAccessControlServiceProxy(Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create<IAccessControlService, AccessControlServiceDispatchProxy>();
        var dispatchProxy = (AccessControlServiceDispatchProxy)(object)proxy!;
        dispatchProxy.Handler = handler;
        return proxy!;
    }

    private class AccessControlServiceDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert(targetMethod is not null, "DispatchProxy target method must not be null.");
            Assert(Handler is not null, "DispatchProxy handler must not be null.");
            return Handler!(targetMethod!, args);
        }
    }
}
