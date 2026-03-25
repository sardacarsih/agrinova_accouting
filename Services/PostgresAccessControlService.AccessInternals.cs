using System.Text;
using System.Text.RegularExpressions;
using Accounting.Infrastructure.Logging;
using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService : IAccessControlService
{
    private static void LogServiceWarning(string eventName, string message, Exception ex)
    {
        AppServices.Logger.LogWarning(nameof(PostgresAccessControlService), eventName, message, ex);
    }

    private static void LogServiceError(string eventName, string message, Exception ex)
    {
        AppServices.Logger.LogError(nameof(PostgresAccessControlService), eventName, message, ex);
    }

    private async Task<HashSet<string>> LoadAllScopeCodesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var fullPermissionCodes = await LoadAllPermissionCodesAsync(connection, cancellationToken);
        return BuildLegacyScopeCodes(fullPermissionCodes.SubmoduleCodes, fullPermissionCodes.ActionCodes);
    }

    private async Task<(HashSet<string> ModuleCodes, HashSet<string> SubmoduleCodes, HashSet<string> ActionCodes)> LoadAllPermissionCodesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var moduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var submoduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = new NpgsqlCommand(@"
SELECT mo.module_code,
       sm.submodule_code,
       a.action_code
FROM sec_actions a
JOIN sec_submodules sm ON sm.id = a.submodule_id
JOIN sec_modules mo ON mo.id = sm.module_id
WHERE mo.is_active = TRUE
  AND sm.is_active = TRUE
  AND a.is_active = TRUE;", connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var moduleCode = reader.GetString(0);
            var submoduleCode = reader.GetString(1);
            var actionCode = reader.GetString(2);

            moduleCodes.Add(moduleCode);
            submoduleCodes.Add($"{moduleCode}.{submoduleCode}");
            actionCodes.Add($"{moduleCode}.{submoduleCode}.{actionCode}");
        }

        return (moduleCodes, submoduleCodes, actionCodes);
    }

    private static HashSet<string> BuildLegacyScopeCodes(IEnumerable<string> submoduleCodes, IEnumerable<string> actionCodes)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var submoduleSet = new HashSet<string>(submoduleCodes, StringComparer.OrdinalIgnoreCase);
        var actionSet = new HashSet<string>(actionCodes, StringComparer.OrdinalIgnoreCase);

        if (submoduleSet.Contains("accounting.dashboard") || submoduleSet.Contains("inventory.dashboard"))
        {
            output.Add("dashboard");
        }
        if (submoduleSet.Contains("accounting.master_data"))
        {
            output.Add("master_data");
        }
        if (submoduleSet.Contains("accounting.transactions"))
        {
            output.Add("transactions");
        }
        if (submoduleSet.Contains("accounting.reports"))
        {
            output.Add("reports");
        }
        if (submoduleSet.Contains("accounting.settings") || submoduleSet.Contains("inventory.api_inv"))
        {
            output.Add("settings");
        }
        if (submoduleSet.Contains("accounting.user_management"))
        {
            output.Add("user_management");
        }
        if (submoduleSet.Any(x => x.StartsWith("inventory.", StringComparison.OrdinalIgnoreCase)))
        {
            output.Add("inventory");
        }
        if (submoduleSet.Any(x => x.StartsWith("fixed_asset.", StringComparison.OrdinalIgnoreCase)))
        {
            output.Add("fixed_asset");
        }
        if (actionSet.Any(x => x.EndsWith(".approve", StringComparison.OrdinalIgnoreCase) ||
                               x.EndsWith(".post", StringComparison.OrdinalIgnoreCase)))
        {
            output.Add("approve");
        }

        return output;
    }

    private async Task<HashSet<long>> LoadAllCompanyIdsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var output = new HashSet<long>();

        await using var command = new NpgsqlCommand(@"
SELECT id
FROM org_companies
WHERE is_active = TRUE;", connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(reader.GetInt64(0));
        }

        return output;
    }

    private static async Task<List<ManagedCompany>> LoadActiveCompaniesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var output = new List<ManagedCompany>();

        await using var command = new NpgsqlCommand(@"
SELECT id, code, name, is_active
FROM org_companies
WHERE is_active = TRUE
ORDER BY code;", connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedCompany
            {
                Id = reader.GetInt64(0),
                Code = reader.GetString(1),
                Name = reader.GetString(2),
                IsActive = !reader.IsDBNull(3) && reader.GetBoolean(3)
            });
        }

        return output;
    }

    private async Task<HashSet<long>> LoadAllLocationIdsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var output = new HashSet<long>();

        await using var command = new NpgsqlCommand(@"
SELECT id
FROM org_locations
WHERE is_active = TRUE;", connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(reader.GetInt64(0));
        }

        return output;
    }

    private static async Task<List<ManagedLocation>> LoadActiveLocationsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var output = new List<ManagedLocation>();

        await using var command = new NpgsqlCommand(@"
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
WHERE l.is_active = TRUE
  AND c.is_active = TRUE
ORDER BY c.code, l.code;", connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedLocation
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

        return output;
    }

    private static async Task FillMapAsync(
        NpgsqlConnection connection,
        string sql,
        Dictionary<long, HashSet<long>> map,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetInt64(0);
            var value = reader.GetInt64(1);

            if (!map.TryGetValue(key, out var bucket))
            {
                bucket = new HashSet<long>();
                map[key] = bucket;
            }

            bucket.Add(value);
        }
    }

    private static async Task FillSingleSetAsync(
        NpgsqlConnection connection,
        string sql,
        string parameterName,
        long parameterValue,
        HashSet<long> output,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameterName, parameterValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(reader.GetInt64(0));
        }
    }

    private static async Task ClearRoleAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long roleId,
        CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            "DELETE FROM sec_role_action_access WHERE role_id = @role_id;"
        };

        foreach (var sql in statements)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("role_id", roleId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertAuditLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string entityType,
        long entityId,
        string action,
        string actorUsername,
        string details,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(@"
INSERT INTO sec_audit_logs (entity_type, entity_id, action, actor_username, details, created_at)
VALUES (@entity_type, @entity_id, @action, @actor_username, @details, NOW());", connection, transaction);

        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("entity_id", entityId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("actor_username", actorUsername);
        command.Parameters.AddWithValue("details", details);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeActor(string actorUsername)
    {
        return string.IsNullOrWhiteSpace(actorUsername) ? "SYSTEM" : actorUsername.Trim();
    }
}
