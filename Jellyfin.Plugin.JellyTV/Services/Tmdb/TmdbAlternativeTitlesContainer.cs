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

internal sealed class TmdbAlternativeTitlesContainer
{
    [JsonPropertyName("titles")]
    public List<TmdbAlternativeTitle>? Titles { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbAlternativeTitle>? Results { get; set; }
}
