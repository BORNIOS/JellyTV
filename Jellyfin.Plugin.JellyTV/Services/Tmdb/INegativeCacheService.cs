using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Tracks titles that recently failed to match so they are not queried again immediately.
/// </summary>
public interface INegativeCacheService
{
    /// <summary>
    /// Determines whether the title should be skipped at the given instant.
    /// </summary>
    /// <param name="normalizedTitle">Normalized programme title.</param>
    /// <param name="now">Current instant.</param>
    /// <returns><c>true</c> when the title is still in the negative cache.</returns>
    bool ShouldSkip(string normalizedTitle, DateTimeOffset now);

    /// <summary>
    /// Records a failed match for the title.
    /// </summary>
    /// <param name="normalizedTitle">Normalized programme title.</param>
    /// <param name="now">Current instant.</param>
    /// <param name="ttl">How long the title remains in the negative cache.</param>
    void MarkNoMatch(string normalizedTitle, DateTimeOffset now, TimeSpan ttl);
}
