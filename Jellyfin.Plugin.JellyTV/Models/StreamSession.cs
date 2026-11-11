using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// Active playback session for a channel, including token renewal state.
/// </summary>
public class StreamSession
{
    private int _pollRunning;

    internal ConcurrentDictionary<string, Services.HlsTimeline> PlaylistTimelines { get; } = new(StringComparer.Ordinal);

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
    /// Gets the in-memory cache of the newest segments, shared by every viewer of the channel.
    /// </summary>
    public Services.SegmentCache SegmentCache { get; } = new();

    /// <summary>
    /// Gets or sets when the token generator last rejected a renewal (403). While this is recent the
    /// plugin stops calling the generator: providers that rate-limit it only recover if left alone.
    /// </summary>
    public DateTimeOffset? LastGeneratorRejectionAt { get; set; }

    /// <summary>
    /// Gets or sets how long, in seconds, the token generator makes us wait between two tokens. It starts
    /// at a conservative value and grows by itself each time the generator rejects a request that came
    /// too early, so each source converges on its own provider's limit with no hard-coded provider rules.
    /// </summary>
    public double MintIntervalSeconds { get; set; } = 45;

    /// <summary>
    /// Gets or sets a value indicating whether the current token is known to be dead (the provider
    /// answered 401/403) and no new one could be minted yet.
    /// </summary>
    public bool TokenKnownDead { get; set; }

    /// <summary>
    /// Gets a value indicating whether asking the generator for a token now would only be rejected.
    /// </summary>
    public bool InGeneratorBackoff =>
        (LastTokenRenewedAt is { } minted && DateTimeOffset.UtcNow - minted < TimeSpan.FromSeconds(MintIntervalSeconds))
        || (LastGeneratorRejectionAt is { } rejected && DateTimeOffset.UtcNow - rejected < TimeSpan.FromSeconds(4));

    /// <summary>
    /// Gets the set of upstream hosts observed in the rewritten playlist for this session.
    /// Used to reject arbitrary URLs on the segment proxy.
    /// </summary>
    public ConcurrentDictionary<string, byte> AllowedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// Gets or sets the country whose proxies this session downloads through, or empty when it downloads directly.
    /// </summary>
    public string? ExitCountry { get; set; }

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

    /// <summary>
    /// Gets the limiter for concurrent segment prefetches of this channel.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemaphoreSlim PrefetchGate { get; } = new SemaphoreSlim(4, 4);

    /// <summary>
    /// Gets the per-session limiter for concurrent upstream segment fetches.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SemaphoreSlim SegmentFetchLimiter { get; } = new SemaphoreSlim(6, 6);

    /// <summary>
    /// Records the host of an absolute URL as allowed for segment proxying.
    /// </summary>
    /// <param name="url">Absolute upstream URL.</param>
    public void AllowHostFromUrl(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            AllowedHosts[uri.Host] = 0;
        }
    }

    /// <summary>
    /// Determines whether the URL may be fetched through the segment proxy.
    /// </summary>
    /// <param name="url">Absolute upstream URL.</param>
    /// <returns><c>true</c> when the scheme is HTTP(S) and the host is allowed.</returns>
    public bool IsUrlAllowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (AllowedHosts.ContainsKey(uri.Host)
            || string.Equals(uri.Host, PreferredHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HostMatches(UpstreamUrl, uri.Host)
            || HostMatches(LastResolved?.FinalUrl, uri.Host)
            || HostMatches(LastResolved?.BaseUrl, uri.Host);
    }

    private static bool HostMatches(string? url, string host) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Claims the background refresh of this session.
    /// </summary>
    /// <returns><c>true</c> when no other refresh is running.</returns>
    public bool TryBeginPoll() => Interlocked.CompareExchange(ref _pollRunning, 1, 0) == 0;

    /// <summary>
    /// Releases the background refresh claimed with <see cref="TryBeginPoll"/>.
    /// </summary>
    public void EndPoll() => Interlocked.Exchange(ref _pollRunning, 0);

    /// <summary>
    /// Records that the generator rejected a request and learns from how early it was.
    /// </summary>
    public void RecordGeneratorRejection()
    {
        var now = DateTimeOffset.UtcNow;
        if (LastTokenRenewedAt is { } minted)
        {
            var elapsed = (now - minted).TotalSeconds;
            MintIntervalSeconds = Math.Clamp(Math.Max(MintIntervalSeconds, elapsed + 8), 20, 120);
        }

        LastGeneratorRejectionAt = now;
    }
}
