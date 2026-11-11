using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Extrae la programación de la respuesta JSON que publica la web de un canal, sin conocer de antemano su formato:
/// busca la lista de objetos que más se parece a una parrilla (algo con hora de inicio y título) y reconoce los
/// nombres de campo habituales. Sirve para cualquier web que alimente su guía con un JSON, no para un sitio concreto.
/// </summary>
internal static class WebGuideExtractor
{
    private static readonly string[] StartKeys =
    [
        "starttime", "start_time", "start", "startdate", "start_date", "begin", "begintime", "begin_time", "from", "time", "timestamp",
        "airtime", "air_time", "datetime", "inicio", "hora", "fecha", "date",
    ];

    private static readonly string[] StopKeys = ["endtime", "end_time", "end", "enddate", "end_date", "stop", "stoptime", "to", "until", "finish", "fin"];

    private static readonly string[] TitleKeys =
    [
        "title", "name", "programtitle", "program_title", "programme", "program", "programname", "show", "showname", "titulo", "título", "nombre", "label",
    ];

    private static readonly string[] DescriptionKeys =
    [
        "description", "summary", "desc", "synopsis", "overview", "descripcion", "descripción", "shortdescription", "short_description", "resumen", "sinopsis",
    ];

    private static readonly string[] ImageKeys = ["image", "imageurl", "image_url", "img", "poster", "thumbnail", "thumb", "picture", "foto", "icon"];

    private static readonly Regex DateToken = new(@"\{date(?::([^}]+))?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Sustituye <c>{date}</c> (dd-MM-yyyy) y <c>{date:formato}</c> por el día pedido.
    /// </summary>
    /// <param name="template">URL con o sin marcadores.</param>
    /// <param name="day">Día a pedir.</param>
    /// <returns>La URL con el día aplicado.</returns>
    internal static string ExpandUrl(string template, DateTime day)
        => DateToken.Replace(template, m => day.ToString(m.Groups[1].Success ? m.Groups[1].Value : "dd-MM-yyyy", CultureInfo.InvariantCulture));

    /// <summary>
    /// Indica si la URL pide un día concreto (lleva el marcador de fecha).
    /// </summary>
    /// <param name="template">URL con o sin marcadores.</param>
    /// <returns><c>true</c> si hay que pedirla una vez por día.</returns>
    internal static bool IsDated(string template) => DateToken.IsMatch(template);

    /// <summary>
    /// Extrae los programas de un JSON.
    /// </summary>
    /// <param name="json">Respuesta de la web.</param>
    /// <param name="zone">Zona horaria para las horas sin offset; null significa UTC.</param>
    /// <param name="day">Día al que pertenecen las horas sueltas del tipo "20:30".</param>
    /// <returns>Programas ordenados por inicio; vacío si no se reconoce una parrilla.</returns>
    internal static IReadOnlyList<WebProgramme> FromJson(string json, TimeZoneInfo? zone, DateTime day)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<WebProgramme>();
        }

        using (document)
        {
            var best = new List<WebProgramme>();
            foreach (var array in Arrays(document.RootElement))
            {
                var parsed = Parse(array, zone, day);
                if (parsed.Count > best.Count)
                {
                    best = parsed;
                }
            }

            return Finish(best);
        }
    }

    /// <summary>
    /// Igual que <see cref="FromJson"/> pero sin calcular los fines que faltan, para poder unir varios días antes de hacerlo.
    /// </summary>
    /// <param name="json">Respuesta de la web.</param>
    /// <param name="zone">Zona horaria para las horas sin offset; null significa UTC.</param>
    /// <param name="day">Día al que pertenecen las horas sueltas del tipo "20:30".</param>
    /// <returns>Programas tal como los declara la web.</returns>
    internal static IReadOnlyList<WebProgramme> FromJsonRaw(string json, TimeZoneInfo? zone, DateTime day)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var best = new List<WebProgramme>();
            foreach (var array in Arrays(document.RootElement))
            {
                var parsed = Parse(array, zone, day);
                if (parsed.Count > best.Count)
                {
                    best = parsed;
                }
            }

            return best;
        }
        catch (JsonException)
        {
            return Array.Empty<WebProgramme>();
        }
    }

    private static IEnumerable<JsonElement> Arrays(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() >= 2 && element.EnumerateArray().Any(static e => e.ValueKind == JsonValueKind.Object))
            {
                yield return element;
            }

            foreach (var child in element.EnumerateArray())
            {
                foreach (var nested in Arrays(child))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in Arrays(property.Value))
                {
                    yield return nested;
                }
            }
        }
    }

    private static List<WebProgramme> Parse(JsonElement array, TimeZoneInfo? zone, DateTime day)
    {
        var rows = array.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object).ToList();
        var startKey = PickKey(rows, StartKeys, value => ReadTime(value, zone, day) is not null);
        var titleKey = PickKey(rows, TitleKeys, static value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()));
        if (startKey is null || titleKey is null)
        {
            return new List<WebProgramme>();
        }

        var stopKey = PickKey(rows, StopKeys.Where(k => !string.Equals(k, startKey, StringComparison.OrdinalIgnoreCase)).ToArray(), value => ReadTime(value, zone, day) is not null);
        var result = new List<WebProgramme>();
        foreach (var row in rows)
        {
            var start = Get(row, startKey) is { } s ? ReadTime(s, zone, day) : null;
            var title = Get(row, titleKey) is { ValueKind: JsonValueKind.String } t ? t.GetString()?.Trim() : null;
            if (start is null || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            DateTimeOffset? stop = stopKey is not null && Get(row, stopKey) is { } e ? ReadTime(e, zone, day) : null;
            result.Add(new WebProgramme(start.Value, stop, title, FirstText(row, DescriptionKeys), FirstText(row, ImageKeys, static v => v.StartsWith("http", StringComparison.OrdinalIgnoreCase))));
        }

        return result;
    }

    /// <summary>
    /// Completa los fines que faltan con el inicio del programa siguiente, quita duplicados y ordena.
    /// </summary>
    internal static IReadOnlyList<WebProgramme> Finish(IEnumerable<WebProgramme> list)
    {
        var ordered = list.OrderBy(static p => p.Start).GroupBy(static p => p.Start).Select(static g => g.First()).ToList();
        var result = new List<WebProgramme>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var next = i + 1 < ordered.Count ? ordered[i + 1].Start : (DateTimeOffset?)null;
            var stop = current.Stop is { } declared && declared > current.Start ? declared : next ?? current.Start.AddHours(1);
            result.Add(current with { Stop = stop });
        }

        return result;
    }

    private static string? PickKey(List<JsonElement> rows, string[] candidates, Func<JsonElement, bool> accepts)
    {
        foreach (var candidate in candidates)
        {
            var valid = 0;
            foreach (var row in rows)
            {
                if (Get(row, candidate) is { } value && accepts(value))
                {
                    valid++;
                }
            }

            if (valid >= Math.Max(2, rows.Count / 2))
            {
                return candidate;
            }
        }

        return null;
    }

    private static JsonElement? Get(JsonElement row, string key)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? FirstText(JsonElement row, string[] keys, Func<string, bool>? accepts = null)
    {
        foreach (var key in keys)
        {
            if (Get(row, key) is { ValueKind: JsonValueKind.String } value && value.GetString()?.Trim() is { Length: > 0 } text && (accepts?.Invoke(text) ?? true))
            {
                return text;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadTime(JsonElement value, TimeZoneInfo? zone, DateTime day)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            // Más de 11 cifras son milisegundos; menos, segundos.
            if (number >= 100_000_000_000)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(number);
            }

            return number >= 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(number) : null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString()?.Trim() is not { Length: > 0 } text)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed) && text.Length > 5)
        {
            // Sin offset explícito la hora es del lugar del canal: se reinterpreta en su zona.
            if (zone is not null && !HasOffset(text))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified), zone.GetUtcOffset(parsed.DateTime));
            }

            return parsed;
        }

        var clock = Regex.Match(text, @"^(\d{1,2}):(\d{2})(?::\d{2})?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (clock.Success)
        {
            var local = day.Date.AddHours(int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture)).AddMinutes(int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture));
            return zone is null ? new DateTimeOffset(local, TimeSpan.Zero) : new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        return null;
    }

    private static bool HasOffset(string text)
        => text.EndsWith('Z') || Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
