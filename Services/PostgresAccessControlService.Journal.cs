using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private bool _journalSchemaEnsured;
    private static readonly string[] AccountingPeriodManagerRoles = ["SUPER_ADMIN", "FINANCE_ADMIN"];
    private static readonly string[] AllowedAccountTypes = ["ASSET", "LIABILITY", "EQUITY", "REVENUE", "EXPENSE"];
    private const string RetainedEarningsSuffix = "33000.001";
}
