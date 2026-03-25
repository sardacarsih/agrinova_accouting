using System.Text;
using System.Text.RegularExpressions;
using Accounting.Infrastructure.Logging;
using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService : IAccessControlService
{
    private const string SuperAdminCode = "SUPER_ADMIN";
    private static readonly Regex SqlNameRegex = new("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    private readonly DatabaseAuthOptions _options;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaEnsured;

    public PostgresAccessControlService(DatabaseAuthOptions options)
    {
        _options = options;
    }
}
