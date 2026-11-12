using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Canal declarado por una guía local, tal como lo ofrece la búsqueda de identidad.
/// </summary>
/// <param name="SourceName">Nombre de la guía que lo declara (por ejemplo "Pluto").</param>
/// <param name="Id">Id del canal en esa guía; es el valor que hace coincidir al canal con su programación.</param>
/// <param name="Names">Nombres a mostrar declarados por la guía.</param>
/// <param name="Logo">Dirección del logo que declara la guía, si trae uno.</param>
internal sealed record GuideChannelEntry(string SourceName, string Id, IReadOnlyList<string> Names, string? Logo);
