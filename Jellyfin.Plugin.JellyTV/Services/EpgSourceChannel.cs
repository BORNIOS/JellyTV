using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Canal declarado por una fuente XMLTV.</summary>
/// <param name="Id">Atributo id del canal en la fuente.</param>
/// <param name="Names">Nombres a mostrar declarados.</param>
/// <param name="Country">País deducido del sufijo del id (por ejemplo <c>ESPN.mx</c>), si es reconocible.</param>
/// <param name="Logo">Dirección del <c>&lt;icon&gt;</c> que la fuente declara para el canal, si trae uno.</param>
internal sealed record EpgSourceChannel(string Id, IReadOnlyList<string> Names, string? Country = null, string? Logo = null);
