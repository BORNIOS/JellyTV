using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Lee (con caché por fecha de modificación del archivo y por zona configurada) el XMLTV fusionado
/// que genera el plugin y lo expone por canal (tvg-id). Así la guía se inyecta de forma nativa en
/// Jellyfin vía <c>ILiveTvService.GetProgramsAsync</c>, sin provider XMLTV externo ni mapeo.
/// </summary>
internal static class LocalEpgReader
{
    private const string WallFormat = "yyyyMMddHHmmss";
    private const string OffsetFormat = "yyyyMMddHHmmss zzz";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<LocalProgramme>> ProgrammesByChannel = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _loadedFileWriteTimeUtc;
    private static string _loadedZoneId = string.Empty;

    /// <summary>
    /// Devuelve los programas de un canal (tvg-id) que caen dentro de la ventana [start,end).
    /// Recarga el archivo automáticamente si cambió en disco o la zona configurada.
    /// </summary>
    /// <param name="tvgId">tvg-id del canal.</param>
    /// <param name="startUtc">Inicio de la ventana (UTC).</param>
    /// <param name="endUtc">Fin de la ventana (UTC).</param>
    /// <returns>Programas en ventana.</returns>
    internal static IReadOnlyList<LocalProgramme> GetPrograms(string tvgId, DateTime startUtc, DateTime endUtc)
    {
        EnsureLoaded();
        lock (Gate)
        {
            if (!ProgrammesByChannel.TryGetValue(tvgId, out var all))
            {
                return [];
            }

            var result = new List<LocalProgramme>();
            foreach (var p in all)
            {
                if (p.StartUtc < endUtc && p.EndUtc > startUtc)
                {
                    result.Add(p);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Resume la programación que la guía local tiene de un canal (para el panel de cobertura).
    /// </summary>
    /// <param name="key">Clave de guía del canal.</param>
    /// <returns>Cantidad de programas y rango que cubren.</returns>
    internal static EpgCoverage GetCoverage(string key)
    {
        EnsureLoaded();
        lock (Gate)
        {
            if (!ProgrammesByChannel.TryGetValue(key, out var all) || all.Count == 0)
            {
                return new EpgCoverage(0, null, null);
            }

            return new EpgCoverage(all.Count, all.Min(static p => p.StartUtc), all.Max(static p => p.EndUtc));
        }
    }

    private static void EnsureLoaded()
    {
        var path = Plugin.Instance?.EpgFilter.EpgFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var zoneId = ResolveZoneId();
        var writeTime = File.GetLastWriteTimeUtc(path);
        lock (Gate)
        {
            if (writeTime == _loadedFileWriteTimeUtc && string.Equals(zoneId, _loadedZoneId, StringComparison.Ordinal))
            {
                return;
            }

            _loadedFileWriteTimeUtc = writeTime;
            _loadedZoneId = zoneId;
            try
            {
                var map = ParseFile(path, GetZone(zoneId));
                ProgrammesByChannel.Clear();
                foreach (var kv in map)
                {
                    ProgrammesByChannel.Add(kv.Key, kv.Value);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"[EPG] No se pudo leer la guía local ({path}): {ex.Message}");
                ProgrammesByChannel.Clear();
            }
        }
    }

    /// <summary>
    /// Zona IANA configurada (vacío/"UTC" → null = respetar el offset declarado en el XMLTV).
    /// </summary>
    private static string ResolveZoneId()
    {
        var configured = Plugin.Instance?.Configuration?.EpgTimezone?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configured) || configured.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return configured;
    }

    private static TimeZoneInfo? GetZone(string zoneId)
    {
        if (zoneId.Length == 0)
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[EPG] Zona horaria inválida '{zoneId}' ({ex.Message}). Se respetará el offset declarado.");
            return null;
        }
    }

    private static Dictionary<string, List<LocalProgramme>> ParseFile(string path, TimeZoneInfo? zone)
    {
        var map = new Dictionary<string, List<LocalProgramme>>(StringComparer.OrdinalIgnoreCase);

        using var fs = File.OpenRead(path);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, Async = false };
        using var reader = XmlReader.Create(fs, settings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme")
            {
                continue;
            }

            var program = ParseProgramme(reader, zone);
            if (program is null)
            {
                continue;
            }

            if (!map.TryGetValue(program.Channel, out var list))
            {
                list = new List<LocalProgramme>();
                map.Add(program.Channel, list);
            }

            list.Add(program);
        }

        return map;
    }

    private static LocalProgramme? ParseProgramme(XmlReader reader, TimeZoneInfo? zone)
    {
        XElement element;
        try
        {
            using (var subtree = reader.ReadSubtree())
            {
                element = XElement.Load(subtree);
            }
        }
        catch
        {
            return null;
        }

        var channel = (string?)element.Attribute("channel");
        if (string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        var startText = (string?)element.Attribute("start");
        var stopText = (string?)element.Attribute("stop");
        if (!TryParseTime(startText, zone, out var start) || !TryParseTime(stopText, zone, out var stop) || stop <= start)
        {
            return null;
        }

        var title = ExtractTitle(element);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var categories = element.Elements("category")
            .Select(c => c.Value.Trim())
            .Where(c => c.Length > 0)
            .ToList();
        var (poster, backdrop) = PickImages(element);
        var rating = (string?)element.Element("rating")?.Value;
        var description = element.Elements("desc").Select(d => d.Value.Trim()).FirstOrDefault(d => d.Length > 0);

        return new LocalProgramme(channel, start, stop, title, description, categories, poster, backdrop, rating);
    }

    /// <summary>
    /// Convierte un timestamp XMLTV a UTC respetando el offset declarado. Un valor sin offset se
    /// interpreta como hora de pared en la zona configurada o, sin zona, como UTC.
    /// </summary>
    private static bool TryParseTime(string? text, TimeZoneInfo? zone, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // La guía la escribe el propio plugin con el offset real de cada programa: ese offset manda. Reinterpretar la
        // hora de pared en la zona configurada desplazaba toda la programación (p. ej. +7 h con America/Hermosillo).
        if (trimmed.Length >= WallFormat.Length + 5
            && DateTime.TryParseExact(trimmed[..WallFormat.Length], WallFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var declaredWall))
        {
            var tail = trimmed[WallFormat.Length..].Trim();
            if (tail.Length == 5
                && (tail[0] == '+' || tail[0] == '-')
                && int.TryParse(tail.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                && int.TryParse(tail.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            {
                var offset = TimeSpan.FromMinutes((hours * 60) + minutes);
                utc = DateTime.SpecifyKind(declaredWall - (tail[0] == '-' ? -offset : offset), DateTimeKind.Utc);
                return true;
            }
        }

        if (zone is not null)
        {
            if (trimmed.Length >= WallFormat.Length
                && DateTime.TryParseExact(trimmed[..WallFormat.Length], WallFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            {
                try
                {
                    utc = TimeZoneInfo.ConvertTimeToUtc(wall, zone);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false; // hora inexistente por cambio de horario (DST)
                }
            }

            return false;
        }

        if (DateTimeOffset.TryParseExact(trimmed, OffsetFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            || DateTimeOffset.TryParseExact(trimmed, WallFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reparte los <c>&lt;icon&gt;</c> de un programa entre póster (vertical o cuadrada) y fondo (apaisada), según el
    /// ancho y el alto declarados. Una imagen sin dimensiones se toma por póster, que es lo que Jellyfin espera como
    /// imagen principal.
    /// </summary>
    private static (string? Poster, string? Backdrop) PickImages(XElement element)
    {
        string? poster = null;
        string? backdrop = null;
        foreach (var icon in element.Elements("icon"))
        {
            var src = (string?)icon.Attribute("src");
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            int w = 0, h = 0;
            var hasSize = int.TryParse((string?)icon.Attribute("width"), NumberStyles.None, CultureInfo.InvariantCulture, out w)
                && int.TryParse((string?)icon.Attribute("height"), NumberStyles.None, CultureInfo.InvariantCulture, out h)
                && w > 0 && h > 0;
            if (hasSize && w >= h * 1.2)
            {
                backdrop ??= src;
            }
            else
            {
                poster ??= src;
            }
        }

        return (poster, backdrop);
    }

    private static string ExtractTitle(XElement element)
    {
        string? fallbackEn = null;
        foreach (var t in element.Elements("title"))
        {
            var value = t.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            var lang = (string?)t.Attribute("lang");
            if (!string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            fallbackEn ??= value;
        }

        return fallbackEn ?? string.Empty;
    }
}
