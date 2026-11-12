namespace Jellyfin.Plugin.JellyTV.Services.Identity;

/// <summary>
/// A possible identity for a channel, taken from the iptv-org database.
/// </summary>
public sealed class IdentityCandidate
{
    /// <summary>Gets or sets the canonical name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the iptv-org database id.</summary>
    public string? IptvOrgId { get; set; }

    /// <summary>
    /// Gets or sets the id that matches the channel with its guide: the iptv-org id, or the channel id declared by a local guide
    /// (Pluto, Samsung, Plex and Tubi publish their own ids instead of a tvg-id).
    /// </summary>
    public string? TvgId { get; set; }

    /// <summary>Gets or sets the country code.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the official website.</summary>
    public string? Website { get; set; }

    /// <summary>Gets or sets the logo URL.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Gets or sets how well the candidate matches the query (0..1).</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets the provider that knows this candidate.</summary>
    public string Sources { get; set; } = string.Empty;
}
