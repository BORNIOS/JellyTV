using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public sealed class NegativeCacheService : INegativeCacheService
{
    private readonly ConcurrentDictionary<string, NegativeCacheEntry> _store = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool ShouldSkip(string normalizedTitle, DateTimeOffset now)
    {
        if (!_store.TryGetValue(normalizedTitle, out var entry))
        {
            return false;
        }

        if (entry.NoMatchUntil <= now)
        {
            _store.TryRemove(normalizedTitle, out _);
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public void MarkNoMatch(string normalizedTitle, DateTimeOffset now, TimeSpan ttl)
    {
        _store.AddOrUpdate(
            normalizedTitle,
            _ => new NegativeCacheEntry
            {
                NoMatchUntil = now.Add(ttl),
                LastAttempt = now,
                AttemptCount = 1
            },
            (_, old) => new NegativeCacheEntry
            {
                NoMatchUntil = now.Add(ttl),
                LastAttempt = now,
                AttemptCount = old.AttemptCount + 1
            });
    }
}
