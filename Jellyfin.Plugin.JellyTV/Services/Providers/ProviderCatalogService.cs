using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Catálogo de proveedores FAST: lista sus canales, los importa al plugin, traduce a la dirección de reproducción de hoy
/// las direcciones internas que guardan los canales importados, y produce la guía de esos canales.
/// </summary>
internal sealed class ProviderCatalogService
{
    // Horas de guía hacia adelante que se descargan de cada proveedor.
    private const int GuideHours = 48;

    private static readonly TimeSpan ListCacheLifetime = TimeSpan.FromMinutes(15);

    private readonly UpstreamClientService _upstream;
    private readonly IReadOnlyList<IChannelProvider> _providers;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<ProviderChannel> Channels)> _lists = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderCatalogService"/> class.
    /// </summary>
    /// <param name="upstream">Da el cliente HTTP directo o con salida por país.</param>
    public ProviderCatalogService(UpstreamClientService upstream)
        : this(upstream, [new PlutoProvider(), new PlexProvider(), new TubiProvider(), new RuntimeProvider()])
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderCatalogService"/> class with the given providers.
    /// </summary>
    /// <param name="upstream">Da el cliente HTTP directo o con salida por país.</param>
    /// <param name="providers">Proveedores disponibles.</param>
    internal ProviderCatalogService(UpstreamClientService upstream, IReadOnlyList<IChannelProvider> providers)
    {
        _upstream = upstream;
        _providers = providers;
    }

    /// <summary>Gets los proveedores disponibles.</summary>
    internal IReadOnlyList<IChannelProvider> All => _providers;

    /// <summary>Busca un proveedor por su id.</summary>
    /// <param name="id">Id del proveedor.</param>
    /// <returns>El proveedor, o <c>null</c> si no existe.</returns>
    internal IChannelProvider? Get(string id)
        => _providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lista el catálogo de un proveedor (se recuerda 15 minutos por proveedor y país de salida).</summary>
    /// <param name="provider">Proveedor.</param>
    /// <param name="exitCountry">País por el que se consulta, o <c>null</c> para la IP del servidor.</param>
    /// <param name="refresh">Ignora lo recordado.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>Los canales del catálogo.</returns>
    internal async Task<IReadOnlyList<ProviderChannel>> ListAsync(IChannelProvider provider, string? exitCountry, bool refresh, CancellationToken cancellationToken)
    {
        var key = provider.Id + "|" + ProxyPoolService.NormalizeCountry(exitCountry);
        if (!refresh && _lists.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < ListCacheLifetime)
        {
            return cached.Channels;
        }

        using var lease = Lease(exitCountry, "provider:" + provider.Id);
        var channels = await provider.ListChannelsAsync(lease.Client, cancellationToken).ConfigureAwait(false);
        _lists[key] = (DateTimeOffset.UtcNow, channels);
        PluginLog.Info($"[PROVIDER] {provider.Name}: {channels.Count} canales en el catálogo{(UpstreamClientService.RequiresExit(exitCountry) ? $" (salida {exitCountry})" : string.Empty)}.");
        return channels;
    }

    /// <summary>Cuántos canales de un proveedor hay ya importados.</summary>
    /// <param name="config">Configuración del plugin.</param>
    /// <param name="providerId">Id del proveedor.</param>
    /// <returns>El número de canales.</returns>
    internal static int CountImported(PluginConfiguration config, string providerId)
        => config.Channels.Count(c => c.Sources.Any(s => IsChannelOf(s.Url, providerId)));

    /// <summary>
    /// Importa canales del catálogo: crea cada canal con su dirección interna, su logo y su id de guía, y da de alta la guía del
    /// proveedor si aún no estaba. Lo que ya está importado no se toca.
    /// </summary>
    /// <param name="config">Configuración del plugin.</param>
    /// <param name="provider">Proveedor.</param>
    /// <param name="catalog">Catálogo del que se importa.</param>
    /// <param name="channelIds">Ids de los canales elegidos.</param>
    /// <param name="exitCountry">País de salida de las fuentes nuevas, o <c>null</c>.</param>
    /// <param name="backupOf">Por id de canal del proveedor, el id de un canal que ya existe al que se añade como fuente de respaldo (en vez de crear otro canal).</param>
    /// <returns>Cuántos se importaron, cuántos ya estaban, cuántos se añadieron como respaldo y si se añadió la guía.</returns>
    internal static (int Imported, int Existing, int BackedUp, bool GuideAdded) Import(
        PluginConfiguration config,
        IChannelProvider provider,
        IReadOnlyList<ProviderChannel> catalog,
        IReadOnlyCollection<string> channelIds,
        string? exitCountry,
        IReadOnlyDictionary<string, string>? backupOf = null)
    {
        var exit = ProxyPoolService.NormalizeCountry(exitCountry);
        var wanted = new HashSet<string>(channelIds, StringComparer.Ordinal);
        var known = config.Channels.SelectMany(static c => c.Sources).Select(static s => s.Url).ToHashSet(StringComparer.Ordinal);
        int imported = 0, existing = 0, backedUp = 0;
        foreach (var channel in catalog.Where(c => wanted.Contains(c.Id)))
        {
            var url = ProviderUrl.Channel(provider.Id, channel.Id);
            if (known.Contains(url))
            {
                existing++;
                continue;
            }

            var source = new ChannelSource { Label = provider.Name, Url = url, Type = ChannelEntry.FastType, ExitCountry = exit.Length > 0 ? exit : null };

            // El administrador pidió que sea respaldo de un canal que ya tiene: se añade como última fuente y no se crea otro canal.
            string? targetId = null;
            var target = backupOf is not null && backupOf.TryGetValue(channel.Id, out targetId)
                ? config.Channels.FirstOrDefault(c => string.Equals(c.Id, targetId, StringComparison.Ordinal))
                : null;
            if (target is not null)
            {
                target.EnsureMigratedSources();
                target.Sources.Add(source);
                known.Add(url);
                backedUp++;
                continue;
            }

            known.Add(url);
            config.Channels.Add(new ChannelEntry
            {
                Name = channel.Name,
                Type = ChannelEntry.FastType,
                Url = url,
                Sources = [source],
                LogoUrl = channel.Logo,
                TvgId = channel.Id,
                Category = ProviderCategories.Map(channel.Group, channel.Name),
                Country = channel.Country,
                IsEnabled = true
            });
            imported++;
        }

        var guideUrl = ProviderUrl.Guide(provider.Id);
        var guideAdded = imported > 0 && !config.EpgSources.Any(s => string.Equals(s.Url, guideUrl, StringComparison.Ordinal));
        if (guideAdded)
        {
            config.EpgSources.Add(new EpgSource { Name = provider.Name + " (JellyTV)", Url = guideUrl, IsEnabled = true });
        }

        return (imported, existing, backedUp, guideAdded);
    }

    /// <summary>
    /// Traduce la dirección interna de un canal a la dirección con la que se reproduce hoy (con su token vigente).
    /// </summary>
    /// <param name="url">Dirección interna (<c>jtv-provider://…/channel/…</c>).</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>El resultado de la resolución, listo para abrir una sesión del relay.</returns>
    internal async Task<ResolvedStream> ResolveAsync(string url, CancellationToken cancellationToken)
    {
        if (!ProviderUrl.TryParseChannel(url, out var providerId, out var channelId))
        {
            throw new InvalidOperationException("Dirección de proveedor no válida: " + url);
        }

        var provider = Get(providerId) ?? throw new InvalidOperationException("Proveedor desconocido: " + providerId);
        using var lease = Lease(ExitCountryOf(url), url);
        var stream = await provider.ResolveStreamAsync(channelId, lease.Client, cancellationToken).ConfigureAwait(false);
        PluginLog.Info($"[PROVIDER] {provider.Name}: canal {channelId} resuelto.");
        return new ResolvedStream
        {
            OriginalUrl = url,
            FinalUrl = stream.Url,
            BaseUrl = BaseOf(stream.Url),
            ContentType = "application/vnd.apple.mpegurl",
            ResolvedAt = DateTimeOffset.UtcNow,
            TokenExpiry = stream.Expiry,
            IsHls = true,
            TokenValue = stream.Token,
            RedirectChain = [url, stream.Url],
            PlaylistPollUrl = stream.Url
        };
    }

    /// <summary>
    /// Escribe en XMLTV la guía de los canales importados de un proveedor (la sincronización de la guía la lee como cualquier
    /// otra fuente).
    /// </summary>
    /// <param name="config">Configuración del plugin.</param>
    /// <param name="providerId">Id del proveedor.</param>
    /// <param name="path">Archivo de destino.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>Una tarea que termina cuando el archivo está escrito.</returns>
    internal async Task WriteGuideAsync(PluginConfiguration config, string providerId, string path, CancellationToken cancellationToken)
    {
        var provider = Get(providerId) ?? throw new InvalidOperationException("Proveedor desconocido: " + providerId);
        var sources = config.Channels
            .SelectMany(static c => c.Sources)
            .Where(s => IsChannelOf(s.Url, providerId))
            .ToList();
        var ids = sources
            .Select(static s => ProviderUrl.TryParseChannel(s.Url, out _, out var id) ? id : string.Empty)
            .Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            ProviderGuideWriter.Write(path, [], []);
            return;
        }

        // Todos los canales importados de un proveedor salen por el mismo país: es el catálogo con el que se importaron.
        var exit = sources.Select(static s => s.ExitCountry).FirstOrDefault(static c => UpstreamClientService.RequiresExit(c));
        var channels = (await ListAsync(provider, exit, refresh: false, cancellationToken).ConfigureAwait(false)).Where(c => ids.Contains(c.Id)).ToList();
        IReadOnlyList<ProviderProgramme> programmes;
        using (var lease = Lease(exit, "provider:" + provider.Id))
        {
            programmes = await provider.GetProgrammesAsync(lease.Client, channels, GuideHours, cancellationToken).ConfigureAwait(false);
        }

        PluginLog.Info($"[PROVIDER] {provider.Name}: guía de {channels.Count} canal(es), {programmes.Count} programa(s).");
        ProviderGuideWriter.Write(path, channels, programmes);
    }

    // El cliente sin país de salida es el compartido del plugin: liberarlo rompería todas las descargas. Solo se libera el que lleva proxy.
    private ClientLease Lease(string? exitCountry, string stickyKey)
        => new(_upstream.For(exitCountry, stickyKey), UpstreamClientService.RequiresExit(exitCountry));

    private static bool IsChannelOf(string? url, string providerId)
        => ProviderUrl.TryParseChannel(url, out var provider, out _) && string.Equals(provider, providerId, StringComparison.OrdinalIgnoreCase);

    // El país de salida que el administrador eligió para la fuente con esta dirección.
    private static string? ExitCountryOf(string url)
        => Plugin.Instance?.Configuration.Channels
            .SelectMany(static c => c.Sources)
            .FirstOrDefault(s => string.Equals(s.Url, url, StringComparison.Ordinal))?.ExitCountry;

    private static string BaseOf(string url)
    {
        var withoutQuery = url.Split('?')[0];
        return withoutQuery[..(withoutQuery.LastIndexOf('/') + 1)];
    }

    private sealed class ClientLease(HttpClient client, bool owned) : IDisposable
    {
        public HttpClient Client => client;

        public void Dispose()
        {
            if (owned)
            {
                client.Dispose();
            }
        }
    }
}
