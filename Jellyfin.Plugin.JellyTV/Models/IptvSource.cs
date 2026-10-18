using System;

namespace Jellyfin.Plugin.JellyTV.Models;

public class IptvSource
{
    /// <summary>Gets or sets unique identifier for this source.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets display name shown in the config UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets uRL to the M3U playlist (may contain a token).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether whether this source is active.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets last time this source was successfully fetched.</summary>
    public DateTimeOffset? LastFetched { get; set; }

    /// <summary>Gets or sets number of channels parsed on last fetch.</summary>
    public int LastChannelCount { get; set; }

    /// <summary>Gets or sets last error message if fetch failed, null if OK.</summary>
    public string? LastError { get; set; }
}
