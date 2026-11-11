using System;

namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// A single stream source of a channel: the primary one or a backup. Each source
/// has its own delivery type (Direct/FAST/Tokenizado), session-token flag and health status.
/// </summary>
public class ChannelSource
{
    /// <summary>Gets or sets unique identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Gets or sets optional display label (p. ej. "Principal", "Respaldo 1").</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets stream URL (HLS .m3u8 or MPEG-TS).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets source type: 0=Directo, 1=FAST, 2=Tokenizado.
    /// Stored as int to avoid XML enum serialization issues with Jellyfin's config serializer.
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether el token es de sesión (válido toda la sesión).
    /// Solo relevante cuando <see cref="Type"/> es Tokenizado.
    /// </summary>
    public bool SessionToken { get; set; }

    /// <summary>Gets or sets a value indicating whether la fuente está habilitada (se usa en failover/health).</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the country the stream must be fetched from (such as "US") for geoblocked sources. Empty means the
    /// server fetches it directly. A source with an exit is always served through the relay, which downloads everything
    /// through a proxy of that country.
    /// </summary>
    public string? ExitCountry { get; set; }

    /// <summary>Gets or sets last time this source was checked.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Gets or sets whether the source was alive during the last health check.</summary>
    public bool? IsAlive { get; set; }

    /// <summary>Gets or sets last error message if the health check failed.</summary>
    public string? LastError { get; set; }
}
