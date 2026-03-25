namespace Accounting.Services;

public sealed record AuthResult(bool IsSuccess, string? ErrorMessage = null);

public interface IAuthService
{
    Task<AuthResult> SignInAsync(string username, string password, bool rememberMe, CancellationToken cancellationToken = default);
}

