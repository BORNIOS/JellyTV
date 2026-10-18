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
