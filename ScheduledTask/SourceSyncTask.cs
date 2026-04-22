using System;
using System.Collections.Generic;
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
