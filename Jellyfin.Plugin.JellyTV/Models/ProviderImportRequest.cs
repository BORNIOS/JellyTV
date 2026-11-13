using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// Pedido para importar canales del catálogo de un proveedor FAST.
/// </summary>
public class ProviderImportRequest
{
    /// <summary>Gets or sets the ids (in the provider) of the channels to import.</summary>
    public List<string> ChannelIds { get; set; } = new();

    /// <summary>Gets or sets the country the new sources exit through (for example US), or empty to use the server's own IP.</summary>
    public string? ExitCountry { get; set; }

    /// <summary>
    /// Gets or sets, by provider channel id, the id of an existing channel the provider's channel is added to as a backup source
    /// (instead of creating a new channel).
    /// </summary>
    public Dictionary<string, string> BackupOf { get; set; } = new();
}
