using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// Represents a single TV channel managed by the plugin.
/// </summary>
public class ChannelEntry
{
    public const int DirectType = 0;
    public const int FastType = 1;

    /// <summary>
    /// Tokenizado. El canal necesita un token (via gateway/redirect/HTML embebido) que el
    /// plugin resuelve, sirve por relay y renueva (proactivo o ante 401/403).
    /// </summary>
    public const int TokenizedType = 2;

    /// <summary>Gets or sets unique identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Gets or sets public display name shown in Jellyfin.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ordered list of stream sources: Sources[0] is the primary and the rest
    /// are backups. Every source has its own delivery type (Direct/FAST/Tokenizado) and health.
    /// </summary>
    public List<ChannelSource> Sources { get; set; } = new();

    /// <summary>
    /// Gets or sets primary stream URL (HLS .m3u8 or MPEG-TS).
    /// Legacy field kept for XML compatibility; migrated into <see cref="Sources"/>[0].
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets fallback URL #1 (legacy — migrado a <see cref="Sources"/>).</summary>
    public string? FallbackUrl1 { get; set; }

    /// <summary>Gets or sets fallback URL #2 (legacy — migrado a <see cref="Sources"/>).</summary>
    public string? FallbackUrl2 { get; set; }

    /// <summary>
    /// Gets or sets channel type: 0=Direct, 1=FAST, 2=Tokenizado.
    /// Stored as int to avoid XML enum serialization issues with Jellyfin's config serializer.
    /// </summary>
    public int Type { get; set; } = 0;

    /// <summary>Gets a value indicating whether el canal requiere resolver/renovar token (Tokenizado).</summary>
    public bool IsTokenized => IsTokenizedType(Type);

    /// <summary>
    /// Gets or sets a value indicating whether token de sesión (checkbox en la UI): el gateway entrega un token para TODA la sesión
    /// (p.ej. tvf90 embebe un m3u8 con token de varias horas). El plugin re-baja el MISMO
    /// playlist tokenizado y NO renueva proactivamente; solo renueva de forma reactiva
    /// (401/403 o al expirar). Si es false, el token se considera efímero y se renueva.
    /// </summary>
    public bool SessionToken { get; set; }

    /// <summary>Gets a value indicating whether whether the channel is an open origin without token handling (Direct/FAST).</summary>
    public bool IsDirectPlayback => Type is DirectType or FastType;

    /// <summary>Gets or sets a value indicating whether whether this channel is active and included in the M3U output.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether el plugin desactivó este canal automáticamente
    /// (todas sus fuentes estaban caídas). Se reactiva solo cuando alguna fuente vuelve a estar viva.
    /// </summary>
    public bool AutoDisabled { get; set; }

    /// <summary>Gets or sets optional custom HTTP headers (one per line, format: Name: Value).</summary>
    public string? CustomHeaders { get; set; }

    /// <summary>Gets or sets optional per-channel User-Agent override.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Gets or sets uRL to the channel logo/icon.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Gets or sets tvg-id for EPG matching.</summary>
    public string? TvgId { get; set; }

    /// <summary>Gets or sets tvg-name override for EPG matching.</summary>
    public string? TvgName { get; set; }

    /// <summary>Gets or sets category / group-title for the M3U output.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets last time this channel was successfully resolved/checked.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Gets or sets whether the channel was alive during the last health check.</summary>
    public bool? LastCheckAlive { get; set; }

    /// <summary>Gets or sets last error message if the health check failed.</summary>
    public string? LastCheckError { get; set; }

    /// <summary>
    /// Populates <see cref="Sources"/> from the legacy Url/FallbackUrl1/FallbackUrl2 fields when
    /// it is empty. Idempotent: does nothing once Sources already contains at least one entry.
    /// </summary>
    public void EnsureMigratedSources()
    {
        if (Sources.Count > 0 || string.IsNullOrWhiteSpace(Url))
        {
            return;
        }

        Sources.Add(new ChannelSource { Label = "Principal", Url = Url, Type = Type, SessionToken = SessionToken });
        if (!string.IsNullOrWhiteSpace(FallbackUrl1))
        {
            Sources.Add(new ChannelSource { Label = "Respaldo 1", Url = FallbackUrl1, Type = Type, SessionToken = SessionToken });
        }

        if (!string.IsNullOrWhiteSpace(FallbackUrl2))
        {
            Sources.Add(new ChannelSource { Label = "Respaldo 2", Url = FallbackUrl2, Type = Type, SessionToken = SessionToken });
        }
    }

    /// <summary>True si el tipo requiere resolver/renovar un token.</summary>
    /// <param name="type">Tipo de canal (0/1/2).</param>
    /// <returns>true si el tipo es Tokenizado.</returns>
    public static bool IsTokenizedType(int type) => type == TokenizedType;
}
