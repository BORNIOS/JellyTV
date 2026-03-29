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
}
