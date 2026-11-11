namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Canal del plugin que debe tener guía.</summary>
/// <param name="Key">Clave de guía (tvg-id o derivada).</param>
/// <param name="Name">Nombre del canal.</param>
/// <param name="TvgId">tvg-id configurado, si lo hay.</param>
/// <param name="Country">País del canal (ISO 3166-1 alfa-2), si está definido.</param>
internal sealed record EpgTarget(string Key, string Name, string? TvgId, string? Country = null);
