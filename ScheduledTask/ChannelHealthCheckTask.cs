using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellyTV.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyTV.ScheduledTask;

/// <summary>
/// Scheduled task that performs health checks on all configured channels from all IPTV sources.
/// Runs on a configurable interval (default: every 24 hours).
/// Downloads real stream data (HLS playlists + segments or MPEG-TS chunks) to verify each channel.
/// </summary>
public class ChannelHealthCheckTask : IScheduledTask
{
    private readonly ILogger<ChannelHealthCheckTask> _logger;

    public string Name => "JellyTV: Health Check de Canales";
    public string Description => "Verifica que todos los canales configurados estén vivos descargando datos reales de stream.";
    public string Category => "JellyTV";
    public string Key => "JellyTVChannelHealthCheck";

    public ChannelHealthCheckTask(
        ILogger<ChannelHealthCheckTask> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        PluginLog.Info("=== HEALTH CHECK TASK: START ===");

        try
        {
            var config = Plugin.Instance?.Configuration;
            var healthCheck = Plugin.Instance?.ChannelHealthCheck;
            if (config is null || config.Sources.Count == 0)
            {
                PluginLog.Warn("[HEALTH-TASK] No sources configured, skipping health check.");
                return;
            }

            if (healthCheck is null)
            {
                PluginLog.Warn("[HEALTH-TASK] ChannelHealthCheck service not available, skipping.");
                return;
            }

            var enabledSources = config.Sources.Where(s => s.IsEnabled).ToList();
            if (!enabledSources.Any())
            {
                PluginLog.Info("[HEALTH-TASK] No enabled sources, skipping health check.");
                return;
            }

            var totalChannels = 0;
            var progressIncrement = 1.0 / enabledSources.Count;
            var currentProgress = 0.0;

            foreach (var source in enabledSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(source.Url))
                {
                    currentProgress += progressIncrement;
                    progress.Report(currentProgress);
                    continue;
                }

                PluginLog.Info($"[HEALTH-TASK] Processing source: {source.Name}");

                try
                {
                    // Parse M3U to extract channels
                    // This is simplified — assumes M3U format with #EXTINF lines
                    var channels = await ParseM3uChannelsAsync(source.Url, cancellationToken).ConfigureAwait(false);
                    if (!channels.Any())
                    {
                        PluginLog.Warn($"[HEALTH-TASK] No channels found in source {source.Name}");
                        currentProgress += progressIncrement;
                        progress.Report(currentProgress);
                        continue;
                    }

                    // Perform health checks
                    var results = await healthCheck.CheckSourceChannelsAsync(
                        source.Id,
                        channels,
                        cancellationToken).ConfigureAwait(false);

                    var alive = results.Count(r => r.IsAlive);
                    var dead = results.Count - alive;

                    PluginLog.Info($"[HEALTH-TASK] Source '{source.Name}': {alive}/{results.Count} canales vivos");

                    if (dead > 0)
                    {
                        var deadChannels = string.Join(", ", results.Where(r => !r.IsAlive).Select(r => r.ChannelName).Take(5));
                        PluginLog.Warn($"[HEALTH-TASK] Dead channels: {deadChannels} {(results.Count(r => !r.IsAlive) > 5 ? "..." : "")}");
                    }

                    totalChannels += results.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[HEALTH-TASK] Error processing source {Name}", source.Name);
                    PluginLog.Error($"[HEALTH-TASK] Error in source {source.Name}: {ex.Message}", ex);
                }

                currentProgress += progressIncrement;
                progress.Report(currentProgress);
            }

            progress.Report(1.0);
            PluginLog.Info($"=== HEALTH CHECK TASK: COMPLETE ({totalChannels} canales validados) ===");
        }
        catch (OperationCanceledException)
        {
            PluginLog.Warn("[HEALTH-TASK] Health check cancelled by user");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HEALTH-TASK] Unhandled exception");
            PluginLog.Error("[HEALTH-TASK] Unhandled exception", ex);
        }
    }

    /// <summary>
    /// Parses an M3U source and extracts channel names and URLs.
    /// </summary>
    private async Task<List<(string Name, string Url)>> ParseM3uChannelsAsync(
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var channels = new List<(string, string)>();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var client = Plugin.Instance?.HttpClientFactory?.CreateClient("JellyTV");
            if (client is null)
            {
                return channels;
            }

            var content = await client.GetStringAsync(sourceUrl, cts.Token).ConfigureAwait(false);
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            string? currentName = null;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("#EXTINF:"))
                {
                    var commaIndex = trimmed.LastIndexOf(',');
                    currentName = commaIndex >= 0 && commaIndex + 1 < trimmed.Length
                        ? trimmed[(commaIndex + 1)..].Trim()
                        : "(Sin nombre)";
                }
                else if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                {
                    // This is a URL
                    if (!string.IsNullOrEmpty(currentName))
                    {
                        channels.Add((currentName, trimmed));
                    }

                    currentName = null;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[HEALTH-TASK] Failed to parse M3U from {sourceUrl}: {ex.Message}");
        }

        return channels;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Plugin.Instance?.Configuration?.ChannelHealthCheckIntervalHours ?? 24;
        hours = Math.Clamp(hours, 1, 168);

        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks,
            }
        };
    }
}
