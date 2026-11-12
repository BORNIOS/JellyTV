using System;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Foto del avance de una tarea larga (revisión de salud o sincronización de la guía), tal como la consulta el panel.
/// </summary>
/// <param name="Running">Si la tarea está en curso.</param>
/// <param name="Phase">Fase actual.</param>
/// <param name="Detail">Elemento que se procesa ahora (canal, fuente).</param>
/// <param name="Done">Unidades terminadas.</param>
/// <param name="Total">Unidades totales; 0 si aún no se conocen.</param>
/// <param name="StartedAt">Inicio de la última ejecución.</param>
/// <param name="FinishedAt">Fin de la última ejecución, si ya terminó.</param>
/// <param name="Summary">Resultado de la última ejecución terminada.</param>
public sealed record TaskProgress(
    bool Running,
    string Phase,
    string Detail,
    int Done,
    int Total,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Summary)
{
    /// <summary>Gets el estado de una tarea que nunca se ha ejecutado.</summary>
    public static TaskProgress Idle { get; } = new(false, string.Empty, string.Empty, 0, 0, null, null, null);
}
