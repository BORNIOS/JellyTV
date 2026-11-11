using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Un programa listo para publicar, con su ventana de emisión.</summary>
/// <param name="Start">Inicio de la emisión.</param>
/// <param name="Stop">Fin de la emisión.</param>
/// <param name="Xml">Fragmento XMLTV del programa.</param>
/// <param name="HasDescription">Si el programa trae sinopsis.</param>
/// <param name="Related">La misma emisión según las demás fuentes (a lo sumo una por fuente), usada para completar datos y corroborar.</param>
internal sealed record GuideItem(DateTimeOffset Start, DateTimeOffset Stop, string Xml, bool HasDescription, IReadOnlyList<GuideItem>? Related = null);
