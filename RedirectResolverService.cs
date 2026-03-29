using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IptvProxy.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IptvProxy.Services;

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
        var userAgent = config?.UserAgent ?? "Mozilla/5.0 (compatible; JellyfinIptvProxy/1.0)";

        // Use a client that does NOT auto-follow redirects so we can track them
        var client = _httpClientFactory.CreateClient("IptvNoRedirect");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

        var currentUrl = originalUrl;
        var redirectCount = 0;
        string contentType = string.Empty;

        _logger.LogInformation("[IptvProxy] Starting resolution for: {Url}", originalUrl);

        while (redirectCount <= maxRedirects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            HttpResponseMessage response;

            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IptvProxy] HTTP error fetching {Url}", currentUrl);
                throw;
            }

            contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    _logger.LogWarning("[IptvProxy] Redirect with no Location header at {Url}", currentUrl);
                    break;
                }

                // Resolve relative redirects
                var nextUrl = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(currentUrl), location).ToString();

                _logger.LogDebug("[IptvProxy] Redirect {Count}: {From} -> {To}", redirectCount + 1, currentUrl, nextUrl);
                currentUrl = nextUrl;
                redirectCount++;
                response.Dispose();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[IptvProxy] Non-success HTTP {Status} while resolving {Url}", (int)response.StatusCode, currentUrl);
                // Throw an HttpRequestException that preserves the status code for callers to handle
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Response status code does not indicate success: {(int)status} ({status}).", null, status);
            }
            response.Dispose();
            break;
        }

        if (redirectCount > maxRedirects)
        {
            throw new InvalidOperationException($"[IptvProxy] Exceeded maximum redirect count ({maxRedirects}) for URL: {originalUrl}");
        }

        var baseUrl = BuildBaseUrl(currentUrl);
        var isHls = IsHlsUrl(currentUrl, contentType);
        var tokenExpiry = TryParseTokenExpiry(currentUrl);
        var tokenValue = TryExtractTokenFromUrl(currentUrl);

        var resolved = new ResolvedStream
        {
            OriginalUrl = originalUrl,
            FinalUrl = currentUrl,
            BaseUrl = baseUrl,
            ContentType = contentType,
            IsHls = isHls,
            TokenExpiry = tokenExpiry,
            TokenValue = tokenValue,
            ResolvedAt = DateTimeOffset.UtcNow
        };

        _logger.LogInformation(
            "[IptvProxy] Resolved to: {FinalUrl} | HLS={IsHls} | TokenExpiry={Expiry} | TokenPresent={TokenPresent}",
            currentUrl, isHls, tokenExpiry?.ToString("o") ?? "unknown", !string.IsNullOrEmpty(tokenValue));

        return resolved;
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
            _logger.LogDebug(ex, "[IptvProxy] Could not extract token from URL");
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
            _logger.LogDebug(ex, "[IptvProxy] Could not parse token expiry from URL");
        }

        return null;
    }
}
