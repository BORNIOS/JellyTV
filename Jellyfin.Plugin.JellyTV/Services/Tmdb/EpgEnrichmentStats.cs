using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Counters collected during a TMDB enrichment run.
/// </summary>
public sealed class EpgEnrichmentStats
{
    /// <summary>Gets or sets the number of programmes considered.</summary>
    public int ProgrammesInput { get; set; }

    /// <summary>Gets or sets the number of programmes remaining after de-duplication.</summary>
    public int UniqueAfterDedup { get; set; }

    /// <summary>Gets or sets the number of programmes enriched with TMDB metadata.</summary>
    public int Enriched { get; set; }

    /// <summary>Gets or sets the number of programmes skipped by heuristic rules.</summary>
    public int SkippedByHeuristics { get; set; }

    /// <summary>Gets or sets the number of matches served from the in-memory cache.</summary>
    public int MemoryCacheHits { get; set; }

    /// <summary>Gets or sets the number of programmes skipped by the negative cache.</summary>
    public int NegativeCacheSkips { get; set; }

    /// <summary>Gets or sets the number of programmes with no TMDB match.</summary>
    public int NoMatch { get; set; }

    /// <summary>Gets or sets the number of errors during enrichment.</summary>
    public int Errors { get; set; }

    /// <summary>Gets or sets the number of titles matched by the fallback providers (Jellyfin metadata providers, TVmaze).</summary>
    public int FallbackMatches { get; set; }

    /// <summary>Gets or sets the number of TMDB requests issued.</summary>
    public int TmdbRequests { get; set; }

    /// <summary>Gets or sets the number of TMDB request retries.</summary>
    public int Retries { get; set; }

    /// <summary>Gets or sets the number of programmes that received changes.</summary>
    public int Saved { get; set; }
}
