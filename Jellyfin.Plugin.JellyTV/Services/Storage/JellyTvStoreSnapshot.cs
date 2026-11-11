using System.Collections.Generic;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// Persistent snapshot of the plugin-owned data that can live outside Jellyfin's plugin configuration.
/// </summary>
public sealed class JellyTvStoreSnapshot
{
    /// <summary>Gets or sets the snapshot format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the configured channels.</summary>
    public List<ChannelEntry> Channels { get; set; } = new();

    /// <summary>Gets or sets the configured proxies.</summary>
    public List<ProxyEntry> Proxies { get; set; } = new();

    /// <summary>Gets or sets the configured EPG sources.</summary>
    public List<EpgSource> EpgSources { get; set; } = new();

    /// <summary>Gets a value indicating whether the snapshot contains any entity.</summary>
    public bool HasData => Channels.Count > 0 || Proxies.Count > 0 || EpgSources.Count > 0;

    /// <summary>
    /// Builds a snapshot from the current plugin configuration.
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <returns>The snapshot.</returns>
    public static JellyTvStoreSnapshot FromConfiguration(PluginConfiguration configuration)
        => new()
        {
            Channels = configuration.Channels ?? new List<ChannelEntry>(),
            Proxies = configuration.Proxies ?? new List<ProxyEntry>(),
            EpgSources = configuration.EpgSources ?? new List<EpgSource>()
        };
}
