using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

internal sealed class MatchWeights
{
    public double ExactNormalized { get; init; } = 1.00;
    public double TokenSetRatio { get; init; } = 0.75;
    public double TokenSortRatio { get; init; } = 0.55;
    public double LevenshteinRatio { get; init; } = 0.80;
    public double PartialRatio { get; init; } = 0.45;
    public double YearProximity { get; init; } = 0.30;
    public double PopularityBonus { get; init; } = 0.10;
    public double MediaTypeBonus { get; init; } = 0.20;
    public double CountryLangBonus { get; init; } = 0.12;
    public double AliasBonus { get; init; } = 0.18;
    public double MismatchPenalty { get; init; } = 0.45;
    public double MinimumAcceptableScore { get; init; } = 1.55;
}

internal sealed class CandidateScore
{
    public TmdbSearchResult Candidate { get; init; } = new();
    public double Score { get; init; }
    public string Breakdown { get; init; } = string.Empty;
}

internal interface IMatchEvaluator
{
    CandidateScore? SelectBest(
        UncachedProgrammeItem item,
        List<TmdbSearchResult> candidates,
        IReadOnlyList<string> preferredLanguages,
        string? expectedMediaType = null);
}

internal sealed class MatchEvaluator : IMatchEvaluator
{
    private static readonly Regex MultiSpace = new("\\s+", RegexOptions.Compiled);

    private readonly ITmdbNormalizationService _normalizer;
    private readonly MatchWeights _weights;

    public MatchEvaluator(ITmdbNormalizationService normalizer, MatchWeights? weights = null)
    {
        _normalizer = normalizer;
        _weights = weights ?? new MatchWeights();
    }

    public CandidateScore? SelectBest(
        UncachedProgrammeItem item,
        List<TmdbSearchResult> candidates,
        IReadOnlyList<string> preferredLanguages,
        string? expectedMediaType = null)
    {
        if (candidates.Count == 0) return null;

        var query = _normalizer.NormalizeTitle(item.Title);
        if (string.IsNullOrWhiteSpace(query)) return null;

        var scored = new List<CandidateScore>(candidates.Count);
        foreach (var c in candidates)
        {
            if (c.MediaType != "movie" && c.MediaType != "tv") continue;

            var candTitle = c.Title ?? c.Name ?? string.Empty;
            var candNorm = _normalizer.NormalizeTitle(candTitle);
            if (string.IsNullOrWhiteSpace(candNorm)) continue;

            var tokenSet = TokenSetRatio(query, candNorm);
            var tokenSort = TokenSortRatio(query, candNorm);
            var lev = LevenshteinSimilarity(query, candNorm);
            var partial = PartialRatio(query, candNorm);

            var exact = query == candNorm ? 1.0 : 0.0;
            var aliasBonus = AliasLikely(item, c) ? 1.0 : 0.0;

            var searchYear = item.Year;
            var candidateYear = ParseYear(c.ReleaseDate ?? c.FirstAirDate);
            var yearScore = ComputeYearScore(searchYear, candidateYear);

            var popBonus = Math.Clamp((c.Popularity ?? 0d) / 100d, 0d, 1d);
            var mediaBonus = string.IsNullOrWhiteSpace(expectedMediaType)
                ? 0.5
                : string.Equals(c.MediaType, expectedMediaType, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

            var langBonus = PreferredLanguageBonus(c, preferredLanguages);

            var mismatch = ComputeMismatchPenalty(query, candNorm, item, c);

            var score =
                (exact * _weights.ExactNormalized) +
                (tokenSet * _weights.TokenSetRatio) +
                (tokenSort * _weights.TokenSortRatio) +
                (lev * _weights.LevenshteinRatio) +
                (partial * _weights.PartialRatio) +
                (yearScore * _weights.YearProximity) +
                (popBonus * _weights.PopularityBonus) +
                (mediaBonus * _weights.MediaTypeBonus) +
                (langBonus * _weights.CountryLangBonus) +
                (aliasBonus * _weights.AliasBonus) -
                (mismatch * _weights.MismatchPenalty);

            scored.Add(new CandidateScore
            {
                Candidate = c,
                Score = score,
                Breakdown = $"exact={exact:F2},set={tokenSet:F2},sort={tokenSort:F2},lev={lev:F2},partial={partial:F2},year={yearScore:F2},pop={popBonus:F2},media={mediaBonus:F2},lang={langBonus:F2},alias={aliasBonus:F2},pen={mismatch:F2}"
            });
        }

        if (scored.Count == 0) return null;

        var best = scored.OrderByDescending(x => x.Score).First();
        return best.Score >= _weights.MinimumAcceptableScore ? best : null;
    }

    private static bool AliasLikely(UncachedProgrammeItem item, TmdbSearchResult c)
    {
        var source = (item.Title + " " + (item.Description ?? string.Empty)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(c.OriginalTitle) && source.Contains(c.OriginalTitle.ToLowerInvariant())) return true;
        if (!string.IsNullOrWhiteSpace(c.OriginalName) && source.Contains(c.OriginalName.ToLowerInvariant())) return true;
        return false;
    }

    private static double PreferredLanguageBonus(TmdbSearchResult c, IReadOnlyList<string> preferred)
    {
        var original = c.OriginalLanguage?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(original)) return 0.3;
        return preferred.Any(p => original.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.0;
    }

    private static double ComputeMismatchPenalty(string query, string candidate, UncachedProgrammeItem item, TmdbSearchResult result)
    {
        var wordsA = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wordsB = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wordsA.Length == 0 || wordsB.Length == 0) return 1.0;

        var overlap = wordsA.Count(w => wordsB.Contains(w));
        var overlapRatio = (double)overlap / Math.Max(wordsA.Length, wordsB.Length);

        var penalty = overlapRatio < 0.25 ? 1.0 : (1.0 - overlapRatio);

        if (!string.IsNullOrWhiteSpace(item.Category))
        {
            var cat = item.Category.ToLowerInvariant();
            if (cat.Contains("sport") && result.MediaType == "movie") penalty += 0.4;
            if (cat.Contains("news") && result.MediaType == "movie") penalty += 0.25;
        }

        return Math.Clamp(penalty, 0, 1.3);
    }

    private static double ComputeYearScore(int? expectedYear, int? candidateYear)
    {
        if (!expectedYear.HasValue || !candidateYear.HasValue) return 0.5;
        var delta = Math.Abs(expectedYear.Value - candidateYear.Value);
        if (delta == 0) return 1.0;
        if (delta == 1) return 0.85;
        if (delta == 2) return 0.65;
        if (delta <= 4) return 0.35;
        return 0;
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return null;
        return int.TryParse(date[..4], out var y) ? y : null;
    }

    private static double TokenSetRatio(string a, string b)
    {
        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (setA.Count == 0 && setB.Count == 0) return 1;
        var intersection = setA.Count(setB.Contains);
        var union = setA.Count + setB.Count - intersection;
        return union == 0 ? 1 : (double)intersection / union;
    }

    private static double TokenSortRatio(string a, string b)
    {
        var sa = SortTokens(a);
        var sb = SortTokens(b);
        return LevenshteinSimilarity(sa, sb);
    }

    private static string SortTokens(string s)
    {
        var ordered = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal);
        return string.Join(' ', ordered);
    }

    private static double PartialRatio(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);

        if (longer.Contains(shorter, StringComparison.Ordinal)) return 1;

        double best = 0;
        var maxOffset = Math.Max(0, longer.Length - shorter.Length);
        for (int i = 0; i <= maxOffset; i++)
        {
            var chunk = longer.Substring(i, shorter.Length);
            var score = LevenshteinSimilarity(shorter, chunk);
            if (score > best) best = score;
            if (best > 0.98) break;
        }

        return best;
    }

    private static double LevenshteinSimilarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;

        var distance = LevenshteinDistance(a, b);
        var max = Math.Max(a.Length, b.Length);
        return 1.0 - ((double)distance / max);
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];

        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }
}
