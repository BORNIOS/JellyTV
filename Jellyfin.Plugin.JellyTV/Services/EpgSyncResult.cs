using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Aggregated result of a local EPG sync run.
/// </summary>
public class EpgSyncResult
{
    /// <summary>Gets or sets a value indicating whether the sync completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the number of TVG identifiers found in the configured channels.</summary>
    public int TvgIdsFound { get; set; }

    /// <summary>Gets or sets how many channel logos were filled in from the guides.</summary>
    public int IdentityLogos { get; set; }

    /// <summary>Gets or sets how many channel countries were filled in from the guides.</summary>
    public int IdentityCountries { get; set; }

    /// <summary>Gets or sets the enrichment statistics of the merged guide, or null when TMDB is not configured.</summary>
    public Tmdb.EpgEnrichmentStats? Enrichment { get; set; }

    /// <summary>Gets or sets the per-source sync results.</summary>
    public List<EpgSourceSyncResult> Sources { get; set; } = new();

    /// <summary>Gets or sets the error message when the sync failed.</summary>
    public string? Error { get; set; }
}
