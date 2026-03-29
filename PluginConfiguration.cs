using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.IptvProxy.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the maximum number of redirects to follow per request.
    /// </summary>
    public int MaxRedirects { get; set; } = 10;

    /// <summary>
    /// Gets or sets the token refresh margin in seconds before expiry.
    /// </summary>
    public int TokenRefreshMarginSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the number of HLS segments to prefetch.
    /// </summary>
    public int PrefetchSegmentCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the user agent sent to the upstream IPTV provider.
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; JellyfinIptvProxy/1.0)";

    /// <summary>
    /// Gets or sets whether verbose logging is enabled.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    /// When true the plugin will proxy segment requests server-side instead of
    /// only resolving final URLs. Use only if necessary; proxying increases server
    /// bandwidth and may require careful header/cookie handling to succeed.
    /// </summary>
    public bool EnableProxyMode { get; set; } = false;

    /// <summary>
    /// When true the plugin will download and concatenate segments server-side using ffmpeg.
    /// </summary>
    public bool EnableConcatMode { get; set; } = false;

    /// <summary>
    /// Interval in seconds at which to run the concatenation job (default 20s).
    /// </summary>
    public int ConcatIntervalSeconds { get; set; } = 20;

    /// <summary>
    /// Path to ffmpeg executable. If empty, assumes ffmpeg is on PATH.
    /// </summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
}
