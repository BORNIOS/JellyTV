using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services.Identity;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Decide a qué canal del plugin pertenece cada canal de una fuente XMLTV. Primero por tvg-id exacto y, para los
/// canales que la fuente no trae con ese id (o que no tienen tvg-id), por parecido de nombre.
/// </summary>
internal static class EpgChannelMatcher
{
    /// <summary>Similitud mínima de nombre para aceptar una coincidencia sin tvg-id.</summary>
    internal const double NameThreshold = 0.9;

    private const string SyntheticPrefix = "jtv:";

    // Códigos de país que se reconocen como sufijo de un id XMLTV ("ESPN.mx"). Un sufijo que no esté aquí ("hd", "tv")
    // no se toma por país.
    private static readonly HashSet<string> KnownCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "MX", "US", "UY", "AR", "CO", "CL", "PE", "VE", "EC", "BO", "PY", "CR", "PA", "DO", "GT", "HN", "SV", "NI", "CU", "PR", "ES", "PT",
        "BR", "GB", "UK", "FR", "DE", "IT", "RU", "CN", "JP", "KR", "IN", "TR", "QA", "AE", "CA", "AU",
    };

    /// <summary>
    /// Clave con la que la guía local identifica a un canal: su tvg-id o, si no lo tiene, una clave derivada del id del canal.
    /// </summary>
    /// <param name="channel">Canal del plugin.</param>
    /// <returns>Clave de guía.</returns>
    internal static string KeyFor(ChannelEntry channel)
        => !string.IsNullOrWhiteSpace(channel.TvgId) ? channel.TvgId.Trim() : SyntheticPrefix + channel.Id;

    /// <summary>
    /// Deduce el país de un id XMLTV a partir de su sufijo ("ESPN.mx" es México).
    /// </summary>
    /// <param name="xmltvId">Id del canal en la fuente.</param>
    /// <returns>Código de país en mayúsculas, o null.</returns>
    internal static string? CountryFromId(string xmltvId)
    {
        var dot = xmltvId.LastIndexOf('.');
        if (dot <= 0 || xmltvId.Length - dot - 1 != 2)
        {
            return null;
        }

        var suffix = xmltvId[(dot + 1)..];
        return KnownCountries.Contains(suffix) ? suffix.ToUpperInvariant() : null;
    }

    // "Canal TUDN (México)" también es "Canal TUDN" y su paréntesis es otro nombre posible: las guías públicas añaden país o emisora entre paréntesis.
    private static IEnumerable<string> NameVariants(string name)
    {
        yield return name;
        var stripped = System.Text.RegularExpressions.Regex.Replace(name, @"\([^)]*\)", " ", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
        if (stripped.Length > 0 && !string.Equals(stripped, name, StringComparison.Ordinal))
        {
            yield return stripped;
        }

        foreach (System.Text.RegularExpressions.Match inner in System.Text.RegularExpressions.Regex.Matches(name, @"\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            yield return inner.Groups[1].Value;
        }
    }

    private static bool CountriesCompatible(string? left, string? right)
        => string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) || string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Empareja los canales de una fuente con los del plugin.
    /// </summary>
    /// <param name="targets">Canales del plugin que necesitan guía.</param>
    /// <param name="sourceChannels">Canales declarados por la fuente XMLTV (id y nombres).</param>
    /// <returns>Mapa id de la fuente → coincidencia.</returns>
    internal static Dictionary<string, EpgMatch> Match(IReadOnlyList<EpgTarget> targets, IReadOnlyList<EpgSourceChannel> sourceChannels)
    {
        var result = new Dictionary<string, EpgMatch>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        // 1. tvg-id exacto: es la coincidencia fiable y siempre gana.
        var byTvgId = new Dictionary<string, EpgTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets.Where(static t => t.TvgId is not null))
        {
            byTvgId.TryAdd(target.TvgId!, target);
        }

        foreach (var source in sourceChannels)
        {
            if (byTvgId.TryGetValue(source.Id, out var exact) && !result.ContainsKey(source.Id))
            {
                result[source.Id] = new EpgMatch(exact.Key, "tvg-id");
                claimed.Add(exact.Key);
            }
        }

        // 2. Nombre: solo canales aún sin guía y solo entre canales de la fuente que no coincidieron por id.
        var pairs = new List<(double Score, EpgTarget Target, string SourceId)>();
        foreach (var target in targets.Where(t => !claimed.Contains(t.Key)))
        {
            foreach (var source in sourceChannels.Where(s => !result.ContainsKey(s.Id)))
            {
                // Con país conocido en ambos lados, "Univision" de la guía de EE. UU. no es el de la de México.
                if (!CountriesCompatible(target.Country, source.Country))
                {
                    continue;
                }

                var best = source.Names.Count == 0 ? 0 : source.Names.SelectMany(NameVariants).Max(n => NameMatcher.Similarity(target.Name, n));

                // Mismo país declarado en los dos lados: desempata a favor de esa fuente.
                if (best >= NameThreshold && !string.IsNullOrEmpty(target.Country) && string.Equals(target.Country, source.Country, StringComparison.OrdinalIgnoreCase))
                {
                    best += 0.02;
                }

                if (best >= NameThreshold)
                {
                    pairs.Add((best, target, source.Id));
                }
            }
        }

        // Voraz por puntuación; un empate exacto entre dos canales distintos se descarta para no adivinar.
        foreach (var group in pairs.GroupBy(static p => p.Score).OrderByDescending(static g => g.Key))
        {
            foreach (var pair in group)
            {
                var ambiguous = group.Count(p => p.Target.Key == pair.Target.Key) > 1 || group.Count(p => p.SourceId == pair.SourceId) > 1;
                if (ambiguous || claimed.Contains(pair.Target.Key) || result.ContainsKey(pair.SourceId))
                {
                    continue;
                }

                result[pair.SourceId] = new EpgMatch(pair.Target.Key, "nombre");
                claimed.Add(pair.Target.Key);
            }
        }

        return result;
    }
}
