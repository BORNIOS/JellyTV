using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Tamaño de la ventana de cada fuente, aprendido de su playlist de medios en el health check. Algunos proveedores publican
/// horas de DVR en la playlist en vivo (decenas de miles de segmentos): el navegador baja y analiza cientos de KB en cada
/// recarga y tarda en arrancar. Esas fuentes pasan por el relay, que recorta la playlist a unos 60 s de vivo.
/// </summary>
internal static class PlaylistWindows
{
    /// <summary>Segmentos a partir de los cuales una ventana se considera desmesurada (unos 3 min con segmentos de 6 s).</summary>
    internal const int LongWindowSegments = 30;

    private static readonly ConcurrentDictionary<string, int> SegmentsByUrl = new(StringComparer.Ordinal);

    /// <summary>Indica si la fuente publica una ventana desmesurada.</summary>
    /// <param name="sourceUrl">URL de la fuente tal como está configurada.</param>
    /// <returns><c>true</c> si la última playlist vista superaba <see cref="LongWindowSegments"/>.</returns>
    internal static bool IsLong(string sourceUrl) =>
        SegmentsByUrl.TryGetValue(sourceUrl, out var segments) && segments > LongWindowSegments;

    /// <summary>Registra el tamaño de la playlist de medios de una fuente.</summary>
    /// <param name="sourceUrl">URL de la fuente configurada (clave).</param>
    /// <param name="mediaPlaylist">Contenido de la playlist de medios (no del master).</param>
    internal static void Record(string sourceUrl, string mediaPlaylist)
    {
        var segments = 0;
        foreach (var line in mediaPlaylist.Split('\n'))
        {
            if (line.Length > 0 && line[0] != '#' && !string.IsNullOrWhiteSpace(line))
            {
                segments++;
            }
        }

        SegmentsByUrl[sourceUrl] = segments;
    }
}
