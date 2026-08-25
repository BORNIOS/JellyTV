using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public sealed class EnrichmentRunSummary
{
    public int TitlesFound { get; set; }
    public int TitlesUnique { get; set; }
    public int Enriched { get; set; }
    public int Saved { get; set; }
    public int Errors { get; set; }
    public int PushErrors { get; set; }
    public int SkippedByHeuristics { get; set; }
    public int MemoryCacheHits { get; set; }
    public int NegativeCacheSkips { get; set; }
    public int TmdbRequests { get; set; }
    public int RetryCount { get; set; }
    public int RateLimitWaits { get; set; }
    public string? Error { get; set; }
}

public sealed class EnrichBatchEntry
{
    [JsonPropertyName("titleNormalized")]
    public string TitleNormalized { get; set; } = string.Empty;

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("posterPath")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdropPath")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("logoPath")]
    public string? LogoPath { get; set; }

    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = [];

    [JsonPropertyName("cast")]
    public List<EnrichCastMember> Cast { get; set; } = [];

    [JsonPropertyName("originCountry")]
    public string? OriginCountry { get; set; }

    [JsonPropertyName("releaseYear")]
    public int? ReleaseYear { get; set; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("voteAverage")]
    public double? VoteAverage { get; set; }

    [JsonPropertyName("voteCount")]
    public int? VoteCount { get; set; }

    [JsonPropertyName("popularity")]
    public double? Popularity { get; set; }

    [JsonPropertyName("originalLanguage")]
    public string? OriginalLanguage { get; set; }

    [JsonPropertyName("productionCompanies")]
    public List<string> ProductionCompanies { get; set; } = [];

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("trailers")]
    public List<EnrichTrailer> Trailers { get; set; } = [];

    [JsonPropertyName("certifications")]
    public List<EnrichCertification> Certifications { get; set; } = [];

    [JsonPropertyName("numberOfSeasons")]
    public int? NumberOfSeasons { get; set; }

    [JsonPropertyName("numberOfEpisodes")]
    public int? NumberOfEpisodes { get; set; }

    [JsonPropertyName("networks")]
    public List<string> Networks { get; set; } = [];

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("episodeRuntimes")]
    public List<int> EpisodeRuntimes { get; set; } = [];

    [JsonPropertyName("createdBy")]
    public List<string> CreatedBy { get; set; } = [];

    [JsonPropertyName("externalIds")]
    public Dictionary<string, string?> ExternalIds { get; set; } = [];

    [JsonPropertyName("translations")]
    public List<string> Translations { get; set; } = [];

    [JsonPropertyName("alternativeTitles")]
    public List<string> AlternativeTitles { get; set; } = [];

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = [];
}

public sealed class EnrichCastMember
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}

public sealed class EnrichTrailer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string? Site { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class EnrichCertification
{
    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("rating")]
    public string Rating { get; set; } = string.Empty;
}

public sealed record UncachedProgrammeItem(
    string Title,
    string? Description = null,
    string? Category = null,
    int? Year = null,
    /// <summary>Secondary title (e.g. Spanish when primary is English) used as additional search variant.</summary>
    string? FallbackTitle = null);

/// <summary>
/// Per-source TMDB enrichment statistics produced by inline enrichment during EPG filtering.
/// Included in <see cref="JellyTV.Services.EpgSourceSyncResult"/>.
/// </summary>
public sealed class EpgEnrichmentStats
{
    public int ProgrammesInput   { get; set; }
    public int UniqueAfterDedup  { get; set; }
    public int Enriched          { get; set; }
    public int SkippedByHeuristics { get; set; }
    public int MemoryCacheHits   { get; set; }
    public int NegativeCacheSkips { get; set; }
    public int NoMatch           { get; set; }
    public int Errors            { get; set; }
    public int TmdbRequests      { get; set; }
    public int Retries           { get; set; }
    public int Saved             { get; set; }
}

internal sealed class TmdbMetadata
{
    public static readonly TmdbMetadata NoMatch = new();

    public int? TmdbId { get; init; }
    public string? MediaType { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public string? LogoPath { get; init; }
    public List<string> Genres { get; init; } = [];
    public List<EnrichCastMember> Cast { get; init; } = [];
    public string? OriginCountry { get; init; }
    public int? ReleaseYear { get; init; }
    public string? Tagline { get; init; }
    public string? ImdbId { get; init; }
    public int? Runtime { get; init; }
    public double? VoteAverage { get; init; }
    public int? VoteCount { get; init; }
    public double? Popularity { get; init; }
    public string? OriginalLanguage { get; init; }
    public List<string> ProductionCompanies { get; init; } = [];
    public string? ReleaseDate { get; init; }
    public List<EnrichTrailer> Trailers { get; init; } = [];
    public List<EnrichCertification> Certifications { get; init; } = [];
    public int? NumberOfSeasons { get; init; }
    public int? NumberOfEpisodes { get; init; }
    public List<string> Networks { get; init; } = [];
    public string? Status { get; init; }
    public List<int> EpisodeRuntimes { get; init; } = [];
    public List<string> CreatedBy { get; init; } = [];
    public Dictionary<string, string?> ExternalIds { get; init; } = [];
    public List<string> Translations { get; init; } = [];
    public List<string> AlternativeTitles { get; init; } = [];
    public List<string> Keywords { get; init; } = [];

    public bool IsMatch => TmdbId.HasValue;
}
