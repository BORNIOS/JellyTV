using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly ProxyPoolService _proxyPool;
    private readonly ILogger<HlsRewriterService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsRewriterService"/> class.
    /// </summary>
    public HlsRewriterService(
        IHttpClientFactory httpClientFactory,
        ProxyPoolService proxyPool,
        ILogger<HlsRewriterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _proxyPool = proxyPool;
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

        // --- CASO 1: El resolver ya capturo el body durante la cadena de redirects.
        // Usarlo directamente evita un segundo GET al token de un solo uso.
        if (!string.IsNullOrWhiteSpace(resolved.InitialPlaylistContent))
        {
            PluginLog.Info($"[HLS] Using resolver-cached playlist sid={session.SessionId}");
            var fresh = resolved.InitialPlaylistContent;
            resolved.InitialPlaylistContent = null;

            var (ws, ts) = AnalyzeMediaPlaylistWindow(fresh);
            session.LastPlaylistWindowSeconds = ws;
            session.LastPlaylistTargetDurationSeconds = ts;

            var rw = RewritePlaylist(fresh, resolved, session.SessionId, proxyBaseUrl);
            session.LastPlaylistContent = rw;
            session.LastPlaylistSourceUrl = resolved.FinalUrl;
            session.LastPlaylistFetchedAt = DateTimeOffset.UtcNow;
            return rw;
        }

        // --- CASO 2: Tenemos playlist cacheado y la ventana HLS no ha expirado todavia.
        // Servir desde cache sin ningun request externo.
        // La ventana de segmentos del proveedor suele ser 40-60s; el controlador ya se
        // encarga de renovar el token antes de que expire (deteccion de ultimo segmento).
        if (!string.IsNullOrWhiteSpace(session.LastPlaylistContent)
            && session.LastPlaylistFetchedAt.HasValue)
        {
            var age = DateTimeOffset.UtcNow - session.LastPlaylistFetchedAt.Value;
            // Usar la ventana conocida menos un margen de un segmento; si no la conocemos, 50s.
            var windowTtl = session.LastPlaylistWindowSeconds.HasValue
                ? TimeSpan.FromSeconds(Math.Max(10, session.LastPlaylistWindowSeconds.Value - (session.LastPlaylistTargetDurationSeconds ?? 10)))
                : TimeSpan.FromSeconds(50);

            if (age < windowTtl)
            {
                // Cache vigente: devolver sin tocar el proveedor.
                return session.LastPlaylistContent;
            }

            // Cache expirado pero aun no tenemos InitialPlaylistContent del renewal.
            // Servir stale para no romper playback; el controlador triggera renewal por
            // deteccion de ultimo segmento y, cuando llegue, tendra InitialPlaylistContent.
            PluginLog.Warn($"[HLS] Playlist cache expired (age={age.TotalSeconds:F0}s window={windowTtl.TotalSeconds:F0}s) but no renewal ready yet sid={session.SessionId} — serving stale to preserve playback");
            return session.LastPlaylistContent;
        }

        // --- CASO 3: Sin cache y sin InitialPlaylistContent (sesion nueva, primer poll
        // despues de fallo de red, etc.). Hacer fetch directo como fallback.
        PluginLog.Info($"[HLS] No cache and no InitialPlaylistContent, fetching direct sid={session.SessionId}");
        var playlistContent = await FetchPlaylistWithProxyRotationAsync(resolved.FinalUrl, cancellationToken)
            .ConfigureAwait(false);

        var (windowSeconds, targetDurationSeconds) = AnalyzeMediaPlaylistWindow(playlistContent);
        session.LastPlaylistWindowSeconds = windowSeconds;
        session.LastPlaylistTargetDurationSeconds = targetDurationSeconds;

        var rewritten = RewritePlaylist(playlistContent, resolved, session.SessionId, proxyBaseUrl);
        session.LastPlaylistContent = rewritten;
        session.LastPlaylistSourceUrl = resolved.FinalUrl;
        session.LastPlaylistFetchedAt = DateTimeOffset.UtcNow;

        return rewritten;
    }

    private async Task<string> FetchPlaylistWithProxyRotationAsync(string playlistUrl, CancellationToken cancellationToken)
    {
        var directClient = _httpClientFactory.CreateClient("JellyTV");
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (proxyKey, proxyClient) = _proxyPool.GetNextSegmentProxy();
            var client = proxyClient ?? directClient;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).",
                        null,
                        response.StatusCode);
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal when Jellyfin/FFmpeg stops playback and aborts pending HTTP calls.
                throw;
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403)
            {
                // Auth failures are token-specific, not proxy-related.
                // Rotating to a different proxy will NOT fix an expired token.
                // Re-throw immediately so the caller triggers renewal without burning
                // 3 more round-trips that all produce 403 for the same stale token.
                PluginLog.Warn($"[HLS] Playlist auth failure {(int?)ex.StatusCode} for {playlistUrl} — failing fast (no proxy rotation for auth errors)");
                throw;
            }
            catch (HttpRequestException ex) when (proxyKey is not null && ex.StatusCode is null)
            {
                _proxyPool.MarkFailed(proxyKey);
                lastException = ex;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "[JellyTV] Playlist fetch attempt {Attempt} failed for {Url}", attempt, playlistUrl);
            }
        }

        if (lastException is not null)
        {
            throw lastException;
        }

        throw new HttpRequestException($"Unable to fetch playlist from {playlistUrl} after proxy rotation attempts.");
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

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
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