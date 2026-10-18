using System;

namespace Jellyfin.Plugin.JellyTV.Models;

public class ProxyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 8080;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Gets or sets a value indicating whether when true, the plugin connects to the proxy itself over TLS (https://).</summary>
    public bool UseHttps { get; set; } = false;

    public bool IsEnabled { get; set; } = true;
}
