using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Catálogo de recetas para leer guías en HTML: las que vienen incluidas y las que el usuario añade como JSON en los
/// ajustes. Una receta propia con el mismo dominio que una incluida la reemplaza.
/// </summary>
internal static class HtmlGuideRecipes
{
    /// <summary>
    /// Páginas de canal de GatoTV: una tabla por día con hora de inicio, hora de fin y título en cada fila. Las horas
    /// están en hora de México.
    /// </summary>
    internal static readonly HtmlGuideRecipe GatoTv = new()
    {
        Name = "GatoTV (página de canal)",
        Host = "gatotv.com",
        DayUrl = "{url}/{date:yyyy-MM-dd}",
        Row = "<tr class=\"tbl_EPG_row[^\"]*\">(?:(?!</tr>).)*?<time datetime=\"(?<start>\\d{1,2}:\\d{2})\"(?:(?!</tr>).)*?<time datetime=\"(?<stop>\\d{1,2}:\\d{2})\"(?:(?!</tr>).)*?div_program_title_on_channel[^>]*>\\s*<span>(?<title>[^<]*)</span>",
        Logo = "<meta itemprop=\"image\" content=\"(?<logo>[^\"]+)\"",
        Zone = "America/Mexico_City",
        Days = 3,
        DelayMs = 800,
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Busca la receta que corresponde a una URL.
    /// </summary>
    /// <param name="url">URL de la guía del canal.</param>
    /// <param name="customJson">Recetas del usuario (lista JSON), o vacío.</param>
    /// <returns>La receta, o null si el dominio no tiene ninguna.</returns>
    internal static HtmlGuideRecipe? Find(string url, string? customJson)
    {
        if (!Uri.TryCreate(url.Replace("{", "x", StringComparison.Ordinal).Replace("}", "x", StringComparison.Ordinal), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        return Custom(customJson).Concat(new[] { GatoTv })
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Host)
                && (host.Equals(r.Host, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + r.Host, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Sustituye en la plantilla del día los grupos con nombre que la receta saca de la URL pegada por el usuario.
    /// </summary>
    /// <param name="recipe">Receta de la página.</param>
    /// <param name="pastedUrl">URL que el usuario pegó.</param>
    /// <param name="dayTemplate">Plantilla de la URL de un día.</param>
    /// <returns>La plantilla con los <c>{nombre}</c> reemplazados; igual si la receta no pide nada o la URL no encaja.</returns>
    internal static string ApplyUrlParts(HtmlGuideRecipe recipe, string pastedUrl, string dayTemplate)
    {
        if (string.IsNullOrWhiteSpace(recipe.UrlFrom))
        {
            return dayTemplate;
        }

        try
        {
            var regex = new System.Text.RegularExpressions.Regex(recipe.UrlFrom, System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            var match = regex.Match(pastedUrl);
            if (!match.Success)
            {
                return dayTemplate;
            }

            foreach (var name in regex.GetGroupNames().Where(static n => !int.TryParse(n, out _)))
            {
                dayTemplate = dayTemplate.Replace("{" + name + "}", Uri.EscapeDataString(match.Groups[name].Value), StringComparison.Ordinal);
            }

            return dayTemplate;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return dayTemplate;
        }
    }

    /// <summary>
    /// Lee las recetas del usuario. Una lista mal escrita se ignora en vez de romper la sincronización.
    /// </summary>
    /// <param name="json">Lista JSON de recetas.</param>
    /// <returns>Las recetas válidas.</returns>
    internal static IReadOnlyList<HtmlGuideRecipe> Custom(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<HtmlGuideRecipe>();
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<HtmlGuideRecipe>>(json, JsonOptions);
            return list?.Where(static r => !string.IsNullOrWhiteSpace(r.Host) && !string.IsNullOrWhiteSpace(r.Row)).ToList() ?? new List<HtmlGuideRecipe>();
        }
        catch (JsonException)
        {
            return Array.Empty<HtmlGuideRecipe>();
        }
    }
}
