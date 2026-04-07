using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using JellyTV.Models;

namespace JellyTV.Services;

/// <summary>
/// Manages per-channel broadcast pumps that independently download HLS segments
/// and fan-out raw MPEG-TS bytes to all connected subscribers.
/// This avoids relying on ffmpeg's native HLS client, which fails when upstream
/// providers rotate tokens mid-stream.
/// </summary>
public sealed class BroadcastService : IDisposable
{
    private readonly ConcurrentDictionary<string, BroadcastPump> _pumps =
        new(StringComparer.Ordinal);

    private readonly SessionManagerService _sessions;
    private readonly IHttpClientFactory    _httpFactory;
    private bool _disposed;

    /// <summary>
    /// Initializes the service with its dependencies.
    /// </summary>
    public BroadcastService(SessionManagerService sessions, IHttpClientFactory httpFactory)
    {
        _sessions    = sessions;
        _httpFactory = httpFactory;
    }

    /// <summary>
    /// Returns the existing pump for this session's channel, or creates a new one.
    /// Pumps are keyed by upstream URL so multiple ffprobe/ffmpeg instances share one pump.
    /// </summary>
    public BroadcastPump GetOrCreate(StreamSession session) =>
        _pumps.GetOrAdd(
            session.UpstreamUrl,
            _ => new BroadcastPump(session, _sessions, _httpFactory));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pump in _pumps.Values)
            pump.Dispose();
        _pumps.Clear();
    }
}

/// <summary>
/// Downloads HLS segments for a single channel and fans them out as continuous
/// raw MPEG-TS bytes to all connected subscribers.
///
/// Architecture:
///   - One background task polls the media playlist and downloads new .ts segments.
///   - A ring buffer (last N segments) lets late-joining subscribers receive immediate data.
///   - Each subscriber gets a bounded Channel so back-pressure is per-subscriber.
///   - Token renewal is handled transparently: when 401/403 is received the session is
///     renewed and the media playlist URL is reset so the next iteration re-resolves it
///     from the master playlist with the new token.
/// </summary>
public sealed class BroadcastPump : IDisposable
{
    // ── Configuration ──────────────────────────────────────────────────────────
    // 3 segments ≈ 6 s of buffer at 2 s/segment; enough to seed late joiners
    // without excessive memory use when many channels are active concurrently.
    private const int  RingBufferSegments    = 3;
    private const int  SubscriberChannelCap  = 32;   // max in-flight segments per subscriber
    private const int  MaxSeenHistory        = 500;  // rolling seen-set to prevent memory bloat
    private static readonly TimeSpan IdleStopDelay  = TimeSpan.FromSeconds(15);  // stop pump quickly after last viewer leaves
    private static readonly TimeSpan DefaultPoll    = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorBackoff   = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AuthRetryDelay = TimeSpan.FromMilliseconds(1200);

    // ── Subscriber fan-out state ────────────────────────────────────────────────
    private readonly object                      _sync        = new();
    private readonly Queue<byte[]>               _ring        = new();
    private readonly List<ChannelWriter<byte[]>> _subscribers = new();

    // ── HLS polling state ──────────────────────────────────────────────────────
    private readonly Queue<string>   _seenOrder    = new();
    private readonly HashSet<string> _seenSegments = new(StringComparer.Ordinal);
    // The stable URL used for each playlist poll — for tokenized streams this is the
    // penultimate redirect URL (e.g. alfa.7057460.xyz) so that every poll follows a
    // fresh token-generating redirect instead of re-using an expired ?token= directly.
    private string? _mediaPlaylistUrl;
    // The effective URL after the playlist redirect resolves (e.g. tuenisix?freshToken).
    // Used for BuildBaseUrl so that relative segment paths (/hlsr/TOKEN/...) resolve
    // against the correct media server, not the alfa redirect server.
    private string? _mediaBaseUrl;
    // True once we have consumed the body that the resolver fetched during resolution.
    // Prevents a redundant second GET to a rate-limited provider URL on the very first poll.
    private bool _initialPlaylistConsumed;
    // Rolling window continuity tracking — mirrors the PS1 check: p2.Seq ≤ p1.Seq + p1.Segs.Count.
    // Reset to -1 on every renewal/re-resolve so the first new window is never flagged as a gap.
    private int  _lastMediaSeq  = -1;
    private int  _lastSegCount  =  0;

    // ── Pump lifecycle ─────────────────────────────────────────────────────────
    private CancellationTokenSource _cts         = new();
    private Task?                   _pumpTask;
    private int                     _activeCount;
    private DateTimeOffset          _lastLeft    = DateTimeOffset.MinValue;
    private bool                    _disposed;

    // ── Pre-warm gate ──────────────────────────────────────────────────────────
    // Completed when the pump has published its first segment.
    // Used by GetStream to pre-warm the ring buffer before redirecting ffmpeg.
    private TaskCompletionSource<bool> _firstDataTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ── Dependencies ───────────────────────────────────────────────────────────
    private readonly StreamSession         _session;
    private readonly SessionManagerService _sessions;
    private readonly IHttpClientFactory    _httpFactory;

    internal BroadcastPump(
        StreamSession         session,
        SessionManagerService sessions,
        IHttpClientFactory    httpFactory)
    {
        _session     = session;
        _sessions    = sessions;
        _httpFactory = httpFactory;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the download pump if it is not already running.
    /// Safe to call concurrently; only one pump runs per channel at a time.
    /// </summary>
    public void EnsureRunning()
    {
        if (_pumpTask is { IsCompleted: false }) return;

        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        // Reset the pre-warm gate so callers of WaitForFirstSegmentAsync will
        // block until the new pump cycle has at least one segment ready.
        if (_firstDataTcs.Task.IsCompleted)
            _firstDataTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pumpTask = Task.Run(() => PumpLoopAsync(_cts.Token));
        PluginLog.Info($"[BROADCAST] Pump started session={_session.SessionId}");
    }

    /// <summary>
    /// Waits until the pump has published at least one segment (ring buffer non-empty),
    /// or until <paramref name="timeout"/> elapses. Never throws.
    /// Call this from <c>GetStream</c> to pre-warm the ring buffer before redirecting
    /// ffmpeg, eliminating the blank-screen startup delay.
    /// </summary>
    public Task WaitForFirstSegmentAsync(TimeSpan timeout, CancellationToken ct)
    {
        // If data is already available (pump was already running), return immediately.
        var gate = _firstDataTcs.Task;
        if (gate.IsCompleted) return Task.CompletedTask;

        return WaitWithTimeoutAsync(gate, timeout, ct);
    }

    private static async Task WaitWithTimeoutAsync(Task gate, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout or client disconnect — proceed anyway; the pump is still starting.
        }
    }

    /// <summary>
    /// Subscribes the caller's output stream to receive segment bytes.
    /// Seeds the subscriber with buffered segments for immediate playback.
    /// Blocks (awaits) until <paramref name="ct"/> is cancelled or the pump stops.
    /// </summary>
    public async Task SubscribeAndStreamAsync(Stream output, CancellationToken ct)
    {
        EnsureRunning();

        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SubscriberChannelCap)
        {
            FullMode                      = BoundedChannelFullMode.DropOldest,
            SingleReader                  = true,
            SingleWriter                  = false,
            AllowSynchronousContinuations = false
        });

        // Seed with already-buffered segments so the subscriber gets data immediately.
        lock (_sync)
        {
            foreach (var seg in _ring)
                ch.Writer.TryWrite(seg);

            _subscribers.Add(ch.Writer);
            Interlocked.Increment(ref _activeCount);
        }

        PluginLog.Info($"[BROADCAST] Subscriber joined session={_session.SessionId} active={_activeCount}");

        try
        {
            await foreach (var seg in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await output.WriteAsync(seg.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal client disconnect */ }
        finally
        {
            lock (_sync)
                _subscribers.Remove(ch.Writer);

            Interlocked.Decrement(ref _activeCount);
            _lastLeft = DateTimeOffset.UtcNow;
            PluginLog.Info($"[BROADCAST] Subscriber left session={_session.SessionId} remaining={_activeCount}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pump loop
    // ─────────────────────────────────────────────────────────────────────────

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        PluginLog.Info($"[BROADCAST] Pump running session={_session.SessionId} url={_session.UpstreamUrl}");

        while (!ct.IsCancellationRequested)
        {
            if (IsIdle()) break;

            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                PluginLog.Error("[BROADCAST] Pump error, backing off", ex);
                await Task.Delay(ErrorBackoff, ct).ConfigureAwait(false);
            }
        }

        // Signal all waiting subscribers that there will be no more data.
        lock (_sync)
        {
            foreach (var sub in _subscribers)
                sub.TryComplete();
        }

        PluginLog.Info($"[BROADCAST] Pump stopped session={_session.SessionId}");
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (_sessions.TryGetRenewalBackoffRemaining(_session, out var backoffRemaining))
        {
            var wait = backoffRemaining > ErrorBackoff ? ErrorBackoff : backoffRemaining;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            return;
        }

        if (_sessions.NeedsRenewal(_session))
        {
            await _sessions.RenewSessionAsync(_session, ct).ConfigureAwait(false);
            // Reset so the next iteration uses the fresh body the resolver just fetched.
            _mediaPlaylistUrl        = null;
            _mediaBaseUrl            = null;
            _initialPlaylistConsumed = false;
            _lastMediaSeq            = -1;
            _lastSegCount            =  0;
        }

        var resolved = _session.LastResolved
            ?? throw new InvalidOperationException("Session has no resolved stream.");

        var client = _httpFactory.CreateClient("JellyTV");

        // cachedPlaylistText is non-null when we can skip the standard GetStringAsync below
        // (the body was already fetched during resolution or renewal — avoid double-GET).
        string? cachedPlaylistText = null;
        if (string.IsNullOrWhiteSpace(_mediaPlaylistUrl))
        {
            // On the very first poll (and after each renewal), reuse the playlist body that the
            // RedirectResolverService already fetched.  The provider applies a per-token rate-limit
            // (effectively single-use per window); a redundant GET made milliseconds later returns
            // 403 and causes a 52-second backoff loop.
            string? cachedBody = null;
            if (!_initialPlaylistConsumed)
            {
                _initialPlaylistConsumed = true;
                cachedBody = resolved.InitialPlaylistContent;
                resolved.InitialPlaylistContent = null; // consume once
                if (cachedBody is not null)
                    PluginLog.Info($"[BROADCAST] Using cached playlist body (skipping redundant GET) session={_session.SessionId}");
            }

            try
            {
                (_mediaPlaylistUrl, cachedPlaylistText) = await ResolveMediaPlaylistUrlAsync(client, resolved, cachedBody, ct).ConfigureAwait(false);
                // Seed the base URL from the resolver's final URL so relative segment paths
                // (/hlsr/TOKEN/...) resolve against the correct media server host.
                if (_mediaBaseUrl is null)
                    _mediaBaseUrl = resolved.FinalUrl;
            }
            catch (HttpRequestException ex) when (IsAuthFailure(ex))
            {
                // Do NOT call RenewSessionAsync here: PlaylistPollUrl already points to
                // alfa so the next poll will naturally follow alfa → tuenisix?freshToken.
                // Hammering RenewSessionAsync causes the same stale token to be retried
                // multiple times in quick succession, triggering provider rate-limiting.
                PluginLog.Warn($"[BROADCAST] Resolve media playlist 401/403 session={_session.SessionId}, retrying via PlaylistPollUrl after backoff");
                _mediaPlaylistUrl        = null;
                _mediaBaseUrl            = null;
                _initialPlaylistConsumed = false;
                _lastMediaSeq            = -1;
                _lastSegCount            =  0;
                await Task.Delay(ErrorBackoff, ct).ConfigureAwait(false);
                return;
            }
        }

        string playlistText;
        if (cachedPlaylistText is not null)
        {
            // Use the text returned by ResolveMediaPlaylistUrlAsync directly — no additional GET.
            playlistText = cachedPlaylistText;
        }
        else
        {
            // Use GetAsync so we can follow the fresh-token redirect (alfa → tuenisix?newToken)
            // and capture the effective final URL for correct segment base-URL computation.
            using var playlistResp = await client.GetAsync(_mediaPlaylistUrl, ct).ConfigureAwait(false);
            if (IsAuthFailure(playlistResp.StatusCode))
            {
                // Do NOT call RenewSessionAsync: the next poll will hit PlaylistPollUrl (alfa)
                // which issues a fresh token-redirect automatically.
                // Calling RenewSessionAsync here would hammer the same stale candidates
                // and trigger provider rate-limiting within seconds.
                PluginLog.Warn($"[BROADCAST] Playlist 401/403 session={_session.SessionId}, retrying via PlaylistPollUrl after backoff");
                _mediaPlaylistUrl        = null;
                _mediaBaseUrl            = null;
                _initialPlaylistConsumed = false;
                _lastMediaSeq            = -1;
                _lastSegCount            =  0;
                await Task.Delay(ErrorBackoff, ct).ConfigureAwait(false);
                return;
            }
            if (!playlistResp.IsSuccessStatusCode)
                throw new HttpRequestException($"Playlist fetch failed: {(int)playlistResp.StatusCode}", null, playlistResp.StatusCode);

            playlistText = await playlistResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Update base URL from the effective URL after redirect (e.g. tuenisix?freshToken).
            var responseUrl = playlistResp.RequestMessage?.RequestUri?.ToString();
            if (!string.IsNullOrWhiteSpace(responseUrl))
                _mediaBaseUrl = responseUrl;
        }

        var baseUrl        = BuildBaseUrl(_mediaBaseUrl ?? _mediaPlaylistUrl!);
        var segments       = ParseMediaSegments(playlistText, baseUrl);
        var targetDuration = ParseTargetDuration(playlistText);
        var mediaSeq       = ParseMediaSequence(playlistText);

        // Continuity guard: equivalent to the PS1 check `p2.Seq <= p1.Seq + p1.Segs.Count`.
        // If the new window starts beyond the previous window's range, segments were missed.
        if (_lastMediaSeq >= 0 && mediaSeq > _lastMediaSeq + _lastSegCount)
        {
            PluginLog.Warn($"[BROADCAST] Continuity break: expected≤{_lastMediaSeq + _lastSegCount} got={mediaSeq} missed≈{mediaSeq - (_lastMediaSeq + _lastSegCount)} segs session={_session.SessionId}");
            _mediaPlaylistUrl        = null;
            _mediaBaseUrl            = null;
            _initialPlaylistConsumed = false;
            _lastMediaSeq            = -1;
            _lastSegCount            =  0;
            return; // re-resolve on next poll to catch up to the live edge
        }

        _lastMediaSeq = mediaSeq;
        _lastSegCount = segments.Count;

        UpdateEstimatedTokenExpiry(_session, segments.Count, targetDuration);

        var downloadedAny  = await DownloadNewSegmentsAsync(client, segments, ct).ConfigureAwait(false);

        // Keep renewal demand-driven (NeedsRenewal + reactive 401/403). Renewing on every
        // successful cycle can rotate playlist tokens too aggressively and cause timeline jumps
        // when providers return overlapping media windows.

        // Always wait one targetDuration before the next poll — this matches the HLS spec
        // reload interval and prevents provider rate-limiting from rapid re-requests.
        // (The previous 500ms fast-poll after downloading segments caused 20+ requests/window.)
        await Task.Delay(targetDuration, ct).ConfigureAwait(false);
    }

    private async Task<bool> DownloadNewSegmentsAsync(
        HttpClient        client,
        List<string>      segments,
        CancellationToken ct)
    {
        var downloadedAny = false;

        foreach (var segUrl in segments)
        {
            if (ct.IsCancellationRequested) break;
            if (!MarkSeen(segUrl)) continue;

            var data = await DownloadSegmentAsync(client, segUrl, ct).ConfigureAwait(false);
            if (data is null || data.Length == 0) continue;

            downloadedAny = true;
            Publish(data);
        }

        return downloadedAny;
    }

    private bool IsIdle() =>
        _activeCount == 0
        && _lastLeft != DateTimeOffset.MinValue
        && DateTimeOffset.UtcNow - _lastLeft > IdleStopDelay;

    // ─────────────────────────────────────────────────────────────────────────
    // HLS parsing helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// If <paramref name="resolved"/> points to a master playlist, returns the URL of the
    /// highest-bandwidth variant; otherwise returns the resolved URL unchanged.
    /// </summary>
    private static async Task<(string Url, string? Text)> ResolveMediaPlaylistUrlAsync(
        HttpClient        client,
        ResolvedStream    resolved,
        string?           cachedBody,
        CancellationToken ct)
    {
        // Use PlaylistPollUrl (e.g. alfa.7057460.xyz) as the stable cache key.
        // This URL does a fresh token-generating redirect on each GET, so _mediaPlaylistUrl
        // never contains an expired ?token= query string.
        var text = cachedBody ?? await client.GetStringAsync(resolved.PlaylistPollUrl, ct).ConfigureAwait(false);

        if (!text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            // Pure media playlist — return PlaylistPollUrl so subsequent polls also go
            // through the fresh-token redirect, and the text for the first poll.
            return (resolved.PlaylistPollUrl, text);
        }

        // Master playlist — parse best variant URL.  Base URL uses FinalUrl (the actual
        // media server) so relative variant paths resolve to the correct host.
        var baseUrl = BuildBaseUrl(resolved.FinalUrl);
        var best    = ParseBestVariantUrl(text, baseUrl);
        PluginLog.Info($"[BROADCAST] Resolved variant playlist: {best ?? "(fallback to PlaylistPollUrl)"}");
        return (best ?? resolved.PlaylistPollUrl, null);
    }

    private static string? ParseBestVariantUrl(string masterText, string baseUrl)
    {
        string? bestUrl = null;
        long    bestBw  = -1;

        using var reader = new StringReader(masterText);
        string?   line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;

            var bw      = ParseBandwidth(line);
            var uriLine = reader.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(uriLine)) continue;

            if (bw > bestBw)
            {
                bestBw  = bw;
                bestUrl = uriLine.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? uriLine
                    : new Uri(new Uri(baseUrl), uriLine).ToString();
            }
        }

        return bestUrl;
    }

    private static long ParseBandwidth(string streamInfLine)
    {
        const string marker = "BANDWIDTH=";
        var idx = streamInfLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;

        var start = idx + marker.Length;
        var end   = start;
        while (end < streamInfLine.Length && char.IsDigit(streamInfLine[end]))
            end++;

        return long.TryParse(streamInfLine[start..end], out var bw) ? bw : 0L;
    }

    private static List<string> ParseMediaSegments(string playlistText, string baseUrl)
    {
        var urls = new List<string>();
        using var reader = new StringReader(playlistText);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
            if (line.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) continue;

            var abs = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(new Uri(baseUrl), line).ToString();

            urls.Add(abs);
        }

        return urls;
    }

    private static int ParseMediaSequence(string playlistText)
    {
        const string tag = "#EXT-X-MEDIA-SEQUENCE:";
        var idx = playlistText.IndexOf(tag, StringComparison.Ordinal);
        if (idx < 0) return 0;

        var start = idx + tag.Length;
        var end   = start;
        while (end < playlistText.Length && char.IsDigit(playlistText[end]))
            end++;

        return int.TryParse(playlistText[start..end], out var seq) ? seq : 0;
    }

    private static TimeSpan ParseTargetDuration(string playlistText)
    {
        const string tag = "#EXT-X-TARGETDURATION:";
        var idx = playlistText.IndexOf(tag, StringComparison.Ordinal);
        if (idx < 0) return DefaultPoll;

        var start = idx + tag.Length;
        var end   = start;
        while (end < playlistText.Length && char.IsDigit(playlistText[end]))
            end++;

        return int.TryParse(playlistText[start..end], out var secs)
            ? TimeSpan.FromSeconds(secs)
            : DefaultPoll;
    }

    /// <summary>
    /// For providers that rotate opaque tokens by short playlist windows (e.g. ~60s),
    /// infer a moving expiry from the current media window size.
    /// </summary>
    private static void UpdateEstimatedTokenExpiry(StreamSession session, int segmentCount, TimeSpan targetDuration)
    {
        var resolved = session.LastResolved;
        if (resolved is null)
        {
            return;
        }

        // Only estimate for tokenized URLs; non-token channels must stay renewal-free.
        if (string.IsNullOrWhiteSpace(resolved.TokenValue))
        {
            return;
        }

        if (segmentCount <= 0 || targetDuration <= TimeSpan.Zero)
        {
            return;
        }

        var windowSeconds = segmentCount * targetDuration.TotalSeconds;
        // Ignore pathological playlists to avoid bad estimates.
        if (windowSeconds < 15 || windowSeconds > 300)
        {
            return;
        }

        resolved.TokenExpiry = DateTimeOffset.UtcNow.AddSeconds(windowSeconds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Segment download
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<byte[]?> DownloadSegmentAsync(
        HttpClient        client,
        string            segUrl,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await client.GetByteArrayAsync(segUrl, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) when (IsAuthFailure(ex) && attempt == 1)
            {
                // Reset playlist so the next poll re-resolves via PlaylistPollUrl (alfa),
                // getting segment URLs with a fresh token. No RenewSessionAsync needed.
                PluginLog.Warn($"[BROADCAST] Segment 401/403 attempt={attempt}, resetting playlist for fresh token");
                _mediaPlaylistUrl = null;
                _mediaBaseUrl     = null;
                _lastMediaSeq     = -1;
                _lastSegCount     =  0;
                // Don't retry the old URL with the old token — let the next poll fetch fresh URLs.
                return null;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"[BROADCAST] Segment download attempt={attempt} error: {ex.Message}");
                if (attempt >= 2) return null;
            }
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // State management
    // ─────────────────────────────────────────────────────────────────────────

    private void Publish(byte[] data)
    {
        lock (_sync)
        {
            _ring.Enqueue(data);
            while (_ring.Count > RingBufferSegments)
                _ring.Dequeue();

            // Signal all WaitForFirstSegmentAsync waiters on first publish.
            // TrySetResult is a no-op on subsequent calls.
            _firstDataTcs.TrySetResult(true);

            foreach (var sub in _subscribers)
                sub.TryWrite(data);
        }
    }

    /// <summary>
    /// Marks <paramref name="segUrl"/> as seen with a rolling window of <see cref="MaxSeenHistory"/>.
    /// Returns <c>true</c> if this is a new segment that should be downloaded.
    /// </summary>
    private bool MarkSeen(string segUrl)
    {
        var dedupKey = BuildSegmentDedupKey(segUrl);
        if (_seenSegments.Contains(dedupKey)) return false;

        _seenSegments.Add(dedupKey);
        _seenOrder.Enqueue(dedupKey);

        if (_seenOrder.Count > MaxSeenHistory)
        {
            var evicted = _seenOrder.Dequeue();
            _seenSegments.Remove(evicted);
        }

        return true;
    }

    /// <summary>
    /// Builds a stable deduplication key for a segment URL.
    /// The key intentionally ignores token wrappers (e.g. /hlsr/{token}/...) so
    /// rotating tokens do not make already-seen timeline fragments look new.
    /// </summary>
    private static string BuildSegmentDedupKey(string segUrl)
    {
        try
        {
            var uri = new Uri(segUrl, UriKind.Absolute);
            var path = uri.AbsolutePath;

            const string marker = "/hlsr/";
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var afterMarker = path[(idx + marker.Length)..];
                var slash = afterMarker.IndexOf('/');
                if (slash >= 0 && slash + 1 < afterMarker.Length)
                {
                    // Remove token part and keep channel/segment identity.
                    path = afterMarker[(slash + 1)..];
                }
            }

            var fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
        }
        catch
        {
            return segUrl;
        }
    }

    /// <summary>
    /// Returns the base URL (everything up to and including the last '/') for resolving
    /// relative URIs, ignoring query-string components.
    /// </summary>
    private static string BuildBaseUrl(string url)
    {
        var q    = url.IndexOf('?');
        var path = q >= 0 ? url[..q] : url;
        var last = path.LastIndexOf('/');
        return last > 8 ? path[..(last + 1)] : path;
    }

    private static bool IsAuthFailure(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    private static bool IsAuthFailure(System.Net.HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
