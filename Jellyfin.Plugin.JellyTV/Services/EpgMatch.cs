namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Resultado de emparejar un canal de la fuente con uno del plugin.</summary>
/// <param name="Key">Clave de guía del canal del plugin.</param>
/// <param name="Method">Cómo se emparejó: <c>tvg-id</c> o <c>nombre</c>.</param>
internal sealed record EpgMatch(string Key, string Method);
