using System.IO;
using System.Text.Json;

namespace Accounting.Services;

public sealed class DatabaseAuthOptions
{
    public string ConnectionString { get; init; } = string.Empty;

    public string UsersTable { get; init; } = "public.app_users";

    public int QueryTimeoutSeconds { get; init; } = 8;

    public static DatabaseAuthOptions FromConfiguration()
    {
        var fromFile = LoadFromAppSettings();

        var connectionFromEnv = Environment.GetEnvironmentVariable("WFP_PG_CONNECTION");
        var tableFromEnv = Environment.GetEnvironmentVariable("WFP_AUTH_USERS_TABLE");
        var timeoutFromEnv = Environment.GetEnvironmentVariable("WFP_AUTH_QUERY_TIMEOUT");

        return new DatabaseAuthOptions
        {
            ConnectionString = string.IsNullOrWhiteSpace(connectionFromEnv) ? fromFile.ConnectionString : connectionFromEnv,
            UsersTable = string.IsNullOrWhiteSpace(tableFromEnv) ? fromFile.UsersTable : tableFromEnv,
            QueryTimeoutSeconds = ParseInt(timeoutFromEnv, fromFile.QueryTimeoutSeconds)
        };
    }

    private static DatabaseAuthOptions LoadFromAppSettings()
    {
        try
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(filePath))
            {
                return Default();
            }

            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("DatabaseAuth", out var dbAuthElement))
            {
                return Default();
            }

            return new DatabaseAuthOptions
            {
                ConnectionString = dbAuthElement.TryGetProperty("ConnectionString", out var connection)
                    ? connection.GetString() ?? string.Empty
                    : string.Empty,
                UsersTable = dbAuthElement.TryGetProperty("UsersTable", out var table)
                    ? table.GetString() ?? "public.app_users"
                    : "public.app_users",
                QueryTimeoutSeconds = dbAuthElement.TryGetProperty("QueryTimeoutSeconds", out var timeout)
                    ? ParseInt(timeout.GetRawText(), 8)
                    : 8
            };
        }
        catch (Exception)
        {
            return Default();
        }
    }

    private static DatabaseAuthOptions Default()
    {
        return new DatabaseAuthOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=5432;Database=agrinova_accounting;Username=agrinova;Password=CHANGE_ME;Pooling=true;Timeout=8;Command Timeout=8;",
            UsersTable = "public.app_users",
            QueryTimeoutSeconds = 8
        };
    }

    private static int ParseInt(string? value, int defaultValue)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }
}

