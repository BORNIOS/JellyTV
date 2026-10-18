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

public class EpgSourceSyncResult
{
    public string SourceId { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public bool Success { get; set; }

    public int ChannelsKept { get; set; }

    public int ProgrammesKept { get; set; }

    public string? Error { get; set; }

    /// <summary>Gets or sets inline TMDB enrichment stats. Null when TMDB is not configured.</summary>
    public EpgEnrichmentStats? EnrichmentStats { get; set; }

    /// <summary>Gets or sets error message if enrichment failed (filtering still succeeds).</summary>
    public string? EnrichmentError { get; set; }
}
