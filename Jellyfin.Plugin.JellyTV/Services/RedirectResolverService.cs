using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JellyTV.Models;
using Microsoft.Extensions.Logging;

namespace JellyTV.Services;

/// <summary>
/// Resolves IPTV URLs by following HTTP redirects to obtain the final playback URL.
/// </summary>
public class RedirectResolverService
{
    // Hardcoded constants (not configurable to avoid interference with token renewal logic)
    private const int MAX_REDIRECTS = 20;
    private const int REQUEST_TIMEOUT_SECONDS = 30;

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
        var maxRedirects = MAX_REDIRECTS;

        // Select one proxy for this entire resolve call (both auto and manual paths share it).
        // proxyKey is non-null only when a healthy proxy was selected.
        var (proxyKey, proxyNoRedirect, proxyAutoRedirect) = _proxyPool.GetNextProxy();

        // Strategy similar to robust player clients:
        // 1) Try automatic redirect handling first (browser-like behavior + shared cookies).
        // 2) Fallback to manual redirect walk for full diagnostics/control.
        var autoResolved = await TryResolveWithAutoRedirectAsync(originalUrl, proxyAutoRedirect, cancellationToken).ConfigureAwait(false);
        if (autoResolved is not null)
        {
            autoResolved.ResolverProxyKey = proxyKey;
            return autoResolved;
        }

        // Use a client that does NOT auto-follow redirects so we can track them.
        // directClient is always the factory fallback; proxyClient may be null.
        var directClient = _httpClientFactory.CreateClient("IptvNoRedirect");
        var client = proxyNoRedirect ?? directClient;
        // Set to true after the first proxy failure so remaining hops skip the proxy timeout.
        bool proxyMarkedFailed = proxyKey is null;

        var currentUrl = originalUrl;
        var redirectCount = 0;
        var redirectChain = new List<string> { originalUrl };
        string contentType = string.Empty;
        string? initialPlaylistContent = null;
        string? rotationNodeUrl = null;

        if (Plugin.Instance?.Configuration.VerboseLogging == true)
        {
            _logger.LogInformation("[JellyTV] Starting resolution for: {Url}", originalUrl);
        }
        else
        {
            _logger.LogDebug("[JellyTV] Starting resolution for: {Url}", originalUrl);
        }

        while (redirectCount <= maxRedirects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage response;

            try
            {
                PluginLog.Info($"[RESOLVE] GET {currentUrl} (attempt {redirectCount + 1})");
                var (resp, usedFallback) = await SendWithFallbackAsync(client, directClient, currentUrl, cancellationToken).ConfigureAwait(false);
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
                _logger.LogError(ex, "[JellyTV] HTTP error fetching {Url}", currentUrl);
                PluginLog.Error($"[RESOLVE] HTTP error fetching {currentUrl}", ex);
                throw;
            }

            contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    _logger.LogWarning("[JellyTV] Redirect with no Location header at {Url}", currentUrl);
                    PluginLog.Warn($"[RESOLVE] Redirect with no Location header at {currentUrl}");
                    break;
                }

                // Resolve relative redirects
                var nextUrl = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(currentUrl), location).ToString();

                _logger.LogDebug("[JellyTV] Redirect {Count}: {From} -> {To}", redirectCount + 1, currentUrl, nextUrl);
                PluginLog.Info($"[RESOLVE] Redirect {redirectCount + 1}: {currentUrl} -> {nextUrl}");
                // Track the rotation node: the hop that outputs a tokenized redirect destination.
                if (IsRotationSignal(nextUrl))
                    rotationNodeUrl = currentUrl;
                // Record redirect hop
                redirectChain.Add(nextUrl);
                currentUrl = nextUrl;
                redirectCount++;
                response.Dispose();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Some providers return 401/403 for specific proxy egress IPs on intermediate hops
                // (e.g., api/alfa) even when the direct host path is valid. Retry this same hop once
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

                _logger.LogWarning("[JellyTV] Non-success HTTP {Status} while resolving {Url}", (int)response.StatusCode, currentUrl);
                PluginLog.Warn($"[RESOLVE] Non-success HTTP {(int)response.StatusCode} while resolving {currentUrl}");
                // Throw an HttpRequestException that preserves the status code for callers to handle
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Response status code does not indicate success: {(int)status} ({status}).", null, status);
            }

            // Read the body on the final 200 step so the BroadcastPump can reuse it
            // without making a second GET.  Many IPTV providers apply a per-token
            // rate-limit (effectively single-use per window), so a redundant fetch
            // immediately after resolution returns 403 and causes a ~52-second 403 loop.
            string? resolverBodyText = null;
            var ct2 = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (IsHlsUrl(currentUrl, ct2))
            {
                try { resolverBodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
                catch { /* non-fatal — pump will fetch normally */ }
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

        // For tokenized streams, use the rotation node (e.g. alfa.7057460.xyz) as poll URL.
        // GET rotationNode → HTTP→HTTP redirect → tuenisix?freshToken on every poll cycle.
        // This avoids: (a) HTTPS→HTTP block from Chain[0] (jellytv HTTPS origin),
        //              (b) re-using a stale ?token= URL that expired between polls.
        // Falls back to originalUrl if no rotation node was detected (non-standard chain).
        string playlistPollUrl;
        if (!string.IsNullOrEmpty(tokenValue))
            playlistPollUrl = rotationNodeUrl ?? originalUrl;
        else
            playlistPollUrl = currentUrl;

        var resolved = new ResolvedStream
        {
            OriginalUrl = originalUrl,
            FinalUrl = currentUrl,
            PlaylistPollUrl = playlistPollUrl,
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

        if (Plugin.Instance?.Configuration.VerboseLogging == true)
        {
            _logger.LogInformation(
                "[JellyTV] Resolved to: {FinalUrl} | HLS={IsHls} | TokenExpiry={Expiry} | TokenPresent={TokenPresent}",
                currentUrl, isHls, tokenExpiry?.ToString("o") ?? "unknown", !string.IsNullOrEmpty(tokenValue));
        }
        else
        {
            _logger.LogDebug(
                "[JellyTV] Resolved to: {FinalUrl} | HLS={IsHls} | TokenExpiry={Expiry} | TokenPresent={TokenPresent}",
                currentUrl, isHls, tokenExpiry?.ToString("o") ?? "unknown", !string.IsNullOrEmpty(tokenValue));
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
        catch { }

        return resolved;
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
                // A 302 at the top of an HTTPS→HTTP chain is expected for providers like
                // jellytv.hmovies.tv (the auto-redirect client cannot downgrade schemes).
                // Log as INFO so it does not pollute the warning stream on normal operation.
                var sc = (int)response.StatusCode;
                if (sc is >= 300 and < 400)
                    PluginLog.Info($"[RESOLVE-AUTO] Redirect {sc} at {originalUrl}; using manual resolver for cross-scheme chain");
                else
                    PluginLog.Warn($"[RESOLVE-AUTO] Non-success HTTP {sc} at {originalUrl}; falling back to manual resolver");
                return null;
            }

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            string? initialPlaylistContent = null;

            if (IsHlsUrl(finalUrl, contentType))
            {
                try
                {
                    initialPlaylistContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Non-fatal, plugin can fetch playlist later.
                }
            }

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
                InitialPlaylistContent = initialPlaylistContent,
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
        HttpClient preferred, HttpClient fallback, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        try
        {
            var resp = await preferred.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (resp, false);
        }
        catch (HttpRequestException ex) when (preferred != fallback)
        {
            PluginLog.Warn($"[RESOLVE] Proxy failed for {url}: {ex.Message} — retrying direct");
            using var retryReq = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await fallback.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (resp, true);
        }
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
        if (url.Contains("?token=", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains("/hlsr/", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var q = new Uri(url).Query;
            return q.Length > 80;
        }
        catch { return false; }
    }

    /// <summary>
    /// Attempts to extract a token expiry time from common query parameter patterns.
    /// </summary>
    private DateTimeOffset? TryParseTokenExpiry(string url)
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
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[JellyTV] Could not parse token expiry from URL");
        }

        return null;
    }
}
