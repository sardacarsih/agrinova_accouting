namespace Accounting.Services;

public static class AuthServiceFactory
{
    public static IAuthService Create()
    {
        var options = DatabaseAuthOptions.FromConfiguration();
        return new PostgresAuthService(options);
    }

    public static IAccessControlService CreateAccessControlService()
    {
        var options = DatabaseAuthOptions.FromConfiguration();
        return new PostgresAccessControlService(options);
    }
}

