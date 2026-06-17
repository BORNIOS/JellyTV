using System;
using System.Collections.Concurrent;

namespace JellyTV.Services.Tmdb;

public sealed class NegativeCacheEntry
{
    public DateTimeOffset NoMatchUntil { get; init; }
    public DateTimeOffset LastAttempt { get; init; }
    public int AttemptCount { get; init; }
}

public interface INegativeCacheService
{
    bool ShouldSkip(string normalizedTitle, DateTimeOffset now);
    void MarkNoMatch(string normalizedTitle, DateTimeOffset now, TimeSpan ttl);
}

public sealed class NegativeCacheService : INegativeCacheService
{
    private readonly ConcurrentDictionary<string, NegativeCacheEntry> _store = new(StringComparer.Ordinal);

    public bool ShouldSkip(string normalizedTitle, DateTimeOffset now)
    {
        if (!_store.TryGetValue(normalizedTitle, out var entry)) return false;
        if (entry.NoMatchUntil <= now)
        {
            _store.TryRemove(normalizedTitle, out _);
            return false;
        }

        return true;
    }

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
