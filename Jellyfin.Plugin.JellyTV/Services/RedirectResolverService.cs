using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Resolves IPTV URLs by following HTTP redirects to obtain the final playback URL.
/// </summary>
public class RedirectResolverService
{
    // Hardcoded constants (not configurable to avoid interference with token renewal logic)
    private const int MAXREDIRECTS = 20;

    private static readonly Regex QuotedPlaylistRegex = new(
        @"[""'](?<u>[^""'\s<>]*\.m3u8?(?![A-Za-z0-9])[^""'\s<>]*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BarePlaylistRegex = new(
        @"https?://[^""'\s<>]+?\.m3u8?(?![A-Za-z0-9])(?:\?[^""'\s<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TokenParamRegex = new(
        @"[?&](?:token|access_token|auth|t|key|sig|tokenid|tkn|hdnea|expires)=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RedirectResolverService> _logger;
    private readonly ProxyPoolService _proxyPool;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedirectResolverService"/> class.
    /// </summary>
    public RedirectResolverService(
        IHttpClientFactory httpClientFactory,
        ILogger<RedirectResolverService> logger,
        ProxyPoolService proxyPool)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _proxyPool = proxyPool;
    }

    /// <summary>
    /// Resolves the given URL by following all HTTP redirects until the final URL is reached.
    /// </summary>
    /// <param name="originalUrl">The initial IPTV URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ResolvedStream"/> containing the final URL and metadata.</returns>
    public async Task<ResolvedStream> ResolveAsync(
        string originalUrl,
        CancellationToken cancellationToken = default)
    {
        var maxRedirects = MAXREDIRECTS;

        // Select one proxy for this entire resolve call (both auto and manual paths share it).
        // proxyKey is non-null only when a healthy proxy was selected.
        var (proxyKey, proxyNoRedirect, proxyAutoRedirect) = _proxyPool.GetNextProxy();

        // Strategy similar to robust player clients:
        // 1) Try automatic redirect handling first (browser-like behavior + shared cookies).
        // 2) Fallback to manual redirect walk for full diagnostics/control.
        // The manual walk is required here because automatic redirects discard
        // the intermediate hop that emitted a short-lived token.

        // Use a client that does NOT auto-follow redirects so we can track them.
        // directClient is always the factory fallback; proxyClient may be null.
        var directClient = _httpClientFactory.CreateClient("IptvNoRedirect");
        var client = proxyNoRedirect ?? directClient;
        // Set to true after the first proxy failure so remaining hops skip the proxy timeout.
        bool proxyMarkedFailed = proxyKey is null;

        var currentUrl = originalUrl;
        string? referer = null;
        var redirectCount = 0;
        var redirectChain = new List<string> { originalUrl };
        string contentType = string.Empty;
        string? initialPlaylistContent = null;
        string? rotationNodeUrl = null;

        if (Plugin.Instance?.Configuration.VerboseLogging == true && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[JellyTV] Starting resolution for: {Url}", PluginLog.Redact(originalUrl));
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[JellyTV] Starting resolution for: {Url}", PluginLog.Redact(originalUrl));
        }

        while (redirectCount <= maxRedirects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage response;

            try
            {
                PluginLog.Info($"[RESOLVE] GET {currentUrl} (attempt {redirectCount + 1})");
                var (resp, usedFallback) = await SendWithFallbackAsync(client, directClient, currentUrl, referer, cancellationToken).ConfigureAwait(false);
                response = resp;
                PluginLog.Info($"[RESOLVE] Response {currentUrl} -> {(int)response.StatusCode}");

                // On the first proxy failure: mark the proxy unhealthy and switch all
                // remaining hops to the direct client (avoids one 3-second timeout per hop).
                if (usedFallback && !proxyMarkedFailed && proxyKey is not null)
                {
                    proxyMarkedFailed = true;
                    _proxyPool.MarkFailed(proxyKey);
                    client = directClient;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JellyTV] HTTP error fetching {Url}", PluginLog.Redact(currentUrl));
                PluginLog.Error($"[RESOLVE] HTTP error fetching {currentUrl}", ex);
                throw;
            }

            contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // Algunos gateways exigen Referer de su propio sitio (403 sin él), incluso cuando la
            // fuente configurada es el reproductor embebido. Se reintenta una vez con el origen.
            if ((int)response.StatusCode is 401 or 403 && string.IsNullOrWhiteSpace(referer))
            {
                var origin = new Uri(currentUrl).GetLeftPart(UriPartial.Authority) + "/";
                response.Dispose();
                var (retryResponse, retryFallback) = await SendWithFallbackAsync(client, directClient, currentUrl, origin, cancellationToken).ConfigureAwait(false);
                response = retryResponse;
                if (retryFallback && !proxyMarkedFailed && proxyKey is not null)
                {
                    proxyMarkedFailed = true;
                    _proxyPool.MarkFailed(proxyKey);
                    client = directClient;
                }

                PluginLog.Info($"[RESOLVE] Retried {currentUrl} with Referer {origin} -> {(int)response.StatusCode}");
                contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            }

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    _logger.LogWarning("[JellyTV] Redirect with no Location header at {Url}", PluginLog.Redact(currentUrl));
                    PluginLog.Warn($"[RESOLVE] Redirect with no Location header at {currentUrl}");
                    break;
                }

                // Resolve relative redirects
                var nextUrl = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(currentUrl), location).ToString();

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[JellyTV] Redirect {Count}: {From} -> {To}", redirectCount + 1, PluginLog.Redact(currentUrl), PluginLog.Redact(nextUrl));
                }

                PluginLog.Info($"[RESOLVE] Redirect {redirectCount + 1}: {currentUrl} -> {nextUrl}");
                // Track the rotation node: the hop that outputs a tokenized redirect destination.
                if (IsRotationSignal(nextUrl))
                {
                    rotationNodeUrl = currentUrl;
                }

                // Record redirect hop
                redirectChain.Add(nextUrl);
                referer = currentUrl;
                currentUrl = nextUrl;
                redirectCount++;
                response.Dispose();
                continue;
            }

            if (response.IsSuccessStatusCode && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var html = await ReadLimitedBodyAsync(response, cancellationToken).ConfigureAwait(false);
                var metaUrl = TryExtractMetaRefreshUrl(html, currentUrl);
                if (metaUrl is not null)
                {
                    if (IsRotationSignal(metaUrl))
                    {
                        rotationNodeUrl = currentUrl;
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("[JellyTV] Meta refresh {Count}: {From} -> {To}", redirectCount + 1, PluginLog.Redact(currentUrl), PluginLog.Redact(metaUrl));
                    }

                    redirectChain.Add(metaUrl);
                    referer = currentUrl;
                    currentUrl = metaUrl;
                    redirectCount++;
                    response.Dispose();
                    continue;
                }

                var embeddedUrl = TryExtractEmbeddedPlaybackUrl(html, currentUrl);
                if (embeddedUrl is not null)
                {
                    // Gateway HTML: la pagina embebe el m3u8 tokenizado en
                    // JS (playbackURL/source/...). Es el salto final con token: NO hacemos
                    // GET aqui para no consumirlo; HlsRewriterService hara el fetch del playlist.
                    if (IsRotationSignal(embeddedUrl))
                    {
                        rotationNodeUrl = currentUrl;
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("[JellyTV] Embedded HLS {Count}: {From} -> {To}", redirectCount + 1, PluginLog.Redact(currentUrl), PluginLog.Redact(embeddedUrl));
                    }

                    PluginLog.Info($"[RESOLVE] Embedded HLS in HTML: {currentUrl} -> {embeddedUrl}");
                    redirectChain.Add(embeddedUrl);
                    currentUrl = embeddedUrl;
                    contentType = "application/vnd.apple.mpegurl";
                    response.Dispose();
                    break;
                }

                if (IsHlsUrl(currentUrl, contentType))
                {
                    initialPlaylistContent = html;
                }

                // Gateway → iframe player: la página "online.php?stream=..." no trae el m3u8,
                // lo entrega el reproductor embebido. Se sigue el iframe (nunca beacons) y en
                // esa página aparece el m3u8 con token.
                var iframeUrl = TryExtractIframeUrl(html, currentUrl);
                if (iframeUrl is not null && !string.Equals(iframeUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("[JellyTV] Iframe player {Count}: {From} -> {To}", redirectCount + 1, PluginLog.Redact(currentUrl), PluginLog.Redact(iframeUrl));
                    }

                    PluginLog.Info($"[RESOLVE] Iframe player {redirectCount + 1}: {currentUrl} -> {iframeUrl}");
                    redirectChain.Add(iframeUrl);
                    referer = currentUrl;
                    currentUrl = iframeUrl;
                    redirectCount++;
                    response.Dispose();
                    continue;
                }

                response.Dispose();
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Some providers return 401/403 for specific proxy egress IPs on intermediate hops
                // (e.g., an intermediate API/redirect hop) even when the direct host path is valid. Retry this same hop once
                // without proxy before failing the whole chain.
                if (((int)response.StatusCode == 401 || (int)response.StatusCode == 403) && client != directClient)
                {
                    PluginLog.Warn($"[RESOLVE] HTTP {(int)response.StatusCode} at {currentUrl} via proxy; retrying direct");
                    if (!proxyMarkedFailed && proxyKey is not null)
                    {
                        proxyMarkedFailed = true;
                        _proxyPool.MarkFailed(proxyKey);
                    }

                    response.Dispose();
                    client = directClient;
                    continue;
                }

                _logger.LogWarning("[JellyTV] Non-success HTTP {Status} while resolving {Url}", (int)response.StatusCode, PluginLog.Redact(currentUrl));
                PluginLog.Warn($"[RESOLVE] Non-success HTTP {(int)response.StatusCode} while resolving {currentUrl}");
                // Throw an HttpRequestException that preserves the status code for callers to handle
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Response status code does not indicate success: {(int)status} ({status}).", null, status);
            }

            // don't read the body for tokenized streams — it consumes
            // the single-use token that HlsRewriterService needs for the first fetch.
            string? resolverBodyText = null;
            var ct2 = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var isTokenized = !string.IsNullOrEmpty(TryExtractTokenFromUrl(currentUrl));
            if (!isTokenized && IsHlsUrl(currentUrl, ct2))
            {
                try
                {
                    resolverBodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                { /* non-fatal */
                }
            }

            response.Dispose();
            initialPlaylistContent = resolverBodyText;
            break;
        }

        if (redirectCount > maxRedirects)
        {
            throw new InvalidOperationException($"[JellyTV] Exceeded maximum redirect count ({maxRedirects}) for URL: {originalUrl}");
        }

        var baseUrl = BuildBaseUrl(currentUrl);
        var isHls = IsHlsUrl(currentUrl, contentType);
        var tokenExpiry = TryParseTokenExpiry(currentUrl);
        var tokenValue = TryExtractTokenFromUrl(currentUrl);

        // For tokenized streams, use the rotation node as the poll URL.
        // Each poll follows the redirect chain and obtains a fresh token.
        // This avoids: (a) HTTPS→HTTP block from Chain[0] (jellytv HTTPS origin),
        //              (b) re-using a stale ?token= URL that expired between polls.
        // Falls back to originalUrl if no rotation node was detected (non-standard chain).
        string playlistPollUrl;
        if (!string.IsNullOrEmpty(tokenValue))
        {
            playlistPollUrl = rotationNodeUrl ?? originalUrl;
        }
        else
        {
            playlistPollUrl = currentUrl;
        }

        var resolved = new ResolvedStream
        {
            OriginalUrl = originalUrl,
            FinalUrl = currentUrl,
            PlaylistPollUrl = playlistPollUrl,
            TokenGeneratorHopUrl = rotationNodeUrl,
            BaseUrl = baseUrl,
            ContentType = contentType,
            IsHls = isHls,
            TokenExpiry = tokenExpiry,
            TokenValue = tokenValue,
            ResolvedAt = DateTimeOffset.UtcNow,
            InitialPlaylistContent = initialPlaylistContent
        };

        // Attach the redirect chain for diagnostics/history
        resolved.RedirectChain = redirectChain;
        resolved.RotationNodeUrl = rotationNodeUrl;
        resolved.ResolverProxyKey = proxyKey;

        if (Plugin.Instance?.Configuration.VerboseLogging == true && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[JellyTV] Resolved to: {FinalUrl} | HLS={IsHls} | TokenExpiry={Expiry} | TokenPresent={TokenPresent}",
                PluginLog.Redact(currentUrl),
                isHls,
                tokenExpiry?.ToString("o") ?? "unknown",
                !string.IsNullOrEmpty(tokenValue));
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "[JellyTV] Resolved to: {FinalUrl} | HLS={IsHls} | TokenExpiry={Expiry} | TokenPresent={TokenPresent}",
                PluginLog.Redact(currentUrl),
                isHls,
                tokenExpiry?.ToString("o") ?? "unknown",
                !string.IsNullOrEmpty(tokenValue));
        }

        // Write redirect chain to plugin log for diagnostics
        try
        {
            PluginLog.Info($"[RESOLVE] Final: {currentUrl} | HLS={isHls} | TokenPresent={!string.IsNullOrEmpty(tokenValue)} | ChainCount={redirectChain.Count}");
            for (var i = 0; i < redirectChain.Count; i++)
            {
                PluginLog.Info($"[RESOLVE] Chain[{i}] = {redirectChain[i]}");
            }
        }
        catch
        {
        }

        return resolved;
    }

    internal HttpClient CreateNoRedirectClient() => _httpClientFactory.CreateClient("IptvNoRedirect");

    internal static string? TryExtractMetaRefreshUrl(string html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > 64 * 1024)
        {
            return null;
        }

        var match = Regex.Match(
            html,
            "<meta\\b[^>]*http-equiv\\s*=\\s*[\\\"']?refresh[\\\"']?[^>]*content\\s*=\\s*[\\\"']?[^>]*?url\\s*=\\s*[\\\"']?([^\\\"' >]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                html,
                "<meta\\b[^>]*content\\s*=\\s*[\\\"']?[^>]*?url\\s*=\\s*[\\\"']?([^\\\"' >]+)[^>]*http-equiv\\s*=\\s*[\\\"']?refresh",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (!match.Success || !Uri.TryCreate(new Uri(baseUrl), match.Groups[1].Value.Trim(), out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    /// <summary>
    /// Extracts the HLS playlist embedded in an HTML/JS gateway page, whatever the player or variable
    /// holding it (<c>"src":"…"</c>, <c>playbackURL = '…'</c>, JSON-escaped <c>https:\/\/</c>, entities). Any
    /// <c>.m3u8</c>/<c>.m3u</c> URL is a candidate; the one carrying a token is the official stream, so it
    /// wins over plain ones (ads, trailers). Without a token, the first candidate is used.
    /// </summary>
    /// <param name="html">Gateway HTML.</param>
    /// <param name="baseUrl">URL of the page, used to resolve relative sources.</param>
    /// <returns>The absolute playlist URL, or <c>null</c> when the page has none.</returns>
    internal static string? TryExtractEmbeddedPlaybackUrl(string html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > 64 * 1024)
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(html.Replace("\\/", "/", StringComparison.Ordinal).Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase));
        string? first = null;
        foreach (var raw in QuotedPlaylistRegex.Matches(text).Select(m => m.Groups["u"].Value)
                     .Concat(BarePlaylistRegex.Matches(text).Select(m => m.Value)))
        {
            var url = ResolveHtmlUrl(baseUrl, raw);
            if (url is null)
            {
                continue;
            }

            if (TokenParamRegex.IsMatch(url) || url.Contains("/hlsr/", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            first ??= url;
        }

        return first;
    }

    /// <summary>
    /// Extracts the player iframe of a gateway page, e.g. <c>&lt;iframe src="/5.php?stream=..."&gt;</c>.
    /// Tracking/beacon shells are rejected, and iframes whose URL looks like a player are preferred.
    /// </summary>
    /// <param name="html">Gateway HTML.</param>
    /// <param name="baseUrl">URL of the gateway page, used to resolve relative sources.</param>
    /// <returns>The absolute iframe URL, or <c>null</c> when none is usable.</returns>
    internal static string? TryExtractIframeUrl(string html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > 64 * 1024)
        {
            return null;
        }

        var matches = Regex.Matches(
            html,
            "<iframe\\b[^>]*\\bsrc\\s*=\\s*[\"'](?<u>[^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        string? fallback = null;
        foreach (Match match in matches)
        {
            var url = ResolveHtmlUrl(baseUrl, WebUtility.HtmlDecode(match.Groups["u"].Value));
            if (url is null || IsBeaconOrAdUrl(url))
            {
                continue;
            }

            if (url.Contains("stream=", StringComparison.OrdinalIgnoreCase)
                || url.Contains("player", StringComparison.OrdinalIgnoreCase)
                || url.Contains("embed", StringComparison.OrdinalIgnoreCase)
                || url.Contains("online", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            fallback ??= url;
        }

        return fallback;
    }

    /// <summary>
    /// Rejects tracking pixels, ad shells and pop-under links so the resolver never follows them.
    /// </summary>
    /// <param name="url">Candidate URL.</param>
    /// <returns><c>true</c> when the URL must not be followed.</returns>
    private static bool IsBeaconOrAdUrl(string url)
    {
        var value = url.ToLowerInvariant();
        return value.Contains("block.html", StringComparison.Ordinal)
            || value.Contains("trkr.", StringComparison.Ordinal)
            || value.Contains("/ads/", StringComparison.Ordinal)
            || value.Contains("adservice", StringComparison.Ordinal)
            || value.Contains("doubleclick", StringComparison.Ordinal)
            || value.Contains("googlesyndication", StringComparison.Ordinal)
            || value.Contains("popup", StringComparison.Ordinal)
            || value.Contains("banner", StringComparison.Ordinal);
    }

    private static string? ResolveHtmlUrl(string baseUrl, string raw)
    {
        var value = raw.Trim().TrimEnd(';', ',');
        if (value.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(new Uri(baseUrl), value, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri.ToString();
        }

        return null;
    }

    private static async Task<string> ReadLimitedBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 64 * 1024)
        {
            return string.Empty;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Length <= 64 * 1024 ? body : body[..(64 * 1024)];
    }

    private async Task<ResolvedStream?> TryResolveWithAutoRedirectAsync(
        string originalUrl,
        HttpClient? proxyClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = proxyClient ?? _httpClientFactory.CreateClient("JellyTV");
            using var request = new HttpRequestMessage(HttpMethod.Get, originalUrl);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A 302 at the top of an HTTPS→HTTP chain is expected for providers behind a gateway like
                // gateway.example (the auto-redirect client cannot downgrade schemes).
                // Log as INFO so it does not pollute the warning stream on normal operation.
                var sc = (int)response.StatusCode;
                if (sc is >= 300 and < 400)
                {
                    PluginLog.Info($"[RESOLVE-AUTO] Redirect {sc} at {originalUrl}; using manual resolver for cross-scheme chain");
                }
                else
                {
                    PluginLog.Warn($"[RESOLVE-AUTO] Non-success HTTP {sc} at {originalUrl}; falling back to manual resolver");
                }

                return null;
            }

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // don't read the response body. For tokenized streams the
            // token is single-use and ReadAsStringAsync consumes it. The playlist
            // is fetched separately by HlsRewriterService after resolution.
            // The old BroadcastPump (dead code) was the only consumer of this.

            var redirectChain = new List<string> { originalUrl };
            if (!string.Equals(finalUrl, originalUrl, StringComparison.Ordinal))
            {
                redirectChain.Add(finalUrl);
            }

            var resolved = new ResolvedStream
            {
                OriginalUrl = originalUrl,
                FinalUrl = finalUrl,
                PlaylistPollUrl = !string.IsNullOrEmpty(TryExtractTokenFromUrl(finalUrl)) ? originalUrl : finalUrl,
                BaseUrl = BuildBaseUrl(finalUrl),
                ContentType = contentType,
                IsHls = IsHlsUrl(finalUrl, contentType),
                TokenExpiry = TryParseTokenExpiry(finalUrl),
                TokenValue = TryExtractTokenFromUrl(finalUrl),
                ResolvedAt = DateTimeOffset.UtcNow,
                RedirectChain = redirectChain,
                InitialPlaylistContent = null,
                ResolverProxyKey = null,
            };

            PluginLog.Info($"[RESOLVE-AUTO] Final: {finalUrl} | HLS={resolved.IsHls} | TokenPresent={!string.IsNullOrEmpty(resolved.TokenValue)}");
            return resolved;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[RESOLVE-AUTO] Failed for {originalUrl}: {ex.Message}; falling back to manual resolver");
            return null;
        }
    }

    /// <summary>
    /// Sends a GET using <paramref name="preferred"/>; on network failure switches to
    /// <paramref name="fallback"/> and retries the same URL so a broken proxy never
    /// permanently blocks the resolve chain.
    /// Returns the response and a flag indicating whether the fallback was used.
    /// </summary>
    private static async Task<(HttpResponseMessage Response, bool UsedFallback)> SendWithFallbackAsync(
        HttpClient preferred, HttpClient fallback, string url, string? referer, CancellationToken ct)
    {
        using var req = BuildRequest(url, referer);
        try
        {
            var resp = await preferred.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (resp, false);
        }
        catch (HttpRequestException ex) when (preferred != fallback)
        {
            PluginLog.Warn($"[RESOLVE] Proxy failed for {url}: {ex.Message} — retrying direct");
            using var retryReq = BuildRequest(url, referer);
            var resp = await fallback.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (resp, true);
        }
    }

    private static HttpRequestMessage BuildRequest(string url, string? referer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            request.Headers.Referrer = refererUri;
        }

        return request;
    }

    private string? TryExtractTokenFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);

            // Common token parameter names used by IPTV providers
            foreach (var key in new[] { "token", "access_token", "auth", "t", "key", "sig", "tokenid" })
            {
                var v = qs[key];
                if (!string.IsNullOrEmpty(v))
                {
                    return v;
                }
            }

            var hlsr = Regex.Match(uri.AbsolutePath, @"/hlsr/([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (hlsr.Success)
            {
                return hlsr.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[JellyTV] Could not extract token from URL");
        }

        return null;
    }

    /// <summary>
    /// Builds the base URL for resolving relative HLS segment URIs.
    /// </summary>
    private static string BuildBaseUrl(string url)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        var basePath = lastSlash >= 0 ? path[..(lastSlash + 1)] : "/";
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}{basePath}";
    }

    /// <summary>
    /// Determines whether the URL or content type indicates an HLS stream.
    /// </summary>
    private static bool IsHlsUrl(string url, string contentType)
    {
        return url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("x-mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <c>true</c> when a redirect destination URL shows signs of a token-rotation
    /// handoff — i.e. the upstream node is generating or embedding a short-lived token.
    /// Used to identify the <c>RotationNodeUrl</c> (the hop that <em>outputs</em> the redirect).
    /// </summary>
    private static bool IsRotationSignal(string url)
    {
        if (url.Contains("?token=", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (url.Contains("/hlsr/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var q = new Uri(url).Query;
            return q.Length > 80;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to extract a token expiry time from common query parameter patterns or from the
    /// epoch pair some providers embed in the token itself
    /// (e.g. <c>hash-28-1792327724-1792309724</c>).
    /// </summary>
    internal static DateTimeOffset? TryParseTokenExpiry(string url)
    {
        try
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

            // Common IPTV expiry parameters
            foreach (var key in new[] { "expires", "exp", "expiry", "end", "validuntil", "token_expiry" })
            {
                var value = query[key];
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (long.TryParse(value, out var unixTimestamp))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
                }
            }

            // Tokens that carry their own epoch pair: the largest plausible future epoch is the expiry.
            var token = query["token"] ?? query["access_token"] ?? string.Empty;
            if (token.Length > 0)
            {
                var now = DateTimeOffset.UtcNow;
                var floor = now.AddDays(-1).ToUnixTimeSeconds();
                var ceiling = now.AddDays(7).ToUnixTimeSeconds();
                DateTimeOffset? best = null;
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(token, "(?<![0-9])(1[6-9][0-9]{8})(?![0-9])"))
                {
                    if (!long.TryParse(match.Groups[1].Value, out var epoch) || epoch < floor || epoch > ceiling)
                    {
                        continue;
                    }

                    var candidate = DateTimeOffset.FromUnixTimeSeconds(epoch);
                    if (best is null || candidate > best)
                    {
                        best = candidate;
                    }
                }

                if (best is not null)
                {
                    return best;
                }
            }
        }
        catch (Exception)
        {
            // URL sin expiración parseable: se usa el intervalo de respaldo.
        }

        return null;
    }
}
