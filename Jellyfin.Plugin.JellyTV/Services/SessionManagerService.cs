using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Manages active stream sessions and handles proactive token renewal
/// before expiry to prevent playback interruptions.
/// </summary>
public class SessionManagerService : BackgroundService
{
    private const int CleanupEveryTicks = 5;

    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly ConcurrentDictionary<string, StreamSession> _sessionsById = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<StreamSession>>> _sessionCreations = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _resolveFailures = new();
    private readonly RedirectResolverService _resolver;
    private readonly ILogger<SessionManagerService> _logger;
    private readonly UpstreamClientService _upstream;

    // Sessions inactive longer than this are cleaned up
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How often the background poller looks for channels that need a refresh.</summary>
    private static readonly TimeSpan PollTick = TimeSpan.FromSeconds(2);

    /// <summary>A channel counts as watched while a viewer touched it this recently.</summary>
    private static readonly TimeSpan ViewerIdleTimeout = TimeSpan.FromSeconds(45);

    // After a non-auth upstream failure (400, 5xx), wait this long before retrying
    private static readonly TimeSpan ResolveFailureBackoff = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionManagerService"/> class.
    /// </summary>
    public SessionManagerService(
        RedirectResolverService resolver,
        UpstreamClientService upstream,
        ILogger<SessionManagerService> logger)
    {
        _resolver = resolver;
        _upstream = upstream;
        _logger = logger;
    }

    /// <summary>
    /// Creates or retrieves a session for the given upstream URL (type-aware).
    /// channelType: 0=Direct, 1=FAST, 2=Tokenizado.
    /// Direct/FAST channels skip the token-chain resolution for instant start.
    /// </summary>
    /// <param name="upstreamUrl">Source URL.</param>
    /// <param name="channelType">Source type.</param>
    /// <param name="sessionToken">The source's session-token checkbox: <c>true</c> = token that lasts hours, <c>false</c> = ephemeral token.</param>
    /// <param name="exitCountry">Country whose proxies the session downloads through, or null to download directly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    public Task<StreamSession> GetOrCreateSessionAsync(string upstreamUrl, int channelType, bool sessionToken, string? exitCountry, CancellationToken ct = default)
    {
        var lazy = _sessionCreations.GetOrAdd(
            upstreamUrl,
            key => new Lazy<Task<StreamSession>>(
                () => CreateSessionAsync(key, channelType, sessionToken, exitCountry, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitSessionCreationAsync(upstreamUrl, lazy);
    }

    private async Task<StreamSession> AwaitSessionCreationAsync(
        string upstreamUrl,
        Lazy<Task<StreamSession>> lazy)
    {
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _sessionCreations.TryRemove(
                new KeyValuePair<string, Lazy<Task<StreamSession>>>(upstreamUrl, lazy));
            throw;
        }
    }

    private async Task<StreamSession> CreateSessionAsync(string upstreamUrl, int channelType, bool sessionToken, string? exitCountry, CancellationToken ct)
    {
        // Use URL as key so the same channel reuses its session
        var key = upstreamUrl;

        if (_sessions.TryGetValue(key, out var existing) && !existing.IsInvalid)
        {
            // Validate the cached session's UpstreamUrl matches the requested key.
            // If they differ the session was corrupted by the old code that overwrote UpstreamUrl
            // with an intermediate redirect hop. Evict and re-create.
            if (!string.Equals(existing.UpstreamUrl, key, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "[JellyTV] Evicting corrupted session {Id} (UpstreamUrl={Bad} != key={Key})",
                    existing.SessionId,
                    existing.UpstreamUrl,
                    key);
                _sessions.TryRemove(new KeyValuePair<string, StreamSession>(key, existing));
                _sessionsById.TryRemove(new KeyValuePair<string, StreamSession>(existing.SessionId, existing));
            }
            else
            {
                existing.LastAccessedAt = DateTimeOffset.UtcNow;
                existing.ExitCountry = exitCountry;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[JellyTV] Reusing session {Id} for {Url}", existing.SessionId, PluginLog.Redact(upstreamUrl));
                }

                return existing;
            }
        }

        // Reject immediately if the upstream recently failed (non-auth) to avoid spamming the provider.
        if (_resolveFailures.TryGetValue(key, out var failedAt) &&
            DateTimeOffset.UtcNow - failedAt < ResolveFailureBackoff)
        {
            var remaining = (int)(ResolveFailureBackoff - (DateTimeOffset.UtcNow - failedAt)).TotalSeconds;
            _logger.LogWarning("[JellyTV] Upstream {Url} in failure backoff ({Sec}s remaining), skipping resolve", PluginLog.Redact(upstreamUrl), remaining);
            throw new HttpRequestException(
                $"Upstream recently failed — backoff {remaining}s remaining.",
                null,
                System.Net.HttpStatusCode.BadGateway);
        }

        // El id deriva de la URL: tras un reinicio o una evicción los clientes siguen usando su id y el relay lo puede recrear.
        var session = new StreamSession { SessionId = StableId(upstreamUrl), UpstreamUrl = upstreamUrl, ChannelType = channelType, ExitCountry = exitCountry };

        // Lo decide el checkbox de la fuente (token de sesión = dura horas; sin marcar = token efímero).
        session.SessionToken = sessionToken;

        try
        {
            if (channelType is ChannelEntry.DirectType or ChannelEntry.FastType && !ProviderUrl.IsProviderUrl(upstreamUrl))
            {
                // Directo/FAST: sin cadena de tokens — usar la URL tal cual para arranque instantaneo.
                var isHls = upstreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
                var finalUrl = upstreamUrl;
                if (!isHls && UpstreamClientService.RequiresExit(exitCountry))
                {
                    // Una dirección sin extensión (un redirect de un servicio FAST) no dice qué es. Con salida por país el
                    // dispositivo nunca podría abrirla (está bloqueada para él), así que se descubre aquí, por el proxy.
                    (isHls, finalUrl) = await ProbeExitStreamAsync(upstreamUrl, exitCountry, ct).ConfigureAwait(false);
                }

                session.LastResolved = new ResolvedStream
                {
                    OriginalUrl = upstreamUrl,
                    FinalUrl = finalUrl,
                    BaseUrl = BuildBaseUrl(finalUrl),
                    IsHls = isHls,
                    ContentType = isHls ? "application/vnd.apple.mpegurl" : string.Empty,
                    ResolvedAt = DateTimeOffset.UtcNow,
                    RedirectChain = [upstreamUrl]
                };
                session.LastTokenRenewedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                session.LastResolved = await _resolver.ResolveAsync(upstreamUrl, ct).ConfigureAwait(false);
                session.TokenRenewalUrl = session.LastResolved.TokenGeneratorHopUrl;
                session.LastTokenRenewedAt = DateTimeOffset.UtcNow; // token emitido ahora
            }

            _resolveFailures.TryRemove(key, out _); // clear any previous failure on success
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is not (401 or 403))
        {
            // Non-auth failure (400, 5xx, network error) — record backoff so rapid retries don't spam upstream.
            _resolveFailures[key] = DateTimeOffset.UtcNow;
            _logger.LogWarning(
                "[JellyTV] Resolve failed for {Url} (status={Status}), backoff {Sec}s",
                upstreamUrl,
                (int?)ex.StatusCode,
                (int)ResolveFailureBackoff.TotalSeconds);
            throw;
        }

        session.AllowHostFromUrl(session.UpstreamUrl);
        session.AllowHostFromUrl(session.LastResolved?.FinalUrl);
        session.AllowHostFromUrl(session.LastResolved?.BaseUrl);
        _sessions[key] = session;
        _sessionsById[session.SessionId] = session;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[JellyTV] Created session {Id} for {Url}", session.SessionId, PluginLog.Redact(upstreamUrl));
        }

        return session;
    }

    /// <summary>
    /// Descarga el comienzo de una dirección por el proxy del país de salida para saber si es una lista HLS y a dónde
    /// redirige.
    /// </summary>
    private async Task<(bool IsHls, string FinalUrl)> ProbeExitStreamAsync(string url, string? exitCountry, CancellationToken ct)
    {
        using var client = _upstream.For(exitCountry, url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var isHls = mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || finalUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        if (!isHls)
        {
            var head = new byte[16];
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var read = await body.ReadAsync(head, timeout.Token).ConfigureAwait(false);
            isHls = read >= 7 && System.Text.Encoding.ASCII.GetString(head, 0, 7) == "#EXTM3U";
        }

        return (isHls, finalUrl);
    }

    /// <summary>
    /// Session id for a source URL: stable across restarts (not a secret; the relay key and the host check still apply).
    /// </summary>
    /// <param name="upstreamUrl">Session key (source URL).</param>
    /// <returns>32 hex characters.</returns>
    public static string StableId(string upstreamUrl) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(upstreamUrl)))[..32].ToLowerInvariant();

    /// <summary>
    /// Returns all active sessions for diagnostics.
    /// </summary>
    /// <returns></returns>
    public StreamSession[] GetAllSessions() => [.. _sessions.Values];

    /// <summary>
    /// Retrieves a session by its unique session ID.
    /// </summary>
    /// <returns></returns>
    public StreamSession? GetSessionById(string sessionId)
    {
        if (_sessionsById.TryGetValue(sessionId, out var session))
        {
            session.LastAccessedAt = DateTimeOffset.UtcNow;
            return session;
        }

        return null;
    }

    /// <summary>
    /// Forces an immediate token renewal for the given session.
    /// Thread-safe: only one renewal runs at a time per session.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    public async Task RenewSessionAsync(StreamSession session, CancellationToken ct = default)
    {
        // El guard anti-spam (8s) aplica solo a tokens de SESION larga (SessionToken=true):
        // renovarlos seguido martilla al proveedor sin beneficio. Los tokens EFIMEROS
        // (SessionToken=false, el proveedor los invalida a los ~20s) deben poder renovarse
        // con la frecuencia que pida el flujo en vivo.
        if (session.SessionToken
            && session.LastTokenRenewedAt is { } lastRenewed
            && DateTimeOffset.UtcNow - lastRenewed < TimeSpan.FromSeconds(8))
        {
            return;
        }

        // El generador rechazó hace poco: reintentarlo por cada segmento 403 solo prolonga su límite.
        if (session.InGeneratorBackoff)
        {
            return;
        }

        await session.RenewalLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.IsRenewing)
            {
                return;
            }

            if (session.SessionToken
                && session.LastTokenRenewedAt is { } completedRenewal
                && DateTimeOffset.UtcNow - completedRenewal < TimeSpan.FromSeconds(8))
            {
                return;
            }

            await session.PlaylistFetchLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Guard: if this upstream recently failed, skip the HTTP call entirely
                // so PollOnceAsync doesn't hammer the provider on a 4-5s ErrorBackoff loop.
                var url = session.UpstreamUrl;
                if (_resolveFailures.TryGetValue(url, out var failedAt) &&
                    DateTimeOffset.UtcNow - failedAt < ResolveFailureBackoff)
                {
                    if (session.SessionToken)
                    {
                        var remaining = (int)(ResolveFailureBackoff - (DateTimeOffset.UtcNow - failedAt)).TotalSeconds;
                        _logger.LogWarning(
                            "[JellyTV] Session {Id} upstream in failure backoff ({Sec}s remaining), skipping renewal",
                            session.SessionId,
                            remaining);
                        return;
                    }

                    // Token efimero: el proveedor espera re-pisadas del origin; reintentar pese al
                    // backoff (limpiandolo) para no dejar la reproduccion cortada por un fallo previo.
                    _resolveFailures.TryRemove(url, out _);
                }

                session.IsRenewing = true;
                if (Plugin.Instance?.Configuration.VerboseLogging == true && _logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "[JellyTV] Renewing token for session {Id} upstream={Url}",
                        session.SessionId,
                        PluginLog.Redact(session.UpstreamUrl));
                }
                else if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "[JellyTV] Renewing token for session {Id} upstream={Url}",
                        session.SessionId,
                        PluginLog.Redact(session.UpstreamUrl));
                }

                if (!string.IsNullOrWhiteSpace(session.TokenRenewalUrl))
                {
                    var renewalUrl = session.TokenRenewalUrl;
                    var client = _resolver.CreateNoRedirectClient();
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, renewalUrl);
                            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            var location = response.Headers.Location?.ToString();
                            var htmlHop = false;
                            if (location is null && response.IsSuccessStatusCode &&
                                response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                                location = RedirectResolverService.TryExtractMetaRefreshUrl(html, renewalUrl)
                                           ?? RedirectResolverService.TryExtractEmbeddedPlaybackUrl(html, renewalUrl);
                                htmlHop = location is not null;
                            }

                            var nextUrl = ResolveUrl(renewalUrl, location);
                            var token = nextUrl is null ? null : ExtractToken(nextUrl);
                            if ((!htmlHop && (int)response.StatusCode != 302) || nextUrl is null || token is null)
                            {
                                throw new HttpRequestException($"Token renewal returned {(int)response.StatusCode} without a token redirect.", null, response.StatusCode);
                            }

                            var old = session.LastResolved ?? throw new InvalidOperationException("Session has no resolution.");
                            session.LastResolved = new ResolvedStream
                            {
                                OriginalUrl = old.OriginalUrl,
                                FinalUrl = nextUrl,
                                BaseUrl = BuildBaseUrl(nextUrl),
                                ContentType = old.ContentType,
                                IsHls = old.IsHls,
                                TokenValue = token,
                                TokenExpiry = ParseExpiry(nextUrl) ?? DateTimeOffset.UtcNow.AddSeconds(12),
                                PlaylistPollUrl = renewalUrl,
                                TokenGeneratorHopUrl = renewalUrl,
                                ResolvedAt = DateTimeOffset.UtcNow,
                                RedirectChain = old.RedirectChain,
                                ResolverProxyKey = old.ResolverProxyKey
                            };
                            session.AllowHostFromUrl(nextUrl);
                            session.IsInvalid = false;
                            session.LastTokenRenewedAt = DateTimeOffset.UtcNow;
                            InvalidatePlaylistCache(session);
                            _resolveFailures.TryRemove(session.UpstreamUrl, out _);
                            if (_logger.IsEnabled(LogLevel.Debug))
                            {
                                _logger.LogDebug("[JellyTV] Token renewal succeeded for session {Id}, attempt={Attempt}, generator={Generator}", session.SessionId, attempt, PluginLog.Redact(renewalUrl));
                            }

                            return;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                        {
                            if (_logger.IsEnabled(LogLevel.Debug))
                            {
                                _logger.LogDebug(ex, "[JellyTV] Token renewal attempt {Attempt}/3 failed for session {Id}", attempt, session.SessionId);
                            }

                            if (attempt < 3)
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(150 * Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false);
                            }
                        }
                    }
                }

                _logger.LogWarning("[JellyTV] Direct token renewal exhausted for session {Id}; re-resolving origin", session.SessionId);
                var resolved = await _resolver.ResolveAsync(session.UpstreamUrl, ct).ConfigureAwait(false);
                session.LastResolved = resolved;
                session.AllowHostFromUrl(resolved.FinalUrl);
                session.AllowHostFromUrl(resolved.BaseUrl);
                session.TokenRenewalUrl = resolved.TokenGeneratorHopUrl;
                session.IsInvalid = false;
                session.LastTokenRenewedAt = DateTimeOffset.UtcNow;
                InvalidatePlaylistCache(session);
                _resolveFailures.TryRemove(session.UpstreamUrl, out _);
            }
            finally
            {
                session.PlaylistFetchLock.Release();
            }
        }
        catch
        {
            session.IsInvalid = true;
            _resolveFailures[session.UpstreamUrl] = DateTimeOffset.UtcNow;
            throw;
        }
        finally
        {
            session.IsRenewing = false;
            session.RenewalLock.Release();
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

    private static string? ResolveUrl(string baseUrl, string? location) =>
        location is null ? null : (Uri.TryCreate(new Uri(baseUrl), location, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null);

    private static string? ExtractToken(string url)
    {
        var uri = new Uri(url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in new[] { "token", "access_token", "auth", "t", "key", "sig", "tokenid" })
        {
            if (!string.IsNullOrWhiteSpace(query[key]))
            {
                return query[key];
            }
        }

        var match = Regex.Match(uri.AbsolutePath, @"/hlsr/([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static DateTimeOffset? ParseExpiry(string url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        foreach (var key in new[] { "expires", "exp", "expiry", "end", "validuntil", "token_expiry" })
        {
            if (long.TryParse(query[key], out var value))
            {
                return DateTimeOffset.FromUnixTimeSeconds(value);
            }
        }

        return null;
    }

    private static string BuildBaseUrl(string url)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}{path[..(slash + 1)]}";
    }

    /// <summary>
    /// Returns remaining backoff time for a session upstream if currently throttled.
    /// </summary>
    /// <returns></returns>
    public bool TryGetRenewalBackoffRemaining(StreamSession session, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;

        if (!_resolveFailures.TryGetValue(session.UpstreamUrl, out var failedAt))
        {
            return false;
        }

        var elapsed = DateTimeOffset.UtcNow - failedAt;
        if (elapsed >= ResolveFailureBackoff)
        {
            return false;
        }

        remaining = ResolveFailureBackoff - elapsed;
        return true;
    }

    private static bool IsLikelyTokenizedUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("token=", StringComparison.OrdinalIgnoreCase)
               || url.Contains("access_token=", StringComparison.OrdinalIgnoreCase)
               || url.Contains("/hlsr/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildRenewalCandidates(StreamSession session)
    {
        // Only the stable origin URL. Intermediate chain hops all route
        // through the same token router and get the same cached/stale token on rapid
        // sequential requests. Trying them adds HTTP requests without improving the
        // recovery rate — and the burst triggers stricter CDN rate-limiting, extending
        // the outage from ~2 s (single candidate) to 5+ s and risking a temporary ban.
        yield return session.UpstreamUrl;
    }

    /// <summary>
    /// Checks whether the session needs proactive renewal.
    /// Tokenized HLS renewal is handled by window logic in /stream and reactive 401/403 retries.
    /// </summary>
    /// <returns></returns>
    public bool NeedsRenewal(StreamSession session)
    {
        // The provider owns token rotation. A new playlist request follows the
        // generator redirect and receives the current token automatically.
        return false;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[JellyTV] Session manager background service started.");

        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(PollTick, stoppingToken).ConfigureAwait(false);

            PollActiveSessions(stoppingToken);

            if (++tick % CleanupEveryTicks == 0)
            {
                await ProcessSessionsAsync().ConfigureAwait(false);
            }
        }

        _logger.LogInformation("[JellyTV] Session manager background service stopped.");
    }

    /// <summary>
    /// Keeps every relayed channel that has viewers fresh from a single place: one playlist refresh,
    /// one token renewal and one set of segment downloads per channel, whether it has 1 or 1,000
    /// viewers. Viewers only ever read the result from memory, so they can never burn tokens.
    /// </summary>
    private void PollActiveSessions(CancellationToken stoppingToken)
    {
        var hls = Plugin.Instance?.HlsRewriter;
        if (hls is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var session in _sessions.Values)
        {
            // Solo los tokens efímeros necesitan que alguien renueve y descargue por adelantado; el resto
            // (FAST, directo, token de sesión) se sirve a demanda sin tocar al proveedor de más.
            if (session.ChannelType != ChannelEntry.TokenizedType
                || session.SessionToken
                || session.LastResolved is not { IsHls: true }
                || session.IsInvalid
                || now - session.LastAccessedAt > ViewerIdleTimeout
                || !session.TryBeginPoll())
            {
                continue;
            }

            _ = PollSessionAsync(hls, session, stoppingToken);
        }
    }

    private async Task PollSessionAsync(HlsRewriterService hls, StreamSession session, CancellationToken ct)
    {
        try
        {
            // FetchAndRewritePlaylistAsync respeta el TTL del playlist: llamarla en cada tick solo
            // toca al proveedor cuando el playlist realmente caducó, y dispara el prefetch.
            await hls.FetchAndRewritePlaylistAsync(session, "/JellyTV", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Apagado del servidor.
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[JellyTV] Background refresh failed for session {Id}", session.SessionId);
            }
        }
        finally
        {
            session.EndPoll();
        }
    }

    private Task ProcessSessionsAsync()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _sessions)
        {
            var session = kvp.Value;

            // Una sesión sin espectadores no retiene memoria: la caché de segmentos se libera al minuto.
            if (now - session.LastAccessedAt > TimeSpan.FromSeconds(60))
            {
                session.SegmentCache.Clear();
            }

            // Remove stale sessions (no one has accessed them in SessionTimeout).
            if (now - session.LastAccessedAt > SessionTimeout)
            {
                _sessions.TryRemove(kvp);
                _sessionsById.TryRemove(new KeyValuePair<string, StreamSession>(session.SessionId, session));
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("[JellyTV] Cleaned up idle session {Id}", session.SessionId);
                }
            }

            // NOTE: Token renewal is handled on-demand in two places:
            //   1. JellyTVController.GetStream  — when a channel is opened.
            //   2. BroadcastPump.PollOnceAsync  — before every playlist fetch while streaming.
            // We intentionally do NOT proactively renew here because doing so would
            // keep hitting the upstream provider for sessions that are idle between
            // plays, and would trigger renewal for every session every 5 minutes
            // regardless of whether anyone is actually watching.
        }

        return Task.CompletedTask;
    }
}
