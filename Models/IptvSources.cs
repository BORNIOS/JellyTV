using System;

namespace JellyTV.Models;

/// <summary>
/// Represents a single M3U/IPTV source managed by the plugin.
/// </summary>
public class IptvSource
{
    /// <summary>Unique identifier for this source.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name shown in the config UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URL to the M3U playlist (may contain a token).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Whether this source is active.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Last time this source was successfully fetched.</summary>
    public DateTimeOffset? LastFetched { get; set; }

    /// <summary>Number of channels parsed on last fetch.</summary>
    public int LastChannelCount { get; set; }

    /// <summary>Last error message if fetch failed, null if OK.</summary>
    public string? LastError { get; set; }
}
