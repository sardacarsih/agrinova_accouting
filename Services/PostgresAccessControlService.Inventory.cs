using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private static readonly string[] InventoryOpeningBalanceAdminRoles = ["SUPER_ADMIN"];
    private const string InventoryMasterCompanySettingKey = "inventory_master_company_id";
    private const decimal DefaultLowStockThreshold = 10m;
    private bool _inventorySchemaEnsured;
}
