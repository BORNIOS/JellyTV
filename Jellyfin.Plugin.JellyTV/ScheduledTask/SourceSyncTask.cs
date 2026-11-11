using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.ScheduledTask;

/// <summary>
/// Sincroniza la guía EPG local: descarga las fuentes XMLTV, filtra por tvg-id de los canales,
/// enriquece con TMDB y escribe el archivo de guía fusionado (epg.xml) que Jellyfin consume como
/// proveedor XMLTV. Cadencia pesada (horas, default 24). Tras un sync exitoso encola el
/// "Refresh Guide" de Jellyfin para que re-importe la guía.
/// </summary>
public sealed class SourceSyncTask : IScheduledTask
{
    private readonly ILogger<SourceSyncTask> _logger;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceSyncTask"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="taskManager">Jellyfin task manager used to queue guide refreshes.</param>
    public SourceSyncTask(
        ILogger<SourceSyncTask> logger,
        ITaskManager taskManager)
    {
        _logger = logger;
        _taskManager = taskManager;
    }

    /// <inheritdoc/>
    public string Name => "JellyTV: Sincronizar EPG (guía local)";

    /// <inheritdoc/>
    public string Description => "Descarga fuentes XMLTV, filtra por canales y regenera la guía local (epg.xml) con enriquecimiento TMDB.";

    /// <inheritdoc/>
    public string Category => "JellyTV";

    /// <inheritdoc/>
    public string Key => "JellyTVSourceSync";

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("[JellyTV] SourceSyncTask: plugin not available.");
            return;
        }

        var sources = plugin.Configuration.Channels;
        if (sources.Count == 0)
        {
            _logger.LogInformation("[JellyTV] SourceSyncTask: no sources configured, skipping.");
            progress.Report(100);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[JellyTV] SourceSyncTask: processing {Count} channel(s).", sources.Count);
        }

        progress.Report(0);

        // ── EPG local + enriquecimiento TMDB ────────────────────────────────
        // Descarga las fuentes XMLTV, filtra por tvg-id de los canales, enriquece con TMDB
        // (si hay clave) y escribe la guía local fusionada que el plugin sirve a Jellyfin.
        if (plugin.Configuration.EpgSources?.Any(s => s.IsEnabled) == true)
        {
            _logger.LogInformation("[JellyTV] SourceSyncTask: syncing local EPG guide.");
            bool synced = false;
            try
            {
                var epgResult = await plugin.EpgFilter.SyncLocalGuideAsync(cancellationToken).ConfigureAwait(false);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[JellyTV] SourceSyncTask: EPG sync done. success={Success}, sources={Sources}, programmes={Programmes}",
                        epgResult.Success,
                        epgResult.Sources.Count,
                        epgResult.Sources.Sum(s => s.ProgrammesKept));
                }

                synced = epgResult.Success;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[JellyTV] SourceSyncTask: EPG sync cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JellyTV] SourceSyncTask: EPG sync failed.");
            }

            if (synced)
            {
                try
                {
                    plugin.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[JellyTV] SourceSyncTask: no se pudo guardar configuración tras el sync.");
                }

                if (plugin.Configuration.AutoRefreshGuideOnChange)
                {
                    PluginLog.Info("[JellyTV] SourceSyncTask: EPG actualizada; encolando Refresh Guide de Jellyfin.");
                    GuideRefresh.Queue(_taskManager);
                }
            }
        }
        else
        {
            _logger.LogDebug("[JellyTV] SourceSyncTask: no EPG sources enabled, skipping guide sync.");
        }

        progress.Report(100);
    }

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Plugin.Instance?.Configuration.SourceSyncIntervalHours ?? 24;
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks
            }
        ];
    }
}
