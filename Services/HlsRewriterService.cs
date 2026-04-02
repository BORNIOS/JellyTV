using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JellyTV.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JellyTV.Services;

/// <summary>
/// Fetches and rewrites HLS playlists so all segment and sub-playlist URLs
/// are routed through the local proxy, enabling seamless token renewal.
/// </summary>
public class HlsRewriterService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HlsRewriterService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsRewriterService"/> class.
    /// </summary>
    public HlsRewriterService(
        IHttpClientFactory httpClientFactory,
        ILogger<HlsRewriterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Downloads the HLS master or media playlist from the resolved stream URL,
    /// rewrites all URIs to go through the proxy, and returns the modified content.
    /// </summary>
    /// <param name="session">The active stream session.</param>
    /// <param name="proxyBaseUrl">The base URL of this proxy controller (e.g. https://jellyfin/JellyTV).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rewritten m3u8 playlist as a UTF-8 string.</returns>
    public async Task<string> FetchAndRewritePlaylistAsync(
        StreamSession session,
        string proxyBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var resolved = session.LastResolved
            ?? throw new InvalidOperationException("Session has no resolved stream.");

        var client = _httpClientFactory.CreateClient("JellyTV");
        var playlistContent = await client.GetStringAsync(resolved.FinalUrl, cancellationToken)
            .ConfigureAwait(false);

        return RewritePlaylist(playlistContent, resolved, session.SessionId, proxyBaseUrl);
    }

    /// <summary>
    /// Rewrites all URIs inside an m3u8 playlist to point to the local proxy.
    /// Handles both master playlists (EXT-X-STREAM-INF) and media playlists (.ts segments).
    /// </summary>
    private string RewritePlaylist(
        string content,
        ResolvedStream resolved,
        string sessionId,
        string proxyBaseUrl)
    {
        var sb = new StringBuilder();
        using var reader = new StringReader(content);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // Skip comments and directives — but handle URI= attributes inline
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                // Rewrite URI="..." attributes inside tags like #EXT-X-KEY, #EXT-X-MAP
                line = RewriteInlineUri(line, resolved, sessionId, proxyBaseUrl);
                sb.AppendLine(line);
                continue;
            }

            // Blank lines pass through
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine(line);
                continue;
            }

            // This is a segment or sub-playlist URI — rewrite it
            var absoluteUri = ResolveUri(line, resolved.BaseUrl);
            var proxiedUri = BuildProxiedSegmentUrl(absoluteUri, sessionId, proxyBaseUrl);

            if (Plugin.Instance?.Configuration.VerboseLogging == true)
            {
                _logger.LogDebug("[JellyTV] Rewrote segment: {Original} -> {Proxied}", line, proxiedUri);
            }

            sb.AppendLine(proxiedUri);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rewrites URI="..." attributes found inside HLS tag lines.
    /// </summary>
    private string RewriteInlineUri(string tagLine, ResolvedStream resolved, string sessionId, string proxyBaseUrl)
    {
        const string uriAttr = "URI=\"";
        var idx = tagLine.IndexOf(uriAttr, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return tagLine;
        }

        var start = idx + uriAttr.Length;
        var end = tagLine.IndexOf('"', start);
        if (end < 0)
        {
            return tagLine;
        }

        var originalUri = tagLine[start..end];
        var absoluteUri = ResolveUri(originalUri, resolved.BaseUrl);
        var proxiedUri = BuildProxiedSegmentUrl(absoluteUri, sessionId, proxyBaseUrl);

        return tagLine[..start] + proxiedUri + tagLine[end..];
    }

    /// <summary>
    /// Resolves a potentially relative URI against the stream's base URL.
    /// </summary>
    private static string ResolveUri(string uri, string baseUrl)
    {
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return new Uri(new Uri(baseUrl), uri).ToString();
    }

    /// <summary>
    /// Builds a proxy URL that routes a segment or sub-playlist through this plugin.
    /// The upstream URL is Base64-encoded to safely pass it as a query parameter.
    /// </summary>
    private static string BuildProxiedSegmentUrl(string upstreamUrl, string sessionId, string proxyBaseUrl)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(upstreamUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('='); // URL-safe Base64

        return $"{proxyBaseUrl}/segment?sid={Uri.EscapeDataString(sessionId)}&url={encoded}";
    }

    /// <summary>
    /// Decodes a URL-safe Base64-encoded upstream URL back to its original form.
    /// </summary>
    public static string DecodeSegmentUrl(string encoded)
    {
        // Re-pad and restore standard Base64 chars
        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        var pad = base64.Length % 4;
        if (pad == 2) base64 += "==";
        else if (pad == 3) base64 += "=";

        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}