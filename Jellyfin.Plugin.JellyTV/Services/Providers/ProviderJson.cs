using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Lectura tolerante de los JSON de los proveedores: un campo que falta o cambia de tipo da <c>null</c>, no una excepción.
/// </summary>
internal static class ProviderJson
{
    /// <summary>Texto de un campo, o <c>null</c> si falta, está vacío o no es texto.</summary>
    /// <param name="element">Objeto JSON.</param>
    /// <param name="name">Nombre del campo.</param>
    /// <returns>El texto, o <c>null</c>.</returns>
    internal static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Entero de un campo, o <c>null</c> si falta o no es numérico.</summary>
    /// <param name="element">Objeto JSON.</param>
    /// <param name="name">Nombre del campo.</param>
    /// <returns>El número, o <c>null</c>.</returns>
    internal static int? Int(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>Entero largo de un campo (por ejemplo, una marca de tiempo en segundos), o <c>null</c>.</summary>
    /// <param name="element">Objeto JSON.</param>
    /// <param name="name">Nombre del campo.</param>
    /// <returns>El número, o <c>null</c>.</returns>
    internal static long? Long(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>Objeto de un campo, o un elemento vacío si falta (así se encadena sin comprobar cada nivel).</summary>
    /// <param name="element">Objeto JSON.</param>
    /// <param name="name">Nombre del campo.</param>
    /// <returns>El objeto, o un elemento sin tipo.</returns>
    internal static JsonElement Obj(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>Elementos de un arreglo, o nada si el campo falta o no es un arreglo.</summary>
    /// <param name="element">Objeto JSON.</param>
    /// <param name="name">Nombre del campo.</param>
    /// <returns>Los elementos del arreglo.</returns>
    internal static IEnumerable<JsonElement> Items(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in value.EnumerateArray())
        {
            yield return item;
        }
    }
}
