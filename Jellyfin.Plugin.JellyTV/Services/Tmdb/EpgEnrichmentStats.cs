using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public sealed class EpgEnrichmentStats
{
    public int ProgrammesInput { get; set; }

    public int UniqueAfterDedup { get; set; }

    public int Enriched { get; set; }

    public int SkippedByHeuristics { get; set; }

    public int MemoryCacheHits { get; set; }

    public int NegativeCacheSkips { get; set; }

    public int NoMatch { get; set; }

    public int Errors { get; set; }

    public int TmdbRequests { get; set; }

    public int Retries { get; set; }

    public int Saved { get; set; }
}
