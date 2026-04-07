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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RedirectResolverService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedirectResolverService"/> class.
    /// </summary>
    public RedirectResolverService(
        IHttpClientFactory httpClientFactory,
        ILogger<RedirectResolverService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the given URL by following all HTTP redirects until the final URL is reached.
    /// </summary>
    /// <param name="originalUrl">The initial IPTV URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ResolvedStream"/> containing the final URL and metadata.</returns>
    public async Task<ResolvedStream> ResolveAsync(string originalUrl, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        var maxRedirects = config?.MaxRedirects ?? 10;

        // Strategy similar to robust player clients:
        // 1) Try automatic redirect handling first (browser-like behavior + shared cookies).
        // 2) Fallback to manual redirect walk for full diagnostics/control.
        var autoResolved = await TryResolveWithAutoRedirectAsync(originalUrl, cancellationToken).ConfigureAwait(false);
        if (autoResolved is not null)
        {
            return autoResolved;
        }

        // Use a client that does NOT auto-follow redirects so we can track them
        var client = _httpClientFactory.CreateClient("IptvNoRedirect");

        var currentUrl = originalUrl;
        var redirectCount = 0;
        var redirectChain = new List<string> { originalUrl };
        string contentType = string.Empty;
        string? initialPlaylistContent = null;

        _logger.LogInformation("[JellyTV] Starting resolution for: {Url}", originalUrl);

        while (redirectCount <= maxRedirects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            HttpResponseMessage response;

            try
            {
                PluginLog.Info($"[RESOLVE] GET {currentUrl} (attempt {redirectCount + 1})");
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                PluginLog.Info($"[RESOLVE] Response {currentUrl} -> {(int)response.StatusCode}");
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
                // Record redirect hop
                redirectChain.Add(nextUrl);
                currentUrl = nextUrl;
                redirectCount++;
                response.Dispose();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
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

        // For tokenized streams, use the penultimate redirect URL as the playlist poll URL.
        // This means each playlist poll follows a fresh token-generating redirect (e.g. alfa →
        // tuenisix?newToken) rather than re-using the short-lived query-string token directly.
        // The short-lived ?token= in the final URL is effectively single-use; re-polying it
        // directly causes 401/403 within seconds and triggers unnecessary renewal loops.
        string playlistPollUrl = currentUrl;
        if (!string.IsNullOrEmpty(tokenValue) && redirectChain.Count >= 2)
            playlistPollUrl = redirectChain[redirectChain.Count - 2];

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

        _logger.LogInformation(
            "[JellyTV] Resolved to: {FinalUrl} | HLS={IsHls} | TokenExpiry={Expiry} | TokenPresent={TokenPresent}",
            currentUrl, isHls, tokenExpiry?.ToString("o") ?? "unknown", !string.IsNullOrEmpty(tokenValue));

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
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("JellyTV");
            using var request = new HttpRequestMessage(HttpMethod.Get, originalUrl);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                PluginLog.Warn($"[RESOLVE-AUTO] Non-success HTTP {(int)response.StatusCode} at {originalUrl}; falling back to manual resolver");
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
                PlaylistPollUrl = finalUrl, // auto-resolve doesn't expose chain; fallback to FinalUrl
                BaseUrl = BuildBaseUrl(finalUrl),
                ContentType = contentType,
                IsHls = IsHlsUrl(finalUrl, contentType),
                TokenExpiry = TryParseTokenExpiry(finalUrl),
                TokenValue = TryExtractTokenFromUrl(finalUrl),
                ResolvedAt = DateTimeOffset.UtcNow,
                RedirectChain = redirectChain,
                InitialPlaylistContent = initialPlaylistContent,
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
