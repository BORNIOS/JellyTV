using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Fetches and rewrites HLS playlists so all segment and sub-playlist URLs
/// are routed through the local proxy, enabling seamless token renewal.
/// </summary>
public class HlsRewriterService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProxyPoolService _proxyPool;
    private readonly RedirectResolverService _resolver;
    private readonly ILogger<HlsRewriterService> _logger;

    /// <summary>
    /// Backoff after a rejected token renewal. The token generator (e.g. alfa) applies
    /// a strict per-IP rate limit; hammering it makes playback stall for the whole window.
    /// </summary>
    private static readonly TimeSpan TokenRenewalCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Intervalo para renovar proactivamente el token antes de que expire. El proveedor
    /// emite tokens de corta vida (~2 min observado); renovar cada minuto mantiene uno
    /// fresco y evita el corte cuando el token muere (la app oficial re-consulta la URL).
    /// </summary>
    private static readonly TimeSpan ProactiveRenewalInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsRewriterService"/> class.
    /// </summary>
    public HlsRewriterService(
        IHttpClientFactory httpClientFactory,
        ProxyPoolService proxyPool,
        RedirectResolverService resolver,
        ILogger<HlsRewriterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _proxyPool = proxyPool;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// TTL del cache de playlist. Los segmentos de la ventana expiran en pocos segundos
    /// (verificado: el segmento mas viejo devuelve 403 unos segundos despues de publicarse).
    /// Un TTL cercano a la ventana entera hace que FFmpeg intente segmentos ya expirados y
    /// congele entre cambios de ventana. Servir siempre una ventana fresca (~1/3 de la ventana).
    /// </summary>
    private static TimeSpan GetPlaylistTtl(double? windowSeconds)
    {
        var window = windowSeconds ?? 5;
        var ttlSeconds = Math.Clamp(window / 3.0, 2.0, 4.0);
        return TimeSpan.FromSeconds(ttlSeconds);
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

        // Fast path: cache hit → no lock needed.
        if (!string.IsNullOrWhiteSpace(session.LastPlaylistContent)
            && session.LastPlaylistFetchedAt.HasValue
            && string.Equals(session.LastPlaylistSourceUrl, resolved.FinalUrl, StringComparison.Ordinal))
        {
            var age = DateTimeOffset.UtcNow - session.LastPlaylistFetchedAt.Value;
            var ttl = GetPlaylistTtl(session.LastPlaylistWindowSeconds);
            if (age < ttl)
            {
                PluginLog.Info($"[HLS] Cache hit (age={age.TotalSeconds:F0}s ttl={ttl.TotalSeconds:F0}s)");
                return session.LastPlaylistContent;
            }
        }

        // Slow path: cache expired. Serialize with a semaphore so ffmpeg + browser
        // don't both fetch from alfa simultaneously, doubling rate-limit pressure.
        await session.PlaylistFetchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check cache: another consumer may have populated it while we waited.
            if (!string.IsNullOrWhiteSpace(session.LastPlaylistContent)
                && session.LastPlaylistFetchedAt.HasValue
                && string.Equals(session.LastPlaylistSourceUrl, session.LastResolved?.FinalUrl, StringComparison.Ordinal))
            {
                var age = DateTimeOffset.UtcNow - session.LastPlaylistFetchedAt.Value;
                var ttl2 = GetPlaylistTtl(session.LastPlaylistWindowSeconds);
                if (age < ttl2)
                {
                    PluginLog.Info($"[HLS] Cache hit after wait (age={age.TotalSeconds:F0}s ttl={ttl2.TotalSeconds:F0}s)");
                    return session.LastPlaylistContent;
                }
            }

            // ponytail: for tokenized streams, use FinalUrl (tuenisix?token=XXX)
            // directly — tuenisix sirve playlist y segmentos sin rate-limit mientras
            // el token es valido. La renovacion (contra alfa) solo se dispara por
            // 403 real o proactivamente antes de que el token expire.
            // Proxy rotation is disabled — alfa blocks proxy IPs anyway.
            resolved = session.LastResolved
                ?? throw new InvalidOperationException("Session has no resolved stream.");
            var isTokenized = !string.IsNullOrWhiteSpace(resolved.TokenValue);

            // Renovacion proactiva: mantener un token fresco (< 60s) evita el corte
            // cuando el token muere (el proveedor lo rechaza con 403 en el peor momento).
            if (isTokenized && ShouldProactivelyRenew(session))
            {
                PluginLog.Info("[HLS] Proactive token renewal (age threshold reached).");
                var pr = !string.IsNullOrWhiteSpace(session.TokenRenewalUrl)
                    ? await TryRenewPlaylistViaGeneratorAsync(session, cancellationToken).ConfigureAwait(false)
                    : null;
                if (pr is null && !string.IsNullOrWhiteSpace(session.UpstreamUrl))
                {
                    await TryReResolveOriginAsync(session, cancellationToken).ConfigureAwait(false);
                }

                resolved = session.LastResolved;
            }

            string playlistContent;
            string finalPlaylistUrl;
            try
            {
                (playlistContent, finalPlaylistUrl) = await FetchPlaylistWithProxyRotationAsync(resolved.FinalUrl, isTokenized, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (IsAuthenticationFailure(ex) && isTokenized)
            {
                // Solo los tokens efimeros (type 2) se renuevan. Directo/FAST/permanente:
                // un 403 es terminal para el token actual, no se re-resuelve.
                if (session.ChannelType != 2)
                {
                    PluginLog.Warn($"[HLS] Auth failure on non-ephemeral channel (type={session.ChannelType}); not renewing.");
                    throw;
                }

                // Sin URL de generacion ni origen, no hay nada que renovar.
                if (string.IsNullOrWhiteSpace(session.TokenRenewalUrl) && string.IsNullOrWhiteSpace(session.UpstreamUrl))
                {
                    throw;
                }

                // Respect the generator's per-IP rate limit: after a failed renewal,
                // back off before trying again and keep serving the last valid playlist.
                if (session.LastTokenRenewalAttemptAt is { } lastAttempt
                    && DateTimeOffset.UtcNow - lastAttempt < TokenRenewalCooldown)
                {
                    PluginLog.Warn($"[HLS] Token renewal in cooldown; serving stale cache ({(int?)ex.StatusCode}).");
                    throw;
                }

                session.LastTokenRenewalAttemptAt = DateTimeOffset.UtcNow;
                PluginLog.Warn($"[HLS] Final token rejected; renewing via {session.TokenRenewalUrl}");

                // Intento 1: renovar contra el hop generador (alfa), como la resolucion inicial.
                var renewed = !string.IsNullOrWhiteSpace(session.TokenRenewalUrl)
                    ? await TryRenewPlaylistViaGeneratorAsync(session, cancellationToken).ConfigureAwait(false)
                    : null;
                if (renewed is null)
                {
                    // Intento 2: re-resolver desde el origen (api -> alfa -> tuenisix), el
                    // mismo camino con el que arranco la sesion y que la app oficial usa.
                    if (!string.IsNullOrWhiteSpace(session.UpstreamUrl)
                        && await TryReResolveOriginAsync(session, cancellationToken).ConfigureAwait(false))
                    {
                        PluginLog.Info("[HLS] Renewed via origin re-resolve; fetching fresh playlist.");
                        (playlistContent, finalPlaylistUrl) = await FetchPlaylistWithProxyRotationAsync(
                            session.LastResolved.FinalUrl, true, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        PluginLog.Warn("[HLS] Renewal rejected by generator; serving stale cache.");
                        throw;
                    }
                }
                else
                {
                    (playlistContent, finalPlaylistUrl) = renewed.Value;
                }
            }
            resolved = session.LastResolved ?? resolved;
            UpdateResolvedFinalUrl(resolved, finalPlaylistUrl);
            PluginLog.Info($"[HLS] Fetched {playlistContent.Length} bytes OK from {finalPlaylistUrl}");

            var (windowSeconds, targetDurationSeconds) = AnalyzeMediaPlaylistWindow(playlistContent);
            session.LastPlaylistSegmentUrls = ExtractMediaSegmentUrls(playlistContent, resolved).ToArray();
            var segmentCount = session.LastPlaylistSegmentUrls.Length;
            PluginLog.Info($"[HLS] Window={windowSeconds:F1}s TargetDur={targetDurationSeconds:F1}s Segments={segmentCount} First80={playlistContent[..Math.Min(80, playlistContent.Length)]}");
            session.LastPlaylistWindowSeconds = windowSeconds;
            session.LastPlaylistTargetDurationSeconds = targetDurationSeconds;

            var rewritten = RewritePlaylist(playlistContent, resolved, session.SessionId, proxyBaseUrl);
            session.LastPlaylistContent = rewritten;
            session.LastPlaylistSourceUrl = resolved.FinalUrl;
            session.LastPlaylistFetchedAt = DateTimeOffset.UtcNow;

            return rewritten;
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(session.LastPlaylistContent))
        {
            // Keep the last playlist during provider throttling. The next request
            // after this short cooldown may obtain a fresh token through redirects.
            var age = DateTimeOffset.UtcNow - (session.LastPlaylistFetchedAt ?? DateTimeOffset.MinValue);
            if (IsAuthenticationFailure(ex))
                session.LastPlaylistFetchedAt = DateTimeOffset.UtcNow;
            PluginLog.Warn($"[HLS] Fetch failed, serving stale cache (age={age.TotalSeconds:F0}s): {ex.Message}");
            return session.LastPlaylistContent;
        }
        finally
        {
            session.PlaylistFetchLock.Release();
        }
    }

    private static bool IsAuthenticationFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };

    /// <summary>
    /// Pre-fetches a fresh playlist in the background when ~80% of the current window
    /// has been consumed, so the next ffmpeg poll hits cache instead of making an upstream call.
    /// </summary>
    public async Task TryBackgroundRefreshPlaylistAsync(
        StreamSession session,
        string servedSegmentUrl,
        string proxyBaseUrl,
        CancellationToken ct)
    {
        if (session.IsPlaylistRefreshInProgress) return;
        if (session.LastPlaylistRefreshAttemptAt.HasValue
            && DateTimeOffset.UtcNow - session.LastPlaylistRefreshAttemptAt.Value < TimeSpan.FromSeconds(10))
            return;

        var segments = session.LastPlaylistSegmentUrls;
        if (segments is null || segments.Length == 0) return;

        var window = session.LastPlaylistWindowSeconds;
        if (!window.HasValue || window.Value <= 0) return;

        var idx = Array.FindIndex(segments,
            s => string.Equals(s, servedSegmentUrl, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;

        var targetDur = session.LastPlaylistTargetDurationSeconds ?? 2;
        var consumedFraction = (idx + 1) * targetDur / window.Value;
        if (consumedFraction < 0.8) return;

        session.IsPlaylistRefreshInProgress = true;
        session.LastPlaylistRefreshAttemptAt = DateTimeOffset.UtcNow;

        try
        {
            var resolved = session.LastResolved;
            if (resolved is null) return;

            var isTokenized = !string.IsNullOrWhiteSpace(resolved.TokenValue);
            var fetchUrl = resolved.FinalUrl;
            var (content, finalPlaylistUrl) = await FetchPlaylistWithProxyRotationAsync(fetchUrl, false, ct).ConfigureAwait(false);
            UpdateResolvedFinalUrl(resolved, finalPlaylistUrl);
            var (ws, ts) = AnalyzeMediaPlaylistWindow(content);
            session.LastPlaylistWindowSeconds = ws;
            session.LastPlaylistTargetDurationSeconds = ts;
            session.LastPlaylistSegmentUrls = ExtractMediaSegmentUrls(content, resolved).ToArray();

            var rewritten = RewritePlaylist(content, resolved, session.SessionId, proxyBaseUrl);
            session.LastPlaylistContent = rewritten;
            session.LastPlaylistSourceUrl = resolved.FinalUrl;
            session.LastPlaylistFetchedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[HLS] BG refresh failed: {ex.Message}");
        }
        finally
        {
            session.IsPlaylistRefreshInProgress = false;
        }
    }

    private static string[] ExtractMediaSegmentUrls(string playlistContent, ResolvedStream resolved)
    {
        var urls = new List<string>();
        using var reader = new StringReader(playlistContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;
            if (t.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) continue;
            urls.Add(ResolveUri(t, resolved.BaseUrl));
        }
        return urls.ToArray();
    }

    private async Task<(string Content, string FinalUrl)> FetchPlaylistWithProxyRotationAsync(string playlistUrl, bool isTokenized, CancellationToken cancellationToken)
    {
        // ponytail: use a fresh clean HttpClient. Shared cookies from the
        // IHttpClientFactory accumulate state across sessions and cause 403
        // from tuenisix even when the token is still valid (confirmed via curl).
        using var cleanHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 15,
            UseCookies = false
        };
        using var fallbackClient = new HttpClient(cleanHandler, disposeHandler: false);
        fallbackClient.Timeout = TimeSpan.FromSeconds(5);
        fallbackClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            Plugin.Instance?.Configuration?.UserAgent ?? "Mozilla/5.0 (compatible; JellyTV/1.0)");

        const int attempt = 1;
        cancellationToken.ThrowIfCancellationRequested();

            var (proxyKey, proxyClient) = isTokenized ? _proxyPool.GetNextSegmentProxy() : (null, null);
            var client = proxyClient ?? fallbackClient;

            PluginLog.Info($"[HLS] Fetch attempt {attempt} from {playlistUrl} proxy={proxyKey ?? "direct"} tokenized={isTokenized}");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode is 401 or 403)
                    {
                        // Proxy 403 is usually CDN-blocking, not a token issue.
                        // Mark failed and retry direct on next iteration.
                        if (proxyKey is not null)
                        {
                            _proxyPool.MarkFailed(proxyKey);
                            throw new HttpRequestException("Tokenized playlist proxy was rejected.", null, response.StatusCode);
                        }

                        throw new HttpRequestException(
                            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).",
                            null,
                            response.StatusCode);
                    }

                    throw new HttpRequestException(
                        $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).",
                        null,
                        response.StatusCode);
                }

                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? playlistUrl;
                return (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), finalUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal when Jellyfin/FFmpeg stops playback and aborts pending HTTP calls.
                throw;
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403)
            {
                throw;
            }
            catch (HttpRequestException ex) when (proxyKey is not null && ex.StatusCode is null)
            {
                _proxyPool.MarkFailed(proxyKey);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[JellyTV] Playlist fetch attempt {Attempt} failed for {Url}", attempt, playlistUrl);
                throw;
            }
    }

    private static (double? WindowSeconds, double? TargetDurationSeconds) AnalyzeMediaPlaylistWindow(string playlistText)
    {
        if (string.IsNullOrWhiteSpace(playlistText))
        {
            return (null, null);
        }

        // Master playlists do not carry segment EXTINF lines for playback window sizing.
        if (playlistText.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            return (null, null);
        }

        double window = 0;
        int segmentCount = 0;
        double? targetDuration = null;

        using var reader = new StringReader(playlistText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                var comma = line.IndexOf(',', colon + 1);
                var end = comma >= 0 ? comma : line.Length;
                if (colon >= 0
                    && end > colon + 1
                    && double.TryParse(line[(colon + 1)..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    && d > 0)
                {
                    window += d;
                    segmentCount++;
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                var value = line["#EXT-X-TARGETDURATION:".Length..];
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                {
                    targetDuration = d;
                }
            }
        }

        if (segmentCount == 0)
        {
            return (null, targetDuration);
        }

        return (window > 0 ? window : null, targetDuration);
    }

    /// <summary>
    /// Rewrites an already downloaded HLS playlist using the same rules as the main stream path.
    /// This is used for nested playlists fetched through /JellyTV/segment.
    /// </summary>
    public string RewritePlaylistContent(string content, string sourceUrl, string sessionId, string proxyBaseUrl)
    {
        var resolved = new ResolvedStream
        {
            BaseUrl = BuildBaseUrl(sourceUrl),
            FinalUrl = sourceUrl
        };

        return RewritePlaylist(content, resolved, sessionId, proxyBaseUrl);
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

        var addedLiveStart = false;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!addedLiveStart && line.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                sb.AppendLine(line);
                sb.AppendLine("#EXT-X-START:TIME-OFFSET=-3,PRECISE=YES");
                addedLiveStart = true;
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                line = RewriteMediaTrack(line, resolved, sessionId, proxyBaseUrl);
                sb.AppendLine(line);
                continue;
            }

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
    private static string RewriteInlineUri(string tagLine, ResolvedStream resolved, string sessionId, string proxyBaseUrl)
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
    /// Rewrites audio/subtitle track declarations so the preferred Spanish audio becomes default.
    /// Subtitles remain optional but are rewritten through the proxy when present.
    /// </summary>
    private static string RewriteMediaTrack(string tagLine, ResolvedStream resolved, string sessionId, string proxyBaseUrl)
    {
        var attributes = ParseTagAttributes(tagLine);
        if (attributes.Count == 0)
        {
            return tagLine;
        }

        string? type = null;
        string? language = null;
        string? uri = null;

        foreach (var attribute in attributes)
        {
            if (attribute.Key.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
            {
                type = attribute.Value;
            }
            else if (attribute.Key.Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase))
            {
                language = attribute.Value;
            }
            else if (attribute.Key.Equals("URI", StringComparison.OrdinalIgnoreCase))
            {
                uri = attribute.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(uri))
        {
            var absoluteUri = ResolveUri(uri, resolved.BaseUrl);
            var proxiedUri = BuildProxiedSegmentUrl(absoluteUri, sessionId, proxyBaseUrl);
            ReplaceAttribute(attributes, "URI", proxiedUri, quoted: true);
        }

        var preferredAudioLanguages = GetConfiguredLanguages(Plugin.Instance?.Configuration?.PreferredAudioLanguages, "es,spa,es-419");
        var preferredSubtitleLanguages = GetConfiguredLanguages(Plugin.Instance?.Configuration?.PreferredSubtitleLanguages, "es,spa");

        if (!string.IsNullOrWhiteSpace(type) && type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
        {
            var isPreferred = IsPreferredLanguage(language, preferredAudioLanguages);
            ReplaceAttribute(attributes, "DEFAULT", isPreferred ? "YES" : "NO", quoted: false);
            ReplaceAttribute(attributes, "AUTOSELECT", isPreferred ? "YES" : "NO", quoted: false);
        }
        else if (!string.IsNullOrWhiteSpace(type) && type.Equals("SUBTITLES", StringComparison.OrdinalIgnoreCase))
        {
            var isPreferred = IsPreferredLanguage(language, preferredSubtitleLanguages);
            ReplaceAttribute(attributes, "DEFAULT", "NO", quoted: false);
            ReplaceAttribute(attributes, "AUTOSELECT", isPreferred ? "YES" : "NO", quoted: false);
        }

        return BuildTagLine(tagLine, attributes);
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

    private static List<TagAttribute> ParseTagAttributes(string tagLine)
    {
        var colonIndex = tagLine.IndexOf(':');
        if (colonIndex < 0 || colonIndex + 1 >= tagLine.Length)
        {
            return [];
        }

        var payload = tagLine[(colonIndex + 1)..];
        var result = new List<TagAttribute>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in payload)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                AddAttribute(result, current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        AddAttribute(result, current.ToString());
        return result;
    }

    private static void AddAttribute(List<TagAttribute> result, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var parts = raw.Split('=', 2);
        if (parts.Length != 2)
        {
            return;
        }

        var value = parts[1].Trim();
        var quoted = value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"');
        if (quoted)
        {
            value = value[1..^1];
        }

        result.Add(new TagAttribute(parts[0].Trim(), value, quoted));
    }

    private static void ReplaceAttribute(List<TagAttribute> attributes, string key, string value, bool quoted)
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            if (attributes[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                attributes[i] = attributes[i] with { Value = value, WasQuoted = quoted };
                return;
            }
        }

        attributes.Add(new TagAttribute(key, value, quoted));
    }

    private static string BuildTagLine(string tagLine, List<TagAttribute> attributes)
    {
        var colonIndex = tagLine.IndexOf(':');
        if (colonIndex < 0)
        {
            return tagLine;
        }

        var sb = new StringBuilder();
        sb.Append(tagLine[..(colonIndex + 1)]);

        for (var i = 0; i < attributes.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var attribute = attributes[i];
            sb.Append(attribute.Key).Append('=');
            if (attribute.WasQuoted)
            {
                sb.Append('"').Append(attribute.Value).Append('"');
            }
            else
            {
                sb.Append(attribute.Value);
            }
        }

        return sb.ToString();
    }

    private static List<string> GetConfiguredLanguages(string? value, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value;
        var languages = new List<string>();

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                languages.Add(part.Trim());
            }
        }

        return languages;
    }

    private static bool IsPreferredLanguage(string? language, List<string> preferredLanguages)
    {
        if (string.IsNullOrWhiteSpace(language) || preferredLanguages.Count == 0)
        {
            return false;
        }

        foreach (var preferred in preferredLanguages)
        {
            if (language.Equals(preferred, StringComparison.OrdinalIgnoreCase)
                || language.StartsWith(preferred + "-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a proxy URL that routes a segment or sub-playlist through this plugin.
    /// The upstream URL is Base64-encoded to safely pass it as a query parameter.
    /// </summary>
    private static string BuildProxiedSegmentUrl(string upstreamUrl, string sessionId, string proxyBaseUrl)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(upstreamUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('='); // URL-safe Base64
        var suffix = GetProxySuffix(upstreamUrl);

        return $"{proxyBaseUrl}/segment.{suffix}?sid={Uri.EscapeDataString(sessionId)}&url={encoded}";
    }

    private static string GetProxySuffix(string upstreamUrl)
    {
        try
        {
            var path = new Uri(upstreamUrl).AbsolutePath;
            if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return "m3u8";
            }

            if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                return "ts";
            }

            if (path.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
            {
                return "aac";
            }

            if (path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                return "m4s";
            }
        }
        catch
        {
            // fallback below
        }

        return "ts";
    }

    private static string BuildBaseUrl(string url)
    {
        var uri = new Uri(url);
        var last = uri.AbsolutePath.LastIndexOf('/');
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}{(last >= 0 ? uri.AbsolutePath[..(last + 1)] : "/")}";
    }

    /// <summary>
    /// Requests a fresh playlist from the token generator using the factory client
    /// (automatic redirects + shared cookies). This replicates the initial resolve
    /// chain, which is the only path the generator accepts reliably. Returns null
    /// when the generator rejects the request (e.g. rate limit).
    /// </summary>
    private async Task<(string Content, string FinalUrl)?> TryRenewPlaylistViaGeneratorAsync(
        StreamSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("JellyTV");
            using var request = new HttpRequestMessage(HttpMethod.Get, session.TokenRenewalUrl!);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                PluginLog.Warn($"[HLS] Generator rejected renewal with {(int)response.StatusCode}.");
                return null;
            }

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? session.TokenRenewalUrl!;
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (session.LastResolved is { } resolved)
            {
                UpdateResolvedFinalUrl(resolved, finalUrl);
            }

            session.LastTokenRenewedAt = DateTimeOffset.UtcNow;
            PluginLog.Info($"[HLS] Token renewed via generator; final={finalUrl}");
            return (content, finalUrl);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[HLS] Generator renewal fetch failed: {ex.Message}");
            return null;
        }
    }

    private static void InvalidatePlaylistCache(StreamSession session)
    {
        session.LastPlaylistContent = null;
        session.LastPlaylistSourceUrl = null;
        session.LastPlaylistFetchedAt = null;
        session.LastPlaylistSegmentUrls = null;
        session.LastPlaylistWindowSeconds = null;
        session.LastPlaylistTargetDurationSeconds = null;
    }

    /// <summary>
    /// Renueva el token re-resolviendo la cadena completa desde el origen (api -> alfa ->
    /// tuenisix), el mismo camino con el que arranco la sesion. Mas fiable que golpear
    /// alfa directamente cuando este rechaza la renovacion.
    /// </summary>
    private async Task<bool> TryReResolveOriginAsync(StreamSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.UpstreamUrl))
        {
            return false;
        }

        try
        {
            var resolved = await _resolver.ResolveAsync(session.UpstreamUrl, ct).ConfigureAwait(false);
            session.LastResolved = resolved;
            session.TokenRenewalUrl = resolved.TokenGeneratorHopUrl;
            session.LastTokenRenewedAt = DateTimeOffset.UtcNow;
            session.IsInvalid = false;
            InvalidatePlaylistCache(session);
            PluginLog.Info($"[HLS] Origin re-resolved; final={resolved.FinalUrl}");
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[HLS] Origin re-resolve failed: {ex.Message}");
            return false;
        }
    }

    private static bool ShouldProactivelyRenew(StreamSession session) =>
        session.ChannelType == 2
        && session.LastTokenRenewedAt is { } last
        && DateTimeOffset.UtcNow - last >= ProactiveRenewalInterval
        && session.LastResolved is { TokenValue: not null and not "" };

    private static void UpdateResolvedFinalUrl(ResolvedStream resolved, string finalUrl)
    {
        if (string.IsNullOrWhiteSpace(finalUrl) || !Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri))
            return;

        resolved.FinalUrl = finalUrl;
        resolved.BaseUrl = BuildBaseUrl(finalUrl);

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in new[] { "token", "access_token", "auth", "t", "key", "sig", "tokenid" })
        {
            if (!string.IsNullOrWhiteSpace(query[key]))
            {
                resolved.TokenValue = query[key];
                break;
            }
        }

        foreach (var key in new[] { "expires", "exp", "expiry", "end", "validuntil", "token_expiry" })
        {
            if (long.TryParse(query[key], out var expiry))
            {
                resolved.TokenExpiry = DateTimeOffset.FromUnixTimeSeconds(expiry);
                break;
            }
        }
    }

    private record struct TagAttribute(string Key, string Value, bool WasQuoted);

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