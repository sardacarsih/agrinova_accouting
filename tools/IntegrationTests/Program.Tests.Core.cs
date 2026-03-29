using Npgsql;
using Accounting.Services;

internal static partial class Program
{
    private static async Task TestGetLoginAccessOptionsAsync()
    {
        var service = CreateService();
        var options = await service.GetLoginAccessOptionsAsync("admin");

        Assert(options is not null, "Login access options should not be null for admin.");
        Assert(options!.Roles.Count > 0, "At least one active role must exist.");

        var companyIds = options.Companies.Select(x => x.Id).ToHashSet();
        var locationIds = options.Locations.Select(x => x.Id).ToHashSet();

        foreach (var role in options.Roles)
        {
            Assert(options.ScopeCodesByRoleId.ContainsKey(role.Id), $"Missing scope map for role {role.Code}.");
        }

        Assert(options.CompanyIdsByUserId.ContainsKey(options.UserId), $"Missing company map for user {options.Username}.");
        Assert(options.LocationIdsByUserId.ContainsKey(options.UserId), $"Missing location map for user {options.Username}.");

        foreach (var companyId in options.CompanyIdsByUserId[options.UserId])
        {
            Assert(companyIds.Contains(companyId), $"User {options.Username} references unknown company id {companyId}.");
        }

        foreach (var locationId in options.LocationIdsByUserId[options.UserId])
        {
            Assert(locationIds.Contains(locationId), $"User {options.Username} references unknown location id {locationId}.");
        }
    }

    private static async Task TestInventoryApiInvRuntimeMigrationVerifierAsync()
    {
        var verificationResult = await InventoryApiInvRuntimeMigrationVerifier.VerifyAsync(
            DatabaseAuthOptions.FromConfiguration());

        Assert(!verificationResult.WasSkipped, "Runtime migration verification should not be skipped during integration tests.");
        Assert(!verificationResult.ShouldWarn, $"Runtime migration verification reported missing actions: {verificationResult.Message}");
        Assert(
            verificationResult.MissingActionCodes.Count == 0,
            $"Expected no missing inventory API import actions, got: {string.Join(", ", verificationResult.MissingActionCodes)}");
    }

    private static async Task TestRbacDbViewAndFunctionSmokeAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        var managementData = await service.GetUserManagementDataAsync();

        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var createScope = managementData.AccessScopes.FirstOrDefault(scope =>
            string.Equals(scope.ModuleCode, "accounting", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(scope.SubmoduleCode, "transactions", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(scope.ActionCode, "create", StringComparison.OrdinalIgnoreCase));
        Assert(createScope is not null, "Required accounting.transactions.create scope was not found.");

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var roleCode = $"ITEST_RBAC_SMOKE_{stamp}";
        var username = $"itest_rbac_smoke_{stamp}";
        long? roleId = null;
        long? userId = null;

        try
        {
            var roleSaveResult = await service.SaveRoleAsync(
                new ManagedRole
                {
                    Id = 0,
                    Code = roleCode,
                    Name = "RBAC Smoke Role",
                    IsSuperRole = false,
                    IsActive = true
                },
                [createScope!.Id],
                "admin");
            Assert(roleSaveResult.IsSuccess && roleSaveResult.EntityId.HasValue, "Failed to create RBAC smoke role.");
            roleId = roleSaveResult.EntityId!.Value;

            var userSaveResult = await service.SaveUserAsync(
                new ManagedUser
                {
                    Id = 0,
                    Username = username,
                    FullName = "RBAC Smoke User",
                    Email = $"{username}@local",
                    IsActive = true,
                    DefaultCompanyId = companyId,
                    DefaultLocationId = locationId
                },
                "Admin@123",
                [roleId.Value],
                [companyId],
                [locationId],
                "admin");
            Assert(userSaveResult.IsSuccess && userSaveResult.EntityId.HasValue, "Failed to create RBAC smoke user.");
            userId = userSaveResult.EntityId!.Value;

            await using var connection = await OpenConnectionAsync();

            await using (var permissionViewCommand = new NpgsqlCommand(
                @"SELECT COUNT(1)
FROM vw_user_effective_permissions
WHERE user_id = @user_id
  AND lower(module_code) = 'accounting'
  AND lower(submodule_code) = 'transactions'
  AND lower(action_code) = 'create';",
                connection))
            {
                permissionViewCommand.Parameters.AddWithValue("user_id", userId.Value);
                var permissionViewCount = Convert.ToInt32(await permissionViewCommand.ExecuteScalarAsync());
                Assert(permissionViewCount == 1, $"Expected one effective permission row, got {permissionViewCount}.");
            }

            await using (var functionCommand = new NpgsqlCommand(
                @"SELECT
    fn_user_has_permission(@username, 'accounting', 'transactions', 'create', @company_id, @location_id),
    fn_user_has_permission(@username, 'accounting', 'transactions', 'approve', @company_id, @location_id),
    fn_user_has_permission(@username, 'accounting', 'transactions', 'create', @invalid_company_id, @location_id);",
                connection))
            {
                functionCommand.Parameters.AddWithValue("username", username);
                functionCommand.Parameters.AddWithValue("company_id", companyId);
                functionCommand.Parameters.AddWithValue("location_id", locationId);
                functionCommand.Parameters.AddWithValue("invalid_company_id", long.MaxValue - 99);

                await using var reader = await functionCommand.ExecuteReaderAsync();
                Assert(await reader.ReadAsync(), "RBAC function query should return one row.");
                Assert(reader.GetBoolean(0), "Expected create permission to be granted.");
                Assert(!reader.GetBoolean(1), "Approve permission should not be granted.");
                Assert(!reader.GetBoolean(2), "Permission should fail for company outside user scope.");
            }
        }
        finally
        {
            if (userId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();
                await using (var deleteRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @id;", connection))
                {
                    deleteRoles.Parameters.AddWithValue("id", userId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand("DELETE FROM app_users WHERE id = @id;", connection))
                {
                    deleteUser.Parameters.AddWithValue("id", userId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }
            }

            if (roleId.HasValue)
            {
                await service.DeleteRoleAsync(roleId.Value, "admin");
            }
        }
    }

    private static async Task TestRbacPermissionSeedCoverageAsync()
    {
        var requiredPermissions = new (string ModuleCode, string SubmoduleCode, string ActionCode)[]
        {
            ("accounting", "user_management", "create"),
            ("accounting", "user_management", "update"),
            ("accounting", "user_management", "manage_roles"),
            ("accounting", "user_management", "manage_companies"),
            ("accounting", "user_management", "manage_locations"),
            ("accounting", "master_data", "create"),
            ("accounting", "master_data", "update"),
            ("accounting", "master_data", "delete"),
            ("accounting", "transactions", "create"),
            ("accounting", "transactions", "update"),
            ("accounting", "transactions", "submit"),
            ("accounting", "transactions", "approve"),
            ("accounting", "transactions", "post"),
            ("inventory", "kategori", "create"),
            ("inventory", "kategori", "update"),
            ("inventory", "kategori", "delete"),
            ("inventory", "item", "create"),
            ("inventory", "item", "update"),
            ("inventory", "item", "delete"),
            ("inventory", "satuan", "create"),
            ("inventory", "satuan", "update"),
            ("inventory", "satuan", "delete"),
            ("inventory", "gudang", "create"),
            ("inventory", "gudang", "update"),
            ("inventory", "gudang", "delete"),
            ("inventory", "stock_in", "create"),
            ("inventory", "stock_in", "update"),
            ("inventory", "stock_in", "submit"),
            ("inventory", "stock_in", "approve"),
            ("inventory", "stock_in", "post"),
            ("inventory", "stock_out", "create"),
            ("inventory", "stock_out", "update"),
            ("inventory", "stock_out", "submit"),
            ("inventory", "stock_out", "approve"),
            ("inventory", "stock_out", "post"),
            ("inventory", "transfer", "create"),
            ("inventory", "transfer", "update"),
            ("inventory", "transfer", "submit"),
            ("inventory", "transfer", "approve"),
            ("inventory", "transfer", "post"),
            ("inventory", "stock_opname", "create"),
            ("inventory", "stock_opname", "update"),
            ("inventory", "stock_opname", "submit"),
            ("inventory", "stock_opname", "approve"),
            ("inventory", "stock_opname", "post"),
            ("inventory", "api_inv", "manage_master_company"),
            ("inventory", "api_inv", "update_settings"),
            ("inventory", "api_inv", "sync_upload"),
            ("inventory", "api_inv", "sync_download"),
            ("inventory", "api_inv", "download_import_template"),
            ("inventory", "api_inv", "import_master_data"),
            ("inventory", "api_inv", "pull_journal")
        };

        await using var connection = await OpenConnectionAsync();
        var missingPermissions = new List<string>();

        foreach (var permission in requiredPermissions)
        {
            await using var command = new NpgsqlCommand(
                @"SELECT COUNT(1)
FROM sec_actions a
JOIN sec_submodules sm ON sm.id = a.submodule_id
JOIN sec_modules mo ON mo.id = sm.module_id
WHERE lower(mo.module_code) = lower(@module_code)
  AND lower(sm.submodule_code) = lower(@submodule_code)
  AND lower(a.action_code) = lower(@action_code);",
                connection);
            command.Parameters.AddWithValue("module_code", permission.ModuleCode);
            command.Parameters.AddWithValue("submodule_code", permission.SubmoduleCode);
            command.Parameters.AddWithValue("action_code", permission.ActionCode);

            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (count <= 0)
            {
                missingPermissions.Add($"{permission.ModuleCode}.{permission.SubmoduleCode}.{permission.ActionCode}");
            }
        }

        Assert(
            missingPermissions.Count == 0,
            $"Missing RBAC seed permissions: {string.Join(", ", missingPermissions)}");
    }

    private static long RequireAccessScopeId(
        UserManagementData managementData,
        string moduleCode,
        string submoduleCode,
        string actionCode)
    {
        var scope = managementData.AccessScopes.FirstOrDefault(candidate =>
            string.Equals(candidate.ModuleCode, moduleCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.SubmoduleCode, submoduleCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ActionCode, actionCode, StringComparison.OrdinalIgnoreCase));

        Assert(
            scope is not null,
            $"Required access scope was not found: {moduleCode}.{submoduleCode}.{actionCode}.");

        return scope!.Id;
    }

    private static ManagedRole RequireRole(UserManagementData managementData, string roleCode)
    {
        var role = managementData.Roles.FirstOrDefault(candidate =>
            string.Equals(candidate.Code, roleCode, StringComparison.OrdinalIgnoreCase));

        Assert(role is not null, $"Required role was not found: {roleCode}.");
        return role!;
    }

    private static async Task TestSaveUserSingleRoleRuleAsync()
    {
        var service = CreateService();
        var data = await service.GetUserManagementDataAsync();
        var activeRoles = data.Roles.Where(x => x.IsActive).OrderBy(x => x.Id).ToList();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");

        Assert(activeRoles.Count > 0, "No active role found for testing.");
        Assert(accessOptions is not null, "Admin access options should not be null.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required for SaveUser test.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required for SaveUser test.");

        var defaultCompanyId = accessOptions.Companies[0].Id;
        var defaultLocationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == defaultCompanyId)?.Id
            ?? accessOptions.Locations[0].Id;

        long? temporaryRoleId = null;
        if (activeRoles.Count < 2)
        {
            var roleStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var saveRoleResult = await service.SaveRoleAsync(
                new ManagedRole
                {
                    Id = 0,
                    Code = $"ITEST_ROLE_{roleStamp}",
                    Name = $"Integration Test Role {roleStamp}",
                    IsSuperRole = false,
                    IsActive = true
                },
                Array.Empty<long>(),
                "admin");

            Assert(saveRoleResult.IsSuccess && saveRoleResult.EntityId.HasValue, "Failed to create temporary role for test.");
            temporaryRoleId = saveRoleResult.EntityId!.Value;
            activeRoles.Add(new ManagedRole
            {
                Id = temporaryRoleId.Value,
                Code = $"ITEST_ROLE_{roleStamp}",
                Name = $"Integration Test Role {roleStamp}",
                IsActive = true
            });
        }

        var primaryRoleId = activeRoles[0].Id;
        var secondaryRoleId = activeRoles[1].Id;
        var userStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var username = $"itest_single_role_{userStamp}";
        long? createdUserId = null;

        try
        {
            var user = new ManagedUser
            {
                Id = 0,
                Username = username,
                FullName = "Integration Test User",
                Email = $"{username}@local",
                IsActive = true,
                DefaultCompanyId = defaultCompanyId,
                DefaultLocationId = defaultLocationId
            };

            var multiRoleResult = await service.SaveUserAsync(
                user,
                "Admin@123",
                new[] { primaryRoleId, secondaryRoleId },
                new[] { defaultCompanyId },
                new[] { defaultLocationId },
                "admin");

            Assert(!multiRoleResult.IsSuccess, "SaveUser should fail when multiple roles are provided.");
            Assert(
                multiRoleResult.Message.Contains("one role", StringComparison.OrdinalIgnoreCase),
                "Expected single-role validation message.");

            var singleRoleResult = await service.SaveUserAsync(
                user,
                "Admin@123",
                new[] { primaryRoleId },
                new[] { defaultCompanyId },
                new[] { defaultLocationId },
                "admin");

            Assert(singleRoleResult.IsSuccess, "SaveUser should succeed with single role.");
            Assert(singleRoleResult.EntityId.HasValue && singleRoleResult.EntityId.Value > 0, "Created user id must be returned.");
            createdUserId = singleRoleResult.EntityId!.Value;

            await using var connection = await OpenConnectionAsync();
            await using var roleCountCommand = new NpgsqlCommand(
                "SELECT COUNT(1) FROM sec_user_roles WHERE user_id = @user_id;",
                connection);
            roleCountCommand.Parameters.AddWithValue("user_id", createdUserId.Value);
            var roleCount = Convert.ToInt32(await roleCountCommand.ExecuteScalarAsync());

            Assert(roleCount == 1, $"Expected exactly one role row for user, got {roleCount}.");
        }
        finally
        {
            if (createdUserId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();

                await using (var deleteRoles = new NpgsqlCommand(
                    "DELETE FROM sec_user_roles WHERE user_id = @user_id;",
                    connection))
                {
                    deleteRoles.Parameters.AddWithValue("user_id", createdUserId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand(
                    "DELETE FROM app_users WHERE id = @user_id;",
                    connection))
                {
                    deleteUser.Parameters.AddWithValue("user_id", createdUserId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }

                await using (var deleteAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'USER' AND details LIKE @details;",
                    connection))
                {
                    deleteAudit.Parameters.AddWithValue("details", $"username={username};%");
                    await deleteAudit.ExecuteNonQueryAsync();
                }
            }

            if (temporaryRoleId.HasValue)
            {
                await service.DeleteRoleAsync(temporaryRoleId.Value, "admin");
            }
        }
    }

    private static async Task TestJournalReadApisRespectScopeAccessAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        var managementData = await service.GetUserManagementDataAsync();

        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var targetCompanyId = accessOptions.Companies[0].Id;
        var targetLocationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == targetCompanyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var previousTargetPeriod = await GetAccountingPeriodStateAsync(targetCompanyId, targetLocationId, targetMonth);

        var transactionViewScopeId = RequireAccessScopeId(managementData, "accounting", "transactions", "view");
        var reportViewScopeId = RequireAccessScopeId(managementData, "accounting", "reports", "view");

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var roleCode = $"ITEST_GL_READ_{stamp}";
        var username = $"itest_gl_read_{stamp}";
        var companyCode = $"ITGLR{stamp % 100000:00000}";
        var locationCode = $"L{stamp % 100000:00000}";

        long? scopedRoleId = null;
        long? scopedUserId = null;
        long? scopedCompanyId = null;
        long? scopedLocationId = null;

        try
        {
            await SetAccountingPeriodStateAsync(targetCompanyId, targetLocationId, targetMonth, isOpen: true, note: "ITEST_SCOPE_READ_TARGET");

            var adminWorkspace = await service.GetJournalWorkspaceDataAsync(targetCompanyId, targetLocationId, "admin");
            var adminAccounts = await service.GetAccountsAsync(targetCompanyId, includeInactive: false, actorUsername: "admin");
            var adminPeriods = await service.GetAccountingPeriodsAsync(targetCompanyId, targetLocationId, "admin");

            Assert(adminWorkspace.Accounts.Count > 0, "Target scope should expose journal workspace data to admin.");
            Assert(adminAccounts.Count > 0, "Target scope should expose accounts to admin.");
            Assert(adminPeriods.Count > 0, "Target scope should expose accounting periods to admin.");

            var createCompanyResult = await service.SaveCompanyAsync(
                new ManagedCompany
                {
                    Id = 0,
                    Code = companyCode,
                    Name = $"GL Read Scope Company {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createCompanyResult.IsSuccess && createCompanyResult.EntityId.HasValue,
                $"Failed to create scoped company: {createCompanyResult.Message}");
            scopedCompanyId = createCompanyResult.EntityId!.Value;

            var createLocationResult = await service.SaveLocationAsync(
                new ManagedLocation
                {
                    Id = 0,
                    CompanyId = scopedCompanyId.Value,
                    Code = locationCode,
                    Name = $"GL Read Scope Location {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createLocationResult.IsSuccess && createLocationResult.EntityId.HasValue,
                $"Failed to create scoped location: {createLocationResult.Message}");
            scopedLocationId = createLocationResult.EntityId!.Value;

            var roleSaveResult = await service.SaveRoleAsync(
                new ManagedRole
                {
                    Id = 0,
                    Code = roleCode,
                    Name = "GL Read Scope Role",
                    IsSuperRole = false,
                    IsActive = true
                },
                [transactionViewScopeId, reportViewScopeId],
                "admin");
            Assert(roleSaveResult.IsSuccess && roleSaveResult.EntityId.HasValue, "Failed to create GL read scope role.");
            scopedRoleId = roleSaveResult.EntityId!.Value;

            var userSaveResult = await service.SaveUserAsync(
                new ManagedUser
                {
                    Id = 0,
                    Username = username,
                    FullName = "GL Read Scope User",
                    Email = $"{username}@local",
                    IsActive = true,
                    DefaultCompanyId = scopedCompanyId.Value,
                    DefaultLocationId = scopedLocationId.Value
                },
                "Admin@123",
                [scopedRoleId.Value],
                [scopedCompanyId.Value],
                [scopedLocationId.Value],
                "admin");
            Assert(userSaveResult.IsSuccess && userSaveResult.EntityId.HasValue, "Failed to create GL read scope user.");
            scopedUserId = userSaveResult.EntityId!.Value;

            var unauthorizedWorkspace = await service.GetJournalWorkspaceDataAsync(targetCompanyId, targetLocationId, username);
            var unauthorizedJournals = await service.SearchJournalsAsync(
                targetCompanyId,
                targetLocationId,
                new JournalSearchFilter { PeriodMonth = targetMonth },
                actorUsername: username);
            var unauthorizedTrialBalance = await service.GetTrialBalanceAsync(targetCompanyId, targetLocationId, targetMonth, username);
            var unauthorizedAccounts = await service.GetAccountsAsync(targetCompanyId, includeInactive: false, actorUsername: username);
            var unauthorizedPeriods = await service.GetAccountingPeriodsAsync(targetCompanyId, targetLocationId, username);

            Assert(unauthorizedWorkspace.Accounts.Count == 0, "Out-of-scope actor should not receive workspace accounts.");
            Assert(unauthorizedWorkspace.Journals.Count == 0, "Out-of-scope actor should not receive workspace journals.");
            Assert(unauthorizedJournals.Count == 0, "Out-of-scope actor should not receive journal search results.");
            Assert(unauthorizedTrialBalance.Count == 0, "Out-of-scope actor should not receive report data.");
            Assert(unauthorizedAccounts.Count == 0, "Out-of-scope actor should not receive account master data.");
            Assert(unauthorizedPeriods.Count == 0, "Out-of-scope actor should not receive accounting periods.");
        }
        finally
        {
            if (scopedUserId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();
                await using (var deleteRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @id;", connection))
                {
                    deleteRoles.Parameters.AddWithValue("id", scopedUserId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand("DELETE FROM app_users WHERE id = @id;", connection))
                {
                    deleteUser.Parameters.AddWithValue("id", scopedUserId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }
            }

            if (scopedRoleId.HasValue)
            {
                await service.DeleteRoleAsync(scopedRoleId.Value, "admin");
            }

            if (scopedCompanyId.HasValue)
            {
                await CleanupTemporaryInventoryCostingCompanyAsync(scopedCompanyId.Value);
            }

            if (previousTargetPeriod.Exists)
            {
                await SetAccountingPeriodStateAsync(targetCompanyId, targetLocationId, targetMonth, previousTargetPeriod.IsOpen, "ITEST_RESTORE_SCOPE_READ");
            }
        }
    }

    private static async Task TestJournalDraftPostFlowAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;

        var workspace = await service.GetJournalWorkspaceDataAsync(companyId, locationId, "admin");
        Assert(workspace.Accounts.Count >= 2, "At least two active accounts are required for journal test.");
        await SetAccountingPeriodStateAsync(companyId, locationId, DateTime.Today, isOpen: true, note: "ITEST_OPEN");

        var debitAccount = workspace.Accounts[0];
        var creditAccount = workspace.Accounts[1];
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var journalNo = $"ITEST-JRN-{stamp}";

        var header = new ManagedJournalHeader
        {
            Id = 0,
            CompanyId = companyId,
            LocationId = locationId,
            JournalNo = journalNo,
            JournalDate = DateTime.Today,
            ReferenceNo = "ITEST",
            Description = "Integration Test Journal"
        };

        var lines = new[]
        {
            new ManagedJournalLine
            {
                LineNo = 1,
                AccountCode = debitAccount.Code,
                Description = "Debit line",
                Debit = 100000m,
                Credit = 0m,
                DepartmentCode = "FIN",
                ProjectCode = "PRJ01",
                CostCenterCode = "CC01"
            },
            new ManagedJournalLine
            {
                LineNo = 2,
                AccountCode = creditAccount.Code,
                Description = "Credit line",
                Debit = 0m,
                Credit = 100000m,
                DepartmentCode = "FIN",
                ProjectCode = "PRJ01",
                CostCenterCode = "CC01"
            }
        };

        long? journalId = null;
        try
        {
            var saveResult = await service.SaveJournalDraftAsync(header, lines, "admin");
            Assert(saveResult.IsSuccess, $"SaveJournalDraft failed: {saveResult.Message}");
            Assert(saveResult.EntityId.HasValue && saveResult.EntityId.Value > 0, "Journal id must be returned.");
            journalId = saveResult.EntityId!.Value;

            var bundle = await service.GetJournalBundleAsync(journalId.Value, companyId, locationId, "admin");
            Assert(bundle is not null, "Saved journal bundle should be loadable.");
            Assert(string.Equals(bundle!.Header.Status, "DRAFT", StringComparison.OrdinalIgnoreCase), "Saved journal status must be DRAFT.");
            Assert(bundle.Lines.Count == 2, $"Expected 2 detail lines, got {bundle.Lines.Count}.");

            var postBeforeApprove = await service.PostJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(!postBeforeApprove.IsSuccess, "Post should fail before approval step.");

            var submitResult = await service.SubmitJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(submitResult.IsSuccess, $"SubmitJournal failed: {submitResult.Message}");

            var approveResult = await service.ApproveJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(approveResult.IsSuccess, $"ApproveJournal failed: {approveResult.Message}");

            var postResult = await service.PostJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(postResult.IsSuccess, $"PostJournal failed: {postResult.Message}");

            await using (var connection = await OpenConnectionAsync())
            {
                await using var ledgerCommand = new NpgsqlCommand(
                    @"SELECT COUNT(1),
       COALESCE(SUM(debit), 0),
       COALESCE(SUM(credit), 0)
FROM gl_ledger_entries
WHERE journal_id = @journal_id;",
                    connection);
                ledgerCommand.Parameters.AddWithValue("journal_id", journalId.Value);

                await using var reader = await ledgerCommand.ExecuteReaderAsync();
                Assert(await reader.ReadAsync(), "Ledger query should return one row.");
                var ledgerRowCount = reader.GetInt64(0);
                var ledgerDebit = reader.GetDecimal(1);
                var ledgerCredit = reader.GetDecimal(2);

                Assert(ledgerRowCount == 2, $"Expected 2 ledger rows, got {ledgerRowCount}.");
                Assert(ledgerDebit == 100000m, $"Expected ledger debit 100000, got {ledgerDebit}.");
                Assert(ledgerCredit == 100000m, $"Expected ledger credit 100000, got {ledgerCredit}.");
            }

            var trialBalance = await service.GetTrialBalanceAsync(companyId, locationId, DateTime.Today, "admin");
            var debitRow = trialBalance.FirstOrDefault(x => x.AccountCode == debitAccount.Code);
            var creditRow = trialBalance.FirstOrDefault(x => x.AccountCode == creditAccount.Code);
            Assert(debitRow is not null, $"Trial balance should include account {debitAccount.Code}.");
            Assert(creditRow is not null, $"Trial balance should include account {creditAccount.Code}.");
            Assert(debitRow!.TotalDebit >= 100000m, "Trial balance debit account should include posted amount.");
            Assert(creditRow!.TotalCredit >= 100000m, "Trial balance credit account should include posted amount.");

            var profitLoss = await service.GetProfitLossAsync(companyId, locationId, DateTime.Today, "admin");
            Assert(profitLoss is not null, "Profit/loss result should not be null.");

            var balanceSheet = await service.GetBalanceSheetAsync(companyId, locationId, DateTime.Today, "admin") ?? new List<ManagedBalanceSheetRow>();
            Assert(balanceSheet is not null, "Balance sheet result should not be null.");

            var debitPrefix = debitAccount.Code.Length > 0 ? debitAccount.Code[0] : '0';
            if (debitPrefix is '1' or '2' or '3')
            {
                Assert(
                    (balanceSheet ?? new List<ManagedBalanceSheetRow>()).Any(x => x.AccountCode == debitAccount.Code),
                    $"Balance sheet should include account {debitAccount.Code}.");
            }

            var updateAfterPostResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    Id = journalId.Value,
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = journalNo,
                    JournalDate = DateTime.Today,
                    ReferenceNo = "ITEST-UPDATED",
                    Description = "Should fail"
                },
                lines,
                "admin");

            Assert(!updateAfterPostResult.IsSuccess, "Updating posted journal should fail.");
            Assert(
                updateAfterPostResult.Message.Contains("draft", StringComparison.OrdinalIgnoreCase),
                "Expected draft-lock message when updating posted journal.");
        }
        finally
        {
            if (journalId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();
                await using var deleteCommand = new NpgsqlCommand(
                    "DELETE FROM gl_journal_headers WHERE id = @id;",
                    connection);
                deleteCommand.Parameters.AddWithValue("id", journalId.Value);
                await deleteCommand.ExecuteNonQueryAsync();

                await using var deleteAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'JOURNAL' AND entity_id = @id;",
                    connection);
                deleteAudit.Parameters.AddWithValue("id", journalId.Value);
                await deleteAudit.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestJournalRejectApprovePostWithoutApproveScopeAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        var managementData = await service.GetUserManagementDataAsync();
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;

        var workspace = await service.GetJournalWorkspaceDataAsync(companyId, locationId, "admin");
        Assert(workspace.Accounts.Count >= 2, "At least two active accounts are required for journal test.");
        await SetAccountingPeriodStateAsync(companyId, locationId, DateTime.Today, isOpen: true, note: "ITEST_OPEN_APPROVE_MODULE");

        var roleStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var roleCode = $"ITEST_NOAPPROVE_{roleStamp}";
        var username = $"itest_noapprove_{roleStamp}";
        var allowedScopeIds = managementData.AccessScopes
            .Where(scope =>
                string.Equals(scope.ModuleCode, "accounting", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(scope.SubmoduleCode, "transactions", StringComparison.OrdinalIgnoreCase) &&
                (
                    string.Equals(scope.ActionCode, "create", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scope.ActionCode, "update", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scope.ActionCode, "submit", StringComparison.OrdinalIgnoreCase)))
            .Select(scope => scope.Id)
            .ToArray();
        Assert(allowedScopeIds.Length == 3, "Expected create/update/submit scopes for journal permission test.");

        long? roleId = null;
        long? userId = null;
        long? journalId = null;
        var journalNo = $"ITEST-NO-APPROVE-{roleStamp}";

        var debitAccount = workspace.Accounts[0];
        var creditAccount = workspace.Accounts[1];
        var lines = new[]
        {
            new ManagedJournalLine
            {
                LineNo = 1,
                AccountCode = debitAccount.Code,
                Description = "Debit line",
                Debit = 150000m,
                Credit = 0m,
                DepartmentCode = "FIN",
                ProjectCode = "PRJ01",
                CostCenterCode = "CC01"
            },
            new ManagedJournalLine
            {
                LineNo = 2,
                AccountCode = creditAccount.Code,
                Description = "Credit line",
                Debit = 0m,
                Credit = 150000m,
                DepartmentCode = "FIN",
                ProjectCode = "PRJ01",
                CostCenterCode = "CC01"
            }
        };

        try
        {
            var roleSaveResult = await service.SaveRoleAsync(
                new ManagedRole
                {
                    Id = 0,
                    Code = roleCode,
                    Name = "No Approve Scope Role",
                    IsSuperRole = false,
                    IsActive = true
                },
                allowedScopeIds,
                "admin");
            Assert(roleSaveResult.IsSuccess && roleSaveResult.EntityId.HasValue, "Failed to create role without approve scope.");
            roleId = roleSaveResult.EntityId!.Value;

            var userSaveResult = await service.SaveUserAsync(
                new ManagedUser
                {
                    Id = 0,
                    Username = username,
                    FullName = "No Approve Scope User",
                    Email = $"{username}@local",
                    IsActive = true,
                    DefaultCompanyId = companyId,
                    DefaultLocationId = locationId
                },
                "Admin@123",
                new[] { roleId.Value },
                new[] { companyId },
                new[] { locationId },
                "admin");
            Assert(userSaveResult.IsSuccess && userSaveResult.EntityId.HasValue, "Failed to create user without approve scope.");
            userId = userSaveResult.EntityId!.Value;

            var saveResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    Id = 0,
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = journalNo,
                    JournalDate = DateTime.Today,
                    ReferenceNo = "ITEST",
                    Description = "No Approve Scope"
                },
                lines,
                username);
            Assert(saveResult.IsSuccess && saveResult.EntityId.HasValue, $"SaveJournalDraft failed: {saveResult.Message}");
            journalId = saveResult.EntityId!.Value;

            var submitResult = await service.SubmitJournalAsync(journalId.Value, companyId, locationId, username);
            Assert(submitResult.IsSuccess, $"SubmitJournal failed: {submitResult.Message}");

            var approveResult = await service.ApproveJournalAsync(journalId.Value, companyId, locationId, username);
            Assert(!approveResult.IsSuccess, "Approve should fail when actor has no approve scope.");
            Assert(
                approveResult.Message.Contains("izin", StringComparison.OrdinalIgnoreCase),
                "Expected approve unauthorized message containing izin.");

            var postResult = await service.PostJournalAsync(journalId.Value, companyId, locationId, username);
            Assert(!postResult.IsSuccess, "Post should fail when actor has no approve scope.");
            Assert(
                postResult.Message.Contains("izin", StringComparison.OrdinalIgnoreCase),
                "Expected post unauthorized message containing izin.");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (journalId.HasValue)
            {
                await using (var deleteJournal = new NpgsqlCommand(
                    "DELETE FROM gl_journal_headers WHERE id = @id;",
                    connection))
                {
                    deleteJournal.Parameters.AddWithValue("id", journalId.Value);
                    await deleteJournal.ExecuteNonQueryAsync();
                }

                await using (var deleteAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'JOURNAL' AND entity_id = @id;",
                    connection))
                {
                    deleteAudit.Parameters.AddWithValue("id", journalId.Value);
                    await deleteAudit.ExecuteNonQueryAsync();
                }
            }

            if (userId.HasValue)
            {
                await using (var deleteRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @id;", connection))
                {
                    deleteRoles.Parameters.AddWithValue("id", userId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand("DELETE FROM app_users WHERE id = @id;", connection))
                {
                    deleteUser.Parameters.AddWithValue("id", userId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }
            }

            if (roleId.HasValue)
            {
                await service.DeleteRoleAsync(roleId.Value, "admin");
            }
        }
    }

    private static async Task TestJournalRejectWhenPeriodClosedAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var journalDate = DateTime.Today;
        var periodMonth = new DateTime(journalDate.Year, journalDate.Month, 1);

        var workspace = await service.GetJournalWorkspaceDataAsync(companyId, locationId, "admin");
        Assert(workspace.Accounts.Count >= 2, "At least two active accounts are required for journal test.");

        var debitAccount = workspace.Accounts[0];
        var creditAccount = workspace.Accounts[1];
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new ManagedJournalHeader
        {
            Id = 0,
            CompanyId = companyId,
            LocationId = locationId,
            JournalNo = $"ITEST-CLOSE-{stamp}",
            JournalDate = journalDate,
            ReferenceNo = "ITEST-CLOSE",
            Description = "Period Closed Validation"
        };

        var lines = new[]
        {
            new ManagedJournalLine
            {
                LineNo = 1,
                AccountCode = debitAccount.Code,
                Description = "Debit line",
                Debit = 50000m,
                Credit = 0m
            },
            new ManagedJournalLine
            {
                LineNo = 2,
                AccountCode = creditAccount.Code,
                Description = "Credit line",
                Debit = 0m,
                Credit = 50000m
            }
        };

        var previous = await GetAccountingPeriodStateAsync(companyId, locationId, periodMonth);

        try
        {
            await SetAccountingPeriodStateAsync(companyId, locationId, periodMonth, isOpen: false, note: "ITEST_CLOSED");

            var saveResult = await service.SaveJournalDraftAsync(header, lines, "admin");
            Assert(!saveResult.IsSuccess, "SaveJournalDraft should fail when accounting period is closed.");
            Assert(
                saveResult.Message.Contains("Periode", StringComparison.OrdinalIgnoreCase),
                "Expected accounting period closed validation message.");
        }
        finally
        {
            if (previous.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, periodMonth, previous.IsOpen, "ITEST_RESTORE");
            }
            else
            {
                await using var connection = await OpenConnectionAsync();
                await using var deleteCommand = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deleteCommand.Parameters.AddWithValue("company_id", companyId);
                deleteCommand.Parameters.AddWithValue("location_id", locationId);
                deleteCommand.Parameters.AddWithValue("period_month", periodMonth);
                await deleteCommand.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestJournalSubmitApproveRejectWhenPeriodClosedAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var previousPeriod = await GetAccountingPeriodStateAsync(companyId, locationId, targetMonth);
        var workspace = await service.GetJournalWorkspaceDataAsync(companyId, locationId, "admin");
        Assert(workspace.Accounts.Count >= 2, "At least two active accounts are required for submit/approve close test.");

        var debitAccount = workspace.Accounts[0];
        var creditAccount = workspace.Accounts[1];
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var journalNo = $"ITEST-SUBMIT-CLOSED-{stamp}";
        long? journalId = null;

        try
        {
            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: true, note: "ITEST_SUBMIT_APPROVE_OPEN");

            var saveResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    Id = 0,
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = journalNo,
                    JournalDate = targetMonth.AddDays(1),
                    ReferenceNo = "ITEST-SUBMIT",
                    Description = "Submit/approve closed period regression"
                },
                [
                    new ManagedJournalLine
                    {
                        LineNo = 1,
                        AccountCode = debitAccount.Code,
                        Description = "Debit line",
                        Debit = 2500m,
                        Credit = 0m
                    },
                    new ManagedJournalLine
                    {
                        LineNo = 2,
                        AccountCode = creditAccount.Code,
                        Description = "Credit line",
                        Debit = 0m,
                        Credit = 2500m
                    }
                ],
                "admin");
            Assert(saveResult.IsSuccess && saveResult.EntityId.HasValue, $"Failed to save journal draft: {saveResult.Message}");
            journalId = saveResult.EntityId!.Value;

            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: false, note: "ITEST_SUBMIT_APPROVE_CLOSED");

            var blockedSubmit = await service.SubmitJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(!blockedSubmit.IsSuccess, "Submit should be rejected when the accounting period is closed.");
            Assert(
                blockedSubmit.Message.Contains("periode", StringComparison.OrdinalIgnoreCase),
                $"Expected closed-period submit message, got: {blockedSubmit.Message}");

            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: true, note: "ITEST_SUBMIT_APPROVE_REOPEN");

            var submitResult = await service.SubmitJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(submitResult.IsSuccess, $"Submit should succeed after reopening period: {submitResult.Message}");

            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: false, note: "ITEST_APPROVE_CLOSED");

            var blockedApprove = await service.ApproveJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(!blockedApprove.IsSuccess, "Approve should be rejected when the accounting period is closed.");
            Assert(
                blockedApprove.Message.Contains("periode", StringComparison.OrdinalIgnoreCase),
                $"Expected closed-period approve message, got: {blockedApprove.Message}");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (journalId.HasValue)
            {
                await using (var deleteJournalAudit = new NpgsqlCommand(
                    @"DELETE FROM sec_audit_logs
WHERE entity_type = 'JOURNAL'
  AND (entity_id = @entity_id OR details ILIKE @journal_no);",
                    connection))
                {
                    deleteJournalAudit.Parameters.AddWithValue("entity_id", journalId.Value);
                    deleteJournalAudit.Parameters.AddWithValue("journal_no", $"%{journalNo}%");
                    await deleteJournalAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteJournal = new NpgsqlCommand(
                    "DELETE FROM gl_journal_headers WHERE id = @id;",
                    connection))
                {
                    deleteJournal.Parameters.AddWithValue("id", journalId.Value);
                    await deleteJournal.ExecuteNonQueryAsync();
                }
            }

            if (previousPeriod.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, previousPeriod.IsOpen, "ITEST_RESTORE_SUBMIT_APPROVE");
            }
            else
            {
                await using var deletePeriod = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deletePeriod.Parameters.AddWithValue("company_id", companyId);
                deletePeriod.Parameters.AddWithValue("location_id", locationId);
                deletePeriod.Parameters.AddWithValue("period_month", targetMonth);
                await deletePeriod.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestJournalAllowsSameNumberAcrossDifferentPeriodsAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;

        var currentMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var nextMonth = currentMonth.AddMonths(1);
        var previousCurrent = await GetAccountingPeriodStateAsync(companyId, locationId, currentMonth);
        var previousNext = await GetAccountingPeriodStateAsync(companyId, locationId, nextMonth);

        var workspace = await service.GetJournalWorkspaceDataAsync(companyId, locationId, "admin");
        Assert(workspace.Accounts.Count >= 2, "At least two active accounts are required for journal test.");

        var debitAccount = workspace.Accounts[0];
        var creditAccount = workspace.Accounts[1];
        var sharedJournalNo = $"ITEST-PERIOD-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var lines = new[]
        {
            new ManagedJournalLine
            {
                LineNo = 1,
                AccountCode = debitAccount.Code,
                Description = "Debit line",
                Debit = 25000m,
                Credit = 0m
            },
            new ManagedJournalLine
            {
                LineNo = 2,
                AccountCode = creditAccount.Code,
                Description = "Credit line",
                Debit = 0m,
                Credit = 25000m
            }
        };

        long? currentJournalId = null;
        long? nextJournalId = null;

        try
        {
            await SetAccountingPeriodStateAsync(companyId, locationId, currentMonth, isOpen: true, note: "ITEST_OPEN_CURRENT");
            await SetAccountingPeriodStateAsync(companyId, locationId, nextMonth, isOpen: true, note: "ITEST_OPEN_NEXT");

            var currentResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = sharedJournalNo,
                    JournalDate = currentMonth.AddDays(1),
                    PeriodMonth = currentMonth,
                    ReferenceNo = "ITEST-CURRENT",
                    Description = "Duplicate number different period current"
                },
                lines,
                "admin");

            Assert(currentResult.IsSuccess, $"Current-period journal save should succeed: {currentResult.Message}");
            currentJournalId = currentResult.EntityId;

            var nextResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = sharedJournalNo,
                    JournalDate = nextMonth.AddDays(1),
                    PeriodMonth = nextMonth,
                    ReferenceNo = "ITEST-NEXT",
                    Description = "Duplicate number different period next"
                },
                lines,
                "admin");

            Assert(nextResult.IsSuccess, $"Next-period journal with same number should succeed: {nextResult.Message}");
            nextJournalId = nextResult.EntityId;
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (currentJournalId.HasValue || nextJournalId.HasValue)
            {
                var journalIds = new List<long>();
                if (currentJournalId.HasValue)
                {
                    journalIds.Add(currentJournalId.Value);
                }

                if (nextJournalId.HasValue)
                {
                    journalIds.Add(nextJournalId.Value);
                }

                foreach (var journalId in journalIds)
                {
                    await using (var deleteAudit = new NpgsqlCommand(
                        "DELETE FROM sec_audit_logs WHERE entity_type = 'JOURNAL' AND entity_id = @journal_id;",
                        connection))
                    {
                        deleteAudit.Parameters.AddWithValue("journal_id", journalId);
                        await deleteAudit.ExecuteNonQueryAsync();
                    }

                    await using (var deleteHeader = new NpgsqlCommand(
                        "DELETE FROM gl_journal_headers WHERE id = @journal_id;",
                        connection))
                    {
                        deleteHeader.Parameters.AddWithValue("journal_id", journalId);
                        await deleteHeader.ExecuteNonQueryAsync();
                    }
                }
            }

            if (previousCurrent.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, currentMonth, previousCurrent.IsOpen, "ITEST_RESTORE_CURRENT");
            }

            if (previousNext.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, nextMonth, previousNext.IsOpen, "ITEST_RESTORE_NEXT");
            }
            else
            {
                await using var deleteNextPeriod = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deleteNextPeriod.Parameters.AddWithValue("company_id", companyId);
                deleteNextPeriod.Parameters.AddWithValue("location_id", locationId);
                deleteNextPeriod.Parameters.AddWithValue("period_month", nextMonth);
                await deleteNextPeriod.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestAccountingPeriodOpenCloseApiAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);

        var nextMonth = targetMonth.AddMonths(1);
        var previous = await GetAccountingPeriodStateAsync(companyId, locationId, targetMonth);
        var previousNext = await GetAccountingPeriodStateAsync(companyId, locationId, nextMonth);
        try
        {
            var closeResult = await service.SetAccountingPeriodOpenStateAsync(
                companyId,
                locationId,
                targetMonth,
                isOpen: false,
                actorUsername: "admin",
                note: "ITEST_CLOSE_API");
            Assert(closeResult.IsSuccess, $"Close period API failed: {closeResult.Message}");

            var periodsAfterClose = await service.GetAccountingPeriodsAsync(companyId, locationId, "admin");
            var closed = periodsAfterClose.FirstOrDefault(x => x.PeriodMonth.Date == targetMonth.Date);
            Assert(closed is not null, "Closed period row should exist.");
            Assert(!closed!.IsOpen, "Period should be closed after close API.");
            var nextPeriod = periodsAfterClose.FirstOrDefault(x => x.PeriodMonth.Date == nextMonth.Date);
            Assert(nextPeriod is not null, "Next period row should exist after closing current period.");
            if (previousNext.Exists)
            {
                Assert(nextPeriod!.IsOpen == previousNext.IsOpen, "Existing next period state should be preserved.");
            }
            else
            {
                Assert(nextPeriod!.IsOpen, "Auto-created next period should be open.");
            }

            var openResult = await service.SetAccountingPeriodOpenStateAsync(
                companyId,
                locationId,
                targetMonth,
                isOpen: true,
                actorUsername: "admin",
                note: "ITEST_OPEN_API");
            Assert(openResult.IsSuccess, $"Open period API failed: {openResult.Message}");

            var periodsAfterOpen = await service.GetAccountingPeriodsAsync(companyId, locationId, "admin");
            var opened = periodsAfterOpen.FirstOrDefault(x => x.PeriodMonth.Date == targetMonth.Date);
            Assert(opened is not null, "Opened period row should exist.");
            Assert(opened!.IsOpen, "Period should be open after open API.");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (previous.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, previous.IsOpen, "ITEST_RESTORE");
            }
            else
            {
                await using var deleteCommand = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deleteCommand.Parameters.AddWithValue("company_id", companyId);
                deleteCommand.Parameters.AddWithValue("location_id", locationId);
                deleteCommand.Parameters.AddWithValue("period_month", targetMonth);
                await deleteCommand.ExecuteNonQueryAsync();
            }

            if (previousNext.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, nextMonth, previousNext.IsOpen, "ITEST_RESTORE_NEXT");
            }
            else
            {
                await using var deleteNextCommand = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deleteNextCommand.Parameters.AddWithValue("company_id", companyId);
                deleteNextCommand.Parameters.AddWithValue("location_id", locationId);
                deleteNextCommand.Parameters.AddWithValue("period_month", nextMonth);
                await deleteNextCommand.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestAccountingPeriodCloseCreatesClosingEntryAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(18);
        var nextMonth = targetMonth.AddMonths(1);
        var previous = await GetAccountingPeriodStateAsync(companyId, locationId, targetMonth);
        var previousNext = await GetAccountingPeriodStateAsync(companyId, locationId, nextMonth);

        var workspace = await service.GetJournalWorkspaceDataAsync(companyId, locationId, "admin");
        var accounts = workspace.Accounts;
        var asset = accounts.FirstOrDefault(x => string.Equals(x.AccountType, "ASSET", StringComparison.OrdinalIgnoreCase));
        var revenue = accounts.FirstOrDefault(x => string.Equals(x.AccountType, "REVENUE", StringComparison.OrdinalIgnoreCase));
        var expense = accounts.FirstOrDefault(x => string.Equals(x.AccountType, "EXPENSE", StringComparison.OrdinalIgnoreCase));
        Assert(asset is not null, "Asset account is required for close-period test.");
        Assert(revenue is not null, "Revenue account is required for close-period test.");
        Assert(expense is not null, "Expense account is required for close-period test.");

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var revenueJournalNo = $"ITEST-CLOSEFLOW-R-{stamp}";
        var expenseJournalNo = $"ITEST-CLOSEFLOW-E-{stamp}";
        var closingJournalNo = $"CLS-{targetMonth:yyyyMM}-{companyId}-{locationId}";
        long? revenueJournalId = null;
        long? expenseJournalId = null;

        try
        {
            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: true, note: "ITEST_CLOSEFLOW_OPEN");

            var revenueSave = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    Id = 0,
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = revenueJournalNo,
                    JournalDate = targetMonth.AddDays(5),
                    ReferenceNo = "ITEST-CLOSEFLOW",
                    Description = "Revenue Journal for Closing"
                },
                new[]
                {
                    new ManagedJournalLine
                    {
                        LineNo = 1,
                        AccountCode = asset!.Code,
                        Description = "Revenue cash in",
                        Debit = 1000m,
                        Credit = 0m
                    },
                    new ManagedJournalLine
                    {
                        LineNo = 2,
                        AccountCode = revenue!.Code,
                        Description = "Revenue recognition",
                        Debit = 0m,
                        Credit = 1000m
                    }
                },
                "admin");
            Assert(revenueSave.IsSuccess && revenueSave.EntityId.HasValue, $"Failed to save revenue journal: {revenueSave.Message}");
            revenueJournalId = revenueSave.EntityId!.Value;

            var revenueSubmit = await service.SubmitJournalAsync(revenueJournalId.Value, companyId, locationId, "admin");
            Assert(revenueSubmit.IsSuccess, $"Failed to submit revenue journal: {revenueSubmit.Message}");
            var revenueApprove = await service.ApproveJournalAsync(revenueJournalId.Value, companyId, locationId, "admin");
            Assert(revenueApprove.IsSuccess, $"Failed to approve revenue journal: {revenueApprove.Message}");
            var revenuePost = await service.PostJournalAsync(revenueJournalId.Value, companyId, locationId, "admin");
            Assert(revenuePost.IsSuccess, $"Failed to post revenue journal: {revenuePost.Message}");

            var expenseSave = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    Id = 0,
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = expenseJournalNo,
                    JournalDate = targetMonth.AddDays(6),
                    ReferenceNo = "ITEST-CLOSEFLOW",
                    Description = "Expense Journal for Closing"
                },
                new[]
                {
                    new ManagedJournalLine
                    {
                        LineNo = 1,
                        AccountCode = expense!.Code,
                        Description = "Expense recognition",
                        Debit = 300m,
                        Credit = 0m
                    },
                    new ManagedJournalLine
                    {
                        LineNo = 2,
                        AccountCode = asset!.Code,
                        Description = "Expense cash out",
                        Debit = 0m,
                        Credit = 300m
                    }
                },
                "admin");
            Assert(expenseSave.IsSuccess && expenseSave.EntityId.HasValue, $"Failed to save expense journal: {expenseSave.Message}");
            expenseJournalId = expenseSave.EntityId!.Value;

            var expenseSubmit = await service.SubmitJournalAsync(expenseJournalId.Value, companyId, locationId, "admin");
            Assert(expenseSubmit.IsSuccess, $"Failed to submit expense journal: {expenseSubmit.Message}");
            var expenseApprove = await service.ApproveJournalAsync(expenseJournalId.Value, companyId, locationId, "admin");
            Assert(expenseApprove.IsSuccess, $"Failed to approve expense journal: {expenseApprove.Message}");
            var expensePost = await service.PostJournalAsync(expenseJournalId.Value, companyId, locationId, "admin");
            Assert(expensePost.IsSuccess, $"Failed to post expense journal: {expensePost.Message}");

            var closeResult = await service.SetAccountingPeriodOpenStateAsync(
                companyId,
                locationId,
                targetMonth,
                isOpen: false,
                actorUsername: "admin",
                note: "ITEST_CLOSEFLOW_CLOSE");
            Assert(closeResult.IsSuccess, $"Close period with closing journal failed: {closeResult.Message}");
            Assert(closeResult.Message.Contains(nextMonth.ToString("yyyy-MM"), StringComparison.Ordinal), "Close message should mention next accounting period.");

            await using var connection = await OpenConnectionAsync();
            await using (var closingHeaderCommand = new NpgsqlCommand(
                @"SELECT COUNT(1)
FROM gl_journal_headers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND journal_no = @journal_no
  AND status = 'POSTED';",
                connection))
            {
                closingHeaderCommand.Parameters.AddWithValue("company_id", companyId);
                closingHeaderCommand.Parameters.AddWithValue("location_id", locationId);
                closingHeaderCommand.Parameters.AddWithValue("journal_no", closingJournalNo);
                var closingHeaderCount = Convert.ToInt32(await closingHeaderCommand.ExecuteScalarAsync());
                Assert(closingHeaderCount == 1, "Closing journal should be created and posted exactly once.");
            }

            await using (var closingCheck = new NpgsqlCommand(
                @"SELECT COALESCE(SUM(CASE WHEN upper(a.account_type) = 'REVENUE' THEN le.credit - le.debit ELSE 0 END), 0) AS revenue_net,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'EXPENSE' THEN le.debit - le.credit ELSE 0 END), 0) AS expense_net,
       COALESCE(SUM(CASE WHEN a.account_code LIKE '__.33000.001' THEN le.credit - le.debit ELSE 0 END), 0) AS retained_earnings
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month = @period_month;",
                connection))
            {
                closingCheck.Parameters.AddWithValue("company_id", companyId);
                closingCheck.Parameters.AddWithValue("location_id", locationId);
                closingCheck.Parameters.AddWithValue("period_month", targetMonth);
                await using var reader = await closingCheck.ExecuteReaderAsync();
                Assert(await reader.ReadAsync(), "Closing check query should return one row.");
                var revenueNet = reader.GetDecimal(0);
                var expenseNet = reader.GetDecimal(1);
                var retained = reader.GetDecimal(2);

                Assert(Math.Abs(revenueNet) < 0.01m, $"Revenue net should be zero after closing, got {revenueNet:N2}.");
                Assert(Math.Abs(expenseNet) < 0.01m, $"Expense net should be zero after closing, got {expenseNet:N2}.");
                Assert(Math.Abs(retained - 700m) < 0.01m, $"Retained earnings should capture net income 700, got {retained:N2}.");
            }
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            await using (var deleteAuditJournals = new NpgsqlCommand(
                @"DELETE FROM sec_audit_logs
WHERE entity_type = 'JOURNAL'
  AND (
      details ILIKE @rev_no
      OR details ILIKE @exp_no
      OR details ILIKE @close_no
      OR details ILIKE '%ITEST-CLOSEFLOW%');",
                connection))
            {
                deleteAuditJournals.Parameters.AddWithValue("rev_no", $"%{revenueJournalNo}%");
                deleteAuditJournals.Parameters.AddWithValue("exp_no", $"%{expenseJournalNo}%");
                deleteAuditJournals.Parameters.AddWithValue("close_no", $"%{closingJournalNo}%");
                await deleteAuditJournals.ExecuteNonQueryAsync();
            }

            await using (var deleteHeaders = new NpgsqlCommand(
                @"DELETE FROM gl_journal_headers
WHERE company_id = @company_id
  AND location_id = @location_id
  AND journal_no = ANY(@journal_nos);",
                connection))
            {
                deleteHeaders.Parameters.AddWithValue("company_id", companyId);
                deleteHeaders.Parameters.AddWithValue("location_id", locationId);
                deleteHeaders.Parameters.AddWithValue("journal_nos", new[] { revenueJournalNo, expenseJournalNo, closingJournalNo });
                await deleteHeaders.ExecuteNonQueryAsync();
            }

            await using (var deletePeriodAudit = new NpgsqlCommand(
                @"DELETE FROM sec_audit_logs
WHERE entity_type = 'ACCOUNTING_PERIOD'
  AND details ILIKE @period_mark
  AND details ILIKE @scope_mark;",
                connection))
            {
                deletePeriodAudit.Parameters.AddWithValue("period_mark", $"%period={targetMonth:yyyy-MM}%");
                deletePeriodAudit.Parameters.AddWithValue("scope_mark", $"%company={companyId};location={locationId};%");
                await deletePeriodAudit.ExecuteNonQueryAsync();
            }

            if (previous.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, previous.IsOpen, "ITEST_RESTORE");
            }
            else
            {
                await using var deleteCommand = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deleteCommand.Parameters.AddWithValue("company_id", companyId);
                deleteCommand.Parameters.AddWithValue("location_id", locationId);
                deleteCommand.Parameters.AddWithValue("period_month", targetMonth);
                await deleteCommand.ExecuteNonQueryAsync();
            }

            if (previousNext.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, nextMonth, previousNext.IsOpen, "ITEST_RESTORE_NEXT");
            }
            else
            {
                await using var deleteNextCommand = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deleteNextCommand.Parameters.AddWithValue("company_id", companyId);
                deleteNextCommand.Parameters.AddWithValue("location_id", locationId);
                deleteNextCommand.Parameters.AddWithValue("period_month", nextMonth);
                await deleteNextCommand.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestAccountingPeriodCloseAllowsEquationBalancedDespiteBalanceSheetDiffAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(30);
        var priorMonth = targetMonth.AddMonths(-1);
        var previousTarget = await GetAccountingPeriodStateAsync(companyId, locationId, targetMonth);
        var previousPrior = await GetAccountingPeriodStateAsync(companyId, locationId, priorMonth);

        var workspace = await service.GetJournalWorkspaceDataAsync(companyId, locationId, "admin");
        var accounts = workspace.Accounts;
        var asset = accounts.FirstOrDefault(x => string.Equals(x.AccountType, "ASSET", StringComparison.OrdinalIgnoreCase));
        var revenue = accounts.FirstOrDefault(x => string.Equals(x.AccountType, "REVENUE", StringComparison.OrdinalIgnoreCase));
        Assert(asset is not null, "Asset account is required for equation-balance close test.");
        Assert(revenue is not null, "Revenue account is required for equation-balance close test.");

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var journalNo = $"ITEST-EQN-CLOSE-{stamp}";
        long? journalId = null;

        try
        {
            await SetAccountingPeriodStateAsync(companyId, locationId, priorMonth, isOpen: true, note: "ITEST_EQN_PREV_OPEN");
            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: true, note: "ITEST_EQN_TARGET_OPEN");

            var saveResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    Id = 0,
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = journalNo,
                    JournalDate = priorMonth.AddDays(5),
                    ReferenceNo = "ITEST-EQN-CLOSE",
                    Description = "Equation-balance close scenario setup"
                },
                new[]
                {
                    new ManagedJournalLine
                    {
                        LineNo = 1,
                        AccountCode = asset!.Code,
                        Description = "Asset increase",
                        Debit = 1234m,
                        Credit = 0m
                    },
                    new ManagedJournalLine
                    {
                        LineNo = 2,
                        AccountCode = revenue!.Code,
                        Description = "Revenue increase",
                        Debit = 0m,
                        Credit = 1234m
                    }
                },
                "admin");
            Assert(saveResult.IsSuccess && saveResult.EntityId.HasValue, $"Failed to save setup journal: {saveResult.Message}");
            journalId = saveResult.EntityId!.Value;

            var submitResult = await service.SubmitJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(submitResult.IsSuccess, $"Failed to submit setup journal: {submitResult.Message}");
            var approveResult = await service.ApproveJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(approveResult.IsSuccess, $"Failed to approve setup journal: {approveResult.Message}");
            var postResult = await service.PostJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(postResult.IsSuccess, $"Failed to post setup journal: {postResult.Message}");

            await using (var connection = await OpenConnectionAsync())
            {
                await using var snapshotCommand = new NpgsqlCommand(
                    @"SELECT COALESCE(SUM(CASE WHEN upper(a.account_type) = 'ASSET' THEN le.debit - le.credit ELSE 0 END), 0) AS assets,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'LIABILITY' THEN le.credit - le.debit ELSE 0 END), 0) AS liabilities,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'EQUITY' THEN le.credit - le.debit ELSE 0 END), 0) AS equity,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'REVENUE' THEN le.credit - le.debit ELSE 0 END), 0) AS revenue,
       COALESCE(SUM(CASE WHEN upper(a.account_type) = 'EXPENSE' THEN le.debit - le.credit ELSE 0 END), 0) AS expense
FROM gl_ledger_entries le
JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = @company_id
  AND le.location_id = @location_id
  AND le.period_month <= @period_month;",
                    connection);
                snapshotCommand.Parameters.AddWithValue("company_id", companyId);
                snapshotCommand.Parameters.AddWithValue("location_id", locationId);
                snapshotCommand.Parameters.AddWithValue("period_month", targetMonth);

                await using var reader = await snapshotCommand.ExecuteReaderAsync();
                Assert(await reader.ReadAsync(), "Equation snapshot query should return one row.");
                var assets = reader.GetDecimal(0);
                var liabilities = reader.GetDecimal(1);
                var equity = reader.GetDecimal(2);
                var revenueAmount = reader.GetDecimal(3);
                var expenseAmount = reader.GetDecimal(4);

                var balanceSheetDiff = Math.Round(assets - (liabilities + equity), 2);
                var equationDiff = Math.Round((assets + expenseAmount) - (liabilities + equity + revenueAmount), 2);

                Assert(
                    Math.Abs(balanceSheetDiff) > 0.01m,
                    $"Setup should produce a non-zero balance-sheet-only diff, got {balanceSheetDiff:N2}.");
                Assert(
                    Math.Abs(equationDiff) < 0.01m,
                    $"Setup should remain equation-balanced, got {equationDiff:N2}.");
            }

            var closeResult = await service.SetAccountingPeriodOpenStateAsync(
                companyId,
                locationId,
                targetMonth,
                isOpen: false,
                actorUsername: "admin",
                note: "ITEST_EQN_CLOSE");
            Assert(
                closeResult.IsSuccess,
                $"Close period should succeed when equation is balanced despite balance-sheet-only diff: {closeResult.Message}");

            var periodsAfterClose = await service.GetAccountingPeriodsAsync(companyId, locationId, "admin");
            var closed = periodsAfterClose.FirstOrDefault(x => x.PeriodMonth.Date == targetMonth.Date);
            Assert(closed is not null, "Target period row should exist after close.");
            Assert(!closed!.IsOpen, "Target period should be closed.");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (journalId.HasValue)
            {
                await using (var deleteJournalAudit = new NpgsqlCommand(
                    @"DELETE FROM sec_audit_logs
WHERE entity_type = 'JOURNAL'
  AND (entity_id = @entity_id OR details ILIKE @journal_no);",
                    connection))
                {
                    deleteJournalAudit.Parameters.AddWithValue("entity_id", journalId.Value);
                    deleteJournalAudit.Parameters.AddWithValue("journal_no", $"%{journalNo}%");
                    await deleteJournalAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteJournal = new NpgsqlCommand(
                    "DELETE FROM gl_journal_headers WHERE id = @id;",
                    connection))
                {
                    deleteJournal.Parameters.AddWithValue("id", journalId.Value);
                    await deleteJournal.ExecuteNonQueryAsync();
                }
            }

            await using (var deletePeriodAudit = new NpgsqlCommand(
                @"DELETE FROM sec_audit_logs
WHERE entity_type = 'ACCOUNTING_PERIOD'
  AND details ILIKE @target_mark
  AND details ILIKE @scope_mark;",
                connection))
            {
                deletePeriodAudit.Parameters.AddWithValue("target_mark", $"%period={targetMonth:yyyy-MM}%");
                deletePeriodAudit.Parameters.AddWithValue("scope_mark", $"%company={companyId};location={locationId};%");
                await deletePeriodAudit.ExecuteNonQueryAsync();
            }

            await using (var deletePriorAudit = new NpgsqlCommand(
                @"DELETE FROM sec_audit_logs
WHERE entity_type = 'ACCOUNTING_PERIOD'
  AND details ILIKE @prior_mark
  AND details ILIKE @scope_mark;",
                connection))
            {
                deletePriorAudit.Parameters.AddWithValue("prior_mark", $"%period={priorMonth:yyyy-MM}%");
                deletePriorAudit.Parameters.AddWithValue("scope_mark", $"%company={companyId};location={locationId};%");
                await deletePriorAudit.ExecuteNonQueryAsync();
            }

            if (previousTarget.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, previousTarget.IsOpen, "ITEST_RESTORE");
            }
            else
            {
                await using var deleteTargetPeriod = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deleteTargetPeriod.Parameters.AddWithValue("company_id", companyId);
                deleteTargetPeriod.Parameters.AddWithValue("location_id", locationId);
                deleteTargetPeriod.Parameters.AddWithValue("period_month", targetMonth);
                await deleteTargetPeriod.ExecuteNonQueryAsync();
            }

            if (previousPrior.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, priorMonth, previousPrior.IsOpen, "ITEST_RESTORE");
            }
            else
            {
                await using var deletePriorPeriod = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deletePriorPeriod.Parameters.AddWithValue("company_id", companyId);
                deletePriorPeriod.Parameters.AddWithValue("location_id", locationId);
                deletePriorPeriod.Parameters.AddWithValue("period_month", priorMonth);
                await deletePriorPeriod.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestAccountingPeriodRejectsOutOfScopeFinanceAdminAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        var managementData = await service.GetUserManagementDataAsync();

        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var targetCompanyId = accessOptions.Companies[0].Id;
        var targetLocationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == targetCompanyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var financeAdminRole = RequireRole(managementData, "FINANCE_ADMIN");
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(2);

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var username = $"itest_fin_scope_{stamp}";
        var companyCode = $"ITFNA{stamp % 100000:00000}";
        var locationCode = $"L{stamp % 100000:00000}";

        long? scopedCompanyId = null;
        long? scopedLocationId = null;
        long? scopedUserId = null;

        try
        {
            var createCompanyResult = await service.SaveCompanyAsync(
                new ManagedCompany
                {
                    Id = 0,
                    Code = companyCode,
                    Name = $"Finance Admin Scope Company {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createCompanyResult.IsSuccess && createCompanyResult.EntityId.HasValue,
                $"Failed to create finance-admin scope company: {createCompanyResult.Message}");
            scopedCompanyId = createCompanyResult.EntityId!.Value;

            var createLocationResult = await service.SaveLocationAsync(
                new ManagedLocation
                {
                    Id = 0,
                    CompanyId = scopedCompanyId.Value,
                    Code = locationCode,
                    Name = $"Finance Admin Scope Location {stamp}",
                    IsActive = true
                },
                "admin");
            Assert(
                createLocationResult.IsSuccess && createLocationResult.EntityId.HasValue,
                $"Failed to create finance-admin scope location: {createLocationResult.Message}");
            scopedLocationId = createLocationResult.EntityId!.Value;

            var userSaveResult = await service.SaveUserAsync(
                new ManagedUser
                {
                    Id = 0,
                    Username = username,
                    FullName = "Scoped Finance Admin",
                    Email = $"{username}@local",
                    IsActive = true,
                    DefaultCompanyId = scopedCompanyId.Value,
                    DefaultLocationId = scopedLocationId.Value
                },
                "Admin@123",
                [financeAdminRole.Id],
                [scopedCompanyId.Value],
                [scopedLocationId.Value],
                "admin");
            Assert(userSaveResult.IsSuccess && userSaveResult.EntityId.HasValue, "Failed to create scoped finance-admin user.");
            scopedUserId = userSaveResult.EntityId!.Value;

            var result = await service.SetAccountingPeriodOpenStateAsync(
                targetCompanyId,
                targetLocationId,
                targetMonth,
                isOpen: false,
                actorUsername: username,
                note: "ITEST_SCOPE_DENIED");
            Assert(!result.IsSuccess, "Finance admin should not manage periods outside assigned company/location scope.");
            Assert(
                result.Message.Contains("akses", StringComparison.OrdinalIgnoreCase) ||
                result.Message.Contains("izin", StringComparison.OrdinalIgnoreCase),
                $"Expected scope-denial message, got: {result.Message}");
        }
        finally
        {
            if (scopedUserId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();
                await using (var deleteRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @id;", connection))
                {
                    deleteRoles.Parameters.AddWithValue("id", scopedUserId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand("DELETE FROM app_users WHERE id = @id;", connection))
                {
                    deleteUser.Parameters.AddWithValue("id", scopedUserId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }
            }

            if (scopedCompanyId.HasValue)
            {
                await CleanupTemporaryInventoryCostingCompanyAsync(scopedCompanyId.Value);
            }
        }
    }

    private static async Task TestAccountingPeriodCloseRejectsPendingStockOpnameAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(2);
        var previousPeriod = await GetAccountingPeriodStateAsync(companyId, locationId, targetMonth);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var opnameNo = $"ITEST-OPN-{stamp}";
        long? opnameId = null;

        try
        {
            _ = await service.GetInventoryWorkspaceDataAsync(companyId, locationId);
            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: true, note: "ITEST_PENDING_OPNAME_OPEN");

            await using (var connection = await OpenConnectionAsync())
            {
                await using var insertOpname = new NpgsqlCommand(
                    @"INSERT INTO inv_stock_opname (
    company_id,
    location_id,
    opname_no,
    opname_date,
    warehouse_id,
    description,
    status,
    created_by,
    updated_by,
    created_at,
    updated_at)
VALUES (
    @company_id,
    @location_id,
    @opname_no,
    @opname_date,
    NULL,
    @description,
    'DRAFT',
    'admin',
    'admin',
    NOW(),
    NOW())
RETURNING id;",
                    connection);
                insertOpname.Parameters.AddWithValue("company_id", companyId);
                insertOpname.Parameters.AddWithValue("location_id", locationId);
                insertOpname.Parameters.AddWithValue("opname_no", opnameNo);
                insertOpname.Parameters.AddWithValue("opname_date", targetMonth.AddDays(1));
                insertOpname.Parameters.AddWithValue("description", "Pending stock opname blocker");
                opnameId = Convert.ToInt64(await insertOpname.ExecuteScalarAsync());
            }

            var closeResult = await service.SetAccountingPeriodOpenStateAsync(
                companyId,
                locationId,
                targetMonth,
                isOpen: false,
                actorUsername: "admin",
                note: "ITEST_PENDING_OPNAME");
            Assert(!closeResult.IsSuccess, "Close period should be rejected when stock opname is still pending.");
            Assert(
                closeResult.Message.Contains("stock opname", StringComparison.OrdinalIgnoreCase),
                $"Expected stock-opname blocker message, got: {closeResult.Message}");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (opnameId.HasValue)
            {
                await using var deleteOpname = new NpgsqlCommand(
                    "DELETE FROM inv_stock_opname WHERE id = @id;",
                    connection);
                deleteOpname.Parameters.AddWithValue("id", opnameId.Value);
                await deleteOpname.ExecuteNonQueryAsync();
            }

            if (previousPeriod.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, previousPeriod.IsOpen, "ITEST_RESTORE_PENDING_OPNAME");
            }
            else
            {
                await using var deletePeriod = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deletePeriod.Parameters.AddWithValue("company_id", companyId);
                deletePeriod.Parameters.AddWithValue("location_id", locationId);
                deletePeriod.Parameters.AddWithValue("period_month", targetMonth);
                await deletePeriod.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestReportsCashFlowHonorsCashMetadataAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var previousPeriod = await GetAccountingPeriodStateAsync(companyId, locationId, targetMonth);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var journalNo = $"ITEST-CASHFLOW-{stamp}";

        long? cashAccountId = null;
        string cashAccountCode = string.Empty;
        string originalReportGroup = string.Empty;
        string originalCashflowCategory = string.Empty;
        string revenueAccountCode = string.Empty;
        long? journalId = null;

        try
        {
            _ = await service.GetCashFlowAsync(companyId, locationId, targetMonth, "admin");
            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: true, note: "ITEST_CASHFLOW_OPEN");

            await using (var connection = await OpenConnectionAsync())
            {
                await using (var accountCommand = new NpgsqlCommand(
                    @"SELECT id,
       account_code,
       COALESCE(report_group, ''),
       COALESCE(cashflow_category, '')
FROM gl_accounts
WHERE company_id = @company_id
  AND is_active = TRUE
  AND is_posting = TRUE
  AND upper(account_type) = 'ASSET'
  AND account_code NOT LIKE '%11100%'
  AND account_name NOT ILIKE '%kas%'
  AND account_name NOT ILIKE '%bank%'
ORDER BY account_code
LIMIT 1;",
                    connection))
                {
                    accountCommand.Parameters.AddWithValue("company_id", companyId);
                    await using var reader = await accountCommand.ExecuteReaderAsync();
                    Assert(await reader.ReadAsync(), "A non-cash heuristic asset account is required for cash-flow metadata test.");
                    cashAccountId = reader.GetInt64(0);
                    cashAccountCode = reader.GetString(1);
                    originalReportGroup = reader.GetString(2);
                    originalCashflowCategory = reader.GetString(3);
                }

                await using (var revenueCommand = new NpgsqlCommand(
                    @"SELECT account_code
FROM gl_accounts
WHERE company_id = @company_id
  AND is_active = TRUE
  AND is_posting = TRUE
  AND upper(account_type) = 'REVENUE'
ORDER BY account_code
LIMIT 1;",
                    connection))
                {
                    revenueCommand.Parameters.AddWithValue("company_id", companyId);
                    var scalar = await revenueCommand.ExecuteScalarAsync();
                    Assert(scalar is not null && scalar is not DBNull, "A revenue account is required for cash-flow metadata test.");
                    revenueAccountCode = Convert.ToString(scalar) ?? string.Empty;
                }

                await using var updateMetadata = new NpgsqlCommand(
                    @"UPDATE gl_accounts
SET report_group = 'CASH_BANK',
    cashflow_category = 'OPERATING_CASH',
    updated_by = 'admin',
    updated_at = NOW()
WHERE id = @id;",
                    connection);
                updateMetadata.Parameters.AddWithValue("id", cashAccountId.Value);
                await updateMetadata.ExecuteNonQueryAsync();
            }

            var saveResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    Id = 0,
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = journalNo,
                    JournalDate = targetMonth.AddDays(2),
                    ReferenceNo = "ITEST-CASHFLOW",
                    Description = "Cash flow metadata regression"
                },
                [
                    new ManagedJournalLine
                    {
                        LineNo = 1,
                        AccountCode = cashAccountCode,
                        Description = "Metadata-classified cash inflow",
                        Debit = 4321m,
                        Credit = 0m
                    },
                    new ManagedJournalLine
                    {
                        LineNo = 2,
                        AccountCode = revenueAccountCode,
                        Description = "Revenue offset",
                        Debit = 0m,
                        Credit = 4321m
                    }
                ],
                "admin");
            Assert(saveResult.IsSuccess && saveResult.EntityId.HasValue, $"Failed to save cash-flow setup journal: {saveResult.Message}");
            journalId = saveResult.EntityId!.Value;

            var submitResult = await service.SubmitJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(submitResult.IsSuccess, $"Failed to submit cash-flow setup journal: {submitResult.Message}");
            var approveResult = await service.ApproveJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(approveResult.IsSuccess, $"Failed to approve cash-flow setup journal: {approveResult.Message}");
            var postResult = await service.PostJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(postResult.IsSuccess, $"Failed to post cash-flow setup journal: {postResult.Message}");

            var cashFlow = await service.GetCashFlowAsync(companyId, locationId, targetMonth, "admin");
            var cashRow = cashFlow.FirstOrDefault(x => string.Equals(x.AccountCode, cashAccountCode, StringComparison.OrdinalIgnoreCase));
            Assert(cashRow is not null, $"Cash-flow report should include metadata-classified account {cashAccountCode}.");
            Assert(cashRow!.CashIn >= 4321m, $"Cash-flow report should include the posted inflow amount for {cashAccountCode}.");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (cashAccountId.HasValue)
            {
                await using var restoreMetadata = new NpgsqlCommand(
                    @"UPDATE gl_accounts
SET report_group = @report_group,
    cashflow_category = @cashflow_category,
    updated_by = 'admin',
    updated_at = NOW()
WHERE id = @id;",
                    connection);
                restoreMetadata.Parameters.AddWithValue("id", cashAccountId.Value);
                restoreMetadata.Parameters.AddWithValue("report_group", originalReportGroup);
                restoreMetadata.Parameters.AddWithValue("cashflow_category", originalCashflowCategory);
                await restoreMetadata.ExecuteNonQueryAsync();
            }

            if (journalId.HasValue)
            {
                await using (var deleteJournalAudit = new NpgsqlCommand(
                    @"DELETE FROM sec_audit_logs
WHERE entity_type = 'JOURNAL'
  AND (entity_id = @entity_id OR details ILIKE @journal_no);",
                    connection))
                {
                    deleteJournalAudit.Parameters.AddWithValue("entity_id", journalId.Value);
                    deleteJournalAudit.Parameters.AddWithValue("journal_no", $"%{journalNo}%");
                    await deleteJournalAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteJournal = new NpgsqlCommand(
                    "DELETE FROM gl_journal_headers WHERE id = @id;",
                    connection))
                {
                    deleteJournal.Parameters.AddWithValue("id", journalId.Value);
                    await deleteJournal.ExecuteNonQueryAsync();
                }
            }

            if (previousPeriod.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, previousPeriod.IsOpen, "ITEST_RESTORE_CASHFLOW");
            }
            else
            {
                await using var deletePeriod = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deletePeriod.Parameters.AddWithValue("company_id", companyId);
                deletePeriod.Parameters.AddWithValue("location_id", locationId);
                deletePeriod.Parameters.AddWithValue("period_month", targetMonth);
                await deletePeriod.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestAccountingPeriodRejectsUnauthorizedActorAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(2);

        var roleStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var roleCode = $"ITEST_NO_PERIOD_{roleStamp}";
        var username = $"itest_noperiod_{roleStamp}";
        long? roleId = null;
        long? userId = null;

        try
        {
            var roleSaveResult = await service.SaveRoleAsync(
                new ManagedRole
                {
                    Id = 0,
                    Code = roleCode,
                    Name = "No Period Permission Role",
                    IsSuperRole = false,
                    IsActive = true
                },
                Array.Empty<long>(),
                "admin");
            Assert(roleSaveResult.IsSuccess && roleSaveResult.EntityId.HasValue, "Failed to create non-privileged role.");
            roleId = roleSaveResult.EntityId!.Value;

            var userSaveResult = await service.SaveUserAsync(
                new ManagedUser
                {
                    Id = 0,
                    Username = username,
                    FullName = "No Period User",
                    Email = $"{username}@local",
                    IsActive = true,
                    DefaultCompanyId = companyId,
                    DefaultLocationId = locationId
                },
                "Admin@123",
                new[] { roleId.Value },
                new[] { companyId },
                new[] { locationId },
                "admin");
            Assert(userSaveResult.IsSuccess && userSaveResult.EntityId.HasValue, "Failed to create non-privileged user.");
            userId = userSaveResult.EntityId!.Value;

            var updatePeriodResult = await service.SetAccountingPeriodOpenStateAsync(
                companyId,
                locationId,
                targetMonth,
                isOpen: false,
                actorUsername: username,
                note: "ITEST_UNAUTHORIZED");
            Assert(!updatePeriodResult.IsSuccess, "Unauthorized actor should be rejected for period update.");
            Assert(
                updatePeriodResult.Message.Contains("izin", StringComparison.OrdinalIgnoreCase),
                "Expected unauthorized message containing izin.");
        }
        finally
        {
            if (userId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();
                await using (var deleteRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @id;", connection))
                {
                    deleteRoles.Parameters.AddWithValue("id", userId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand("DELETE FROM app_users WHERE id = @id;", connection))
                {
                    deleteUser.Parameters.AddWithValue("id", userId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }
            }

            if (roleId.HasValue)
            {
                await service.DeleteRoleAsync(roleId.Value, "admin");
            }
        }
    }

    private static async Task TestJournalRequiresPostingCostCenterAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(18);
        var previousPeriod = await GetAccountingPeriodStateAsync(companyId, locationId, targetMonth);

        var workspace = await service.GetJournalWorkspaceDataAsync(companyId, locationId, "admin");
        var assetAccount = workspace.Accounts.FirstOrDefault(x =>
            x.IsPosting &&
            string.Equals(x.AccountType, "ASSET", StringComparison.OrdinalIgnoreCase));
        Assert(assetAccount is not null, "Posting asset account is required for cost center journal test.");

        var allAccounts = await service.GetAccountsAsync(companyId, includeInactive: false, actorUsername: "admin");
        var expenseParent = allAccounts.FirstOrDefault(x =>
            !x.IsPosting &&
            x.IsActive &&
            string.Equals(x.AccountType, "EXPENSE", StringComparison.OrdinalIgnoreCase));
        Assert(expenseParent is not null, "Expense parent account is required for cost center journal test.");

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var middleSegment = (int)(Math.Abs(stamp / 1000) % 10000);
        var suffixSegment = (int)(Math.Abs(stamp) % 1000);
        var expenseCode = $"KB.5{middleSegment:0000}.{suffixSegment:000}";
        var journalNo = $"ITEST-CC-{stamp}";

        long? accountId = null;
        long? estateId = null;
        long? divisionId = null;
        long? blockId = null;
        long? journalId = null;

        try
        {
            await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, isOpen: true, note: "ITEST_CC_OPEN");

            var saveAccountResult = await service.SaveAccountAsync(
                companyId,
                new ManagedAccount
                {
                    Id = 0,
                    Code = expenseCode,
                    Name = $"Cost Center Required Account {stamp}",
                    AccountType = "EXPENSE",
                    ParentAccountId = expenseParent!.Id,
                    RequiresCostCenter = true,
                    IsActive = true
                },
                "admin");
            Assert(saveAccountResult.IsSuccess && saveAccountResult.EntityId.HasValue, $"Failed to create cost-center-required account: {saveAccountResult.Message}");
            accountId = saveAccountResult.EntityId!.Value;

            var estateSave = await service.SaveCostCenterAsync(
                companyId,
                locationId,
                new ManagedCostCenter
                {
                    EstateCode = "NE",
                    EstateName = "North Estate",
                    IsActive = true
                },
                "admin");
            Assert(estateSave.IsSuccess && estateSave.EntityId.HasValue, $"Failed to create estate cost center: {estateSave.Message}");
            estateId = estateSave.EntityId!.Value;

            var divisionSave = await service.SaveCostCenterAsync(
                companyId,
                locationId,
                new ManagedCostCenter
                {
                    EstateCode = "NE",
                    EstateName = "North Estate",
                    DivisionCode = "D01",
                    DivisionName = "Division 01",
                    IsActive = true
                },
                "admin");
            Assert(divisionSave.IsSuccess && divisionSave.EntityId.HasValue, $"Failed to create division cost center: {divisionSave.Message}");
            divisionId = divisionSave.EntityId!.Value;

            var blockSave = await service.SaveCostCenterAsync(
                companyId,
                locationId,
                new ManagedCostCenter
                {
                    EstateCode = "NE",
                    EstateName = "North Estate",
                    DivisionCode = "D01",
                    DivisionName = "Division 01",
                    BlockCode = "B12",
                    BlockName = "Block B12",
                    IsActive = true
                },
                "admin");
            Assert(blockSave.IsSuccess && blockSave.EntityId.HasValue, $"Failed to create block cost center: {blockSave.Message}");
            blockId = blockSave.EntityId!.Value;

            var costCenters = await service.GetCostCentersAsync(companyId, locationId, includeInactive: true, actorUsername: "admin");
            var blockCostCenter = costCenters.FirstOrDefault(x => x.Id == blockId.Value);
            Assert(blockCostCenter is not null, "Block cost center should be returned by GetCostCenters.");
            Assert(blockCostCenter!.IsPosting, "Block level cost center should be posting.");
            Assert(
                string.Equals(blockCostCenter.CostCenterCode, "NE-D01-B12", StringComparison.Ordinal),
                $"Unexpected block cost center code: {blockCostCenter.CostCenterCode}");

            var missingCostCenterResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = $"{journalNo}-MISS",
                    JournalDate = targetMonth.AddDays(5),
                    PeriodMonth = targetMonth,
                    Description = "Missing cost center should fail"
                },
                new[]
                {
                    new ManagedJournalLine
                    {
                        LineNo = 1,
                        AccountCode = expenseCode,
                        Description = "Expense without cost center",
                        Debit = 100m,
                        Credit = 0m
                    },
                    new ManagedJournalLine
                    {
                        LineNo = 2,
                        AccountCode = assetAccount!.Code,
                        Description = "Balancing asset",
                        Debit = 0m,
                        Credit = 100m
                    }
                },
                "admin");
            Assert(!missingCostCenterResult.IsSuccess, "Draft save should fail when required cost center is missing.");
            Assert(
                missingCostCenterResult.Message.Contains("cost center", StringComparison.OrdinalIgnoreCase),
                $"Unexpected missing cost center message: {missingCostCenterResult.Message}");

            var divisionCostCenterResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = $"{journalNo}-DIV",
                    JournalDate = targetMonth.AddDays(6),
                    PeriodMonth = targetMonth,
                    Description = "Non-posting cost center should fail"
                },
                new[]
                {
                    new ManagedJournalLine
                    {
                        LineNo = 1,
                        AccountCode = expenseCode,
                        Description = "Expense with division cost center",
                        Debit = 100m,
                        Credit = 0m,
                        CostCenterCode = "NE-D01"
                    },
                    new ManagedJournalLine
                    {
                        LineNo = 2,
                        AccountCode = assetAccount.Code,
                        Description = "Balancing asset",
                        Debit = 0m,
                        Credit = 100m
                    }
                },
                "admin");
            Assert(!divisionCostCenterResult.IsSuccess, "Draft save should fail when cost center is non-posting.");
            Assert(
                divisionCostCenterResult.Message.Contains("posting", StringComparison.OrdinalIgnoreCase),
                $"Unexpected non-posting cost center message: {divisionCostCenterResult.Message}");

            var saveResult = await service.SaveJournalDraftAsync(
                new ManagedJournalHeader
                {
                    CompanyId = companyId,
                    LocationId = locationId,
                    JournalNo = journalNo,
                    JournalDate = targetMonth.AddDays(7),
                    PeriodMonth = targetMonth,
                    Description = "Posting cost center should pass"
                },
                new[]
                {
                    new ManagedJournalLine
                    {
                        LineNo = 1,
                        AccountCode = expenseCode,
                        Description = "Expense with posting cost center",
                        Debit = 250m,
                        Credit = 0m,
                        CostCenterCode = "NE-D01-B12"
                    },
                    new ManagedJournalLine
                    {
                        LineNo = 2,
                        AccountCode = assetAccount.Code,
                        Description = "Balancing asset",
                        Debit = 0m,
                        Credit = 250m
                    }
                },
                "admin");
            Assert(saveResult.IsSuccess && saveResult.EntityId.HasValue, $"Draft save should pass with posting cost center: {saveResult.Message}");
            journalId = saveResult.EntityId!.Value;

            var submitResult = await service.SubmitJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(submitResult.IsSuccess, $"Failed to submit journal with cost center: {submitResult.Message}");
            var approveResult = await service.ApproveJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(approveResult.IsSuccess, $"Failed to approve journal with cost center: {approveResult.Message}");
            var postResult = await service.PostJournalAsync(journalId.Value, companyId, locationId, "admin");
            Assert(postResult.IsSuccess, $"Failed to post journal with cost center: {postResult.Message}");

            var subLedgerRows = await service.GetSubLedgerAsync(companyId, locationId, targetMonth, expenseCode, actorUsername: "admin");
            var postedRow = subLedgerRows.FirstOrDefault(x =>
                string.Equals(x.JournalNo, journalNo, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.CostCenterCode, "NE-D01-B12", StringComparison.OrdinalIgnoreCase));
            Assert(postedRow is not null, "Subledger should return posted journal row with cost center.");
            Assert(
                string.Equals(postedRow!.EstateCode, "NE", StringComparison.OrdinalIgnoreCase),
                $"Unexpected subledger estate code: {postedRow.EstateCode}");
            Assert(
                string.Equals(postedRow.DivisionCode, "D01", StringComparison.OrdinalIgnoreCase),
                $"Unexpected subledger division code: {postedRow.DivisionCode}");
            Assert(
                string.Equals(postedRow.BlockCode, "B12", StringComparison.OrdinalIgnoreCase),
                $"Unexpected subledger block code: {postedRow.BlockCode}");
        }
        finally
        {
            await using var connection = await OpenConnectionAsync();

            if (journalId.HasValue)
            {
                await using (var deleteLedger = new NpgsqlCommand(
                    "DELETE FROM gl_ledger_entries WHERE journal_id = @journal_id;",
                    connection))
                {
                    deleteLedger.Parameters.AddWithValue("journal_id", journalId.Value);
                    await deleteLedger.ExecuteNonQueryAsync();
                }

                await using (var deleteDetails = new NpgsqlCommand(
                    "DELETE FROM gl_journal_details WHERE header_id = @header_id;",
                    connection))
                {
                    deleteDetails.Parameters.AddWithValue("header_id", journalId.Value);
                    await deleteDetails.ExecuteNonQueryAsync();
                }

                await using (var deleteHeader = new NpgsqlCommand(
                    "DELETE FROM gl_journal_headers WHERE id = @id;",
                    connection))
                {
                    deleteHeader.Parameters.AddWithValue("id", journalId.Value);
                    await deleteHeader.ExecuteNonQueryAsync();
                }
            }

            await using (var deleteJournalAudit = new NpgsqlCommand(
                @"DELETE FROM sec_audit_logs
WHERE entity_type = 'JOURNAL'
  AND details ILIKE @journal_no;",
                connection))
            {
                deleteJournalAudit.Parameters.AddWithValue("journal_no", $"%{journalNo}%");
                await deleteJournalAudit.ExecuteNonQueryAsync();
            }

            if (accountId.HasValue)
            {
                await using (var deleteAccountAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'ACCOUNT' AND entity_id = @entity_id;",
                    connection))
                {
                    deleteAccountAudit.Parameters.AddWithValue("entity_id", accountId.Value);
                    await deleteAccountAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteAccount = new NpgsqlCommand(
                    "DELETE FROM gl_accounts WHERE id = @id;",
                    connection))
                {
                    deleteAccount.Parameters.AddWithValue("id", accountId.Value);
                    await deleteAccount.ExecuteNonQueryAsync();
                }
            }

            foreach (var costCenterId in new[] { blockId, divisionId, estateId }.Where(x => x.HasValue).Select(x => x!.Value))
            {
                await using (var deleteCostCenterAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'COST_CENTER' AND entity_id = @entity_id;",
                    connection))
                {
                    deleteCostCenterAudit.Parameters.AddWithValue("entity_id", costCenterId);
                    await deleteCostCenterAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteCostCenter = new NpgsqlCommand(
                    "DELETE FROM gl_cost_centers WHERE id = @id;",
                    connection))
                {
                    deleteCostCenter.Parameters.AddWithValue("id", costCenterId);
                    await deleteCostCenter.ExecuteNonQueryAsync();
                }
            }

            if (previousPeriod.Exists)
            {
                await SetAccountingPeriodStateAsync(companyId, locationId, targetMonth, previousPeriod.IsOpen, "ITEST_CC_RESTORE");
            }
            else
            {
                await using var deletePeriod = new NpgsqlCommand(
                    @"DELETE FROM gl_accounting_periods
WHERE company_id = @company_id
  AND location_id = @location_id
  AND period_month = @period_month;",
                    connection);
                deletePeriod.Parameters.AddWithValue("company_id", companyId);
                deletePeriod.Parameters.AddWithValue("location_id", locationId);
                deletePeriod.Parameters.AddWithValue("period_month", targetMonth);
                await deletePeriod.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task TestAccountSaveAndSoftDeleteAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");

        var companyId = accessOptions.Companies[0].Id;
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var middleSegment = (int)(Math.Abs(stamp / 1000) % 10000);
        var suffixSegment = (int)(Math.Abs(stamp) % 1000);
        var code = $"HO.5{middleSegment:0000}.{suffixSegment:000}";
        long? accountId = null;

        try
        {
            var saveResult = await service.SaveAccountAsync(
                companyId,
                new ManagedAccount
                {
                    Id = 0,
                    Code = code,
                    Name = $"Integration Test Account {stamp}",
                    AccountType = "EXPENSE",
                    RequiresCostCenter = true,
                    IsActive = true
                },
                "admin");

            Assert(saveResult.IsSuccess, $"SaveAccount failed: {saveResult.Message}");
            Assert(saveResult.EntityId.HasValue && saveResult.EntityId.Value > 0, "Saved account id must be returned.");

            accountId = saveResult.EntityId!.Value;
            var listAfterSave = await service.GetAccountsAsync(companyId, includeInactive: true, actorUsername: "admin");
            var saved = listAfterSave.FirstOrDefault(x => x.Id == accountId.Value);
            Assert(saved is not null, "Saved account should be returned by GetAccounts.");
            Assert(saved!.IsActive, "Saved account should be active.");
            Assert(
                string.Equals(saved.AccountType, "EXPENSE", StringComparison.OrdinalIgnoreCase),
                $"Expected account type EXPENSE, got {saved.AccountType}.");
            Assert(saved.RequiresCostCenter, "Saved account should preserve RequiresCostCenter flag.");

            var deactivateResult = await service.SoftDeleteAccountAsync(companyId, accountId.Value, "admin");
            Assert(deactivateResult.IsSuccess, $"SoftDeleteAccount failed: {deactivateResult.Message}");

            var listAfterDeactivate = await service.GetAccountsAsync(companyId, includeInactive: true, actorUsername: "admin");
            var deactivated = listAfterDeactivate.FirstOrDefault(x => x.Id == accountId.Value);
            Assert(deactivated is not null, "Deactivated account should remain queryable.");
            Assert(!deactivated!.IsActive, "Deactivated account should be inactive.");
        }
        finally
        {
            if (accountId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();
                await using (var deleteAudit = new NpgsqlCommand(
                    "DELETE FROM sec_audit_logs WHERE entity_type = 'ACCOUNT' AND entity_id = @entity_id;",
                    connection))
                {
                    deleteAudit.Parameters.AddWithValue("entity_id", accountId.Value);
                    await deleteAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteAccount = new NpgsqlCommand(
                    "DELETE FROM gl_accounts WHERE id = @id;",
                    connection))
                {
                    deleteAccount.Parameters.AddWithValue("id", accountId.Value);
                    await deleteAccount.ExecuteNonQueryAsync();
                }
            }
        }
    }

    private static async Task TestAccountRejectsUnauthorizedActorAsync()
    {
        var service = CreateService();
        var accessOptions = await service.GetLoginAccessOptionsAsync("admin");
        Assert(accessOptions is not null, "Admin access options must exist.");
        Assert(accessOptions!.Companies.Count > 0, "At least one company is required.");
        Assert(accessOptions.Locations.Count > 0, "At least one location is required.");

        var companyId = accessOptions.Companies[0].Id;
        var locationId = accessOptions.Locations.FirstOrDefault(x => x.CompanyId == companyId)?.Id
            ?? accessOptions.Locations[0].Id;
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var roleCode = $"ITEST_NO_ACC_WRITE_{stamp}";
        var username = $"itest_noaccwrite_{stamp}";
        var accountCode = $"HO.51998.{Math.Abs(stamp % 1000):000}";

        long? roleId = null;
        long? userId = null;

        try
        {
            var roleSaveResult = await service.SaveRoleAsync(
                new ManagedRole
                {
                    Id = 0,
                    Code = roleCode,
                    Name = "No Account Write Permission Role",
                    IsSuperRole = false,
                    IsActive = true
                },
                Array.Empty<long>(),
                "admin");
            Assert(roleSaveResult.IsSuccess && roleSaveResult.EntityId.HasValue, "Failed to create restricted role.");
            roleId = roleSaveResult.EntityId!.Value;

            var userSaveResult = await service.SaveUserAsync(
                new ManagedUser
                {
                    Id = 0,
                    Username = username,
                    FullName = "No Account Write User",
                    Email = $"{username}@local",
                    IsActive = true,
                    DefaultCompanyId = companyId,
                    DefaultLocationId = locationId
                },
                "Admin@123",
                [roleId.Value],
                [companyId],
                [locationId],
                "admin");
            Assert(userSaveResult.IsSuccess && userSaveResult.EntityId.HasValue, "Failed to create restricted user.");
            userId = userSaveResult.EntityId!.Value;

            var saveResult = await service.SaveAccountAsync(
                companyId,
                new ManagedAccount
                {
                    Id = 0,
                    Code = accountCode,
                    Name = $"Unauthorized Account {stamp}",
                    AccountType = "EXPENSE",
                    IsActive = true
                },
                username);

            Assert(!saveResult.IsSuccess, "Unauthorized actor should be rejected for account save.");
            Assert(
                saveResult.Message.Contains("izin", StringComparison.OrdinalIgnoreCase),
                "Expected unauthorized account-save message containing izin.");
        }
        finally
        {
            if (userId.HasValue)
            {
                await using var connection = await OpenConnectionAsync();
                await using (var deleteRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @id;", connection))
                {
                    deleteRoles.Parameters.AddWithValue("id", userId.Value);
                    await deleteRoles.ExecuteNonQueryAsync();
                }

                await using (var deleteUser = new NpgsqlCommand("DELETE FROM app_users WHERE id = @id;", connection))
                {
                    deleteUser.Parameters.AddWithValue("id", userId.Value);
                    await deleteUser.ExecuteNonQueryAsync();
                }
            }

            if (roleId.HasValue)
            {
                await service.DeleteRoleAsync(roleId.Value, "admin");
            }
        }
    }

}

