using System.Collections.Generic;
using JellyTV.Models;
using MediaBrowser.Model.Plugins;

namespace JellyTV.Configuration;

/// <summary>Plugin configuration.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── HTTP / Token settings ─────────────────────────────────────────────
    public int MaxRedirects { get; set; } = 10;
    public int TokenRefreshMarginSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; JellyTV/1.0)";
    public bool VerboseLogging { get; set; } = false;

    // ── M3U Source management ─────────────────────────────────────────────
    /// <summary>List of IPTV sources managed by this plugin.</summary>
    public List<IptvSource> Sources { get; set; } = new();

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
}