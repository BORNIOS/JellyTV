using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Heurísticas para procesar la EPG local de forma más inteligente:
/// clasificar el canal por su nombre/categoría, detectar programas "placeholder"
/// (No Data, sin información) y programas que son transmisiones en vivo / shows
/// (debates, partidos, noticieros) para no reclasificarlos erróneamente con TMDB.
/// </summary>
internal static class EpgClassifier
{
    private static readonly string[] SportsMarks = ["sport", "deporte", "espn", "fox sports", "tdn", "azteca deportes", "nfl", "nba", "lmb", "liga mx", "beisbol", "futbol", "tennis", "ufc", "box"];
    private static readonly string[] NewsMarks = ["news", "noticias", "noticiero", "informativos", "milenio", "foro tv", "cnn", "televisa news"];
    private static readonly string[] KidsMarks = ["kids", "nick", "cartoon", "disney", "toon", "infantil", "ninos", "niños", "baby"];
    private static readonly string[] MoviesMarks = ["cine", "movie", "film", "peliculas", "películas", "estrenos", "amc", "paramount", "studio universal", "tnt"];

    private static readonly string[] PlaceholderTitles = ["no data", "sin datos", "por definir", "tba", "to be announced", "sin informacion", "programa por definir", "próximamente", "proximamente"];
    private static readonly string[] LiveishMarks = ["debate", "en vivo", "en directo", "transmision", "transmisión", "partido", " vs ", "liga", "campeonato", "noticiero", "noticias", "reportaje", "magazine", "show", "programa", "analisis", "análisis", "final", "juego"];

    /// <summary>
    /// Clasifica el canal según su nombre y categoría (group-title del M3U).
    /// </summary>
    /// <param name="name">Nombre del canal.</param>
    /// <param name="category">Categoría/grupo del canal.</param>
    /// <returns>Tipo de canal.</returns>
    internal static ChannelKind ClassifyChannel(string? name, string? category)
    {
        var haystack = $"{name ?? string.Empty} {category ?? string.Empty}";
        if (ContainsAny(haystack, SportsMarks))
        {
            return ChannelKind.Sports;
        }

        if (ContainsAny(haystack, KidsMarks))
        {
            return ChannelKind.Kids;
        }

        if (ContainsAny(haystack, NewsMarks))
        {
            return ChannelKind.News;
        }

        if (ContainsAny(haystack, MoviesMarks))
        {
            return ChannelKind.Movies;
        }

        return ChannelKind.General;
    }

    /// <summary>
    /// Indica si el programa es un placeholder del provider (No Data / sin información / ayuda).
    /// Estos no deben enriquecerse ni clasificarse con TMDB.
    /// </summary>
    /// <param name="title">Título del programa.</param>
    /// <param name="description">Descripción, si existe.</param>
    /// <returns>true si es placeholder.</returns>
    internal static bool IsPlaceholder(string title, string? description)
    {
        var titleLow = title.Trim();
        foreach (var marker in PlaceholderTitles)
        {
            if (titleLow.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return description is not null
            && (description.Contains("iptv-epg.org", StringComparison.OrdinalIgnoreCase)
                || description.Contains("/help", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Indica si el programa parece una transmisión en vivo / show (no una película/serie),
    /// por su título o descripción. En esos casos no se debe inyectar categoría movie/series
    /// ni metadata de TMDB (evita reclasificar un debate deportivo como película).
    /// </summary>
    /// <param name="title">Título del programa.</param>
    /// <param name="description">Descripción, si existe.</param>
    /// <returns>true si parece en vivo/show.</returns>
    internal static bool LooksLikeLiveShow(string title, string? description)
        => ContainsAny($"{title} {description ?? string.Empty}", LiveishMarks);

    private static bool ContainsAny(string haystack, IReadOnlyList<string> markers)
    {
        foreach (var marker in markers)
        {
            if (haystack.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
