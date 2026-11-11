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
/// Result of syncing a single EPG source into the local guide.
/// </summary>
public class EpgSourceSyncResult
{
    /// <summary>Gets or sets the identifier of the EPG source.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name of the EPG source.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the sync completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the number of channels retained after filtering.</summary>
    public int ChannelsKept { get; set; }

    /// <summary>Gets or sets the number of programmes retained after filtering.</summary>
    public int ProgrammesKept { get; set; }

    /// <summary>Gets or sets the error message when the sync failed.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the number of channels matched by name instead of tvg-id.</summary>
    public int ChannelsByName { get; set; }

    /// <summary>Gets or sets the number of programmes dropped for being too old or too far ahead.</summary>
    public int ProgrammesPruned { get; set; }

    /// <summary>Gets or sets a value indicating whether the last good copy was used because the download failed.</summary>
    public bool UsedCache { get; set; }

    /// <summary>Gets or sets the age in hours of the copy that was used, when <see cref="UsedCache"/> is true.</summary>
    public int CacheAgeHours { get; set; }

    /// <summary>Gets or sets inline TMDB enrichment stats. Null when TMDB is not configured.</summary>
    public EpgEnrichmentStats? EnrichmentStats { get; set; }

    /// <summary>Gets or sets error message if enrichment failed (filtering still succeeds).</summary>
    public string? EnrichmentError { get; set; }
}
