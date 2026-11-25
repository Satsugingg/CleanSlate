using System.IO;

namespace CleanSlate.Core;

/// <summary>
/// Lightweight, thread-safe rolling file logger under %LocalAppData%\CleanSlate\cleanslate.log.
/// Best-effort only: logging failures never throw or interrupt UI/hook execution.
/// </summary>
internal static class AppLogger
{
    private const long MaxLogBytes = 512 * 1024;
    private static readonly object LogLock = new();
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CleanSlate",
        "cleanslate.log");

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (LogLock)
            {
                string? dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var info = new FileInfo(LogFilePath);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    string rotated = LogFilePath + ".old";
                    File.Move(LogFilePath, rotated, overwrite: true);
                }

                string line = ex is null
                    ? $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}"
                    : $"{DateTime.UtcNow:O} [{level}] {message} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}";

                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // Logging must never crash callers.
        }
    }
}
