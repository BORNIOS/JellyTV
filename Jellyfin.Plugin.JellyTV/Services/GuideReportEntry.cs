namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Qué fuente se eligió como principal para un canal en la última sincronización.</summary>
/// <param name="Primary">Nombre de la fuente principal.</param>
/// <param name="Alternates">Cuántas fuentes más aportaron programas para rellenar huecos.</param>
/// <param name="Candidates">Cuántas fuentes traían el canal.</param>
internal sealed record GuideReportEntry(string Primary, int Alternates, int Candidates);
