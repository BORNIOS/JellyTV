using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Extrae la programación de una página HTML aplicando una receta (una expresión regular con grupos con nombre y una
/// zona horaria). No sabe nada de ningún sitio concreto: todo lo específico está en la receta.
/// </summary>
internal static class HtmlGuideExtractor
{
    private static readonly string[] TimeFormats = ["H:mm", "HH:mm", "h:mm tt", "hh:mm tt", "H.mm", "HH:mm:ss"];

    /// <summary>
    /// Convierte las filas de una página en programas de un día.
    /// </summary>
    /// <param name="html">HTML de la página.</param>
    /// <param name="recipe">Receta que describe sus filas.</param>
    /// <param name="day">Día al que pertenece la página, en la zona de la receta.</param>
    /// <returns>Programas con inicio y, si la página lo trae, fin.</returns>
    internal static IReadOnlyList<WebProgramme> Extract(string html, HtmlGuideRecipe recipe, DateTime day)
    {
        var zone = ResolveZone(DetectZone(html, recipe) ?? recipe.Zone);
        Regex regex;
        try
        {
            regex = new Regex(recipe.Row, RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(3));
        }
        catch (ArgumentException)
        {
            return Array.Empty<WebProgramme>();
        }

        var result = new List<WebProgramme>();
        var offset = 0;
        TimeSpan? previous = null;
        try
        {
            foreach (Match match in regex.Matches(html))
            {
                var title = Clean(match.Groups["title"].Value);
                if (title.Length == 0 || !TryTime(match.Groups["start"].Value, out var start))
                {
                    continue;
                }

                // Una hora menor que la anterior indica que la página ya pasó la medianoche.
                if (previous is { } last && start < last)
                {
                    offset++;
                }

                previous = start;
                var begin = At(day.Date.AddDays(offset), start, zone);
                DateTimeOffset? end = null;
                if (match.Groups["stop"].Success && TryTime(match.Groups["stop"].Value, out var stop))
                {
                    var finish = At(day.Date.AddDays(offset), stop, zone);
                    end = finish <= begin ? finish.AddDays(1) : finish;
                }

                var description = match.Groups["desc"].Success ? Clean(match.Groups["desc"].Value) : string.Empty;
                var image = match.Groups["image"].Success ? WebUtility.HtmlDecode(match.Groups["image"].Value).Trim() : string.Empty;
                result.Add(new WebProgramme(begin, end, title, description.Length > 0 ? description : null, image.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? image : null));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Una receta que tarda demasiado se descarta en vez de colgar la sincronización.
        }

        return result;
    }

    /// <summary>
    /// Lee de la propia página la zona en que muestra las horas, cuando la receta sabe dónde está. Hay sitios que eligen la zona
    /// por la IP de quien consulta, así que una zona fija en la receta daría horas corridas según desde dónde se pida.
    /// </summary>
    /// <param name="html">HTML de la página.</param>
    /// <param name="recipe">Receta de la página.</param>
    /// <returns>El identificador de zona leído, o null si la receta no lo pide o la página no lo trae.</returns>
    private static string? DetectZone(string html, HtmlGuideRecipe recipe)
    {
        if (string.IsNullOrWhiteSpace(recipe.ZoneFrom))
        {
            return null;
        }

        try
        {
            var match = Regex.Match(html, recipe.ZoneFrom, RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            var id = match.Success ? WebUtility.HtmlDecode(match.Groups["zone"].Value).Trim() : string.Empty;
            if (id.Length == 0)
            {
                return null;
            }

            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return id;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saca de la página la URL del logo del canal, si la receta sabe dónde está.
    /// </summary>
    /// <param name="html">HTML de la página.</param>
    /// <param name="recipe">Receta de la página.</param>
    /// <returns>La URL absoluta del logo, o null.</returns>
    internal static string? ExtractLogo(string html, HtmlGuideRecipe recipe)
    {
        if (string.IsNullOrWhiteSpace(recipe.Logo))
        {
            return null;
        }

        try
        {
            var match = Regex.Match(html, recipe.Logo, RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            var url = match.Success ? WebUtility.HtmlDecode(match.Groups["logo"].Value).Trim() : string.Empty;
            return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string Clean(string value)
        => Regex.Replace(WebUtility.HtmlDecode(value), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)).Trim();

    private static bool TryTime(string value, out TimeSpan time)
    {
        time = default;
        if (DateTime.TryParseExact(value.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateTime.TryParse(value.Trim(), CultureInfo.GetCultureInfo("es-MX"), DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            time = parsed.TimeOfDay;
            return true;
        }

        return false;
    }

    private static DateTimeOffset At(DateTime date, TimeSpan time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.IsInvalidTime(local) ? zone.GetUtcOffset(local.AddHours(1)) : zone.GetUtcOffset(local));
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
