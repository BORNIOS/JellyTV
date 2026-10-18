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
        var icon = (string?)element.Element("icon")?.Attribute("src");
        var rating = (string?)element.Element("rating")?.Value;
        var description = element.Elements("desc").Select(d => d.Value.Trim()).FirstOrDefault(d => d.Length > 0);

        return new LocalProgramme(channel, start, stop, title, description, categories, icon, rating);
    }

    /// <summary>
    /// Convierte un timestamp XMLTV a UTC. Si hay zona configurada, se interpreta la hora de
    /// pared (primeros 14 dígitos) en esa zona (ignora el offset declarado, que muchos feeds
    /// escriben como +0000 aunque sean hora local). Sin zona, se respeta el offset declarado y
    /// un valor sin offset se asume UTC.
    /// </summary>
    private static bool TryParseTime(string? text, TimeZoneInfo? zone, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
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
