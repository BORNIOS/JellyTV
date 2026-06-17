using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyTV.ScheduledTask;

/// <summary>
/// Re-fetches all configured M3U sources on a schedule so the channel list
/// stays current without waiting for a manual trigger or Jellyfin tuner poll.
/// </summary>
public sealed class SourceSyncTask : IScheduledTask
{
    private readonly ILogger<SourceSyncTask> _logger;

    public SourceSyncTask(ILogger<SourceSyncTask> logger)
    {
        _logger = logger;
    }

    public string Name        => "JellyTV: Sync IPTV Sources";
    public string Description => "Re-fetches all configured M3U sources and refreshes the channel list.";
    public string Category    => "JellyTV";
    public string Key         => "JellyTVSourceSync";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("[JellyTV] SourceSyncTask: plugin not available.");
            return;
        }

        var sources = plugin.Configuration.Sources;
        if (sources.Count == 0)
        {
            _logger.LogInformation("[JellyTV] SourceSyncTask: no sources configured, skipping.");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("[JellyTV] SourceSyncTask: syncing {Count} source(s).", sources.Count);
        progress.Report(0);

        // KnownBaseUrl is set on the first /m3u request. Fall back to localhost so
        // the task can still update source stats even before the first client hit.
        var baseUrl = plugin.KnownBaseUrl ?? "http://localhost:8096";

        try
        {
            await plugin.M3uAggregator
                .BuildCombinedM3uAsync(baseUrl, cancellationToken, forceRefresh: true)
                .ConfigureAwait(false);

            _logger.LogInformation("[JellyTV] SourceSyncTask: sync complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JellyTV] SourceSyncTask: sync failed.");
        }

        progress.Report(30);

        // ── EPG sync to Worker ────────────────────────────────────────────────
        // Push the filtered EPG XML to the Worker BEFORE TMDB enrichment so that
        // epg_programmes is up-to-date when we query uncached-titles.
        if (!string.IsNullOrWhiteSpace(plugin.Configuration.WorkerBaseUrl))
        {
            _logger.LogInformation("[JellyTV] SourceSyncTask: pushing EPG XML to Worker.");
            try
            {
                var epgResult = await plugin.EpgFilter
                    .SyncAllToWorkerAsync(cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "[JellyTV] SourceSyncTask: EPG sync done. sources={Sources}, programmes={Programmes}",
                    epgResult.Sources.Count, epgResult.Sources.Sum(s => s.ProgrammesKept));
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[JellyTV] SourceSyncTask: EPG sync cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JellyTV] SourceSyncTask: EPG sync failed.");
            }
        }

        progress.Report(60);

        // ── TMDB enrichment ──────────────────────────────────────────────────
        // Runs after EPG sync so epg_programmes is populated with fresh titles
        // when we query uncached-titles on the Worker.
        // Only runs when both TmdbApiKey and WorkerBaseUrl are configured.
        if (!string.IsNullOrWhiteSpace(plugin.Configuration.TmdbApiKey) &&
            !string.IsNullOrWhiteSpace(plugin.Configuration.WorkerBaseUrl))
        {
            _logger.LogInformation("[JellyTV] SourceSyncTask: starting TMDB enrichment.");
            try
            {
                var enrichProgress = new Progress<double>(p =>
                    progress.Report(60.0 + p * 0.4));

                var summary = await plugin.TmdbEnrichment
                    .EnrichAsync(enrichProgress, cancellationToken)
                    .ConfigureAwait(false);

                if (summary.Error is not null)
                {
                    _logger.LogWarning("[JellyTV] SourceSyncTask: enrichment skipped — {Error}", summary.Error);
                }
                else
                {
                    _logger.LogInformation(
                        "[JellyTV] SourceSyncTask: enrichment done. titles={TitlesFound}, enriched={Enriched}, saved={Saved}, errors={Errors}",
                        summary.TitlesFound, summary.Enriched, summary.Saved, summary.Errors);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[JellyTV] SourceSyncTask: enrichment cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JellyTV] SourceSyncTask: enrichment failed.");
            }
        }
        else
        {
            _logger.LogDebug("[JellyTV] SourceSyncTask: TMDB enrichment skipped (TmdbApiKey or WorkerBaseUrl not set).");
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Plugin.Instance?.Configuration.SourceSyncIntervalHours ?? 24;
        return
        [
            new TaskTriggerInfo
            {
                Type          = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks
            }
        ];
    }
}
