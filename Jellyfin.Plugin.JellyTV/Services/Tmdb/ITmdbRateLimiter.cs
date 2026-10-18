using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public interface ITmdbRateLimiter
{
    int WaitEvents { get; }

    Task WaitTurnAsync(CancellationToken ct);
}
