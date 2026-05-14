using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JellyTV.Models;
using JellyTV.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyTV.Controllers;

[ApiController]
[Route("JellyTV")]
public class JellyTVController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private const string HlsMimeType = "application/vnd.apple.mpegurl";
    private static readonly TimeSpan StreamForcedRenewalMinInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthySegmentWindow = TimeSpan.FromSeconds(15);

    private static Plugin P => Plugin.Instance!;
    private static SessionManagerService Sessions   => P.SessionManager;
    private static M3uAggregatorService  Aggregator => P.M3uAggregator;
    private static HlsRewriterService     HlsRewriter => P.HlsRewriter;

    public JellyTVController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    // ── M3U COMBINADO ────────────────────────────────────────────────────────
    // Jellyfin descarga esta URL como tuner M3U.
    // Combina todas las fuentes y reescribe las URLs de canales → /JellyTV/stream
    [HttpGet("m3u")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCombinedM3u([FromQuery] string? refresh = null)
    {
        var forceRefresh = string.Equals(refresh, "1", StringComparison.OrdinalIgnoreCase);
        if (!forceRefresh && !string.IsNullOrWhiteSpace(refresh) && bool.TryParse(refresh, out var parsedRefresh))
        {
            forceRefresh = parsedRefresh;
        }

        if (P.Configuration?.AutoRegisterTuner == true)
        {
            // No bloquear la respuesta M3U si el registro tarda/falla.
            _ = Task.Run(() => P.EnsureTunerRegistered($"{Request.Scheme}://{Request.Host}"));
        }

        var sourcesCount = Plugin.Instance?.Configuration?.Sources?.Count ?? 0;
        if (sourcesCount == 0)
        {
            var empty = "#EXTM3U\n# Sin fuentes configuradas. Ve a Dashboard → Plugins → JellyTV.\n";
            return Content(empty, "application/x-mpegurl", Encoding.UTF8);
        }

        try
        {
            // Evita fallar por cancelacion del cliente y permite cache/single-flight interno.
            var m3u = await Aggregator.BuildCombinedM3uAsync(GetProxyBaseUrl(), CancellationToken.None, forceRefresh).ConfigureAwait(false);
            return Content(m3u, "application/x-mpegurl", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            PluginLog.Error("[M3U] Error building M3U", ex);
            return StatusCode(500, "Error building playlist.");
        }
    }

    // ── STREAM ───────────────────────────────────────────────────────────────
    // Llamado por ffmpeg/Jellyfin para abrir un canal.
    // url = URL-safe Base64 de la URL original del canal.
    [HttpGet("stream")]
    [HttpGet("stream.m3u8")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStream([FromQuery] string url, CancellationToken cancellationToken)
    {
        // Decodificar Base64 (viene del M3U reescrito por M3uAggregatorService)
        string decodedUrl;
        try   { decodedUrl = HlsRewriterService.DecodeSegmentUrl(url); }
        catch { decodedUrl = url; } // fallback si llega sin codificar

        PluginLog.Info($"[STREAM] url={decodedUrl}");

        try
        {
            var session = await Sessions.GetOrCreateSessionAsync(decodedUrl, cancellationToken).ConfigureAwait(false);
            if (Sessions.NeedsRenewal(session))
                await Sessions.RenewSessionAsync(session, cancellationToken).ConfigureAwait(false);

            await RefreshTokenizedSessionIfStaleAsync(session, cancellationToken).ConfigureAwait(false);

            var resolved = session.LastResolved!;
            PluginLog.Info($"[STREAM] session={session.SessionId} IsHls={resolved.IsHls} FinalUrl={resolved.FinalUrl}");

            if (resolved.IsHls)
            {
                var rewrittenPlaylist = await FetchRewrittenPlaylistWithRetryAsync(session, cancellationToken)
                    .ConfigureAwait(false);

                Response.Headers["Cache-Control"] = "no-cache, no-store";
                PluginLog.Info($"[STREAM] Returning rewritten HLS playlist sid={session.SessionId}");
                return Content(rewrittenPlaylist, HlsMimeType, Encoding.UTF8);
            }

            return Redirect(resolved.FinalUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal when playback is stopped and Jellyfin aborts inflight requests.
            return new EmptyResult();
        }
        catch (HttpRequestException ex)
        {
            PluginLog.Error($"[STREAM] Upstream error", ex);
            return StatusCode(502, ex.Message);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[STREAM] Internal error", ex);
            return StatusCode(500, ex.Message);
        }
    }

    private static async Task RefreshTokenizedSessionIfStaleAsync(StreamSession session, CancellationToken ct)
    {
        var resolved = session.LastResolved;
        if (resolved is null || string.IsNullOrWhiteSpace(resolved.TokenValue))
        {
            return;
        }

        var age = DateTimeOffset.UtcNow - resolved.ResolvedAt;
        var windowSeconds = session.LastPlaylistWindowSeconds;
        var targetDurationSeconds = session.LastPlaylistTargetDurationSeconds;

        // Renew only when entering the last real segment of the measured media window.
        // If target duration is unavailable, avoid proactive renewal and rely on reactive 401/403 recovery.
        var shouldRenewByWindow = false;
        if (windowSeconds.HasValue && windowSeconds.Value > 0
            && targetDurationSeconds.HasValue && targetDurationSeconds.Value > 0)
        {
            var renewAfterSeconds = Math.Max(0, windowSeconds.Value - targetDurationSeconds.Value);
            shouldRenewByWindow = age.TotalSeconds >= renewAfterSeconds;
        }

        if (!shouldRenewByWindow)
        {
            return;
        }

        // If segments are still being served recently, avoid churning token renewal from repeated /stream polls.
        if (session.LastSegmentServedAt.HasValue
            && DateTimeOffset.UtcNow - session.LastSegmentServedAt.Value <= HealthySegmentWindow)
        {
            return;
        }

        if (session.LastForcedRenewalAt.HasValue
            && DateTimeOffset.UtcNow - session.LastForcedRenewalAt.Value < StreamForcedRenewalMinInterval)
        {
            return;
        }

        if (windowSeconds.HasValue)
        {
            PluginLog.Info($"[STREAM] Entering last segment (age={age.TotalSeconds:F1}s window={windowSeconds.Value:F1}s target={targetDurationSeconds.GetValueOrDefault(0):F1}s), forcing renewal sid={session.SessionId}");
        }
        else
        {
            PluginLog.Info($"[STREAM] Tokenized session stale ({age.TotalSeconds:F1}s), forcing renewal sid={session.SessionId}");
        }
        session.LastForcedRenewalAt = DateTimeOffset.UtcNow;
        await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
    }

    private async Task<string> FetchRewrittenPlaylistWithRetryAsync(StreamSession session, CancellationToken ct)
    {
        try
        {
            return await HlsRewriter.FetchAndRewritePlaylistAsync(session, GetProxyBaseUrl(), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsAuthFailure(ex))
        {
            PluginLog.Warn($"[STREAM] Playlist fetch got {(int?)ex.StatusCode}, renewing sid={session.SessionId} and retrying once");
            await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
            return await HlsRewriter.FetchAndRewritePlaylistAsync(session, GetProxyBaseUrl(), ct).ConfigureAwait(false);
        }
    }

    // ── SEGMENT ──────────────────────────────────────────────────────────────
    // Proxy de segmentos .ts y sub-playlists .m3u8.
    [HttpGet("segment")]
    [HttpGet("segment.{ext}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSegment([FromQuery] string sid, [FromQuery] string url, CancellationToken cancellationToken, [FromRoute] string? ext = null)
    {
        string upstreamUrl;
        try { upstreamUrl = HlsRewriterService.DecodeSegmentUrl(url); }
        catch (Exception ex)
        {
            PluginLog.Warn($"[SEGMENT] Bad URL encoding: {ex.Message}");
            return BadRequest("Invalid segment URL.");
        }

        PluginLog.Info($"[SEGMENT] sid={sid} upstream={upstreamUrl}");

        var session = Sessions.GetSessionById(sid);
        if (session is null)
        {
            PluginLog.Warn($"[SEGMENT] Unknown session {sid}");
            return NotFound("Session expired. Reopen stream.");
        }

        if (upstreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            return await ProxySubPlaylistAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);

        return await ProxySegmentAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);
    }

    // ── DIAGNOSTICS ──────────────────────────────────────────────────────────
    [HttpGet("sessions")]
    [AllowAnonymous]
    public IActionResult GetSessions()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== JellyTV Sessions @ {DateTimeOffset.Now:O} ===");
        var sessions = Plugin.Instance?.SessionManager?.GetAllSessions() ?? Array.Empty<StreamSession>();
        if (sessions.Length == 0) sb.AppendLine("No active sessions.");
        foreach (var s in sessions)
        {
            sb.AppendLine($"ID={s.SessionId}");
            sb.AppendLine($"  Upstream   = {s.UpstreamUrl}");
            sb.AppendLine($"  FinalUrl   = {s.LastResolved?.FinalUrl ?? "null"}");
            sb.AppendLine($"  IsHls      = {s.LastResolved?.IsHls}");
            sb.AppendLine($"  Expiry     = {s.LastResolved?.TokenExpiry?.ToString("O") ?? "unknown"}");
            sb.AppendLine($"  LastAccess = {s.LastAccessedAt:O}");
            sb.AppendLine();
        }
        return Content(sb.ToString(), "text/plain", Encoding.UTF8);
    }

    [HttpGet("log")]
    [AllowAnonymous]
    public IActionResult GetPluginLog([FromQuery] int lines = 500)
    {
        lines = Math.Clamp(lines, 20, 5000);

        try
        {
            var logPath = PluginLog.CurrentLogPath;
            if (string.IsNullOrWhiteSpace(logPath) || !System.IO.File.Exists(logPath))
            {
                return NotFound("No se encontro el log de JellyTV.");
            }

            var allLines = System.IO.File.ReadAllLines(logPath);
            var tail = allLines.Skip(Math.Max(0, allLines.Length - lines));

            var sb = new StringBuilder();
            sb.AppendLine($"=== JellyTV log ({Path.GetFileName(logPath)}) ===");
            sb.AppendLine($"Path: {logPath}");
            sb.AppendLine($"Lines: {Math.Min(allLines.Length, lines)}");
            sb.AppendLine();

            foreach (var line in tail)
            {
                sb.AppendLine(line);
            }

            return Content(sb.ToString(), "text/plain", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            PluginLog.Error("[LOG] Failed to read plugin log", ex);
            return StatusCode(500, "No se pudo leer el log de JellyTV.");
        }
    }

    // ── CHANNEL HEALTH & MANAGEMENT ──────────────────────────────────────────
    [HttpGet("channels")]
    [AllowAnonymous]
    public async Task<IActionResult> GetChannels([FromQuery] string? sourceId = null, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Sources.Count == 0)
        {
            return Ok(new { channels = Array.Empty<object>(), channelCount = 0, aliveCount = 0, deadCount = 0 });
        }

        try
        {
            var sources = string.IsNullOrWhiteSpace(sourceId)
                ? config.Sources
                : config.Sources.Where(s => s.Id == sourceId).ToList();

            if (!sources.Any())
            {
                return NotFound(new { error = "Source not found" });
            }

            var rows = new List<object>();
            var aliveCount = 0;
            var deadCount = 0;
            var healthCheck = Plugin.Instance?.ChannelHealthCheck;

            foreach (var source in sources.Where(s => s.IsEnabled))
            {
                // Parse M3U to extract channels
                var channels = await ParseM3uChannelsAsync(source.Url, ct).ConfigureAwait(false);
                foreach (var (channelName, channelUrl) in channels)
                {
                    ChannelHealthStatus? health = null;
                    if (healthCheck is not null && healthCheck.TryGetCachedChannelStatus(channelUrl, out var cached))
                    {
                        health = cached;
                    }

                    var status = health is null ? "unknown" : (health.IsAlive ? "alive" : "dead");
                    if (status == "alive") aliveCount++;
                    else if (status == "dead") deadCount++;

                    rows.Add(new
                    {
                        sourceId = source.Id,
                        sourceName = source.Name,
                        name = channelName,
                        url = channelUrl,
                        status,
                        httpStatus = health?.HttpStatus,
                        bytesDownloaded = health?.BytesDownloaded,
                        streamType = health?.StreamType,
                        checkedAt = health?.CheckedAt,
                        durationMs = health?.DurationMs,
                        errorMessage = health?.ErrorMessage
                    });
                }

            }
            return Ok(new
            {
                channels = rows,
                channelCount = rows.Count,
                aliveCount,
                deadCount
            });
        }
        catch (Exception ex)
        {
            PluginLog.Error("[CHANNELS] Error fetching channels", ex);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("health-check")]
    [AllowAnonymous]
    public async Task<IActionResult> StartHealthCheck([FromQuery] string? sourceId = null, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var healthCheck = Plugin.Instance?.ChannelHealthCheck;

        if (config is null || healthCheck is null)
        {
            return StatusCode(500, new { error = "Service not available" });
        }

        try
        {
            var sources = string.IsNullOrWhiteSpace(sourceId)
                ? config.Sources.Where(s => s.IsEnabled).ToList()
                : config.Sources.Where(s => s.Id == sourceId && s.IsEnabled).ToList();

            if (!sources.Any())
            {
                return NotFound(new { error = "No enabled sources found" });
            }

            // Execute in background
            _ = Task.Run(async () =>
            {
                foreach (var source in sources)
                {
                    try
                    {
                        var channels = await ParseM3uChannelsAsync(source.Url, CancellationToken.None).ConfigureAwait(false);
                        await healthCheck.CheckSourceChannelsAsync(source.Id, channels, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error($"[HEALTH-CHECK] Error checking source {source.Name}", ex);
                    }
                }
            }, CancellationToken.None);

            return Ok(new { message = "Health check started in background", sourcesCount = sources.Count });
        }
        catch (Exception ex)
        {
            PluginLog.Error("[HEALTH-CHECK] Error starting health check", ex);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private async Task<List<(string Name, string Url)>> ParseM3uChannelsAsync(
        string sourceUrl,
        CancellationToken ct)
    {
        var channels = new List<(string, string)>();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var client = _httpClientFactory.CreateClient("JellyTV");
            var content = await client.GetStringAsync(sourceUrl, cts.Token).ConfigureAwait(false);
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            string? currentName = null;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("#EXTINF:"))
                {
                    var commaIndex = trimmed.LastIndexOf(',');
                    currentName = commaIndex >= 0 && commaIndex + 1 < trimmed.Length
                        ? trimmed[(commaIndex + 1)..].Trim()
                        : "(Sin nombre)";
                }
                else if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                {
                    if (!string.IsNullOrEmpty(currentName))
                    {
                        channels.Add((currentName, trimmed));
                    }

                    currentName = null;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[CHANNELS] Failed to parse M3U: {ex.Message}");
        }

        return channels;
    }

    private async Task<IActionResult> ProxySegmentAsync(StreamSession session, string upstreamUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        var fetchUrl = upstreamUrl;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            PluginLog.Info($"[SEGMENT] attempt={attempt} url={fetchUrl}");
            var (response, errorResult) = await FetchSegmentResponseAsync(client, fetchUrl, attempt, ct).ConfigureAwait(false);
            if (errorResult is not null)
            {
                return errorResult;
            }

            if (response is null)
            {
                return StatusCode(502, "Segment fetch failed.");
            }

            PluginLog.Info($"[SEGMENT] HTTP {(int)response.StatusCode} attempt={attempt}");

            if (IsAuthFailure(response.StatusCode) && attempt == 1)
            {
                response.Dispose();

                var (renewed, nextFetchUrl) = await TryRenewAndGetSegmentUrlAsync(session, fetchUrl, ct).ConfigureAwait(false);
                if (!renewed)
                {
                    return StatusCode(502, "Renewal failed.");
                }

                fetchUrl = nextFetchUrl;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                PluginLog.Warn($"[SEGMENT] Upstream error {(int)response.StatusCode}");
                return StatusCode((int)response.StatusCode);
            }

            session.LastSegmentServedAt = DateTimeOffset.UtcNow;

            return await StreamSegmentToClientAsync(response, ct).ConfigureAwait(false);
        }
        return StatusCode(502, "Segment unavailable after renewal.");
    }

    private async Task<IActionResult> ProxySubPlaylistAsync(StreamSession session, string subUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        var fetchUrl = subUrl;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var content = await client.GetStringAsync(fetchUrl, ct).ConfigureAwait(false);
                var rewritten = HlsRewriter.RewritePlaylistContent(content, fetchUrl, session.SessionId, GetProxyBaseUrl());
                return Content(rewritten, HlsMimeType, Encoding.UTF8);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new EmptyResult();
            }
            catch (HttpRequestException ex) when (IsAuthFailure(ex) && attempt == 1)
            {
                PluginLog.Warn($"[SUBPLAYLIST] Token rejected for {fetchUrl}; renewing and retrying once.");
                try
                {
                    await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
                    fetchUrl = TryRebuildTokenizedHlsrUrl(fetchUrl, session.LastResolved?.FinalUrl);
                }
                catch (Exception renewEx)
                {
                    PluginLog.Error("[SUBPLAYLIST] Renewal failed", renewEx);
                    return StatusCode(502);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[SUBPLAYLIST] Failed {fetchUrl}", ex);
                return StatusCode(502);
            }
        }

        return StatusCode(502);
    }

    private async Task<(HttpResponseMessage? Response, IActionResult? ErrorResult)> FetchSegmentResponseAsync(HttpClient client, string fetchUrl, int attempt, CancellationToken ct)
    {
        try
        {
            var response = await client.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, new EmptyResult());
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[SEGMENT] Network error attempt {attempt}", ex);
            return (null, StatusCode(502, ex.Message));
        }
    }

    private async Task<(bool Succeeded, string NextFetchUrl)> TryRenewAndGetSegmentUrlAsync(StreamSession session, string failedUrl, CancellationToken ct)
    {
        PluginLog.Warn($"[SEGMENT] Token rejected, renewing session {session.SessionId}");

        try
        {
            await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);

            var remapped = await TryRemapSegmentUrlFromFreshPlaylistAsync(session, failedUrl, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(remapped))
            {
                PluginLog.Info($"[SEGMENT] Remapped segment URL from fresh playlist: {remapped}");
                return (true, remapped);
            }

            var rebuilt = TryRebuildTokenizedHlsrUrl(failedUrl, session.LastResolved?.FinalUrl);
            return (true, rebuilt);
        }
        catch (Exception ex)
        {
            PluginLog.Error("[SEGMENT] Renewal failed", ex);
            return (false, failedUrl);
        }
    }

    private async Task<IActionResult> StreamSegmentToClientAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/MP2T";
        if (response.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = response.Content.Headers.ContentLength.Value;
        }

        PluginLog.Info($"[SEGMENT] Streaming {Response.ContentLength ?? -1} bytes");
        using (response)
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await stream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        }

        return new EmptyResult();
    }

    private async Task<string?> TryRemapSegmentUrlFromFreshPlaylistAsync(StreamSession session, string failedSegmentUrl, CancellationToken ct)
    {
        var targetFileName = TryGetFileNameFromUrl(failedSegmentUrl);
        var finalUrl = session.LastResolved?.FinalUrl;

        if (string.IsNullOrWhiteSpace(targetFileName) || string.IsNullOrWhiteSpace(finalUrl))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("JellyTV");
            var topPlaylist = await client.GetStringAsync(finalUrl, ct).ConfigureAwait(false);

            var topBase = BuildBaseUrl(finalUrl);
            if (TryFindSegmentUrlByFileName(topPlaylist, topBase, targetFileName, out var directMatch))
            {
                return directMatch;
            }

            // If exact filename is no longer in the live window, fall back to the newest segment.
            if (TryFindLatestSegmentUrl(topPlaylist, topBase, out var topLatestSegment))
            {
                return topLatestSegment;
            }

            if (TryFindFirstSubPlaylistUrl(topPlaylist, topBase, out var subPlaylistUrl))
            {
                var mediaPlaylist = await client.GetStringAsync(subPlaylistUrl, ct).ConfigureAwait(false);
                var mediaBase = BuildBaseUrl(subPlaylistUrl);

                if (TryFindSegmentUrlByFileName(mediaPlaylist, mediaBase, targetFileName, out var nestedMatch))
                {
                    return nestedMatch;
                }

                if (TryFindLatestSegmentUrl(mediaPlaylist, mediaBase, out var nestedLatestSegment))
                {
                    return nestedLatestSegment;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[SEGMENT] Could not remap from fresh playlist: {ex.Message}");
        }

        return null;
    }

    private static bool TryFindSegmentUrlByFileName(string playlistContent, string baseUrl, string fileName, out string matchedUrl)
    {
        matchedUrl = string.Empty;

        using var reader = new StringReader(playlistContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var absolute = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(new Uri(baseUrl), line).ToString();

            var candidateName = TryGetFileNameFromUrl(absolute);
            if (!string.IsNullOrWhiteSpace(candidateName)
                && string.Equals(candidateName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                matchedUrl = absolute;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindFirstSubPlaylistUrl(string playlistContent, string baseUrl, out string subPlaylistUrl)
    {
        subPlaylistUrl = string.Empty;

        using var reader = new StringReader(playlistContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var absolute = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(new Uri(baseUrl), line).ToString();

            if (absolute.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                subPlaylistUrl = absolute;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindLatestSegmentUrl(string playlistContent, string baseUrl, out string latestSegmentUrl)
    {
        latestSegmentUrl = string.Empty;

        using var reader = new StringReader(playlistContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var absolute = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(new Uri(baseUrl), line).ToString();

            // Keep the last segment-like URI; this tracks the newest item in a media playlist.
            if (!absolute.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                latestSegmentUrl = absolute;
            }
        }

        return !string.IsNullOrWhiteSpace(latestSegmentUrl);
    }

    private static string? TryGetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return Path.GetFileName(uri.AbsolutePath);
        }
        catch
        {
            return null;
        }
    }

    private string GetProxyBaseUrl() => $"{Request.Scheme}://{Request.Host}/JellyTV";

    private static bool IsAuthFailure(HttpRequestException ex)
    {
        return ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
    }

    private static bool IsAuthFailure(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
    }

    private static string TryRebuildTokenizedHlsrUrl(string currentUrl, string? latestFinalUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUrl) || string.IsNullOrWhiteSpace(latestFinalUrl))
        {
            return currentUrl;
        }

        try
        {
            var latestToken = TryGetTokenFromUrl(latestFinalUrl);
            if (string.IsNullOrWhiteSpace(latestToken))
            {
                return currentUrl;
            }

            var marker = "/hlsr/";
            var currentUri = new Uri(currentUrl);
            var path = currentUri.AbsolutePath;
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return currentUrl;
            }

            var afterMarker = path[(idx + marker.Length)..];
            var nextSlash = afterMarker.IndexOf('/');
            if (nextSlash < 0)
            {
                return currentUrl;
            }

            var tail = afterMarker[(nextSlash + 1)..];
            var latestUri = new Uri(latestFinalUrl);

            var builder = new UriBuilder(currentUri)
            {
                Scheme = latestUri.Scheme,
                Host = latestUri.Host,
                Port = latestUri.IsDefaultPort ? -1 : latestUri.Port,
                Path = $"hlsr/{latestToken}/{tail}",
                Query = string.Empty
            };

            return builder.Uri.ToString();
        }
        catch
        {
            return currentUrl;
        }
    }

    private static string? TryGetTokenFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (string.IsNullOrWhiteSpace(uri.Query))
            {
                return null;
            }

            var query = uri.Query[0] == '?' ? uri.Query[1..] : uri.Query;
            var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("token", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }
        }
        catch
        {
            // ignore parse errors
        }

        return null;
    }

    private static string BuildBaseUrl(string url)
    {
        var uri = new Uri(url);
        var last = uri.AbsolutePath.LastIndexOf('/');
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{(last >= 0 ? uri.AbsolutePath[..(last+1)] : "/")}";
    }
}