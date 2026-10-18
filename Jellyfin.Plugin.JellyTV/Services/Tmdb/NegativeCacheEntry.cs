using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public sealed class NegativeCacheEntry
{
    public DateTimeOffset NoMatchUntil { get; init; }

    public DateTimeOffset LastAttempt { get; init; }

    public int AttemptCount { get; init; }
}
