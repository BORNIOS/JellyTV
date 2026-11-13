using System;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Direcciones internas que el plugin guarda para lo que importa de un proveedor. No son direcciones de red: el plugin las
/// traduce a la dirección real justo cuando hace falta (al abrir el canal o al sincronizar la guía), porque esa dirección
/// lleva un token que caduca.
/// <list type="bullet">
/// <item><description><c>jtv-provider://pluto/channel/{id}</c>: un canal.</description></item>
/// <item><description><c>jtv-provider://pluto/guide</c>: la guía de todos los canales importados del proveedor.</description></item>
/// </list>
/// </summary>
internal static class ProviderUrl
{
    /// <summary>Esquema de las direcciones internas.</summary>
    internal const string Scheme = "jtv-provider";

    private const string ChannelKind = "channel";
    private const string GuideKind = "guide";

    /// <summary>Dirección interna de un canal de un proveedor.</summary>
    /// <param name="provider">Id del proveedor.</param>
    /// <param name="channelId">Id del canal.</param>
    /// <returns>La dirección interna.</returns>
    internal static string Channel(string provider, string channelId) => $"{Scheme}://{provider}/{ChannelKind}/{Uri.EscapeDataString(channelId)}";

    /// <summary>Dirección interna de la guía de un proveedor.</summary>
    /// <param name="provider">Id del proveedor.</param>
    /// <returns>La dirección interna.</returns>
    internal static string Guide(string provider) => $"{Scheme}://{provider}/{GuideKind}";

    /// <summary>Indica si una dirección es una dirección interna de proveedor.</summary>
    /// <param name="url">Dirección a revisar.</param>
    /// <returns><c>true</c> si empieza por <c>jtv-provider://</c>.</returns>
    internal static bool IsProviderUrl(string? url) => url is not null && url.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lee la dirección interna de un canal.</summary>
    /// <param name="url">Dirección a leer.</param>
    /// <param name="provider">Id del proveedor.</param>
    /// <param name="channelId">Id del canal.</param>
    /// <returns><c>true</c> si es la dirección de un canal.</returns>
    internal static bool TryParseChannel(string? url, out string provider, out string channelId)
    {
        provider = channelId = string.Empty;
        if (!TryParse(url, out var parsedProvider, out var kind, out var id) || kind != ChannelKind || id.Length == 0)
        {
            return false;
        }

        provider = parsedProvider;
        channelId = id;
        return true;
    }

    /// <summary>Lee la dirección interna de una guía.</summary>
    /// <param name="url">Dirección a leer.</param>
    /// <param name="provider">Id del proveedor.</param>
    /// <returns><c>true</c> si es la dirección de la guía de un proveedor.</returns>
    internal static bool TryParseGuide(string? url, out string provider)
    {
        provider = string.Empty;
        if (!TryParse(url, out var parsedProvider, out var kind, out _) || kind != GuideKind)
        {
            return false;
        }

        provider = parsedProvider;
        return true;
    }

    private static bool TryParse(string? url, out string provider, out string kind, out string id)
    {
        provider = kind = id = string.Empty;
        if (!IsProviderUrl(url))
        {
            return false;
        }

        var parts = url![(Scheme.Length + 3)..].Split('/', 3);
        if (parts.Length < 2 || parts[0].Length == 0)
        {
            return false;
        }

        provider = parts[0].ToLowerInvariant();
        kind = parts[1].ToLowerInvariant();
        id = parts.Length == 3 ? Uri.UnescapeDataString(parts[2]) : string.Empty;
        return true;
    }
}
