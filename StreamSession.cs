using System;

namespace Jellyfin.Plugin.IptvProxy.Models;

/// <summary>
/// Represents a resolved IPTV stream with its final URL and token metadata.
/// </summary>
public class ResolvedStream
{
    /// <summary>
    /// Gets or sets the original URL provided by the user/Jellyfin.
    /// </summary>
    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the final resolved URL after following all redirects.
    /// </summary>
    public string FinalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL used for resolving relative HLS segment paths.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content type of the resolved stream.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this resolution was performed.
    /// </summary>
    public DateTimeOffset ResolvedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the estimated token expiry time, if detected.
    /// </summary>
    public DateTimeOffset? TokenExpiry { get; set; }

    /// <summary>
    /// Gets or sets whether this URL points to an HLS playlist.
    /// </summary>
    public bool IsHls { get; set; }
}

/// <summary>
/// Represents an active proxy session for a stream.
/// </summary>
public class StreamSession
{
    /// <summary>
    /// Gets or sets the unique session ID.
    /// </summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the original upstream URL.
    /// </summary>
    public string UpstreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the last resolved stream information.
    /// </summary>
    public ResolvedStream? LastResolved { get; set; }

    /// <summary>
    /// Gets or sets when this session was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets when this session was last accessed.
    /// </summary>
    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets whether a token renewal is currently in progress.
    /// </summary>
    public bool IsRenewing { get; set; }
}
