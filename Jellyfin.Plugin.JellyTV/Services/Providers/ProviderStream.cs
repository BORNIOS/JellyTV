using System;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Dirección con la que se reproduce hoy un canal de un proveedor.
/// </summary>
/// <param name="Url">Lista maestra HLS del canal (puede llevar un token en la dirección).</param>
/// <param name="Token">Token de la sesión que lleva la dirección, si lo hay.</param>
/// <param name="Expiry">Cuándo caduca el token, si se sabe.</param>
internal sealed record ProviderStream(string Url, string? Token, DateTimeOffset? Expiry);
