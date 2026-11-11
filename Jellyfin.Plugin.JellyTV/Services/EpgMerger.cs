using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyTV.Services.Identity;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Combina lo que varias fuentes saben de un mismo canal: la que mejor lo cubre es la principal y las demás solo
/// rellenan los huecos de tiempo que la principal deja, sin solapar ni duplicar programas. Además enlaza cada programa
/// con la misma emisión según las otras fuentes, que sirve para completar datos y para corroborar el título.
/// </summary>
internal static class EpgMerger
{
    private const double SameTitle = 0.8;

    private static readonly TimeSpan OverlapTolerance = TimeSpan.FromSeconds(60);
    private static readonly Regex TitleTag = new("<title\\b[^>]*>([^<]*)</title>", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Puntúa una fuente para un canal: horas realmente cubiertas, cantidad de programas, riqueza (sinopsis) y fiabilidad del
    /// emparejamiento. La guía propia del canal recibe un plus porque el usuario la declaró a propósito.
    /// </summary>
    /// <param name="candidate">Fuente candidata.</param>
    /// <param name="now">Instante de referencia.</param>
    /// <returns>Puntuación entre 0 y 1,15.</returns>
    internal static double Score(GuideCandidate candidate, DateTimeOffset now)
    {
        if (candidate.Items.Count == 0)
        {
            return 0;
        }

        // Horas realmente cubiertas de la próxima semana (no el último programa, que podría estar solo y lejos).
        var horizon = now.AddHours(168);
        var coveredHours = candidate.Items.Sum(i =>
        {
            var from = i.Start > now ? i.Start : now;
            var to = i.Stop < horizon ? i.Stop : horizon;
            return Math.Max(0, (to - from).TotalHours);
        });
        var coverage = Math.Min(coveredHours, 168) / 168.0;
        var volume = Math.Min(candidate.Items.Count, 200) / 200.0;
        var rich = candidate.Items.Count(static i => i.HasDescription) / (double)candidate.Items.Count;
        var method = candidate.Method is "tvg-id" or "propia" ? 1.0 : 0.85;
        return (0.55 * coverage) + (0.15 * volume) + (0.2 * rich) + (0.1 * method) + (candidate.IsOwn ? 0.15 : 0);
    }

    /// <summary>
    /// Elige la fuente principal de un canal y completa con las demás los tramos que ella no cubre.
    /// </summary>
    /// <param name="candidates">Fuentes que traen el canal.</param>
    /// <param name="now">Instante de referencia.</param>
    /// <returns>Programas finales y la atribución por fuente.</returns>
    internal static GuideMergeResult Merge(IReadOnlyList<GuideCandidate> candidates, DateTimeOffset now)
    {
        var scores = candidates.ToDictionary(static c => c.SourceId, c => Score(c, now), StringComparer.Ordinal);
        var ranked = candidates.Where(static c => c.Items.Count > 0).OrderByDescending(c => scores[c.SourceId]).ToList();
        var contributed = new Dictionary<string, int>(StringComparer.Ordinal);
        if (ranked.Count == 0)
        {
            return new GuideMergeResult(null, Array.Empty<GuideItem>(), contributed, scores);
        }

        var merged = new List<(GuideItem Item, string SourceId)>();
        foreach (var candidate in ranked)
        {
            var added = 0;
            foreach (var item in candidate.Items.OrderBy(static i => i.Start))
            {
                if (merged.Any(m => item.Start < m.Item.Stop - OverlapTolerance && item.Stop > m.Item.Start + OverlapTolerance))
                {
                    continue;
                }

                merged.Add((item, candidate.SourceId));
                added++;
            }

            contributed[candidate.SourceId] = added;
        }

        var titles = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        string TitleOf(GuideItem item)
        {
            if (!titles.TryGetValue(item, out var title))
            {
                title = TitleTag.Match(item.Xml) is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : string.Empty;
                titles[item] = title;
            }

            return title;
        }

        var final = new List<GuideItem>(merged.Count);
        foreach (var (item, sourceId) in merged.OrderBy(static m => m.Item.Start))
        {
            var related = new List<GuideItem>();
            if (ranked.Count > 1 && TitleOf(item).Length > 0)
            {
                foreach (var other in ranked.Where(c => !string.Equals(c.SourceId, sourceId, StringComparison.Ordinal)))
                {
                    var match = other.Items
                        .Where(o => Overlap(item, o) >= 0.5 && NameMatcher.Similarity(TitleOf(item), TitleOf(o)) >= SameTitle)
                        .OrderByDescending(o => Overlap(item, o))
                        .FirstOrDefault();
                    if (match is not null)
                    {
                        related.Add(match);
                    }
                }
            }

            final.Add(related.Count == 0 ? item : item with { Related = related });
        }

        return new GuideMergeResult(ranked[0], final, contributed, scores);
    }

    /// <summary>
    /// Qué fracción del más corto de los dos programas queda dentro del otro (0 si no se tocan).
    /// </summary>
    private static double Overlap(GuideItem a, GuideItem b)
    {
        var from = a.Start > b.Start ? a.Start : b.Start;
        var to = a.Stop < b.Stop ? a.Stop : b.Stop;
        var overlap = (to - from).TotalSeconds;
        var shorter = Math.Min((a.Stop - a.Start).TotalSeconds, (b.Stop - b.Start).TotalSeconds);
        return overlap <= 0 || shorter <= 0 ? 0 : overlap / shorter;
    }
}
