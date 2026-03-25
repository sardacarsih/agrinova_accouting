using System.Diagnostics;
using Accounting.Services;
using Accounting.ViewModels;

internal static partial class Program
{
    private static async Task TestUserManagementRoleEditorDirtyDiscardFlowAsync()
    {
        var service = CreateService();
        var viewModel = new UserManagementViewModel(service, "itest");
        await viewModel.EnsureLoadedAsync();

        viewModel.NewRoleCommand.Execute(null);
        Assert(viewModel.SelectedRole is not null, "NewRole command should initialize a role editor.");
        Assert(!viewModel.IsRoleEditorDirty, "Role editor should start clean after NewRole.");

        viewModel.SelectedRole!.Name = "Role Sementara";
        Assert(viewModel.IsRoleEditorDirty, "Editing role name should mark role editor as dirty.");

        if (viewModel.RoleScopeOptions.Count > 0)
        {
            var firstScope = viewModel.RoleScopeOptions[0];
            firstScope.IsSelected = !firstScope.IsSelected;
            Assert(viewModel.IsRoleEditorDirty, "Editing role scope selection should keep editor dirty.");
        }

        viewModel.DiscardRoleChangesCommand.Execute(null);

        Assert(!viewModel.IsRoleEditorDirty, "Discard should reset role editor dirty state.");
        Assert(
            string.IsNullOrWhiteSpace(viewModel.SelectedRole.Name),
            "Discard should restore role name from snapshot for new role editor.");
        Assert(
            viewModel.StatusMessage.Contains("dibatalkan", StringComparison.OrdinalIgnoreCase),
            "Discard should set a cancellation status message.");
    }

    private static async Task TestUserManagementRolePermissionFilterAndSelectedOnlyAsync()
    {
        var service = CreateService();
        var viewModel = new UserManagementViewModel(service, "itest");
        await viewModel.EnsureLoadedAsync();

        Assert(viewModel.Roles.Count > 0, "User management data should contain at least one role.");
        Assert(viewModel.RoleScopeOptions.Count > 0, "User management data should contain access scopes.");

        viewModel.ClearRolePermissionFilterCommand.Execute(null);

        var unfilteredVisibleIds = EnumerateVisibleRoleActions(viewModel)
            .Select(x => x.Id)
            .Distinct()
            .ToArray();
        Assert(
            viewModel.VisibleRolePermissionCount == unfilteredVisibleIds.Length,
            "VisibleRolePermissionCount should match rendered permission tree action count.");

        var seed = viewModel.RoleScopeOptions.First();
        var keyword = !string.IsNullOrWhiteSpace(seed.ActionCode)
            ? seed.ActionCode
            : !string.IsNullOrWhiteSpace(seed.SubmoduleCode)
                ? seed.SubmoduleCode
                : seed.ModuleCode;
        Assert(!string.IsNullOrWhiteSpace(keyword), "Seed scope should have searchable metadata.");

        viewModel.RolePermissionSearchText = keyword;

        var filteredActions = EnumerateVisibleRoleActions(viewModel).ToArray();
        Assert(filteredActions.Length > 0, "Permission search should return at least one row for seed keyword.");
        Assert(
            filteredActions.All(x => MatchesPermissionKeyword(x, keyword)),
            "Filtered permission tree should only contain rows matching the search keyword.");

        viewModel.RolePermissionSearchText = string.Empty;
        viewModel.ShowSelectedRolePermissionsOnly = true;

        var selectedOnlyActions = EnumerateVisibleRoleActions(viewModel).ToArray();
        Assert(selectedOnlyActions.All(x => x.IsSelected), "Selected-only filter should only show selected scopes.");
        Assert(
            viewModel.VisibleRolePermissionCount == viewModel.SelectedRolePermissionCount,
            "Visible permission count should equal selected permission count in selected-only mode.");
    }

    private static async Task TestUserManagementCloneRoleCommandCreatesRoleCopyAsync()
    {
        var service = CreateService();
        var seedData = await service.GetUserManagementDataAsync();
        Assert(seedData.AccessScopes.Count > 0, "At least one access scope is required for clone-role test.");

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sourceCode = $"ITEST_VM_CLONE_{Math.Abs(stamp % 1000000):000000}";
        var sourceName = $"Integration VM Clone Source {stamp}";
        var sourceScopeIds = seedData.AccessScopes.Take(2).Select(x => x.Id).ToArray();
        if (sourceScopeIds.Length == 0)
        {
            sourceScopeIds = new[] { seedData.AccessScopes[0].Id };
        }

        var sourceCreateResult = await service.SaveRoleAsync(
            new ManagedRole
            {
                Id = 0,
                Code = sourceCode,
                Name = sourceName,
                IsSuperRole = false,
                IsActive = true
            },
            sourceScopeIds,
            "admin");
        Assert(
            sourceCreateResult.IsSuccess && sourceCreateResult.EntityId.HasValue,
            $"Failed to create source role for clone test: {sourceCreateResult.Message}");

        var sourceRoleId = sourceCreateResult.EntityId!.Value;
        long? cloneRoleId = null;

        try
        {
            var viewModel = new UserManagementViewModel(service, "admin");
            await viewModel.EnsureLoadedAsync();

            viewModel.SelectedRole = viewModel.Roles.FirstOrDefault(x => x.Id == sourceRoleId);
            Assert(viewModel.SelectedRole is not null, "Source role should be selectable in user management view model.");

            var expectedCloneCode = BuildExpectedCloneCode(viewModel.Roles, viewModel.SelectedRole!.Code);
            var expectedCloneName = BuildExpectedCloneName(viewModel.Roles, viewModel.SelectedRole.Name);

            viewModel.CloneRoleCommand.Execute(null);
            await WaitForUserManagementViewModelIdleAsync(viewModel);

            Assert(
                !viewModel.StatusMessage.Contains("gagal", StringComparison.OrdinalIgnoreCase),
                $"Clone role command returned failure status: {viewModel.StatusMessage}");

            var clonedRole = viewModel.Roles.FirstOrDefault(x =>
                string.Equals(x.Code, expectedCloneCode, StringComparison.OrdinalIgnoreCase));
            Assert(clonedRole is not null, $"Expected cloned role '{expectedCloneCode}' was not found.");
            Assert(clonedRole!.Id != sourceRoleId, "Cloned role id should differ from source role id.");
            Assert(
                string.Equals(clonedRole.Name, expectedCloneName, StringComparison.Ordinal),
                "Cloned role name should follow clone naming rule.");
            Assert(
                viewModel.GetRoleScopeCount(clonedRole.Id) == viewModel.GetRoleScopeCount(sourceRoleId),
                "Cloned role should copy permission scopes from source role.");

            cloneRoleId = clonedRole.Id;
        }
        finally
        {
            if (cloneRoleId.HasValue)
            {
                await service.DeleteRoleAsync(cloneRoleId.Value, "admin");
            }

            await service.DeleteRoleAsync(sourceRoleId, "admin");
        }
    }

    private static async Task TestUserManagementTryLeaveRoleEditorReturnsTrueWhenCleanAsync()
    {
        var service = CreateService();
        var viewModel = new UserManagementViewModel(service, "itest");
        await viewModel.EnsureLoadedAsync();

        Assert(!viewModel.IsRoleEditorDirty, "Role editor should be clean after initial load.");
        Assert(viewModel.TryLeaveRoleEditor(), "TryLeaveRoleEditor should return true when there are no unsaved changes.");
    }

    private static async Task TestUserManagementRolePermissionTreeIncludesInventoryImportActionsAsync()
    {
        var service = CreateService();
        var viewModel = new UserManagementViewModel(service, "admin");
        await viewModel.EnsureLoadedAsync();

        var importScope = viewModel.RoleScopeOptions.FirstOrDefault(option =>
            string.Equals(option.ModuleCode, "inventory", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.SubmoduleCode, "api_inv", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.ActionCode, "import_master_data", StringComparison.OrdinalIgnoreCase));
        Assert(importScope is not null, "Role scope options should include inventory.api_inv.import_master_data.");
        Assert(
            string.Equals(importScope!.Label, "Import Master Data", StringComparison.Ordinal),
            $"Unexpected inventory import label: {importScope.Label}");

        var templateScope = viewModel.RoleScopeOptions.FirstOrDefault(option =>
            string.Equals(option.ModuleCode, "inventory", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.SubmoduleCode, "api_inv", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.ActionCode, "download_import_template", StringComparison.OrdinalIgnoreCase));
        Assert(templateScope is not null, "Role scope options should include inventory.api_inv.download_import_template.");
        Assert(
            string.Equals(templateScope!.Label, "Download Import Template", StringComparison.Ordinal),
            $"Unexpected inventory template label: {templateScope.Label}");

        viewModel.RolePermissionSearchText = "Import Master Data";
        var filteredLabels = EnumerateVisibleRoleActions(viewModel)
            .Select(x => x.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert(filteredLabels.Length > 0, "Permission filter should surface the inventory import action.");
        Assert(
            filteredLabels.Any(label => string.Equals(label, "Import Master Data", StringComparison.Ordinal)),
            "Permission tree should render the dedicated inventory import label.");
    }

    private static IEnumerable<SelectableOption> EnumerateVisibleRoleActions(UserManagementViewModel viewModel)
    {
        return viewModel.RolePermissionModules
            .SelectMany(module => module.Submodules)
            .SelectMany(submodule => submodule.Actions);
    }

    private static bool MatchesPermissionKeyword(SelectableOption option, string keyword)
    {
        return option.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               option.ModuleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               option.SubmoduleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               option.ActionCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               option.ModuleCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               option.SubmoduleCode.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildExpectedCloneCode(IEnumerable<ManagedRole> roles, string sourceCode)
    {
        var baseCode = string.IsNullOrWhiteSpace(sourceCode)
            ? "ROLE_SALINAN"
            : $"{sourceCode.Trim().ToUpperInvariant()}_SALINAN";

        var existingCodes = roles
            .Select(x => x.Code)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingCodes.Contains(baseCode))
        {
            return baseCode;
        }

        var index = 2;
        while (existingCodes.Contains($"{baseCode}_{index}"))
        {
            index++;
        }

        return $"{baseCode}_{index}";
    }

    private static string BuildExpectedCloneName(IEnumerable<ManagedRole> roles, string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName)
            ? "Role Salinan"
            : $"{sourceName.Trim()} (Salinan)";

        var existingNames = roles
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        var index = 2;
        while (existingNames.Contains($"{baseName} {index}"))
        {
            index++;
        }

        return $"{baseName} {index}";
    }

    private static async Task WaitForUserManagementViewModelIdleAsync(UserManagementViewModel viewModel, int timeoutMs = 15000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (!viewModel.IsBusy)
            {
                await Task.Delay(50);
                if (!viewModel.IsBusy)
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("UserManagementViewModel remained busy beyond timeout.");
    }
}
