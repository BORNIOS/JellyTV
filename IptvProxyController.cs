using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IptvProxy.Models;
using Jellyfin.Plugin.IptvProxy.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IptvProxy.Controllers;

/// <summary>
/// API controller that exposes proxy endpoints for IPTV streams and segments.
/// Handles playlist rewriting, session creation and token renewal.
/// </summary>
[ApiController]
[Route("IptvProxy")]
public class IptvProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IptvProxyController> _logger;

    private static SessionManagerService Sessions => Plugin.Instance!.SessionManager;
    private static HlsRewriterService Rewriter => Plugin.Instance!.HlsRewriter;

    /// <summary>
    /// Initializes a new instance of the <see cref="IptvProxyController"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating HTTP clients used to fetch upstream resources.</param>
    /// <param name="logger">Logger for controller messages.</param>
    public IptvProxyController(IHttpClientFactory httpClientFactory, ILogger<IptvProxyController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        PluginLog.Info($"IptvProxyController instantiated — Plugin.Instance is {(Plugin.Instance is null ? "NULL !!!" : "OK")}");
    }

    // ── PING — no auth, use this to verify the plugin is alive ───────────────
    // Test it: http://your-jellyfin:8096/IptvProxy/ping
    /// <summary>
    /// Healthcheck endpoint. Returns a simple alive status string.
    /// </summary>
    [HttpGet("ping")]
    [AllowAnonymous]
    public IActionResult Ping()
    {
        var pluginOk = Plugin.Instance is not null;
        var sessionOk = pluginOk && Plugin.Instance!.SessionManager is not null;
        var msg = $"IPTV Proxy is ALIVE | Plugin.Instance={pluginOk} | SessionManager={sessionOk} | Time={DateTimeOffset.Now:O}";
        PluginLog.Info($"[PING] {msg}");
        return Ok(msg);
    }

    // ── STREAM ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Proxies an upstream stream URL and rewrites HLS playlists to proxy segment URLs.
    /// </summary>
    /// <param name="url">Upstream stream URL to proxy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("stream")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStream([FromQuery] string url, CancellationToken cancellationToken)
    {
        PluginLog.Info($"[STREAM] Request received. url={url}");

        if (string.IsNullOrWhiteSpace(url))
        {
            PluginLog.Warn("[STREAM] Missing url parameter");
            return BadRequest("Parameter 'url' is required.");
        }

        try
        {
            PluginLog.Info($"[STREAM] Getting/creating session for: {url}");
            var session = await Sessions.GetOrCreateSessionAsync(url, cancellationToken).ConfigureAwait(false);
            PluginLog.Info($"[STREAM] Session ID={session.SessionId} | NeedsRenewal={Sessions.NeedsRenewal(session)}");

            if (Sessions.NeedsRenewal(session))
            {
                PluginLog.Info($"[STREAM] Renewing token for session {session.SessionId}");
                await Sessions.RenewSessionAsync(session, cancellationToken).ConfigureAwait(false);
                PluginLog.Info($"[STREAM] Token renewed. New URL={session.LastResolved?.FinalUrl}");
            }

            var resolved = session.LastResolved!;
            PluginLog.Info($"[STREAM] Resolved: FinalUrl={resolved.FinalUrl} | IsHls={resolved.IsHls} | TokenExpiry={resolved.TokenExpiry}");

            if (resolved.IsHls)
            {
                var proxyBase = GetProxyBaseUrl();
                PluginLog.Info($"[STREAM] Rewriting HLS playlist. ProxyBase={proxyBase}");
                var playlist = await Rewriter.FetchAndRewritePlaylistAsync(session, proxyBase, cancellationToken).ConfigureAwait(false);
                PluginLog.Info($"[STREAM] Playlist rewritten. Lines={playlist.Split('\n').Length}");
                return Content(playlist, "application/vnd.apple.mpegurl", Encoding.UTF8);
            }

            PluginLog.Info($"[STREAM] Non-HLS stream. Redirecting to: {resolved.FinalUrl}");
            return Redirect(resolved.FinalUrl);
        }
        catch (HttpRequestException ex)
        {
            PluginLog.Error($"[STREAM] Upstream HTTP error for {url}", ex);
            return StatusCode(502, "Upstream stream unavailable.");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[STREAM] Unexpected error for {url}", ex);
            return StatusCode(500, "Internal proxy error.");
        }
    }

    // ── SEGMENT ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Proxies an HLS segment or sub-playlist request for an existing proxy session.
    /// </summary>
    /// <param name="sid">Session identifier returned when creating the stream session.</param>
    /// <param name="url">Encoded segment URL (base64 URL-safe).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("segment")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSegment([FromQuery] string sid, [FromQuery] string url, CancellationToken cancellationToken)
    {
        string upstreamUrl;
        try
        {
            upstreamUrl = HlsRewriterService.DecodeSegmentUrl(url);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[SEGMENT] Bad URL encoding: {url} | {ex.Message}");
            return BadRequest("Invalid segment URL encoding.");
        }

        PluginLog.Info($"[SEGMENT] sid={sid} | upstream={upstreamUrl}");

        var session = Sessions.GetSessionById(sid);
        if (session is null)
        {
            PluginLog.Warn($"[SEGMENT] Unknown session ID: {sid}");
            return NotFound("Session not found. Refresh the stream.");
        }

        if (upstreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            PluginLog.Info($"[SEGMENT] Is sub-playlist, proxying: {upstreamUrl}");
            return await ProxySubPlaylistAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);
        }

        return await ProxySegmentAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> ProxySegmentAsync(StreamSession session, string upstreamUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IptvProxy");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            PluginLog.Info($"[SEGMENT] Fetching attempt {attempt}: {upstreamUrl}");
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(upstreamUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[SEGMENT] HTTP error on attempt {attempt}: {upstreamUrl}", ex);
                return StatusCode(502, "Segment fetch failed.");
            }

            PluginLog.Info($"[SEGMENT] Upstream responded: HTTP {(int)response.StatusCode} | attempt={attempt}");

            if ((response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) && attempt == 1)
            {
                response.Dispose();
                PluginLog.Warn($"[SEGMENT] Token rejected (HTTP {(int)response.StatusCode}). Renewing session {session.SessionId}...");
                try
                {
                    await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
                    PluginLog.Info($"[SEGMENT] Renewal OK. New URL={session.LastResolved?.FinalUrl}");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("[SEGMENT] Renewal failed", ex);
                    return StatusCode(502, "Token renewal failed.");
                }
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                PluginLog.Warn($"[SEGMENT] Upstream error HTTP {(int)response.StatusCode} for {upstreamUrl}");
                return StatusCode((int)response.StatusCode);
            }

            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/MP2T";
            if (response.Content.Headers.ContentLength.HasValue)
                Response.ContentLength = response.Content.Headers.ContentLength.Value;

            PluginLog.Info($"[SEGMENT] Streaming segment to client. ContentType={Response.ContentType} | Length={Response.ContentLength}");
            await (await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false)).CopyToAsync(Response.Body, ct).ConfigureAwait(false);
            response.Dispose();
            return new EmptyResult();
        }

        PluginLog.Warn($"[SEGMENT] All attempts failed for {upstreamUrl}");
        return StatusCode(502, "Could not fetch segment after renewal.");
    }

    private async Task<IActionResult> ProxySubPlaylistAsync(StreamSession session, string subUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IptvProxy");
        try
        {
            var content = await client.GetStringAsync(subUrl, ct).ConfigureAwait(false);
            var proxyBase = GetProxyBaseUrl();
            var rewritten = RewriteM3u8(content, BuildBaseUrl(subUrl), session.SessionId, proxyBase);
            PluginLog.Info($"[SUBPLAYLIST] Rewritten sub-playlist for {subUrl}");
            return Content(rewritten, "application/vnd.apple.mpegurl", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[SUBPLAYLIST] Failed for {subUrl}", ex);
            return StatusCode(502, "Sub-playlist fetch failed.");
        }
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
                var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(abs)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                line = $"{proxyBase}/segment?sid={Uri.EscapeDataString(sid)}&url={enc}";
            }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private string GetProxyBaseUrl() => $"{Request.Scheme}://{Request.Host}/IptvProxy";

    private static string BuildBaseUrl(string url)
    {
        var uri = new Uri(url);
        var last = uri.AbsolutePath.LastIndexOf('/');
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{(last >= 0 ? uri.AbsolutePath[..(last + 1)] : "/")}";
    }
}