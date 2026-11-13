using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Reduce las categorías de cada proveedor (decenas de nombres distintos) al puñado que usa Jellyfin en Live TV
/// (Movies, Series, Kids, News, Sports) más Music y General, para no llenar la lista de micro categorías.
/// </summary>
internal static class ProviderCategories
{
    /// <summary>Categoría cuando nada indica otra.</summary>
    internal const string General = "General";

    // El orden importa: gana la primera que coincida (un canal infantil de películas es Kids, no Movies).
    private static readonly (string Category, string[] Keywords)[] Rules =
    [
        ("Kids", ["kids", "kid ", "infantil", "nino", "children", "cartoon", "toon", "animacion", "animation", "junior", "familia", "family"]),
        ("News", ["news", "noticia", "informativ", "noticiero", "headline"]),
        ("Sports", ["sport", "deporte", "futbol", "soccer", "nfl", "nba", "mlb", "nhl", "boxeo", "box ", "wrestling", "lucha", "racing", "motor", "padel", "poker", "combat", "golf", "tenis", "tennis"]),
        ("Music", ["music", "musica", "radio", "concierto", "concert", "karaoke", "hits", "mtv"]),
        ("Movies", ["movie", "pelicula", "cine", "film", "cinema", "horror", "terror", "western", "accion", "action"]),
        ("Series", ["series", "serie", "novela", "drama", "sitcom", "comedia", "comedy", "crimen", "crime", "reality", "romance", "thriller", "doc", "historia", "misterio"])
    ];

    /// <summary>Elige la categoría del plugin para un canal de un proveedor.</summary>
    /// <param name="providerGroup">Categoría que le da el proveedor, si la tiene.</param>
    /// <param name="channelName">Nombre del canal, por si la categoría no basta.</param>
    /// <returns>Una de: Kids, News, Sports, Music, Movies, Series, General.</returns>
    internal static string Map(string? providerGroup, string? channelName)
        => Match(providerGroup) ?? Match(channelName) ?? General;

    private static string? Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var haystack = " " + Fold(text) + " ";
        foreach (var (category, keywords) in Rules)
        {
            foreach (var keyword in keywords)
            {
                if (haystack.Contains(keyword, StringComparison.Ordinal))
                {
                    return category;
                }
            }
        }

        return null;
    }

    // Minúsculas y sin acentos, para que "Películas" y "peliculas" cuenten igual.
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
