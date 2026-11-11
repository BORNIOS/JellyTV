using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services.Identity;

/// <summary>
/// A channel of the public iptv-org database (name, alternative names, country, website and best logo).
/// </summary>
/// <param name="Id">Database id, like <c>ESPN.us</c>.</param>
/// <param name="Name">Channel name.</param>
/// <param name="AltNames">Other names the channel is known by.</param>
/// <param name="Country">ISO 3166-1 country code.</param>
/// <param name="Website">Official website.</param>
/// <param name="Logo">Best logo URL.</param>
/// <param name="GatoTv">Channel page id on gatotv.com (<c>tudn_mexico</c>), when the database lists one.</param>
internal sealed record IptvOrgChannel(string Id, string Name, IReadOnlyList<string> AltNames, string? Country, string? Website, string? Logo, string? GatoTv = null);
