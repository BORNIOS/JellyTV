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

internal sealed class TmdbKeywordsContainer
{
    [JsonPropertyName("keywords")]
    public List<TmdbKeyword>? Keywords { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbKeyword>? Results { get; set; }
}
