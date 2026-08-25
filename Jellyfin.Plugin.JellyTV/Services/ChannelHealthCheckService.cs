using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Performs real stream health checks using ffmpeg (preferred, fast) or fallback real download.
/// ffmpeg: decodes a small slice of the live stream to verify playback starts quickly.
/// Fallback: downloads actual video data (HLS segments or MPEG-TS chunks) if ffmpeg unavailable.
/// Uses in-memory cache to avoid excessive checks on the same channels.
/// </summary>
public class ChannelHealthCheckService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChannelHealthCheckService> _logger;
    private readonly SessionManagerService _sessionManager;
    private readonly IApplicationPaths _applicationPaths;

    // In-memory cache: key = ChannelUrl, value = (ChannelHealthStatus, CachedAt)
    private readonly ConcurrentDictionary<string, (ChannelHealthStatus, DateTimeOffset)> _healthCache = new();

    // Hardcoded constants
    private const int CACHE_TTL_MINUTES = 30;
    private const int MAX_BYTES_TO_DOWNLOAD = 5_000_000; // 5 MB max per fallback check
    private const int MIN_BYTES_FOR_SUCCESS = 200_000;   // 200 KB minimum to consider "alive"
    private const int RESOLVE_TIMEOUT_SECONDS = 20;
    private const int REQUEST_TIMEOUT_SECONDS = 30;
    private const int FFMPEG_TIMEOUT_SECONDS = 18;
    private const int FFMPEG_NETWORK_TIMEOUT_MICROSECONDS = 5_000_000;
    private const int FFMPEG_PROBE_DURATION_SECONDS = 2;

    private string? _ffmpegPath;
    private DateTimeOffset _lastFfmpegCheck = DateTimeOffset.MinValue;

    public ChannelHealthCheckService(
        IHttpClientFactory httpClientFactory,
        ILogger<ChannelHealthCheckService> logger,
        SessionManagerService sessionManager,
        IApplicationPaths applicationPaths)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _sessionManager = sessionManager;
        _applicationPaths = applicationPaths;
    }

    /// <summary>
    /// Checks health of a single channel. Direct streams use a quick HTTP check;
    /// tokenized streams go through full session resolution + ffmpeg + fallback.
    /// </summary>
    public async Task<ChannelHealthStatus> CheckChannelAsync(string channelName, string channelUrl, int channelType = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channelUrl))
        {
            return new ChannelHealthStatus
            {
                ChannelName = channelName,
                ChannelUrl = channelUrl,
                HttpStatus = 0,
                CheckedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "URL vacía"
            };
        }

        // Check cache
        if (_healthCache.TryGetValue(channelUrl, out var cached))
        {
            var age = DateTimeOffset.UtcNow - cached.Item2;
            if (age.TotalMinutes < CACHE_TTL_MINUTES)
            {
                PluginLog.Info($"[HEALTH] Cache hit for {channelName}");
                return cached.Item1;
            }
        }

        // Direct (non-token) streams: quick HTTP check, no session resolution.
        if (channelType == 0)
        {
            var quick = await QuickHttpCheckAsync(channelName, channelUrl, ct);
            _healthCache[channelUrl] = (quick, DateTimeOffset.UtcNow);
            return quick;
        }

        var result = await PerformRealHealthCheckAsync(channelName, channelUrl, ct);
        _healthCache[channelUrl] = (result, DateTimeOffset.UtcNow);
        return result;
    }

    /// <summary>
    /// Quick HTTP GET check for direct streams. Alive = URL responds 2xx within timeout.
    /// </summary>
    private async Task<ChannelHealthStatus> QuickHttpCheckAsync(string channelName, string channelUrl, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ChannelHealthStatus
        {
            ChannelName = channelName,
            ChannelUrl = channelUrl,
            CheckedAt = DateTimeOffset.UtcNow,
            StreamType = channelUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "mpeg2ts"
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var client = _httpClientFactory.CreateClient("JellyTV");
            using var request = new HttpRequestMessage(HttpMethod.Get, channelUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            result.HttpStatus = (int)response.StatusCode;
            result.BytesDownloaded = response.IsSuccessStatusCode ? MIN_BYTES_FOR_SUCCESS : 0;
            if (!response.IsSuccessStatusCode)
                result.ErrorMessage = $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Timeout (10s)";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        sw.Stop();
        result.DurationMs = (int)sw.ElapsedMilliseconds;
        return result;
    }

    public bool TryGetCachedChannelStatus(string channelUrl, out ChannelHealthStatus status)
    {
        status = default!;

        if (string.IsNullOrWhiteSpace(channelUrl))
        {
            return false;
        }

        if (!_healthCache.TryGetValue(channelUrl, out var cached))
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - cached.Item2;
        if (age.TotalMinutes >= CACHE_TTL_MINUTES)
        {
            return false;
        }

        status = cached.Item1;
        return true;
    }

    /// <summary>
    /// Checks health of all channels in a source by downloading real stream data in parallel (4 channels at a time).
    /// </summary>
    public async Task<List<ChannelHealthStatus>> CheckSourceChannelsAsync(
        string sourceId,
        List<(string Name, string Url, int Type)> channels,
        CancellationToken ct = default)
    {
        PluginLog.Info($"[HEALTH] Starting health check for {channels.Count} channels from source {sourceId}");

        var results = new List<ChannelHealthStatus>();

        var enabledProxyCount = Plugin.Instance?.Configuration?.Proxies?.Count(p => p.IsEnabled) ?? 0;
        var maxParallel = Math.Clamp(Math.Max(6, enabledProxyCount), 6, 24);
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

        PluginLog.Info($"[HEALTH] Parallel checks configured: {maxParallel} (enabled proxies: {enabledProxyCount})");

        try
        {
            var tasks = channels.Select(async ch =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await CheckChannelAsync(ch.Name, ch.Url, ch.Type, ct).ConfigureAwait(false);
                    lock (results)
                    {
                        results.Add(result);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PluginLog.Warn($"[HEALTH] Health check cancelled for source {sourceId}");
        }

        var alive = results.Count(r => r.IsAlive);
        var dead = results.Count - alive;
        PluginLog.Info($"[HEALTH] Health check complete: {alive} vivos, {dead} muertos");

        return results;
    }

    /// <summary>
    /// Performs the actual health check using ffmpeg (preferred) or fallback real download.
    /// ffmpeg verifies that the stream can actually start decoding within a short window.
    /// </summary>
    private async Task<ChannelHealthStatus> PerformRealHealthCheckAsync(
        string channelName,
        string channelUrl,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ChannelHealthStatus
        {
            ChannelName = channelName,
            ChannelUrl = channelUrl,
            CheckedAt = DateTimeOffset.UtcNow
        };

        try
        {
            // Resolve URL with its own timeout budget.
            using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            resolveCts.CancelAfter(TimeSpan.FromSeconds(RESOLVE_TIMEOUT_SECONDS));

            // Get or create session for this channel (handles token renewal)
            var session = await _sessionManager.GetOrCreateSessionAsync(channelUrl, resolveCts.Token).ConfigureAwait(false);
            if (session?.LastResolved is null)
            {
                result.HttpStatus = 0;
                result.ErrorMessage = "No se pudo resolver la URL";
                return result;
            }

            var resolvedUrl = session.LastResolved.FinalUrl;
            var resolvedStreamType = session.LastResolved.IsHls ? "hls" : "mpeg2ts";

            // Probe with ffmpeg under a dedicated timeout budget.
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(FFMPEG_TIMEOUT_SECONDS + 3));
            var (ffmpegResult, ffmpegTimedOut) = await TryCheckWithFfmpegAsync(channelName, resolvedUrl, probeCts.Token);
            if (ffmpegResult is not null)
            {
                result.HttpStatus = ffmpegResult.HttpStatus;
                result.BytesDownloaded = ffmpegResult.BytesDownloaded;
                result.ErrorMessage = ffmpegResult.ErrorMessage;
                // ffmpeg valido confirma reproduccion real; aqui si es seguro etiquetar tipo.
                result.StreamType = resolvedStreamType;
                return result;
            }

            // Fallback gets a fresh timeout budget so ffmpeg probe time doesn't starve it.
            using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fallbackCts.CancelAfter(TimeSpan.FromSeconds(REQUEST_TIMEOUT_SECONDS));

            // When ffmpeg timed out, the token is now ~18s old and likely expired.
            // Pre-renew here so the fallback starts with a guaranteed fresh token,
            // eliminating the 401-renew-401 double-round-trip in CheckHlsStreamAsync.
            if (ffmpegTimedOut)
            {
                PluginLog.Info($"[HEALTH] Pre-renewing session after ffmpeg timeout for {channelName}");
                await _sessionManager.RenewSessionAsync(session, fallbackCts.Token).ConfigureAwait(false);
            }

            // Use the (potentially refreshed) final URL for direct-stream fallback.
            var fallbackUrl = session.LastResolved?.FinalUrl ?? resolvedUrl;

            // Fallback: download real data if ffmpeg is unavailable or failed.
            if (session.LastResolved?.IsHls == true)
            {
                result = await CheckHlsStreamAsync(channelName, session, fallbackCts.Token);
            }
            else
            {
                result = await CheckDirectStreamAsync(channelName, fallbackUrl, fallbackCts.Token);
            }

            result.ChannelName = channelName;
            result.ChannelUrl = channelUrl;
            result.CheckedAt = DateTimeOffset.UtcNow;

            // Nunca etiquetar tipo en checks fallidos para evitar falsos positivos.
            if (!result.IsAlive)
            {
                result.StreamType = "unknown";
            }
            else if (string.IsNullOrWhiteSpace(result.StreamType) ||
                     string.Equals(result.StreamType, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                result.StreamType = resolvedStreamType;
            }
        }
        catch (OperationCanceledException)
        {
            result.HttpStatus = 0;
            result.ErrorMessage = $"Timeout ({REQUEST_TIMEOUT_SECONDS}s)";
        }
        catch (HttpRequestException ex)
        {
            result.HttpStatus = (int?)ex.StatusCode ?? 0;
            result.ErrorMessage = ex.Message;
            PluginLog.Warn($"[HEALTH] HTTP error for {channelName}: {ex.Message}");
        }
        catch (Exception ex)
        {
            result.HttpStatus = 0;
            result.ErrorMessage = ex.Message;
            PluginLog.Error($"[HEALTH] Unexpected error checking {channelName}", ex);
        }
        finally
        {
            sw.Stop();
            result.DurationMs = (int)sw.ElapsedMilliseconds;
        }

        return result;
    }

    /// <summary>
    /// Attempts to check stream health using ffmpeg.
    /// Returns (null, false) if ffmpeg is unavailable or exits with error (fallback proceeds with current token).
    /// Returns (null, true) if ffmpeg TIMED OUT (token likely expired; caller should pre-renew before fallback).
    /// Returns (status, false) on success.
    /// </summary>
    private async Task<(ChannelHealthStatus? Status, bool TimedOut)> TryCheckWithFfmpegAsync(string channelName, string streamUrl, CancellationToken ct)
    {
        var ffmpeg = await GetFfmpegPathAsync(ct);
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return (null, false); // ffmpeg not available
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = BuildFfmpegArgs(streamUrl),
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(processInfo);
            if (process is null)
            {
                return (null, false);
            }

            // Wait for ffmpeg to finish (with timeout)
            var completed = await Task.Run(
                () => process.WaitForExit(FFMPEG_TIMEOUT_SECONDS * 1000),
                ct).ConfigureAwait(false);

            sw.Stop();

            if (!completed)
            {
                process.Kill();
                PluginLog.Warn($"[HEALTH] ffmpeg timeout channel={channelName} url={streamUrl}");
                // TimedOut=true signals caller to pre-renew (token is now ~18s old, likely expired)
                return (null, true);
            }

            if (process.ExitCode != 0)
            {
                return (null, false); // Not a fatal error, try fallback with current token
            }

            PluginLog.Info($"[HEALTH] ffmpeg OK channel={channelName} in ~{sw.ElapsedMilliseconds}ms for {streamUrl}");
            return (new ChannelHealthStatus
            {
                HttpStatus = 200,
                BytesDownloaded = MIN_BYTES_FOR_SUCCESS,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, false);
        }
        catch (Exception ex)
        {
            PluginLog.Info($"[HEALTH] ffmpeg check failed channel={channelName}: {ex.Message}");
            return (null, false); // Fallback to real download
        }
    }

    /// <summary>
    /// Builds ffmpeg command-line arguments for a short decode probe.
    /// </summary>
    private static string BuildFfmpegArgs(string streamUrl)
    {
        return $@"-v error -hide_banner -nostats -nostdin -timeout {FFMPEG_NETWORK_TIMEOUT_MICROSECONDS} -i ""{streamUrl}"" -t {FFMPEG_PROBE_DURATION_SECONDS} -f null -";
    }

    /// <summary>
    /// Finds ffmpeg executable from Jellyfin-managed locations first.
    /// Caches the result to avoid repeated filesystem checks.
    /// </summary>
    private async Task<string?> GetFfmpegPathAsync(CancellationToken ct)
    {
        // Cache check every 1 hour
        if (_ffmpegPath is not null || DateTimeOffset.UtcNow - _lastFfmpegCheck < TimeSpan.FromHours(1))
        {
            return _ffmpegPath;
        }

        _lastFfmpegCheck = DateTimeOffset.UtcNow;

        foreach (var candidate in BuildJellyfinFfmpegCandidates())
        {
            try
            {
                if (File.Exists(candidate))
                {
                    PluginLog.Info($"[HEALTH] Found ffmpeg at: {candidate}");
                    _ffmpegPath = candidate;
                    return _ffmpegPath;
                }
            }
            catch
            {
                // Ignore filesystem errors
            }
        }

        // Last resort only: rely on process PATH if Jellyfin paths were not enough.
        foreach (var candidate in new[] { "ffmpeg", "ffmpeg.exe" })
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(info);
                if (process is null)
                {
                    continue;
                }

                var completed = await Task.Run(() => process.WaitForExit(1500), ct).ConfigureAwait(false);
                if (completed && process.ExitCode == 0)
                {
                    PluginLog.Info($"[HEALTH] Using ffmpeg from PATH as fallback: {candidate}");
                    _ffmpegPath = candidate;
                    return _ffmpegPath;
                }
            }
            catch
            {
                // Ignore and continue fallback probing.
            }
        }

        PluginLog.Warn("[HEALTH] ffmpeg no encontrado en rutas de Jellyfin; se usa fallback por descarga real");
        _ffmpegPath = string.Empty; // Mark as checked but not found
        return null;
    }

    private IEnumerable<string> BuildJellyfinFfmpegCandidates()
    {
        var isWindows = OperatingSystem.IsWindows();
        var exeName = isWindows ? "ffmpeg.exe" : "ffmpeg";
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new List<string>();

        // 1) Binarios junto al servidor Jellyfin.
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            candidates.Add(Path.Combine(baseDir, exeName));
            candidates.Add(Path.Combine(baseDir, "ffmpeg", exeName));
            candidates.Add(Path.Combine(baseDir, "ffmpeg", "bin", exeName));
        }

        // 2) Binarios administrados por Jellyfin dentro de ProgramDataPath.
        if (!string.IsNullOrWhiteSpace(_applicationPaths.ProgramDataPath))
        {
            var ffmpegRoot = Path.Combine(_applicationPaths.ProgramDataPath, "ffmpeg");
            candidates.Add(Path.Combine(ffmpegRoot, exeName));
            candidates.Add(Path.Combine(ffmpegRoot, "bin", exeName));

            try
            {
                if (Directory.Exists(ffmpegRoot))
                {
                    foreach (var dir in Directory.EnumerateDirectories(ffmpegRoot))
                    {
                        candidates.Add(Path.Combine(dir, exeName));
                        candidates.Add(Path.Combine(dir, "bin", exeName));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo enumerar directorios de ffmpeg en ProgramDataPath");
            }
        }

        return candidates
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads HLS playlist and first segment to verify the stream is working.
    /// </summary>
    private async Task<ChannelHealthStatus> CheckHlsStreamAsync(
        string channelName,
        StreamSession session,
        CancellationToken ct)
    {
        var result = new ChannelHealthStatus { StreamType = "unknown" };
        // Set to true after a renewal so attempt=2 uses a lightweight playlist-only check.
        // Segment downloads fail without the original proxy/cookie context used during resolution;
        // a fresh m3u8 with live segments is sufficient proof the channel is streaming.
        bool isPostRenewal = false;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var resolved = session.LastResolved;
                if (resolved is null)
                {
                    result.HttpStatus = 0;
                    result.ErrorMessage = "Sesion sin URL resuelta";
                    return result;
                }

                var client = _httpClientFactory.CreateClient("JellyTV");
                var playlistUrl = resolved.FinalUrl;
                var playlistBaseUrl = resolved.BaseUrl;

                // 1. Download playlist (usually small, ~5-10 KB)
                using var playlistResp = await client.GetAsync(playlistUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!playlistResp.IsSuccessStatusCode)
                {
                    if (attempt == 1 && (int)playlistResp.StatusCode is 401 or 403)
                    {
                        PluginLog.Warn($"[HEALTH] Playlist auth {(int)playlistResp.StatusCode} channel={channelName}; renewing session and retrying once");
                        await _sessionManager.RenewSessionAsync(session, ct).ConfigureAwait(false);
                        continue;
                    }

                    result.HttpStatus = (int)playlistResp.StatusCode;
                    result.ErrorMessage = $"Playlist HTTP {(int)playlistResp.StatusCode}";
                    return result;
                }

                var playlistContent = await playlistResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                result.BytesDownloaded = Encoding.UTF8.GetByteCount(playlistContent);

                // Master playlist: pick best variant first (same strategy as previous broadcast path).
                if (playlistContent.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
                {
                    var variantUrl = ParseBestVariantUrl(playlistContent, playlistBaseUrl);
                    if (!string.IsNullOrWhiteSpace(variantUrl))
                    {
                        using var variantResp = await client.GetAsync(variantUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (!variantResp.IsSuccessStatusCode)
                        {
                            if (attempt == 1 && (int)variantResp.StatusCode is 401 or 403)
                            {
                                PluginLog.Warn($"[HEALTH] Variant auth {(int)variantResp.StatusCode} channel={channelName}; renewing session and retrying once");
                                await _sessionManager.RenewSessionAsync(session, ct).ConfigureAwait(false);
                                continue;
                            }

                            result.HttpStatus = (int)variantResp.StatusCode;
                            result.ErrorMessage = $"Variant HTTP {(int)variantResp.StatusCode}";
                            return result;
                        }

                        playlistContent = await variantResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        result.BytesDownloaded += Encoding.UTF8.GetByteCount(playlistContent);
                        playlistBaseUrl = BuildBaseUrl(variantUrl);
                    }
                }

                // 2. Extract and try multiple segments (first segment can already be stale in live HLS).
                var segmentUrls = ExtractHlsSegments(playlistContent, playlistBaseUrl, maxCount: 5);
                if (segmentUrls.Count == 0)
                {
                    result.HttpStatus = 200;
                    result.ErrorMessage = "No se encontraron segmentos en la playlist";
                    return result;
                }

                // Post-renewal lightweight check: a fresh m3u8 with segments present means the
                // channel is actively streaming. We skip segment download because the health-check
                // HttpClient lacks the proxy/cookie context used during resolution, which causes
                // false 401s on segment requests even with a valid fresh token.
                if (isPostRenewal)
                {
                    result.HttpStatus = 200;
                    result.BytesDownloaded = MIN_BYTES_FOR_SUCCESS;
                    result.StreamType = "hls";
                    PluginLog.Info($"[HEALTH] HLS playlist OK channel={channelName}: {segmentUrls.Count} segments found (post-renewal lightweight check)");
                    return result;
                }

                var lastSegmentStatus = 0;
                var mustRenew = false;

                foreach (var rawSegmentUrl in segmentUrls)
                {
                    // Handle beacon/tracking wrappers: .../beacon?...&redirect_url=https%3A%2F%2Freal-segment.ts
                    var segmentUrl = ExtractRealSegmentUrl(rawSegmentUrl);

                    using var segmentResp = await client.GetAsync(segmentUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!segmentResp.IsSuccessStatusCode)
                    {
                        lastSegmentStatus = (int)segmentResp.StatusCode;

                        // Tokenized streams may require one forced renewal between checks.
                        if (attempt == 1 && lastSegmentStatus is 401 or 403)
                        {
                            PluginLog.Warn($"[HEALTH] Segment auth {lastSegmentStatus} channel={channelName}; renewing session and retrying once");
                            mustRenew = true;
                            break;
                        }

                        // For 404 on live playlists, try next segment before failing the channel.
                        if (lastSegmentStatus == 404)
                        {
                            continue;
                        }

                        continue;
                    }

                    long segmentBytes = 0;
                    using var stream = await segmentResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var buffer = new byte[64 * 1024]; // 64 KB chunks
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        segmentBytes += read;
                        if (segmentBytes >= MAX_BYTES_TO_DOWNLOAD)
                        {
                            break;
                        }
                    }

                    result.HttpStatus = 200;
                    result.BytesDownloaded += segmentBytes;
                    result.StreamType = "hls";
                    PluginLog.Info($"[HEALTH] HLS check OK channel={channelName}: {result.BytesDownloaded} bytes");
                    return result;
                }

                if (mustRenew)
                {
                    await _sessionManager.RenewSessionAsync(session, ct).ConfigureAwait(false);
                    isPostRenewal = true;
                    continue;
                }

                result.HttpStatus = lastSegmentStatus == 0 ? 0 : lastSegmentStatus;
                result.ErrorMessage = lastSegmentStatus == 0
                    ? "No se pudo descargar un segmento valido"
                    : $"Segmento HTTP {lastSegmentStatus}";
                return result;
            }
            catch (Exception ex)
            {
                result.HttpStatus = 0;
                result.ErrorMessage = ex.Message;
                if (attempt == 2)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private static string ExtractRealSegmentUrl(string url)
    {
        const string marker = "redirect_url=";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return url;
        }

        var start = idx + marker.Length;
        var end = url.IndexOf('&', start);
        var encoded = end < 0 ? url[start..] : url[start..end];

        try
        {
            var decoded = Uri.UnescapeDataString(encoded);
            if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return decoded;
            }
        }
        catch
        {
            // Keep original URL when malformed.
        }

        return url;
    }

    private static string? ParseBestVariantUrl(string masterText, string baseUrl)
    {
        string? bestUrl = null;
        long bestBw = -1;
        int bestTier = int.MaxValue;

        using var reader = new StringReader(masterText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                continue;
            }

            var bw = ParseBandwidth(line);
            var tier = GetCodecTier(ParseCodecs(line));
            var uriLine = reader.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(uriLine))
            {
                continue;
            }

            if (tier < bestTier || (tier == bestTier && bw > bestBw))
            {
                bestTier = tier;
                bestBw = bw;
                bestUrl = uriLine.StartsWith("http", StringComparison.OrdinalIgnoreCase)
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
        if (idx < 0)
        {
            return null;
        }

        var start = idx + marker.Length;
        var end = streamInfLine.IndexOf('"', start);
        return end < 0 ? streamInfLine[start..] : streamInfLine[start..end];
    }

    private static int GetCodecTier(string? codecs)
    {
        if (string.IsNullOrEmpty(codecs))
        {
            return 2;
        }

        var lower = codecs.ToLowerInvariant();
        if (lower.Contains("hvc1") || lower.Contains("hev1") ||
            lower.Contains("dvh1") || lower.Contains("dvhe") ||
            lower.Contains("av01") || lower.Contains("vp09"))
        {
            return 3;
        }

        var isAvc = lower.Contains("avc1") || lower.Contains("avc2");
        if (isAvc && lower.Contains("mp4a"))
        {
            return 0;
        }

        if (isAvc)
        {
            return 1;
        }

        return 2;
    }

    private static long ParseBandwidth(string streamInfLine)
    {
        const string marker = "BANDWIDTH=";
        var idx = streamInfLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return 0;
        }

        var start = idx + marker.Length;
        var end = start;
        while (end < streamInfLine.Length && char.IsDigit(streamInfLine[end]))
        {
            end++;
        }

        return long.TryParse(streamInfLine[start..end], out var bw) ? bw : 0L;
    }

    private static string BuildBaseUrl(string url)
    {
        var q = url.IndexOf('?');
        var path = q >= 0 ? url[..q] : url;
        var last = path.LastIndexOf('/');
        return last > 8 ? path[..(last + 1)] : path;
    }

    /// <summary>
    /// Downloads first 2-5 MB of a direct stream (MPEG-TS or similar) to verify it's working.
    /// </summary>
    private async Task<ChannelHealthStatus> CheckDirectStreamAsync(
        string channelName,
        string streamUrl,
        CancellationToken ct)
    {
        var result = new ChannelHealthStatus { StreamType = "unknown" };

        try
        {
            var client = _httpClientFactory.CreateClient("JellyTV");

            using var resp = await client.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                result.HttpStatus = (int)resp.StatusCode;
                result.ErrorMessage = $"Stream HTTP {(int)resp.StatusCode}";
                return result;
            }

            long bytesDownloaded = 0;
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[128 * 1024]; // 128 KB chunks
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                bytesDownloaded += read;
                if (bytesDownloaded >= MAX_BYTES_TO_DOWNLOAD)
                {
                    break;
                }
            }

            result.HttpStatus = 200;
            result.BytesDownloaded = bytesDownloaded;
            result.StreamType = "mpeg2ts";
            PluginLog.Info($"[HEALTH] Direct stream check OK channel={channelName}: {bytesDownloaded} bytes");
        }
        catch (Exception ex)
        {
            result.HttpStatus = 0;
            result.ErrorMessage = ex.Message;
            throw;
        }

        return result;
    }

    /// <summary>
    /// Extracts up to <paramref name="maxCount"/> segment URLs from an HLS playlist.
    /// Resolves relative URLs to absolute using the base URL.
    /// </summary>
    private static List<string> ExtractHlsSegments(string playlistContent, string baseUrl, int maxCount)
    {
        var segments = new List<string>(maxCount);

        foreach (var line in playlistContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
            {
                continue;
            }

            // Found a segment URL
            var segmentUrl = trimmed;
            if (!segmentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Relative URL — resolve against base
                if (!baseUrl.EndsWith("/"))
                {
                    baseUrl += "/";
                }

                segmentUrl = baseUrl + segmentUrl.TrimStart('/');
            }

            segments.Add(segmentUrl);
            if (segments.Count >= maxCount)
            {
                break;
            }
        }

        return segments;
    }

    /// <summary>
    /// Clears all cached health check results.
    /// </summary>
    public void ClearCache()
    {
        _healthCache.Clear();
        PluginLog.Info("[HEALTH] Health check cache cleared");
    }

    /// <summary>
    /// Returns count of cached entries (for diagnostics).
    /// </summary>
    public int GetCacheSize() => _healthCache.Count;
}
