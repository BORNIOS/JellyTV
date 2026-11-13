using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Un proveedor de canales FAST al que el plugin le pregunta directamente (sin intermediarios): qué canales ofrece,
/// con qué dirección se reproduce cada uno hoy y qué programación tiene.
/// </summary>
internal interface IChannelProvider
{
    /// <summary>Gets el identificador corto del proveedor (aparece en las direcciones <c>jtv-provider://</c>).</summary>
    string Id { get; }

    /// <summary>Gets el nombre que se muestra al administrador.</summary>
    string Name { get; }

    /// <summary>Lista los canales del catálogo, tal como los ve el cliente HTTP recibido (su IP decide el catálogo).</summary>
    /// <param name="client">Cliente por el que se consulta (directo o con salida por país).</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>Canales del catálogo.</returns>
    Task<IReadOnlyList<ProviderChannel>> ListChannelsAsync(HttpClient client, CancellationToken cancellationToken);

    /// <summary>Obtiene la dirección con la que se reproduce hoy un canal.</summary>
    /// <param name="channelId">Id del canal en el proveedor.</param>
    /// <param name="client">Cliente por el que se consulta.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>Dirección y token vigentes.</returns>
    Task<ProviderStream> ResolveStreamAsync(string channelId, HttpClient client, CancellationToken cancellationToken);

    /// <summary>Descarga la programación de unos canales.</summary>
    /// <param name="client">Cliente por el que se consulta.</param>
    /// <param name="channels">Canales de los que se quiere la guía.</param>
    /// <param name="hours">Horas hacia adelante.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>Programas de esos canales.</returns>
    Task<IReadOnlyList<ProviderProgramme>> GetProgrammesAsync(HttpClient client, IReadOnlyList<ProviderChannel> channels, int hours, CancellationToken cancellationToken);
}
