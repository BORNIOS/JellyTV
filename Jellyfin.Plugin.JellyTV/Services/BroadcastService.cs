using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Manages per-channel broadcast pumps that independently download HLS segments
/// and fan-out raw MPEG-TS bytes to all connected subscribers.
/// </summary>
public sealed class BroadcastService : IDisposable
{
    private readonly ConcurrentDictionary<string, BroadcastPump> _pumps =
        new(StringComparer.Ordinal);

    private readonly IHttpClientFactory      _httpFactory;
    private readonly ProxyPoolService        _proxyPool;
    private readonly RedirectResolverService _resolver;
    private bool _disposed;

    public BroadcastService(IHttpClientFactory httpFactory, ProxyPoolService proxyPool, RedirectResolverService resolver)
    {
        _httpFactory = httpFactory;
        _proxyPool   = proxyPool;
        _resolver    = resolver;
    }

    /// <summary>
    /// Returns the existing pump for this session's channel, or creates a new one.
    /// Pumps are keyed by upstream URL so multiple ffprobe/ffmpeg instances share one pump.
    /// </summary>
    public BroadcastPump GetOrCreate(StreamSession session) =>
        _pumps.GetOrAdd(
            session.UpstreamUrl,
            _ => new BroadcastPump(session, _httpFactory, _proxyPool, _resolver));

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
/// Each poll resolves the full redirect chain from the root upstream URL so there
/// is no stored token, no token tracking, and no CDN-window logic. The proxy pool
/// rotates IPs across polls automatically inside RedirectResolverService.
/// </summary>
public sealed class BroadcastPump : IDisposable
{
    // ── Configuration ──────────────────────────────────────────────────────────
    private const int  RingBufferSegments   = 5;
    private const int  SubscriberChannelCap = 32;
    private const int  MaxSeenHistory       = 500;
    private const int  MaxActiveSubscribers = 4;
    private const int  LiveEdgeSegments     = 10;  // max segments to download on fresh start (DVR guard)
    private static readonly TimeSpan IdleStopDelay         = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ErrorBackoff          = TimeSpan.FromSeconds(4);
    // Worst-case silence: full window (60 s) + poll delay (50 s) + resolve (~3 s) = ~113 s.
    private static readonly TimeSpan SubscriberDataTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(1.5);

    // ── Subscriber fan-out ─────────────────────────────────────────────────────
    private readonly object                      _sync        = new();
    private readonly Queue<byte[]>               _ring        = new();
    private readonly List<ChannelWriter<byte[]>> _subscribers = new();

    // ── HLS polling state ──────────────────────────────────────────────────────
    private readonly Queue<string>   _seenOrder    = new();
    private readonly HashSet<string> _seenSegments = new(StringComparer.Ordinal);
    private int  _lastMediaSeq              = -1;
    private int  _lastSegCount              =  0;
    private int  _consecutiveAllSeenPolls   =  0;
    private int  _lastPublishedSeq          = -1;

    // ── Pump lifecycle ─────────────────────────────────────────────────────────
    private CancellationTokenSource _cts         = new();
    private Task?                   _pumpTask;
    private int                     _activeCount;
    private DateTimeOffset          _lastLeft    = DateTimeOffset.MinValue;
    private bool                    _disposed;
    private long                    _lastPublishedAtTicks;

    public bool IsAtCapacity => Volatile.Read(ref _activeCount) >= MaxActiveSubscribers;

    private TaskCompletionSource<bool> _firstDataTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ── Dependencies ───────────────────────────────────────────────────────────
    private readonly StreamSession           _session;
    private readonly IHttpClientFactory      _httpFactory;
    private readonly ProxyPoolService        _proxyPool;
    private readonly RedirectResolverService _resolver;

    internal BroadcastPump(
        StreamSession           session,
        IHttpClientFactory      httpFactory,
        ProxyPoolService        proxyPool,
        RedirectResolverService resolver)
    {
        _session     = session;
        _httpFactory = httpFactory;
        _proxyPool   = proxyPool;
        _resolver    = resolver;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    public void EnsureRunning()
    {
        if (_pumpTask is { IsCompleted: false }) return;

        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        // Full state reset.
        // _seenSegments MUST be cleared: a restarted pump that inherits the previous
        // seen-set delivers zero bytes — silent failure on channels like Amagi.
        Volatile.Write(ref _lastPublishedAtTicks, 0);
        _lastPublishedSeq            = -1;
        _seenSegments.Clear();
        _seenOrder.Clear();
        _lastMediaSeq                = -1;
        _lastSegCount                =  0;
        _consecutiveAllSeenPolls   =  0;

        if (_firstDataTcs.Task.IsCompleted)
            _firstDataTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pumpTask = Task.Run(() => PumpLoopAsync(_cts.Token));
        PluginLog.Info($"[BROADCAST] Pump started session={_session.SessionId}");
    }

    /// <summary>
    /// Waits until the pump has published at least one segment, or until timeout.
    /// </summary>
    public Task WaitForFirstSegmentAsync(TimeSpan timeout, CancellationToken ct)
    {
        var gate = _firstDataTcs.Task;
        if (gate.IsCompleted) return Task.CompletedTask;
        return WaitWithTimeoutAsync(gate, timeout, ct);
    }

    private static async Task WaitWithTimeoutAsync(Task gate, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try { await gate.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Subscribes the caller to the segment stream. Blocks until disconnect or zombie eviction.
    /// </summary>
    public async Task SubscribeAndStreamAsync(Stream output, CancellationToken ct)
    {
        if (IsAtCapacity)
        {
            PluginLog.Warn($"[BROADCAST] Subscriber cap ({MaxActiveSubscribers}) reached, rejecting session={_session.SessionId}");
            return;
        }

        EnsureRunning();

        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SubscriberChannelCap)
        {
            FullMode                      = BoundedChannelFullMode.DropOldest,
            SingleReader                  = true,
            SingleWriter                  = false,
            AllowSynchronousContinuations = false
        });

        lock (_sync)
        {
            foreach (var seg in _ring)
                ch.Writer.TryWrite(seg);
            _subscribers.Add(ch.Writer);
            Interlocked.Increment(ref _activeCount);
        }

        PluginLog.Info($"[BROADCAST] Subscriber joined session={_session.SessionId} active={_activeCount}");

        using var zombieCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, zombieCts.Token);
        var linkedToken = linkedCts.Token;

        _ = Task.Run(async () =>
        {
            var lastSeenTicks = Interlocked.Read(ref _lastPublishedAtTicks);
            while (!linkedToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), linkedToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                var currentTicks = Interlocked.Read(ref _lastPublishedAtTicks);
                if (currentTicks != lastSeenTicks) { lastSeenTicks = currentTicks; continue; }
                if (currentTicks == 0) continue;

                var elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(currentTicks, TimeSpan.Zero);
                if (elapsed > SubscriberDataTimeout)
                {
                    PluginLog.Warn($"[BROADCAST] Zombie eviction: no data for {elapsed.TotalSeconds:F0}s session={_session.SessionId}");
                    zombieCts.Cancel();
                    return;
                }
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var seg in ch.Reader.ReadAllAsync(linkedToken).ConfigureAwait(false))
                await output.WriteAsync(seg.AsMemory(), linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_sync) _subscribers.Remove(ch.Writer);
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

        lock (_sync)
        {
            foreach (var sub in _subscribers)
                sub.TryComplete();
        }

        PluginLog.Info($"[BROADCAST] Pump stopped session={_session.SessionId}");
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // Always resolve from root — no stored URLs, no token management.
        // The resolver uses the proxy pool internally, so each poll rotates IPs.
        ResolvedStream fresh;
        try
        {
            fresh = await _resolver.ResolveAsync(_session.UpstreamUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[BROADCAST] Root resolve failed — backing off session={_session.SessionId}", ex);
            await Task.Delay(ErrorBackoff, ct).ConfigureAwait(false);
            return;
        }

        // Prefer the playlist body already read by the resolver (avoids a redundant GET).
        // If it's a master playlist we need to fetch the best variant.
        string playlistText;
        string baseUrl;

        var cachedBody = fresh.InitialPlaylistContent;
        if (cachedBody is not null && !cachedBody.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            // Media playlist — use directly.
            playlistText = cachedBody;
            baseUrl      = fresh.BaseUrl;
        }
        else
        {
            var client = _httpFactory.CreateClient("JellyTV");

            if (cachedBody is not null)
            {
                // Master playlist — pick the best variant and fetch it.
                var variantUrl = ParseBestVariantUrl(cachedBody, fresh.BaseUrl);
                if (variantUrl is null)
                {
                    PluginLog.Warn($"[BROADCAST] No variant found in master playlist session={_session.SessionId}");
                    await Task.Delay(ErrorBackoff, ct).ConfigureAwait(false);
                    return;
                }
                try
                {
                    playlistText = await client.GetStringAsync(variantUrl, ct).ConfigureAwait(false);
                    baseUrl      = BuildBaseUrl(variantUrl);
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[BROADCAST] Variant fetch failed session={_session.SessionId}", ex);
                    await Task.Delay(ErrorBackoff, ct).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                // Resolver returned no body (non-HLS or read error) — fetch directly.
                try
                {
                    playlistText = await client.GetStringAsync(fresh.FinalUrl, ct).ConfigureAwait(false);
                    baseUrl      = fresh.BaseUrl;
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[BROADCAST] Playlist fetch failed session={_session.SessionId}", ex);
                    await Task.Delay(ErrorBackoff, ct).ConfigureAwait(false);
                    return;
                }
            }
        }

        if (IsHtmlContent(playlistText))
        {
            PluginLog.Warn($"[BROADCAST] Playlist response is HTML session={_session.SessionId}");
            await Task.Delay(ErrorBackoff, ct).ConfigureAwait(false);
            return;
        }

        var segments       = ParseMediaSegments(playlistText, baseUrl);
        var mediaSeq       = ParseMediaSequence(playlistText);
        var targetDuration = ParseTargetDuration(playlistText, segments);
        PluginLog.Info($"[BROADCAST] Playlist has {segments.Count} segments mediaSeq={mediaSeq} targetDuration={targetDuration:F1}s session={_session.SessionId}");

        // Continuity guard: clear seen-set on gap so current segments are eligible.
        if (_lastMediaSeq >= 0 && mediaSeq > _lastMediaSeq + _lastSegCount)
        {
            PluginLog.Warn($"[BROADCAST] Continuity break: expected≤{_lastMediaSeq + _lastSegCount} got={mediaSeq} missed≈{mediaSeq - (_lastMediaSeq + _lastSegCount)} segs session={_session.SessionId}");
            _seenSegments.Clear();
            _seenOrder.Clear();
        }

        _lastMediaSeq = mediaSeq;
        _lastSegCount = segments.Count;

        var client2 = _httpFactory.CreateClient("JellyTV");
        var (downloadedAny, totalNewDuration) = await DownloadNewSegmentsAsync(client2, segments, mediaSeq, targetDuration, ct).ConfigureAwait(false);

        // Stall detection: CDN returning same frozen playlist (segments present but all already seen).
        if (!downloadedAny && segments.Count > 0)
        {
            _consecutiveAllSeenPolls++;
            if (_consecutiveAllSeenPolls >= 5)
            {
                PluginLog.Warn($"[BROADCAST] Frozen CDN: {_consecutiveAllSeenPolls} polls all-seen, resetting seen-set session={_session.SessionId}");
                _consecutiveAllSeenPolls = 0;
                _seenSegments.Clear();
                _seenOrder.Clear();
                _lastPublishedSeq = -1;
            }
        }
        else
        {
            _consecutiveAllSeenPolls = 0;
        }

        if (downloadedAny && totalNewDuration > 0)
            PluginLog.Info($"[BROADCAST] Window served ({totalNewDuration:F1}s) session={_session.SessionId}");

        // ── Poll timing ─────────────────────────────────────────────────────────
        // Always wait one targetDuration between polls — matches RFC 8216 §6.3.4
        // standard HLS client behavior (VLC, ffmpeg, Safari all do this).
        // The previous adaptive formula (totalNewDuration - targetDuration) caused
        // cycles of ~62s that exceeded the delivered window (~54s), draining the
        // subscriber buffer and producing micro-cuts on all channels.
        var pollDelay = TimeSpan.FromSeconds(Math.Max(targetDuration, MinPollInterval.TotalSeconds));

        await Task.Delay(pollDelay, ct).ConfigureAwait(false);
    }

    private async Task<(bool Downloaded, double TotalDuration)> DownloadNewSegmentsAsync(
        HttpClient                          client,
        List<(string Url, double Duration)> segments,
        int                                 mediaSeq,
        double                              targetDuration,
        CancellationToken                   ct)
    {
        // Live-edge clamp: on fresh start (or after stall reset), jump to the live edge
        // to avoid downloading thousands of old DVR segments before reaching current content.
        if (_lastPublishedSeq < 0 && segments.Count > LiveEdgeSegments)
        {
            var totalSegs = segments.Count;
            var skip      = totalSegs - LiveEdgeSegments;
            for (var i = 0; i < skip; i++)
                MarkSeenSeq(mediaSeq + i);
            segments  = segments.GetRange(skip, LiveEdgeSegments);
            mediaSeq += skip;
            PluginLog.Info($"[BROADCAST] DVR playlist ({totalSegs} segs): jumped to live edge seq={mediaSeq} session={_session.SessionId}");
        }

        if (_lastPublishedSeq >= 0 && mediaSeq <= _lastPublishedSeq)
        {
            var skipCount = _lastPublishedSeq - mediaSeq + 1;
            if (skipCount >= segments.Count) return (false, 0.0);
            for (var i = 0; i < skipCount; i++)
                MarkSeenSeq(mediaSeq + i);
            segments  = segments.GetRange(skipCount, segments.Count - skipCount);
            mediaSeq += skipCount;
        }

        var segProxy = _proxyPool.GetSegmentClient();

        var pending = new List<(int Seq, double Duration, Task<byte[]?> DownloadTask)>();
        for (var i = 0; i < segments.Count; i++)
        {
            var (url, dur) = segments[i];
            if (!MarkSeenSeq(mediaSeq + i)) continue;
            var realUrl = ExtractRealSegmentUrl(url);
            pending.Add((mediaSeq + i, dur, DownloadSegmentAsync(client, segProxy, realUrl, ct)));
        }

        if (pending.Count > 0)
            PluginLog.Info($"[BROADCAST] Downloading {pending.Count} new segment(s) viaProxy={segProxy is not null} session={_session.SessionId}");

        var downloaded    = false;
        var totalDuration = 0.0;

        foreach (var (seq, dur, task) in pending)
        {
            if (ct.IsCancellationRequested) break;
            var data = await task.ConfigureAwait(false);
            if (data is null || data.Length == 0)
            {
                PluginLog.Warn($"[BROADCAST] Segment returned null/empty (seq={seq}) session={_session.SessionId}");
                continue;
            }

            downloaded        = true;
            totalDuration    += dur;
            _lastPublishedSeq = seq;
            Publish(data);
        }

        return (downloaded, totalDuration);
    }

    private bool IsIdle() =>
        _activeCount == 0
        && _lastLeft != DateTimeOffset.MinValue
        && DateTimeOffset.UtcNow - _lastLeft > IdleStopDelay;

    // ─────────────────────────────────────────────────────────────────────────
    // HLS parsing helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string? ParseBestVariantUrl(string masterText, string baseUrl)
    {
        string? bestUrl  = null;
        long    bestBw   = -1;
        int     bestTier = int.MaxValue;

        using var reader = new StringReader(masterText);
        string?   line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;

            var bw      = ParseBandwidth(line);
            var tier    = GetCodecTier(ParseCodecs(line));
            var uriLine = reader.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(uriLine)) continue;

            if (tier < bestTier || (tier == bestTier && bw > bestBw))
            {
                bestTier = tier;
                bestBw   = bw;
                bestUrl  = uriLine.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? uriLine
                    : new Uri(new Uri(baseUrl), uriLine).ToString();
            }
        }

        return bestUrl;
    }

    private static string? ParseCodecs(string streamInfLine)
    {
        const string marker = "CODECS=\"";
        var idx = streamInfLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        var end   = streamInfLine.IndexOf('"', start);
        return end < 0 ? streamInfLine[start..] : streamInfLine[start..end];
    }

    private static int GetCodecTier(string? codecs)
    {
        if (string.IsNullOrEmpty(codecs)) return 2;
        var lower = codecs.ToLowerInvariant();
        if (lower.Contains("hvc1") || lower.Contains("hev1") ||
            lower.Contains("dvh1") || lower.Contains("dvhe") ||
            lower.Contains("av01") || lower.Contains("vp09"))
            return 3;
        var isAvc = lower.Contains("avc1") || lower.Contains("avc2");
        if (isAvc && lower.Contains("mp4a")) return 0;
        if (isAvc) return 1;
        return 2;
    }

    private static long ParseBandwidth(string streamInfLine)
    {
        const string marker = "BANDWIDTH=";
        var idx = streamInfLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        var start = idx + marker.Length;
        var end   = start;
        while (end < streamInfLine.Length && char.IsDigit(streamInfLine[end])) end++;
        return long.TryParse(streamInfLine[start..end], out var bw) ? bw : 0L;
    }

    private static List<(string Url, double Duration)> ParseMediaSegments(string playlistText, string baseUrl)
    {
        var segments = new List<(string Url, double Duration)>();
        using var reader = new StringReader(playlistText);

        double pendingDuration = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                var comma = line.IndexOf(',', colon + 1);
                var end   = comma >= 0 ? comma : line.Length;
                if (double.TryParse(line[(colon + 1)..end], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var d))
                    pendingDuration = d;
                continue;
            }

            if (line.StartsWith('#')) continue;
            if (line.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) continue;

            var abs = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(new Uri(baseUrl), line).ToString();

            segments.Add((abs, pendingDuration));
            pendingDuration = 0;
        }

        return segments;
    }

    private static int ParseMediaSequence(string playlistText)
    {
        const string tag = "#EXT-X-MEDIA-SEQUENCE:";
        var idx = playlistText.IndexOf(tag, StringComparison.Ordinal);
        if (idx < 0) return 0;
        var start = idx + tag.Length;
        var end   = start;
        while (end < playlistText.Length && char.IsDigit(playlistText[end])) end++;
        return int.TryParse(playlistText[start..end], out var seq) ? seq : 0;
    }

    private static double ParseTargetDuration(string playlistText, List<(string Url, double Duration)> segments)
    {
        const string tag = "#EXT-X-TARGETDURATION:";
        var idx = playlistText.IndexOf(tag, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var start = idx + tag.Length;
            var end   = start;
            while (end < playlistText.Length && (char.IsDigit(playlistText[end]) || playlistText[end] == '.'))
                end++;
            if (double.TryParse(playlistText[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                return d;
        }

        if (segments.Count > 0)
        {
            var avg = segments.Average(s => s.Duration);
            if (avg > 0) return avg;
        }

        return 6.0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Segment download
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly TimeSpan SegmentDownloadTimeout = TimeSpan.FromSeconds(8);

    private async Task<byte[]?> DownloadSegmentAsync(
        HttpClient        directClient,
        HttpClient?       proxyClient,
        string            segUrl,
        CancellationToken ct)
    {
        // Direct first: CDN segments are usually globally accessible; the proxy is
        // for the auth/redirect chain, not for bulk TS downloads. Using the proxy
        // first wastes time when the CDN is reachable directly (e.g. amagi.tv).
        // Proxy is kept as fallback for geo-restricted CDNs.
        var fallback = proxyClient != null && proxyClient != directClient ? proxyClient : null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var clientToUse = attempt == 1 ? directClient : (fallback ?? directClient);
            try
            {
                using var segCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                segCts.CancelAfter(SegmentDownloadTimeout);
                try
                {
                    using var segResp = await clientToUse
                        .GetAsync(segUrl, HttpCompletionOption.ResponseHeadersRead, segCts.Token)
                        .ConfigureAwait(false);

                    var httpStatus = (int)segResp.StatusCode;

                    if (!segResp.IsSuccessStatusCode)
                    {
                        // On segment failure clear the seen-set so the next full-chain poll
                        // can re-download these segments from the fresh URL.
                        if (segResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                        {
                            PluginLog.Warn($"[BROADCAST] Segment {httpStatus} attempt={attempt}, clearing seen-set for next poll url={segUrl}");
                            _seenSegments.Clear();
                            _seenOrder.Clear();
                            return null;
                        }

                        PluginLog.Warn($"[BROADCAST] Segment HTTP {httpStatus} attempt={attempt} url={segUrl}");
                        if (attempt >= 2) return null;
                        continue;
                    }

                    var bytes = await segResp.Content.ReadAsByteArrayAsync(segCts.Token).ConfigureAwait(false);
                    if (bytes.Length == 0)
                    {
                        PluginLog.Warn($"[BROADCAST] Segment HTTP {httpStatus} returned 0 bytes attempt={attempt} url={segUrl}");
                        if (attempt >= 2) return null;
                        continue;
                    }

                    return bytes;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    PluginLog.Warn($"[BROADCAST] Segment download timed out ({SegmentDownloadTimeout.TotalSeconds:F0}s) attempt={attempt} url={segUrl}");
                    if (attempt >= 2) return null;
                    continue;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                PluginLog.Warn($"[BROADCAST] Segment download attempt={attempt} error: {ex.Message} url={segUrl}");
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
        Interlocked.Exchange(ref _lastPublishedAtTicks, DateTimeOffset.UtcNow.Ticks);

        lock (_sync)
        {
            _ring.Enqueue(data);
            while (_ring.Count > RingBufferSegments)
                _ring.Dequeue();

            _firstDataTcs.TrySetResult(true);

            foreach (var sub in _subscribers)
                sub.TryWrite(data);
        }
    }

    // Dedup by absolute sequence number: token-agnostic and unique across all CDN naming schemes.
    private bool MarkSeenSeq(int seq)
    {
        var key = seq.ToString(CultureInfo.InvariantCulture);
        if (_seenSegments.Contains(key)) return false;

        _seenSegments.Add(key);
        _seenOrder.Enqueue(key);

        if (_seenOrder.Count > MaxSeenHistory)
        {
            var evicted = _seenOrder.Dequeue();
            _seenSegments.Remove(evicted);
        }

        return true;
    }

    /// <summary>
    /// Extracts the real segment URL from beacon/tracking wrappers.
    /// Many CDNs wrap segment URLs as: https://tracker.cdn.com/beacon?redirect_url=https%3A%2F%2Freal-cdn.com%2Fseg.ts
    /// We extract and decode the inner URL to download directly, skipping the beacon hop.
    /// </summary>
    private static string ExtractRealSegmentUrl(string url)
    {
        const string marker = "redirect_url=";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return url;

        var start   = idx + marker.Length;
        var end     = url.IndexOf('&', start);
        var encoded = end < 0 ? url[start..] : url[start..end];

        try
        {
            var decoded = Uri.UnescapeDataString(encoded);
            if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return decoded;
        }
        catch { /* malformed encoding — fall through */ }

        return url;
    }

    private static string BuildBaseUrl(string url)
    {
        var q    = url.IndexOf('?');
        var path = q >= 0 ? url[..q] : url;
        var last = path.LastIndexOf('/');
        return last > 8 ? path[..(last + 1)] : path;
    }

    private static bool IsHtmlContent(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('<')
               || t.Contains("<html", StringComparison.OrdinalIgnoreCase)
               || t.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
    }

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
