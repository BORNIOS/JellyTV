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

    public double CountryMatchBonus { get; init; } = 0.15;

    public double MismatchPenalty { get; init; } = 0.45;

    public double MinimumAcceptableScore { get; init; } = 1.55;
}
