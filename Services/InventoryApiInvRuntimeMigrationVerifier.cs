using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed record InventoryApiInvRuntimeMigrationVerificationResult(
    bool ShouldWarn,
    string Message,
    IReadOnlyList<string> MissingActionCodes,
    bool WasSkipped = false);

public static class InventoryApiInvRuntimeMigrationVerifier
{
    private static readonly string[] RequiredActionCodes =
    [
        "download_import_template",
        "import_master_data"
    ];

    public static async Task<InventoryApiInvRuntimeMigrationVerificationResult> VerifyAsync(
        DatabaseAuthOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString) ||
            options.ConnectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            return new InventoryApiInvRuntimeMigrationVerificationResult(
                false,
                "Runtime migration verification skipped because the database connection is not configured.",
                [],
                true);
        }

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT lower(a.action_code)
            FROM sec_actions a
            JOIN sec_submodules sm ON sm.id = a.submodule_id
            JOIN sec_modules mo ON mo.id = sm.module_id
            WHERE lower(mo.module_code) = 'inventory'
              AND lower(sm.submodule_code) = 'api_inv'
              AND a.is_active = TRUE
              AND lower(a.action_code) = ANY(@required_action_codes);
            """,
            connection);
        command.CommandTimeout = Math.Max(options.QueryTimeoutSeconds, 1);
        command.Parameters.Add(new NpgsqlParameter("required_action_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = RequiredActionCodes
        });

        var existingActionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            existingActionCodes.Add(reader.GetString(0));
        }

        var missingActionCodes = RequiredActionCodes
            .Where(actionCode => !existingActionCodes.Contains(actionCode))
            .OrderBy(actionCode => actionCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missingActionCodes.Length == 0)
        {
            return new InventoryApiInvRuntimeMigrationVerificationResult(
                false,
                "Inventory API import runtime migration verification passed.",
                []);
        }

        return new InventoryApiInvRuntimeMigrationVerificationResult(
            true,
            $"Inventory API import permissions are missing: {string.Join(", ", missingActionCodes)}. Run database\\backfill_inventory_api_inv_import_actions.sql before assigning these permissions.",
            missingActionCodes);
    }
}
