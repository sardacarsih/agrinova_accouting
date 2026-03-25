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
