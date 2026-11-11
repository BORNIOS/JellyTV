using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Vista previa de lo que el plugin extrae de la dirección de una guía, para comprobar la configuración antes de guardar.</summary>
/// <param name="Format">Formato detectado (XMLTV, JSON o HTML con el nombre de la receta).</param>
/// <param name="Count">Programas reconocidos en total.</param>
/// <param name="Items">Los primeros programas, ya con su hora local de servidor y su título.</param>
/// <param name="Error">Motivo del fallo, o null si se reconoció una programación.</param>
internal sealed record GuidePreview(string Format, int Count, IReadOnlyList<GuidePreviewItem> Items, string? Error);
