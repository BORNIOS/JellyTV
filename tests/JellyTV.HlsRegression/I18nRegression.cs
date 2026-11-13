using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>La página de configuración se traduce con un diccionario español → inglés: nada puede quedarse sin traducción ni perder un marcador.</summary>
internal static class I18nRegression
{
    internal static void Run()
    {
        var page = ReadPage();

        var dictionary = ReadObject(page, "var JT_EN = ", "var JT_EN_HTML = ");
        var htmlDictionary = ReadObject(page, "var JT_EN_HTML = ", "var JT_LANG");
        Check(dictionary.Count > 300, "el diccionario inglés tiene las cadenas de la página");

        // Toda plantilla t('…') del código tiene traducción (si no, en inglés se vería español).
        var calls = Regex.Matches(page, @"(?<![A-Za-z0-9_.$])t\('((?:[^'\\]|\\.)*)'", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(static m => Unescape(m.Groups[1].Value))
            .Distinct()
            .ToList();
        Check(calls.Count > 20, "se encontraron las llamadas t() de la página");
        var missing = calls.Where(c => !dictionary.ContainsKey(c)).ToList();
        Check(missing.Count == 0, "plantillas t() sin traducción al inglés: " + string.Join(" | ", missing.Take(5)));

        // Cada traducción conserva los mismos marcadores {0}, {1}…
        var placeholder = new Regex(@"\{\d+\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        var broken = dictionary.Where(kv => !placeholder.Matches(kv.Key).Select(static m => m.Value).OrderBy(static v => v, StringComparer.Ordinal)
            .SequenceEqual(placeholder.Matches(kv.Value).Select(static m => m.Value).OrderBy(static v => v, StringComparer.Ordinal))).Select(static kv => kv.Key).ToList();
        Check(broken.Count == 0, "traducciones que pierden o cambian un marcador: " + string.Join(" | ", broken.Take(5)));

        // Ninguna traducción puede estar vacía.
        Check(dictionary.All(static kv => !string.IsNullOrWhiteSpace(kv.Value)), "ninguna traducción está vacía");

        // Cada párrafo marcado con data-i18n tiene su HTML en inglés, con las mismas etiquetas.
        var ids = Regex.Matches(page[..page.IndexOf("<script", StringComparison.Ordinal)], @"data-i18n=""(m\d+)""", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(static m => m.Groups[1].Value).ToList();
        Check(ids.Count >= 8 && ids.All(htmlDictionary.ContainsKey), "cada párrafo con formato tiene su traducción");

        // El texto de los proveedores del catálogo sale de un solo sitio y lleva la misma estructura.
        foreach (var provider in new[] { "pluto", "plex", "tubi", "runtime" })
        {
            Check(Regex.IsMatch(page, provider + @": \['[^']+', (null|'[^']+')\]", RegexOptions.None, TimeSpan.FromSeconds(1)), "nota del proveedor " + provider + " con el formato único");
        }

        Console.WriteLine($"PASS: i18n de la página ({dictionary.Count} cadenas, {calls.Count} plantillas t(), {ids.Count} párrafos con formato).");
    }

    private static Dictionary<string, string> ReadObject(string page, string startMarker, string endMarker)
    {
        var start = page.IndexOf(startMarker, StringComparison.Ordinal);
        var end = page.IndexOf(endMarker, start, StringComparison.Ordinal);
        Check(start >= 0 && end > start, "se encontró " + startMarker.Trim());
        var json = page[(start + startMarker.Length)..end].Trim().TrimEnd(';').Replace("<\\/", "</", StringComparison.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
    }

    // Literal de JavaScript → texto real (lo que ve el diccionario en ejecución).
    private static string Unescape(string literal)
        => Regex.Replace(
            literal.Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal),
            @"\\u([0-9A-Fa-f]{4})",
            static m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString(),
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

    // La página se escribe en piezas (Configuration/Page); aquí se unen igual que en la compilación.
    private static string ReadPage()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var folder = Path.Combine(dir.FullName, "Jellyfin.Plugin.JellyTV", "Configuration", "Page");
            if (Directory.Exists(folder))
            {
                var page = File.ReadAllText(Path.Combine(folder, "page.html"));
                foreach (var piece in new[] { "page.css", "i18n.js", "app.js" })
                {
                    var marker = new Regex("<!--@include " + Regex.Escape(piece) + @"-->\r?\n", RegexOptions.None, TimeSpan.FromSeconds(1));
                    Check(marker.IsMatch(page), "page.html incluye " + piece);
                    var text = File.ReadAllText(Path.Combine(folder, piece));
                    page = marker.Replace(page, _ => text);
                }

                return page;
            }
        }

        throw new FileNotFoundException("No se encontró Configuration/Page subiendo desde " + AppContext.BaseDirectory);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
