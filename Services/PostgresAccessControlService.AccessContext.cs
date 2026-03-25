using System.Text;
using System.Text.RegularExpressions;
using Accounting.Infrastructure.Logging;
using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService : IAccessControlService
{
    public async Task<UserAccessContext?> GetUserAccessContextAsync(string username, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        if (!TryBuildQualifiedTableName(_options.UsersTable, out var usersTable))
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $@"
SELECT u.id,
       u.username,
       r.code AS role_code,
       COALESCE(r.is_super_role, FALSE) AS is_super_role,
       p.module_code,
       p.submodule_code,
       p.action_code,
       c.id AS company_id,
       l.id AS location_id,
       u.default_company_id,
       u.default_location_id
FROM {usersTable} u
LEFT JOIN sec_user_roles ur ON ur.user_id = u.id
LEFT JOIN sec_roles r ON r.id = ur.role_id AND r.is_active = TRUE
LEFT JOIN vw_user_effective_permissions p ON p.user_id = u.id
LEFT JOIN sec_user_company_access uca ON uca.user_id = u.id
LEFT JOIN org_companies c ON c.id = uca.company_id AND c.is_active = TRUE
LEFT JOIN sec_user_location_access ula ON ula.user_id = u.id
LEFT JOIN org_locations l ON l.id = ula.location_id AND l.is_active = TRUE
WHERE lower(u.username) = lower(@username)
  AND u.is_active = TRUE;";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = _options.QueryTimeoutSeconds
        };
        command.Parameters.AddWithValue("username", username.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        long userId = 0;
        var resolvedUsername = string.Empty;
        var hasRows = false;
        var isSuperRole = false;
        long? defaultCompanyId = null;
        long? defaultLocationId = null;

        var roleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var submoduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scopeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var companyIds = new HashSet<long>();
        var locationIds = new HashSet<long>();

        while (await reader.ReadAsync(cancellationToken))
        {
            hasRows = true;
            userId = reader.GetInt64(0);
            resolvedUsername = reader.GetString(1);

            if (!reader.IsDBNull(2))
            {
                roleCodes.Add(reader.GetString(2));
            }

            if (!reader.IsDBNull(3) && reader.GetBoolean(3))
            {
                isSuperRole = true;
            }

            if (!reader.IsDBNull(4) && !reader.IsDBNull(5) && !reader.IsDBNull(6))
            {
                var moduleCode = reader.GetString(4);
                var submoduleCode = reader.GetString(5);
                var actionCode = reader.GetString(6);
                moduleCodes.Add(moduleCode);
                submoduleCodes.Add($"{moduleCode}.{submoduleCode}");
                actionCodes.Add($"{moduleCode}.{submoduleCode}.{actionCode}");
            }

            if (!reader.IsDBNull(7))
            {
                companyIds.Add(reader.GetInt64(7));
            }

            if (!reader.IsDBNull(8))
            {
                locationIds.Add(reader.GetInt64(8));
            }

            if (!reader.IsDBNull(9))
            {
                defaultCompanyId = reader.GetInt64(9);
            }

            if (!reader.IsDBNull(10))
            {
                defaultLocationId = reader.GetInt64(10);
            }
        }

        if (!hasRows)
        {
            return null;
        }

        if (isSuperRole)
        {
            await reader.DisposeAsync();
            var fullPermissionCodes = await LoadAllPermissionCodesAsync(connection, cancellationToken);
            moduleCodes = fullPermissionCodes.ModuleCodes;
            submoduleCodes = fullPermissionCodes.SubmoduleCodes;
            actionCodes = fullPermissionCodes.ActionCodes;
            scopeCodes = BuildLegacyScopeCodes(submoduleCodes, actionCodes);
            companyIds = await LoadAllCompanyIdsAsync(connection, cancellationToken);
            locationIds = await LoadAllLocationIdsAsync(connection, cancellationToken);
        }
        else
        {
            scopeCodes = BuildLegacyScopeCodes(submoduleCodes, actionCodes);
        }

        if (companyIds.Count > 0 && (!defaultCompanyId.HasValue || !companyIds.Contains(defaultCompanyId.Value)))
        {
            defaultCompanyId = companyIds.OrderBy(x => x).FirstOrDefault();
        }

        if (locationIds.Count > 0 && (!defaultLocationId.HasValue || !locationIds.Contains(defaultLocationId.Value)))
        {
            defaultLocationId = locationIds.OrderBy(x => x).FirstOrDefault();
        }

        var selectedCompanyId = defaultCompanyId ?? 0;
        var selectedLocationId = defaultLocationId ?? 0;

        return new UserAccessContext
        {
            UserId = userId,
            Username = resolvedUsername,
            SelectedCompanyId = selectedCompanyId,
            SelectedLocationId = selectedLocationId,
            DefaultCompanyId = defaultCompanyId,
            DefaultLocationId = defaultLocationId,
            IsSuperRole = isSuperRole,
            RoleCodes = roleCodes,
            ModuleCodes = moduleCodes,
            SubmoduleCodes = submoduleCodes,
            ActionCodes = actionCodes,
            ScopeCodes = scopeCodes,
            CompanyIds = companyIds,
            LocationIds = locationIds,
            AllowedCompanyIds = new HashSet<long>(companyIds),
            AllowedLocationIds = new HashSet<long>(locationIds)
        };
    }

    public async Task<LoginAccessOptions?> GetLoginAccessOptionsAsync(string username, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        if (!TryBuildQualifiedTableName(_options.UsersTable, out var usersTable))
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $@"
SELECT u.id,
       u.username,
       u.default_company_id,
       u.default_location_id,
       r.id AS role_id,
       r.code AS role_code,
       r.name AS role_name,
       COALESCE(r.is_super_role, FALSE) AS is_super_role,
       mo.module_code,
       sm.submodule_code,
       a.action_code
FROM {usersTable} u
JOIN sec_user_roles ur ON ur.user_id = u.id
JOIN sec_roles r ON r.id = ur.role_id AND r.is_active = TRUE
LEFT JOIN sec_role_action_access raa ON raa.role_id = r.id
LEFT JOIN sec_actions a ON a.id = raa.action_id AND a.is_active = TRUE
LEFT JOIN sec_submodules sm ON sm.id = a.submodule_id AND sm.is_active = TRUE
LEFT JOIN sec_modules mo ON mo.id = sm.module_id AND mo.is_active = TRUE
WHERE lower(u.username) = lower(@username)
  AND u.is_active = TRUE;";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = _options.QueryTimeoutSeconds
        };
        command.Parameters.AddWithValue("username", username.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var hasRows = false;
        long userId = 0;
        var resolvedUsername = string.Empty;
        long? defaultCompanyId = null;
        long? defaultLocationId = null;

        var roleById = new Dictionary<long, ManagedRole>();
        var scopeMap = new Dictionary<long, HashSet<string>>();
        var moduleMap = new Dictionary<long, HashSet<string>>();
        var submoduleMap = new Dictionary<long, HashSet<string>>();
        var actionMap = new Dictionary<long, HashSet<string>>();

        while (await reader.ReadAsync(cancellationToken))
        {
            hasRows = true;
            userId = reader.GetInt64(0);
            resolvedUsername = reader.GetString(1);
            if (!reader.IsDBNull(2))
            {
                defaultCompanyId = reader.GetInt64(2);
            }
            if (!reader.IsDBNull(3))
            {
                defaultLocationId = reader.GetInt64(3);
            }

            var roleId = reader.GetInt64(4);
            if (!roleById.TryGetValue(roleId, out var role))
            {
                role = new ManagedRole
                {
                    Id = roleId,
                    Code = reader.GetString(5),
                    Name = reader.GetString(6),
                    IsSuperRole = !reader.IsDBNull(7) && reader.GetBoolean(7),
                    IsActive = true
                };
                roleById[roleId] = role;
                scopeMap[roleId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                moduleMap[roleId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                submoduleMap[roleId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                actionMap[roleId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (!reader.IsDBNull(8) && !reader.IsDBNull(9) && !reader.IsDBNull(10))
            {
                var moduleCode = reader.GetString(8);
                var submoduleCode = reader.GetString(9);
                var actionCode = reader.GetString(10);
                moduleMap[roleId].Add(moduleCode);
                submoduleMap[roleId].Add($"{moduleCode}.{submoduleCode}");
                actionMap[roleId].Add($"{moduleCode}.{submoduleCode}.{actionCode}");
            }
        }

        if (!hasRows || roleById.Count == 0)
        {
            return null;
        }

        await reader.DisposeAsync();

        var activeCompanies = await LoadActiveCompaniesAsync(connection, cancellationToken);
        var activeLocations = await LoadActiveLocationsAsync(connection, cancellationToken);
        var allPermissionCodes = await LoadAllPermissionCodesAsync(connection, cancellationToken);

        var companiesById = activeCompanies.ToDictionary(x => x.Id);
        var locationsById = activeLocations.ToDictionary(x => x.Id);
        var allCompanyIds = activeCompanies.Select(x => x.Id).ToHashSet();
        var allLocationIds = activeLocations.Select(x => x.Id).ToHashSet();
        var hasSuperRole = roleById.Values.Any(x => x.IsSuperRole);

        var userCompanyIds = new HashSet<long>();
        var userLocationIds = new HashSet<long>();

        if (hasSuperRole)
        {
            userCompanyIds = allCompanyIds;
            userLocationIds = allLocationIds;
        }
        else
        {
            await FillSingleSetAsync(
                connection,
                "SELECT company_id FROM sec_user_company_access WHERE user_id = @user_id;",
                "user_id",
                userId,
                userCompanyIds,
                cancellationToken);

            await FillSingleSetAsync(
                connection,
                "SELECT location_id FROM sec_user_location_access WHERE user_id = @user_id;",
                "user_id",
                userId,
                userLocationIds,
                cancellationToken);

            userCompanyIds.RemoveWhere(x => !companiesById.ContainsKey(x));
            userLocationIds.RemoveWhere(x => !locationsById.ContainsKey(x));
            userLocationIds.RemoveWhere(x =>
                !locationsById.TryGetValue(x, out var location) ||
                !userCompanyIds.Contains(location.CompanyId));
        }

        if (userCompanyIds.Count > 0 && (!defaultCompanyId.HasValue || !userCompanyIds.Contains(defaultCompanyId.Value)))
        {
            defaultCompanyId = userCompanyIds.OrderBy(x => x).FirstOrDefault();
        }

        if (userLocationIds.Count > 0)
        {
            if (!defaultLocationId.HasValue || !userLocationIds.Contains(defaultLocationId.Value))
            {
                defaultLocationId = userLocationIds
                    .OrderBy(x => x)
                    .FirstOrDefault();
            }

            if (defaultLocationId.HasValue &&
                locationsById.TryGetValue(defaultLocationId.Value, out var defaultLocation) &&
                (!defaultCompanyId.HasValue || defaultLocation.CompanyId != defaultCompanyId.Value))
            {
                defaultCompanyId = defaultLocation.CompanyId;
            }
        }

        foreach (var role in roleById.Values)
        {
            if (!scopeMap.TryGetValue(role.Id, out _))
            {
                scopeMap[role.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            if (!moduleMap.TryGetValue(role.Id, out var moduleCodes))
            {
                moduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                moduleMap[role.Id] = moduleCodes;
            }
            if (!submoduleMap.TryGetValue(role.Id, out var submoduleCodes))
            {
                submoduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                submoduleMap[role.Id] = submoduleCodes;
            }
            if (!actionMap.TryGetValue(role.Id, out var actionCodes))
            {
                actionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                actionMap[role.Id] = actionCodes;
            }

            if (role.IsSuperRole)
            {
                moduleMap[role.Id] = new HashSet<string>(allPermissionCodes.ModuleCodes, StringComparer.OrdinalIgnoreCase);
                submoduleMap[role.Id] = new HashSet<string>(allPermissionCodes.SubmoduleCodes, StringComparer.OrdinalIgnoreCase);
                actionMap[role.Id] = new HashSet<string>(allPermissionCodes.ActionCodes, StringComparer.OrdinalIgnoreCase);
                scopeMap[role.Id] = BuildLegacyScopeCodes(submoduleMap[role.Id], actionMap[role.Id]);
                continue;
            }

            moduleCodes.RemoveWhere(x => !allPermissionCodes.ModuleCodes.Contains(x));
            submoduleCodes.RemoveWhere(x => !allPermissionCodes.SubmoduleCodes.Contains(x));
            actionCodes.RemoveWhere(x => !allPermissionCodes.ActionCodes.Contains(x));
            scopeMap[role.Id] = BuildLegacyScopeCodes(submoduleCodes, actionCodes);
        }

        return new LoginAccessOptions
        {
            UserId = userId,
            Username = resolvedUsername,
            DefaultCompanyId = defaultCompanyId,
            DefaultLocationId = defaultLocationId,
            Roles = roleById.Values.OrderBy(x => x.Code).ToList(),
            Companies = activeCompanies.Where(x => userCompanyIds.Contains(x.Id)).OrderBy(x => x.Code).ToList(),
            Locations = activeLocations.Where(x => userLocationIds.Contains(x.Id)).OrderBy(x => x.CompanyCode).ThenBy(x => x.Code).ToList(),
            ScopeCodesByRoleId = scopeMap,
            ModuleCodesByRoleId = moduleMap,
            SubmoduleCodesByRoleId = submoduleMap,
            ActionCodesByRoleId = actionMap,
            CompanyIdsByUserId = new Dictionary<long, HashSet<long>> { [userId] = userCompanyIds },
            LocationIdsByUserId = new Dictionary<long, HashSet<long>> { [userId] = userLocationIds }
        };
    }
}
