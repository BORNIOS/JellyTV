using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private readonly UpstreamClientService _upstream;
    private readonly RedirectResolverService _resolver;
    private readonly ILogger<HlsRewriterService> _logger;
    private readonly RelayUrlCache _relayUrls = new();

    /// <summary>
    /// Backoff after a rejected token renewal. Some token generators apply
    /// a strict per-IP rate limit; hammering it makes playback stall for the whole window.
    /// </summary>
    private static readonly TimeSpan TokenRenewalCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Intervalo por defecto para renovar proactivamente cuando el token no declara expiración.
    /// Es un respaldo conservador: renovar cada 9 s machacaba generadores que emiten tokens de
    /// horas (gateways con token de sesión), provocando 403 y re-resoluciones constantes.
    /// </summary>
    private static readonly TimeSpan DefaultProactiveRenewalInterval = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Margen antes de la expiración real del token para renovarlo proactivamente.
    /// </summary>
    private static readonly TimeSpan ProactiveRenewalMargin = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsRewriterService"/> class.
    /// </summary>
    public HlsRewriterService(
        IHttpClientFactory httpClientFactory,
        ProxyPoolService proxyPool,
        UpstreamClientService upstream,
        RedirectResolverService resolver,
        ILogger<HlsRewriterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _proxyPool = proxyPool;
        _upstream = upstream;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// TTL del cache de playlist. Los segmentos de la ventana expiran en pocos segundos
    /// (verificado: el segmento mas viejo devuelve 403 unos segundos despues de publicarse).
    /// Un TTL cercano a la ventana entera hace que FFmpeg intente segmentos ya expirados y
    /// congele entre cambios de ventana. Servir siempre una ventana fresca (~1/3 de la ventana).
    /// Cap superior bajo (2.5 s): con segmentos de 6-7 s reduce los micro-cortes de frontera
    /// porque FFmpeg descubre antes el segmento nuevo.
    /// </summary>
    private static TimeSpan GetPlaylistTtl(double? windowSeconds)
    {
        var window = windowSeconds ?? 5;

        // Ventana DVR amplia (300+ s): la playlist se recorta a ~60 s de vivo, así que servir la
        // copia 8 s no deja huecos y reduce a la mitad las descargas del playlist gigante.
        if (window >= 300)
        {
            return TimeSpan.FromSeconds(8);
        }

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
                RelayMetrics.Count(session.SessionId, "playlist.cacheHit");
                PluginLog.Info($"[HLS] Cache hit (age={age.TotalSeconds:F0}s ttl={ttl.TotalSeconds:F0}s)");
                return session.LastPlaylistContent;
            }
        }

        // Slow path: cache expired. Serialize with a semaphore so ffmpeg + browser
        // don't both fetch from the token source simultaneously, doubling rate-limit pressure.
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
                    RelayMetrics.Count(session.SessionId, "playlist.cacheHitAfterWait");
                    PluginLog.Info($"[HLS] Cache hit after wait (age={age.TotalSeconds:F0}s ttl={ttl2.TotalSeconds:F0}s)");
                    return session.LastPlaylistContent;
                }
            }

            // Token efímero sin renovación posible todavía: no se golpea al proveedor con un token muerto,
            // se sirve lo último guardado (los segmentos ya están en memoria) hasta poder pedir otro.
            if (session.TokenKnownDead && session.InGeneratorBackoff && !string.IsNullOrWhiteSpace(session.LastPlaylistContent))
            {
                return session.LastPlaylistContent;
            }

            // for tokenized streams, use FinalUrl directly while the
            // token is valid. Renewal only starts after a real authentication failure
            // 403 real o proactivamente antes de que el token expire.
            // Proxy rotation is disabled for the final playlist request.
            resolved = session.LastResolved
                ?? throw new InvalidOperationException("Session has no resolved stream.");
            var isTokenized = !string.IsNullOrWhiteSpace(resolved.TokenValue);

            // Renovacion proactiva: solo cerca de la expiracion real del token (o cada 45 s si el
            // token no la declara). Evita el bucle de 403 + re-resolve en tokens de horas.
            if (isTokenized && !session.InGeneratorBackoff && ShouldProactivelyRenew(session))
            {
                PluginLog.Info("[HLS] Proactive token renewal (age threshold reached).");
                var pr = !string.IsNullOrWhiteSpace(session.TokenRenewalUrl)
                    ? await TryRenewPlaylistViaGeneratorAsync(session, cancellationToken).ConfigureAwait(false)
                    : null;
                if (pr is null && !session.InGeneratorBackoff && !string.IsNullOrWhiteSpace(session.UpstreamUrl))
                {
                    await TryReResolveOriginAsync(session, cancellationToken).ConfigureAwait(false);
                }

                resolved = session.LastResolved;
            }

            string playlistContent;
            string finalPlaylistUrl;
            var fetchTimer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                (playlistContent, finalPlaylistUrl) = await FetchPlaylistWithProxyRotationAsync(resolved.FinalUrl, isTokenized, session, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (IsAuthenticationFailure(ex) && isTokenized)
            {
                session.TokenKnownDead = true;

                // Solo los canales Tokenizado renuevan. Directo/FAST/Proxy:
                // un 403 es terminal para el token actual, no se re-resuelve.
                if (session.ChannelType != ChannelEntry.TokenizedType)
                {
                    PluginLog.Warn($"[HLS] Auth failure on non-tokenized channel (type={session.ChannelType}); not renewing.");
                    throw;
                }

                // Sin URL de generacion ni origen, no hay nada que renovar.
                if (string.IsNullOrWhiteSpace(session.TokenRenewalUrl) && string.IsNullOrWhiteSpace(session.UpstreamUrl))
                {
                    throw;
                }

                // Respect the generator's per-IP rate limit: after a failed renewal,
                // back off before trying again and keep serving the last valid playlist.
                if (session.InGeneratorBackoff
                    || (session.LastTokenRenewalAttemptAt is { } lastAttempt
                        && DateTimeOffset.UtcNow - lastAttempt < TokenRenewalCooldown))
                {
                    PluginLog.Warn($"[HLS] Token renewal in cooldown; serving stale cache ({(int?)ex.StatusCode}).");
                    throw;
                }

                session.LastTokenRenewalAttemptAt = DateTimeOffset.UtcNow;
                PluginLog.Warn($"[HLS] Final token rejected; renewing via {session.TokenRenewalUrl}");

                // Intento 1: renew against the token-generating redirect hop.
                var renewed = !string.IsNullOrWhiteSpace(session.TokenRenewalUrl)
                    ? await TryRenewPlaylistViaGeneratorAsync(session, cancellationToken).ConfigureAwait(false)
                    : null;
                if (renewed is null)
                {
                    // Intento 2: re-resolve from the original source using the same
                    // redirect chain that created the session.
                    if (!session.InGeneratorBackoff
                        && !string.IsNullOrWhiteSpace(session.UpstreamUrl)
                        && await TryReResolveOriginAsync(session, cancellationToken).ConfigureAwait(false))
                    {
                        PluginLog.Info("[HLS] Renewed via origin re-resolve; fetching fresh playlist.");
                        (playlistContent, finalPlaylistUrl) = await FetchPlaylistWithProxyRotationAsync(
                            session.LastResolved.FinalUrl, true, session, cancellationToken).ConfigureAwait(false);
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

            // Nunca servir HTML/JS como playlist: si el upstream devuelve algo que no es HLS
            // (token muerto que responde con HTML/login), descartarlo y usar la última buena.
            if (!LooksLikeHlsPlaylist(playlistContent))
            {
                PluginLog.Warn($"[HLS] Upstream returned non-HLS content from {finalPlaylistUrl}; serving stale cache.");
                throw new InvalidOperationException("Upstream returned non-HLS content.");
            }

            // Los masters multicapa (FAST) hacen lento el arranque: ffmpeg/Jellyfin eligen la
            // variante top (1080p) y procesan el master completo. Colapsar a UNA variante ligera
            // (~720p) reduce el trabajo de ffmpeg y acelera la primera imagen.
            var playlistForClient = playlistContent;
            string? demuxedMasterUrl = null;
            if (playlistContent.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                var originalMaster = playlistContent;
                var originalMasterUrl = finalPlaylistUrl;
                SourceCodecs.RecordFromMaster(session.UpstreamUrl, originalMaster);
                var mediaUrl = SelectBestVariantForRelay(playlistContent, finalPlaylistUrl);
                if (!string.IsNullOrWhiteSpace(mediaUrl))
                {
                    var mediaFetch = await FetchPlaylistWithProxyRotationAsync(mediaUrl, isTokenized, session, cancellationToken)
                        .ConfigureAwait(false);
                    if (LooksLikeHlsPlaylist(mediaFetch.Content))
                    {
                        // El análisis de ventana/segmentos (mas abajo) y la caché siempre usan la
                        // playlist real de la variante elegida, se la sirva tal cual al cliente o no.
                        playlistContent = mediaFetch.Content;
                        finalPlaylistUrl = mediaFetch.FinalUrl;
                        playlistForClient = playlistContent;

                        // Algunos proveedores separan el audio del video en una
                        // pista HLS propia, referenciada desde el master con AUDIO="...". Colapsar
                        // directo a la playlist de video, como se hacía antes, descartaba esa
                        // referencia por completo: el cliente se quedaba sin audio aunque el video (ya
                        // sin anuncios) funcionara bien. Con audio separado se sirve un master reducido
                        // (una sola variante + sus pistas de audio) para que el cliente pida
                        // la pista de audio por su cuenta; esa petición pasa por este mismo relay, así
                        // que también le quita los anuncios.
                        if (TryBuildReducedMasterForDemuxedTracks(originalMaster, originalMasterUrl, mediaUrl, out var reducedMaster))
                        {
                            playlistForClient = reducedMaster;

                            // La sesión debe seguir pidiendo el MASTER en cada sondeo, no la variante ya
                            // elegida: si se pinara en la variante (como hace el colapso normal), esta
                            // rama nunca volvería a ejecutarse y el audio desaparecería otra vez en el
                            // siguiente refresco de la lista.
                            demuxedMasterUrl = originalMasterUrl;
                        }
                    }
                }
            }

            var normalizedMedia = NormalizeTimeline(playlistContent, finalPlaylistUrl, session);
            if (string.Equals(playlistForClient, playlistContent, StringComparison.Ordinal))
            {
                playlistForClient = normalizedMedia;
            }

            playlistContent = normalizedMedia;
            UpdateResolvedFinalUrl(resolved, finalPlaylistUrl);
            session.TokenKnownDead = false;
            // Incluye master + variante y las renovaciones de token: es lo que espera un espectador cuando el caché expira.
            RelayMetrics.Time(session.SessionId, "playlist.upstreamMs", fetchTimer.Elapsed.TotalMilliseconds);
            PluginLog.Info($"[HLS] Fetched {playlistContent.Length} bytes OK from {finalPlaylistUrl}");

            var (windowSeconds, targetDurationSeconds) = AnalyzeMediaPlaylistWindow(playlistContent);
            session.LastPlaylistSegmentUrls = ExtractMediaSegmentUrls(playlistContent, resolved).ToArray();
            var segmentCount = session.LastPlaylistSegmentUrls.Count;

            session.LastPlaylistWindowSeconds = windowSeconds;
            session.LastPlaylistTargetDurationSeconds = targetDurationSeconds;

            // Token efímero: llega un token cada ~minuto y vive segundos. El reproductor arranca al inicio de la
            // ventana (retraso intencional) para tener qué reproducir hasta que llegue el siguiente token.
            var ephemeral = session.ChannelType == ChannelEntry.TokenizedType && !session.SessionToken;
            var window = windowSeconds ?? 0;
            // FAST/direct/session-token streams use the player's normal HLS hold-back.
            double? startOffset = ephemeral && window > 6 ? Math.Max(3, window - (2 * (targetDurationSeconds ?? 2))) : null;
            PluginLog.Info($"[HLS] Window={windowSeconds:F1}s TargetDur={targetDurationSeconds:F1}s Segments={segmentCount} StartOffset={startOffset?.ToString("F1", CultureInfo.InvariantCulture) ?? "client"}");

            // Re-apunta la sesión al master DESPUÉS del análisis de arriba (que necesitaba la base de la
            // variante real para resolver sus segmentos): la base para reescribir el master reducido es
            // la del master, no la de la variante.
            if (demuxedMasterUrl is not null)
            {
                UpdateResolvedFinalUrl(resolved, demuxedMasterUrl);
            }

            var rewritten = RewritePlaylist(playlistForClient, resolved, session.SessionId, proxyBaseUrl, startOffset);
            session.LastPlaylistContent = rewritten;
            session.LastPlaylistSourceUrl = resolved.FinalUrl;
            session.LastPlaylistFetchedAt = DateTimeOffset.UtcNow;
            PrefetchNewestSegments(session);

            return rewritten;
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(session.LastPlaylistContent))
        {
            // Keep the last playlist during provider throttling. The next request
            // after this short cooldown may obtain a fresh token through redirects.
            var age = DateTimeOffset.UtcNow - (session.LastPlaylistFetchedAt ?? DateTimeOffset.MinValue);
            if (IsAuthenticationFailure(ex))
            {
                session.LastPlaylistFetchedAt = DateTimeOffset.UtcNow;
            }

            RelayMetrics.Count(session.SessionId, IsAuthenticationFailure(ex) ? "playlist.staleServed.auth" : "playlist.staleServed");
            PluginLog.Warn($"[HLS] Fetch failed, serving stale cache (age={age.TotalSeconds:F0}s): {ex.Message}");
            return session.LastPlaylistContent;
        }
        finally
        {
            session.PlaylistFetchLock.Release();
        }
    }

    /// <summary>
    /// Downloads the newest segments of the playlist right away, while the token is still valid, so
    /// viewers get them from memory (instant start, and no freeze when an ephemeral token expires).
    /// </summary>
    private void PrefetchNewestSegments(StreamSession session)
    {
        var urls = session.LastPlaylistSegmentUrls;
        if (session.ChannelType != ChannelEntry.TokenizedType || session.SessionToken || urls is null || urls.Count == 0)
        {
            return;
        }

        // Todo lo nuevo de la ventana, del borde en vivo hacia atrás: el token efímero dura segundos y el
        // generador solo entrega uno por minuto, así que mientras sirve hay que dejar guardado lo que
        // el reproductor pedirá durante el hueco. Dos descargas a la vez como máximo por canal.
        foreach (var raw in urls.Reverse())
        {
            var url = UnwrapRedirectUrl(raw);
            session.SegmentCache.Prefetch(url, async () =>
            {
                await session.PrefetchGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await DownloadSegmentAsync(url, session).ConfigureAwait(false);
                }
                finally
                {
                    session.PrefetchGate.Release();
                }
            });
        }
    }

    /// <summary>
    /// Downloads one segment into memory (no retries, no token renewal).
    /// </summary>
    /// <param name="url">Upstream segment URL.</param>
    /// <param name="session">Relay session the segment belongs to; decides whether it is downloaded through an exit proxy.</param>
    /// <returns>The segment, or <c>null</c> when the download failed or is too large to cache.</returns>
    public async Task<CachedSegment?> DownloadSegmentAsync(string url, StreamSession? session = null)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = session is not null && UpstreamClientService.RequiresExit(session.ExitCountry)
                ? _upstream.For(session)
                : _httpClientFactory.CreateClient("JellyTV");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > SegmentCache.MaxSegmentBytes)
            {
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return data.Length is > 0 and <= SegmentCache.MaxSegmentBytes
                ? new CachedSegment(data, response.Content.Headers.ContentType?.ToString() ?? "video/MP2T")
                : null;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[JellyTV] Segment prefetch failed for {Url}", PluginLog.Redact(url));
            }

            return null;
        }
    }

    private static bool IsAuthenticationFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };

    /// <summary>
    /// Para masters multicapa elige la mejor variante &lt;= ~2.8 Mbps (~720p) para arranque rápido;
    /// si todas superan ese tope, la de mayor bitrate. Devuelve vacío si no hay variantes.
    /// </summary>
    private static string SelectBestVariantForRelay(string playlist, string playlistUrl)
    {
        const long targetBandwidth = 2_800_000;
        string? bestBelow = null;
        long bestBelowBandwidth = -1;
        string? bestAny = null;
        long bestAnyBandwidth = -1;
        string? pendingHeader = null;

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                pendingHeader = line;
                continue;
            }

            if (pendingHeader is not null && line.Length > 0 && line[0] != '#')
            {
                var bandwidth = TryParseBandwidthValue(pendingHeader);
                if (bandwidth > bestAnyBandwidth)
                {
                    bestAnyBandwidth = bandwidth;
                    bestAny = line;
                }

                if (bandwidth <= targetBandwidth && bandwidth > bestBelowBandwidth)
                {
                    bestBelowBandwidth = bandwidth;
                    bestBelow = line;
                }

                pendingHeader = null;
            }
        }

        var chosen = bestBelow ?? bestAny;
        if (chosen is null)
        {
            return string.Empty;
        }

        if (chosen.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || chosen.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return chosen;
        }

        return new Uri(new Uri(playlistUrl), chosen).ToString();
    }

    /// <summary>
    /// Cuando la variante elegida declara una pista de audio aparte (un <c>#EXT-X-MEDIA</c>
    /// con su propio <c>URI</c>, referenciada por el <c>AUDIO=</c> de la variante), arma
    /// un master mínimo con solo esa variante y sus pistas, para que el cliente las pida por su cuenta en
    /// vez de perderlas al colapsar directo a la playlist de video.
    /// </summary>
    /// <param name="originalMaster">El master tal como lo entregó el proveedor, antes de elegir variante.</param>
    /// <param name="masterBaseUrl">URL desde la que se pidió ese master, para resolver URIs relativas.</param>
    /// <param name="chosenVariantUrl">URL absoluta de la variante que escogió <see cref="SelectBestVariantForRelay"/>.</param>
    /// <param name="reducedMaster">El master reducido, cuando hizo falta uno.</param>
    /// <returns><c>true</c> si la variante tiene pistas aparte y se armó <paramref name="reducedMaster"/>.</returns>
    private static bool TryBuildReducedMasterForDemuxedTracks(string originalMaster, string masterBaseUrl, string chosenVariantUrl, out string reducedMaster)
    {
        reducedMaster = string.Empty;
        string? streamInfLine = null;
        string? pendingHeader = null;
        foreach (var raw in originalMaster.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                pendingHeader = line;
                continue;
            }

            if (pendingHeader is not null && line.Length > 0 && line[0] != '#')
            {
                if (string.Equals(ResolveUri(line, masterBaseUrl), chosenVariantUrl, StringComparison.Ordinal))
                {
                    streamInfLine = pendingHeader;
                    break;
                }

                pendingHeader = null;
            }
        }

        if (streamInfLine is null)
        {
            return false;
        }

        var streamAttributes = ParseTagAttributes(streamInfLine);
        var audioGroup = streamAttributes.FirstOrDefault(a => a.Key.Equals("AUDIO", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(audioGroup))
        {
            // El audio ya va dentro de los segmentos del video: no hay nada que separar.
            return false;
        }

        var mediaLines = originalMaster.Split('\n')
            .Select(static l => l.Trim())
            .Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase)
                && ParseTagAttributes(l).Any(a => a.Key.Equals("TYPE", StringComparison.OrdinalIgnoreCase) && a.Value.Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
                && string.Equals(audioGroup, ParseTagAttributes(l).FirstOrDefault(a => a.Key.Equals("GROUP-ID", StringComparison.OrdinalIgnoreCase)).Value, StringComparison.Ordinal))
            .ToList();
        if (mediaLines.Count == 0)
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var mediaLine in mediaLines)
        {
            sb.AppendLine(mediaLine);
        }

        sb.AppendLine(streamInfLine);
        sb.AppendLine(chosenVariantUrl);
        reducedMaster = sb.ToString();
        return true;
    }

    private static long TryParseBandwidthValue(string streamInfLine)
    {
        const string marker = "BANDWIDTH=";
        var idx = streamInfLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return -1;
        }

        var start = idx + marker.Length;
        var end = start;
        while (end < streamInfLine.Length && char.IsAsciiDigit(streamInfLine[end]))
        {
            end++;
        }

        return end > start && long.TryParse(streamInfLine[start..end], out var bandwidth) ? bandwidth : -1;
    }

    /// <summary>
    /// Acepta solo contenido que parezca un playlist HLS (no HTML/JS de un gateway).
    /// </summary>
    private static bool LooksLikeHlsPlaylist(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var head = content.Length > 512 ? content[..512] : content;
        return head.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase)
               || head.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pre-fetches a fresh playlist in the background when ~80% of the current window
    /// has been consumed, so the next ffmpeg poll hits cache instead of making an upstream call.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    public async Task TryBackgroundRefreshPlaylistAsync(
        StreamSession session,
        string servedSegmentUrl,
        string proxyBaseUrl,
        CancellationToken ct)
    {
        if (session.IsPlaylistRefreshInProgress)
        {
            return;
        }

        if (session.LastPlaylistRefreshAttemptAt.HasValue
            && DateTimeOffset.UtcNow - session.LastPlaylistRefreshAttemptAt.Value < TimeSpan.FromSeconds(10))
        {
            return;
        }

        var segments = session.LastPlaylistSegmentUrls;
        if (segments is null || segments.Count == 0)
        {
            return;
        }

        var window = session.LastPlaylistWindowSeconds;
        if (!window.HasValue || window.Value <= 0)
        {
            return;
        }

        var idx = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (string.Equals(segments[i], servedSegmentUrl, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            return;
        }

        var targetDur = session.LastPlaylistTargetDurationSeconds ?? 2;
        var consumedFraction = (idx + 1) * targetDur / window.Value;
        if (consumedFraction < 0.8)
        {
            return;
        }

        session.IsPlaylistRefreshInProgress = true;
        session.LastPlaylistRefreshAttemptAt = DateTimeOffset.UtcNow;

        try
        {
            var resolved = session.LastResolved;
            if (resolved is null)
            {
                return;
            }

            var isTokenized = !string.IsNullOrWhiteSpace(resolved.TokenValue);
            var fetchUrl = resolved.FinalUrl;
            var (content, finalPlaylistUrl) = await FetchPlaylistWithProxyRotationAsync(fetchUrl, false, session, ct).ConfigureAwait(false);
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
            if (t.Length == 0 || t[0] == '#')
            {
                continue;
            }

            if (t.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            urls.Add(ResolveUri(t, resolved.BaseUrl));
        }

        return urls.ToArray();
    }

    private async Task<(string Content, string FinalUrl)> FetchPlaylistWithProxyRotationAsync(string playlistUrl, bool isTokenized, StreamSession? session, CancellationToken cancellationToken)
    {
        // use a fresh clean HttpClient. Shared cookies from the
        // IHttpClientFactory accumulate state across sessions and cause 403
        // from the final tokenized host even when the token is still valid.
        using var cleanHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 15,
            UseCookies = false
        };
        using var fallbackClient = new HttpClient(cleanHandler, disposeHandler: false);
        fallbackClient.Timeout = TimeSpan.FromSeconds(5);
        fallbackClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Plugin.EffectiveUserAgent());

        const int attempt = 1;
        cancellationToken.ThrowIfCancellationRequested();

        // Una fuente con salida por país se descarga siempre por un proxy de ese país; si no, los canales
        // tokenizados rotan por todo el pool y el resto va directo.
        var exitClient = session is not null && UpstreamClientService.RequiresExit(session.ExitCountry) ? _upstream.For(session) : null;
        var (proxyKey, rotatingClient) = isTokenized && exitClient is null ? _proxyPool.GetNextSegmentProxy() : (null, null);
        var client = exitClient ?? rotatingClient ?? fallbackClient;

        PluginLog.Info($"[HLS] Fetch attempt {attempt} from {playlistUrl} proxy={proxyKey ?? (exitClient is not null ? "salida " + session!.ExitCountry : "direct")} tokenized={isTokenized}");

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
            _logger.LogWarning(ex, "[JellyTV] Playlist fetch attempt {Attempt} failed for {Url}", attempt, PluginLog.Redact(playlistUrl));
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
                var colon = line.IndexOf(':', StringComparison.Ordinal);
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

    private static string NormalizeTimeline(string content, string sourceUrl, StreamSession session)
    {
        if (session.ChannelType != ChannelEntry.FastType)
        {
            return content;
        }

        return session.PlaylistTimelines.GetOrAdd(sourceUrl, static _ => new HlsTimeline()).Normalize(content, sourceUrl);
    }

    /// <summary>
    /// Rewrites an already downloaded HLS playlist using the same rules as the main stream path.
    /// This is used for nested playlists fetched through /JellyTV/segment.
    /// </summary>
    /// <returns></returns>
    public string RewritePlaylistContent(string content, string sourceUrl, string sessionId, string proxyBaseUrl, StreamSession? session = null)
    {
        if (session is not null)
        {
            content = NormalizeTimeline(content, sourceUrl, session);
        }

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
    /// <summary>
    /// Detects ad/beacon directives that carry no media and confuse ffmpeg/ffprobe when they point
    /// to endpoints the relay does not proxy.
    /// </summary>
    /// <param name="line">Playlist line.</param>
    /// <returns><c>true</c> when the line must be dropped.</returns>
    private static bool IsBeaconDirective(string line)
        => line.StartsWith("#EXT-X-DATERANGE", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-CUE-", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-OATCLS-SCTE35", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-SCTE35", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-AD", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-ASSET", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-BEACON", StringComparison.OrdinalIgnoreCase);

    private string RewritePlaylist(
        string content,
        ResolvedStream resolved,
        string sessionId,
        string proxyBaseUrl,
        double? startOffsetSeconds = null)
    {
        var sb = new StringBuilder();
        using var reader = new StringReader(content);

        var addedLiveStart = false;
        var trimmed = TryGetTrimInfo(content, out var firstKept);
        var uriIndex = 0;

        // #EXT-X-START es una etiqueta de playlist de medios. Un master reducido (demuxado de
        // audio, ver TryBuildReducedMasterForDemuxedTracks) ya trae su propio #EXT-X-STREAM-INF;
        // si se le inyecta #EXT-X-START antes de esa etiqueta, el demuxer HLS de ffmpeg clasifica
        // el documento como media playlist y luego revienta con "Master Playlist tag found in a
        // Media Playlist" al llegar al STREAM-INF -- reproducible con ffprobe. Por eso solo se
        // agrega cuando el contenido es realmente un media playlist.
        var isMasterPlaylist = content.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);
        var hasStartPreference = content.Contains("#EXT-X-START:", StringComparison.OrdinalIgnoreCase);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!addedLiveStart && line.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                sb.AppendLine(line);
                if (!isMasterPlaylist && !hasStartPreference && startOffsetSeconds is { } offset)
                {
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"#EXT-X-START:TIME-OFFSET=-{offset:F1},PRECISE=YES"));
                }

                addedLiveStart = true;
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                // Do not expose optional subtitle playlists: a failed VTT can abort the entire input.
                if (ParseTagAttributes(line).Any(a => a.Key.Equals("TYPE", StringComparison.OrdinalIgnoreCase)
                    && a.Value.Equals("SUBTITLES", StringComparison.OrdinalIgnoreCase)))
                {
                    RelayMetrics.Count(sessionId, "clean.subtitleDropped");
                    continue;
                }

                line = RewriteMediaTrack(line, resolved, sessionId, proxyBaseUrl);
                sb.AppendLine(line);
                continue;
            }

            // Ad/beacon markers make ffmpeg/ffprobe chase interstitials that the relay does not
            // serve. They carry no media, so they are dropped from the clean playlist.
            if (IsBeaconDirective(line))
            {
                RelayMetrics.Count(sessionId, "clean.tagDropped");
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    var attributes = ParseTagAttributes(line);
                    attributes.RemoveAll(a => a.Key.Equals("SUBTITLES", StringComparison.OrdinalIgnoreCase));
                    line = BuildTagLine(line, attributes);
                }

                if (trimmed && line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim();
                    line = long.TryParse(value, out var sequence)
                        ? $"#EXT-X-MEDIA-SEQUENCE:{sequence + firstKept}"
                        : line;
                }

                // Las cabeceras y las claves se conservan siempre; las etiquetas de segmentos
                // antiguos se descartan junto con su segmento.
                if (trimmed && uriIndex < firstKept && !IsPlaylistHeaderTag(line))
                {
                    continue;
                }

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
            var keep = !trimmed || uriIndex >= firstKept;
            uriIndex++;
            if (!keep)
            {
                continue;
            }

            var absoluteUri = ResolveUri(line, resolved.BaseUrl);
            var proxiedUri = BuildProxiedSegmentUrl(absoluteUri, sessionId, proxyBaseUrl);

            if (Plugin.Instance?.Configuration.VerboseLogging == true && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[JellyTV] Rewrote segment: {Original} -> {Proxied}", PluginLog.Redact(line), PluginLog.Redact(proxiedUri));
            }

            sb.AppendLine(proxiedUri);
        }

        if (trimmed)
        {
            RelayMetrics.Count(sessionId, "clean.dvrTrimmed");
            PluginLog.Info($"[HLS] Playlist recortada a la ventana en vivo: {firstKept} segmentos antiguos descartados");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Detects an oversized DVR window and computes how many leading segments must be dropped so the
    /// player receives a normal live window instead of hours of history.
    /// </summary>
    /// <param name="content">Upstream playlist.</param>
    /// <param name="firstKept">Index of the first segment to keep.</param>
    /// <returns><c>true</c> when the playlist must be trimmed.</returns>
    private static bool TryGetTrimInfo(string content, out int firstKept)
    {
        firstKept = 0;
        if (content.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        double targetDuration = 6;
        var segments = 0;
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(line["#EXT-X-TARGETDURATION:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                targetDuration = Math.Clamp(parsed, 1, 30);
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                segments++;
            }
        }

        // Ventana objetivo: ~60 s de vivo, entre 6 y 20 segmentos.
        var keep = Math.Clamp((int)Math.Ceiling(60.0 / targetDuration), 6, 20);
        if (segments <= keep)
        {
            return false;
        }

        firstKept = segments - keep;
        return true;
    }

    private static bool IsPlaylistHeaderTag(string line)
        => line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-VERSION", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-TARGETDURATION", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-INDEPENDENT-SEGMENTS", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-START", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-PLAYLIST-TYPE", StringComparison.OrdinalIgnoreCase);

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
    /// Rewrites audio/subtitle track declarations so the preferred Spanish audio becomes default.
    /// Subtitles remain optional but are rewritten through the proxy when present.
    /// </summary>
    private string RewriteMediaTrack(string tagLine, ResolvedStream resolved, string sessionId, string proxyBaseUrl)
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
        var colonIndex = tagLine.IndexOf(':', StringComparison.Ordinal);
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
        var colonIndex = tagLine.IndexOf(':', StringComparison.Ordinal);
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
    private string BuildProxiedSegmentUrl(string upstreamUrl, string sessionId, string proxyBaseUrl)
    {
        // Limpieza de beacons (FAST): el playlist puede listar el segmento como beacon/redirect
        // (p. ej. beacons.example/hlsstream/v1/beacon?url=<real>). Se sirve la URL real
        // para que el cliente nunca toque la basura de redirección/telemetría.
        var cleaned = UnwrapRedirectUrl(upstreamUrl);
        if (!ReferenceEquals(cleaned, upstreamUrl))
        {
            RelayMetrics.Count(sessionId, "clean.beaconUnwrapped");
        }

        AllowHost(sessionId, cleaned);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(cleaned))
            .Replace('+', '-').Replace('/', '_').TrimEnd('='); // URL-safe Base64
        var suffix = GetProxySuffix(cleaned);

        var prefix = $"{proxyBaseUrl}/segment.{suffix}?sid={Uri.EscapeDataString(sessionId)}&url=";
        var keyQuery = RelayKeyQuery();
        // FFmpeg reads HLS lines into a fixed buffer; Base64 expands signed JWT URLs past it.
        if (Encoding.UTF8.GetByteCount(prefix + encoded + keyQuery) > 1800)
        {
            encoded = _relayUrls.Store(sessionId, cleaned);
        }

        return prefix + encoded + keyQuery;
    }

    private static void AllowHost(string sessionId, string url)
    {
        Plugin.Instance?.SessionManager?.GetSessionById(sessionId)?.AllowHostFromUrl(url);
    }

    private static string RelayKeyQuery()
    {
        var relayKey = Plugin.Instance?.Configuration?.RelayKey;
        return string.IsNullOrWhiteSpace(relayKey) ? string.Empty : $"&key={Uri.EscapeDataString(relayKey)}";
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

            // FFmpeg rejects WebVTT bytes behind a .ts URL, aborting the whole HLS input.
            if (path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".webvtt", StringComparison.OrdinalIgnoreCase))
            {
                return "vtt";
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
            if (Uri.TryCreate(session.TokenRenewalUrl, UriKind.Absolute, out var renewalUri))
            {
                // Muchos generadores exigen Referer del propio sitio; sin él responden 403.
                request.Headers.Referrer = new Uri(renewalUri.GetLeftPart(UriPartial.Authority) + "/");
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                PluginLog.Warn($"[HLS] Generator rejected renewal with {(int)response.StatusCode}.");
                session.LastTokenRenewalAttemptAt = DateTimeOffset.UtcNow;
                session.RecordGeneratorRejection();
                return null;
            }

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? session.TokenRenewalUrl!;
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Gateway HTML (p.ej. con reproductor embebido): el "generador" es una página de reproductor que embebe
            // el m3u8 con token. Se extrae del HTML ya descargado (sin otra petición) y, si no
            // aparece, se resuelve la página (iframe/meta/JS).
            if (!LooksLikeHlsPlaylist(content))
            {
                var embedded = RedirectResolverService.TryExtractEmbeddedPlaybackUrl(content, finalUrl);
                if (embedded is not null)
                {
                    var reused = session.LastResolved ?? new ResolvedStream { OriginalUrl = session.UpstreamUrl };
                    UpdateResolvedFinalUrl(reused, embedded);
                    session.LastResolved = reused;
                    session.LastTokenRenewedAt = DateTimeOffset.UtcNow;
                    session.LastTokenRenewalAttemptAt = DateTimeOffset.UtcNow;
                    session.IsInvalid = false;
                    InvalidatePlaylistCache(session);
                    var fetched = await FetchPlaylistWithProxyRotationAsync(embedded, true, session, cancellationToken).ConfigureAwait(false);
                    PluginLog.Info($"[HLS] Renewed from player HTML; final={embedded}");
                    return fetched;
                }

                PluginLog.Info($"[HLS] Generator returned HTML without embedded HLS (len={content.Length}); resolving player page.");
                var resolved = await _resolver.ResolveAsync(session.TokenRenewalUrl!, cancellationToken).ConfigureAwait(false);
                session.LastResolved = resolved;
                session.TokenRenewalUrl = resolved.TokenGeneratorHopUrl;
                session.LastTokenRenewedAt = DateTimeOffset.UtcNow;
                session.LastTokenRenewalAttemptAt = DateTimeOffset.UtcNow;
                session.IsInvalid = false;
                InvalidatePlaylistCache(session);
                var fresh = await FetchPlaylistWithProxyRotationAsync(resolved.FinalUrl, true, session, cancellationToken).ConfigureAwait(false);
                PluginLog.Info($"[HLS] Renewed via player page; final={resolved.FinalUrl}");
                return fresh;
            }

            if (session.LastResolved is { } resolvedStream)
            {
                UpdateResolvedFinalUrl(resolvedStream, finalUrl);
            }

            session.LastTokenRenewedAt = DateTimeOffset.UtcNow;
            session.LastTokenRenewalAttemptAt = DateTimeOffset.UtcNow;
            session.LastGeneratorRejectionAt = null;
            session.TokenKnownDead = false;
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
    /// Renueva el token re-resolviendo la cadena completa desde el origen, el mismo
    /// camino con el que arranco la sesion. Mas fiable que golpear directamente el hop
    /// generador cuando este rechaza la renovacion.
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

    private static bool ShouldProactivelyRenew(StreamSession session)
    {
        if (!ChannelEntry.IsTokenizedType(session.ChannelType)
            || session.SessionToken
            || session.LastResolved is not { TokenValue: not null and not "" } resolved)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        // Con expiración declarada (o inferida del par de epoch del token) se renueva solo cerca
        // del final; un token de horas no se toca, evitando el bucle 403 + re-resolve.
        if (resolved.TokenExpiry is { } expiry)
        {
            if (expiry - now > ProactiveRenewalMargin)
            {
                return false;
            }

            return session.LastTokenRenewedAt is not { } renewed || now - renewed >= TokenRenewalCooldown;
        }

        return session.LastTokenRenewedAt is { } last && now - last >= DefaultProactiveRenewalInterval;
    }

    private static void UpdateResolvedFinalUrl(ResolvedStream resolved, string finalUrl)
    {
        if (string.IsNullOrWhiteSpace(finalUrl) || !Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

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

        // Expiración declarada o inferida del par de epoch dentro del token.
        resolved.TokenExpiry = RedirectResolverService.TryParseTokenExpiry(finalUrl) ?? resolved.TokenExpiry;
    }

    /// <summary>
    /// Resolves a compact session-bound reference or a legacy Base64 upstream URL.
    /// </summary>
    /// <param name="sessionId">Session that published the URL.</param>
    /// <param name="encoded">Reference or URL-safe Base64 value.</param>
    /// <returns>The complete upstream URL.</returns>
    public string DecodeRelayUrl(string sessionId, string encoded)
        => encoded.StartsWith("ref.", StringComparison.Ordinal)
            ? _relayUrls.Resolve(sessionId, encoded)
            : DecodeSegmentUrl(encoded);

    /// <summary>
    /// Decodes a URL-safe Base64-encoded upstream URL back to its original form.
    /// </summary>
    /// <returns></returns>
    public static string DecodeSegmentUrl(string encoded)
    {
        // Re-pad and restore standard Base64 chars
        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        var pad = base64.Length % 4;
        if (pad == 2)
        {
            base64 += "==";
        }
        else if (pad == 3)
        {
            base64 += "=";
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    /// <summary>
    /// Des-envuelve URLs beacon/redirect de segmentos (p. ej. SSAI
    /// beacons.example/hlsstream/v1/beacon?url=&lt;real&gt;) devolviendo la URL real del
    /// contenido. Si no parece beacon, devuelve la URL sin cambios.
    /// </summary>
    /// <param name="url">URL del segmento tal como aparece en el playlist.</param>
    /// <returns>La URL real del contenido o la original si no es un beacon.</returns>
    public static string UnwrapRedirectUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var isBeacon = uri.Host.Contains("beacon", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/beacon", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/redirect", StringComparison.OrdinalIgnoreCase);
        if (!isBeacon)
        {
            return url;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var target = query["url"];
        if (string.IsNullOrWhiteSpace(target))
        {
            return url;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            return url;
        }

        return target;
    }

    private record struct TagAttribute(string Key, string Value, bool WasQuoted);
}
