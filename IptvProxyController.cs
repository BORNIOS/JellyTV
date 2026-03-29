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
            PluginLog.Info($"[STREAM] Resolved: FinalUrl={resolved.FinalUrl} | IsHls={resolved.IsHls} | TokenExpiry={resolved.TokenExpiry} | TokenPresent={!string.IsNullOrEmpty(resolved.TokenValue)}");

            if (resolved.IsHls)
            {
                PluginLog.Info("[STREAM] Resolved HLS playlist; confirming fresh token and expanding segments.");

                // Re-resolve the channel URL to confirm token hasn't rotated and update session
                try
                {
                    var resolver = Plugin.Instance?.RedirectResolver;
                    if (resolver is not null)
                    {
                        var fresh = await resolver.ResolveAsync(resolved.FinalUrl, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(fresh.TokenValue, resolved.TokenValue, StringComparison.Ordinal))
                        {
                            PluginLog.Info($"[STREAM] Token changed on re-resolve. Updating session token. OldPresent={!string.IsNullOrEmpty(resolved.TokenValue)} NewPresent={!string.IsNullOrEmpty(fresh.TokenValue)}");
                            // update session storage if available (use the existing session variable)
                            session = await Sessions.GetOrCreateSessionAsync(resolved.OriginalUrl, cancellationToken).ConfigureAwait(false);
                            session.LastResolved = fresh;
                            resolved = fresh;
                        }
                        else
                        {
                            PluginLog.Info("[STREAM] Token unchanged after re-resolve.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"[STREAM] Re-resolve failed: {ex.Message} — continuing with previous resolution.");
                }
                var client = _httpClientFactory.CreateClient("IptvProxy");
                var playlistContent = await client.GetStringAsync(resolved.FinalUrl, cancellationToken).ConfigureAwait(false);

                // Detect master playlist
                if (playlistContent.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    // Try to pick the first variant's URI and expand that media playlist
                    using var r = new StringReader(playlistContent);
                    string? line;
                    string? variantUri = null;
                    while ((line = r.ReadLine()) is not null)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                        variantUri = line.Trim();
                        break;
                    }

                    if (variantUri is not null)
                    {
                        var abs = variantUri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? variantUri : new Uri(new Uri(resolved.BaseUrl), variantUri).ToString();
                        try
                        {
                            var sub = await _httpClientFactory.CreateClient("IptvProxy").GetStringAsync(abs, cancellationToken).ConfigureAwait(false);
                            playlistContent = sub; // process as media playlist below
                        }
                        catch (Exception ex)
                        {
                            PluginLog.Warn($"[STREAM] Failed to fetch variant playlist {abs}: {ex.Message}");
                        }
                    }
                }

                // Process media playlist: for each non-comment line, point to our /segment endpoint
                // so the client requests each fragment through us and we can resolve a fresh token per fragment.
                var sb = new StringBuilder();
                using var reader = new StringReader(playlistContent);
                string? ln;
                var proxyBase = GetProxyBaseUrl();
                while ((ln = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(ln) || ln.StartsWith('#'))
                    {
                        sb.AppendLine(ln);
                        continue;
                    }

                    var abs = ln.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? ln : new Uri(new Uri(resolved.BaseUrl), ln).ToString();

                    // Encode the absolute segment URL and point the playlist to our segment endpoint.
                    var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(abs)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                    var proxySeg = $"{proxyBase}/segment?sid={Uri.EscapeDataString(session.SessionId)}&url={enc}";
                    sb.AppendLine(proxySeg);
                }

                var expanded = sb.ToString();
                PluginLog.Info($"[STREAM] Expanded playlist lines={expanded.Split('\n').Length}");
                return Content(expanded, "application/vnd.apple.mpegurl", Encoding.UTF8);
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

        // Try to use a cached resolution first
        var fetchUrl = upstreamUrl;
        var now = DateTimeOffset.UtcNow;
        if (session.SegmentResolveCache.TryGetValue(upstreamUrl, out var cached) && cached.Expiry > now)
        {
            fetchUrl = cached.FinalUrl;
            PluginLog.Info($"[SEGMENT] Using cached resolved URL for segment: {fetchUrl}");
        }
        else
        {
            // Try to resolve the segment URL to its final location (follow redirects
            // to obtain tokenized URLs issued by the provider). If resolution fails
            // with 401/403, fall back to using the provided upstreamUrl directly.
            try
            {
                var resolver = Plugin.Instance?.RedirectResolver;
                if (resolver is not null)
                {
                    var resolved = await resolver.ResolveAsync(upstreamUrl, ct).ConfigureAwait(false);
                    fetchUrl = resolved.FinalUrl ?? upstreamUrl;
                    // cache short-lived (e.g., 30s)
                    session.SegmentResolveCache[upstreamUrl] = (fetchUrl, DateTimeOffset.UtcNow.AddSeconds(30));
                    // update preferred host for the session
                    try
                    {
                        session.PreferredHost = new Uri(fetchUrl).Host;
                    }
                    catch { }
                    PluginLog.Info($"[SEGMENT] Resolved segment URL to final: {fetchUrl}");
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden || ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                PluginLog.Warn($"[SEGMENT] Resolver returned {ex.StatusCode} for {upstreamUrl}; using original URL.");
                fetchUrl = upstreamUrl;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"[SEGMENT] Resolver failed for {upstreamUrl}: {ex.Message}; using original URL.");
                fetchUrl = upstreamUrl;
            }
        }
        try
        {
            var resolver = Plugin.Instance?.RedirectResolver;
            if (resolver is not null)
            {
                var resolved = await resolver.ResolveAsync(upstreamUrl, ct).ConfigureAwait(false);
                fetchUrl = resolved.FinalUrl ?? upstreamUrl;
                PluginLog.Info($"[SEGMENT] Resolved segment URL to final: {fetchUrl}");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden || ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            PluginLog.Warn($"[SEGMENT] Resolver returned {ex.StatusCode} for {upstreamUrl}; using original URL.");
            fetchUrl = upstreamUrl;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[SEGMENT] Resolver failed for {upstreamUrl}: {ex.Message}; using original URL.");
            fetchUrl = upstreamUrl;
        }

        var config = Plugin.Instance?.Configuration as Jellyfin.Plugin.IptvProxy.Configuration.PluginConfiguration;
        var userAgent = config?.UserAgent ?? "Mozilla/5.0 (compatible; JellyfinIptvProxy/1.0)";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            PluginLog.Info($"[SEGMENT] Fetching attempt {attempt}: {fetchUrl}");
            HttpResponseMessage response;
            try
            {
                // Build outgoing request message so we can control headers (User-Agent, Referer, Range)
                using var req = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                // Set Referer to the playlist base if available to mimic a legitimate player
                if (!string.IsNullOrEmpty(session.LastResolved?.FinalUrl))
                {
                    try
                    {
                        var refUri = new Uri(session.LastResolved.FinalUrl);
                        req.Headers.Referrer = new Uri(refUri.GetLeftPart(UriPartial.Authority));
                    }
                    catch { }
                }
                // Forward Range header if client requested partial content
                if (Request.Headers.TryGetValue("Range", out var range))
                {
                    req.Headers.TryAddWithoutValidation("Range", (string)range);
                }

                response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
                    // After renewal, try to re-resolve the specific segment to its new final URL
                    try
                    {
                        var resolver = Plugin.Instance?.RedirectResolver;
                        if (resolver is not null)
                        {
                            var re = await resolver.ResolveAsync(upstreamUrl, ct).ConfigureAwait(false);
                            fetchUrl = re.FinalUrl ?? fetchUrl;
                            PluginLog.Info($"[SEGMENT] Re-resolved segment URL after renewal: {fetchUrl}");
                        }
                    }
                    catch (Exception rex)
                    {
                        PluginLog.Warn($"[SEGMENT] Re-resolve failed after renewal: {rex.Message}");
                    }
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