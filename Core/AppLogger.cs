using System.Text;

namespace WinGemini;

internal static class AppLogger
{
    private static readonly object Sync = new();
    private static readonly string LogsDirectoryPath = Path.Combine(AppConfig.AppDataRootFolder, "logs");
    private static readonly string LogFilePath = Path.Combine(
        LogsDirectoryPath,
        $"wingemini-{DateTime.Now:yyyyMMdd}.log");
    private static bool _debugLoggingEnabled;

    internal static bool IsDebugLoggingEnabled => _debugLoggingEnabled;

    internal static void SetDebugLoggingEnabled(bool enabled)
    {
        _debugLoggingEnabled = enabled;
    }

    internal static void Debug(string message)
    {
        if (!_debugLoggingEnabled)
        {
            return;
        }

        Write("DEBUG", message, null);
    }

    internal static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectoryPath);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var builder = new StringBuilder();
            builder.Append('[').Append(timestamp).Append("] [").Append(level).Append("] ").Append(message);
            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append(exception);
            }

            var line = builder.ToString();
            lock (Sync)
            {
                Console.WriteLine(line);
                Directory.CreateDirectory(LogsDirectoryPath);
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}

