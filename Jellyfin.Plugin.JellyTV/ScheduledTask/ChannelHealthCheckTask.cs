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
    private readonly ILogger<ChannelHealthCheckTask> _logger;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelHealthCheckTask"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="taskManager">Jellyfin task manager used to queue guide refreshes.</param>
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
                .CheckConfiguredChannelsAsync("scheduled", config.Channels, ct: cancellationToken)
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
