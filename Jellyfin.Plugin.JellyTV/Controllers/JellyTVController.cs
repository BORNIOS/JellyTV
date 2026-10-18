using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTV.Controllers;

[ApiController]
[Route("JellyTV")]
public class JellyTVController : ControllerBase
{
    private const string HlsMimeType = "application/vnd.apple.mpegurl";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITaskManager? _taskManager;

    public JellyTVController(IHttpClientFactory httpClientFactory, ITaskManager? taskManager = null)
    {
        _httpClientFactory = httpClientFactory;
        _taskManager = taskManager;
    }

    private static Plugin P => Plugin.Instance!;

    private static SessionManagerService Sessions => P.SessionManager;

    private static HlsRewriterService HlsRewriter => P.HlsRewriter;

    /// <summary>
    /// Persists the full plugin configuration (channels, proxies, EPG sources and settings).
    /// The config page sends its whole object here so changes are written to disk through the
    /// same path the plugin uses internally (Plugin.SaveConfiguration) and are visible again on
    /// the next read (no stale in-memory copy).
    /// </summary>
    [HttpPost("config")]
    [AllowAnonymous]
    public IActionResult SaveConfig([FromBody] PluginConfiguration incoming)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || incoming is null)
        {
            return BadRequest(new { error = "Plugin no inicializado" });
        }

        var live = plugin.Configuration;
        foreach (var prop in typeof(PluginConfiguration).GetProperties())
        {
            if (prop.CanRead && prop.CanWrite)
            {
                prop.SetValue(live, prop.GetValue(incoming));
            }
        }

        Plugin.NormalizeChannels(live, persist: false);
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

    // ── M3U TUNER (registrar en Jellyfin → Live TV → Tuner devices → M3U Tuner) ──
    // Expone los canales como playlist M3U consumible por el tuner M3U nativo. Cada canal apunta
    // a una URL de REFERENCIA del relay del plugin (/JellyTV/stream?url=...&ch=...): NUNCA se
    // emite la URL cruda registrada (en tokenizados es solo HTML/JS; además expira). El relay
    // resuelve en el momento (HTML→JS→m3u8+token) con failover por fuentes. La playlist se genera
    // con el host de la petición (lo alcanza el propio server) y se PERSISTE en
    // {DataPath}/JellyTV/tuner.m3u (igual que la EPG) para usarse como archivo o vía HTTP.
    [HttpGet("m3u")]
    [AllowAnonymous]
    public IActionResult GetCombinedM3u()
    {
        var baseUrl = ResolveBaseUrl();
        return Content(BuildM3uContent(baseUrl, asTs: false), "application/x-mpegurl", Encoding.UTF8);
    }

    [HttpGet("tuner.m3u")]
    [AllowAnonymous]
    public IActionResult GetTunerM3u()
    {
        var baseUrl = ResolveBaseUrl();
        var content = BuildM3uContent(baseUrl, asTs: false);
        PersistTunerM3u(content);
        Response.Headers["Cache-Control"] = "no-store";
        return Content(content, "application/x-mpegurl", Encoding.UTF8);
    }

    private string ResolveBaseUrl()
    {
        P.KnownBaseUrl ??= $"{Request.Scheme}://{Request.Host}";
        return $"{Request.Scheme}://{Request.Host}";
    }

    private static string BuildM3uContent(string baseUrl, bool asTs)
    {
        var channels = P.Configuration?.Channels?
            .Where(c => c.IsEnabled
                && (c.Sources.Any(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url))
                    || !string.IsNullOrWhiteSpace(c.Url)))
            .ToList() ?? new List<ChannelEntry>();

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
            AppendM3uAttribute(sb, "tvg-logo", ch.LogoUrl);
            AppendM3uAttribute(sb, "group-title", ch.Category);
            sb.Append(CultureInfo.InvariantCulture, $",{SanitizeM3uName(ch.Name)}\n");

            // Enrutado: TODOS los canales pasan por el RELAY del plugin (incluidos los Directo).
            // Antes los Direct (tipo 0) se emitían CRUDOS y Jellyfin los abría nativo/remoto
            // (probe + transcode) → arranque lento (~30s) frente a los relayados (~3s). El relay
            // unifica el arranque rápido (colapso de variante a 720p + segmentos por proxy con
            // remapeo de borde) para Direct, FAST y Tokenizado.
            var streamUrl = BuildRelayStreamUrl(baseUrl, active.Url, ch.Id, asTs);
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
        return $"{baseUrl}/JellyTV/stream?url={encoded}&ch={channelId}{mode}";
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
    [HttpGet("stream")]
    [HttpGet("stream.m3u8")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStream([FromQuery] string url, CancellationToken cancellationToken)
    {
        // Ensure KnownBaseUrl is set for ILiveTvService.
        P.KnownBaseUrl ??= $"{Request.Scheme}://{Request.Host}";

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

        // Candidatas en orden: primero la fuente pedida (la que eligió Live TV = mejor viva)
        // y después el resto de fuentes habilitadas del canal para failover al abrir.
        var candidates = new List<(string Url, int Type)>();
        if (channel is not null)
        {
            channel.EnsureMigratedSources();
            var sources = channel.Sources.Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
            if (sources.Count == 0 && !string.IsNullOrWhiteSpace(channel.Url))
            {
                sources.Add(new ChannelSource { Url = channel.Url, Type = channel.Type });
            }

            foreach (var s in sources)
            {
                if (string.Equals(s.Url, decodedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Insert(0, (s.Url, s.Type));
                }
                else
                {
                    candidates.Add((s.Url, s.Type));
                }
            }
        }

        if (candidates.Count == 0)
        {
            candidates.Add((decodedUrl, 0));
        }

        // Failover al abrir: probamos cada fuente; si una no abre, pasamos a la siguiente.
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
                    return await StreamTsDirectAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
                }

                // ── .m3u8 path: HLS playlist proxy (fetch, rewrite, cache) ──
                var session = await Sessions.GetOrCreateSessionAsync(sourceUrl, sourceType, cancellationToken).ConfigureAwait(false);
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
                return Content(rewritten, HlsMimeType, Encoding.UTF8);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                PluginLog.Warn($"[STREAM] Fuente no disponible, se prueba la siguiente: {candidate.Url} :: {ex.Message}");
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"[STREAM] Fuente con error, se prueba la siguiente: {candidate.Url} :: {ex.Message}");
            }
        }

        PluginLog.Error("[STREAM] Todas las fuentes del canal fallaron al abrir");
        return StatusCode(502, "No hay fuentes disponibles para este canal.");
    }

    // ── SEGMENT ──────────────────────────────────────────────────────────────
    // Proxy de segmentos .ts y sub-playlists .m3u8.
    [HttpGet("segment")]
    [HttpGet("segment.{ext}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSegment([FromQuery] string sid, [FromQuery] string url, CancellationToken cancellationToken, [FromRoute] string? ext = null)
    {
        string upstreamUrl;
        try
        {
            upstreamUrl = HlsRewriterService.DecodeSegmentUrl(url);
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
            PluginLog.Warn($"[SEGMENT] Unknown session {sid}");
            return NotFound("Session expired. Reopen stream.");
        }

        if (upstreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return await ProxySubPlaylistAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);
        }

        return await ProxySegmentAsync(session, upstreamUrl, cancellationToken).ConfigureAwait(false);
    }

    // ── DIAGNOSTICS ──────────────────────────────────────────────────────────
    [HttpGet("sessions")]
    [AllowAnonymous]
    public IActionResult GetSessions()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"=== JellyTV Sessions @ {DateTimeOffset.Now:O} ===");
        var sessions = Plugin.Instance?.SessionManager?.GetAllSessions() ?? Array.Empty<StreamSession>();
        if (sessions.Length == 0)
        {
            sb.AppendLine("No active sessions.");
        }

        foreach (var s in sessions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"ID={s.SessionId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Upstream   = {s.UpstreamUrl}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  FinalUrl   = {s.LastResolved?.FinalUrl ?? "null"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  IsHls      = {s.LastResolved?.IsHls}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Expiry     = {s.LastResolved?.TokenExpiry?.ToString("O") ?? "unknown"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  LastAccess = {s.LastAccessedAt:O}");
            sb.AppendLine();
        }

        return Content(sb.ToString(), "text/plain", Encoding.UTF8);
    }

    [HttpGet("log")]
    [AllowAnonymous]
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
                sb.AppendLine(line);
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
    [HttpGet("channels")]
    [AllowAnonymous]
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
                if (healthCheck is not null && healthCheck.TryGetCachedChannelStatus(ch.Url, out var cached))
                {
                    health = cached;
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
                    url = ch.Url,
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

    [HttpPost("refresh-guide")]
    [AllowAnonymous]
    public IActionResult RefreshGuide()
    {
        if (_taskManager is null)
        {
            return Ok(new { status = "no-task-manager" });
        }

        try
        {
            var guideTask = _taskManager.ScheduledTasks
                .FirstOrDefault(t => t.Name.Contains("Refresh Guide", StringComparison.OrdinalIgnoreCase));
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

    [HttpPost("health-check")]
    [AllowAnonymous]
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

            // Execute in background
            _ = Task.Run(
                async () =>
                {
                    await healthCheck.CheckConfiguredChannelsAsync("manual", channels, CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        Plugin.Instance?.SaveConfiguration();
                    }
                    catch
                    {
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

    // ── EPG LOCAL ─────────────────────────────────────────────────────────────
    // La guía XMLTV la genera el propio plugin (sin Worker) y la sirve aquí; el usuario
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

    /// <summary>Lanza en segundo plano la sincronización local de la guía (fuentes → filtro → TMDB → disco).</summary>
    [HttpPost("epg/sync")]
    [AllowAnonymous]
    public IActionResult StartEpgSync()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin no inicializado" });
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
                            .FirstOrDefault(t => t.Name.Contains("Refresh Guide", StringComparison.OrdinalIgnoreCase));
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

        return Ok(new { message = "Sync EPG iniciado en segundo plano" });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Comprueba rápidamente si una fuente responde (HTTP 2xx con cuerpo) con un límite de 8s.
    /// La UI lo usa antes de guardar un canal para decidir si se guarda activo (ON) u OFF.
    /// </summary>
    [HttpPost("check-source")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckSourceQuick([FromQuery] string url, [FromQuery] int type = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "url requerida" });
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var client = _httpClientFactory.CreateClient("JellyTV");
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
        var client = _httpClientFactory.CreateClient("JellyTV");
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
            // 400/404/410: segmento en el borde de la ventana viva. Xumo devuelve 400 una fracción
            // de segundo mientras publica el segmento más nuevo; ftlly (ventana ~24s) devuelve 404
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
        var client = _httpClientFactory.CreateClient("JellyTV");
        var fetchUrl = HlsRewriterService.UnwrapRedirectUrl(subUrl);

        try
        {
            var content = await client.GetStringAsync(fetchUrl, ct).ConfigureAwait(false);
            var rewritten = HlsRewriter.RewritePlaylistContent(content, fetchUrl, session.SessionId, GetProxyBaseUrl());
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
        try
        {
            var response = await client.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, new EmptyResult());
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[SEGMENT] Network error attempt {attempt}", ex);
            return (null, StatusCode(502, ex.Message));
        }
    }

    private async Task<IActionResult> StreamSegmentToClientAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/MP2T";
        if (response.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = response.Content.Headers.ContentLength.Value;
        }

        if (Plugin.Instance?.Configuration.VerboseLogging == true)
        {
            PluginLog.Info($"[SEGMENT] Streaming {Response.ContentLength ?? -1} bytes");
        }

        using (response)
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await stream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        }

        return new EmptyResult();
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
            var client = _httpClientFactory.CreateClient("JellyTV");
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
        var client = _httpClientFactory.CreateClient("JellyTV");
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
    private async Task<IActionResult> StreamTsDirectAsync(string sourceUrl, CancellationToken ct)
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
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
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
