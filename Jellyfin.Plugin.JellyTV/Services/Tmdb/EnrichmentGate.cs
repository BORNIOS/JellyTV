using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Compuertas de precisión para el enriquecimiento TMDB. Un título igual no basta: la fuente XMLTV ya dice si el
/// programa es un episodio de serie, de qué año es y de qué género, y TMDB solo puede aportar si no contradice eso.
/// Es preferible dejar un programa sin arte que mezclarlo con otra obra (una serie turca marcada como documental).
/// </summary>
internal static class EnrichmentGate
{
    private static readonly Regex EpisodeInDescription = new(
        @"^\s*(?:S|T|Temp(?:orada)?\.?\s*)(?<s>\d{1,2})\s*[·\-]?\s*(?:E|Ep|Cap(?:ítulo|itulo)?\.?)\s*\d{1,4}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly string[] DocumentaryKeys = ["documental", "documentary", "docu"];

    private static readonly string[] FictionKeys =
    [
        "drama", "telenovela", "novela", "serie", "series", "accion", "acción", "comedia", "comedy", "thriller",
        "romance", "suspenso", "terror", "horror", "crimen", "aventura", "fantasia", "fantasía", "misterio"
    ];

    private static readonly string[] MovieKeys = ["pelicula", "película", "movie", "film", "cine"];

    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "para", "como", "pero", "sobre", "entre", "desde", "hasta", "donde", "cuando", "porque", "aunque", "tambien", "cada",
        "esta", "este", "estos", "estas", "tiene", "tienen", "hacer", "ahora", "todos", "todas", "mientras", "despues", "antes",
        "episodio", "capitulo", "temporada", "programa", "serie", "historia", "vida", "mundo",
    };

    private static readonly char[] TokenSeparators = [' ', '\n', '\r', '\t'];

    private static readonly char[] WordSeparators = [' ', '\n', '\r', '\t', ',', '.', ';', ':', '"', '(', ')', '¿', '?', '¡', '!'];

    /// <summary>
    /// Deduce de la fuente si el programa es un episodio de serie (<c>tv</c>) o una película (<c>movie</c>).
    /// </summary>
    /// <param name="data">Metadatos del programa en la fuente.</param>
    /// <returns><c>tv</c>, <c>movie</c> o null cuando la fuente no lo dice.</returns>
    internal static string? ExpectedMediaType(EpgProgrammeData data)
    {
        if (!string.IsNullOrWhiteSpace(data.EpisodeNum) || EpisodeInDescription.IsMatch(data.Description ?? string.Empty))
        {
            return "tv";
        }

        var category = (data.Category ?? string.Empty).ToLowerInvariant();
        return MovieKeys.Any(k => category.Contains(k, StringComparison.Ordinal)) ? "movie" : null;
    }

    /// <summary>
    /// Temporada declarada por la fuente ("S2 E5"), si la hay.
    /// </summary>
    /// <param name="data">Metadatos del programa en la fuente.</param>
    /// <returns>Número de temporada o null.</returns>
    internal static int? Season(EpgProgrammeData data)
    {
        var text = !string.IsNullOrWhiteSpace(data.EpisodeNum) ? data.EpisodeNum! : data.Description ?? string.Empty;
        var match = EpisodeInDescription.Match(text);
        return match.Success && int.TryParse(match.Groups["s"].Value, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    /// <summary>
    /// Descarta de entrada los resultados de búsqueda de tipo o año imposibles para este programa.
    /// </summary>
    /// <param name="item">Programa que se busca.</param>
    /// <param name="candidate">Resultado de TMDB.</param>
    /// <returns><c>true</c> si el candidato sigue siendo plausible.</returns>
    internal static bool AcceptsCandidate(UncachedProgrammeItem item, TmdbSearchResult candidate)
    {
        if (item.ExpectedMediaType is { } expected
            && !string.Equals(expected, candidate.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return YearPlausible(item, ParseYear(candidate.ReleaseDate ?? candidate.FirstAirDate));
    }

    /// <summary>
    /// Indica si el año de una obra es compatible con lo que la fuente dice del programa.
    /// </summary>
    /// <param name="item">Programa que se busca.</param>
    /// <param name="year">Año de la obra candidata.</param>
    /// <returns><c>true</c> si es plausible o si no hay con qué contrastar.</returns>
    internal static bool AcceptsYear(UncachedProgrammeItem item, int? year) => YearPlausible(item, year);

    /// <summary>
    /// Verificación final con los metadatos completos. Hay contradicciones que rechazan sin remedio (el género
    /// choca con la fuente) y el resto se decide por evidencia acumulada: año, nombres compartidos con la sinopsis,
    /// título idéntico y popularidad de la obra. Así una serie conocida sin año en la fuente entra, y una
    /// homónima oscura no.
    /// </summary>
    /// <param name="item">Programa que se busca.</param>
    /// <param name="meta">Metadatos devueltos por el proveedor.</param>
    /// <param name="exactTitle">Si el título de la obra coincide exactamente (normalizado) con el del programa.</param>
    /// <param name="reason">Motivo del rechazo, para el log.</param>
    /// <returns><c>true</c> si el emparejamiento es confiable.</returns>
    internal static bool AcceptsMetadata(UncachedProgrammeItem item, TmdbMetadata meta, bool exactTitle, out string reason)
    {
        reason = string.Empty;

        var category = (item.Category ?? string.Empty).ToLowerInvariant();
        var sourceIsFiction = FictionKeys.Any(k => category.Contains(k, StringComparison.Ordinal)) || item.ExpectedMediaType == "tv";
        var sourceIsDocumentary = DocumentaryKeys.Any(k => category.Contains(k, StringComparison.Ordinal));
        var metaIsDocumentary = meta.Genres.Any(g => DocumentaryKeys.Any(k => g.Contains(k, StringComparison.OrdinalIgnoreCase)));
        if (metaIsDocumentary && !sourceIsDocumentary && sourceIsFiction)
        {
            reason = "el proveedor lo marca como documental pero la fuente lo da como ficción";
            return false;
        }

        if (sourceIsDocumentary && !metaIsDocumentary && meta.Genres.Count > 0)
        {
            reason = "la fuente lo da como documental y el proveedor no";
            return false;
        }

        var evidence = 0;
        if (item.Year is { } sourceYear && meta.ReleaseYear is { } candidateYear)
        {
            var delta = Math.Abs(sourceYear - candidateYear);
            evidence += delta <= 1 ? 3 : delta == 2 ? 1 : 0;
        }

        if (!string.IsNullOrWhiteSpace(item.Description) && SharesText(item.Description, meta))
        {
            evidence += 2;
        }

        if (exactTitle)
        {
            evidence++;
        }

        // Otra fuente independiente que describe la misma emisión con el mismo título refuerza que el programa es ese.
        evidence += Math.Min(item.Corroborated, 2);

        if ((meta.VoteCount ?? 0) >= 20)
        {
            evidence++;
        }

        var needed = exactTitle ? 2 : 3;
        if (evidence >= needed)
        {
            return true;
        }

        reason = $"evidencia insuficiente ({evidence} de {needed}): sin año coincidente ni texto en común";
        return false;
    }

    private static bool YearPlausible(UncachedProgrammeItem item, int? candidateYear)
    {
        if (item.Year is not { } sourceYear || candidateYear is not { } year)
        {
            return true;
        }

        return item.ExpectedMediaType switch
        {
            // Una película se estrenó en el año que declara la fuente (con margen de un par de años).
            "movie" => Math.Abs(sourceYear - year) <= 2,

            // Una serie no puede haber empezado después del año del episodio y, si la fuente dice la temporada,
            // tampoco antes de lo que esas temporadas permiten.
            "tv" => year <= sourceYear + 1 && sourceYear - year <= Math.Max(1, (item.Season ?? 1) + 1),
            _ => Math.Abs(sourceYear - year) <= 3,
        };
    }

    private static bool SharesText(string sourceDescription, TmdbMetadata meta)
    {
        var tmdbText = string.Join(' ', new[] { meta.Overview }.Concat(meta.Cast.Select(static c => c.Name)).Where(static t => !string.IsNullOrWhiteSpace(t)));
        if (string.IsNullOrWhiteSpace(tmdbText))
        {
            return false;
        }

        var known = new HashSet<string>(Words(tmdbText), StringComparer.Ordinal);

        // Un nombre propio en común basta; si no, hacen falta al menos dos palabras con peso.
        if (ProperNouns(sourceDescription).Any(word => known.Contains(Normalize(word))))
        {
            return true;
        }

        return Words(sourceDescription).Count(w => w.Length >= 5 && !CommonWords.Contains(w) && known.Contains(w)) >= 2;
    }

    private static IEnumerable<string> ProperNouns(string text)
    {
        // Palabras con mayúscula inicial que no abren oración: nombres de personajes y lugares.
        var tokens = text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        var sentenceStart = true;
        foreach (var raw in tokens)
        {
            var word = raw.Trim('.', ',', ';', ':', '"', '\'', '!', '?', '(', ')', '¿', '¡', '…');
            if (word.Length >= 4 && char.IsUpper(word[0]) && !sentenceStart)
            {
                yield return word;
            }

            sentenceStart = raw.EndsWith('.') || raw.EndsWith('!') || raw.EndsWith('?') || raw.EndsWith('\n');
        }
    }

    private static IEnumerable<string> Words(string text)
        => text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(static w => w.Length >= 4);

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static int? ParseYear(string? date)
        => !string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date[..4], CultureInfo.InvariantCulture, out var y) ? y : null;
}
