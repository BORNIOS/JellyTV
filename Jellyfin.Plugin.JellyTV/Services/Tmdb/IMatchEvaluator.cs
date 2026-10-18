using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

internal interface IMatchEvaluator
{
    CandidateScore? SelectBest(
        UncachedProgrammeItem item,
        List<TmdbSearchResult> candidates,
        IReadOnlyList<string> preferredLanguages,
        string? expectedMediaType = null);
}
