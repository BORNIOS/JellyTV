using System;
using System.IO;

namespace Jellyfin.Plugin.IptvProxy;

/// <summary>
/// Writes timestamped log lines to a plain text file next to the plugin DLL.
/// Useful for diagnosing issues when Jellyfin's own log output is hard to filter.
/// Delete or disable once the plugin is confirmed working.
/// </summary>
/// <summary>
/// Simple file logger for plugin-level diagnostic messages.
/// Writes timestamped lines to a local file to help debugging outside Jellyfin logs.
/// </summary>
public static class PluginLog
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory,
        "iptv-proxy-debug.log");

    private static readonly object Lock = new();

    /// <summary>
    /// Appends a timestamped log line at the given level to the plugin log file.
    /// </summary>
    /// <param name="level">Log level label (e.g. "INFO").</param>
    /// <param name="message">Message text to write.</param>
    public static void Write(string level, string message)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            lock (Lock)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Never crash Jellyfin due to logging
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
}