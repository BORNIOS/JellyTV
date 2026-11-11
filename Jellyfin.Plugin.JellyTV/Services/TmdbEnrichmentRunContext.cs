using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

internal sealed class TmdbEnrichmentRunContext
{
    internal TmdbEnrichmentRunContext(
        string tmdbApiKey,
        HttpClient httpClient,
        int maxConcurrency,
        TimeSpan noMatchTtl,
        ILogger logger)
    {
        TmdbApiKey = tmdbApiKey;
        MaxConcurrency = maxConcurrency;
        NoMatchTtl = noMatchTtl;
        RateLimiter = new SlidingWindowTmdbRateLimiter(maxRequests: 40, window: TimeSpan.FromSeconds(10));
        TmdbClient = new TmdbClient(httpClient, RateLimiter, logger);
    }

    internal ConcurrentDictionary<string, TmdbMetadata> MemoryCache { get; }
        = new(StringComparer.Ordinal);

    internal SlidingWindowTmdbRateLimiter RateLimiter { get; }

    internal ITmdbClient TmdbClient { get; }

    internal int MaxConcurrency { get; }

    internal TimeSpan NoMatchTtl { get; }

    internal string TmdbApiKey { get; }
}
