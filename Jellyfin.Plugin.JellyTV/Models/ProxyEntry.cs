using System;

namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// Proxy endpoint used by the plugin to reach IPTV providers.
/// </summary>
public class ProxyEntry
{
    /// <summary>Gets or sets the unique identifier of the proxy.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the proxy host name or IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the proxy port.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Gets or sets the optional proxy user name.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the optional proxy password.</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets a value indicating whether when true, the plugin connects to the proxy itself over TLS (https://).</summary>
    public bool UseHttps { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether this proxy is enabled for use.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets the two-letter country (ISO 3166-1) the proxy exits through, such as "US". Channels that need an exit pick a proxy by this value.</summary>
    public string Country { get; set; } = string.Empty;
}
