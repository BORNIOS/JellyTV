using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Controllers;
using Jellyfin.Plugin.JellyTV.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.ScheduledTask;

/// <summary>
/// Scheduled task that performs health checks on all configured channels from all IPTV sources.
/// Cadencia frecuente (minutos). Descarga datos reales de stream para verificar cada canal,
/// auto-deshabilita los caídos y re-habilita los que regresan. Tras cada corrida regenera el
/// tuner.m3u con el estado resultante y, si hubo cambios, encola el "Refresh Guide" de Jellyfin.
/// </summary>
public class ChannelHealthCheckTask : IScheduledTask
{
    private static readonly string[] NewLineSeparators = ["\r\n", "\r", "\n"];

    private readonly ILogger<ChannelHealthCheckTask> _logger;
    private readonly ITaskManager _taskManager;

    public ChannelHealthCheckTask(
        ILogger<ChannelHealthCheckTask> logger,
        ITaskManager taskManager)
    {
        _logger = logger;
        _taskManager = taskManager;
    }

    /// <inheritdoc/>
    public string Name => "JellyTV: Health Check de Canales";

    /// <inheritdoc/>
    public string Description => "Verifica que todos los canales configurados estén vivos descargando datos reales de stream.";

    /// <inheritdoc/>
    public string Category => "JellyTV";

    /// <inheritdoc/>
    public string Key => "JellyTVChannelHealthCheck";

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        PluginLog.Info("=== HEALTH CHECK TASK: START ===");

        try
        {
            var config = Plugin.Instance?.Configuration;
            var healthCheck = Plugin.Instance?.ChannelHealthCheck;
            if (config is null || config.Channels.Count == 0 || healthCheck is null)
            {
                PluginLog.Warn("[HEALTH-TASK] Sin canales o servicio de health no disponible; tarea omitida.");
                return;
            }

            Plugin.NormalizeChannels();

            // Estado previo para detectar canales deshabilitados/rehabilitados por el health.
            var before = config.Channels.ToDictionary(
                c => c.Id,
                c => (Enabled: c.IsEnabled, AutoDisabled: c.AutoDisabled),
                StringComparer.Ordinal);

            var (alive, dead, sourcesChecked, sourcesAlive) = await healthCheck
                .CheckConfiguredChannelsAsync("scheduled", config.Channels, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                Plugin.Instance?.SaveConfiguration();
            }
            catch
            {
            }

            PluginLog.Info($"[HEALTH-TASK] Health check complete: {alive} canales vivos, {dead} caidos ({sourcesAlive}/{sourcesChecked} fuentes)");

            // Regenerar tuner.m3u con el estado resultante (canales deshabilitados fuera).
            JellyTVController.RefreshPersistedTunerM3u();

            var changed = config.Channels.Any(c =>
                before.TryGetValue(c.Id, out var b)
                && (b.Enabled != c.IsEnabled || b.AutoDisabled != c.AutoDisabled));
            if (changed && config.AutoRefreshGuideOnChange)
            {
                PluginLog.Info("[HEALTH-TASK] Cambios de estado detectados; encolando Refresh Guide de Jellyfin.");
                GuideRefresh.Queue(_taskManager);
            }
            else if (changed)
            {
                PluginLog.Info("[HEALTH-TASK] Cambios de estado detectados pero auto-refresh de guía está desactivado.");
            }

            progress.Report(1.0);
            PluginLog.Info($"=== HEALTH CHECK TASK: COMPLETE ({alive + dead} canales validados, changed={changed}) ===");
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
            var lines = content.Split(NewLineSeparators, StringSplitOptions.None);

            string? currentName = null;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    var commaIndex = trimmed.LastIndexOf(',');
                    currentName = commaIndex >= 0 && commaIndex + 1 < trimmed.Length
                        ? trimmed[(commaIndex + 1)..].Trim()
                        : "(Sin nombre)";
                }
                else if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('#'))
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

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Plugin.Instance?.Configuration?.HealthCheckIntervalMinutes ?? 30;
        minutes = Math.Clamp(minutes, 5, 1440);

        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks,
            }
        };
    }
}
