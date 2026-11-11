using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

internal sealed class CandidateScore
{
    public TmdbSearchResult Candidate { get; init; } = new();

    public double Score { get; init; }

    public string Breakdown { get; init; } = string.Empty;
}
