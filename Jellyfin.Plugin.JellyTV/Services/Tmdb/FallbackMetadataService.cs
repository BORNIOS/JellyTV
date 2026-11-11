using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Segunda y tercera opción cuando TMDB no da un resultado confiable: primero los proveedores de metadatos del propio
/// Jellyfin (los mismos que usan las bibliotecas: TMDb, OMDb, TVmaze, etc. según lo que el servidor tenga instalado) y
/// después TVmaze directamente, que cubre muy bien telenovelas, talk shows y programas de TV que TMDB conoce poco.
/// Solo aportan imagen, sinopsis y tipo; nunca reclasifican un programa que la fuente ya describe.
/// </summary>
internal sealed class FallbackMetadataService
{
    private static readonly Regex Html = new("<[^>]+>", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    // TVmaze limita la tasa por IP: una sola consulta a la vez y una pausa corta entre ellas.
    private static readonly SemaphoreSlim TvMazeGate = new(1, 1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly TmdbNormalizationService _normalizer;

    internal FallbackMetadataService(IHttpClientFactory httpClientFactory, ILogger logger, TmdbNormalizationService normalizer)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _normalizer = normalizer;
    }

    /// <summary>
    /// Busca el programa en los proveedores alternativos, en orden, y devuelve el primer resultado que la fuente no contradice.
    /// </summary>
    /// <param name="item">Programa que se busca.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>El resultado, o <see cref="TmdbMetadata.NoMatch"/>.</returns>
    internal async Task<TmdbMetadata> LookupAsync(UncachedProgrammeItem item, CancellationToken ct)
    {
        var wanted = _normalizer.NormalizeTitle(item.Title);
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return TmdbMetadata.NoMatch;
        }

        var native = await TryNativeAsync(item, wanted, ct).ConfigureAwait(false);
        if (native.IsMatch)
        {
            return native;
        }

        // TVmaze solo conoce series y programas de TV.
        return item.ExpectedMediaType == "movie" ? TmdbMetadata.NoMatch : await TryTvMazeAsync(item, wanted, ct).ConfigureAwait(false);
    }

    private static string? ShowCountry(JsonElement show)
    {
        foreach (var network in new[] { "network", "webChannel" })
        {
            if (show.TryGetProperty(network, out var n) && n.ValueKind == JsonValueKind.Object
                && n.TryGetProperty("country", out var c) && c.ValueKind == JsonValueKind.Object
                && c.TryGetProperty("code", out var code) && code.GetString() is { Length: 2 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string? StripHtml(string? html)
        => string.IsNullOrWhiteSpace(html) ? null : WebUtility.HtmlDecode(Html.Replace(html, string.Empty)).Trim();

    private async Task<TmdbMetadata> TryNativeAsync(UncachedProgrammeItem item, string wanted, CancellationToken ct)
    {
        var providers = Plugin.NativeProviders;
        if (providers is null)
        {
            return TmdbMetadata.NoMatch;
        }

        try
        {
            if (item.ExpectedMediaType != "movie")
            {
                var series = await providers.GetRemoteSearchResults<Series, SeriesInfo>(
                    new RemoteSearchQuery<SeriesInfo>
                    {
                        SearchInfo = new SeriesInfo { Name = item.Title, Year = item.Year, MetadataLanguage = "es", MetadataCountryCode = item.Country ?? "MX" },
                    },
                    ct).ConfigureAwait(false);
                var hit = Pick(series, item, wanted, "tv");
                if (hit.IsMatch)
                {
                    return hit;
                }
            }

            if (item.ExpectedMediaType != "tv")
            {
                var movies = await providers.GetRemoteSearchResults<Movie, MovieInfo>(
                    new RemoteSearchQuery<MovieInfo>
                    {
                        SearchInfo = new MovieInfo { Name = item.Title, Year = item.Year, MetadataLanguage = "es", MetadataCountryCode = item.Country ?? "MX" },
                    },
                    ct).ConfigureAwait(false);
                var hit = Pick(movies, item, wanted, "movie");
                if (hit.IsMatch)
                {
                    return hit;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[JellyTV] Búsqueda nativa falló para '{Title}'", item.Title);
            }
        }

        return TmdbMetadata.NoMatch;
    }

    private TmdbMetadata Pick(IEnumerable<MediaBrowser.Model.Providers.RemoteSearchResult> results, UncachedProgrammeItem item, string wanted, string mediaType)
    {
        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.Name)
                || !string.Equals(_normalizer.NormalizeTitle(result.Name), wanted, StringComparison.Ordinal)
                || !EnrichmentGate.AcceptsYear(item, result.ProductionYear))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(result.ImageUrl) && string.IsNullOrWhiteSpace(result.Overview))
            {
                continue;
            }

            return new TmdbMetadata
            {
                TmdbId = 1,
                MediaType = mediaType,
                ImageUrl = result.ImageUrl,
                Overview = result.Overview,
                ReleaseYear = result.ProductionYear,
                SourceName = result.SearchProviderName ?? "Jellyfin",
            };
        }

        return TmdbMetadata.NoMatch;
    }

    private async Task<TmdbMetadata> TryTvMazeAsync(UncachedProgrammeItem item, string wanted, CancellationToken ct)
    {
        await TvMazeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Delay(550, ct).ConfigureAwait(false);
            var client = _httpClientFactory.CreateClient("JellyTV");
            using var response = await client.GetAsync("https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(item.Title), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return TmdbMetadata.NoMatch;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);

            // Con país definido, primero las series de ese país (red de TV o canal web); luego el resto, en el orden de TVmaze.
            var entries = doc.RootElement.EnumerateArray().OrderByDescending(e => e.TryGetProperty("show", out var sh) && ShowCountry(sh) is { } cc && string.Equals(cc, item.Country, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var entry in entries)
            {
                if (!entry.TryGetProperty("show", out var show))
                {
                    continue;
                }

                var name = show.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !string.Equals(_normalizer.NormalizeTitle(name), wanted, StringComparison.Ordinal))
                {
                    continue;
                }

                int? year = show.TryGetProperty("premiered", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: >= 4 } premiered
                    && int.TryParse(premiered.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
                if (!EnrichmentGate.AcceptsYear(item, year))
                {
                    continue;
                }

                string? image = null;
                if (show.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
                {
                    image = img.TryGetProperty("original", out var o) ? o.GetString() : img.TryGetProperty("medium", out var m) ? m.GetString() : null;
                }

                var summary = StripHtml(show.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null);
                if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(summary))
                {
                    continue;
                }

                return new TmdbMetadata
                {
                    TmdbId = show.TryGetProperty("id", out var id) && id.TryGetInt32(out var tvmazeId) ? tvmazeId : 1,
                    MediaType = "tv",
                    ImageUrl = image?.Replace("http://", "https://", StringComparison.Ordinal),
                    Overview = summary,
                    ReleaseYear = year,
                    SourceName = "TVmaze",
                };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[JellyTV] TVmaze falló para '{Title}'", item.Title);
            }
        }
        finally
        {
            TvMazeGate.Release();
        }

        return TmdbMetadata.NoMatch;
    }
}
