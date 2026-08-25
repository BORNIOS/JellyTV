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
    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; JellyTV/1.0)";
    public bool VerboseLogging { get; set; } = false;
    public string PreferredAudioLanguages { get; set; } = "es,spa,es-419";
    public string PreferredSubtitleLanguages { get; set; } = "es,spa";

    // ── Channel management ────────────────────────────────────────────────
    /// <summary>Individual channels managed directly (no M3U sources).</summary>
    public List<ChannelEntry> Channels { get; set; } = new();

    // ── Proxy pool ────────────────────────────────────────────────────────
    /// <summary>HTTPS proxies used for chain resolution (not segment downloads).</summary>
    public List<ProxyEntry> Proxies { get; set; } = new();

    /// <summary>
    /// When true the plugin auto-registers a single M3U tuner in Jellyfin
    /// pointing to /JellyTV/m3u so you don't have to do it manually.
    /// </summary>
    public bool AutoRegisterTuner { get; set; } = true;

    // ── Scheduled tasks ───────────────────────────────────────────────────
    /// <summary>How often (in hours) the source sync task re-fetches all M3U sources.</summary>
    public int SourceSyncIntervalHours { get; set; } = 24;
    /// <summary>How often (in hours) the channel health check task validates all channels.</summary>
    public int ChannelHealthCheckIntervalHours { get; set; } = 24;

    // ── Worker integration ────────────────────────────────────────────────
    /// <summary>Base URL of the JellyTV Cloudflare Worker (e.g. https://jellytv.hmovies.tv).</summary>
    public string WorkerBaseUrl { get; set; } = string.Empty;
    /// <summary>API key sent as x-api-key to authenticate health reports to the Worker.</summary>
    public string WorkerApiKey { get; set; } = string.Empty;

    // ── TMDB enrichment ────────────────────────────────────────────────────
    /// <summary>TMDB API v4 Bearer token used by the Plugin to enrich EPG titles.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Maximum parallel workers for TMDB enrichment pipeline.</summary>
    public int TmdbMaxConcurrency { get; set; } = 6;

    /// <summary>Minutes to suppress retries for titles with no confident TMDB match.</summary>
    public int TmdbNoMatchTtlMinutes { get; set; } = 180;

    // ── EPG / XMLTV filter ─────────────────────────────────────────────────
    /// <summary>
    /// XMLTV sources that the Plugin will download, filter to matching tvg-ids,
    /// and push to the Worker. Keeps Worker CPU/storage usage minimal.
    /// </summary>
    public List<EpgSource> EpgSources { get; set; } = new();
}