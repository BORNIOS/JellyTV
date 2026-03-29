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
/// HTTP controller exposing /IptvProxy/stream and /IptvProxy/segment.
/// Services are resolved from Plugin.Instance to avoid IPluginServiceRegistrator dependency.
/// </summary>
[ApiController]
[Route("IptvProxy")]
[Authorize(Policy = "DefaultAuthorization")]
public class IptvProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IptvProxyController> _logger;

    private static SessionManagerService Sessions => Plugin.Instance!.SessionManager;
    private static HlsRewriterService Rewriter => Plugin.Instance!.HlsRewriter;

    public IptvProxyController(
        IHttpClientFactory httpClientFactory,
        ILogger<IptvProxyController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the IPTV URL, creates a session, returns a rewritten HLS playlist.
    /// </summary>
    [HttpGet("stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetStream([FromQuery] string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest("Parameter 'url' is required.");

        _logger.LogInformation("[IptvProxy] Stream request for: {Url}", url);

        try
        {
            var session = await Sessions.GetOrCreateSessionAsync(url, cancellationToken).ConfigureAwait(false);

            if (Sessions.NeedsRenewal(session))
                await Sessions.RenewSessionAsync(session, cancellationToken).ConfigureAwait(false);

            var resolved = session.LastResolved!;

            if (resolved.IsHls)
            {
                var playlist = await Rewriter.FetchAndRewritePlaylistAsync(session, GetProxyBaseUrl(), cancellationToken).ConfigureAwait(false);
                return Content(playlist, "application/vnd.apple.mpegurl", Encoding.UTF8);
            }

            return Redirect(resolved.FinalUrl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[IptvProxy] Upstream failed for {Url}", url);
            return StatusCode(StatusCodes.Status502BadGateway, "Upstream stream unavailable.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IptvProxy] Error for {Url}", url);
            return StatusCode(StatusCodes.Status500InternalServerError, "Internal proxy error.");
        }
    }

    /// <summary>
    /// Proxies a single HLS segment or sub-playlist. Retries once after token renewal on 401/403.
    /// </summary>
    [HttpGet("segment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetSegment([FromQuery] string sid, [FromQuery] string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(url))
            return BadRequest("Parameters 'sid' and 'url' are required.");

        string upstreamUrl;
        try { upstreamUrl = HlsRewriterService.DecodeSegmentUrl(url); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IptvProxy] Bad segment URL encoding: {E}", url);
            return BadRequest("Invalid segment URL encoding.");
        }

        var session = Sessions.GetSessionById(sid);
        if (session is null)
        {
            _logger.LogWarning("[IptvProxy] Unknown session: {Sid}", sid);
            return NotFound("Session not found. Refresh the stream.");
        }

        if (upstreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            return await ProxySubPlaylistAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);

        return await ProxySegmentAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<IActionResult> ProxySegmentAsync(StreamSession session, string upstreamUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IptvProxy");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            HttpResponseMessage response;
            try { response = await client.GetAsync(upstreamUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IptvProxy] Segment fetch error {Url}", upstreamUrl);
                return StatusCode(502, "Segment fetch failed.");
            }

            if ((response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) && attempt == 1)
            {
                response.Dispose();
                _logger.LogWarning("[IptvProxy] Token rejected; renewing session {Id}", session.SessionId);
                try { await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[IptvProxy] Renewal failed");
                    return StatusCode(502, "Token renewal failed.");
                }
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[IptvProxy] Upstream {Status} for {Url}", (int)response.StatusCode, upstreamUrl);
                return StatusCode((int)response.StatusCode);
            }

            Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/MP2T";
            if (response.Content.Headers.ContentLength.HasValue)
                Response.ContentLength = response.Content.Headers.ContentLength.Value;

            await (await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false)).CopyToAsync(Response.Body, ct).ConfigureAwait(false);
            response.Dispose();
            return new EmptyResult();
        }

        return StatusCode(502, "Could not fetch segment after renewal.");
    }

    private async Task<IActionResult> ProxySubPlaylistAsync(StreamSession session, string subUrl, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("IptvProxy");
        try
        {
            var content = await client.GetStringAsync(subUrl, ct).ConfigureAwait(false);
            var rewritten = RewriteM3u8(content, BuildBaseUrl(subUrl), session.SessionId, GetProxyBaseUrl());
            return Content(rewritten, "application/vnd.apple.mpegurl", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IptvProxy] Sub-playlist error {Url}", subUrl);
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
                var abs = line.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? line : new Uri(new Uri(baseUrl), line).ToString();
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