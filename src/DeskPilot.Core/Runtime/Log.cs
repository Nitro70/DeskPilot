using System.Text;

namespace DeskPilot.Core.Runtime;

/// <summary>Tiny append-only file log in %LOCALAPPDATA%\DeskPilot\logs. Never logs API keys or screenshots.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static string FilePath
    {
        get
        {
            lock (Gate)
            {
                _file ??= Path.Combine(Settings.AppPaths.LogsDirectory, $"deskpilot-{DateTime.Now:yyyy-MM-dd}.log");
                return _file;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(FilePath, line, Encoding.UTF8);
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }
}
