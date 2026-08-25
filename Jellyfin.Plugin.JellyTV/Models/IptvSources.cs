using System;

namespace Jellyfin.Plugin.JellyTV.Models;

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

/// <summary>
/// Represents a single XMLTV/EPG source that the Plugin will download,
/// filter to only matching channels, and push to the Worker.
/// </summary>
public class EpgSource
{
    /// <summary>Unique identifier for this EPG source.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Display name (e.g. "México EPG").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URL of the XMLTV file (may be large — the Plugin handles download + filter).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Whether this EPG source is active.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Last time this source was successfully synced to the Worker.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>Last sync result summary (e.g. "42 channels, 1800 programmes").</summary>
    public string? LastSyncSummary { get; set; }

    /// <summary>Last error during sync, null if OK.</summary>
    public string? LastSyncError { get; set; }
}
