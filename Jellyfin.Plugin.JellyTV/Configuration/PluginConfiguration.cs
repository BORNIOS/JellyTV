using System.Collections.Generic;
using Jellyfin.Plugin.JellyTV.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTV.Configuration;

/// <summary>Plugin configuration.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── HTTP / Token settings ─────────────────────────────────────────────
    // MaxRedirects and RequestTimeoutSeconds are now hardcoded constants to prevent
    // interference with token renewal logic. See RedirectResolverService.
    // El default es un User-Agent de navegador (fuente única: Plugin.DefaultUserAgent).
    // Se edita en la UI; la lógica de fetch/entrega nunca lo hardcodea.
    public string UserAgent { get; set; } = Plugin.DefaultUserAgent;

    /// <summary>
    /// Gets or sets URL base pública con la que la instancia de Jellyfin se alcanza a sí misma para
    /// construir los Path de streaming (p. ej. "https://tv.midominio.com"). Plugin público: cada
    /// instancia registra la suya; NUNCA se hardcodea un dominio concreto. Vacío = auto (host de la
    /// última petición al plugin y, como último recurso, localhost con aviso en log).
    /// </summary>
    public string PublicStreamingBaseUrl { get; set; } = string.Empty;

    public bool VerboseLogging { get; set; } = false;

    public string PreferredAudioLanguages { get; set; } = "es,spa,es-419";

    public string PreferredSubtitleLanguages { get; set; } = "es,spa";

    // ── Channel management ────────────────────────────────────────────────

    /// <summary>Gets or sets individual channels managed directly (no M3U sources).</summary>
    public List<ChannelEntry> Channels { get; set; } = new();

    // ── Proxy pool ────────────────────────────────────────────────────────

    /// <summary>Gets or sets hTTPS proxies used for chain resolution (not segment downloads).</summary>
    public List<ProxyEntry> Proxies { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether keeps the optional diagnostic M3U endpoint available. Channels are exposed
    /// through the JellyTV provider and do not require an external M3U tuner.
    /// </summary>
    public bool AutoRegisterTuner { get; set; } = true;

    // ── Scheduled tasks ───────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets how often (in hours) the EPG sync task re-fetches the XMLTV sources. Contenido
    /// pesado: por defecto cada 24 h (frecuencia distinta del health/M3U, que es en minutos).
    /// </summary>
    public int SourceSyncIntervalHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets how often (in minutes) the maintenance task validates channel/source health.
    /// "Constant" cadence: los canales caídos se re-chequean en cada corrida para re-habilitarlos
    /// en cuanto regresan; los sanos respetan la caché interna del health service.
    /// </summary>
    public int HealthCheckIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets how often (in hours) the channel health check task validates all channels.
    /// Obsoleto: se conserva por compatibilidad XML; la cadencia real es HealthCheckIntervalMinutes.
    /// </summary>
    public int ChannelHealthCheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets a value indicating whether la tarea de mantenimiento dispara el "Refresh Guide"
    /// de Jellyfin cuando un health check deshabilita/rehabilita canales o se regenera la EPG, para
    /// que el tuner M3U refleje el cambio lo antes posible sin tarea externa.
    /// </summary>
    public bool AutoRefreshGuideOnChange { get; set; } = true;

    // ── TMDB enrichment ────────────────────────────────────────────────────

    /// <summary>Gets or sets tMDB API v4 Bearer token used by the Plugin to enrich EPG titles.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets maximum parallel workers for TMDB enrichment pipeline.</summary>
    public int TmdbMaxConcurrency { get; set; } = 6;

    /// <summary>Gets or sets minutes to suppress retries for titles with no confident TMDB match.</summary>
    public int TmdbNoMatchTtlMinutes { get; set; } = 180;

    // ── EPG / XMLTV filter ─────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets xMLTV sources that the Plugin downloads, filters to the channel tvg-ids
    /// and merges locally into a single guide file served by the Plugin itself.
    /// </summary>
    public List<EpgSource> EpgSources { get; set; } = new();

    /// <summary>
    /// Gets or sets zona horaria IANA en la que están escritas las horas de los XMLTV
    /// (p. ej. "America/Mexico_City"). Vacío/"UTC" = respetar el offset declarado.
    /// </summary>
    public string EpgTimezone { get; set; } = string.Empty;
}
