using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyTV;

/// <summary>
/// Exposes JellyTV channels as a Live TV provider without requiring an external M3U tuner.
/// </summary>
public sealed class JellyTVLiveTvService : ILiveTvService, ISupportsDirectStreamProvider
{
    private readonly int _httpPort;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTVLiveTvService"/> class.
    /// </summary>
    /// <param name="applicationHost">Host de la aplicación Jellyfin (para conocer el puerto HTTP real).</param>
    public JellyTVLiveTvService(IServerApplicationHost applicationHost)
    {
        // El Path del MediaSource debe ser alcanzable por el CLIENTE, no por el propio servidor:
        // si apunta a localhost, cualquier cliente remoto recibe una URL rota → SocketTimeout →
        // Jellyfin cae a transcode (bug conocido). Por eso la base se resuelve a la URL pública de
        // la instancia (config → KnownBaseUrl → localhost solo como fallback de desarrollo).
        _httpPort = applicationHost.HttpPort;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration!;

    /// <inheritdoc/>
    public string Name => "JellyTV";

    /// <inheritdoc/>
    public string HomePageUrl => "https://github.com/BORNIOS/JellyTV";

    /// <inheritdoc/>
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
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
        var channel = GetChannel(channelId);
        return Task.FromResult(BuildMediaSource(channel));
    }

    /// <inheritdoc/>
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var channel = GetChannel(channelId);
        return Task.FromResult(new List<MediaSourceInfo> { BuildMediaSource(channel) });
    }

    /// <inheritdoc/>
    public Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId,
        string streamId,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        var channel = GetChannel(channelId);
        PluginLog.Info($"[LiveTV] Direct stream provider selected for channel {channel.Id}: {channel.Url}");
        return Task.FromResult<ILiveStream>(new DirectUrlLiveStream(BuildMediaSource(channel), streamId));
    }

    /// <summary>
    /// Base URL pública con la que los clientes alcanzan a esta instancia para los streams.
    /// Prioridad: config explícita (PublicStreamingBaseUrl) → KnownBaseUrl (host de la última
    /// petición al plugin) → localhost (solo dev, con aviso en log). Plugin público: nunca se
    /// hardcodea un dominio de una instancia concreta.
    /// </summary>
    private static string ResolveStreamBase(int fallbackPort)
    {
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

        PluginLog.Warn("[LiveTV] Sin URL publica configurada ni KnownBaseUrl; usando localhost — el Path no sera alcanzable por clientes remotos. Configura 'URL publica de streaming' en el plugin.");
        return $"http://localhost:{fallbackPort}";
    }

    private static ChannelEntry GetChannel(string channelId) =>
        Config.Channels.FirstOrDefault(c => c.Id == channelId)
            ?? throw new KeyNotFoundException($"Channel '{channelId}' not found.");

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

    private MediaSourceInfo BuildMediaSource(ChannelEntry channel)
    {
        channel.EnsureMigratedSources();
        var active = SelectActiveSource(channel);
        var encoded = EncodeUrl(active.Url);
        // Paso 4 (migración a ILiveTvService): una sola fuente TS por ahora (relay &ts=1) para
        // confirmar DirectPlay 0-ffmpeg en nativos antes de añadir la variante HLS (paso 5).
        var tsMode = true;

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
            // IMPORTANTE: el Path DEBE ser .../JellyTV/stream (SIN sufijo .m3u8). Verificado en
            // producción: con la ruta stream.m3u8 Jellyfin (SupportsDirectStream(path,Http))
            // niega DirectStream y el job de salida falla con "muxer .hls: Invalid argument"
            // (ffmpeg exit -22). Con la ruta stream (mismo handler GetStream) se mantiene true.
            Path = $"{ResolveStreamBase(_httpPort)}/JellyTV/stream?url={encoded}&ch={channel.Id}{(tsMode ? "&ts=1" : string.Empty)}",
            Protocol = MediaProtocol.Http,
            // El relay sirve HLS (playlist+segmentos). Container es honesto por fuente: ts para
            // streams .ts directos, hls para el resto. (NOTA: cambiar a ts continuo no detiene el
            // transcode en clientes nativos y rompe el DirectPlay hls de la web — ver memoria.)
            Container = tsMode ? "ts" : "hls",
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
            RequiredHttpHeaders = BuildRequiredHttpHeaders(channel),
            MediaStreams = BuildStreams()
        };
    }

    /// <summary>
    /// Declara los streams reales del canal para que el StreamBuilder stock de Jellyfin pueda
    /// elegir DirectPlay. Verificado en producción: estos canales IPTV son H.264 (Main/High) +
    /// AAC-LC estéreo 48 kHz. Con Index 0/1 distintos, MediaSourceManager no re-probea ni
    /// Normalize los resetea. Si un canal concreto emitiera HEVC/AC3, declarar aquí h264/aac
    /// rompería su DirectPlay — en ese caso añadir un override de códec por fuente.
    /// </summary>
    private static List<MediaStream> BuildStreams()
    {
        return
        [
            new()
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = "h264",
                Profile = "High",
                IsAVC = true,
                IsInterlaced = false
            },
            new()
            {
                Type = MediaStreamType.Audio,
                Index = 1,
                Codec = "aac",
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

        var ua = string.IsNullOrWhiteSpace(channel.UserAgent)
            ? Plugin.EffectiveUserAgent()
            : Plugin.SanitizeHeaderValue(channel.UserAgent);
        headers["User-Agent"] = ua;

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
        try
        {
            var channel = Config.Channels.FirstOrDefault(c => c.Id == channelId && c.IsEnabled);
            if (channel is not null && !string.IsNullOrWhiteSpace(channel.TvgId))
            {
                foreach (var p in LocalEpgReader.GetPrograms(channel.TvgId, startDateUtc, endDateUtc))
                {
                    programs.Add(MapProgram(p, channelId));
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
    /// <returns>ProgramInfo listo para la guía de Jellyfin.</returns>
    private static ProgramInfo MapProgram(LocalProgramme program, string channelId)
    {
        var info = new ProgramInfo
        {
            Id = $"{channelId}_{program.StartUtc:yyyyMMddHHmmss}",
            ChannelId = channelId,
            Name = program.Title,
            Overview = string.IsNullOrWhiteSpace(program.Description) ? null : program.Description,
            Genres = program.Categories.ToList(),
            StartDate = program.StartUtc,
            EndDate = program.EndUtc,
            ImageUrl = program.IconUrl,
            BackdropImageUrl = program.IconUrl,
            HasImage = !string.IsNullOrWhiteSpace(program.IconUrl),
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

    public Task<TimerInfo> GetDefaultTimer(CancellationToken cancellationToken) => Task.FromResult(new TimerInfo());

    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location) => Task.FromResult(new List<NameIdPair>());

    /// <inheritdoc/>
    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;
}
