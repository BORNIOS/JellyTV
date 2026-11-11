using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Serializes TMDB calls to respect the provider rate limit.
/// </summary>
public interface ITmdbRateLimiter
{
    /// <summary>Gets the number of callers that had to wait for a turn.</summary>
    int WaitEvents { get; }

    /// <summary>
    /// Waits until the next TMDB request is allowed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when a request may be issued.</returns>
    Task WaitTurnAsync(CancellationToken ct);
}
