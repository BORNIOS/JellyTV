using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// Represents a single TV channel managed by the plugin.
/// </summary>
public class ChannelEntry
{
    /// <summary>Unique identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Public display name shown in Jellyfin.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Primary stream URL (HLS .m3u8 or MPEG-TS).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Fallback URL #1 — used when the primary URL is unreachable.</summary>
    public string? FallbackUrl1 { get; set; }

    /// <summary>Fallback URL #2.</summary>
    public string? FallbackUrl2 { get; set; }

    /// <summary>
    /// Channel type: 0=Direct, 1=FAST, 2=TokenEphemeral, 3=TokenPermanent.
    /// Stored as int to avoid XML enum serialization issues with Jellyfin's config serializer.
    /// </summary>
    public int Type { get; set; } = 0;

    /// <summary>Whether the channel is tokenized (Type 2 or 3).</summary>
    public bool IsTokenized => Type is 2 or 3;

    /// <summary>Whether this channel is active and included in the M3U output.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Optional custom HTTP headers (one per line, format: Name: Value).</summary>
    public string? CustomHeaders { get; set; }

    /// <summary>Optional per-channel User-Agent override.</summary>
    public string? UserAgent { get; set; }

    /// <summary>URL to the channel logo/icon.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>tvg-id for EPG matching.</summary>
    public string? TvgId { get; set; }

    /// <summary>tvg-name override for EPG matching.</summary>
    public string? TvgName { get; set; }

    /// <summary>Category / group-title for the M3U output.</summary>
    public string? Category { get; set; }

    /// <summary>Last time this channel was successfully resolved/checked.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Whether the channel was alive during the last health check.</summary>
    public bool? LastCheckAlive { get; set; }

    /// <summary>Last error message if the health check failed.</summary>
    public string? LastCheckError { get; set; }
}
