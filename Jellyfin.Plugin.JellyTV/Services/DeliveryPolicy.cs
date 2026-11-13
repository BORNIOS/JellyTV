using System;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Decides how each source reaches the player. The relay exists for the cases that truly need the
/// plugin in the middle (FAST cleaning and ephemeral tokens); everything else is handed over as the
/// provider URL so Jellyfin can Direct Play without extra hops.
/// </summary>
public static class DeliveryPolicy
{
    /// <summary>
    /// Gets a value indicating whether the source must go through the plugin relay.
    /// </summary>
    /// <param name="source">Source to evaluate.</param>
    /// <returns><c>true</c> when the relay is required.</returns>
    public static bool RequiresRelay(ChannelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Con salida por país, el servidor es quien descarga (por un proxy de ese país): el dispositivo nunca
        // podría llegar al origen bloqueado, así que la fuente siempre pasa por el relay.
        if (UpstreamClientService.RequiresExit(source.ExitCountry))
        {
            return true;
        }

        // FAST: hay que limpiar beacons/anuncios de la playlist.
        if (source.Type == ChannelEntry.FastType)
        {
            return true;
        }

        // Tokenizado: el proveedor ata el token a la IP que lo pidió (el servidor), así que un cliente con otra IP recibe
        // 403 si se le entrega la URL final. Siempre relay: el servidor baja y retransmite (y renueva los efímeros).
        if (ChannelEntry.IsTokenizedType(source.Type))
        {
            return true;
        }

        // Ventana DVR desmesurada (horas de segmentos): el relay la recorta a ~60 s de vivo y el cliente arranca de inmediato.
        if (PlaylistWindows.IsLong(source.Url))
        {
            return true;
        }

        // Directo: Jellyfin habla con el proveedor. Sin relay, sin polling, sin remux.
        return false;
    }

    /// <summary>
    /// Gets a value indicating whether a URL can be delivered as-is to the player.
    /// </summary>
    /// <param name="url">Candidate URL.</param>
    /// <returns><c>true</c> for HLS/TS/MP4/DASH URLs.</returns>
    public static bool IsDirectMediaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".m3u", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".ts", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
    }
}
