using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyTV.Services.Identity;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Lista de canales de cada guía local configurada. Se guarda al sincronizar (la guía publicada solo conserva los canales
/// emparejados) y alimenta la búsqueda de identidad: Pluto, Samsung, Plex o Tubi no usan tvg-id sino su propio id de canal.
/// </summary>
internal static class GuideChannelCatalog
{
    private const double MinScore = 0.6;
    private const int MaxResults = 5;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Guarda la lista de canales de una guía.</summary>
    /// <param name="path">Archivo de destino.</param>
    /// <param name="entries">Canales de la guía.</param>
    internal static void Save(string path, IReadOnlyList<GuideChannelEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    /// <summary>Lee la lista guardada de una guía; vacía si no existe o está dañada.</summary>
    /// <param name="path">Archivo guardado por <see cref="Save"/>.</param>
    /// <returns>Canales de la guía.</returns>
    internal static IReadOnlyList<GuideChannelEntry> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<GuideChannelEntry>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            PluginLog.Warn($"[EPG] No se pudo leer la lista de canales {path}: {ex.Message}");
            return [];
        }
    }

    /// <summary>Busca entre los canales de las guías locales los que se parecen a un nombre.</summary>
    /// <param name="entries">Canales de todas las guías locales.</param>
    /// <param name="query">Nombre buscado.</param>
    /// <param name="country">País que acota la búsqueda; un canal sin país conocido se ofrece igual.</param>
    /// <param name="tvgId">Id de guía que ya tiene el canal; una coincidencia exacta gana a cualquier parecido de nombre.</param>
    /// <returns>Candidatos ordenados de mejor a peor.</returns>
    internal static List<IdentityCandidate> Search(IEnumerable<GuideChannelEntry> entries, string query, string? country, string? tvgId)
    {
        var code = string.IsNullOrWhiteSpace(country) ? null : country.Trim();
        var guideId = (tvgId ?? string.Empty).Trim();
        var result = new List<IdentityCandidate>();
        foreach (var entry in entries)
        {
            var entryCountry = EpgChannelMatcher.CountryFromId(entry.Id);
            if (code is not null && entryCountry is not null && !string.Equals(entryCountry, code, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var exact = guideId.Length > 0 && string.Equals(entry.Id, guideId, StringComparison.Ordinal);
            var score = exact ? 1.0 : entry.Names.Count == 0 ? 0 : Math.Min(0.99, entry.Names.Max(n => NameMatcher.Similarity(query, n)));
            if (score < MinScore)
            {
                continue;
            }

            result.Add(new IdentityCandidate
            {
                Name = entry.Names.Count > 0 ? entry.Names[0] : entry.Id,
                TvgId = entry.Id,
                Country = entryCountry,
                LogoUrl = entry.Logo,
                Score = score,
                Sources = "guía " + entry.SourceName
            });
        }

        return result.OrderByDescending(static c => c.Score).Take(MaxResults).ToList();
    }
}
