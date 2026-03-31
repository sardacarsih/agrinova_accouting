using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    public Task<List<ManagedSubledgerReference>> GetVendorsAsync(
        long companyId,
        bool includeInactive = false,
        string actorUsername = "",
        CancellationToken cancellationToken = default)
    {
        return GetSubledgerReferencesAsync(
            companyId,
            includeInactive,
            actorUsername,
            "VENDOR",
            "gl_vendors",
            "vendor_code",
            "vendor_name",
            cancellationToken);
    }

    public Task<List<ManagedSubledgerReference>> GetCustomersAsync(
        long companyId,
        bool includeInactive = false,
        string actorUsername = "",
        CancellationToken cancellationToken = default)
    {
        return GetSubledgerReferencesAsync(
            companyId,
            includeInactive,
            actorUsername,
            "CUSTOMER",
            "gl_customers",
            "customer_code",
            "customer_name",
            cancellationToken);
    }

    public Task<List<ManagedSubledgerReference>> GetEmployeesAsync(
        long companyId,
        bool includeInactive = false,
        string actorUsername = "",
        CancellationToken cancellationToken = default)
    {
        return GetSubledgerReferencesAsync(
            companyId,
            includeInactive,
            actorUsername,
            "EMPLOYEE",
            "gl_employees",
            "employee_code",
            "employee_name",
            cancellationToken);
    }

    private async Task<List<ManagedSubledgerReference>> GetSubledgerReferencesAsync(
        long companyId,
        bool includeInactive,
        string actorUsername,
        string subledgerType,
        string tableName,
        string codeColumn,
        string nameColumn,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureJournalSchemaAsync(cancellationToken);

        var output = new List<ManagedSubledgerReference>();
        if (companyId <= 0)
        {
            return output;
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HasAnyPermissionAsync(
                connection,
                transaction: null,
                NormalizeActor(actorUsername),
                AccountingModuleCode,
                AccountingSubmoduleTransactions,
                AccountingTransactionsReadActions,
                companyId,
                null,
                cancellationToken))
        {
            return output;
        }

        var sql = $@"
SELECT id,
       company_id,
       upper(btrim(coalesce({codeColumn}, ''))) AS subledger_code,
       btrim(coalesce({nameColumn}, '')) AS subledger_name,
       coalesce(is_active, FALSE) AS is_active
FROM {tableName}
WHERE company_id = @company_id
  AND (@include_inactive = TRUE OR coalesce(is_active, FALSE) = TRUE)
  AND btrim(coalesce({codeColumn}, '')) <> ''
ORDER BY {codeColumn};";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ManagedSubledgerReference
            {
                Id = reader.GetInt64(0),
                CompanyId = reader.GetInt64(1),
                SubledgerType = subledgerType,
                Code = reader.GetString(2),
                Name = reader.GetString(3),
                IsActive = !reader.IsDBNull(4) && reader.GetBoolean(4)
            });
        }

        return output;
    }
}
