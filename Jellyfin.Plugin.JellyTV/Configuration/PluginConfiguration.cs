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

    /// <summary>Gets or sets the User-Agent sent to IPTV providers.</summary>
    public string UserAgent { get; set; } = Plugin.DefaultUserAgent;

    /// <summary>
    /// Gets or sets URL base pública con la que la instancia de Jellyfin se alcanza a sí misma para
    /// construir los Path de streaming (p. ej. "https://tv.example.com"). Plugin público: cada
    /// instancia registra la suya; NUNCA se hardcodea un dominio concreto. Vacío = auto (host de la
    /// última petición al plugin y, como último recurso, localhost con aviso en log).
    /// </summary>
    public string PublicStreamingBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether detailed diagnostic logging is enabled.</summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>Gets or sets the comma-separated preferred audio languages.</summary>
    public string PreferredAudioLanguages { get; set; } = "es,spa,es-419";

    /// <summary>Gets or sets the comma-separated preferred subtitle languages.</summary>
    public string PreferredSubtitleLanguages { get; set; } = "es,spa";

    // ── Channel management ────────────────────────────────────────────────

    /// <summary>Gets or sets individual channels managed directly (no M3U sources).</summary>
    public List<ChannelEntry> Channels { get; set; } = new();

    // ── Proxy pool ────────────────────────────────────────────────────────

    /// <summary>Gets or sets hTTPS proxies used for chain resolution (not segment downloads).</summary>
    public List<ProxyEntry> Proxies { get; set; } = new();

    /// <summary>
    /// Gets or sets how channels reach Jellyfin Live TV:
    /// <c>"Auto"</c> (default) publishes the plugin's own <c>ILiveTvService</c> unless an M3U tuner
    /// already points to <c>/JellyTV/tuner.m3u</c>; <c>"Native"</c> always publishes it (channels and
    /// guide, no playlist needed); <c>"M3u"</c> never publishes it, so only the registered M3U tuner
    /// feeds Live TV. Use one route or the other to avoid duplicated channels.
    /// </summary>
    public string LiveTvMode { get; set; } = "Auto";

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

    /// <summary>
    /// Gets or sets a value indicating whether the guide is also filled automatically: the channels identified in iptv-org
    /// get the public XMLTV files of their country, and those no file covers use their GatoTV channel page.
    /// </summary>
    public bool AutoGuideEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets custom recipes (a JSON list) to read the schedule of HTML pages. Each recipe applies to one domain and
    /// describes its rows with a regular expression; the built-in recipes are always available.
    /// </summary>
    public string? HtmlGuideRecipesJson { get; set; }

    // ── Almacenamiento ─────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the storage backend: "Auto" (PostgreSQL when PG Provider is available, otherwise
    /// JSON files), "Postgres" (PG Provider only, falling back to JSON) or "Json" (always local files).
    /// </summary>
    public string StorageBackend { get; set; } = "Auto";

    /// <summary>
    /// Gets or sets an alternative absolute folder for the JSON files. Empty keeps them under
    /// <c>{DataPath}/JellyTV</c>. A relative path is ignored so data never lands next to the process.
    /// </summary>
    public string JsonDataPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the JSON snapshot is also written while PostgreSQL is the
    /// active store. Disabled by default; PostgreSQL remains the only operational source when it works.
    /// </summary>
    public bool EnableJsonCourtesyBackup { get; set; }

    // ── Seguridad ──────────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the secret key required by anonymous relay endpoints
    /// (playlists, streams and segments). Generated automatically when empty.
    /// </summary>
    public string RelayKey { get; set; } = string.Empty;

    /// <summary>
    /// Creates a copy with every setting but without channels, proxies and EPG sources. Those
    /// collections live in the database store; the XML file keeps only the settings.
    /// </summary>
    /// <returns>A new settings-only configuration.</returns>
    public PluginConfiguration CloneSettingsOnly()
    {
        var copy = new PluginConfiguration();
        foreach (var property in typeof(PluginConfiguration).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite
                || property.Name is nameof(Channels) or nameof(Proxies) or nameof(EpgSources))
            {
                continue;
            }

            property.SetValue(copy, property.GetValue(this));
        }

        return copy;
    }
}
