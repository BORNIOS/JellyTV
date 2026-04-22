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

    private static Plugin P => Plugin.Instance!;
    private static SessionManagerService Sessions   => P.SessionManager;
    private static M3uAggregatorService  Aggregator => P.M3uAggregator;
    private static BroadcastService      Broadcast  => P.Broadcast;

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

            var resolved = session.LastResolved!;
            PluginLog.Info($"[STREAM] session={session.SessionId} IsHls={resolved.IsHls} FinalUrl={resolved.FinalUrl}");

            if (resolved.IsHls)
            {
                // Always use the broadcast pump for HLS — even no-token streams.
                // The pump handles beacon URL extraction, live-edge clamping for DVR
                // playlists, and fans out to multiple concurrent Jellyfin clients.
                // Bypassing the pump would send raw amagi/beacon URLs to ffmpeg directly.
                var pump = Broadcast.GetOrCreate(session);
                pump.EnsureRunning();

                await pump.WaitForFirstSegmentAsync(TimeSpan.FromSeconds(8), cancellationToken)
                    .ConfigureAwait(false);

                var broadcastUrl = $"{GetProxyBaseUrl()}/broadcast?sid={Uri.EscapeDataString(session.SessionId)}";
                PluginLog.Info($"[STREAM] Redirecting to broadcast sid={session.SessionId}");
                return Redirect(broadcastUrl);
            }

            return Redirect(resolved.FinalUrl);
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

    // ── BROADCAST ────────────────────────────────────────────────────────────
    // Serves a continuous raw MPEG-TS stream to ffprobe/ffmpeg.
    // The pump downloads HLS segments in the background and fans them out here.
    [HttpGet("broadcast")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBroadcast([FromQuery] string? sid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sid))
            return BadRequest("Missing sid parameter.");

        var session = Sessions.GetSessionById(sid);
        if (session is null)
        {
            PluginLog.Warn($"[BROADCAST] Unknown session sid={sid}");
            return NotFound("Session expired. Reload the channel.");
        }

        var pump = Broadcast.GetOrCreate(session);

        // Refuse the connection when the pump already has the maximum number of
        // subscribers. This stops Jellyfin's retry loop from accumulating hundreds of
        // zombie HTTP connections (and their associated ffmpeg processes) when the pump
        // stalls during token renewal.
        if (pump.IsAtCapacity)
        {
            PluginLog.Warn($"[BROADCAST] Subscriber cap reached, returning 503 sid={sid}");
            return StatusCode(503, "Stream is at capacity. Retry shortly.");
        }

        // Headers must be set before writing to Response.Body.
        Response.ContentType                   = "video/MP2T";
        Response.Headers["Cache-Control"]      = "no-cache, no-store";
        Response.Headers["X-Accel-Buffering"]  = "no";   // prevent nginx / reverse-proxy buffering
        Response.Headers["Connection"]         = "keep-alive";

        PluginLog.Info($"[BROADCAST] Client connected sid={sid}");

        try
        {
            await pump.SubscribeAndStreamAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal: client closed the connection.
            PluginLog.Info($"[BROADCAST] Client disconnected sid={sid}");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[BROADCAST] Stream error sid={sid}", ex);
        }

        return new EmptyResult();
    }

    // ── SEGMENT ──────────────────────────────────────────────────────────────
    // Proxy de segmentos .ts y sub-playlists .m3u8.
    [HttpGet("segment")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSegment([FromQuery] string sid, [FromQuery] string url, CancellationToken cancellationToken)
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

    // ── Helpers ───────────────────────────────────────────────────────────────

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
                var rewritten = RewriteM3u8(content, BuildBaseUrl(fetchUrl), session.SessionId, GetProxyBaseUrl());
                return Content(rewritten, HlsMimeType, Encoding.UTF8);
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

    private static string RewriteM3u8(string content, string baseUrl, string sid, string proxyBase)
    {
        var sb = new StringBuilder();
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            {
                var abs = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? line : new Uri(new Uri(baseUrl), line).ToString();
                var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(abs))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('=');
                line = $"{proxyBase}/segment?sid={Uri.EscapeDataString(sid)}&url={enc}";
            }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private async Task<(HttpResponseMessage? Response, IActionResult? ErrorResult)> FetchSegmentResponseAsync(HttpClient client, string fetchUrl, int attempt, CancellationToken ct)
    {
        try
        {
            var response = await client.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (response, null);
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

            if (TryFindFirstSubPlaylistUrl(topPlaylist, topBase, out var subPlaylistUrl))
            {
                var mediaPlaylist = await client.GetStringAsync(subPlaylistUrl, ct).ConfigureAwait(false);
                var mediaBase = BuildBaseUrl(subPlaylistUrl);

                if (TryFindSegmentUrlByFileName(mediaPlaylist, mediaBase, targetFileName, out var nestedMatch))
                {
                    return nestedMatch;
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