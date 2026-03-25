namespace Accounting.Infrastructure.Logging;

public static class AppServices
{
    private static IAppLogger _logger = NullAppLogger.Instance;

    public static IAppLogger Logger => _logger;

    public static void InitializeLogger(IAppLogger? logger)
    {
        Interlocked.Exchange(ref _logger, logger ?? NullAppLogger.Instance);
    }
}
