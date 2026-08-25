using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace JellyTV.Models;

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
    
    /// <summary>
    /// If present, the token value extracted from the final URL's query string.
    /// </summary>
    public string? TokenValue { get; set; }

    /// <summary>
    /// Gets or sets the chain of redirects followed to reach the final URL, in order.
    /// </summary>
    public List<string> RedirectChain { get; set; } = new();

    /// <summary>
    /// The URL the BroadcastPump should use for each playlist poll.
    /// For tokenized streams this is the penultimate URL in the redirect chain
    /// (e.g. alfa.7057460.xyz) so that each poll follows a fresh token-generating
    /// redirect rather than re-using a single-use query-string token.
    /// For non-tokenized streams this equals <see cref="FinalUrl"/>.
    /// </summary>
    public string PlaylistPollUrl { get; set; } = string.Empty;

    /// <summary>
    /// Body text fetched during the resolver's final GET.
    /// Available exactly once: the BroadcastPump consumes it to avoid a second
    /// rate-limited request to a single-use provider token.
    /// </summary>
    public string? InitialPlaylistContent { get; set; }

    /// <summary>
    /// The redirect node responsible for rotating the token (e.g. <c>alfa.7057460.xyz</c>).
    /// Detected as the last 3xx hop whose Location URL contains a token signal (<c>?token=</c>,
    /// <c>/hlsr/</c>, or a query string longer than 80 characters).
    /// Diagnostics only — renewal must always start from <see cref="OriginalUrl"/>.
    /// </summary>
    public string? RotationNodeUrl { get; set; }

    /// <summary>
    /// Proxy key used for the successful resolve chain (Host:Port), when resolution used proxy.
    /// </summary>
    public string? ResolverProxyKey { get; set; }
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
    /// Preferred upstream host to use for segment fetches when providers rotate redirects.
    /// Filled from the last successful segment resolution.
    /// </summary>
    public string? PreferredHost { get; set; }

    /// <summary>
    /// Cache of resolved segment URLs to their final URLs with a short TTL.
    /// Key: original segment URL. Value: tuple (finalUrl, expiry).
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, (string FinalUrl, DateTimeOffset Expiry)> SegmentResolveCache { get; } = new();
    /// <summary>
    /// Gets or sets the original upstream URL.
    /// </summary>
    public string UpstreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the last resolved stream information.
    /// </summary>
    public ResolvedStream? LastResolved { get; set; }

    /// <summary>
    /// Last rewritten HLS playlist served to Jellyfin for this session.
    /// A short-lived cache avoids repeated upstream playlist fetches during probe/transcode startup.
    /// </summary>
    public string? LastPlaylistContent { get; set; }

    /// <summary>
    /// Final URL used to fetch <see cref="LastPlaylistContent"/>.
    /// </summary>
    public string? LastPlaylistSourceUrl { get; set; }

    /// <summary>
    /// Timestamp for <see cref="LastPlaylistContent"/>.
    /// </summary>
    public DateTimeOffset? LastPlaylistFetchedAt { get; set; }

    /// <summary>
    /// Estimated HLS media-window duration (sum of EXTINF) for the last fetched media playlist.
    /// </summary>
    public double? LastPlaylistWindowSeconds { get; set; }

    /// <summary>
    /// EXT-X-TARGETDURATION (seconds) observed in the last fetched media playlist.
    /// </summary>
    public double? LastPlaylistTargetDurationSeconds { get; set; }

    /// <summary>
    /// History of recent resolved streams (keeps last resolutions). Entries older than the configured retention
    /// period should be pruned by the manager; stored here for quick access and diagnostics.
    /// </summary>
    public ConcurrentQueue<ResolvedStream> RedirectHistory { get; } = new();

    /// <summary>
    /// Gets or sets when this session was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets when this session was last accessed.
    /// </summary>
    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets when a segment was successfully served for this session.
    /// </summary>
    public DateTimeOffset? LastSegmentServedAt { get; set; }

    /// <summary>
    /// Gets or sets when a forced token renewal was last triggered from /stream.
    /// </summary>
    public DateTimeOffset? LastForcedRenewalAt { get; set; }

    /// <summary>
    /// Gets or sets whether a token renewal is currently in progress.
    /// </summary>
    public bool IsRenewing { get; set; }

    /// <summary>
    /// Directory to store downloaded segments and concat artifacts for this session.
    /// </summary>
    public string? ConcatDirectory { get; set; }

    /// <summary>
    /// Path to the last combined output file produced by ffmpeg.
    /// </summary>
    public string? CombinedFilePath { get; set; }

    /// <summary>
    /// Cancellation token source for prefetch/concat background task.
    /// </summary>
    public System.Threading.CancellationTokenSource? PrefetchCts { get; set; }

    /// <summary>
    /// Background prefetch task reference.
    /// </summary>
    public System.Threading.Tasks.Task? PrefetchTask { get; set; }

    /// <summary>
    /// Set of downloaded segment filenames to avoid re-downloading.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> DownloadedSegments { get; } = new();

    /// <summary>
    /// Cancellation token source for keep-alive background task.
    /// </summary>
    public System.Threading.CancellationTokenSource? KeepAliveCts { get; set; }

    /// <summary>
    /// Background keep-alive task reference.
    /// </summary>
    public System.Threading.Tasks.Task? KeepAliveTask { get; set; }
}
