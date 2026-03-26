using System.Text.RegularExpressions;
using Accounting.Infrastructure.Logging;
using Npgsql;

namespace Accounting.Services;

public sealed class PostgresAuthService : IAuthService
{
    private static readonly Regex SqlNameRegex = new("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    private readonly DatabaseAuthOptions _options;

    public PostgresAuthService(DatabaseAuthOptions options)
    {
        _options = options;
    }

    public async Task<AuthResult> SignInAsync(string username, string password, bool rememberMe, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            return new AuthResult(false, "Database connection is not configured. Set DatabaseAuth.ConnectionString or AGRINOVA_PG_CONNECTION.");
        }

        if (!TryBuildQualifiedTableName(_options.UsersTable, out var qualifiedTableName))
        {
            return new AuthResult(false, "Invalid users table configuration.");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new AuthResult(false, "Username and password are required.");
        }

        var sql = $@"
SELECT password_hash, is_active
FROM {qualifiedTableName}
WHERE lower(username) = lower(@username)
LIMIT 1;";

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(sql, connection)
            {
                CommandTimeout = _options.QueryTimeoutSeconds
            };
            command.Parameters.AddWithValue("username", username.Trim());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return InvalidCredential();
            }

            var passwordHash = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var isActive = !reader.IsDBNull(1) && reader.GetBoolean(1);

            if (!isActive)
            {
                return new AuthResult(false, "This account is inactive. Contact your system administrator.");
            }

            var isValid = PasswordHashUtility.VerifyPbkdf2Hash(password, passwordHash);
            return isValid ? new AuthResult(true) : InvalidCredential();
        }
        catch (OperationCanceledException ex)
        {
            AppServices.Logger.LogWarning(
                nameof(PostgresAuthService),
                "SignInTimedOut",
                $"action=sign_in status=timeout username={username?.Trim()}",
                ex);
            return new AuthResult(false, "Authentication timed out. Please try again.");
        }
        catch (NpgsqlException ex)
        {
            AppServices.Logger.LogError(
                nameof(PostgresAuthService),
                "SignInDatabaseError",
                $"action=sign_in status=db_error username={username?.Trim()}",
                ex);
            return new AuthResult(false, "Database connection failed. Verify PostgreSQL connection settings.");
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(PostgresAuthService),
                "SignInUnexpectedError",
                $"action=sign_in status=unexpected_error username={username?.Trim()}",
                ex);
            return new AuthResult(false, "Unexpected authentication error occurred. Please contact support.");
        }
    }

    private static AuthResult InvalidCredential()
    {
        return new AuthResult(false, "Invalid username or password. Please verify your credentials and try again.");
    }

    private static bool TryBuildQualifiedTableName(string input, out string qualifiedTableName)
    {
        qualifiedTableName = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (!SqlNameRegex.IsMatch(part))
            {
                return false;
            }
        }

        qualifiedTableName = parts.Length == 2
            ? $"\"{parts[0]}\".\"{parts[1]}\""
            : $"\"public\".\"{parts[0]}\"";

        return true;
    }
}

