using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public interface INegativeCacheService
{
    bool ShouldSkip(string normalizedTitle, DateTimeOffset now);

    void MarkNoMatch(string normalizedTitle, DateTimeOffset now, TimeSpan ttl);
}
