namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// External identity of a channel according to the public iptv-org database: which channel it is, where it
/// comes from and its website. It is what lets the plugin fill the logo, country and guide id on its own.
/// </summary>
public class ChannelIdentity
{
    /// <summary>Gets or sets the iptv-org database id (for example ESPN.us).</summary>
    public string? IptvOrgId { get; set; }

    /// <summary>Gets or sets the canonical channel name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the ISO 3166-1 country code of the channel.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the official website of the channel.</summary>
    public string? Website { get; set; }

    /// <summary>Gets or sets a value indicating whether the identity was picked by a person (never overwritten by the automatic enrichment).</summary>
    public bool Confirmed { get; set; }
}
