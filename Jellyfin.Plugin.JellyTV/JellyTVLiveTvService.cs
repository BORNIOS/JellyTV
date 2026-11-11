using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyTV;

/// <summary>
/// Exposes JellyTV channels as a Live TV provider without requiring an external M3U tuner.
/// </summary>
public sealed class JellyTVLiveTvService : ILiveTvService, ISupportsDirectStreamProvider
{
    private readonly int _httpPort;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTVLiveTvService"/> class.
    /// </summary>
    /// <param name="applicationHost">Host de la aplicación Jellyfin (para conocer el puerto HTTP real).</param>
    /// <param name="configurationManager">Gestor de configuración del servidor (tuners registrados).</param>
    /// <param name="httpContextAccessor">Acceso a la petición en curso, de donde sale el host con el que el cliente abrió Jellyfin.</param>
    /// <param name="providerManager">Gestor de proveedores de metadatos de Jellyfin (para enriquecer la guía con lo que el servidor ya sabe buscar).</param>
    public JellyTVLiveTvService(
        IServerApplicationHost applicationHost,
        IServerConfigurationManager configurationManager,
        IHttpContextAccessor httpContextAccessor,
        MediaBrowser.Controller.Providers.IProviderManager providerManager)
    {
        Plugin.NativeProviders = providerManager;
        _configurationManager = configurationManager;
        _httpContextAccessor = httpContextAccessor;

        // El Path del MediaSource debe ser alcanzable por el CLIENTE, no por el propio servidor. Cada
        // petición lo calcula con el host con el que ese cliente abrió Jellyfin (localhost, LAN o el
        // dominio público), así que la misma instancia sirve en todos los escenarios sin configuración.
        _httpPort = applicationHost.HttpPort;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration!;

    /// <summary>
    /// Gets a value indicating whether this service must publish channels. En modo "Auto" se publica
    /// salvo que ya exista un tuner M3U apuntando al plugin, para no duplicar canales.
    /// </summary>
    private bool IsNativeMode
    {
        get
        {
            var mode = Plugin.Instance?.Configuration?.LiveTvMode;
            if (string.Equals(mode, "Native", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(mode, "M3u", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Auto: siempre nativo. El tuner M3U estándar de Jellyfin marca como no reproducible
            // en directo cualquier manifiesto .m3u8, así que mantener un tuner propio condena al
            // transcode; se migra (se elimina) y los canales salen de este servicio.
            return true;
        }
    }

    /// <inheritdoc/>
    public string Name => "JellyTV";

    /// <inheritdoc/>
    public string HomePageUrl => "https://github.com/BORNIOS/JellyTV";

    /// <inheritdoc/>
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        if (!IsNativeMode)
        {
            // Modo "M3u": los canales llegan por /JellyTV/tuner.m3u. Publicar aquí además
            // duplicaría cada canal en Live TV.
            return Task.FromResult<IEnumerable<ChannelInfo>>(Array.Empty<ChannelInfo>());
        }

        RemoveOwnTuner();
        Plugin.NormalizeChannels();

        var list = Config.Channels
            .Where(c => c.IsEnabled && c.Sources.Any(s => !string.IsNullOrWhiteSpace(s.Url)))
            .Select(c => new ChannelInfo
            {
                Id = c.Id,
                Name = c.Name,
                Number = string.Empty,
                ImageUrl = c.LogoUrl,
                ChannelType = ChannelType.TV
            })
            .ToList();

        PluginLog.Info($"[LiveTV] GetChannelsAsync returned {list.Count} channel(s)");
        return Task.FromResult<IEnumerable<ChannelInfo>>(list);
    }

    /// <inheritdoc/>
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        return BuildMediaSourceAsync(GetChannel(channelId), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var source = await BuildMediaSourceAsync(GetChannel(channelId), cancellationToken).ConfigureAwait(false);
        return new List<MediaSourceInfo> { source };
    }

    /// <inheritdoc/>
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId,
        string streamId,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        var channel = GetChannel(channelId);
        PluginLog.Info($"[LiveTV] Direct stream provider selected for channel {channel.Id}");
        var source = await BuildMediaSourceAsync(channel, cancellationToken).ConfigureAwait(false);
        return new DirectUrlLiveStream(source, streamId);
    }

    /// <summary>
    /// Elimina de la configuración de Live TV los tuners M3U que apuntan al propio plugin.
    /// </summary>
    private void RemoveOwnTuner()
    {
        try
        {
            var options = _configurationManager.GetConfiguration<LiveTvOptions>("livetv");
            var own = options.TunerHosts
                ?.Where(t => t.Url?.Contains("/JellyTV/", StringComparison.OrdinalIgnoreCase) == true
                    && (t.Url.Contains("/JellyTV/m3u", StringComparison.OrdinalIgnoreCase)
                        || t.Url.Contains("/JellyTV/tuner.m3u", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (own is null || own.Count == 0)
            {
                return;
            }

            options.TunerHosts = options.TunerHosts!.Except(own).ToArray();
            _configurationManager.SaveConfiguration("livetv", options);
            PluginLog.Info($"[LiveTV] Migrado a servicio nativo: {own.Count} tuner(s) M3U de JellyTV eliminados de Live TV.");
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[LiveTV] No se pudo migrar el tuner M3U propio: {ex.Message}");
        }
    }

    /// <summary>
    /// Base URL con la que el cliente que pide el stream alcanza a esta instancia. Prioridad: el host de la
    /// petición en curso (localhost, LAN o dominio público: funciona en todos sin configurar nada) →
    /// URL de respaldo configurada (para tareas sin petición, como el refresco de la guía) → último host
    /// visto → localhost con aviso. Nunca se hardcodea un dominio de una instancia concreta.
    /// </summary>
    private string ResolveStreamBase()
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request is not null && request.Host.HasValue)
        {
            return $"{ResolveScheme(request)}://{request.Host}{request.PathBase}".TrimEnd('/');
        }

        var configured = Plugin.Instance?.Configuration?.PublicStreamingBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        var known = Plugin.Instance?.KnownBaseUrl;
        if (!string.IsNullOrWhiteSpace(known))
        {
            return known.TrimEnd('/');
        }

        PluginLog.Warn("[LiveTV] Sin petición, URL de respaldo ni host conocido; usando localhost.");
        return $"http://localhost:{_httpPort}";
    }

    /// <summary>
    /// Esquema con el que el cliente abrió Jellyfin. Detrás de un proxy que termina TLS, Request.Scheme puede
    /// ser "http" aunque el navegador esté en https (y bloquearía el relay por contenido mixto), así que
    /// se prefiere X-Forwarded-Proto y, si no, el Origin del propio navegador cuando coincide con el host.
    /// </summary>
    internal static string ResolveScheme(HttpRequest request)
    {
        var forwarded = request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
        if (forwarded is "http" or "https")
        {
            return forwarded;
        }

        if (Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            && string.Equals(origin.Authority, request.Host.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return origin.Scheme;
        }

        return request.Scheme;
    }

    private static ChannelEntry GetChannel(string channelId) =>
        Config.Channels.FirstOrDefault(c => c.Id == channelId)
            ?? throw new KeyNotFoundException($"Channel '{channelId}' not found.");

    /// <summary>
    /// Returns the relay key query fragment required by the plugin's anonymous endpoints.
    /// </summary>
    /// <returns>The <c>&amp;key=…</c> fragment, or an empty string when no key exists.</returns>
    private static string RelayKeyQuery()
    {
        var relayKey = Plugin.Instance?.Configuration?.RelayKey;
        return string.IsNullOrWhiteSpace(relayKey) ? string.Empty : $"&key={Uri.EscapeDataString(relayKey)}";
    }

    /// <summary>
    /// Elige la fuente que se intentará abrir: la primera fuente habilitada marcada como viva
    /// en el último health check; si ninguna está marcada viva, la primera fuente habilitada.
    /// </summary>
    private static ChannelSource SelectActiveSource(ChannelEntry channel)
    {
        channel.EnsureMigratedSources();
        var enabled = channel.Sources.Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0)
        {
            return new ChannelSource { Url = channel.Url, Type = channel.Type };
        }

        return enabled.FirstOrDefault(s => s.IsAlive == true) ?? enabled[0];
    }

    private async Task<MediaSourceInfo> BuildMediaSourceAsync(ChannelEntry channel, CancellationToken cancellationToken)
    {
        channel.EnsureMigratedSources();
        var active = SelectActiveSource(channel);
        var encoded = EncodeUrl(active.Url);

        // Directo: se entrega la URL del proveedor (Direct Play real, sin relay). FAST, tokens (efímeros o de sesión) y
        // salidas por país pasan por el relay. Un token de sesión también: el proveedor lo ata a la IP que lo pidió
        // (el servidor), así que entregárselo al cliente daba 403 y Jellyfin acababa transcodificando.
        var useRelay = DeliveryPolicy.RequiresRelay(active) || !DeliveryPolicy.IsDirectMediaUrl(active.Url);
        var path = useRelay
            ? $"{ResolveStreamBase()}/JellyTV/stream.m3u8?url={encoded}&ch={channel.Id}{RelayKeyQuery()}"
            : active.Url;
        var container = !useRelay
            ? (active.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "ts")
            : "hls";

        return new MediaSourceInfo
        {
            Id = channel.Id,
            // Entrega SIEMPRE vía el relay del plugin (URL same-origin de Jellyfin). El relay
            // reescribe master/variantes/segmentos a /JellyTV/segment.* (same-origin), sin ffmpeg:
            // cada usuario es un relay HTTP del plugin (playlist+segmentos), no un proceso ffmpeg,
            // por lo que escala a cientos de usuarios sin desgaste de hardware.
            // Estrategia (Jellyfin OFICIAL, sin parches de fork): el objetivo es que TODOS los
            // clientes (web, Android TV, Wholphin, móvil) resuelvan en DirectPlay del HLS/TS
            // relayeado. Para eso el MediaSource DEBE declarar MediaStreams con codecs reales
            // (h264+aac) — ver BuildStreams(): con MediaStreams vacío el StreamBuilder stock no
            // encuentra perfil y fuerza Transcode (ffmpeg). Con codecs reales elige DirectPlay y
            // el server sirve el relay HTTP estático sin invocar ffmpeg.
            // ch= permite a /JellyTV/stream resolver TODAS las fuentes del canal y, si la fuente
            // elegida no abre, hacer failover con la siguiente en orden.
            // El Path del relay termina en .m3u8 a propósito: Jellyfin para Android TV crea el MediaItem de
            // ExoPlayer solo con la URI (sin tipo MIME) y ExoPlayer reconoce HLS únicamente por la extensión
            // de la ruta; sin ella intenta leer la playlist como un archivo progresivo y falla.
            Path = path,
            Protocol = MediaProtocol.Http,
            // El relay sirve HLS (playlist+segmentos). Container es honesto por fuente: ts para
            // streams .ts directos, hls para el resto. (NOTA: cambiar a ts continuo no detiene el
            // transcode en clientes nativos y rompe el DirectPlay hls de la web — ver memoria.)
            Container = container,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            // La fuente es remota y alcanzable por el cliente (Path = URL pública de la instancia):
            // marcarlo evita que Jellyfin la trate como local y la entregue mal a clientes remotos.
            IsRemote = true,
            // Fallback solo para clientes sin perfil DirectPlay del contenedor/códec. NO se fuerza:
            // con los codecs declarados abajo, los clientes capaces eligen DirectPlay y ffmpeg
            // jamás arranca para ellos. Apagarlo dejaría sin imagen a cualquier cliente sin perfil
            // directo (mismo problema que el parche del fork que rompía a Wholphin/Android TV).
            SupportsTranscoding = true,
            // Con MediaStreams poblado (Index 0/1) el server NO re-probea (MediaSourceManager
            // solo probea si no hay streams con Index != -1). El probe es innecesario.
            SupportsProbing = false,
            IsInfiniteStream = true,
            ReadAtNativeFramerate = true,
            IgnoreDts = true,
            // Nuestra fuente es un HTTP directo que el servidor abre con un simple GET a
            // /JellyTV/stream (relay del plugin). NO necesita "abrirse" vía MediaSourceManager:
            // con RequiresOpening=true y sin OpenToken, una petición de transcode crashea en
            // OpenLiveStream → GetProvider(null). false evita ese ciclo y deja el relay directo.
            RequiresOpening = false,
            RequiresClosing = false,
            // Jellyfin usa RequiredHttpHeaders al leer la fuente remota (igual que el tuner M3U
            // nativo). Aquí se aplican los overrides por canal (User-Agent + cabeceras custom).
            // Sin cabeceras no hay proxy: con RequiredHttpHeaders el cliente web no reproduce la Path directa y pide
            // /Videos/{id}/stream.hls al servidor (que descarga y reenvía todo). El relay ya pone las suyas al
            // hablar con el proveedor, así que solo las fuentes directas con cabeceras propias las necesitan.
            RequiredHttpHeaders = useRelay ? new Dictionary<string, string>() : BuildRequiredHttpHeaders(channel),
            MediaStreams = BuildStreams(active.Url)
        };
    }

    /// <summary>
    /// Declara los streams reales del canal para que el StreamBuilder stock de Jellyfin pueda
    /// elegir DirectPlay. Verificado en producción: estos canales IPTV son H.264 (Main/High) +
    /// AAC-LC estéreo 48 kHz. Con Index 0/1 distintos, MediaSourceManager no re-probea ni
    /// Normalize los resetea. Si un canal concreto emitiera HEVC/AC3, declarar aquí h264/aac
    /// rompería su DirectPlay — en ese caso añadir un override de códec por fuente.
    /// </summary>
    private static List<MediaStream> BuildStreams(string sourceUrl)
    {
        // Códecs reales si ya se vieron (CODECS del master); si no, h264/aac como siempre.
        var known = SourceCodecs.Get(sourceUrl);
        return
        [
            new()
            {
                Type = MediaStreamType.Video,
                // -1 = "no sé en qué posición del contenedor está", como los tuners estándar de Jellyfin. Con 0/1
                // Android TV intenta fijar la pista de audio 1 al terminar de preparar, no encuentra ese índice en
                // ExoPlayer y reinicia la reproducción una y otra vez (micro-reproducciones).
                Index = -1,
                Codec = known?.Video ?? "h264",
                Profile = known is null ? "High" : known.VideoProfile,
                IsAVC = (known?.Video ?? "h264") == "h264",
                IsInterlaced = false
            },
            new()
            {
                Type = MediaStreamType.Audio,
                Index = -1,
                Codec = known?.Audio ?? "aac",
                Channels = 2,
                SampleRate = 48000
            }
        ];
    }

    /// <summary>
    /// Cabeceras que el servidor envía al leer la URL remota del canal.
    /// User-Agent: override del canal o el global de Configuration (Plugin.EffectiveUserAgent).
    /// CustomHeaders: formato "Name: Value" por línea (Referer, Cookie, Origin, etc.).
    /// Todos los valores se sanean (sin CR/LF) para no romper la cabecera HTTP.
    /// </summary>
    private static Dictionary<string, string> BuildRequiredHttpHeaders(ChannelEntry channel)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Solo el User-Agent propio del canal: el global no es un requisito del proveedor, y ponerlo
        // convertiría cada canal en una descarga a través del servidor.
        if (!string.IsNullOrWhiteSpace(channel.UserAgent))
        {
            headers["User-Agent"] = Plugin.SanitizeHeaderValue(channel.UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(channel.CustomHeaders))
        {
            foreach (var raw in channel.CustomHeaders.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var sep = line.IndexOf(':', StringComparison.Ordinal);
                if (sep <= 0)
                {
                    PluginLog.Warn($"[LiveTV] Cabecera inválida ignorada (se espera 'Name: Value'): {line}");
                    continue;
                }

                var name = line[..sep].Trim();
                var value = line[(sep + 1)..].Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                headers[name] = Plugin.SanitizeHeaderValue(value);
            }
        }

        return headers;
    }

    private static string EncodeUrl(string url) => Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <inheritdoc/>
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var programs = new List<ProgramInfo>();
        if (!IsNativeMode)
        {
            return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
        }

        try
        {
            var channel = Config.Channels.FirstOrDefault(c => c.Id == channelId && c.IsEnabled);
            if (channel is not null)
            {
                // La guía se publica bajo el tvg-id o, si el canal no lo tiene, bajo una clave derivada de su id.
                foreach (var p in LocalEpgReader.GetPrograms(EpgChannelMatcher.KeyFor(channel), startDateUtc, endDateUtc))
                {
                    programs.Add(MapProgram(p, channelId, channel.LogoUrl));
                }
            }

            if (programs.Count > 0)
            {
                PluginLog.Info($"[LiveTV] GetProgramsAsync channel={channelId} → {programs.Count} programme(s)");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[LiveTV] GetProgramsAsync falló para canal {channelId}: {ex.Message}");
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    /// <summary>
    /// Convierte un programa de la guía local en un <see cref="ProgramInfo"/> de Jellyfin,
    /// clasificándolo por sus categorías (movie/series/deportes/noticias/infantil).
    /// </summary>
    /// <param name="program">Programa local.</param>
    /// <param name="channelId">Id externo del canal (el del plugin).</param>
    /// <param name="channelLogo">Logo del canal, que se muestra cuando el programa no tiene ninguna imagen.</param>
    /// <returns>ProgramInfo listo para la guía de Jellyfin.</returns>
    private static ProgramInfo MapProgram(LocalProgramme program, string channelId, string? channelLogo)
    {
        var hasOwnImage = !string.IsNullOrWhiteSpace(program.PosterUrl) || !string.IsNullOrWhiteSpace(program.BackdropUrl);
        var logo = !hasOwnImage && !string.IsNullOrWhiteSpace(channelLogo) ? channelLogo : null;
        var info = new ProgramInfo
        {
            Id = $"{channelId}_{program.StartUtc:yyyyMMddHHmmss}",
            ChannelId = channelId,
            Name = program.Title,
            Overview = string.IsNullOrWhiteSpace(program.Description) ? null : program.Description,
            Genres = program.Categories.ToList(),
            StartDate = program.StartUtc,
            EndDate = program.EndUtc,
            // Jellyfin usa la imagen principal (vertical) en las fichas y el fondo/thumb (horizontal) en las filas
            // apaisadas: dar el póster como fondo lo dejaba recortado. Si solo hay una de las dos, se da solo esa.
            // Sin imagen propia se usa el logo del canal (nunca como fondo: un logo recortado a pantalla completa se ve mal).
            ImageUrl = program.PosterUrl ?? program.BackdropUrl ?? logo,
            ThumbImageUrl = program.BackdropUrl ?? logo,
            BackdropImageUrl = program.BackdropUrl,
            HasImage = hasOwnImage || logo is not null,
            OfficialRating = string.IsNullOrWhiteSpace(program.Rating) ? null : program.Rating
        };

        var categories = program.Categories;
        var isMovie = categories.Any(c => c.Contains("movie", StringComparison.OrdinalIgnoreCase) || c.Contains("pelicula", StringComparison.OrdinalIgnoreCase) || c.Contains("film", StringComparison.OrdinalIgnoreCase));
        var isSeries = categories.Any(c => c.Contains("series", StringComparison.OrdinalIgnoreCase) || c.Contains("serie", StringComparison.OrdinalIgnoreCase));
        info.IsMovie = isMovie && !isSeries;
        info.IsSeries = isSeries && !info.IsMovie;
        info.IsSports = categories.Any(c => c.Contains("sport", StringComparison.OrdinalIgnoreCase) || c.Contains("deporte", StringComparison.OrdinalIgnoreCase));
        info.IsNews = categories.Any(c => c.Contains("news", StringComparison.OrdinalIgnoreCase) || c.Contains("noticias", StringComparison.OrdinalIgnoreCase));
        info.IsKids = categories.Any(c => c.Contains("kids", StringComparison.OrdinalIgnoreCase) || c.Contains("children", StringComparison.OrdinalIgnoreCase) || c.Contains("infantil", StringComparison.OrdinalIgnoreCase));
        return info;
    }

    /// <inheritdoc/>
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<TimerInfo>());

    /// <inheritdoc/>
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken) => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    /// <inheritdoc/>
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null) => Task.FromResult(new SeriesTimerInfo());

    /// <summary>
    /// Returns the default timer used by Jellyfin Live TV.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An empty <see cref="TimerInfo"/>.</returns>
    public Task<TimerInfo> GetDefaultTimer(CancellationToken cancellationToken) => Task.FromResult(new TimerInfo());

    /// <summary>
    /// Returns the lineups exposed by this provider (none).
    /// </summary>
    /// <param name="info">Listings provider information.</param>
    /// <param name="country">Country code.</param>
    /// <param name="location">Location.</param>
    /// <returns>An empty lineup list.</returns>
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location) => Task.FromResult(new List<NameIdPair>());

    /// <inheritdoc/>
    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;
}
