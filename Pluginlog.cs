using System;
using System.IO;

namespace JellyTV;

/// <summary>
/// Simple file logger for plugin-level diagnostic messages.
/// Writes timestamped lines to a daily rotating file (JellyTV-yyyy-MM-dd.log).
/// Files older than <see cref="RetentionDays"/> days are deleted automatically.
/// </summary>
public static class PluginLog
{
    // Base directory — updated by SetLogDirectory
    private static string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "jellyfin",
        "log");

    private const int RetentionDays = 5;
    private static readonly object Lock = new();

    // Track the last date we wrote to so we rotate and clean up at midnight.
    private static DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Appends a timestamped log line at the given level.</summary>
    public static void Write(string level, string message)
    {
        try
        {
            var now  = DateTimeOffset.Now;
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            lock (Lock)
            {
                EnsureDirectory();

                // Rotate: when the calendar day changes, clean up old logs.
                if (now.Date != _lastWrite.Date)
                {
                    PurgeOldLogs();
                    _lastWrite = now;
                }

                File.AppendAllText(CurrentLogPath, line);
            }
        }
        catch
        {
            // Never crash Jellyfin due to logging.
        }
    }

    /// <summary>Writes an informational message.</summary>
    public static void Info(string msg) => Write("INFO ", msg);

    /// <summary>Writes a warning message.</summary>
    public static void Warn(string msg) => Write("WARN ", msg);

    /// <summary>Writes an error message.</summary>
    public static void Error(string msg) => Write("ERROR", msg);

    /// <summary>Writes an error message with exception details.</summary>
    public static void Error(string msg, Exception ex) => Write("ERROR", $"{msg} | {ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// Sets the directory where log files are written.
    /// The directory is created if it does not exist.
    /// </summary>
    public static void SetLogDirectory(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _logDir = dir;
        }
        catch
        {
            // ignore — keep previous directory
        }
    }

    /// <summary>
    /// Gets the absolute path of today's log file.
    /// </summary>
    public static string CurrentLogPath =>
        Path.Combine(_logDir, $"JellyTV-{DateTimeOffset.Now:yyyy-MM-dd}.log");

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void EnsureDirectory()
    {
        try
        {
            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);
        }
        catch { }
    }

    /// <summary>
    /// Deletes JellyTV-*.log files whose date suffix is older than
    /// <see cref="RetentionDays"/> days.
    /// </summary>
    private static void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTimeOffset.Now.Date.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(_logDir, "JellyTV-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // JellyTV-2026-03-27
                var datePart = name.Length > 8 ? name[^10..] : null;  // last 10 chars = yyyy-MM-dd
                if (datePart is not null
                    && DateTimeOffset.TryParseExact(datePart, "yyyy-MM-dd",
                           null, System.Globalization.DateTimeStyles.None, out var fileDate)
                    && fileDate.Date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch { }
    }
}