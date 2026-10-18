using System;

namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// Health check result for a single channel.
/// Represents the outcome of attempting to download real stream data (HLS playlist or video segment).
/// </summary>
public class ChannelHealthStatus
{
    /// <summary>Gets or sets channel name from the M3U source.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets original channel URL from the M3U source.</summary>
    public string ChannelUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets hTTP status code from the actual stream download attempt. 0 = no HTTP response (timeout/network error).</summary>
    public int HttpStatus { get; set; }

    /// <summary>Gets or sets number of bytes successfully downloaded before completion or timeout.</summary>
    public long BytesDownloaded { get; set; }

    /// <summary>Gets a value indicating whether whether this channel is considered "alive" (successful real download).</summary>
    public bool IsAlive => HttpStatus >= 200 && HttpStatus < 400 && BytesDownloaded >= 200_000; // At least 200KB

    /// <summary>Gets human-readable status: "LIVE" or "DEAD".</summary>
    public string Status => IsAlive ? "LIVE" : "DEAD";

    /// <summary>Gets or sets when the health check was performed.</summary>
    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>Gets or sets error message if the check failed (timeout, network error, auth failure, etc.).</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets duration of the health check in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>Gets or sets type of stream detected: "hls" or "mpeg2ts" or "unknown".</summary>
    public string StreamType { get; set; } = "unknown";
}
