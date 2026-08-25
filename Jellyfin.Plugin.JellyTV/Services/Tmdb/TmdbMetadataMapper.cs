using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

internal interface ITmdbMetadataMapper
{
    TmdbMetadata Map(TmdbSearchResult search, TmdbDetailResponse? detail);
}

internal sealed class TmdbMetadataMapper : ITmdbMetadataMapper
{
    private const int MaxCastActors = 8;

    public TmdbMetadata Map(TmdbSearchResult search, TmdbDetailResponse? detail)
    {
        if (detail == null)
        {
            return new TmdbMetadata
            {
                TmdbId = search.Id,
                MediaType = search.MediaType,
                Overview = search.Overview,
                PosterPath = search.PosterPath,
                BackdropPath = search.BackdropPath,
                OriginCountry = search.OriginCountry?.FirstOrDefault(),
                ReleaseYear = ParseYear(search.ReleaseDate ?? search.FirstAirDate),
                Popularity = search.Popularity
            };
        }

        var releaseDate = detail.ReleaseDate ?? detail.FirstAirDate;

        return new TmdbMetadata
        {
            TmdbId = search.Id,
            MediaType = search.MediaType,
            Overview = detail.Overview ?? search.Overview,
            PosterPath = detail.PosterPath ?? search.PosterPath,
            BackdropPath = detail.BackdropPath ?? search.BackdropPath,
            LogoPath = detail.Images?.Logos?.FirstOrDefault()?.FilePath,
            Genres = detail.Genres?.Select(g => g.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            Cast = BuildCast(detail),
            OriginCountry = PickOriginCountry(detail) ?? search.OriginCountry?.FirstOrDefault(),
            ReleaseYear = ParseYear(releaseDate),
            Tagline = detail.Tagline,
            ImdbId = detail.ExternalIds?.ImdbId ?? detail.ImdbId,
            Runtime = detail.Runtime,
            VoteAverage = detail.VoteAverage,
            VoteCount = detail.VoteCount,
            Popularity = detail.Popularity ?? search.Popularity,
            OriginalLanguage = detail.OriginalLanguage,
            ProductionCompanies = detail.ProductionCompanies?.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            ReleaseDate = releaseDate,
            Trailers = BuildTrailers(detail),
            Certifications = BuildCertifications(detail),
            NumberOfSeasons = detail.NumberOfSeasons,
            NumberOfEpisodes = detail.NumberOfEpisodes,
            Networks = detail.Networks?.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            Status = detail.Status,
            EpisodeRuntimes = detail.EpisodeRunTime?.Where(x => x > 0).Distinct().ToList() ?? [],
            CreatedBy = detail.CreatedBy?.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            ExternalIds = BuildExternalIds(detail),
            Translations = BuildTranslations(detail),
            AlternativeTitles = BuildAlternativeTitles(detail),
            Keywords = BuildKeywords(detail)
        };
    }

    private static List<EnrichCastMember> BuildCast(TmdbDetailResponse detail)
    {
        var members = new List<EnrichCastMember>(MaxCastActors + 1);

        var director = detail.Credits?.Crew?
            .FirstOrDefault(c => c.Department == "Directing" && c.Job == "Director");
        if (director != null)
        {
            members.Add(new EnrichCastMember { Name = director.Name, Role = "director" });
        }

        foreach (var actor in (detail.Credits?.Cast ?? []).OrderBy(a => a.Order).Take(MaxCastActors))
        {
            members.Add(new EnrichCastMember { Name = actor.Name, Role = "actor" });
        }

        return members;
    }

    private static string? PickOriginCountry(TmdbDetailResponse detail)
    {
        if (detail.OriginCountry?.Count > 0) return detail.OriginCountry[0];
        if (detail.ProductionCountries?.Count > 0) return detail.ProductionCountries[0].Iso31661;
        return null;
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return null;
        return int.TryParse(date[..4], out var y) && y > 1880 ? y : null;
    }

    private static List<EnrichTrailer> BuildTrailers(TmdbDetailResponse detail)
    {
        return (detail.Videos?.Results ?? [])
            .Where(v => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Official ?? false)
            .ThenBy(v => v.Type)
            .Take(5)
            .Select(v => new EnrichTrailer
            {
                Name = v.Name,
                Site = v.Site,
                Key = v.Key,
                Type = v.Type
            })
            .ToList();
    }

    private static List<EnrichCertification> BuildCertifications(TmdbDetailResponse detail)
    {
        var result = new List<EnrichCertification>();

        foreach (var movie in detail.ReleaseDates?.Results ?? [])
        {
            var cert = movie.ReleaseDates?.Select(x => x.Certification).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(movie.Iso31661) && !string.IsNullOrWhiteSpace(cert))
            {
                result.Add(new EnrichCertification { Country = movie.Iso31661!, Rating = cert! });
            }
        }

        foreach (var tv in detail.ContentRatings?.Results ?? [])
        {
            if (!string.IsNullOrWhiteSpace(tv.Iso31661) && !string.IsNullOrWhiteSpace(tv.Rating))
            {
                result.Add(new EnrichCertification { Country = tv.Iso31661!, Rating = tv.Rating! });
            }
        }

        return result
            .GroupBy(x => $"{x.Country}:{x.Rating}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static Dictionary<string, string?> BuildExternalIds(TmdbDetailResponse detail)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(detail.ExternalIds?.ImdbId)) dict["imdb"] = detail.ExternalIds.ImdbId;
        if (!string.IsNullOrWhiteSpace(detail.ExternalIds?.TvdbId)) dict["tvdb"] = detail.ExternalIds.TvdbId;
        if (!string.IsNullOrWhiteSpace(detail.ExternalIds?.WikidataId)) dict["wikidata"] = detail.ExternalIds.WikidataId;
        return dict;
    }

    private static List<string> BuildTranslations(TmdbDetailResponse detail)
    {
        return (detail.Translations?.Translations ?? [])
            .Select(t => string.IsNullOrWhiteSpace(t.Iso6391) ? null : t.Iso6391)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Cast<string>()
            .ToList();
    }

    private static List<string> BuildAlternativeTitles(TmdbDetailResponse detail)
    {
        var items = new List<string>();
        items.AddRange((detail.AlternativeTitles?.Titles ?? []).Select(x => x.Title).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>());
        items.AddRange((detail.AlternativeTitles?.Results ?? []).Select(x => x.Title).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>());

        return items
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .Cast<string>()
            .ToList();
    }

    private static List<string> BuildKeywords(TmdbDetailResponse detail)
    {
        var items = new List<string>();
        items.AddRange((detail.Keywords?.Keywords ?? []).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>());
        items.AddRange((detail.Keywords?.Results ?? []).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>());

        return items
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .Cast<string>()
            .ToList();
    }
}
