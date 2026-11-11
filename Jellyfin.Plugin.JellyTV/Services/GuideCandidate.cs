using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Lo que una fuente ofrece de un canal.</summary>
/// <param name="SourceId">Identificador de la fuente.</param>
/// <param name="SourceName">Nombre de la fuente.</param>
/// <param name="Method">Cómo se emparejó el canal: <c>tvg-id</c>, <c>nombre</c> o <c>propia</c>.</param>
/// <param name="IsOwn">Si es la guía propia que el usuario declaró para el canal.</param>
/// <param name="Items">Programas de la fuente para ese canal.</param>
internal sealed record GuideCandidate(string SourceId, string SourceName, string Method, bool IsOwn, IReadOnlyList<GuideItem> Items);
