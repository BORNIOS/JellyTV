using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Negative-cache record for a title that did not match any TMDB result.
/// </summary>
public sealed class NegativeCacheEntry
{
    /// <summary>Gets the instant until which the title is skipped.</summary>
    public DateTimeOffset NoMatchUntil { get; init; }

    /// <summary>Gets the instant of the last match attempt.</summary>
    public DateTimeOffset LastAttempt { get; init; }

    /// <summary>Gets the number of failed match attempts.</summary>
    public int AttemptCount { get; init; }
}
