namespace Accounting.Infrastructure.Logging;

public interface IAppLogger
{
    void LogInfo(string source, string eventName, string message);

    void LogWarning(string source, string eventName, string message, Exception? exception = null);

    void LogError(string source, string eventName, string message, Exception? exception = null);

    void LogCritical(string source, string eventName, string message, Exception? exception = null);
}

public sealed class NullAppLogger : IAppLogger
{
    public static readonly NullAppLogger Instance = new();

    private NullAppLogger()
    {
    }

    public void LogInfo(string source, string eventName, string message)
    {
    }

    public void LogWarning(string source, string eventName, string message, Exception? exception = null)
    {
    }

    public void LogError(string source, string eventName, string message, Exception? exception = null)
    {
    }

    public void LogCritical(string source, string eventName, string message, Exception? exception = null)
    {
    }
}
