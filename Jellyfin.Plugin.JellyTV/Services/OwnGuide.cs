using System.Collections.Generic;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Programación obtenida de la guía propia de un canal.</summary>
/// <param name="ChannelXml">Fragmento XMLTV del canal.</param>
/// <param name="Programmes">Programas listos para fusionar y enriquecer.</param>
/// <param name="Error">Motivo del fallo, o null si salió bien.</param>
internal sealed record OwnGuide(string ChannelXml, List<XmlProgrammeRecord> Programmes, string? Error);
