using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTV.Controllers;

[ApiController]
[Route("JellyTV")]
public class JellyTVController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITaskManager? _taskManager;
    private const string HlsMimeType = "application/vnd.apple.mpegurl";
    private static readonly TimeSpan StreamForcedRenewalMinInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthySegmentWindow = TimeSpan.FromSeconds(15);

    private static Plugin P => Plugin.Instance!;
    private static SessionManagerService Sessions   => P.SessionManager;
    private static HlsRewriterService     HlsRewriter => P.HlsRewriter;

    public JellyTVController(IHttpClientFactory httpClientFactory, ITaskManager? taskManager = null)
    {
        _httpClientFactory = httpClientFactory;
        _taskManager = taskManager;
    }

    // ── DIAGNOSTIC M3U (opcional, para backward compat) ──────────────────────
    [HttpGet("m3u")]
    [AllowAnonymous]
    public IActionResult GetCombinedM3u()
    {
        // Set base URL on first access so ILiveTvService can build correct stream URLs.
        P.KnownBaseUrl = $"{Request.Scheme}://{Request.Host}";

        var channels = P.Configuration?.Channels?.Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Url)).ToList() ?? new();
        if (channels.Count == 0)
            return Content("#EXTM3U\n# Sin canales. Ve a Dashboard → Plugins → JellyTV.\n", "application/x-mpegurl", Encoding.UTF8);

        var sb = new StringBuilder("#EXTM3U\n");
        var proxy = $"{Request.Scheme}://{Request.Host}/JellyTV";
        foreach (var ch in channels)
        {
            var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(ch.Url)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            sb.AppendLine($"#EXTINF:-1 tvg-id=\"{ch.TvgId ?? ""}\" tvg-logo=\"{ch.LogoUrl ?? ""}\" group-title=\"{ch.Category ?? ""}\",{ch.Name}");
            sb.AppendLine($"{proxy}/stream?url={enc}");
        }
        return Content(sb.ToString(), "application/x-mpegurl", Encoding.UTF8);
    }

    // ── STREAM ───────────────────────────────────────────────────────────────
    // Llamado por ffmpeg/Jellyfin para abrir un canal.
    // url = URL-safe Base64 de la URL original del canal.
    [HttpGet("stream")]
    [HttpGet("stream.m3u8")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStream([FromQuery] string url, CancellationToken cancellationToken)
    {
        // Ensure KnownBaseUrl is set for ILiveTvService.
        P.KnownBaseUrl ??= $"{Request.Scheme}://{Request.Host}";

        // Decodificar Base64
        string decodedUrl;
        try   { decodedUrl = HlsRewriterService.DecodeSegmentUrl(url); }
        catch { decodedUrl = url; } // fallback si llega sin codificar

        // Tipo de canal: 0=Directo, 1=FAST, 2=TokenEfimero, 3=TokenPermanente.
        var channel = Plugin.Instance?.Configuration?.Channels
            .FirstOrDefault(c => string.Equals(c.Url, decodedUrl, StringComparison.Ordinal));
        var channelType = channel?.Type ?? 0;

        // Los tokens efimeros (Xtream) deben reproducirse SIEMPRE via HLS (segmentos +
        // renovacion proactiva). El path .ts directo reconecta cada ~20s y se congela.
        if (channelType == 2 && decodedUrl.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            decodedUrl = decodedUrl[..^3] + ".m3u8";
            PluginLog.Info($"[STREAM] Token ephemeral: normalizado .ts -> .m3u8: {decodedUrl}");
        }

        // ── .ts path: direct stream with reconnect (solo Directo/FAST) ──
        if (decodedUrl.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return await StreamTsDirectAsync(decodedUrl, cancellationToken).ConfigureAwait(false);
        }

        // ── .m3u8 path: HLS playlist proxy (fetch, rewrite, cache) ──────
        try
        {
            var session = await Sessions.GetOrCreateSessionAsync(decodedUrl, channelType, cancellationToken).ConfigureAwait(false);
            var resolved = session.LastResolved!;
            if (!resolved.IsHls)
            {
                PluginLog.Info($"[STREAM] Non-HLS, redirecting to {resolved.FinalUrl}");
                return Redirect(resolved.FinalUrl);
            }
            var rewritten = await HlsRewriter.FetchAndRewritePlaylistAsync(
                session, GetProxyBaseUrl(), cancellationToken).ConfigureAwait(false);
            Response.Headers["Cache-Control"] = "no-cache, no-store";
            return Content(rewritten, HlsMimeType, Encoding.UTF8);
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

        if (Plugin.Instance?.Configuration.VerboseLogging == true)
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
        if (config is null || config.Channels.Count == 0)
        {
            return Ok(new { channels = Array.Empty<object>(), channelCount = 0, aliveCount = 0, deadCount = 0 });
        }

        try
        {
            var channels = string.IsNullOrWhiteSpace(sourceId)
                ? config.Channels
                : config.Channels.Where(c => c.Id == sourceId).ToList();

            if (!channels.Any())
            {
                return NotFound(new { error = "Channel not found" });
            }

            var rows = new List<object>();
            var aliveCount = 0;
            var deadCount = 0;
            var healthCheck = Plugin.Instance?.ChannelHealthCheck;

            foreach (var ch in channels.Where(c => c.IsEnabled))
            {
                ChannelHealthStatus? health = null;
                if (healthCheck is not null && healthCheck.TryGetCachedChannelStatus(ch.Url, out var cached))
                {
                    health = cached;
                }

                var status = health is null ? "unknown" : (health.IsAlive ? "alive" : "dead");
                if (status == "alive") aliveCount++;
                else if (status == "dead") deadCount++;

                rows.Add(new
                {
                    id = ch.Id,
                    name = ch.Name,
                    url = ch.Url,
                    type = ch.Type,
                    category = ch.Category,
                    isEnabled = ch.IsEnabled,
                    logoUrl = ch.LogoUrl,
                    tvgId = ch.TvgId,
                    lastCheckAlive = ch.LastCheckAlive,
                    lastCheckedAt = ch.LastCheckedAt,
                    status,
                    httpStatus = health?.HttpStatus,
                    bytesDownloaded = health?.BytesDownloaded,
                    streamType = health?.StreamType,
                    checkedAt = health?.CheckedAt,
                    durationMs = health?.DurationMs,
                    errorMessage = health?.ErrorMessage ?? ch.LastCheckError
                });
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

    [HttpPost("refresh-guide")]
    [AllowAnonymous]
    public IActionResult RefreshGuide()
    {
        if (_taskManager is null)
            return Ok(new { status = "no-task-manager" });

        try
        {
            var guideTask = _taskManager.ScheduledTasks
                .FirstOrDefault(t => t.Name.Contains("Refresh Guide", StringComparison.OrdinalIgnoreCase));
            if (guideTask is null)
                return Ok(new { status = "task-not-found" });

            _taskManager.Execute(guideTask, new TaskOptions());
            return Ok(new { status = "started", task = guideTask.Name });
        }
        catch (Exception ex)
        {
            return Ok(new { status = "error", error = ex.Message });
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
            var channels = string.IsNullOrWhiteSpace(sourceId)
                ? config.Channels.Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Url)).ToList()
                : config.Channels.Where(c => c.Id == sourceId && c.IsEnabled && !string.IsNullOrWhiteSpace(c.Url)).ToList();

            if (!channels.Any())
            {
                return NotFound(new { error = "No enabled channels found" });
            }

            // Execute in background
            _ = Task.Run(async () =>
            {
                var channelUrls = channels.Select(c => (c.Name, c.Url, c.Type)).ToList();
                var results = await healthCheck.CheckSourceChannelsAsync("manual", channelUrls, CancellationToken.None).ConfigureAwait(false);

                // Update channel health status
                foreach (var ch in channels)
                {
                    var r = results.FirstOrDefault(res => res.ChannelUrl == ch.Url);
                    if (r is not null)
                    {
                        ch.LastCheckAlive = r.IsAlive;
                        ch.LastCheckedAt = DateTimeOffset.UtcNow;
                        ch.LastCheckError = r.ErrorMessage;
                    }
                }

                try { Plugin.Instance?.SaveConfiguration(); } catch { }

                var reporter = Plugin.Instance?.WorkerReporter;
                if (reporter is not null)
                    await reporter.ReportAsync(results, CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None);

            return Ok(new { message = "Health check started in background", channelsCount = channels.Count });
        }
        catch (Exception ex)
        {
            PluginLog.Error("[HEALTH-CHECK] Error starting health check", ex);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> ProxySegmentAsync(StreamSession session, string upstreamUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        var fetchUrl = upstreamUrl;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (Plugin.Instance?.Configuration.VerboseLogging == true)
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

            if (Plugin.Instance?.Configuration.VerboseLogging == true)
                PluginLog.Info($"[SEGMENT] HTTP {(int)response.StatusCode} attempt={attempt}");

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                response.Dispose();
                PluginLog.Warn($"[SEGMENT] Upstream error {(int)statusCode} attempt={attempt}");

                if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 1)
                {
                    try
                    {
                        await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
                        var remapped = await TryRemapSegmentUrlFromFreshPlaylistAsync(session, fetchUrl, ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(remapped))
                        {
                            fetchUrl = remapped;
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warn($"[SEGMENT] Token refresh/remap failed: {ex.Message}");
                    }
                }

                return StatusCode((int)statusCode);
            }

            session.LastSegmentServedAt = DateTimeOffset.UtcNow;

            return await StreamSegmentToClientAsync(response, ct).ConfigureAwait(false);
        }

        return StatusCode(502, "Segment unavailable after token refresh.");
    }

    private async Task<IActionResult> ProxySubPlaylistAsync(StreamSession session, string subUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        var fetchUrl = subUrl;

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
        catch (Exception ex)
        {
            PluginLog.Error($"[SUBPLAYLIST] Failed {fetchUrl}", ex);
            return StatusCode(502);
        }
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

    private async Task<IActionResult> StreamSegmentToClientAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/MP2T";
        if (response.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = response.Content.Headers.ContentLength.Value;
        }

        if (Plugin.Instance?.Configuration.VerboseLogging == true)
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

    // ── Simple redirect: resolve alfa → tuenisix, 302 to Jellyfin ────────
    private async Task<IActionResult> RedirectToResolvedTs(string alfaUrl, CancellationToken ct)
    {
        try
        {
            using var h = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 15, UseCookies = false };
            using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(15) };
            using var r = await c.GetAsync(alfaUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var final = r.RequestMessage?.RequestUri?.ToString() ?? alfaUrl;
            PluginLog.Info($"[TS] Redirect → {final}");
            return Redirect(final);
        }
        catch (Exception ex) { PluginLog.Error($"[TS] {ex.Message}"); return StatusCode(502, ex.Message); }
    }

    // ── Direct stream with reconnect: no channels, no complexity ──────────
    private async Task<IActionResult> StreamTsDirectAsync(string alfaUrl, CancellationToken ct)
    {
        Response.ContentType = "video/mp2t";
        Response.Headers["Cache-Control"] = "no-cache, no-store";
        PluginLog.Info($"[StreamTS] Starting direct stream for {alfaUrl}");

        int round = 0;
        while (!ct.IsCancellationRequested)
        {
            round++;
            try
            {
                using var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 15, UseCookies = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                PluginLog.Info($"[StreamTS] Round {round}: connecting...");
                using var response = await client.GetAsync(alfaUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? alfaUrl;
                PluginLog.Info($"[StreamTS] Round {round}: connected → {finalUrl}, streaming bytes...");
                using var upstream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await upstream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
                PluginLog.Info($"[StreamTS] Round {round}: upstream EOF (stream ended)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                PluginLog.Info($"[StreamTS] Round {round}: {ex.GetType().Name} — {ex.Message}, retrying in 500ms...");
            }

            if (!ct.IsCancellationRequested)
            {
                await Response.Body.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        PluginLog.Info("[StreamTS] Client disconnected, stopping.");
        return new EmptyResult();
    }
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
