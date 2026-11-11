using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Resultado de combinar las fuentes de un canal.</summary>
/// <param name="Primary">La fuente elegida como principal (null si ninguna aportó programas).</param>
/// <param name="Items">Programas finales, ordenados por inicio y sin solapes.</param>
/// <param name="Contributed">Cuántos programas aportó cada fuente (por identificador).</param>
/// <param name="Scores">Puntuación de cada fuente candidata (por identificador).</param>
internal sealed record GuideMergeResult(
    GuideCandidate? Primary,
    IReadOnlyList<GuideItem> Items,
    IReadOnlyDictionary<string, int> Contributed,
    IReadOnlyDictionary<string, double> Scores);
