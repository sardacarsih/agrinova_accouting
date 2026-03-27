using System.Text;
using System.Text.RegularExpressions;
using Accounting.Infrastructure.Logging;
using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService : IAccessControlService
{
    public async Task<UserManagementData> GetUserManagementDataAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (!TryBuildQualifiedTableName(_options.UsersTable, out var usersTable))
        {
            return new UserManagementData();
        }

        var data = new UserManagementData();

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var usersCommand = new NpgsqlCommand($@"
SELECT u.id,
       u.username,
       COALESCE(u.full_name, ''),
       COALESCE(u.email, ''),
       u.is_active,
       u.default_company_id,
       u.default_location_id,
       COALESCE(dc.code, ''),
       COALESCE(dc.name, ''),
       COALESCE(dl.code, ''),
       COALESCE(dl.name, '')
FROM {usersTable} u
LEFT JOIN org_companies dc ON dc.id = u.default_company_id
LEFT JOIN org_locations dl ON dl.id = u.default_location_id
ORDER BY u.username;", connection))
        {
            await using var reader = await usersCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                data.Users.Add(new ManagedUser
                {
                    Id = reader.GetInt64(0),
                    Username = reader.GetString(1),
                    FullName = reader.GetString(2),
                    Email = reader.GetString(3),
                    IsActive = !reader.IsDBNull(4) && reader.GetBoolean(4),
                    DefaultCompanyId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    DefaultLocationId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    DefaultCompanyDisplay = reader.IsDBNull(5)
                        ? "-"
                        : string.IsNullOrWhiteSpace(reader.GetString(7))
                            ? $"ID {reader.GetInt64(5)}"
                            : $"{reader.GetString(7)} - {reader.GetString(8)}",
                    DefaultLocationDisplay = reader.IsDBNull(6)
                        ? "-"
                        : string.IsNullOrWhiteSpace(reader.GetString(9))
                            ? $"ID {reader.GetInt64(6)}"
                            : $"{reader.GetString(9)} - {reader.GetString(10)}"
                });
            }
        }

        await using (var rolesCommand = new NpgsqlCommand(@"
SELECT r.id,
       r.code,
       r.name,
       r.is_super_role,
       r.is_active,
       COALESCE(ur.assigned_user_count, 0) AS assigned_user_count,
       COALESCE(ra.permission_count, 0) AS permission_count
FROM sec_roles r
LEFT JOIN (
    SELECT role_id, COUNT(*)::INT AS assigned_user_count
    FROM sec_user_roles
    GROUP BY role_id
) ur ON ur.role_id = r.id
LEFT JOIN (
    SELECT role_id, COUNT(*)::INT AS permission_count
    FROM sec_role_action_access
    GROUP BY role_id
) ra ON ra.role_id = r.id
ORDER BY r.code;", connection))
        {
            await using var reader = await rolesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                data.Roles.Add(new ManagedRole
                {
                    Id = reader.GetInt64(0),
                    Code = reader.GetString(1),
                    Name = reader.GetString(2),
                    IsSuperRole = !reader.IsDBNull(3) && reader.GetBoolean(3),
                    IsActive = !reader.IsDBNull(4) && reader.GetBoolean(4),
                    AssignedUserCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    PermissionCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
                });
            }
        }

        await using (var scopesCommand = new NpgsqlCommand(@"
SELECT a.id,
       mo.module_code,
       mo.module_name,
       sm.submodule_code,
       sm.submodule_name,
       a.action_code,
       a.action_name,
       a.is_active
FROM sec_actions a
JOIN sec_submodules sm ON sm.id = a.submodule_id
JOIN sec_modules mo ON mo.id = sm.module_id
ORDER BY mo.sort_order, sm.sort_order, a.sort_order, a.action_code;", connection))
        {
            await using var reader = await scopesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var moduleCode = reader.GetString(1);
                var submoduleCode = reader.GetString(3);
                var actionCode = reader.GetString(5);
                data.AccessScopes.Add(new ManagedAccessScope
                {
                    Id = reader.GetInt64(0),
                    Code = $"{moduleCode}.{submoduleCode}.{actionCode}",
                    Name = reader.GetString(6),
                    ModuleCode = moduleCode,
                    ModuleName = reader.GetString(2),
                    SubmoduleCode = submoduleCode,
                    SubmoduleName = reader.GetString(4),
                    ActionCode = actionCode,
                    IsActive = !reader.IsDBNull(7) && reader.GetBoolean(7)
                });
            }
        }

        await using (var companiesCommand = new NpgsqlCommand(@"
SELECT id, code, name, is_active
FROM org_companies
ORDER BY code;", connection))
        {
            await using var reader = await companiesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                data.Companies.Add(new ManagedCompany
                {
                    Id = reader.GetInt64(0),
                    Code = reader.GetString(1),
                    Name = reader.GetString(2),
                    IsActive = !reader.IsDBNull(3) && reader.GetBoolean(3)
                });
            }
        }

        await using (var locationsCommand = new NpgsqlCommand(@"
SELECT l.id,
       l.company_id,
       c.code,
       c.name,
       l.code,
       l.name,
       COALESCE(l.location_type, 'OFFICE'),
       l.is_active
FROM org_locations l
JOIN org_companies c ON c.id = l.company_id
ORDER BY c.code, l.code;", connection))
        {
            await using var reader = await locationsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                data.Locations.Add(new ManagedLocation
                {
                    Id = reader.GetInt64(0),
                    CompanyId = reader.GetInt64(1),
                    CompanyCode = reader.GetString(2),
                    CompanyName = reader.GetString(3),
                    Code = reader.GetString(4),
                    Name = reader.GetString(5),
                    LocationType = reader.IsDBNull(6) ? "OFFICE" : reader.GetString(6),
                    IsActive = !reader.IsDBNull(7) && reader.GetBoolean(7)
                });
            }
        }

        await FillMapAsync(connection, "SELECT user_id, role_id FROM sec_user_roles;", data.UserRoleIdsByUserId, cancellationToken);
        await FillMapAsync(connection, "SELECT role_id, action_id FROM sec_role_action_access;", data.RoleScopeIdsByRoleId, cancellationToken);
        await FillMapAsync(connection, "SELECT user_id, company_id FROM sec_user_company_access;", data.UserCompanyIdsByUserId, cancellationToken);
        await FillMapAsync(connection, "SELECT user_id, location_id FROM sec_user_location_access;", data.UserLocationIdsByUserId, cancellationToken);
        PopulateUserEffectiveAccessReadModel(data);
        PopulateRoleAuditReadModel(data);

        return data;
    }

    private static void PopulateUserEffectiveAccessReadModel(UserManagementData data)
    {
        data.UserEffectiveAccessByUserId.Clear();

        var roleMap = data.Roles.ToDictionary(x => x.Id);
        var scopeMap = data.AccessScopes.ToDictionary(x => x.Id);
        var companyMap = data.Companies.ToDictionary(x => x.Id);
        var locationMap = data.Locations.ToDictionary(x => x.Id);
        var activeCompanyLabels = data.Companies
            .Where(x => x.IsActive)
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Code} - {x.Name}")
            .ToList();
        var activeLocationLabels = data.Locations
            .Where(x => x.IsActive)
            .OrderBy(x => x.CompanyCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.CompanyCode} • {x.Code} - {x.Name}")
            .ToList();
        var activeCompanyIds = data.Companies
            .Where(x => x.IsActive)
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Id)
            .ToList();
        var activeLocationIds = data.Locations
            .Where(x => x.IsActive)
            .OrderBy(x => x.CompanyCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Id)
            .ToList();

        foreach (var user in data.Users)
        {
            var roleId = data.UserRoleIdsByUserId.TryGetValue(user.Id, out var roleIds)
                ? roleIds.FirstOrDefault()
                : 0;

            roleMap.TryGetValue(roleId, out var role);
            var isSuperRole = role?.IsSuperRole == true;
            var companyIds = isSuperRole
                ? activeCompanyIds
                : data.UserCompanyIdsByUserId.TryGetValue(user.Id, out var userCompanyIds)
                    ? userCompanyIds
                        .Where(companyMap.ContainsKey)
                        .OrderBy(id => companyMap[id].Code, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<long>();
            var locationIds = isSuperRole
                ? activeLocationIds
                : data.UserLocationIdsByUserId.TryGetValue(user.Id, out var userLocationIds)
                    ? userLocationIds
                        .Where(locationMap.ContainsKey)
                        .OrderBy(id => locationMap[id].CompanyCode, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(id => locationMap[id].Code, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<long>();
            var effectiveScopeIds = role is null
                ? new HashSet<long>()
                : role.IsSuperRole
                    ? data.AccessScopes.Select(x => x.Id).ToHashSet()
                    : data.RoleScopeIdsByRoleId.TryGetValue(role.Id, out var scopeIds)
                        ? new HashSet<long>(scopeIds)
                        : new HashSet<long>();
            var roleLabel = role is null ? "-" : $"{role.Code} - {role.Name}";

            var modules = effectiveScopeIds
                .Where(scopeMap.ContainsKey)
                .Select(id => scopeMap[id])
                .OrderBy(scope => scope.Id)
                .GroupBy(scope => $"{scope.ModuleCode}|{scope.ModuleName}", StringComparer.OrdinalIgnoreCase)
                .Select(moduleGroup =>
                {
                    var firstModule = moduleGroup.First();
                    return new UserEffectiveAccessModuleDetail
                    {
                        ModuleCode = firstModule.ModuleCode,
                        ModuleName = firstModule.ModuleName,
                        Submodules = moduleGroup
                            .GroupBy(scope => $"{scope.SubmoduleCode}|{scope.SubmoduleName}", StringComparer.OrdinalIgnoreCase)
                            .Select(submoduleGroup =>
                            {
                                var firstSubmodule = submoduleGroup.First();
                                return new UserEffectiveAccessSubmoduleDetail
                                {
                                    ModuleCode = firstSubmodule.ModuleCode,
                                    ModuleName = firstSubmodule.ModuleName,
                                    SubmoduleCode = firstSubmodule.SubmoduleCode,
                                    SubmoduleName = firstSubmodule.SubmoduleName,
                                    Actions = submoduleGroup
                                        .OrderBy(scope => scope.Id)
                                        .Select(scope => new UserEffectiveAccessActionDetail
                                        {
                                            ScopeId = scope.Id,
                                            Label = string.IsNullOrWhiteSpace(scope.Name) ? scope.ActionCode : scope.Name,
                                            ActionCode = scope.ActionCode,
                                            GrantedByRole = roleLabel
                                        })
                                        .ToList()
                                };
                            })
                            .ToList()
                    };
                })
                .ToList();

            data.UserEffectiveAccessByUserId[user.Id] = new UserEffectiveAccessDetail
            {
                UserId = user.Id,
                RoleId = role?.Id,
                RoleCode = role?.Code ?? string.Empty,
                RoleName = role?.Name ?? string.Empty,
                IsSuperRole = isSuperRole,
                CompanyIds = companyIds,
                CompanyLabels = isSuperRole
                    ? activeCompanyLabels
                    : companyIds.Select(id => $"{companyMap[id].Code} - {companyMap[id].Name}").ToList(),
                LocationIds = locationIds,
                LocationLabels = isSuperRole
                    ? activeLocationLabels
                    : locationIds.Select(id => $"{locationMap[id].CompanyCode} • {locationMap[id].Code} - {locationMap[id].Name}").ToList(),
                Modules = modules
            };
        }
    }

    private static void PopulateRoleAuditReadModel(UserManagementData data)
    {
        data.RoleAuditByRoleId.Clear();

        var usersByRoleId = data.Users
            .Where(user => data.UserRoleIdsByUserId.TryGetValue(user.Id, out var roleIds) && roleIds.Count > 0)
            .Select(user => new
            {
                User = user,
                RoleId = data.UserRoleIdsByUserId[user.Id].First()
            })
            .GroupBy(entry => entry.RoleId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(entry => entry.User.Username, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new ManagedUser
                    {
                        Id = entry.User.Id,
                        Username = entry.User.Username,
                        FullName = entry.User.FullName,
                        Email = entry.User.Email,
                        IsActive = entry.User.IsActive,
                        RoleDisplay = entry.User.RoleDisplay,
                        ModuleDisplay = entry.User.ModuleDisplay,
                        DefaultCompanyId = entry.User.DefaultCompanyId,
                        DefaultLocationId = entry.User.DefaultLocationId,
                        DefaultCompanyDisplay = entry.User.DefaultCompanyDisplay,
                        DefaultLocationDisplay = entry.User.DefaultLocationDisplay
                    })
                    .ToList());

        foreach (var role in data.Roles)
        {
            var persistedScopeIds = role.IsSuperRole
                ? data.AccessScopes.Select(scope => scope.Id).ToList()
                : data.RoleScopeIdsByRoleId.TryGetValue(role.Id, out var scopeIds)
                    ? scopeIds.OrderBy(id => id).ToList()
                    : new List<long>();

            data.RoleAuditByRoleId[role.Id] = new RoleAuditDetail
            {
                RoleId = role.Id,
                RoleCode = role.Code,
                RoleName = role.Name,
                IsSuperRole = role.IsSuperRole,
                PersistedScopeIds = persistedScopeIds,
                AssignedUsers = usersByRoleId.TryGetValue(role.Id, out var assignedUsers)
                    ? assignedUsers
                    : new List<ManagedUser>()
            };
        }
    }

    public async Task<AccessOperationResult> SaveUserAsync(
        ManagedUser user,
        string? plainPassword,
        IReadOnlyCollection<long> roleIds,
        IReadOnlyCollection<long> companyIds,
        IReadOnlyCollection<long> locationIds,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (!TryBuildQualifiedTableName(_options.UsersTable, out var usersTable))
        {
            return new AccessOperationResult(false, "Invalid users table configuration.");
        }

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            return new AccessOperationResult(false, "Username is required.");
        }

        if (roleIds is null || roleIds.Count == 0)
        {
            return new AccessOperationResult(false, "Exactly one role must be selected.");
        }

        var uniqueRoleIds = roleIds
            .Where(x => x > 0)
            .Distinct()
            .ToArray();

        if (uniqueRoleIds.Length != 1)
        {
            return new AccessOperationResult(false, "A user can only have one role.");
        }

        var selectedRoleId = uniqueRoleIds[0];
        var normalizedCompanyIds = (companyIds ?? Array.Empty<long>())
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
        var normalizedLocationIds = (locationIds ?? Array.Empty<long>())
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
        long? defaultCompanyId = user.DefaultCompanyId.HasValue && user.DefaultCompanyId.Value > 0
            ? user.DefaultCompanyId.Value
            : null;
        long? defaultLocationId = user.DefaultLocationId.HasValue && user.DefaultLocationId.Value > 0
            ? user.DefaultLocationId.Value
            : null;

        if (user.Id <= 0 && string.IsNullOrWhiteSpace(plainPassword))
        {
            return new AccessOperationResult(false, "Password is required for new user.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleUserManagement,
                ResolveWriteAction(user.Id),
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola user.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var roleCode = string.Empty;
            var roleIsSuper = false;
            await using (var roleCommand = new NpgsqlCommand(@"
SELECT code,
       COALESCE(is_super_role, FALSE),
       COALESCE(is_active, TRUE)
FROM sec_roles
WHERE id = @id;", connection, transaction))
            {
                roleCommand.Parameters.AddWithValue("id", selectedRoleId);
                await using var roleReader = await roleCommand.ExecuteReaderAsync(cancellationToken);
                if (await roleReader.ReadAsync(cancellationToken))
                {
                    roleCode = roleReader.GetString(0);
                    roleIsSuper = !roleReader.IsDBNull(1) && roleReader.GetBoolean(1);
                    var isActiveRole = !roleReader.IsDBNull(2) && roleReader.GetBoolean(2);
                    if (!isActiveRole)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, "Selected role is inactive.");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(roleCode))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Selected role not found.");
            }

            if (!roleIsSuper && normalizedLocationIds.Length > 0 && normalizedCompanyIds.Length == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Select company first before assigning locations.");
            }

            if (!roleIsSuper && normalizedCompanyIds.Length == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Assign at least one company for non-super role.");
            }

            if (!roleIsSuper && normalizedLocationIds.Length == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Assign at least one location for non-super role.");
            }

            if (!roleIsSuper && !defaultCompanyId.HasValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Default company is required for non-super role.");
            }

            if (!roleIsSuper && !defaultLocationId.HasValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Default location is required for non-super role.");
            }

            if (!roleIsSuper && defaultCompanyId.HasValue && !normalizedCompanyIds.Contains(defaultCompanyId.Value))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Default company must be part of assigned companies.");
            }

            if (!roleIsSuper && defaultLocationId.HasValue && !normalizedLocationIds.Contains(defaultLocationId.Value))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Default location must be part of assigned locations.");
            }

            if (roleIsSuper)
            {
                defaultCompanyId = null;
                defaultLocationId = null;
            }

            var normalizedUsername = user.Username.Trim();
            var normalizedFullName = string.IsNullOrWhiteSpace(user.FullName) ? normalizedUsername : user.FullName.Trim();
            var normalizedEmail = string.IsNullOrWhiteSpace(user.Email) ? (object)DBNull.Value : user.Email.Trim();

            long userId;
            if (user.Id <= 0)
            {
                var passwordHash = PasswordHashUtility.CreatePbkdf2Hash(plainPassword!.Trim());
                await using var insertCommand = new NpgsqlCommand($@"
INSERT INTO {usersTable} (username, full_name, email, password_hash, is_active, created_at, updated_at)
VALUES (@username, @full_name, @email, @password_hash, @is_active, NOW(), NOW())
RETURNING id;", connection, transaction);

                insertCommand.Parameters.AddWithValue("username", normalizedUsername);
                insertCommand.Parameters.AddWithValue("full_name", normalizedFullName);
                insertCommand.Parameters.AddWithValue("email", normalizedEmail);
                insertCommand.Parameters.AddWithValue("password_hash", passwordHash);
                insertCommand.Parameters.AddWithValue("is_active", user.IsActive);
                userId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand($@"
UPDATE {usersTable}
SET username = @username,
    full_name = @full_name,
    email = @email,
    is_active = @is_active,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);

                updateCommand.Parameters.AddWithValue("id", user.Id);
                updateCommand.Parameters.AddWithValue("username", normalizedUsername);
                updateCommand.Parameters.AddWithValue("full_name", normalizedFullName);
                updateCommand.Parameters.AddWithValue("email", normalizedEmail);
                updateCommand.Parameters.AddWithValue("is_active", user.IsActive);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);

                userId = user.Id;
            }

            if (!string.IsNullOrWhiteSpace(plainPassword))
            {
                var passwordHash = PasswordHashUtility.CreatePbkdf2Hash(plainPassword.Trim());
                await using var passwordCommand = new NpgsqlCommand($@"
UPDATE {usersTable}
SET password_hash = @password_hash,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);

                passwordCommand.Parameters.AddWithValue("id", userId);
                passwordCommand.Parameters.AddWithValue("password_hash", passwordHash);
                await passwordCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var clearRoles = new NpgsqlCommand("DELETE FROM sec_user_roles WHERE user_id = @user_id;", connection, transaction))
            {
                clearRoles.Parameters.AddWithValue("user_id", userId);
                await clearRoles.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var roleInsert = new NpgsqlCommand(@"
INSERT INTO sec_user_roles (user_id, role_id)
VALUES (@user_id, @role_id)
ON CONFLICT DO NOTHING;", connection, transaction))
            {
                roleInsert.Parameters.AddWithValue("user_id", userId);
                roleInsert.Parameters.AddWithValue("role_id", selectedRoleId);
                await roleInsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var clearLocations = new NpgsqlCommand("DELETE FROM sec_user_location_access WHERE user_id = @user_id;", connection, transaction))
            {
                clearLocations.Parameters.AddWithValue("user_id", userId);
                await clearLocations.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var clearCompanies = new NpgsqlCommand("DELETE FROM sec_user_company_access WHERE user_id = @user_id;", connection, transaction))
            {
                clearCompanies.Parameters.AddWithValue("user_id", userId);
                await clearCompanies.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!roleIsSuper)
            {
                foreach (var companyId in normalizedCompanyIds)
                {
                    await using var companyInsert = new NpgsqlCommand(@"
INSERT INTO sec_user_company_access (user_id, company_id)
VALUES (@user_id, @company_id)
ON CONFLICT DO NOTHING;", connection, transaction);

                    companyInsert.Parameters.AddWithValue("user_id", userId);
                    companyInsert.Parameters.AddWithValue("company_id", companyId);
                    await companyInsert.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var locationId in normalizedLocationIds)
                {
                    await using var locationInsert = new NpgsqlCommand(@"
INSERT INTO sec_user_location_access (user_id, location_id)
SELECT @user_id, l.id
FROM org_locations l
WHERE l.id = @location_id
  AND l.company_id = ANY(@company_ids)
ON CONFLICT DO NOTHING
RETURNING location_id;", connection, transaction);

                    locationInsert.Parameters.AddWithValue("user_id", userId);
                    locationInsert.Parameters.AddWithValue("location_id", locationId);
                    locationInsert.Parameters.AddWithValue("company_ids", normalizedCompanyIds);
                    var insertedLocation = await locationInsert.ExecuteScalarAsync(cancellationToken);
                    if (insertedLocation is null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, "Location assignment must match selected companies.");
                    }
                }

                if (defaultCompanyId.HasValue && defaultLocationId.HasValue)
                {
                    await using var defaultScopeValidation = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM org_locations
WHERE id = @location_id
  AND company_id = @company_id;", connection, transaction);
                    defaultScopeValidation.Parameters.AddWithValue("location_id", defaultLocationId.Value);
                    defaultScopeValidation.Parameters.AddWithValue("company_id", defaultCompanyId.Value);
                    var isValidDefaultScope = Convert.ToInt32(await defaultScopeValidation.ExecuteScalarAsync(cancellationToken)) > 0;
                    if (!isValidDefaultScope)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new AccessOperationResult(false, "Default location must belong to default company.");
                    }
                }
            }

            await using (var updateDefaultScopeCommand = new NpgsqlCommand($@"
UPDATE {usersTable}
SET default_company_id = @default_company_id,
    default_location_id = @default_location_id,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                updateDefaultScopeCommand.Parameters.AddWithValue("id", userId);
                updateDefaultScopeCommand.Parameters.AddWithValue("default_company_id", defaultCompanyId.HasValue ? defaultCompanyId.Value : DBNull.Value);
                updateDefaultScopeCommand.Parameters.AddWithValue("default_location_id", defaultLocationId.HasValue ? defaultLocationId.Value : DBNull.Value);
                await updateDefaultScopeCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "USER",
                userId,
                user.Id <= 0 ? "CREATE_OR_UPDATE" : "UPDATE",
                actor,
                roleIsSuper
                    ? $"username={normalizedUsername};role={selectedRoleId};companies=ALL;locations=ALL;default_company=NULL;default_location=NULL;active={user.IsActive}"
                    : $"username={normalizedUsername};role={selectedRoleId};companies={string.Join(',', normalizedCompanyIds)};locations={string.Join(',', normalizedLocationIds)};default_company={defaultCompanyId};default_location={defaultLocationId};active={user.IsActive}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "User saved successfully.", userId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("SaveUserDuplicate", $"action=save_user status=duplicate username={user.Username}", ex);
            return new AccessOperationResult(false, "Duplicate username/email/role mapping detected.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            LogServiceWarning("SaveUserInvalidReference", $"action=save_user status=invalid_reference username={user.Username}", ex);
            return new AccessOperationResult(false, "Invalid role/company/location reference.");
        }
        catch (Exception ex)
        {
            LogServiceError("SaveUserFailed", $"action=save_user status=failed username={user.Username}", ex);
            return new AccessOperationResult(false, "Failed to save user.");
        }
    }

    public async Task<AccessOperationResult> SaveRoleAsync(
        ManagedRole role,
        IReadOnlyCollection<long> scopeIds,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(role.Code) || string.IsNullOrWhiteSpace(role.Name))
        {
            return new AccessOperationResult(false, "Role code and name are required.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleUserManagement,
                PermissionActionManageRoles,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola role.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            long roleId;
            if (role.Id <= 0)
            {
                await using var insert = new NpgsqlCommand(@"
INSERT INTO sec_roles (code, name, is_super_role, is_active, created_at, updated_at)
VALUES (@code, @name, @is_super_role, @is_active, NOW(), NOW())
RETURNING id;", connection, transaction);

                insert.Parameters.AddWithValue("code", role.Code.Trim().ToUpperInvariant());
                insert.Parameters.AddWithValue("name", role.Name.Trim());
                insert.Parameters.AddWithValue("is_super_role", role.IsSuperRole);
                insert.Parameters.AddWithValue("is_active", role.IsActive);
                roleId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var guardCommand = new NpgsqlCommand(@"
SELECT code, is_super_role
FROM sec_roles
WHERE id = @id;", connection, transaction);
                guardCommand.Parameters.AddWithValue("id", role.Id);

                string? existingCode = null;
                var existingSuper = false;
                await using (var reader = await guardCommand.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        existingCode = reader.GetString(0);
                        existingSuper = !reader.IsDBNull(1) && reader.GetBoolean(1);
                    }
                }

                if (string.IsNullOrWhiteSpace(existingCode))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Role not found.");
                }

                if (string.Equals(existingCode, SuperAdminCode, StringComparison.OrdinalIgnoreCase) || existingSuper)
                {
                    role.Code = SuperAdminCode;
                    role.IsSuperRole = true;
                    role.IsActive = true;
                }

                await using var update = new NpgsqlCommand(@"
UPDATE sec_roles
SET code = @code,
    name = @name,
    is_super_role = @is_super_role,
    is_active = @is_active,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);

                update.Parameters.AddWithValue("id", role.Id);
                update.Parameters.AddWithValue("code", role.Code.Trim().ToUpperInvariant());
                update.Parameters.AddWithValue("name", role.Name.Trim());
                update.Parameters.AddWithValue("is_super_role", role.IsSuperRole);
                update.Parameters.AddWithValue("is_active", role.IsActive);
                await update.ExecuteNonQueryAsync(cancellationToken);
                roleId = role.Id;
            }

            await ClearRoleAccessAsync(connection, transaction, roleId, cancellationToken);

            if (!role.IsSuperRole)
            {
                foreach (var scopeId in scopeIds.Distinct())
                {
                    await using var scopeInsert = new NpgsqlCommand(@"
INSERT INTO sec_role_action_access (role_id, action_id)
VALUES (@role_id, @action_id)
ON CONFLICT DO NOTHING;", connection, transaction);

                    scopeInsert.Parameters.AddWithValue("role_id", roleId);
                    scopeInsert.Parameters.AddWithValue("action_id", scopeId);
                    await scopeInsert.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "ROLE",
                roleId,
                role.Id <= 0 ? "CREATE_OR_UPDATE" : "UPDATE",
                actor,
                $"code={role.Code};scopes={string.Join(',', scopeIds.Distinct())};active={role.IsActive};super={role.IsSuperRole}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Role saved successfully.", roleId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("SaveRoleDuplicate", $"action=save_role status=duplicate code={role.Code}", ex);
            return new AccessOperationResult(false, "Duplicate role code or duplicate access mapping detected.");
        }
        catch (Exception ex)
        {
            LogServiceError("SaveRoleFailed", $"action=save_role status=failed code={role.Code}", ex);
            return new AccessOperationResult(false, "Failed to save role.");
        }
    }

    public async Task<AccessOperationResult> CloneRoleAsync(
        long sourceRoleId,
        string newCode,
        string newName,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (sourceRoleId <= 0)
        {
            return new AccessOperationResult(false, "Role sumber tidak valid.");
        }

        if (string.IsNullOrWhiteSpace(newCode) || string.IsNullOrWhiteSpace(newName))
        {
            return new AccessOperationResult(false, "Kode dan nama role baru wajib diisi.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleUserManagement,
                PermissionActionManageRoles,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola role.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            string? sourceCode = null;
            var sourceIsSuperRole = false;
            var sourceIsActive = true;

            await using (var sourceCommand = new NpgsqlCommand(@"
SELECT code,
       COALESCE(is_super_role, FALSE),
       COALESCE(is_active, TRUE)
FROM sec_roles
WHERE id = @id;", connection, transaction))
            {
                sourceCommand.Parameters.AddWithValue("id", sourceRoleId);
                await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    sourceCode = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    sourceIsSuperRole = !reader.IsDBNull(1) && reader.GetBoolean(1);
                    sourceIsActive = !reader.IsDBNull(2) && reader.GetBoolean(2);
                }
            }

            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Role sumber tidak ditemukan.");
            }

            var normalizedCode = newCode.Trim().ToUpperInvariant();
            var normalizedName = newName.Trim();

            long clonedRoleId;
            await using (var insertRole = new NpgsqlCommand(@"
INSERT INTO sec_roles (code, name, is_super_role, is_active, created_at, updated_at)
VALUES (@code, @name, @is_super_role, @is_active, NOW(), NOW())
RETURNING id;", connection, transaction))
            {
                insertRole.Parameters.AddWithValue("code", normalizedCode);
                insertRole.Parameters.AddWithValue("name", normalizedName);
                insertRole.Parameters.AddWithValue("is_super_role", sourceIsSuperRole);
                insertRole.Parameters.AddWithValue("is_active", sourceIsActive);
                clonedRoleId = Convert.ToInt64(await insertRole.ExecuteScalarAsync(cancellationToken));
            }

            await using (var cloneAccess = new NpgsqlCommand(@"
INSERT INTO sec_role_action_access (role_id, action_id)
SELECT @target_role_id, src.action_id
FROM sec_role_action_access src
WHERE src.role_id = @source_role_id
ON CONFLICT DO NOTHING;", connection, transaction))
            {
                cloneAccess.Parameters.AddWithValue("target_role_id", clonedRoleId);
                cloneAccess.Parameters.AddWithValue("source_role_id", sourceRoleId);
                await cloneAccess.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "ROLE",
                clonedRoleId,
                "CLONE",
                actor,
                $"source_role_id={sourceRoleId};source_code={sourceCode};new_code={normalizedCode}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Role berhasil diduplikasi.", clonedRoleId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("CloneRoleDuplicate", $"action=clone_role status=duplicate source_role_id={sourceRoleId} new_code={newCode}", ex);
            return new AccessOperationResult(false, "Kode role baru sudah digunakan.");
        }
        catch (Exception ex)
        {
            LogServiceError("CloneRoleFailed", $"action=clone_role status=failed source_role_id={sourceRoleId}", ex);
            return new AccessOperationResult(false, "Gagal menduplikasi role.");
        }
    }

    public async Task<AccessOperationResult> DeleteRoleAsync(long roleId, string actorUsername, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (roleId <= 0)
        {
            return new AccessOperationResult(false, "Invalid role selected.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleUserManagement,
                PermissionActionManageRoles,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola role.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using var infoCommand = new NpgsqlCommand(@"
SELECT code, is_super_role
FROM sec_roles
WHERE id = @id;", connection, transaction);
            infoCommand.Parameters.AddWithValue("id", roleId);

            string? roleCode = null;
            var isSuper = false;
            await using (var reader = await infoCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    roleCode = reader.GetString(0);
                    isSuper = !reader.IsDBNull(1) && reader.GetBoolean(1);
                }
            }

            if (string.IsNullOrWhiteSpace(roleCode))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Role not found.");
            }

            if (isSuper || string.Equals(roleCode, SuperAdminCode, StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "SUPER_ADMIN role is protected and cannot be deleted.");
            }

            await using (var usageCommand = new NpgsqlCommand("SELECT COUNT(1) FROM sec_user_roles WHERE role_id = @id;", connection, transaction))
            {
                usageCommand.Parameters.AddWithValue("id", roleId);
                var usageCount = Convert.ToInt32(await usageCommand.ExecuteScalarAsync(cancellationToken));
                if (usageCount > 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Role is still assigned to users. Unassign first before delete.");
                }
            }

            await using (var deleteCommand = new NpgsqlCommand("DELETE FROM sec_roles WHERE id = @id;", connection, transaction))
            {
                deleteCommand.Parameters.AddWithValue("id", roleId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "ROLE",
                roleId,
                "DELETE",
                actor,
                $"code={roleCode}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Role deleted successfully.", roleId);
        }
        catch (Exception ex)
        {
            LogServiceError("DeleteRoleFailed", $"action=delete_role status=failed role_id={roleId}", ex);
            return new AccessOperationResult(false, "Failed to delete role.");
        }
    }

    public async Task<AccessOperationResult> SaveCompanyAsync(
        ManagedCompany company,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(company.Code) || string.IsNullOrWhiteSpace(company.Name))
        {
            return new AccessOperationResult(false, "Company code and name are required.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleUserManagement,
                PermissionActionManageCompanies,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola company.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            var normalizedCode = company.Code.Trim().ToUpperInvariant();
            var normalizedName = company.Name.Trim();

            long companyId;
            if (company.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO org_companies (code, name, is_active, created_at, updated_at)
VALUES (@code, @name, @is_active, NOW(), NOW())
RETURNING id;", connection, transaction);

                insertCommand.Parameters.AddWithValue("code", normalizedCode);
                insertCommand.Parameters.AddWithValue("name", normalizedName);
                insertCommand.Parameters.AddWithValue("is_active", company.IsActive);
                companyId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE org_companies
SET code = @code,
    name = @name,
    is_active = @is_active,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);

                updateCommand.Parameters.AddWithValue("id", company.Id);
                updateCommand.Parameters.AddWithValue("code", normalizedCode);
                updateCommand.Parameters.AddWithValue("name", normalizedName);
                updateCommand.Parameters.AddWithValue("is_active", company.IsActive);
                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Company not found.");
                }

                companyId = company.Id;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "COMPANY",
                companyId,
                company.Id <= 0 ? "CREATE_OR_UPDATE" : "UPDATE",
                actor,
                $"code={normalizedCode};name={normalizedName};active={company.IsActive}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Company saved successfully.", companyId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("SaveCompanyDuplicate", $"action=save_company status=duplicate code={company.Code}", ex);
            return new AccessOperationResult(false, "Duplicate company code detected.");
        }
        catch (Exception ex)
        {
            LogServiceError("SaveCompanyFailed", $"action=save_company status=failed code={company.Code}", ex);
            return new AccessOperationResult(false, "Failed to save company.");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteCompanyAsync(
        long companyId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (companyId <= 0)
        {
            return new AccessOperationResult(false, "Invalid company selected.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleUserManagement,
                PermissionActionManageCompanies,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola company.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using var lookupCommand = new NpgsqlCommand(@"
SELECT code, name, is_active
FROM org_companies
WHERE id = @id;", connection, transaction);
            lookupCommand.Parameters.AddWithValue("id", companyId);

            string? companyCode = null;
            string? companyName = null;
            var isActive = false;
            await using (var reader = await lookupCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    companyCode = reader.GetString(0);
                    companyName = reader.GetString(1);
                    isActive = !reader.IsDBNull(2) && reader.GetBoolean(2);
                }
            }

            if (string.IsNullOrWhiteSpace(companyCode))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Company not found.");
            }

            if (isActive)
            {
                await using var deactivateCompanyCommand = new NpgsqlCommand(@"
UPDATE org_companies
SET is_active = FALSE,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);
                deactivateCompanyCommand.Parameters.AddWithValue("id", companyId);
                await deactivateCompanyCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var deactivatedLocationCount = 0;
            await using (var deactivateLocationsCommand = new NpgsqlCommand(@"
UPDATE org_locations
SET is_active = FALSE,
    updated_at = NOW()
WHERE company_id = @company_id
  AND is_active = TRUE;", connection, transaction))
            {
                deactivateLocationsCommand.Parameters.AddWithValue("company_id", companyId);
                deactivatedLocationCount = await deactivateLocationsCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "COMPANY",
                companyId,
                "SOFT_DELETE",
                actor,
                $"code={companyCode};name={companyName};deactivated_locations={deactivatedLocationCount}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Company marked as inactive.", companyId);
        }
        catch (Exception ex)
        {
            LogServiceError("SoftDeleteCompanyFailed", $"action=deactivate_company status=failed company_id={companyId}", ex);
            return new AccessOperationResult(false, "Failed to deactivate company.");
        }
    }

    public async Task<AccessOperationResult> SaveLocationAsync(
        ManagedLocation location,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (location.CompanyId <= 0)
        {
            return new AccessOperationResult(false, "Location company is required.");
        }

        if (string.IsNullOrWhiteSpace(location.Code) || string.IsNullOrWhiteSpace(location.Name))
        {
            return new AccessOperationResult(false, "Location code and name are required.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleUserManagement,
                PermissionActionManageLocations,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola lokasi.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using var companyLookupCommand = new NpgsqlCommand(@"
SELECT code, name
FROM org_companies
WHERE id = @id;", connection, transaction);
            companyLookupCommand.Parameters.AddWithValue("id", location.CompanyId);

            string? companyCode = null;
            string? companyName = null;
            await using (var reader = await companyLookupCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    companyCode = reader.GetString(0);
                    companyName = reader.GetString(1);
                }
            }

            if (string.IsNullOrWhiteSpace(companyCode))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Company for location not found.");
            }

            var normalizedCode = location.Code.Trim().ToUpperInvariant();
            var normalizedName = location.Name.Trim();
            var normalizedLocationType = string.IsNullOrWhiteSpace(location.LocationType)
                ? "OFFICE"
                : location.LocationType.Trim().ToUpperInvariant();
            if (normalizedLocationType is not ("ESTATE" or "MILL" or "OFFICE"))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Location type must be ESTATE, MILL, or OFFICE.");
            }

            long locationId;
            if (location.Id <= 0)
            {
                await using var insertCommand = new NpgsqlCommand(@"
INSERT INTO org_locations (company_id, code, name, location_type, is_active, created_at, updated_at)
VALUES (@company_id, @code, @name, @location_type, @is_active, NOW(), NOW())
RETURNING id;", connection, transaction);

                insertCommand.Parameters.AddWithValue("company_id", location.CompanyId);
                insertCommand.Parameters.AddWithValue("code", normalizedCode);
                insertCommand.Parameters.AddWithValue("name", normalizedName);
                insertCommand.Parameters.AddWithValue("location_type", normalizedLocationType);
                insertCommand.Parameters.AddWithValue("is_active", location.IsActive);
                locationId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var updateCommand = new NpgsqlCommand(@"
UPDATE org_locations
SET company_id = @company_id,
    code = @code,
    name = @name,
    location_type = @location_type,
    is_active = @is_active,
    updated_at = NOW()
WHERE id = @id;", connection, transaction);

                updateCommand.Parameters.AddWithValue("id", location.Id);
                updateCommand.Parameters.AddWithValue("company_id", location.CompanyId);
                updateCommand.Parameters.AddWithValue("code", normalizedCode);
                updateCommand.Parameters.AddWithValue("name", normalizedName);
                updateCommand.Parameters.AddWithValue("location_type", normalizedLocationType);
                updateCommand.Parameters.AddWithValue("is_active", location.IsActive);
                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                if (affected <= 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new AccessOperationResult(false, "Location not found.");
                }

                locationId = location.Id;
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "LOCATION",
                locationId,
                location.Id <= 0 ? "CREATE_OR_UPDATE" : "UPDATE",
                actor,
                $"company_id={location.CompanyId};company_code={companyCode};company_name={companyName};code={normalizedCode};name={normalizedName};type={normalizedLocationType};active={location.IsActive}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Location saved successfully.", locationId);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            LogServiceWarning("SaveLocationDuplicate", $"action=save_location status=duplicate company_id={location.CompanyId} code={location.Code}", ex);
            return new AccessOperationResult(false, "Duplicate location code for selected company.");
        }
        catch (Exception ex)
        {
            LogServiceError("SaveLocationFailed", $"action=save_location status=failed company_id={location.CompanyId} code={location.Code}", ex);
            return new AccessOperationResult(false, "Failed to save location.");
        }
    }

    public async Task<AccessOperationResult> SoftDeleteLocationAsync(
        long locationId,
        string actorUsername,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (locationId <= 0)
        {
            return new AccessOperationResult(false, "Invalid location selected.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var actor = NormalizeActor(actorUsername);
            var permissionFailure = await EnsurePermissionAsync(
                connection,
                transaction,
                actor,
                AccountingModuleCode,
                AccountingSubmoduleUserManagement,
                PermissionActionManageLocations,
                null,
                null,
                cancellationToken,
                "Anda tidak memiliki izin untuk mengelola lokasi.");
            if (permissionFailure is not null)
            {
                return permissionFailure;
            }

            await using var lookupCommand = new NpgsqlCommand(@"
SELECT l.code,
       l.name,
       c.code AS company_code
FROM org_locations l
JOIN org_companies c ON c.id = l.company_id
WHERE l.id = @id;", connection, transaction);
            lookupCommand.Parameters.AddWithValue("id", locationId);

            string? locationCode = null;
            string? locationName = null;
            string? companyCode = null;
            await using (var reader = await lookupCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    locationCode = reader.GetString(0);
                    locationName = reader.GetString(1);
                    companyCode = reader.GetString(2);
                }
            }

            if (string.IsNullOrWhiteSpace(locationCode))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AccessOperationResult(false, "Location not found.");
            }

            await using (var deactivateCommand = new NpgsqlCommand(@"
UPDATE org_locations
SET is_active = FALSE,
    updated_at = NOW()
WHERE id = @id;", connection, transaction))
            {
                deactivateCommand.Parameters.AddWithValue("id", locationId);
                await deactivateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditLogAsync(
                connection,
                transaction,
                "LOCATION",
                locationId,
                "SOFT_DELETE",
                actor,
                $"company_code={companyCode};code={locationCode};name={locationName}",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AccessOperationResult(true, "Location marked as inactive.", locationId);
        }
        catch (Exception ex)
        {
            LogServiceError("SoftDeleteLocationFailed", $"action=deactivate_location status=failed location_id={locationId}", ex);
            return new AccessOperationResult(false, "Failed to deactivate location.");
        }
    }
}


