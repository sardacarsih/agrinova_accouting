using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Infrastructure.Logging;

public sealed class FileAppLogger : IAppLogger
{
    private static readonly Regex PasswordPattern = new(
        "(?i)(password|pwd)\\s*=\\s*[^;\\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly string _logDirectory;
    private readonly int _retentionDays;
    private DateTime _lastPruneDate = DateTime.MinValue;

    public static string GetDefaultLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Accounting",
            "logs");
    }

    public FileAppLogger(string? logDirectory = null, int retentionDays = 14)
    {
        _logDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? GetDefaultLogDirectory()
            : logDirectory;
        _retentionDays = Math.Max(1, retentionDays);
    }

    public void LogInfo(string source, string eventName, string message)
    {
        Write("INFO", source, eventName, message, null);
    }

    public void LogWarning(string source, string eventName, string message, Exception? exception = null)
    {
        Write("WARN", source, eventName, message, exception);
    }

    public void LogError(string source, string eventName, string message, Exception? exception = null)
    {
        Write("ERROR", source, eventName, message, exception);
    }

    public void LogCritical(string source, string eventName, string message, Exception? exception = null)
    {
        Write("FATAL", source, eventName, message, exception);
    }

    private void Write(string level, string source, string eventName, string message, Exception? exception)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_logDirectory);

                var now = DateTime.Now;
                if (_lastPruneDate.Date != now.Date)
                {
                    PruneOldLogs(now.Date);
                    _lastPruneDate = now.Date;
                }

                var logFile = Path.Combine(_logDirectory, $"accounting-{now:yyyy-MM-dd}.log");
                var sb = new StringBuilder();
                sb.Append("ts=");
                sb.Append(now.ToString("o", CultureInfo.InvariantCulture));
                sb.Append(" level=");
                sb.Append(NormalizeToken(level));
                sb.Append(" source=");
                sb.Append(NormalizeToken(source));
                sb.Append(" event=");
                sb.Append(NormalizeToken(eventName));
                sb.Append(" msg=\"");
                sb.Append(EscapeValue(Sanitize(message)));
                sb.Append('"');

                if (exception is not null)
                {
                    sb.Append(" ex_type=");
                    sb.Append(NormalizeToken(exception.GetType().FullName ?? exception.GetType().Name));
                    sb.Append(" ex_msg=\"");
                    sb.Append(EscapeValue(Sanitize(exception.Message)));
                    sb.Append('"');
                    if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                    {
                        sb.Append(" ex_stack=\"");
                        sb.Append(EscapeValue(Sanitize(exception.StackTrace)));
                        sb.Append('"');
                    }
                    sb.Append(" ex_full=\"");
                    sb.Append(EscapeValue(Sanitize(exception.ToString())));
                    sb.Append('"');
                }
                sb.AppendLine();

                File.AppendAllText(logFile, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Never break application flow because logging fails.
        }
    }

    private void PruneOldLogs(DateTime today)
    {
        var cutoff = today.AddDays(-_retentionDays);
        foreach (var path in Directory.GetFiles(_logDirectory, "accounting-*.log"))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.CreationTime.Date < cutoff)
                {
                    info.Delete();
                }
            }
            catch
            {
                // Ignore individual cleanup failures.
            }
        }
    }

    private static string Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return PasswordPattern.Replace(input, "$1=***");
    }

    private static string NormalizeToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "-";
        }

        return input.Trim().Replace(' ', '_');
    }

    private static string EscapeValue(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }
}
