using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Descarga cada fuente XMLTV configurada, la filtra a los tvg-ids de los canales del plugin y
/// fusiona el resultado en un único archivo de guía local que el propio plugin sirve a Jellyfin.
/// No depende de ningún servicio externo (Worker).
/// </summary>
public class EpgFilterService
{
    private const int XmltvTimeoutMinutes = 15;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _epgFilePath;
    private readonly ILogger<EpgFilterService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgFilterService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Fábrica de clientes HTTP.</param>
    /// <param name="epgFilePath">Ruta absoluta del archivo XMLTV fusionado que genera el plugin.</param>
    /// <param name="logger">Logger.</param>
    public EpgFilterService(IHttpClientFactory httpClientFactory, string epgFilePath, ILogger<EpgFilterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _epgFilePath = epgFilePath;
        _logger = logger;
    }

    /// <summary>Gets ruta absoluta del archivo XMLTV fusionado que sirve la guía local.</summary>
    public string EpgFilePath => _epgFilePath;

    /// <summary>
    /// Sincroniza todas las fuentes EPG habilitadas: descarga → filtra por tvg-id → enriquece con TMDB
    /// (si hay clave) → fusiona y escribe el archivo de guía local.
    /// </summary>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Resultado del sync.</returns>
    public async Task<EpgSyncResult> SyncLocalGuideAsync(CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var result = new EpgSyncResult();

        if (config is null)
        {
            result.Error = "Plugin configuration unavailable";
            return result;
        }

        var epgSources = config.EpgSources?.FindAll(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url));
        if (epgSources is null || epgSources.Count == 0)
        {
            result.Error = "No EPG sources configured";
            return result;
        }

        PluginLog.Info("[EPG] Collecting tvg-ids from channels…");
        var tvgIds = await CollectTvgIdsAsync(ct).ConfigureAwait(false);
        result.TvgIdsFound = tvgIds.Count;

        if (tvgIds.Count == 0)
        {
            result.Error = "No tvg-ids found in channels — nothing to match against";
            return result;
        }

        var tmdbService = Plugin.Instance?.TmdbEnrichment;
        var hasTmdb = tmdbService != null && !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        var tmdbCtx = hasTmdb ? tmdbService!.CreateRunContext() : null;

        // Contexto por tvg-id: tipo y nombre del canal, para clasificar la EPG (sports/news/kids/...).
        var channelCtx = new Dictionary<string, (ChannelKind Kind, string Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in config.Channels)
        {
            if (channel.IsEnabled && !string.IsNullOrWhiteSpace(channel.TvgId))
            {
                channelCtx[channel.TvgId.Trim()] = (EpgClassifier.ClassifyChannel(channel.Name, channel.Category), channel.Name);
            }
        }

        var dir = Path.GetDirectoryName(_epgFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Cabecera única del documento fusionado.
        var master = new StringBuilder();
        master.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        master.Append("<!DOCTYPE tv SYSTEM \"xmltv.dtd\">\n");
        master.Append("<tv generator-info-name=\"JellyTV Plugin\">\n");

        var seenChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenProgrammes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var epgSource in epgSources)
        {
            var sourceResult = new EpgSourceSyncResult { SourceId = epgSource.Id, SourceName = epgSource.Name };
            try
            {
                PluginLog.Info($"[EPG] Processing source '{epgSource.Name}' ({epgSource.Url})");
                var content = await DownloadFilteredAsync(epgSource.Url, tvgIds, channelCtx, tmdbCtx, ct).ConfigureAwait(false);
                sourceResult.ChannelsKept = content.Channels.Count;
                sourceResult.ProgrammesKept = content.Programmes.Count;

                // Fusionar con dedup: canal por id; programa por (canal + start).
                foreach (var ch in content.Channels)
                {
                    if (seenChannels.Add(ch.Id))
                    {
                        master.Append(ch.Xml).Append('\n');
                    }
                }

                foreach (var p in content.Programmes)
                {
                    if (seenProgrammes.Add(p.Channel + "_" + p.Start))
                    {
                        master.Append(p.Xml).Append('\n');
                    }
                }

                sourceResult.EnrichmentStats = content.Stats;
                sourceResult.Success = true;

                epgSource.LastSyncedAt = DateTimeOffset.UtcNow;
                epgSource.LastSyncSummary = $"{content.Channels.Count} canales · {content.Programmes.Count} programas" +
                    (content.Stats is { } s && s.Enriched > 0 ? $" · {s.Enriched} enriquecidos" : string.Empty);
                epgSource.LastSyncError = null;
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[EPG] Source '{epgSource.Name}' failed", ex);
                sourceResult.Error = ex.Message;
                epgSource.LastSyncError = ex.Message;
                epgSource.LastSyncedAt = DateTimeOffset.UtcNow;
                epgSource.LastSyncSummary = null;
            }

            result.Sources.Add(sourceResult);
        }

        result.Success = result.Sources.Exists(s => s.Success);
        if (result.Success)
        {
            master.Append("</tv>");
            await WriteFileAsync(master.ToString(), ct).ConfigureAwait(false);
            PluginLog.Info($"[EPG] Guía local escrita: {_epgFilePath} ({master.Length:N0} chars)");
            // Jellyfin cachea el XMLTV ~1 h; invalidar su copia para que el Refresh Guide encadenado
            // re-descargue la guía nueva y no use una caché vacía/vieja (causa de "sin programación").
            Plugin.Instance?.InvalidateJellyfinXmltvCache();
        }

        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch
        {
            // No crítico: si falla la persistencia de metadatos de fuente no abortamos el sync.
        }

        return result;
    }

    /// <summary>
    /// Lee los canales habilitados y extrae sus tvg-id.
    /// </summary>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Conjunto de tvg-ids.</returns>
    internal Task<HashSet<string>> CollectTvgIdsAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var tvgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config is null)
        {
            return Task.FromResult(tvgIds);
        }

        var channels = config.Channels?.FindAll(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.TvgId)) ?? new();

        foreach (var ch in channels)
        {
            if (!string.IsNullOrWhiteSpace(ch.TvgId))
            {
                tvgIds.Add(ch.TvgId.Trim());
            }
        }

        PluginLog.Info($"[EPG] Collected {tvgIds.Count} distinct tvg-id(s) from channels");
        return Task.FromResult(tvgIds);
    }

    /// <summary>
    /// Transmite (streaming, SAX) una fuente XMLTV manteniendo los canales cuyo id está en
    /// <paramref name="tvgIds"/> y sus programas asociados. Devuelve fragmentos en memoria listos
    /// para fusionar (sin cabecera ni cierre). Si <paramref name="tmdbCtx"/> no es null, enriquece
    /// los programas con TMDB (imágenes, categorías y descripción) reescribiendo su XML.
    /// </summary>
    /// <param name="sourceUrl">URL del XMLTV.</param>
    /// <param name="tvgIds">tvg-ids de los canales del plugin.</param>
    /// <param name="channelCtx">Contexto (tipo/nombre) por tvg-id de los canales del plugin.</param>
    /// <param name="tmdbCtx">Contexto TMDB compartido por el run, o null para saltar enriquecimiento.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Contenido filtrado (canales, programas y estadísticas de enriquecimiento).</returns>
    private async Task<FilteredSource> DownloadFilteredAsync(
        string sourceUrl,
        HashSet<string> tvgIds,
        Dictionary<string, (ChannelKind Kind, string Name)> channelCtx,
        TmdbEnrichmentRunContext? tmdbCtx,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        PluginLog.Info($"[EPG] Downloading XMLTV: {sourceUrl}");

        using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dlCts.CancelAfter(TimeSpan.FromMinutes(XmltvTimeoutMinutes));

        using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, dlCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var inputStream = await response.Content.ReadAsStreamAsync(dlCts.Token).ConfigureAwait(false);

        var channels = new List<ChannelFragment>();
        var programmes = new List<XmlProgrammeRecord>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var writerSettings = new XmlWriterSettings { Async = true, OmitXmlDeclaration = true, Indent = false, Encoding = Encoding.UTF8 };
        var xmlSettings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Ignore };

        using var reader = XmlReader.Create(inputStream, xmlSettings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Name == "channel")
            {
                var id = reader.GetAttribute("id") ?? string.Empty;
                if (string.IsNullOrEmpty(id) || !tvgIds.Contains(id))
                {
                    await reader.SkipAsync().ConfigureAwait(false);
                    continue;
                }

                matched.Add(id);
                channels.Add(new ChannelFragment(id, await SerializeElementAsync(reader, writerSettings).ConfigureAwait(false)));
            }
            else if (reader.Name == "programme")
            {
                var channel = reader.GetAttribute("channel") ?? string.Empty;
                if (string.IsNullOrEmpty(channel) || !matched.Contains(channel))
                {
                    await reader.SkipAsync().ConfigureAwait(false);
                    continue;
                }

                var start = reader.GetAttribute("start") ?? string.Empty;
                var xml = await SerializeElementAsync(reader, writerSettings).ConfigureAwait(false);
                var data = ParseProgrammeData(xml);
                channelCtx.TryGetValue(channel, out var chCtx);
                var chName = string.IsNullOrEmpty(chCtx.Name) ? channel : chCtx.Name;
                programmes.Add(new XmlProgrammeRecord(xml, channel, start, data, chCtx.Kind, chName));
            }
        }

        EpgEnrichmentStats? stats = null;
        if (tmdbCtx != null && programmes.Count > 0)
        {
            var tmdb = Plugin.Instance?.TmdbEnrichment;
            if (tmdb != null)
            {
                stats = await tmdb.EnrichXmlProgrammesAsync(programmes, tmdbCtx, ct).ConfigureAwait(false);
                PluginLog.Info(
                    $"[EPG] '{sourceUrl}' enrichment: {stats.ProgrammesInput} programmes → {stats.UniqueAfterDedup} unique → {stats.Enriched} enriched · " +
                    $"cache: {stats.MemoryCacheHits} mem + {stats.NegativeCacheSkips} neg · no-match: {stats.NoMatch} · " +
                    $"skip: {stats.SkippedByHeuristics} · TMDB: {stats.TmdbRequests} req · errors: {stats.Errors}");
            }
        }

        PluginLog.Info($"[EPG] Filter result for {sourceUrl}: {channels.Count} channels kept, {programmes.Count} programmes kept");
        return new FilteredSource(channels, programmes, stats);
    }

    /// <summary>
    /// Serializa el elemento actual del lector (un &lt;channel&gt; o &lt;programme&gt;) a string.
    /// </summary>
    /// <param name="reader">Lector posicionado en un elemento.</param>
    /// <param name="settings">Opciones del escritor.</param>
    /// <returns>XML del elemento.</returns>
    private static async Task<string> SerializeElementAsync(XmlReader reader, XmlWriterSettings settings)
    {
        var sb = new StringBuilder();
        using var w = XmlWriter.Create(sb, settings);
        await w.WriteNodeAsync(reader.ReadSubtree(), true).ConfigureAwait(false);
        await w.FlushAsync().ConfigureAwait(false);
        return sb.ToString();
    }

    /// <summary>
    /// Parsea los campos ricos de un único &lt;programme&gt; serializado.
    /// </summary>
    /// <param name="xml">XML del programa.</param>
    /// <returns>Metadatos extraídos (o vacíos si algo falla).</returns>
    private static EpgProgrammeData ParseProgrammeData(string xml)
    {
        string? titleEs = null, titleEn = null, desc = null, category = null, iconUrl = null;
        int? year = null;

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, Async = false });

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.Name)
                {
                    case "title":
                        var lang = reader.GetAttribute("lang") ?? string.Empty;
                        var titleText = reader.IsEmptyElement ? string.Empty : reader.ReadElementContentAsString();
                        if (lang.Equals("en", StringComparison.OrdinalIgnoreCase))
                        {
                            titleEn ??= titleText.Trim();
                        }
                        else
                        {
                            titleEs ??= titleText.Trim();
                        }

                        break;

                    case "desc":
                        if (desc is null && !reader.IsEmptyElement)
                        {
                            desc = reader.ReadElementContentAsString().Trim();
                        }

                        break;

                    case "category":
                        if (category is null && !reader.IsEmptyElement)
                        {
                            var cat = reader.ReadElementContentAsString().Trim();
                            if (!string.IsNullOrWhiteSpace(cat))
                            {
                                category = cat;
                            }
                        }

                        break;

                    case "date":
                        if (year is null && !reader.IsEmptyElement)
                        {
                            var dateStr = reader.ReadElementContentAsString().Trim();
                            if (dateStr.Length >= 4
                                && int.TryParse(dateStr[..4], out var y)
                                && y > 1900 && y < 2030)
                            {
                                year = y;
                            }
                        }

                        break;

                    case "icon":
                        iconUrl ??= reader.GetAttribute("src");
                        break;
                }
            }
        }
        catch
        {
            // Usar lo que se haya parseado — datos incompletos mejor que un crash.
        }

        return new EpgProgrammeData
        {
            TitleEs = string.IsNullOrWhiteSpace(titleEs) ? null : titleEs,
            TitleEn = string.IsNullOrWhiteSpace(titleEn) ? null : titleEn,
            Description = string.IsNullOrWhiteSpace(desc) ? null : desc,
            Category = string.IsNullOrWhiteSpace(category) ? null : category,
            Year = year,
            ExistingIconUrl = iconUrl,
        };
    }

    /// <summary>
    /// Escribe el contenido a un archivo temporal y lo mueve a la ruta final (escritura atómica).
    /// </summary>
    /// <param name="content">Contenido XML completo.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Una tarea.</returns>
    private async Task WriteFileAsync(string content, CancellationToken ct)
    {
        var tmp = _epgFilePath + ".tmp";
        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(tmp, _epgFilePath, true);
    }

    private sealed class ChannelFragment
    {
        internal ChannelFragment(string id, string xml)
        {
            Id = id;
            Xml = xml;
        }

        internal string Id { get; }

        internal string Xml { get; }
    }

    private sealed class FilteredSource
    {
        internal FilteredSource(List<ChannelFragment> channels, List<XmlProgrammeRecord> programmes, EpgEnrichmentStats? stats)
        {
            Channels = channels;
            Programmes = programmes;
            Stats = stats;
        }

        internal List<ChannelFragment> Channels { get; }

        internal List<XmlProgrammeRecord> Programmes { get; }

        internal EpgEnrichmentStats? Stats { get; }
    }
}
