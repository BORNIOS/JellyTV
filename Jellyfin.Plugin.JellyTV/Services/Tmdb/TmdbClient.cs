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

namespace JellyTV.Services.Tmdb;

internal sealed class TmdbClientMetrics
{
    public int Requests;
    public int Retries;
}

internal interface ITmdbClient
{
    TmdbClientMetrics Metrics { get; }
    Task<TmdbSearchResponse?> SearchMultiAsync(string apiKey, string query, string language, CancellationToken ct);
    Task<TmdbDetailResponse?> GetDetailAsync(string apiKey, string mediaType, int id, string language, CancellationToken ct);
}

internal sealed class TmdbClient : ITmdbClient
{
    private static readonly JsonSerializerOptions JsonCaseInsensitive = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<HttpStatusCode> Retryable =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    private const string TmdbBase = "https://api.themoviedb.org/3";
    private readonly HttpClient _client;
    private readonly ITmdbRateLimiter _rateLimiter;
    private readonly ILogger _logger;
    public TmdbClient(HttpClient client, ITmdbRateLimiter rateLimiter, ILogger logger)
    {
        _client = client;
        _rateLimiter = rateLimiter;
        _logger = logger;
        Metrics = new TmdbClientMetrics();
    }

    public TmdbClientMetrics Metrics { get; }

    public Task<TmdbSearchResponse?> SearchMultiAsync(string apiKey, string query, string language, CancellationToken ct)
    {
        var url = $"{TmdbBase}/search/multi?api_key={Uri.EscapeDataString(apiKey)}&query={Uri.EscapeDataString(query)}&language={language}&include_adult=false&page=1";
        return SendWithRetryAsync<TmdbSearchResponse>(url, ct);
    }

    public Task<TmdbDetailResponse?> GetDetailAsync(string apiKey, string mediaType, int id, string language, CancellationToken ct)
    {
        var append = "credits,images,external_ids,videos,release_dates,content_ratings,keywords,translations,alternative_titles";
        var url = $"{TmdbBase}/{mediaType}/{id}?api_key={Uri.EscapeDataString(apiKey)}&language={language}&append_to_response={append}";
        return SendWithRetryAsync<TmdbDetailResponse>(url, ct);
    }

    private async Task<T?> SendWithRetryAsync<T>(string url, CancellationToken ct) where T : class
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);

            try
            {
                Interlocked.Increment(ref Metrics.Requests);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/json");

                using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadFromJsonAsync<T>(JsonCaseInsensitive, ct).ConfigureAwait(false);
                }

                if (!Retryable.Contains(resp.StatusCode) || attempt == maxAttempts)
                {
                    return null;
                }

                var delay = ComputeBackoffWithJitter(attempt);
                Interlocked.Increment(ref Metrics.Retries);
                _logger.LogDebug("[JellyTV] TMDB retry attempt={Attempt} status={Status} waitMs={Delay}", attempt, (int)resp.StatusCode, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                var delay = ComputeBackoffWithJitter(attempt);
                Interlocked.Increment(ref Metrics.Retries);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch
            {
                if (attempt == maxAttempts) return null;
            }
        }

        return null;
    }

    private TimeSpan ComputeBackoffWithJitter(int attempt)
    {
        var baseMs = Math.Min(6000, 250 * Math.Pow(2, attempt - 1));
        var jitterMs = Random.Shared.Next(50, 350);
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }
}

internal sealed class TmdbSearchResponse
{
    [JsonPropertyName("results")]
    public List<TmdbSearchResult> Results { get; set; } = [];
}

internal sealed class TmdbSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("origin_country")]
    public List<string>? OriginCountry { get; set; }

    [JsonPropertyName("popularity")]
    public double? Popularity { get; set; }
}

internal sealed class TmdbDetailResponse
{
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; set; }

    [JsonPropertyName("popularity")]
    public double? Popularity { get; set; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("number_of_seasons")]
    public int? NumberOfSeasons { get; set; }

    [JsonPropertyName("number_of_episodes")]
    public int? NumberOfEpisodes { get; set; }

    [JsonPropertyName("episode_run_time")]
    public List<int>? EpisodeRunTime { get; set; }

    [JsonPropertyName("genres")]
    public List<TmdbGenre>? Genres { get; set; }

    [JsonPropertyName("origin_country")]
    public List<string>? OriginCountry { get; set; }

    [JsonPropertyName("production_companies")]
    public List<TmdbCompany>? ProductionCompanies { get; set; }

    [JsonPropertyName("production_countries")]
    public List<TmdbCountry>? ProductionCountries { get; set; }

    [JsonPropertyName("networks")]
    public List<TmdbNetwork>? Networks { get; set; }

    [JsonPropertyName("created_by")]
    public List<TmdbCreator>? CreatedBy { get; set; }

    [JsonPropertyName("credits")]
    public TmdbCredits? Credits { get; set; }

    [JsonPropertyName("images")]
    public TmdbImages? Images { get; set; }

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; set; }

    [JsonPropertyName("videos")]
    public TmdbVideos? Videos { get; set; }

    [JsonPropertyName("release_dates")]
    public TmdbReleaseDatesContainer? ReleaseDates { get; set; }

    [JsonPropertyName("content_ratings")]
    public TmdbContentRatingsContainer? ContentRatings { get; set; }

    [JsonPropertyName("keywords")]
    public TmdbKeywordsContainer? Keywords { get; set; }

    [JsonPropertyName("translations")]
    public TmdbTranslationsContainer? Translations { get; set; }

    [JsonPropertyName("alternative_titles")]
    public TmdbAlternativeTitlesContainer? AlternativeTitles { get; set; }
}

internal sealed class TmdbGenre { [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; }
internal sealed class TmdbCompany { [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; }
internal sealed class TmdbNetwork { [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; }
internal sealed class TmdbCreator { [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; }
internal sealed class TmdbCountry { [JsonPropertyName("iso_3166_1")] public string Iso31661 { get; set; } = string.Empty; }

internal sealed class TmdbCredits
{
    [JsonPropertyName("crew")]
    public List<TmdbCrewMember>? Crew { get; set; }

    [JsonPropertyName("cast")]
    public List<TmdbCastMember>? Cast { get; set; }
}

internal sealed class TmdbCrewMember
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("job")]
    public string Job { get; set; } = string.Empty;

    [JsonPropertyName("department")]
    public string Department { get; set; } = string.Empty;
}

internal sealed class TmdbCastMember
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

internal sealed class TmdbImages
{
    [JsonPropertyName("logos")]
    public List<TmdbImage>? Logos { get; set; }
}

internal sealed class TmdbImage
{
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }
}

internal sealed class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("tvdb_id")]
    public string? TvdbId { get; set; }

    [JsonPropertyName("wikidata_id")]
    public string? WikidataId { get; set; }
}

internal sealed class TmdbVideos
{
    [JsonPropertyName("results")]
    public List<TmdbVideo>? Results { get; set; }
}

internal sealed class TmdbVideo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string? Site { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("official")]
    public bool? Official { get; set; }
}

internal sealed class TmdbReleaseDatesContainer
{
    [JsonPropertyName("results")]
    public List<TmdbReleaseDatesByCountry>? Results { get; set; }
}

internal sealed class TmdbReleaseDatesByCountry
{
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; set; }

    [JsonPropertyName("release_dates")]
    public List<TmdbReleaseDateRow>? ReleaseDates { get; set; }
}

internal sealed class TmdbReleaseDateRow
{
    [JsonPropertyName("certification")]
    public string? Certification { get; set; }
}

internal sealed class TmdbContentRatingsContainer
{
    [JsonPropertyName("results")]
    public List<TmdbContentRating>? Results { get; set; }
}

internal sealed class TmdbContentRating
{
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; set; }

    [JsonPropertyName("rating")]
    public string? Rating { get; set; }
}

internal sealed class TmdbKeywordsContainer
{
    [JsonPropertyName("keywords")]
    public List<TmdbKeyword>? Keywords { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbKeyword>? Results { get; set; }
}

internal sealed class TmdbKeyword
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class TmdbTranslationsContainer
{
    [JsonPropertyName("translations")]
    public List<TmdbTranslation>? Translations { get; set; }
}

internal sealed class TmdbTranslation
{
    [JsonPropertyName("iso_639_1")]
    public string? Iso6391 { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("english_name")]
    public string? EnglishName { get; set; }
}

internal sealed class TmdbAlternativeTitlesContainer
{
    [JsonPropertyName("titles")]
    public List<TmdbAlternativeTitle>? Titles { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbAlternativeTitle>? Results { get; set; }
}

internal sealed class TmdbAlternativeTitle
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
