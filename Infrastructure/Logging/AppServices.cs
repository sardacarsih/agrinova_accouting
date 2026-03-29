namespace Accounting.Infrastructure.Logging;

using Accounting.Services;

public static class AppServices
{
    private static IAppLogger _logger = NullAppLogger.Instance;
    private static AppThemeCoordinator? _themeCoordinator;

    public static IAppLogger Logger => _logger;

    public static AppThemeCoordinator ThemeCoordinator =>
        _themeCoordinator ?? throw new InvalidOperationException("Theme coordinator has not been initialized.");

    public static void InitializeLogger(IAppLogger? logger)
    {
        Interlocked.Exchange(ref _logger, logger ?? NullAppLogger.Instance);
    }

    public static void InitializeThemeCoordinator(AppThemeCoordinator themeCoordinator)
    {
        ArgumentNullException.ThrowIfNull(themeCoordinator);
        Interlocked.Exchange(ref _themeCoordinator, themeCoordinator);
    }

    public static void ShutdownThemeCoordinator()
    {
        Interlocked.Exchange(ref _themeCoordinator, null)?.Dispose();
    }
}
