using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.ScheduledTask;
using Jellyfin.Plugin.JellyTV.Services;
using Jellyfin.Plugin.JellyTV.Services.Identity;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTV.Controllers;

/// <summary>
/// Exposes the plugin relay endpoints, diagnostics and channel management API.
/// </summary>
[ApiController]
[Route("JellyTV")]
public class JellyTVController : ControllerBase
{
    private const string HlsMimeType = "application/vnd.apple.mpegurl";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool IsHls, DateTimeOffset At)> HlsProbeCache = new(StringComparer.Ordinal);

    private static int _probeRunning;
    private static string _probeProgress = string.Empty;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITaskManager? _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTVController"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory used for upstream requests.</param>
    /// <param name="taskManager">Optional Jellyfin task manager.</param>
    public JellyTVController(IHttpClientFactory httpClientFactory, ITaskManager? taskManager = null)
    {
        _httpClientFactory = httpClientFactory;
        _taskManager = taskManager;
    }

    private static Plugin P => Plugin.Instance!;

    private static SessionManagerService Sessions => P.SessionManager;

    private static HlsRewriterService HlsRewriter => P.HlsRewriter;

    private static UpstreamClientService Upstream => P.Upstream;

    /// <summary>
    /// Persists the full plugin configuration (channels, proxies, EPG sources and settings).
    /// The config page sends its whole object here so changes are written to disk through the
    /// same path the plugin uses internally (Plugin.SaveConfiguration) and are visible again on
    /// the next read (no stale in-memory copy).
    /// </summary>
    [HttpPost("config")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult SaveConfig([FromBody] PluginConfiguration incoming)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || incoming is null)
        {
            return BadRequest(new { error = "Plugin no inicializado" });
        }

        var live = plugin.Configuration;
        var previousRelayKey = live.RelayKey;
        foreach (var prop in typeof(PluginConfiguration).GetProperties())
        {
            if (prop.CanRead && prop.CanWrite)
            {
                prop.SetValue(live, prop.GetValue(incoming));
            }
        }

        if (string.IsNullOrWhiteSpace(live.RelayKey))
        {
            live.RelayKey = string.IsNullOrWhiteSpace(previousRelayKey)
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
                : previousRelayKey;
        }

        Plugin.NormalizeChannels(live, persist: false);

        // Si el administrador deja un canal habilitado explícitamente, se limpia el auto-disable
        // que puso el health check: su decisión manda.
        foreach (var channel in live.Channels.Where(channel => channel.IsEnabled))
        {
            channel.AutoDisabled = false;
        }

        try
        {
            plugin.SaveConfiguration();
            // Mantener el archivo tuner.m3u fresco en disco (el endpoint HTTP lo regenera con su host).
            RefreshPersistedTunerM3u();
            PluginLog.Info("[CONFIG] Configuración guardada correctamente.");
            return Ok(new { saved = true });
        }
        catch (Exception ex)
        {
            PluginLog.Error("[CONFIG] Error al guardar configuración", ex);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Rotates the relay key. Existing tuner/stream URLs stop working until the
    /// administrator updates the M3U tuner URL with the new key.
    /// </summary>
    /// <returns>The new relay key.</returns>
    [HttpPost("relay-key/rotate")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult RotateRelayKey()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return BadRequest(new { error = "Plugin no inicializado" });
        }

        plugin.Configuration.RelayKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            plugin.SaveConfiguration();
            RefreshPersistedTunerM3u();
            PluginLog.Info("[SECURITY] Relay key rotated.");
            return Ok(new { relayKey = plugin.Configuration.RelayKey });
        }
        catch (Exception ex)
        {
            PluginLog.Error("[SECURITY] Error rotating relay key", ex);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ── M3U TUNER (registrar en Jellyfin → Live TV → Tuner devices → M3U Tuner) ──
    // Expone los canales como playlist M3U consumible por el tuner M3U nativo. Cada canal apunta
    // a una URL de REFERENCIA del relay del plugin (/JellyTV/stream?url=...&ch=...): NUNCA se
    // emite la URL cruda registrada (en tokenizados es solo HTML/JS; además expira). El relay
    // resuelve en el momento (HTML→JS→m3u8+token) con failover por fuentes. La playlist se genera
    // con el host de la petición (lo alcanza el propio server) y se PERSISTE en
    // {DataPath}/JellyTV/tuner.m3u (igual que la EPG) para usarse como archivo o vía HTTP.

    /// <summary>
    /// Returns the combined M3U playlist for all enabled channels.
    /// </summary>
    /// <returns>The playlist as <c>application/x-mpegurl</c>.</returns>
    [HttpGet("m3u")]
    [AllowAnonymous]
    public IActionResult GetCombinedM3u([FromQuery] string? key = null)
    {
        if (!RelayAuthorized(key))
        {
            return Unauthorized();
        }

        var baseUrl = ResolveBaseUrl();
        Response.Headers["Cache-Control"] = "no-store";
        return Content(BuildM3uContent(baseUrl, asTs: false), "application/x-mpegurl", Encoding.UTF8);
    }

    /// <summary>
    /// Returns the M3U playlist persisted to disk for the native M3U tuner.
    /// </summary>
    /// <returns>The playlist as <c>application/x-mpegurl</c>.</returns>
    [HttpGet("tuner.m3u")]
    [AllowAnonymous]
    public IActionResult GetTunerM3u([FromQuery] string? key = null)
    {
        if (!RelayAuthorized(key))
        {
            return Unauthorized();
        }

        var baseUrl = ResolveBaseUrl();
        var content = BuildM3uContent(baseUrl, asTs: false);
        PersistTunerM3u(content);
        Response.Headers["Cache-Control"] = "no-store";
        return Content(content, "application/x-mpegurl", Encoding.UTF8);
    }

    private string ResolveBaseUrl()
    {
        // Detrás de un proxy que termina TLS, Request.Scheme llega como "http": ResolveScheme usa X-Forwarded-Proto
        // para no emitir enlaces http (bloqueados como contenido mixto por apps https externas).
        var configured = P.Configuration?.PublicStreamingBaseUrl;
        var baseUrl = !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : $"{JellyTVLiveTvService.ResolveScheme(Request)}://{Request.Host}{Request.PathBase}".TrimEnd('/');
        P.KnownBaseUrl ??= baseUrl;
        return baseUrl;
    }

    private static string BuildM3uContent(string baseUrl, bool asTs)
    {
        var all = P.Configuration?.Channels ?? new List<ChannelEntry>();
        var channels = new List<ChannelEntry>();
        foreach (var channel in all)
        {
            channel.EnsureMigratedSources();
            if (!channel.IsEnabled)
            {
                PluginLog.Info($"[M3U] Omitido '{channel.Name}': deshabilitado (auto={channel.AutoDisabled}).");
                continue;
            }

            var hasSource = channel.Sources.Any(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url))
                || !string.IsNullOrWhiteSpace(channel.Url);
            if (!hasSource)
            {
                PluginLog.Info($"[M3U] Omitido '{channel.Name}': sin fuentes habilitadas.");
                continue;
            }

            channels.Add(channel);
        }

        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var ch in channels)
        {
            var active = SelectActiveSourceForM3u(ch);
            if (active is null)
            {
                continue;
            }

            sb.Append("#EXTINF:-1 ");
            AppendM3uAttribute(sb, "tvg-id", ch.TvgId);
            AppendM3uAttribute(sb, "tvg-name", string.IsNullOrWhiteSpace(ch.TvgName) ? ch.Name : ch.TvgName);
            AppendM3uAttribute(sb, "tvg-country", ch.Country);
            AppendM3uAttribute(sb, "tvg-logo", ch.LogoUrl);
            AppendM3uAttribute(sb, "group-title", ch.Category);
            sb.Append(CultureInfo.InvariantCulture, $",{SanitizeM3uName(ch.Name)}\n");

            // Enrutado: TODOS los canales pasan por el RELAY del plugin (incluidos los Directo).
            // Antes los Direct (tipo 0) se emitían CRUDOS y Jellyfin los abría nativo/remoto
            // (probe + transcode) → arranque lento (~30s) frente a los relayados (~3s). El relay
            // unifica el arranque rápido (colapso de variante a 720p + segmentos por proxy con
            // remapeo de borde) para Direct, FAST y Tokenizado.
            // La playlist expuesta SIEMPRE apunta al relay (también los Directo): un cliente externo nunca recibe la URL del
            // proveedor, y el relay aporta failover entre fuentes, métricas y limpieza. Se pide relay=1 para que un Directo
            // no se redirija de vuelta al proveedor (el 302 solo aplica al Live TV nativo de Jellyfin).
            var streamUrl = BuildRelayStreamUrl(baseUrl, active.Url, ch.Id, asTs);

            if (P.Configuration?.VerboseLogging == true)
            {
                PluginLog.Info($"[M3U] {ch.Name}: relay ({PluginLog.Redact(active.Url)})");
            }

            sb.AppendLine(streamUrl);
        }

        return sb.ToString();
    }

    private static void PersistTunerM3u(string content)
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null || string.IsNullOrWhiteSpace(plugin.DataDirectory))
            {
                return;
            }

            Directory.CreateDirectory(plugin.DataDirectory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(plugin.DataDirectory, "tuner.m3u"), content, Encoding.UTF8);
            PluginLog.Info($"[M3U] tuner.m3u persistido en {System.IO.Path.Combine(plugin.DataDirectory, "tuner.m3u")}");
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[M3U] No se pudo persistir tuner.m3u: {ex.Message}");
        }
    }

    /// <summary>
    /// Regenera y persiste el archivo tuner.m3u con el estado actual de configuración, usando la
    /// base URL conocida (o localhost) sin depender de una petición HTTP. Lo usan la página de
    /// configuración y la tarea de mantenimiento programado tras un health check.
    /// </summary>
    public static void RefreshPersistedTunerM3u()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        PersistTunerM3u(BuildM3uContent(plugin.KnownBaseUrl ?? "http://localhost:8096", asTs: false));
    }

    private static ChannelSource? SelectActiveSourceForM3u(ChannelEntry channel)
    {
        var selected = Plugin.Instance?.ChannelCatalog?.SelectSource(channel);
        if (selected is not null)
        {
            return selected;
        }

        channel.EnsureMigratedSources();
        var enabled = channel.Sources.Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0)
        {
            return string.IsNullOrWhiteSpace(channel.Url) ? null : new ChannelSource { Url = channel.Url, Type = channel.Type };
        }

        return enabled.FirstOrDefault(s => s.IsAlive == true) ?? enabled[0];
    }

    private static string BuildRelayStreamUrl(string baseUrl, string upstreamUrl, string channelId, bool asTs)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(upstreamUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var mode = asTs ? "&ts=1" : string.Empty;
        var relayKey = P.Configuration?.RelayKey;
        var auth = string.IsNullOrWhiteSpace(relayKey) ? string.Empty : $"&key={Uri.EscapeDataString(relayKey)}";
        // Con extensión .m3u8: los reproductores basados en ExoPlayer deducen HLS solo por la extensión de la ruta (sin ella lo
        // tratan como progresivo/TS y fallan), igual que el Path del Live TV nativo. Modo ts=1 sí es un flujo continuo.
        var file = asTs ? "stream" : "stream.m3u8";
        return $"{baseUrl}/JellyTV/{file}?url={encoded}&ch={channelId}&relay=1{mode}{auth}";
    }

    private static void AppendM3uAttribute(StringBuilder sb, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.Append(CultureInfo.InvariantCulture, $"{name}=\"");
        foreach (var c in value)
        {
            if (c == '\"')
            {
                sb.Append("&quot;");
            }
            else if (c == '\r' || c == '\n' || c == ',')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        sb.Append("\" ");
    }

    private static string SanitizeM3uName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(c is '\r' or '\n' or ',' ? ' ' : c);
        }

        return sb.ToString();
    }

    // ── STREAM ───────────────────────────────────────────────────────────────
    // Llamado por ffmpeg/Jellyfin para abrir un canal.
    // url = URL-safe Base64 de la URL original del canal.

    /// <summary>
    /// Opens a channel stream through the plugin relay.
    /// </summary>
    /// <param name="url">URL-safe Base64 encoded upstream URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="key">Relay key required by the endpoint.</param>
    /// <returns>The relayed stream response.</returns>
    [HttpGet("stream")]
    [HttpGet("stream.m3u8")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStream([FromQuery] string url, CancellationToken cancellationToken, [FromQuery] string? key = null)
    {
        if (!RelayAuthorized(key))
        {
            return Unauthorized();
        }

        // Ensure KnownBaseUrl is set for ILiveTvService.
        ResolveBaseUrl();
        var openTimer = System.Diagnostics.Stopwatch.StartNew();

        // Decodificar Base64
        string decodedUrl;
        try
        {
            decodedUrl = HlsRewriterService.DecodeSegmentUrl(url);
        }
        catch
        {
            decodedUrl = url;
        } // fallback si llega sin codificar

        // Resolver el canal y sus fuentes (por ?ch= o por coincidencia de URL). Cada fuente
        // puede tener su propio tipo; usamos el de la fuente que coincida con la URL pedida.
        var cfg = Plugin.Instance?.Configuration;
        var chParam = Request.Query["ch"].ToString();
        var channel = (!string.IsNullOrWhiteSpace(chParam)
                ? cfg?.Channels.FirstOrDefault(c => string.Equals(c.Id, chParam, StringComparison.Ordinal))
                : null)
            ?? cfg?.Channels.FirstOrDefault(c =>
                string.Equals(c.Url, decodedUrl, StringComparison.OrdinalIgnoreCase)
                || c.Sources.Any(s => string.Equals(s.Url, decodedUrl, StringComparison.OrdinalIgnoreCase)));

        // Candidatas en orden: la fuente pedida por el reproductor, después la activa persistida y
        // luego los respaldos vivos por prioridad. El catálogo lo resuelve en memoria (O(1) por canal).
        if (channel is null)
        {
            PluginLog.Warn($"[STREAM] Rejected unknown channel/url (ch='{chParam}')");
            return Forbid();
        }

        channel.EnsureMigratedSources();
        if (!channel.Sources.Any(s => string.Equals(s.Url, decodedUrl, StringComparison.Ordinal))
            && !string.Equals(channel.Url, decodedUrl, StringComparison.Ordinal))
        {
            // Playlist vieja en el cliente (fuente editada o quitada desde que la descargó): la URL pedida ya no existe, pero el canal
            // sí. Se sirve la fuente vigente del canal; nunca se descarga la URL recibida, así que el relay no sirve para URLs ajenas.
            var current = Plugin.Instance?.ChannelCatalog?.SelectSource(channel)?.Url;
            if (string.IsNullOrWhiteSpace(current))
            {
                PluginLog.Warn($"[STREAM] Rejected URL not configured for channel {channel.Id}, and it has no enabled source");
                return Forbid();
            }

            PluginLog.Info($"[STREAM] Playlist desactualizada para el canal {channel.Id}: se sirve su fuente vigente.");
            RelayMetrics.Count(SessionManagerService.StableId(current), "open.staleListUrl");
            decodedUrl = current;
        }

        var catalog = Plugin.Instance?.ChannelCatalog;
        var candidates = catalog?.OrderSources(channel, decodedUrl)
            ?? channel.Sources.Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();

        if (candidates.Count == 0)
        {
            return StatusCode(502, "No hay fuentes disponibles para este canal.");
        }

        // Entrega directa: si la fuente no necesita relay (Directo, o token fijo ya resuelto),
        // se responde 302 a la URL del proveedor para que Jellyfin haga Direct Play sin pasar
        // por el plugin. Evita polling, reescritura y cualquier riesgo de remux.
        var first = candidates[0];
        var forceRelay = string.Equals(Request.Query["relay"], "1", StringComparison.Ordinal);
        if (!forceRelay
            && !DeliveryPolicy.RequiresRelay(first)
            && (DeliveryPolicy.IsDirectMediaUrl(first.Url) || await IsHlsAsync(first.Url, cancellationToken).ConfigureAwait(false)))
        {
            PluginLog.Info($"[STREAM] Directo sin relay: redirigiendo a la fuente del canal {channel.Id}");
            RelayMetrics.Time(SessionManagerService.StableId(first.Url), "open.redirectMs", openTimer.Elapsed.TotalMilliseconds);
            return Redirect(first.Url);
        }

        // Failover al abrir: probamos cada fuente; si una no abre, pasamos a la siguiente y el
        // catálogo mueve el puntero activo para que la próxima petición no reintente la caída.
        foreach (var candidate in candidates)
        {
            try
            {
                var sourceUrl = candidate.Url;
                var sourceType = candidate.Type;

                // Tokenizado siempre vía HLS: el path .ts directo reconecta y se congela.
                if (ChannelEntry.IsTokenizedType(sourceType) && sourceUrl.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                {
                    sourceUrl = sourceUrl[..^3] + ".m3u8";
                    PluginLog.Info($"[STREAM] Tokenizado: normalizado .ts -> .m3u8: {sourceUrl}");
                }

                // ── .ts path: direct stream (solo Directo/FAST) ──
                if (sourceUrl.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                {
                    return await StreamTsDirectAsync(sourceUrl, candidate.ExitCountry, cancellationToken).ConfigureAwait(false);
                }

                // ── .m3u8 path: HLS playlist proxy (fetch, rewrite, cache) ──
                var session = await Sessions.GetOrCreateSessionAsync(sourceUrl, sourceType, candidate.SessionToken, candidate.ExitCountry, cancellationToken).ConfigureAwait(false);
                var resolved = session.LastResolved!;
                if (!resolved.IsHls)
                {
                    PluginLog.Info($"[STREAM] Non-HLS, redirecting to {resolved.FinalUrl}");
                    return Redirect(resolved.FinalUrl);
                }

                // ts=1 (emitido por tuner.m3u): entrega el canal como MPEG-TS continuo para que los
                // clientes nativos DirectPlay con perfil ts (0 ffmpeg). Sin ts=1 devuelve HLS.
                if (string.Equals(Request.Query["ts"], "1", StringComparison.Ordinal))
                {
                    return await StreamHlsAsTsAsync(session, cancellationToken).ConfigureAwait(false);
                }

                var rewritten = await HlsRewriter.FetchAndRewritePlaylistAsync(
                    session, GetProxyBaseUrl(), cancellationToken).ConfigureAwait(false);
                Response.Headers["Cache-Control"] = "no-cache, no-store";
                RelayMetrics.Time(session.SessionId, "open.relayMs", openTimer.Elapsed.TotalMilliseconds);
                return Content(rewritten, HlsMimeType, Encoding.UTF8);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                RelayMetrics.Count(SessionManagerService.StableId(candidate.Url), "open.fail");
                PluginLog.Warn($"[STREAM] Fuente no disponible, se prueba la siguiente: {candidate.Url} :: {ex.Message}");
                catalog?.MarkSourceUnavailable(channel, candidate.Id, "open-failure");
            }
            catch (Exception ex)
            {
                RelayMetrics.Count(SessionManagerService.StableId(candidate.Url), "open.fail");
                PluginLog.Warn($"[STREAM] Fuente con error, se prueba la siguiente: {candidate.Url} :: {ex.Message}");
                catalog?.MarkSourceUnavailable(channel, candidate.Id, "open-failure");
            }
        }

        PluginLog.Error("[STREAM] Todas las fuentes del canal fallaron al abrir");
        return StatusCode(502, "No hay fuentes disponibles para este canal.");
    }

    // ── SEGMENT ──────────────────────────────────────────────────────────────
    // Proxy de segmentos .ts y sub-playlists .m3u8.

    /// <summary>
    /// Proxies HLS segments and sub-playlists for an active session.
    /// </summary>
    /// <param name="sid">Session identifier.</param>
    /// <param name="url">URL-safe Base64 encoded upstream segment URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="key">Relay key required by the endpoint.</param>
    /// <param name="ext">Optional segment extension from the route.</param>
    /// <returns>The proxied segment or sub-playlist.</returns>
    [HttpGet("segment")]
    [HttpGet("segment.{ext}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSegment([FromQuery] string sid, [FromQuery] string url, CancellationToken cancellationToken, [FromQuery] string? key = null, [FromRoute] string? ext = null)
    {
        if (!RelayAuthorized(key))
        {
            return Unauthorized();
        }

        string upstreamUrl;
        try
        {
            upstreamUrl = HlsRewriter.DecodeRelayUrl(sid, url);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[SEGMENT] Bad URL encoding: {ex.Message}");
            return BadRequest("Invalid segment URL.");
        }

        if (Plugin.Instance?.Configuration.VerboseLogging == true)
        {
            PluginLog.Info($"[SEGMENT] sid={sid} upstream={upstreamUrl}");
        }

        var session = Sessions.GetSessionById(sid);
        if (session is null)
        {
            // El sid puede quedar obsoleto (sesión reemplazada/evictada) mientras el canal ya
            // tiene una sesión vigente sirviendo el mismo host upstream. En vez de fallar la
            // reproducción entera -- lo que hace que el cliente cierre y reabra el canal y
            // Jellyfin deje el ffmpeg anterior huérfano transcodificando para nadie -- se
            // recupera reutilizando esa sesión vigente.
            session = Sessions.GetAllSessions().FirstOrDefault(s => s.IsUrlAllowed(upstreamUrl))
                ?? await RecreateSessionAsync(sid, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                PluginLog.Warn($"[SEGMENT] Unknown session {sid}");
                RelayMetrics.Count(sid, "segment.unknownSession");
                return NotFound("Session expired. Reopen stream.");
            }

            PluginLog.Info($"[SEGMENT] Sesión {sid} no encontrada; recuperada via sesión vigente {session.SessionId}");
        }

        if (!session.IsUrlAllowed(upstreamUrl))
        {
            PluginLog.Warn($"[SEGMENT] Blocked URL host for session {sid}: {PluginLog.Redact(upstreamUrl)}");
            return Forbid();
        }

        if (upstreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            await session.SegmentFetchLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProxySubPlaylistAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                session.SegmentFetchLimiter.Release();
            }
        }

        session.LastAccessedAt = DateTimeOffset.UtcNow;

        // Un solo viaje al proveedor por segmento, sin importar cuántos clientes lo pidan: la primera
        // petición (o el prefetch del poller) descarga, las demás esperan esa misma descarga.
        if (string.IsNullOrWhiteSpace(Request.Headers["Range"].ToString()))
        {
            var segmentKey = HlsRewriterService.UnwrapRedirectUrl(upstreamUrl);
            var waitTimer = System.Diagnostics.Stopwatch.StartNew();
            session.SegmentCache.Prefetch(segmentKey, () => HlsRewriter.DownloadSegmentAsync(segmentKey));
            var cached = await session.SegmentCache.TryGetAsync(segmentKey).ConfigureAwait(false);
            if (cached is not null)
            {
                // Tiempo hasta tener el segmento entero en memoria: lo que el cliente espera antes del primer byte.
                RelayMetrics.Time(session.SessionId, "segment.firstByteMs", waitTimer.Elapsed.TotalMilliseconds);
                RelayMetrics.Count(session.SessionId, "segment.bytes", cached.Data.Length);
                session.LastSegmentServedAt = DateTimeOffset.UtcNow;
                return await ServeCachedSegmentAsync(cached, cancellationToken).ConfigureAwait(false);
            }
        }

        await session.SegmentFetchLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        var originTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await ProxySegmentAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);
            var status = result is StatusCodeResult failed ? failed.StatusCode : Response.StatusCode;
            RelayMetrics.Time(session.SessionId, "segment.originMs", originTimer.Elapsed.TotalMilliseconds);
            RelayMetrics.Count(session.SessionId, status is >= 200 and < 300 ? "segment.originOk" : $"segment.originStatus.{status}");
            return result;
        }
        finally
        {
            session.SegmentFetchLimiter.Release();
        }
    }

    /// <summary>
    /// Detects by content (not by extension) whether a Direct URL serves an HLS playlist, e.g. Plex-style URLs without
    /// ".m3u8". When it does, GetStream answers 302 to the provider instead of relaying every segment. Cached 30 min
    /// (5 min when negative); a failed probe falls back to the relay.
    /// </summary>
    private async Task<bool> IsHlsAsync(string url, CancellationToken ct)
    {
        if (HlsProbeCache.TryGetValue(url, out var hit) && DateTimeOffset.UtcNow - hit.At < (hit.IsHls ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5)))
        {
            return hit.IsHls;
        }

        var isHls = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _httpClientFactory.CreateClient("JellyTV").GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var head = new byte[7];
                using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var read = await body.ReadAsync(head, timeout.Token).ConfigureAwait(false);
                isHls = read == 7 && Encoding.ASCII.GetString(head) == "#EXTM3U";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PluginLog.Warn($"[STREAM] Sondeo HLS falló, se usa el relay: {ex.Message}");
        }

        HlsProbeCache[url] = (isHls, DateTimeOffset.UtcNow);
        return isHls;
    }

    /// <summary>
    /// Recreates the session behind a stable sid (plugin restart or idle eviction) from the configured sources, so a
    /// player that kept its playlist keeps playing instead of looping on 404.
    /// </summary>
    private static async Task<StreamSession?> RecreateSessionAsync(string sid, CancellationToken ct)
    {
        var source = Plugin.Instance?.Configuration.Channels
            .SelectMany(static c => c.Sources.Where(static s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)))
            .FirstOrDefault(s => string.Equals(SessionManagerService.StableId(NormalizeSessionUrl(s)), sid, StringComparison.Ordinal));
        if (source is null)
        {
            return null;
        }

        try
        {
            return await Sessions.GetOrCreateSessionAsync(NormalizeSessionUrl(source), source.Type, source.SessionToken, source.ExitCountry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PluginLog.Warn($"[SEGMENT] No se pudo recrear la sesión {sid}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Same normalization GetStream applies before creating a session (tokenized .ts → .m3u8).</summary>
    private static string NormalizeSessionUrl(ChannelSource source) =>
        ChannelEntry.IsTokenizedType(source.Type) && source.Url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            ? source.Url[..^3] + ".m3u8"
            : source.Url;

    // ── MEASUREMENT ──────────────────────────────────────────────────────────

    /// <summary>
    /// Everything measured per channel: what real viewers did (relay counters/percentiles) and the latest synthetic probes.
    /// </summary>
    /// <param name="ch">Optional channel id.</param>
    /// <returns>JSON report.</returns>
    [HttpGet("metrics")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetMetrics([FromQuery] string? ch = null)
    {
        var channels = Plugin.Instance?.Configuration.Channels
            .Where(c => string.IsNullOrEmpty(ch) || string.Equals(c.Id, ch, StringComparison.Ordinal))
            .Select(c =>
            {
                c.EnsureMigratedSources();
                return new
                {
                    id = c.Id,
                    name = c.Name,
                    sources = c.Sources.Where(static s => !string.IsNullOrWhiteSpace(s.Url)).Select(s => new
                    {
                        type = s.Type,
                        exit = s.ExitCountry,
                        enabled = s.IsEnabled,
                        relay = RelayMetrics.SnapshotOf(SessionManagerService.StableId(NormalizeSessionUrl(s))),
                        open = RelayMetrics.SnapshotOf(SessionManagerService.StableId(s.Url)),
                    }).ToList(),
                    probes = SyntheticProbe.Recent(c.Id).Take(3).ToList(),
                };
            }).ToList();
        return Ok(new { probeRunning = Volatile.Read(ref _probeRunning) == 1, probeProgress = _probeProgress, channels });
    }

    /// <summary>
    /// Runs the synthetic player against one channel (waits for the result) or, without <c>ch</c>, against all of them in the background.
    /// </summary>
    /// <param name="ch">Channel id; omit to probe every channel.</param>
    /// <param name="segments">Live-edge segments to download per channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe result, or 202 when the batch started.</returns>
    [HttpPost("probe")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> RunProbe([FromQuery] string? ch = null, [FromQuery] int segments = 3, CancellationToken cancellationToken = default)
    {
        var channels = Plugin.Instance?.Configuration.Channels ?? [];
        var baseUrl = ResolveBaseUrl();
        segments = Math.Clamp(segments, 1, 6);
        var http = _httpClientFactory.CreateClient("JellyTV");
        if (!string.IsNullOrEmpty(ch))
        {
            var one = channels.FirstOrDefault(c => string.Equals(c.Id, ch, StringComparison.Ordinal));
            if (one is null)
            {
                return NotFound("Canal desconocido.");
            }

            one.EnsureMigratedSources();
            return Ok(await SyntheticProbe.RunAsync(http, baseUrl, one, segments, cancellationToken).ConfigureAwait(false));
        }

        if (Interlocked.CompareExchange(ref _probeRunning, 1, 0) == 1)
        {
            return Conflict("Ya hay una medición en curso.");
        }

        var batch = channels.ToList();
        _ = RunProbeBatchAsync(batch, http, baseUrl, segments);
        return Accepted(new { started = batch.Count, hint = "GET /JellyTV/metrics para ver el avance y los resultados." });
    }

    private static async Task RunProbeBatchAsync(List<ChannelEntry> batch, HttpClient http, string baseUrl, int segments)
    {
        try
        {
            var done = 0;
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = 3 },
                async (channel, token) =>
                {
                    channel.EnsureMigratedSources();
                    await SyntheticProbe.RunAsync(http, baseUrl, channel, segments, token).ConfigureAwait(false);
                    _probeProgress = $"{Interlocked.Increment(ref done)}/{batch.Count}";
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[PROBE] Medición interrumpida: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _probeRunning, 0);
        }
    }

    // ── DIAGNOSTICS ──────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the active playback sessions for diagnostics.
    /// </summary>
    /// <returns>A plain-text session dump.</returns>
    [HttpGet("sessions")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetSessions()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"=== JellyTV Sessions @ {DateTimeOffset.Now:O} (cache de segmentos: {SegmentCache.GlobalBytes / 1024 / 1024} MB de {SegmentCache.GlobalMaxBytes / 1024 / 1024} MB) ===");
        var sessions = Plugin.Instance?.SessionManager?.GetAllSessions() ?? Array.Empty<StreamSession>();
        if (sessions.Length == 0)
        {
            sb.AppendLine("No active sessions.");
        }

        foreach (var s in sessions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"ID={s.SessionId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Upstream   = {PluginLog.Redact(s.UpstreamUrl)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  FinalUrl   = {PluginLog.Redact(s.LastResolved?.FinalUrl ?? "null")}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  IsHls      = {s.LastResolved?.IsHls}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Expiry     = {s.LastResolved?.TokenExpiry?.ToString("O") ?? "unknown"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  LastAccess = {s.LastAccessedAt:O}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Kind       = {(s.ChannelType == ChannelEntry.TokenizedType ? (s.SessionToken ? "token-de-sesion" : "token-efimero") : (s.ChannelType == ChannelEntry.FastType ? "fast" : "directo"))}");
            sb.AppendLine();
        }

        return Content(sb.ToString(), "text/plain", Encoding.UTF8);
    }

    /// <summary>
    /// Returns the tail of the plugin log file.
    /// </summary>
    /// <param name="lines">Number of trailing lines to return.</param>
    /// <returns>The requested log lines as plain text.</returns>
    [HttpGet("log")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetPluginLog([FromQuery] int lines = 500)
    {
        lines = Math.Clamp(lines, 20, 5000);

        try
        {
            var logPath = PluginLog.CurrentLogPath;
            if (string.IsNullOrWhiteSpace(logPath) || !System.IO.File.Exists(logPath))
            {
                return NotFound("No se encontro el log de JellyTV.");
            }

            var allLines = System.IO.File.ReadAllLines(logPath);
            var tail = allLines.Skip(Math.Max(0, allLines.Length - lines));

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"=== JellyTV log ({Path.GetFileName(logPath)}) ===");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Path: {logPath}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Lines: {Math.Min(allLines.Length, lines)}");
            sb.AppendLine();

            foreach (var line in tail)
            {
                sb.AppendLine(PluginLog.Redact(line));
            }

            return Content(sb.ToString(), "text/plain", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            PluginLog.Error("[LOG] Failed to read plugin log", ex);
            return StatusCode(500, "No se pudo leer el log de JellyTV.");
        }
    }

    // ── CHANNEL HEALTH & MANAGEMENT ──────────────────────────────────────────

    /// <summary>
    /// Searches the external identity (iptv-org channel: country, guide id, logo, website) of a channel name.
    /// </summary>
    /// <param name="q">Channel or network name.</param>
    /// <param name="country">Optional country code that narrows the search.</param>
    /// <param name="tvgId">Optional guide id of the channel (exact iptv-org id match).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The best candidates.</returns>
    [HttpGet("identity/search")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> SearchIdentity([FromQuery] string q, [FromQuery] string? country, [FromQuery] string? tvgId, CancellationToken ct)
    {
        var candidates = (await P.ChannelIdentity.SearchAsync(q, country, tvgId, ct).ConfigureAwait(false)).ToList();

        // Las guías locales (Pluto, Samsung, Plex, Tubi) no usan tvg-id sino su propio id de canal: se ofrecen como un candidato
        // más y decide el administrador. Nada se aplica solo.
        candidates.AddRange(GuideChannelCatalog.Search(P.EpgFilter.LoadGuideChannels(P.Configuration.EpgSources), q, country, tvgId));
        return Ok(candidates.OrderByDescending(static c => c.Score).ToList());
    }

    /// <summary>
    /// Fills the identity and the logo of every channel that has none, applying only confident matches.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many channels were updated.</returns>
    [HttpPost("identity/enrich")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> EnrichIdentities(CancellationToken ct)
    {
        var channels = P.Configuration.Channels;
        var (updated, skipped) = await P.ChannelIdentity.EnrichAsync(channels, ct).ConfigureAwait(false);
        if (updated > 0)
        {
            P.SaveConfiguration();
        }

        return Ok(new { updated, skipped });
    }

    /// <summary>
    /// Returns the active storage backend and the configured preference.
    /// </summary>
    /// <returns>The backend identifier, description and schema version.</returns>
    [HttpGet("storage")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetStorageInfo()
    {
        var plugin = Plugin.Instance;
        var storage = plugin?.Storage;
        return Ok(new
        {
            configured = plugin?.Configuration.StorageBackend ?? "Auto",
            backend = storage?.BackendName ?? "json",
            description = storage?.Description ?? "Archivos JSON",
            schemaVersion = storage?.SchemaVersion ?? 0,
            jsonFolder = storage?.JsonFolder,
            jsonFiles = storage?.JsonFiles ?? Array.Empty<string>(),
            jsonBackup = storage?.WritesJsonBackup ?? false
        });
    }

    /// <summary>
    /// Returns the configured channels with their health state.
    /// </summary>
    /// <param name="sourceId">Optional channel identifier to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The channel list and counters.</returns>
    [HttpGet("channels")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> GetChannels([FromQuery] string? sourceId = null, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Channels.Count == 0)
        {
            return Ok(new { channels = Array.Empty<object>(), channelCount = 0, aliveCount = 0, deadCount = 0 });
        }

        try
        {
            var channels = string.IsNullOrWhiteSpace(sourceId)
                ? config.Channels
                : config.Channels.Where(c => c.Id == sourceId).ToList();

            if (channels.Count == 0)
            {
                return NotFound(new { error = "Channel not found" });
            }

            var rows = new List<object>();
            var aliveCount = 0;
            var deadCount = 0;
            var healthCheck = Plugin.Instance?.ChannelHealthCheck;

            foreach (var ch in channels.Where(c => c.IsEnabled))
            {
                ChannelHealthStatus? health = null;
                if (healthCheck is not null)
                {
                    ch.EnsureMigratedSources();
                    foreach (var source in ch.Sources.Where(static s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)))
                    {
                        if (healthCheck.TryGetCachedChannelStatus(source.Url, out var cached) && (health is null || cached.IsAlive))
                        {
                            health = cached;
                        }
                    }
                }

                var status = health is null ? "unknown" : (health.IsAlive ? "alive" : "dead");
                if (status == "alive")
                {
                    aliveCount++;
                }
                else if (status == "dead")
                {
                    deadCount++;
                }

                rows.Add(new
                {
                    id = ch.Id,
                    name = ch.Name,
                    url = PluginLog.Redact(ch.Url),
                    type = ch.Type,
                    category = ch.Category,
                    isEnabled = ch.IsEnabled,
                    logoUrl = ch.LogoUrl,
                    tvgId = ch.TvgId,
                    lastCheckAlive = ch.LastCheckAlive,
                    lastCheckedAt = ch.LastCheckedAt,
                    status,
                    httpStatus = health?.HttpStatus,
                    bytesDownloaded = health?.BytesDownloaded,
                    streamType = health?.StreamType,
                    checkedAt = health?.CheckedAt,
                    durationMs = health?.DurationMs,
                    errorMessage = health?.ErrorMessage ?? ch.LastCheckError
                });
            }

            return Ok(new
            {
                channels = rows,
                channelCount = rows.Count,
                aliveCount,
                deadCount
            });
        }
        catch (Exception ex)
        {
            PluginLog.Error("[CHANNELS] Error fetching channels", ex);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Queues Jellyfin's guide refresh task.
    /// </summary>
    /// <returns>Status of the queue operation.</returns>
    [HttpPost("refresh-guide")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult RefreshGuide()
    {
        if (_taskManager is null)
        {
            return Ok(new { status = "no-task-manager" });
        }

        try
        {
            var guideTask = _taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshGuide", StringComparison.OrdinalIgnoreCase));
            if (guideTask is null)
            {
                return Ok(new { status = "task-not-found" });
            }

            _taskManager.Execute(guideTask, new TaskOptions());
            return Ok(new { status = "started", task = guideTask.Name });
        }
        catch (Exception ex)
        {
            return Ok(new { status = "error", error = ex.Message });
        }
    }

    /// <summary>
    /// Starts a channel health check.
    /// </summary>
    /// <param name="sourceId">Optional channel identifier to limit the check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The health check result summary.</returns>
    [HttpPost("health-check")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> StartHealthCheck([FromQuery] string? sourceId = null, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var healthCheck = Plugin.Instance?.ChannelHealthCheck;

        if (config is null || healthCheck is null)
        {
            return StatusCode(500, new { error = "Service not available" });
        }

        try
        {
            var channels = string.IsNullOrWhiteSpace(sourceId)
                ? config.Channels
                : config.Channels.Where(c => c.Id == sourceId).ToList();

            if (channels.Count == 0)
            {
                return NotFound(new { error = "No channels found" });
            }

            if (healthCheck.IsRunning)
            {
                return Conflict(new { error = "Ya hay una revisión de salud en curso" });
            }

            // Execute in background
            _ = Task.Run(
                async () =>
                {
                    var before = channels.ToDictionary(
                        channel => channel.Id,
                        channel => (Enabled: channel.IsEnabled, AutoDisabled: channel.AutoDisabled),
                        StringComparer.Ordinal);

                    await healthCheck.CheckConfiguredChannelsAsync("manual", channels, true, CancellationToken.None).ConfigureAwait(false);

                    try
                    {
                        Plugin.Instance?.SaveConfiguration();
                    }
                    catch
                    {
                    }

                    // El health check puede re-habilitar canales AutoDisabled: el archivo y la guía
                    // de Jellyfin deben reflejarlo de inmediato o el canal "vivo" no aparece en Live TV.
                    RefreshPersistedTunerM3u();
                    var changed = channels.Any(channel =>
                        before.TryGetValue(channel.Id, out var previous)
                        && (previous.Enabled != channel.IsEnabled || previous.AutoDisabled != channel.AutoDisabled));
                    if (changed && config.AutoRefreshGuideOnChange && _taskManager is not null)
                    {
                        PluginLog.Info("[HEALTH-CHECK] Cambios de estado detectados; encolando Refresh Guide de Jellyfin.");
                        GuideRefresh.Queue(_taskManager);
                    }
                },
                CancellationToken.None);

            return Ok(new { message = "Health check started in background", channelsCount = channels.Count });
        }
        catch (Exception ex)
        {
            PluginLog.Error("[HEALTH-CHECK] Error starting health check", ex);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>Avance de la revisión de salud en curso (o de la última terminada).</summary>
    /// <returns>Estado, fuentes comprobadas y totales.</returns>
    [HttpGet("health-check/status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetHealthCheckStatus()
    {
        var healthCheck = Plugin.Instance?.ChannelHealthCheck;
        return healthCheck is null ? StatusCode(500, new { error = "Service not available" }) : Ok(ProgressDto(healthCheck.GetProgress()));
    }

    /// <summary>Avance de la sincronización de la guía en curso (o de la última terminada).</summary>
    /// <returns>Fase, paso actual y total.</returns>
    [HttpGet("epg/sync/status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetEpgSyncStatus()
    {
        var plugin = Plugin.Instance;
        return plugin is null ? StatusCode(500, new { error = "Plugin no inicializado" }) : Ok(ProgressDto(plugin.EpgFilter.GetProgress()));
    }

    // Jellyfin serializa los registros en PascalCase; el panel lee estos nombres en camelCase.
    private static object ProgressDto(TaskProgress p) => new
    {
        running = p.Running,
        phase = p.Phase,
        detail = p.Detail,
        done = p.Done,
        total = p.Total,
        startedAt = p.StartedAt,
        finishedAt = p.FinishedAt,
        summary = p.Summary,
    };

    // ── EPG LOCAL ─────────────────────────────────────────────────────────────
    // La guía XMLTV la genera el propio plugin y la sirve aquí; el usuario
    // apunta el provider XMLTV de Jellyfin a {server}/JellyTV/epg.xml.

    /// <summary>Sirve la guía XMLTV fusionada generada localmente.</summary>
    [HttpGet("epg.xml")]
    [AllowAnonymous]
    public IActionResult ServeLocalEpg()
    {
        var path = Plugin.Instance?.EpgFilter.EpgFilePath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return NotFound(new { error = "Guía local no encontrada. Ejecuta la sincronización EPG primero." });
        }

        return PhysicalFile(path!, "application/xml");
    }

    /// <summary>
    /// Comprueba una dirección de guía: la pide como lo haría una sincronización y devuelve el formato detectado y los
    /// primeros programas, sin guardar nada.
    /// </summary>
    /// <param name="request">Dirección y nombre del canal.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Formato, total y primeros programas, o el motivo por el que no se reconoció nada.</returns>
    [HttpPost("epg/test-guide")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> TestGuide([FromBody] GuideTestRequest request, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin no inicializado" });
        }

        var probe = (request.Url ?? string.Empty).Replace("{", "x", StringComparison.Ordinal).Replace("}", "x", StringComparison.Ordinal);
        if (IsBlockedSourceUrl(probe))
        {
            return BadRequest(new { error = "La dirección debe ser una URL http(s) pública." });
        }

        var preview = await plugin.EpgFilter.PreviewGuideAsync(request.Url!, request.Name ?? string.Empty, ct).ConfigureAwait(false);
        return Ok(new
        {
            format = preview.Format,
            count = preview.Count,
            error = preview.Error,
            items = preview.Items.Select(static i => new { start = i.Start, stop = i.Stop, title = i.Title }),
        });
    }

    /// <summary>
    /// Cobertura de la guía por canal: cuántos programas hay, hasta cuándo y si alcanza para las próximas horas.
    /// </summary>
    /// <returns>Resumen y detalle por canal.</returns>
    [HttpGet("epg/coverage")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetEpgCoverage()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin no inicializado" });
        }

        var now = DateTime.UtcNow;
        var path = plugin.EpgFilter.EpgFilePath;
        var fileExists = !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path);
        var updated = fileExists ? System.IO.File.GetLastWriteTimeUtc(path) : (DateTime?)null;

        var report = plugin.EpgFilter.LoadReport();
        var rows = plugin.Configuration.Channels
            .OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                report.TryGetValue(EpgChannelMatcher.KeyFor(c), out var chosen);
                var coverage = c.IsEnabled && fileExists ? LocalEpgReader.GetCoverage(EpgChannelMatcher.KeyFor(c)) : new EpgCoverage(0, null, null);
                var hoursAhead = coverage.LastEndUtc is { } end ? Math.Max(0, (end - now).TotalHours) : 0;
                var state = !c.IsEnabled ? "disabled" : coverage.Count == 0 ? "none" : hoursAhead < 12 ? "short" : "ok";
                return new
                {
                    id = c.Id,
                    name = c.Name,
                    tvgId = c.TvgId,
                    enabled = c.IsEnabled,
                    programmes = coverage.Count,
                    until = coverage.LastEndUtc,
                    hoursAhead = Math.Round(hoursAhead, 1),
                    source = chosen?.Primary,
                    alternates = chosen?.Alternates ?? 0,
                    candidates = chosen?.Candidates ?? 0,
                    country = c.Country,
                    ownGuide = !string.IsNullOrWhiteSpace(c.EpgPageUrl),
                    state,
                };
            })
            .ToList();

        var enabled = rows.Count(static r => r.enabled);
        return Ok(new
        {
            guideExists = fileExists,
            guideUpdatedAt = updated,
            guideAgeHours = updated is { } u ? Math.Round((now - u).TotalHours, 1) : (double?)null,
            enabledChannels = enabled,
            withGuide = rows.Count(static r => r.state == "ok" || r.state == "short"),
            short_ = rows.Count(static r => r.state == "short"),
            withoutGuide = rows.Count(static r => r.state == "none"),
            channels = rows,
        });
    }

    /// <summary>Lanza en segundo plano la sincronización local de la guía (fuentes → filtro → TMDB → disco).</summary>
    /// <param name="clean">Si es <c>true</c>, antes limpia las cachés (copias de fuentes, títulos sin coincidencia, imágenes comprobadas).</param>
    /// <returns>Confirmación de que el sync arrancó.</returns>
    [HttpPost("epg/sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult StartEpgSync([FromQuery] bool clean = false)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin no inicializado" });
        }

        if (plugin.EpgFilter.IsRunning)
        {
            return Conflict(new { error = "Ya hay una sincronización en curso" });
        }

        if (clean)
        {
            plugin.EpgFilter.ResetCaches();
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = await plugin.EpgFilter.SyncLocalGuideAsync(CancellationToken.None).ConfigureAwait(false);
                    PluginLog.Info($"[EPG] Sync local terminado. success={result.Success}, tvgIds={result.TvgIdsFound}, sources={result.Sources.Count}");

                    // Inyección nativa: al terminar el sync refresca la guía de Jellyfin para que
                    // los programas nuevos aparezcan sin que el usuario toque ningún provider XMLTV.
                    if (result.Success && _taskManager is not null)
                    {
                        var guideTask = _taskManager.ScheduledTasks
                            .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshGuide", StringComparison.OrdinalIgnoreCase));
                        if (guideTask is not null)
                        {
                            _ = _taskManager.Execute(guideTask, new TaskOptions());
                            PluginLog.Info("[EPG] Refresh Guide encadenado tras el sync local.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error("[EPG] Sync local falló", ex);
                }
            },
            CancellationToken.None);

        return Ok(new { message = clean ? "Sync EPG iniciado desde cero (cachés limpiadas)" : "Sync EPG iniciado en segundo plano" });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the relay key required by the anonymous playlist/stream/segment endpoints.
    /// </summary>
    /// <param name="key">Key supplied by the caller.</param>
    /// <returns><c>true</c> when the key matches the configured one.</returns>
    private static bool RelayAuthorized(string? key)
    {
        var expected = P.Configuration?.RelayKey;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(key))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(key);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    /// <summary>
    /// Rejects non-HTTP(S) and link-local/metadata targets to avoid SSRF from the
    /// health-check endpoint. Loopback is also rejected because the plugin has no
    /// legitimate reason to probe the Jellyfin host itself.
    /// </summary>
    /// <param name="url">URL to validate.</param>
    /// <returns><c>true</c> when the URL must be rejected.</returns>
    private static bool IsBlockedSourceUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return true;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return true;
        }

        if (uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(uri.Host, out var address))
        {
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Prueba un proxy desde el servidor, que es desde donde se descargan los canales. Con una dirección descarga un
    /// trozo por ese proxy (lo que demuestra que llega al canal desde el país del proxy); sin ella solo comprueba que
    /// el proxy acepta conexiones.
    /// </summary>
    /// <param name="request">Proxy y dirección de prueba.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>El resultado de la prueba.</returns>
    [HttpPost("proxies/test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> TestProxy([FromBody] ProxyTestRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Host) || request.Port is < 1 or > 65535)
        {
            return BadRequest(new { error = "Indica el host y un puerto válido." });
        }

        var url = request.Url?.Trim();
        if (!string.IsNullOrEmpty(url)
            && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || IsBlockedSourceUrl(url)))
        {
            return BadRequest(new { error = "La dirección de prueba no es válida o no está permitida." });
        }

        var entry = new ProxyEntry
        {
            Host = request.Host.Trim(),
            Port = request.Port,
            UseHttps = request.UseHttps,
            Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username,
            Password = string.IsNullOrEmpty(request.Password) ? null : request.Password
        };
        return Ok(await P.ProxyPool.TestAsync(entry, string.IsNullOrEmpty(url) ? null : url, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Comprueba rápidamente si una fuente responde (HTTP 2xx con cuerpo) con un límite de 8s.
    /// La UI lo usa antes de guardar un canal para decidir si se guarda activo (ON) u OFF.
    /// </summary>
    [HttpPost("check-source")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> CheckSourceQuick([FromQuery] string url, [FromQuery] int type = 0, [FromQuery] string? exit = null, [FromQuery] bool sessionToken = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "url requerida" });
        }

        if (IsBlockedSourceUrl(url))
        {
            return BadRequest(new { error = "URL no permitida" });
        }

        // Una fuente de token de sesión no responde a un GET plano (el proveedor da 403): se comprueba igual que el chequeo programado,
        // si no, guardar el canal marcaba caídas fuentes que funcionan.
        if (sessionToken && ChannelEntry.IsTokenizedType(type) && Plugin.Instance?.ChannelHealthCheck is { } health)
        {
            var status = await health.CheckSessionTokenSourceAsync("check", url, type, ct).ConfigureAwait(false);
            return Ok(new { alive = status.IsAlive, status = status.HttpStatus, bytes = status.BytesDownloaded });
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            // Con salida por país la fuente se prueba por el mismo proxy con el que se reproducirá.
            var client = UpstreamClientService.RequiresExit(exit) ? Upstream.For(exit, url) : _httpClientFactory.CreateClient("JellyTV");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            long bytes = 0;
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var buffer = new byte[8192];
                while (bytes < 200_000)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    bytes += read;
                }
            }

            var alive = response.IsSuccessStatusCode && bytes > 0;
            PluginLog.Info($"[CHECK] {url} -> {(int)response.StatusCode} ({bytes} bytes) alive={alive}");
            return Ok(new { alive, status = (int)response.StatusCode, bytes });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { alive = false, status = 0, bytes = 0, error = "timeout" });
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[CHECK] Error comprobando {url}: {ex.Message}");
            return Ok(new { alive = false, status = 0, bytes = 0, error = ex.Message });
        }
    }

    private async Task<IActionResult> ProxySegmentAsync(StreamSession session, string upstreamUrl, CancellationToken ct)
    {
        var client = UpstreamClientService.RequiresExit(session.ExitCountry) ? Upstream.For(session) : _httpClientFactory.CreateClient("JellyTV");
        var fetchUrl = upstreamUrl;
        var authHandled = false;
        var remapTried = false;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            // Defensa ante beacons que lleguen por playlist cacheada/sub-playlist: servir la URL real.
            fetchUrl = HlsRewriterService.UnwrapRedirectUrl(fetchUrl);

            if (Plugin.Instance?.Configuration.VerboseLogging == true)
            {
                PluginLog.Info($"[SEGMENT] attempt={attempt} url={fetchUrl}");
            }

            var (response, errorResult) = await FetchSegmentResponseAsync(client, fetchUrl, attempt, ct).ConfigureAwait(false);
            if (errorResult is not null)
            {
                return errorResult;
            }

            if (response is null)
            {
                return StatusCode(502, "Segment fetch failed.");
            }

            if (Plugin.Instance?.Configuration.VerboseLogging == true)
            {
                PluginLog.Info($"[SEGMENT] HTTP {(int)response.StatusCode} attempt={attempt}");
            }

            if (response.IsSuccessStatusCode)
            {
                session.LastSegmentServedAt = DateTimeOffset.UtcNow;
                return await StreamSegmentToClientAsync(response, ct).ConfigureAwait(false);
            }

            var statusCode = response.StatusCode;
            response.Dispose();
            PluginLog.Warn($"[SEGMENT] Upstream error {(int)statusCode} attempt={attempt}");

            // 401/403: token rechazado → renovar (los guards anti-spam solo aplican a sesión larga;
            // los efímeros se renuevan libremente). No reintentar sin token nuevo.
            var isAuth = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            // 400/404/410: segmento en el borde de la ventana viva. algunos proveedores devuelven 400 una fracción
            // de segundo mientras publica el segmento más nuevo; otros (ventana ~24s) devuelven 404
            // cuando un segmento rota. Reintento breve + remapeo desde playlist fresco lo resuelven;
            // sin esto el error llega a ffmpeg y causa cortes + ráfagas de reintento.
            var isTransient = statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone;

            if (isAuth && !authHandled)
            {
                authHandled = true;
                try
                {
                    await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"[SEGMENT] Token refresh failed: {ex.Message}");
                }
            }

            if ((isAuth || isTransient) && !remapTried)
            {
                remapTried = true;
                try
                {
                    var remapped = await TryRemapSegmentUrlFromFreshPlaylistAsync(session, fetchUrl, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(remapped))
                    {
                        fetchUrl = remapped;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"[SEGMENT] Remap failed: {ex.Message}");
                }
            }

            // Si el error transitorio persiste (borde vivo aún no publicado), esperar un instante y
            // reintentar la misma URL: el segmento queda disponible en <1s. Fuera de transitorios se
            // devuelve el estado de una vez (evita martillar a un upstream realmente roto).
            if (isTransient && attempt < 4)
            {
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new EmptyResult();
                }

                continue;
            }

            return StatusCode((int)statusCode);
        }

        return StatusCode(502, "Segment unavailable.");
    }

    private async Task<IActionResult> ProxySubPlaylistAsync(StreamSession session, string subUrl, CancellationToken ct)
    {
        var client = UpstreamClientService.RequiresExit(session.ExitCountry) ? Upstream.For(session) : _httpClientFactory.CreateClient("JellyTV");
        var fetchUrl = HlsRewriterService.UnwrapRedirectUrl(subUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var content = await client.GetStringAsync(fetchUrl, timeout.Token).ConfigureAwait(false);
            var rewritten = HlsRewriter.RewritePlaylistContent(content, fetchUrl, session.SessionId, GetProxyBaseUrl(), session);
            Response.Headers["Cache-Control"] = "no-cache, no-store";
            return Content(rewritten, HlsMimeType, Encoding.UTF8);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[SUBPLAYLIST] Failed {fetchUrl}", ex);
            return StatusCode(502);
        }
    }

    private async Task<(HttpResponseMessage? Response, IActionResult? ErrorResult)> FetchSegmentResponseAsync(HttpClient client, string fetchUrl, int attempt, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
            var range = Request.Headers["Range"].ToString();
            if (!string.IsNullOrWhiteSpace(range))
            {
                request.Headers.TryAddWithoutValidation("Range", range);
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, new EmptyResult());
        }
        catch (OperationCanceledException)
        {
            PluginLog.Warn($"[SEGMENT] Upstream timeout attempt {attempt}");
            return (null, StatusCode(504, "Upstream timeout."));
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[SEGMENT] Network error attempt {attempt}", ex);
            return (null, StatusCode(502, ex.Message));
        }
    }

    private async Task<IActionResult> ServeCachedSegmentAsync(CachedSegment segment, CancellationToken ct)
    {
        Response.StatusCode = 200;
        Response.ContentType = segment.ContentType;
        Response.ContentLength = segment.Data.Length;
        await Response.Body.WriteAsync(segment.Data, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    private async Task<IActionResult> StreamSegmentToClientAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Response.StatusCode = (int)response.StatusCode;
        Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/MP2T";
        if (response.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = response.Content.Headers.ContentLength.Value;
        }

        CopyHeader(response, "Accept-Ranges");
        CopyHeader(response, "Content-Range");
        CopyHeader(response, "ETag");
        CopyHeader(response, "Last-Modified");
        CopyHeader(response, "Cache-Control");

        if (Plugin.Instance?.Configuration.VerboseLogging == true)
        {
            PluginLog.Info($"[SEGMENT] Streaming {Response.ContentLength ?? -1} bytes (HTTP {Response.StatusCode})");
        }

        using (response)
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await stream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        }

        return new EmptyResult();
    }

    private void CopyHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            Response.Headers[name] = string.Join(", ", values);
        }
        else if (response.Content.Headers.TryGetValues(name, out values))
        {
            Response.Headers[name] = string.Join(", ", values);
        }
    }

    private async Task<string?> TryRemapSegmentUrlFromFreshPlaylistAsync(StreamSession session, string failedSegmentUrl, CancellationToken ct)
    {
        var targetFileName = TryGetFileNameFromUrl(failedSegmentUrl);
        var finalUrl = session.LastResolved?.FinalUrl;

        if (string.IsNullOrWhiteSpace(targetFileName) || string.IsNullOrWhiteSpace(finalUrl))
        {
            return null;
        }

        try
        {
            var client = UpstreamClientService.RequiresExit(session.ExitCountry) ? Upstream.For(session) : _httpClientFactory.CreateClient("JellyTV");
            var topPlaylist = await client.GetStringAsync(finalUrl, ct).ConfigureAwait(false);

            var topBase = BuildBaseUrl(finalUrl);
            if (TryFindSegmentUrlByFileName(topPlaylist, topBase, targetFileName, out var directMatch))
            {
                return directMatch;
            }

            // If exact filename is no longer in the live window, fall back to the newest segment.
            if (TryFindLatestSegmentUrl(topPlaylist, topBase, out var topLatestSegment))
            {
                return topLatestSegment;
            }

            if (TryFindFirstSubPlaylistUrl(topPlaylist, topBase, out var subPlaylistUrl))
            {
                var mediaPlaylist = await client.GetStringAsync(subPlaylistUrl, ct).ConfigureAwait(false);
                var mediaBase = BuildBaseUrl(subPlaylistUrl);

                if (TryFindSegmentUrlByFileName(mediaPlaylist, mediaBase, targetFileName, out var nestedMatch))
                {
                    return nestedMatch;
                }

                if (TryFindLatestSegmentUrl(mediaPlaylist, mediaBase, out var nestedLatestSegment))
                {
                    return nestedLatestSegment;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[SEGMENT] Could not remap from fresh playlist: {ex.Message}");
        }

        return null;
    }

    private static bool TryFindSegmentUrlByFileName(string playlistContent, string baseUrl, string fileName, out string matchedUrl)
    {
        matchedUrl = string.Empty;

        using var reader = new StringReader(playlistContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var absolute = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(new Uri(baseUrl), line).ToString();

            var candidateName = TryGetFileNameFromUrl(absolute);
            if (!string.IsNullOrWhiteSpace(candidateName)
                && string.Equals(candidateName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                matchedUrl = absolute;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindFirstSubPlaylistUrl(string playlistContent, string baseUrl, out string subPlaylistUrl)
    {
        subPlaylistUrl = string.Empty;

        using var reader = new StringReader(playlistContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var absolute = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(new Uri(baseUrl), line).ToString();

            if (absolute.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                subPlaylistUrl = absolute;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindLatestSegmentUrl(string playlistContent, string baseUrl, out string latestSegmentUrl)
    {
        latestSegmentUrl = string.Empty;

        using var reader = new StringReader(playlistContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var absolute = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(new Uri(baseUrl), line).ToString();

            // Keep the last segment-like URI; this tracks the newest item in a media playlist.
            if (!absolute.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                latestSegmentUrl = absolute;
            }
        }

        return !string.IsNullOrWhiteSpace(latestSegmentUrl);
    }

    private static string? TryGetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return Path.GetFileName(uri.AbsolutePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Base de proxy para los playlists HLS, emitida como ruta RELATIVA de raíz (/JellyTV).
    /// Al estar dentro del contenido m3u8 que Jellyfin sirve al cliente (vía su proxy /Videos),
    /// el cliente resuelve esos links contra el host con el que está viendo (localhost, LAN o URL
    /// pública) — nunca se incrusta un host hardcodeado ni el de una petición interna.
    /// </summary>
    private static string GetProxyBaseUrl() => "/JellyTV";

    /// <summary>
    /// Convierte el HLS del upstream en un flujo TS contiguo: lee el media playlist en vivo y
    /// concatena los segmentos .ts en orden hacia el cliente. En canales tokenizados renueva los
    /// tokens efímeros (los que el proveedor invalida a los pocos segundos, p. ej. alfa.7057460)
    /// re-pisando el origin cuando el playlist deja de responder, para que la reproducción no se
    /// corte al expirar el token.
    /// </summary>
    private async Task<IActionResult> StreamHlsAsTsAsync(StreamSession session, CancellationToken ct)
    {
        Response.ContentType = "video/mp2t";
        Response.Headers["Cache-Control"] = "no-cache, no-store";
        var client = UpstreamClientService.RequiresExit(session.ExitCountry) ? Upstream.For(session) : _httpClientFactory.CreateClient("JellyTV");
        var isTokenized = ChannelEntry.IsTokenizedType(session.ChannelType);

        // Trae el playlist desde la URL vigente de la sesión. En tokenizado, si el fetch falla
        // (token efímero ya expirado), renueva la sesión re-pisando el origin y reintenta una vez.
        async Task<string> FetchPlaylistCoreAsync()
        {
            var url = session.LastResolved?.FinalUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new HttpRequestException("Sesión sin URL final resuelta.");
            }

            try
            {
                return await client.GetStringAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (isTokenized)
            {
                PluginLog.Warn($"[HLS-TS] Playlist no disponible ({ex.StatusCode}); renovando token efímero.");
                try
                {
                    await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
                }
                catch (Exception renewEx)
                {
                    PluginLog.Warn($"[HLS-TS] Renovación de token falló: {renewEx.Message}");
                }

                var fresh = session.LastResolved?.FinalUrl;
                if (string.IsNullOrWhiteSpace(fresh))
                {
                    throw;
                }

                if (string.Equals(fresh, url, StringComparison.OrdinalIgnoreCase))
                {
                    // El guard anti-spam de renovación impidió renovar: invalidar la sesión para
                    // que la próxima apertura re-resuelva el origin desde cero (token fresco).
                    session.IsInvalid = true;
                    PluginLog.Warn("[HLS-TS] Token sin renovar (guard); sesión marcada para re-resolver.");
                }

                return await client.GetStringAsync(fresh, ct).ConfigureAwait(false);
            }
        }

        try
        {
            // Playlist inicial (si el token cacheado ya murió, FetchPlaylistCoreAsync lo renueva).
            var initial = await FetchPlaylistCoreAsync().ConfigureAwait(false);

            // Master multicapa: colapsar a una variante ligera una sola vez.
            string? master = null;
            string mediaUrl = session.LastResolved?.FinalUrl ?? string.Empty;
            if (initial.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                master = mediaUrl;
                var best = SelectBestVariant(initial, mediaUrl);
                if (best is null)
                {
                    throw new HttpRequestException("El master HLS no expone variantes válidas.");
                }

                mediaUrl = best;
            }

            var sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var firstBatch = true;

            while (!ct.IsCancellationRequested)
            {
                string playlist;
                try
                {
                    // Token efimero (SessionToken=false): el proveedor lo invalida a los ~20s y la
                    // renovacion reactiva (403) queda bloqueada por los guards de tokens largos.
                    // Renovar proactivamente antes de que expire (pisando el origin) evita el corte.
                    if (isTokenized && !session.SessionToken && ShouldRenewEphemeral(session))
                    {
                        PluginLog.Info("[HLS-TS] Renovacion proactiva de token efimero.");
                        try
                        {
                            await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
                        }
                        catch (Exception renewEx)
                        {
                            PluginLog.Warn($"[HLS-TS] Renovacion proactiva fallo: {renewEx.Message}");
                        }
                    }

                    // Media playlist (no master): usar siempre la URL vigente de la sesión para que
                    // la renovación de token efímero surta efecto en el siguiente refresco.
                    if (master is null)
                    {
                        mediaUrl = session.LastResolved?.FinalUrl ?? mediaUrl;
                    }

                    playlist = await client.GetStringAsync(mediaUrl, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (isTokenized)
                {
                    // Token efímero expirado a mitad de reproducción: renovar y continuar.
                    PluginLog.Warn("[HLS-TS] Refresco falló (token expirado); renovando token efímero…");
                    try
                    {
                        await Sessions.RenewSessionAsync(session, ct).ConfigureAwait(false);
                    }
                    catch (Exception renewEx)
                    {
                        PluginLog.Warn($"[HLS-TS] Renovación de token falló: {renewEx.Message}");
                    }

                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"[HLS-TS] Refresco de playlist falló: {ex.Message}");
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                if (playlist.Contains("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var segments = ParseHlsSegmentUris(playlist, mediaUrl);
                if (segments.Count == 0)
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                if (firstBatch)
                {
                    // Arrancar en el borde vivo SIN esperar al último segmento: en el live edge el
                    // último segmento del playlist suele estar aún grabándose, así que su GET se
                    // bloquea varios segundos y, si se corta, inyecta TS truncado (congelación).
                    // Dejar los 2 más recientes: el penúltimo ya está completo → arranque inmediato
                    // y estable; el último se envía cuando termine de publicarse.
                    var skip = Math.Max(0, segments.Count - 2);
                    for (var i = 0; i < skip; i++)
                    {
                        sent.Add(segments[i]);
                    }

                    firstBatch = false;
                }

                foreach (var segment in segments)
                {
                    if (!sent.Add(segment))
                    {
                        continue;
                    }

                    await SendTsSegmentAsync(client, segment, ct).ConfigureAwait(false);
                }

                // Podar segmentos ya enviados que salieron de la ventana viva (no reaparecen).
                sent.RemoveWhere(u => !segments.Contains(u, StringComparer.OrdinalIgnoreCase));
                if (sent.Count > 64)
                {
                    sent.Clear();
                }

                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El cliente se desconectó; salida limpia.
        }

        PluginLog.Info("[HLS-TS] Fin del flujo TS.");
        return new EmptyResult();
    }

    /// <summary>
    /// Decide si un token efímero debe renovarse proactivamente: si la URL trae expiración, se
    /// renueva 8s antes; si no (proveedores que la omiten pero invalidan a los ~20s, p. ej.
    /// alfa.7057460), se renueva cuando la última renovación supera ~9s para no llegar al 403.
    /// </summary>
    private static bool ShouldRenewEphemeral(StreamSession session)
    {
        var age = DateTimeOffset.UtcNow - (session.LastTokenRenewedAt ?? DateTimeOffset.MinValue);
        if (session.LastResolved?.TokenExpiry is { } expiry)
        {
            return expiry - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(8) || age > TimeSpan.FromSeconds(9);
        }

        return age > TimeSpan.FromSeconds(9);
    }

    private async Task SendTsSegmentAsync(HttpClient client, string segmentUrl, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(segmentUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                PluginLog.Warn($"[HLS-TS] Segmento {response.StatusCode}: {segmentUrl}");
                return;
            }

            using var upstream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await upstream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un segmento fallido no debe matar el flujo en vivo.
            PluginLog.Warn($"[HLS-TS] Error enviando segmento: {ex.Message}");
        }
    }

    private static string SelectBestVariant(string playlist, string playlistUrl)
    {
        // Arranque rápido: elegir la mejor variante <= ~2.8 Mbps (~720p) para que el primer
        // segmento baje ligero y la primera imagen llegue antes. Si todas superan ese tope
        // (o no hay tope utilizable), caer a la de mayor bitrate.
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
                var bandwidth = TryParseBandwidth(pendingHeader);
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
        return chosen is null ? string.Empty : AbsolutizeHlsUri(chosen, playlistUrl);
    }

    private static long TryParseBandwidth(string streamInfLine)
    {
        var marker = "BANDWIDTH=";
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

        return end > start && long.TryParse(streamInfLine[start..end], out var bw) ? bw : -1;
    }

    private static List<string> ParseHlsSegmentUris(string playlist, string baseUrl)
    {
        var result = new List<string>();
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            result.Add(AbsolutizeHlsUri(line, baseUrl));
        }

        return result;
    }

    private static string AbsolutizeHlsUri(string uri, string baseUrl)
    {
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        try
        {
            return new Uri(new Uri(baseUrl), uri).ToString();
        }
        catch (Exception)
        {
            return uri;
        }
    }

    // ── Simple redirect: resolve the source and return it to Jellyfin ─────
    private async Task<IActionResult> RedirectToResolvedTs(string sourceUrl, CancellationToken ct)
    {
        try
        {
            using var h = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 15, UseCookies = false };
            using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(15) };
            using var r = await c.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var final = r.RequestMessage?.RequestUri?.ToString() ?? sourceUrl;
            PluginLog.Info($"[TS] Redirect → {final}");
            return Redirect(final);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[TS] {ex.Message}");
            return StatusCode(502, ex.Message);
        }
    }

    // ── Direct stream with reconnect: no channels, no complexity ──────────
    private async Task<IActionResult> StreamTsDirectAsync(string sourceUrl, string? exitCountry, CancellationToken ct)
    {
        Response.ContentType = "video/mp2t";
        Response.Headers["Cache-Control"] = "no-cache, no-store";
        PluginLog.Info($"[StreamTS] Starting direct stream for {sourceUrl}");

        int round = 0;
        while (!ct.IsCancellationRequested)
        {
            round++;
            try
            {
                using var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 15, UseCookies = false };
                using var directClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var client = UpstreamClientService.RequiresExit(exitCountry) ? Upstream.For(exitCountry, sourceUrl) : directClient;
                PluginLog.Info($"[StreamTS] Round {round}: connecting...");
                using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? sourceUrl;
                PluginLog.Info($"[StreamTS] Round {round}: connected → {finalUrl}, streaming bytes...");
                using var upstream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await upstream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
                PluginLog.Info($"[StreamTS] Round {round}: upstream EOF (stream ended)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PluginLog.Info($"[StreamTS] Round {round}: {ex.GetType().Name} — {ex.Message}, retrying in 500ms...");
            }

            if (!ct.IsCancellationRequested)
            {
                await Response.Body.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        PluginLog.Info("[StreamTS] Client disconnected, stopping.");
        return new EmptyResult();
    }

    private static bool IsAuthFailure(HttpRequestException ex)
    {
        return ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
    }

    private static bool IsAuthFailure(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
    }

    private static string TryRebuildTokenizedHlsrUrl(string currentUrl, string? latestFinalUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUrl) || string.IsNullOrWhiteSpace(latestFinalUrl))
        {
            return currentUrl;
        }

        try
        {
            var latestToken = TryGetTokenFromUrl(latestFinalUrl);
            if (string.IsNullOrWhiteSpace(latestToken))
            {
                return currentUrl;
            }

            var marker = "/hlsr/";
            var currentUri = new Uri(currentUrl);
            var path = currentUri.AbsolutePath;
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return currentUrl;
            }

            var afterMarker = path[(idx + marker.Length)..];
            var nextSlash = afterMarker.IndexOf('/', StringComparison.Ordinal);
            if (nextSlash < 0)
            {
                return currentUrl;
            }

            var tail = afterMarker[(nextSlash + 1)..];
            var latestUri = new Uri(latestFinalUrl);

            var builder = new UriBuilder(currentUri)
            {
                Scheme = latestUri.Scheme,
                Host = latestUri.Host,
                Port = latestUri.IsDefaultPort ? -1 : latestUri.Port,
                Path = $"hlsr/{latestToken}/{tail}",
                Query = string.Empty
            };

            return builder.Uri.ToString();
        }
        catch
        {
            return currentUrl;
        }
    }

    private static string? TryGetTokenFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (string.IsNullOrWhiteSpace(uri.Query))
            {
                return null;
            }

            var query = uri.Query[0] == '?' ? uri.Query[1..] : uri.Query;
            var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("token", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }
        }
        catch
        {
            // ignore parse errors
        }

        return null;
    }

    private static string BuildBaseUrl(string url)
    {
        var uri = new Uri(url);
        var last = uri.AbsolutePath.LastIndexOf('/');
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}{(last >= 0 ? uri.AbsolutePath[..(last + 1)] : "/")}";
    }
}
