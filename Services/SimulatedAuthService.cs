namespace Accounting.Services;

public sealed class SimulatedAuthService : IAuthService
{
    public async Task<AuthResult> SignInAsync(string username, string password, bool rememberMe, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1200, cancellationToken);

        var validUser = string.Equals(username.Trim(), "admin@company.com", StringComparison.OrdinalIgnoreCase);
        var validPassword = string.Equals(password, "Password123!", StringComparison.Ordinal);

        return validUser && validPassword
            ? new AuthResult(true)
            : new AuthResult(false, "Invalid username or password. Please verify your credentials and try again.");
    }
}

