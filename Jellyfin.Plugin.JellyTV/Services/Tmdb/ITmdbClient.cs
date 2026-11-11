using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

internal interface ITmdbClient
{
    TmdbClientMetrics Metrics { get; }

    Task<TmdbSearchResponse?> SearchMultiAsync(string apiKey, string query, string language, CancellationToken ct);

    Task<TmdbDetailResponse?> GetDetailAsync(string apiKey, string mediaType, int id, string language, CancellationToken ct);
}
