using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Models;

public class StreamSession
{
    /// <summary>
    /// Gets or sets the unique session ID.
    /// </summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets preferred upstream host to use for segment fetches when providers rotate redirects.
    /// Filled from the last successful segment resolution.
    /// </summary>
    public string? PreferredHost { get; set; }

    /// <summary>
    /// Gets cache of resolved segment URLs to their final URLs with a short TTL.
    /// Key: original segment URL. Value: tuple (finalUrl, expiry).
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, (string FinalUrl, DateTimeOffset Expiry)> SegmentResolveCache { get; } = new();

    /// <summary>
    /// Gets or sets the original upstream URL.
    /// </summary>
    public string UpstreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel type: 0=Direct, 1=FAST, 2=Tokenizado.
    /// Determines whether the session resolves the upstream through JellyTV.
    /// </summary>
    public int ChannelType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the channel token is issued for the whole
    /// session (no proactive renewal — only reactive on 401/403 or near expiry).
    /// </summary>
    public bool SessionToken { get; set; }

    /// <summary>
    /// Gets or sets uRL of the redirect hop that generates the short-lived token.
    /// </summary>
    public string? TokenRenewalUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether indicates that direct renewal and full re-resolution both failed.
    /// </summary>
    public bool IsInvalid { get; set; }

    /// <summary>
    /// Gets or sets the last resolved stream information.
    /// </summary>
    public ResolvedStream? LastResolved { get; set; }

    /// <summary>
    /// Gets or sets last rewritten HLS playlist served to Jellyfin for this session.
    /// A short-lived cache avoids repeated upstream playlist fetches during probe/transcode startup.
    /// </summary>
    public string? LastPlaylistContent { get; set; }

    /// <summary>
    /// Gets or sets final URL used to fetch <see cref="LastPlaylistContent"/>.
    /// </summary>
    public string? LastPlaylistSourceUrl { get; set; }

    /// <summary>
    /// Gets or sets timestamp for <see cref="LastPlaylistContent"/>.
    /// </summary>
    public DateTimeOffset? LastPlaylistFetchedAt { get; set; }

    /// <summary>
    /// Gets or sets estimated HLS media-window duration (sum of EXTINF) for the last fetched media playlist.
    /// </summary>
    public double? LastPlaylistWindowSeconds { get; set; }

    /// <summary>
    /// Gets or sets eXT-X-TARGETDURATION (seconds) observed in the last fetched media playlist.
    /// </summary>
    public double? LastPlaylistTargetDurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets absolute upstream segment URLs for the last fetched playlist.
    /// </summary>
    public IReadOnlyList<string>? LastPlaylistSegmentUrls { get; set; }

    /// <summary>
    /// Gets or sets timestamp of the last attempted background playlist refresh.
    /// </summary>
    public DateTimeOffset? LastPlaylistRefreshAttemptAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether indicates whether a background playlist refresh is currently running.
    /// </summary>
    public bool IsPlaylistRefreshInProgress { get; set; }

    /// <summary>
    /// Gets history of recent resolved streams (keeps last resolutions). Entries older than the configured retention
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
    /// Gets or sets last token renewal completed for this session.
    /// </summary>
    public DateTimeOffset? LastTokenRenewedAt { get; set; }

    /// <summary>
    /// Gets or sets last token renewal attempt (successful or not) for this session.
    /// </summary>
    public DateTimeOffset? LastTokenRenewalAttemptAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether gets or sets whether a token renewal is currently in progress.
    /// </summary>
    public bool IsRenewing { get; set; }

    /// <summary>
    /// Gets serializes token renewal and publication of a complete resolution.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemaphoreSlim RenewalLock { get; } = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Gets lock to prevent concurrent playlist fetches (ffmpeg + browser race condition).
    /// Ensures only one upstream request to the token-generating rotation node at a time.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemaphoreSlim PlaylistFetchLock { get; } = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Gets or sets directory to store downloaded segments and concat artifacts for this session.
    /// </summary>
    public string? ConcatDirectory { get; set; }

    /// <summary>
    /// Gets or sets path to the last combined output file produced by ffmpeg.
    /// </summary>
    public string? CombinedFilePath { get; set; }

    /// <summary>
    /// Gets or sets cancellation token source for prefetch/concat background task.
    /// </summary>
    public System.Threading.CancellationTokenSource? PrefetchCts { get; set; }

    /// <summary>
    /// Gets or sets background prefetch task reference.
    /// </summary>
    public System.Threading.Tasks.Task? PrefetchTask { get; set; }

    /// <summary>
    /// Gets set of downloaded segment filenames to avoid re-downloading.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> DownloadedSegments { get; } = new();

    /// <summary>
    /// Gets or sets cancellation token source for keep-alive background task.
    /// </summary>
    public System.Threading.CancellationTokenSource? KeepAliveCts { get; set; }

    /// <summary>
    /// Gets or sets background keep-alive task reference.
    /// </summary>
    public System.Threading.Tasks.Task? KeepAliveTask { get; set; }
}
