using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

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
