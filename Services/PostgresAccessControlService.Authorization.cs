using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private const string AccountingModuleCode = "accounting";
    private const string InventoryModuleCode = "inventory";

    private const string AccountingSubmoduleMasterData = "master_data";
    private const string AccountingSubmoduleTransactions = "transactions";
    private const string AccountingSubmoduleUserManagement = "user_management";

    private const string InventorySubmoduleItem = "item";
    private const string InventorySubmoduleCategory = "kategori";
    private const string InventorySubmoduleUnit = "satuan";
    private const string InventorySubmoduleWarehouse = "gudang";
    private const string InventorySubmoduleStockIn = "stock_in";
    private const string InventorySubmoduleStockOut = "stock_out";
    private const string InventorySubmoduleTransfer = "transfer";
    private const string InventorySubmoduleStockOpname = "stock_opname";
    private const string InventorySubmoduleApiInv = "api_inv";

    private const string PermissionActionCreate = "create";
    private const string PermissionActionView = "view";
    private const string PermissionActionUpdate = "update";
    private const string PermissionActionDelete = "delete";
    private const string PermissionActionSubmit = "submit";
    private const string PermissionActionApprove = "approve";
    private const string PermissionActionPost = "post";
    private const string PermissionActionManageRoles = "manage_roles";
    private const string PermissionActionManageCompanies = "manage_companies";
    private const string PermissionActionManageLocations = "manage_locations";
    private const string PermissionActionUpdateSettings = "update_settings";
    private const string PermissionActionManageMasterCompany = "manage_master_company";
    private const string PermissionActionSyncUpload = "sync_upload";
    private const string PermissionActionSyncDownload = "sync_download";
    private const string PermissionActionPullJournal = "pull_journal";
    private const string PermissionActionImportMasterData = "import_master_data";
    private const string PermissionActionDownloadImportTemplate = "download_import_template";
    private static readonly string[] AccountingTransactionsReadActions =
    [
        PermissionActionView,
        PermissionActionCreate,
        PermissionActionUpdate,
        PermissionActionSubmit,
        PermissionActionApprove,
        PermissionActionPost,
        "import",
        "export"
    ];
    private static readonly string[] AccountingReportsReadActions =
    [
        PermissionActionView,
        "export"
    ];

    private async Task<bool> HasPermissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string username,
        string moduleCode,
        string submoduleCode,
        string actionCode,
        long? companyId,
        long? locationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(moduleCode) ||
            string.IsNullOrWhiteSpace(submoduleCode) ||
            string.IsNullOrWhiteSpace(actionCode))
        {
            return false;
        }

        await using var command = new NpgsqlCommand(@"
SELECT fn_user_has_permission(
    @username,
    @module_code,
    @submodule_code,
    @action_code,
    @company_id,
    @location_id);", connection, transaction);

        command.Parameters.AddWithValue("username", username.Trim());
        command.Parameters.AddWithValue("module_code", moduleCode.Trim());
        command.Parameters.AddWithValue("submodule_code", submoduleCode.Trim());
        command.Parameters.AddWithValue("action_code", actionCode.Trim());
        command.Parameters.Add(new NpgsqlParameter("company_id", NpgsqlDbType.Bigint)
        {
            Value = companyId.HasValue ? companyId.Value : DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("location_id", NpgsqlDbType.Bigint)
        {
            Value = locationId.HasValue ? locationId.Value : DBNull.Value
        });

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is bool isAllowed && isAllowed;
    }

    private async Task<AccessOperationResult?> EnsurePermissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string username,
        string moduleCode,
        string submoduleCode,
        string actionCode,
        long? companyId,
        long? locationId,
        CancellationToken cancellationToken,
        string failureMessage)
    {
        var hasPermission = await HasPermissionAsync(
            connection,
            transaction,
            username,
            moduleCode,
            submoduleCode,
            actionCode,
            companyId,
            locationId,
            cancellationToken);
        if (hasPermission)
        {
            return null;
        }

        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return new AccessOperationResult(false, failureMessage);
    }

    private async Task<bool> HasAnyPermissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string username,
        string moduleCode,
        string submoduleCode,
        IReadOnlyCollection<string> actionCodes,
        long? companyId,
        long? locationId,
        CancellationToken cancellationToken)
    {
        if (actionCodes is null || actionCodes.Count == 0)
        {
            return false;
        }

        foreach (var actionCode in actionCodes)
        {
            if (await HasPermissionAsync(
                    connection,
                    transaction,
                    username,
                    moduleCode,
                    submoduleCode,
                    actionCode,
                    companyId,
                    locationId,
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> HasScopeAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string username,
        long? companyId,
        long? locationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        if (!TryBuildQualifiedTableName(_options.UsersTable, out var usersTable))
        {
            return false;
        }

        await using var userCommand = new NpgsqlCommand($@"
SELECT u.id,
       EXISTS (
           SELECT 1
           FROM sec_user_roles ur
           JOIN sec_roles r ON r.id = ur.role_id
           WHERE ur.user_id = u.id
             AND r.is_active = TRUE
             AND COALESCE(r.is_super_role, FALSE) = TRUE
       )
FROM {usersTable} u
WHERE lower(u.username) = lower(@username)
  AND u.is_active = TRUE
LIMIT 1;", connection, transaction);
        userCommand.Parameters.AddWithValue("username", username.Trim());

        long? userId = null;
        var hasSuperRole = false;
        await using (var reader = await userCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                userId = reader.GetInt64(0);
                hasSuperRole = !reader.IsDBNull(1) && reader.GetBoolean(1);
            }
        }

        if (!userId.HasValue)
        {
            return false;
        }

        if (hasSuperRole)
        {
            return true;
        }

        if (companyId.HasValue)
        {
            await using var companyCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM sec_user_company_access uca
JOIN org_companies c ON c.id = uca.company_id
WHERE uca.user_id = @user_id
  AND uca.company_id = @company_id
  AND c.is_active = TRUE;", connection, transaction);
            companyCommand.Parameters.AddWithValue("user_id", userId.Value);
            companyCommand.Parameters.AddWithValue("company_id", companyId.Value);

            var companyCount = Convert.ToInt32(await companyCommand.ExecuteScalarAsync(cancellationToken));
            if (companyCount <= 0)
            {
                return false;
            }
        }

        if (locationId.HasValue)
        {
            await using var locationCommand = new NpgsqlCommand(@"
SELECT COUNT(1)
FROM sec_user_location_access ula
JOIN org_locations l ON l.id = ula.location_id
WHERE ula.user_id = @user_id
  AND ula.location_id = @location_id
  AND l.is_active = TRUE
  AND (@company_id IS NULL OR l.company_id = @company_id);", connection, transaction);
            locationCommand.Parameters.AddWithValue("user_id", userId.Value);
            locationCommand.Parameters.AddWithValue("location_id", locationId.Value);
            locationCommand.Parameters.Add(new NpgsqlParameter("company_id", NpgsqlDbType.Bigint)
            {
                Value = companyId.HasValue ? companyId.Value : DBNull.Value
            });

            var locationCount = Convert.ToInt32(await locationCommand.ExecuteScalarAsync(cancellationToken));
            if (locationCount <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<InventoryImportExecutionResult?> EnsureInventoryImportPermissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string username,
        string moduleCode,
        string submoduleCode,
        string actionCode,
        long? companyId,
        long? locationId,
        CancellationToken cancellationToken,
        string failureMessage)
    {
        var hasPermission = await HasPermissionAsync(
            connection,
            transaction,
            username,
            moduleCode,
            submoduleCode,
            actionCode,
            companyId,
            locationId,
            cancellationToken);
        if (hasPermission)
        {
            return null;
        }

        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return new InventoryImportExecutionResult
        {
            IsSuccess = false,
            Message = failureMessage
        };
    }

    private static string ResolveWriteAction(long entityId)
    {
        return entityId > 0 ? PermissionActionUpdate : PermissionActionCreate;
    }

    private static string ResolveInventoryTransactionSubmodule(string transactionType)
    {
        return NormalizeTransactionType(transactionType) switch
        {
            "STOCK_IN" => InventorySubmoduleStockIn,
            "STOCK_OUT" => InventorySubmoduleStockOut,
            "TRANSFER" => InventorySubmoduleTransfer,
            _ => string.Empty
        };
    }

    private static string ResolveWorkflowAction(string targetStatus)
    {
        return (targetStatus ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "SUBMITTED" => PermissionActionSubmit,
            "APPROVED" => PermissionActionApprove,
            "POSTED" => PermissionActionPost,
            _ => string.Empty
        };
    }
}
