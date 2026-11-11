using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyTV.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// In-memory index over the channel graph that answers "which source do I use now?" without touching
/// the database, and persists the resulting decisions. Health checks and playback failures only need a
/// dictionary lookup plus the ordered backup list of one channel.
/// </summary>
public sealed class ChannelCatalog
{
    private readonly StorageBackend _storage;
    private readonly ILogger<ChannelCatalog> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveEntry> _active = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelCatalog"/> class.
    /// </summary>
    /// <param name="storage">Active storage backend.</param>
    /// <param name="logger">Logger.</param>
    public ChannelCatalog(StorageBackend storage, ILogger<ChannelCatalog> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>Gets the number of channels with a persisted active source.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }

    /// <summary>
    /// Loads the persisted active-source pointers. The pointers are advisory: they are validated against
    /// the current configuration, so a stale id never selects a source that no longer exists.
    /// </summary>
    /// <param name="channels">Configured channels.</param>
    public void Hydrate(IReadOnlyList<ChannelEntry> channels)
    {
        IReadOnlyList<ActiveSourceRecord> stored;
        try
        {
            stored = _storage.Store.LoadActiveSources();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FAILOVER] Could not load active sources");
            return;
        }

        var known = channels
            .SelectMany(channel =>
            {
                channel.EnsureMigratedSources();
                return channel.Sources.Select(source => (channel.Id, source.Id));
            })
            .ToHashSet();

        lock (_gate)
        {
            _active.Clear();
            foreach (var record in stored)
            {
                if (known.Contains((record.ChannelId, record.SourceId)))
                {
                    _active[record.ChannelId] = new ActiveEntry(record.SourceId, record.SwitchedAt);
                }
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[FAILOVER] Loaded {Count} active source pointers", ActiveCount);
        }
    }

    /// <summary>
    /// Orders the sources of a channel for playback: requested first, then the persisted active source,
    /// then healthy backups by priority, then unknown ones and finally the known-dead ones.
    /// </summary>
    /// <param name="channel">Channel to inspect.</param>
    /// <param name="requestedUrl">Source URL requested by the player, if any.</param>
    /// <returns>Candidate sources in failover order.</returns>
    public IReadOnlyList<ChannelSource> OrderSources(ChannelEntry channel, string? requestedUrl = null)
    {
        channel.EnsureMigratedSources();
        var enabled = channel.Sources
            .Where(source => source.IsEnabled && !string.IsNullOrWhiteSpace(source.Url))
            .ToList();
        if (enabled.Count == 0)
        {
            return Array.Empty<ChannelSource>();
        }

        var activeId = GetActiveSourceId(channel.Id);
        var ordered = new List<ChannelSource>(enabled.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(ChannelSource? source)
        {
            if (source is not null && seen.Add(source.Id))
            {
                ordered.Add(source);
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedUrl))
        {
            Add(enabled.Find(source => string.Equals(source.Url, requestedUrl, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(activeId))
        {
            Add(enabled.Find(source => string.Equals(source.Id, activeId, StringComparison.Ordinal)));
        }

        foreach (var source in enabled.Where(source => source.IsAlive == true))
        {
            Add(source);
        }

        foreach (var source in enabled.Where(source => source.IsAlive is null))
        {
            Add(source);
        }

        foreach (var source in enabled.Where(source => source.IsAlive == false))
        {
            Add(source);
        }

        return ordered;
    }

    /// <summary>
    /// Selects the best source for a channel.
    /// </summary>
    /// <param name="channel">Channel to inspect.</param>
    /// <param name="requestedUrl">Source URL requested by the player, if any.</param>
    /// <returns>The selected source, or <c>null</c> when the channel has no usable source.</returns>
    public ChannelSource? SelectSource(ChannelEntry channel, string? requestedUrl = null)
    {
        var ordered = OrderSources(channel, requestedUrl);
        return ordered.Count > 0 ? ordered[0] : null;
    }

    /// <summary>
    /// Persists the health outcome of the whole run and switches the active source of any channel whose
    /// current source is dead.
    /// </summary>
    /// <param name="channels">Channels that were checked.</param>
    public void RecordHealth(IReadOnlyList<ChannelEntry> channels)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var records = new List<SourceHealthRecord>();
        var switches = new List<ActiveSourceRecord>();

        foreach (var channel in channels)
        {
            channel.EnsureMigratedSources();
            var enabled = channel.Sources
                .Where(source => source.IsEnabled && !string.IsNullOrWhiteSpace(source.Url))
                .ToList();
            if (enabled.Count == 0)
            {
                continue;
            }

            foreach (var source in enabled)
            {
                if (source.LastCheckedAt.HasValue || source.IsAlive.HasValue)
                {
                    records.Add(new SourceHealthRecord(
                        channel.Id,
                        source.Id,
                        source.LastCheckedAt ?? checkedAt,
                        source.IsAlive == true,
                        source.LastError));
                }
            }

            var activeId = GetActiveSourceId(channel.Id);
            var current = enabled.Find(source => string.Equals(source.Id, activeId, StringComparison.Ordinal));
            var next = current?.IsAlive == true
                ? current
                : enabled.Find(source => source.IsAlive == true);

            if (next is null)
            {
                // Ninguna fuente viva: se conserva la actual para no oscilar; el próximo run reintenta.
                continue;
            }

            if (current is null || !string.Equals(current.Id, next.Id, StringComparison.Ordinal))
            {
                SetActive(channel.Id, next.Id, "health", checkedAt);
                switches.Add(new ActiveSourceRecord(channel.Id, next.Id, "health", checkedAt));
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[FAILOVER] {Channel}: {From} -> {To} (health)",
                        channel.Name,
                        current?.Label ?? current?.Id ?? "none",
                        next.Label ?? next.Id);
                }
            }
        }

        TryPersist(() => _storage.Store.SaveHealth(records), "health events");
        TryPersist(() => _storage.Store.SaveActiveSources(switches), "active sources");
    }

    /// <summary>
    /// Marks a source as unusable after a playback failure and moves the channel to the next candidate
    /// immediately, so the following request does not retry the dead source.
    /// </summary>
    /// <param name="channel">Channel whose source failed.</param>
    /// <param name="sourceId">Failed source identifier.</param>
    /// <param name="reason">Failure reason.</param>
    public void MarkSourceUnavailable(ChannelEntry channel, string sourceId, string reason)
    {
        channel.EnsureMigratedSources();
        var failed = channel.Sources.Find(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal));
        if (failed is not null)
        {
            failed.IsAlive = false;
            failed.LastError = reason;
            failed.LastCheckedAt = DateTimeOffset.UtcNow;
        }

        ChannelSource? next = null;
        foreach (var candidate in OrderSources(channel))
        {
            if (!string.Equals(candidate.Id, sourceId, StringComparison.Ordinal) && candidate.IsAlive != false)
            {
                next = candidate;
                break;
            }
        }

        if (next is null)
        {
            return;
        }

        var switchedAt = DateTimeOffset.UtcNow;
        SetActive(channel.Id, next.Id, reason, switchedAt);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[FAILOVER] {Channel}: {From} -> {To} ({Reason})",
                channel.Name,
                failed?.Label ?? sourceId,
                next.Label ?? next.Id,
                reason);
        }

        TryPersist(() => _storage.Store.SaveActiveSources([new ActiveSourceRecord(channel.Id, next.Id, reason, switchedAt)]), "active sources");
        if (failed is not null)
        {
            var checkedAt = failed.LastCheckedAt ?? switchedAt;
            TryPersist(
                () => _storage.Store.SaveHealth([new SourceHealthRecord(channel.Id, failed.Id, checkedAt, false, reason)]),
                "health events");
        }
    }

    private string? GetActiveSourceId(string channelId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(channelId, out var entry) ? entry.SourceId : null;
        }
    }

    private void SetActive(string channelId, string sourceId, string reason, DateTimeOffset switchedAt)
    {
        lock (_gate)
        {
            _active[channelId] = new ActiveEntry(sourceId, switchedAt);
        }
    }

    private void TryPersist(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FAILOVER] Could not persist {What}", what);
        }
    }

    private sealed record ActiveEntry(string SourceId, DateTimeOffset SwitchedAt);
}
