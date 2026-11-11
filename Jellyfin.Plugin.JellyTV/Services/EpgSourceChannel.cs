using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Canal declarado por una fuente XMLTV.</summary>
/// <param name="Id">Atributo id del canal en la fuente.</param>
/// <param name="Names">Nombres a mostrar declarados.</param>
/// <param name="Country">País deducido del sufijo del id (por ejemplo <c>ESPN.mx</c>), si es reconocible.</param>
internal sealed record EpgSourceChannel(string Id, IReadOnlyList<string> Names, string? Country = null);
