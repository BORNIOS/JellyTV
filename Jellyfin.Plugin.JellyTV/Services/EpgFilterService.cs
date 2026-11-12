using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services.Storage;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Descarga cada fuente XMLTV configurada, la filtra a los tvg-ids de los canales del plugin y
/// fusiona el resultado en un único archivo de guía local que el propio plugin sirve a Jellyfin.
/// No depende de ningún servicio externo.
/// </summary>
public class EpgFilterService
{
    private const int XmltvTimeoutMinutes = 15;
    private const string AutoSourcePrefix = "auto-";

    // Ventana que se publica: lo que terminó hace más de 3 h o empieza dentro de más de 10 días no sirve a la guía.
    private static readonly TimeSpan KeepPast = TimeSpan.FromHours(3);
    private static readonly TimeSpan KeepFuture = TimeSpan.FromDays(10);

    // Antigüedad máxima de la copia de una fuente que falla antes de dejar de usarla.
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _epgFilePath;
    private readonly ILogger<EpgFilterService> _logger;
    private readonly OwnGuideService _ownGuide;
    private readonly AutoGuideCatalog _autoGuide;

    // Avance de la sincronización en curso, para la barra del panel.
    private TaskProgress _progress = TaskProgress.Idle;
    private int _running;
    private int _progressTotal;
    private DateTimeOffset _syncStartedAt;

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
        _ownGuide = new OwnGuideService(httpClientFactory);
        _autoGuide = new AutoGuideCatalog(httpClientFactory);
    }

    /// <summary>Gets ruta absoluta del archivo XMLTV fusionado que sirve la guía local.</summary>
    public string EpgFilePath => _epgFilePath;

    /// <summary>Gets a value indicating whether a guide sync is running.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Gets ruta del informe de la última sincronización (qué fuente fue la principal de cada canal).</summary>
    internal string ReportPath => Path.Combine(Path.GetDirectoryName(_epgFilePath) ?? string.Empty, "guide-report.json");

    /// <summary>
    /// Sincroniza todas las fuentes EPG habilitadas: descarga → empareja canales (tvg-id, nombre y país) → filtra →
    /// enriquece → y, canal por canal, elige la fuente que mejor lo cubre como principal y usa las demás solo para
    /// rellenar los tramos que le faltan. Si una fuente falla se usa su última copia buena, y los canales con guía
    /// propia (la web oficial) compiten con las demás fuentes.
    /// </summary>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Resultado del sync.</returns>
    public async Task<EpgSyncResult> SyncLocalGuideAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            return new EpgSyncResult { Error = "Ya hay una sincronización en curso" };
        }

        var startedAt = DateTimeOffset.UtcNow;
        _syncStartedAt = startedAt;
        SetProgress("Preparando fuentes", string.Empty, 0, 0);
        try
        {
            var result = await RunSyncAsync(ct).ConfigureAwait(false);
            var summary = result.Success ? $"{result.Sources.Count(static s => s.Success)} fuente(s) · {result.TvgIdsFound} canal(es) con tvg-id" : result.Error ?? "Sin cambios";
            Volatile.Write(ref _progress, new TaskProgress(false, string.Empty, string.Empty, _progressTotal, _progressTotal, startedAt, DateTimeOffset.UtcNow, summary));
            return result;
        }
        catch
        {
            Volatile.Write(ref _progress, new TaskProgress(false, string.Empty, string.Empty, 0, 0, startedAt, DateTimeOffset.UtcNow, "Interrumpida"));
            throw;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Gets the progress of the running (or last finished) guide sync.</summary>
    /// <returns>Progress snapshot.</returns>
    public TaskProgress GetProgress() => Volatile.Read(ref _progress);

    private void SetProgress(string phase, string detail, int done, int total)
    {
        _progressTotal = total;
        Volatile.Write(ref _progress, new TaskProgress(true, phase, detail, done, total, _syncStartedAt, null, null));
    }

    private async Task<EpgSyncResult> RunSyncAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var result = new EpgSyncResult();

        if (config is null)
        {
            result.Error = "Plugin configuration unavailable";
            return result;
        }

        var epgSources = config.EpgSources?.FindAll(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)) ?? new List<EpgSource>();
        var ownChannels = config.Channels.Where(static c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.EpgPageUrl)).ToList();
        if (config.AutoGuideEnabled)
        {
            await AddAutomaticSourcesAsync(config, epgSources, ct).ConfigureAwait(false);
        }

        if (epgSources.Count == 0 && ownChannels.Count == 0 && !config.AutoGuideEnabled)
        {
            result.Error = "No EPG sources configured";
            return result;
        }

        var targets = BuildTargets(config);
        result.TvgIdsFound = targets.Count(static t => t.TvgId is not null);
        if (targets.Count == 0)
        {
            result.Error = "No hay canales habilitados — nada contra qué emparejar la guía";
            return result;
        }

        PluginLog.Info($"[EPG] {targets.Count} canal(es) necesitan guía ({result.TvgIdsFound} con tvg-id, {ownChannels.Count} con guía propia)");

        // Pasos reales: una descarga por fuente, una consulta por guía propia y tres etapas finales
        // (fusionar, enriquecer, escribir). Las guías propias automáticas se suman cuando aparecen.
        var totalSteps = epgSources.Count + ownChannels.Count + 3;
        var step = 0;
        void Step(string phase, string detail) => SetProgress(phase, detail, Math.Min(step++, totalSteps), totalSteps);

        var tmdbService = Plugin.Instance?.TmdbEnrichment;
        var hasTmdb = tmdbService != null && !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        var tmdbCtx = hasTmdb ? tmdbService!.CreateRunContext() : null;

        // Contexto por clave de guía: tipo, nombre y país del canal, para clasificar la EPG y afinar los metadatos.
        var channelCtx = new Dictionary<string, (ChannelKind Kind, string Name, string? Country)>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in config.Channels.Where(static c => c.IsEnabled))
        {
            channelCtx[EpgChannelMatcher.KeyFor(channel)] = (EpgClassifier.ClassifyChannel(channel.Name, channel.Category), channel.Name, NormalizeCountry(channel.Country));
        }

        var dir = Path.GetDirectoryName(_epgFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 1. Obtener el contenido de cada fuente (descarga, o la última copia buena si la descarga falla).
        var gathered = new List<(string Id, string Name, bool IsOwn, FilteredSource Content)>();
        foreach (var epgSource in epgSources)
        {
            var sourceResult = new EpgSourceSyncResult { SourceId = epgSource.Id, SourceName = epgSource.Name };
            FilteredSource? content = null;
            Step("Descargando guías", epgSource.Name);
            try
            {
                PluginLog.Info($"[EPG] Processing source '{epgSource.Name}' ({epgSource.Url})");
                content = await DownloadFilteredAsync(epgSource.Url, targets, channelCtx, channels => RememberGuideChannels(config, epgSource, channels), ct).ConfigureAwait(false);
                SaveCache(epgSource.Id, content);
                sourceResult.Success = true;
                epgSource.LastSyncedAt = DateTimeOffset.UtcNow;
                epgSource.LastSyncError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                PluginLog.Error($"[EPG] Source '{epgSource.Name}' failed", ex);
                sourceResult.Error = ex.Message;
                epgSource.LastSyncSummary = null;

                // Una caída de la fuente no debe borrar la programación que ya teníamos de ella.
                content = LoadCache(epgSource.Id, channelCtx, out var cacheAge);
                if (content is not null)
                {
                    sourceResult.UsedCache = true;
                    sourceResult.CacheAgeHours = (int)Math.Round(cacheAge.TotalHours);
                    sourceResult.Success = true;
                    epgSource.LastSyncError = $"{ex.Message} — se usa la copia de hace {FormatAge(cacheAge)}.";
                }
                else
                {
                    epgSource.LastSyncError = ex.Message;
                }
            }

            if (content is not null)
            {
                sourceResult.ChannelsByName = content.ByName;
                sourceResult.EnrichmentStats = content.Stats;
                gathered.Add((epgSource.Id, epgSource.Name, false, content));
                if (!epgSource.Id.StartsWith(AutoSourcePrefix, StringComparison.Ordinal))
                {
                    PersistEpgAlignment(epgSource, epgSources.IndexOf(epgSource), content);
                }
            }

            result.Sources.Add(sourceResult);
        }

        // La guía propia de cada canal (su web oficial) entra como una fuente más.
        var zone = ResolveZone(config.EpgTimezone);
        if (config.AutoGuideEnabled)
        {
            var before = ownChannels.Count;
            await AddAutomaticOwnGuidesAsync(config, gathered.SelectMany(static g => g.Content.Channels).Select(static f => f.Id), ownChannels, ct).ConfigureAwait(false);
            totalSteps += ownChannels.Count - before;
        }

        foreach (var channel in ownChannels)
        {
            Step("Guías de las webs de canal", channel.Name);
            var key = EpgChannelMatcher.KeyFor(channel);
            var kind = EpgClassifier.ClassifyChannel(channel.Name, channel.Category);
            var own = await _ownGuide.FetchAsync(channel, key, NormalizeCountry(channel.Country), kind, zone, config.HtmlGuideRecipesJson, ct).ConfigureAwait(false);
            if (own.Error is not null)
            {
                PluginLog.Warn($"[EPG] Guía propia de '{channel.Name}': {own.Error}");
                result.Sources.Add(new EpgSourceSyncResult { SourceId = "own:" + channel.Id, SourceName = "Guía propia · " + channel.Name, Error = own.Error });
                continue;
            }

            var fragment = new ChannelFragment(key, own.ChannelXml, "propia");
            gathered.Add(("own:" + channel.Id, "Guía propia · " + channel.Name, true, new FilteredSource(new List<ChannelFragment> { fragment }, own.Programmes, null, 0)));
            result.Sources.Add(new EpgSourceSyncResult
            {
                SourceId = "own:" + channel.Id,
                SourceName = "Guía propia · " + channel.Name,
                Success = true,
                ChannelsKept = 1,
                ProgrammesKept = own.Programmes.Count,
            });
            PluginLog.Info($"[EPG] Guía propia de '{channel.Name}': {own.Programmes.Count} programas");
        }

        // 2. Por canal: elegir la fuente principal y completar los huecos con las demás.
        Step("Fusionando fuentes", string.Empty);
        var nowUtc = DateTimeOffset.UtcNow;
        var perKey = new Dictionary<string, List<GuideCandidate>>(StringComparer.OrdinalIgnoreCase);
        var channelXml = new Dictionary<(string SourceId, string Key), ChannelFragment>();
        var prunedBySource = new Dictionary<string, int>(StringComparer.Ordinal);
        var earliest = nowUtc - KeepPast;
        var latest = nowUtc + KeepFuture;
        foreach (var (id, name, isOwn, content) in gathered)
        {
            foreach (var fragment in content.Channels)
            {
                channelXml[(id, fragment.Id)] = fragment;
            }

            foreach (var group in content.Programmes.GroupBy(static p => p.Channel, StringComparer.OrdinalIgnoreCase))
            {
                var items = new List<GuideItem>();
                foreach (var p in group)
                {
                    var start = ParseXmltvTime(p.Start, nowUtc);
                    var stop = ParseXmltvTime(p.Stop, start.AddMinutes(30));
                    if (stop <= start)
                    {
                        stop = start.AddMinutes(30);
                    }

                    if (stop < earliest || start > latest)
                    {
                        prunedBySource[id] = prunedBySource.GetValueOrDefault(id) + 1;
                        continue;
                    }

                    items.Add(new GuideItem(start, stop, p.Xml, !string.IsNullOrWhiteSpace(p.Data.Description)));
                }

                var method = content.Channels.FirstOrDefault(c => string.Equals(c.Id, group.Key, StringComparison.OrdinalIgnoreCase))?.Method ?? "tvg-id";
                if (!perKey.TryGetValue(group.Key, out var list))
                {
                    list = new List<GuideCandidate>();
                    perKey[group.Key] = list;
                }

                list.Add(new GuideCandidate(id, name, method, isOwn, items));
            }
        }

        var report = new Dictionary<string, GuideReportEntry>(StringComparer.OrdinalIgnoreCase);
        var contributedBySource = new Dictionary<string, int>(StringComparer.Ordinal);
        var channelsBySource = new Dictionary<string, int>(StringComparer.Ordinal);
        var merges = new List<(string Key, GuideMergeResult Merge)>();
        foreach (var (key, candidates) in perKey)
        {
            var merge = EpgMerger.Merge(candidates, nowUtc);
            if (merge.Primary is null)
            {
                continue;
            }

            merges.Add((key, merge));
            report[key] = new GuideReportEntry(merge.Primary.SourceName, merge.Contributed.Count(static c => c.Value > 0) - 1, candidates.Count);
            foreach (var (sourceId, count) in merge.Contributed.Where(static c => c.Value > 0))
            {
                contributedBySource[sourceId] = contributedBySource.GetValueOrDefault(sourceId) + count;
                channelsBySource[sourceId] = channelsBySource.GetValueOrDefault(sourceId) + 1;
            }
        }

        // Las fuentes también dicen quién es el canal: su logo y el país de su emisión. Solo se completan los vacíos.
        await ApplyIdentityAsync(config, merges, channelXml, result, ct).ConfigureAwait(false);

        // Cada programa se completa con lo que dicen las demás fuentes de esa misma emisión y se enriquece una sola vez,
        // ya con esa evidencia (cuántas fuentes coinciden y qué otros títulos usan).
        var published = new List<(string Key, List<XmlProgrammeRecord> Records)>();
        var allRecords = new List<XmlProgrammeRecord>();
        var corroboratedTotal = 0;
        foreach (var (key, merge) in merges)
        {
            channelCtx.TryGetValue(key, out var chCtx);
            var records = new List<XmlProgrammeRecord>(merge.Items.Count);
            foreach (var item in merge.Items)
            {
                var (xml, corroborated, alternates) = EpgConsolidator.Consolidate(item);
                var data = ParseProgrammeData(xml);
                var record = new XmlProgrammeRecord(xml, key, ReadAttribute(xml, "start"), ReadAttribute(xml, "stop"), data, chCtx.Kind, string.IsNullOrEmpty(chCtx.Name) ? key : chCtx.Name, chCtx.Country)
                {
                    Corroborated = corroborated,
                    AlternateTitles = alternates,
                };
                corroboratedTotal += corroborated > 0 ? 1 : 0;
                records.Add(record);
                allRecords.Add(record);
            }

            published.Add((key, records));
        }

        Step(tmdbCtx != null ? "Enriqueciendo con TMDB" : "Validando imágenes", string.Empty);
        if (tmdbCtx != null && tmdbService != null && allRecords.Count > 0)
        {
            var stats = await tmdbService.EnrichXmlProgrammesAsync(allRecords, tmdbCtx, ct).ConfigureAwait(false);
            result.Enrichment = stats;
            PluginLog.Info(
                $"[EPG] Enriquecimiento: {stats.ProgrammesInput} programas → {stats.UniqueAfterDedup} títulos únicos → {stats.Enriched} enriquecidos · " +
                $"corroborados por otra fuente: {corroboratedTotal} · alternos: {stats.FallbackMatches} · sin coincidencia: {stats.NoMatch} · " +
                $"cache: {stats.MemoryCacheHits} mem + {stats.NegativeCacheSkips} neg · TMDB: {stats.TmdbRequests} req · errores: {stats.Errors}");
        }

        // Las imágenes que publican las fuentes pueden estar caducadas (Jellyfin respondería 500) y hay que anotar su forma.
        await EpgIconValidator.ValidateAsync(allRecords, _httpClientFactory.CreateClient("JellyTV"), ct).ConfigureAwait(false);

        Step("Escribiendo la guía", string.Empty);
        var tempPath = _epgFilePath + ".tmp";
        long written;

        // La guía se escribe en streaming para no tener el documento completo en memoria.
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024))
        {
            await writer.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE tv SYSTEM \"xmltv.dtd\">\n<tv generator-info-name=\"JellyTV Plugin\">\n").ConfigureAwait(false);

            foreach (var (key, merge) in merges)
            {
                var primaryId = merge.Primary!.SourceId;
                await writer.WriteAsync(channelXml.GetValueOrDefault((primaryId, key))?.Xml ?? $"<channel id=\"{SecurityElement.Escape(key)}\"></channel>").ConfigureAwait(false);
                await writer.WriteAsync('\n').ConfigureAwait(false);
            }

            foreach (var (_, records) in published)
            {
                foreach (var record in records)
                {
                    await writer.WriteAsync(record.Xml).ConfigureAwait(false);
                    await writer.WriteAsync('\n').ConfigureAwait(false);
                }
            }

            await writer.WriteAsync("</tv>").ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            written = stream.Length;
        }

        // 3. Resumen por fuente: cuánto aportó de verdad a la guía publicada.
        foreach (var sourceResult in result.Sources.Where(static r => r.Success))
        {
            sourceResult.ChannelsKept = channelsBySource.GetValueOrDefault(sourceResult.SourceId);
            sourceResult.ProgrammesKept = contributedBySource.GetValueOrDefault(sourceResult.SourceId);
            sourceResult.ProgrammesPruned = prunedBySource.GetValueOrDefault(sourceResult.SourceId);
            var configured = epgSources.Find(s => s.Id == sourceResult.SourceId);
            if (configured is null)
            {
                continue;
            }

            var primaryFor = report.Values.Count(r => r.Primary == configured.Name);
            configured.LastSyncSummary = sourceResult.UsedCache
                ? $"{sourceResult.ChannelsKept} canales · {sourceResult.ProgrammesKept} programas (copia guardada)"
                : $"{sourceResult.ChannelsKept} canales · {sourceResult.ProgrammesKept} programas · principal en {primaryFor}"
                    + (sourceResult.ChannelsByName > 0 ? $" · {sourceResult.ChannelsByName} emparejados por nombre" : string.Empty);
        }

        result.Success = result.Sources.Exists(static s => s.Success);
        if (result.Success)
        {
            File.Move(tempPath, _epgFilePath, true);
            SaveReport(report);
            PluginLog.Info($"[EPG] Guía local escrita: {_epgFilePath} ({written:N0} bytes) · {report.Count} canales con guía");

            // Jellyfin cachea el XMLTV ~1 h; invalidar su copia para que el Refresh Guide encadenado
            // re-descargue la guía nueva y no use una caché vacía/vieja (causa de "sin programación").
            Plugin.Instance?.InvalidateJellyfinXmltvCache();
        }
        else if (File.Exists(tempPath))
        {
            // Todas las fuentes fallaron y no había copia: se conserva la guía anterior tal cual.
            File.Delete(tempPath);
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
    /// Completa la identidad de los canales con lo que dicen las fuentes de guía: el logo (el que viene en el
    /// <c>&lt;channel&gt;</c> de la fuente o en su página) y el país (sufijo de su tvg-id, como <c>.mx</c>, o el de la
    /// identidad de TVMaze). Solo rellena campos vacíos: nada de lo que el usuario definió se pisa.
    /// </summary>
    private async Task ApplyIdentityAsync(
        Configuration.PluginConfiguration config,
        List<(string Key, GuideMergeResult Merge)> merges,
        Dictionary<(string SourceId, string Key), ChannelFragment> fragments,
        EpgSyncResult result,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        var byKey = merges.ToDictionary(static m => m.Key, static m => m.Merge, StringComparer.OrdinalIgnoreCase);
        foreach (var channel in config.Channels.Where(static c => c.IsEnabled))
        {
            if (!byKey.TryGetValue(EpgChannelMatcher.KeyFor(channel), out var merge) || merge.Primary is null)
            {
                continue;
            }

            var key = EpgChannelMatcher.KeyFor(channel);

            // La fuente principal primero y luego las demás por puntuación.
            var order = merge.Scores.OrderByDescending(static kv => kv.Value).Select(static kv => kv.Key).ToList();
            order.Remove(merge.Primary.SourceId);
            order.Insert(0, merge.Primary.SourceId);
            var found = order.Select(id => fragments.GetValueOrDefault((id, key))).Where(static f => f is not null).Select(static f => f!).ToList();

            if (string.IsNullOrWhiteSpace(channel.Country))
            {
                // Solo con evidencia del propio canal: el sufijo de su tvg-id (ESPN.mx) o la identidad confirmada por TVMaze. El
                // sufijo de la guía que ganó por nombre no vale: dice dónde se publica esa guía, no de dónde es el canal.
                var country = EpgChannelMatcher.CountryFromId(channel.TvgId ?? string.Empty)
                    ?? NormalizeCountry(channel.Identity?.Country);
                if (country is not null)
                {
                    channel.Country = country;
                    result.IdentityCountries++;
                    PluginLog.Info($"[EPG] Identidad: '{channel.Name}' → país {country}");
                }
            }

            if (string.IsNullOrWhiteSpace(channel.LogoUrl))
            {
                foreach (var logo in found.Select(static f => LogoOf(f.Xml)).Where(static l => l is not null))
                {
                    if (await EpgIconValidator.IsImageAsync(logo!, client, ct).ConfigureAwait(false))
                    {
                        channel.LogoUrl = logo;
                        result.IdentityLogos++;
                        PluginLog.Info($"[EPG] Identidad: '{channel.Name}' → logo {logo}");
                        break;
                    }
                }
            }
        }

        if (result.IdentityLogos + result.IdentityCountries > 0)
        {
            PluginLog.Info($"[EPG] Identidad completada desde las guías: {result.IdentityLogos} logo(s), {result.IdentityCountries} país(es)");
        }
    }

    private static string? LogoOf(string channelXml)
    {
        var tagStart = channelXml.IndexOf("<icon", StringComparison.Ordinal);
        if (tagStart < 0)
        {
            return null;
        }

        var src = channelXml.IndexOf("src=\"", tagStart, StringComparison.Ordinal);
        if (src < 0)
        {
            return null;
        }

        var from = src + 5;
        var to = channelXml.IndexOf('"', from);
        var url = to < 0 ? string.Empty : System.Net.WebUtility.HtmlDecode(channelXml[from..to]).Trim();
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
    }

    /// <summary>
    /// Lee el informe de la última sincronización.
    /// </summary>
    /// <returns>Fuente principal y número de alternas por clave de canal; vacío si aún no hay informe.</returns>
    internal IReadOnlyDictionary<string, GuideReportEntry> LoadReport()
    {
        try
        {
            if (!File.Exists(ReportPath))
            {
                return new Dictionary<string, GuideReportEntry>();
            }

            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, GuideReportEntry>>(File.ReadAllText(ReportPath))
                ?? new Dictionary<string, GuideReportEntry>();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return new Dictionary<string, GuideReportEntry>();
        }
    }

    private void SaveReport(Dictionary<string, GuideReportEntry> report)
    {
        try
        {
            File.WriteAllText(ReportPath, System.Text.Json.JsonSerializer.Serialize(report));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.Warn($"[EPG] No se pudo guardar el informe de la guía: {ex.Message}");
        }
    }

    private static TimeZoneInfo? ResolveZone(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || configured.Trim().Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configured.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// Borra todo lo que el plugin recuerda entre sincronizaciones para que la siguiente parta de cero: las copias de
    /// respaldo de cada fuente, la memoria de títulos sin coincidencia y la de imágenes comprobadas. La guía publicada
    /// (<c>epg.xml</c>) no se toca hasta que la nueva sincronización termine bien, así Jellyfin nunca se queda sin guía.
    /// </summary>
    public void ResetCaches()
    {
        var removed = 0;
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(_epgFilePath) ?? string.Empty, "cache");
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.xml"))
                {
                    File.Delete(file);
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.Warn($"[EPG] No se pudieron borrar todas las copias de las fuentes: {ex.Message}");
        }

        Plugin.Instance?.TmdbEnrichment?.ClearCaches();
        EpgIconValidator.Clear();
        PluginLog.Info($"[EPG] Cachés limpiadas: {removed} copia(s) de fuente, títulos sin coincidencia e imágenes comprobadas.");
    }

    /// <summary>
    /// Canales que declaran las guías locales configuradas, según su última sincronización (vacío antes de la primera).
    /// </summary>
    /// <param name="sources">Guías configuradas.</param>
    /// <returns>Canales de todas ellas.</returns>
    internal IReadOnlyList<GuideChannelEntry> LoadGuideChannels(IEnumerable<EpgSource> sources)
        => sources.SelectMany(s => GuideChannelCatalog.Load(ChannelsPath(s.Id))).ToList();

    /// <summary>
    /// Guía automática, paso 1: identifica con iptv-org los canales que aún no tienen identidad (país, tvg-id, logo) y añade a
    /// las fuentes los XMLTV públicos de los países de esos canales. El emparejador de siempre reparte cada canal; lo que
    /// el usuario configuró a mano va primero y las automáticas nunca se guardan en la configuración.
    /// </summary>
    /// <param name="config">Configuración del plugin.</param>
    /// <param name="epgSources">Fuentes configuradas; se les añaden las automáticas.</param>
    /// <param name="ct">Token de cancelación.</param>
    private async Task AddAutomaticSourcesAsync(Configuration.PluginConfiguration config, List<EpgSource> epgSources, CancellationToken ct)
    {
        try
        {
            var enabled = config.Channels.Where(static c => c.IsEnabled).ToList();
            var identity = Plugin.Instance?.ChannelIdentity;
            if (identity is not null)
            {
                var (updated, _) = await identity.EnrichAsync(enabled, ct).ConfigureAwait(false);
                if (updated > 0)
                {
                    PluginLog.Info($"[EPG] Identidad iptv-org completada en {updated} canal(es)");
                    Plugin.Instance?.SaveConfiguration();
                }
            }

            var countries = enabled.Select(static c => string.IsNullOrWhiteSpace(c.Country) ? c.Identity?.Country : c.Country).OfType<string>();
            // epg-mx.xml y epg-mx.xml.gz son la misma guía: lo que ya tienes configurado no se descarga dos veces.
            var known = new HashSet<string>(epgSources.Select(static s => WithoutGzip(s.Url)), StringComparer.OrdinalIgnoreCase);
            foreach (var url in await _autoGuide.UrlsForAsync(countries, ct).ConfigureAwait(false))
            {
                if (!known.Add(WithoutGzip(url)))
                {
                    continue;
                }

                var file = Path.GetFileName(new Uri(url).LocalPath);
                var id = AutoSourcePrefix + file.Replace(".xml.gz", string.Empty, StringComparison.OrdinalIgnoreCase);
                epgSources.Add(new EpgSource { Id = id, Name = "Automática · " + file, Url = url });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PluginLog.Warn($"[EPG] La guía automática no se pudo preparar: {ex.Message}");
        }
    }

    private static string WithoutGzip(string url)
    {
        var trimmed = url.Trim();
        return trimmed.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? trimmed[..^3] : trimmed;
    }

    /// <summary>
    /// Guía automática, paso 2: a los canales que ninguna fuente XMLTV cubrió y que iptv-org enlaza con una página de canal
    /// de GatoTV (la web que el plugin sabe leer) les asigna esa página como guía propia, solo durante el sync.
    /// </summary>
    /// <param name="config">Configuración del plugin.</param>
    /// <param name="coveredKeys">Claves de guía que ya tienen programación de alguna fuente.</param>
    /// <param name="ownChannels">Canales con guía propia; se les añaden los automáticos.</param>
    /// <param name="ct">Token de cancelación.</param>
    private async Task AddAutomaticOwnGuidesAsync(Configuration.PluginConfiguration config, IEnumerable<string> coveredKeys, List<ChannelEntry> ownChannels, CancellationToken ct)
    {
        var identity = Plugin.Instance?.ChannelIdentity;
        if (identity is null)
        {
            return;
        }

        var covered = new HashSet<string>(coveredKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var channel in config.Channels.Where(static c => c.IsEnabled && string.IsNullOrWhiteSpace(c.EpgPageUrl)))
        {
            var id = channel.Identity?.IptvOrgId ?? channel.TvgId;
            if (string.IsNullOrWhiteSpace(id) || covered.Contains(EpgChannelMatcher.KeyFor(channel)))
            {
                continue;
            }

            try
            {
                var found = await identity.GetByIdAsync(id, ct).ConfigureAwait(false);
                if (found?.GatoTv is not { Length: > 0 } page)
                {
                    continue;
                }

                ownChannels.Add(new ChannelEntry
                {
                    Id = channel.Id,
                    Name = channel.Name,
                    Category = channel.Category,
                    Country = channel.Country,
                    TvgId = channel.TvgId,
                    EpgPageUrl = "https://www.gatotv.com/canal/" + page,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PluginLog.Warn($"[EPG] Guía automática de '{channel.Name}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Canales habilitados que necesitan guía, con la clave bajo la que se publica su programación.
    /// </summary>
    /// <param name="config">Configuración del plugin.</param>
    /// <returns>Canales objetivo.</returns>
    internal static List<EpgTarget> BuildTargets(Configuration.PluginConfiguration config)
    {
        var targets = new List<EpgTarget>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in config.Channels ?? new List<ChannelEntry>())
        {
            if (!channel.IsEnabled)
            {
                continue;
            }

            var key = EpgChannelMatcher.KeyFor(channel);
            if (keys.Add(key))
            {
                targets.Add(new EpgTarget(key, channel.Name, string.IsNullOrWhiteSpace(channel.TvgId) ? null : channel.TvgId.Trim(), NormalizeCountry(channel.Country)));
            }
        }

        return targets;
    }

    /// <summary>
    /// Pide una dirección de guía como lo haría una sincronización y devuelve lo que reconoce, sin guardar nada.
    /// </summary>
    /// <param name="url">Dirección de la guía.</param>
    /// <param name="channelName">Nombre del canal, para elegir canal en un XMLTV de varios.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Formato detectado, total y los primeros programas.</returns>
    internal Task<GuidePreview> PreviewGuideAsync(string url, string channelName, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        return _ownGuide.PreviewAsync(url, channelName, ResolveZone(config?.EpgTimezone), config?.HtmlGuideRecipesJson, ct);
    }

    /// <summary>Interpreta una hora XMLTV; devuelve null si no se puede leer.</summary>
    /// <param name="value">Valor del atributo start o stop.</param>
    /// <returns>El instante, o null.</returns>
    internal static DateTimeOffset? ParseXmltvTimeOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : ParseXmltvTime(value, DateTimeOffset.MinValue) is { } t && t != DateTimeOffset.MinValue ? t : null;

    /// <summary>Normaliza un código de país: dos letras en mayúsculas, o null.</summary>
    internal static string? NormalizeCountry(string? country)
        => string.IsNullOrWhiteSpace(country) || country.Trim().Length != 2 ? null : country.Trim().ToUpperInvariant();

    /// <summary>Lee un atributo de la etiqueta de apertura de un fragmento XML; vacío si no está.</summary>
    private static string ReadAttribute(string xml, string name)
    {
        var tagEnd = xml.IndexOf('>', StringComparison.Ordinal);
        var at = xml.IndexOf(name + "=\"", 0, tagEnd < 0 ? xml.Length : tagEnd, StringComparison.Ordinal);
        if (at < 0)
        {
            return string.Empty;
        }

        var from = at + name.Length + 2;
        var to = xml.IndexOf('"', from);
        return to < 0 ? string.Empty : xml[from..to];
    }

    private static string FormatAge(TimeSpan age)
        => age.TotalHours < 1 ? "menos de 1 h" : age.TotalHours < 48 ? $"{(int)age.TotalHours} h" : $"{(int)age.TotalDays} d";

    private string ChannelsPath(string sourceId) => Path.ChangeExtension(CachePath(sourceId), ".channels.json");

    // Solo las guías que el administrador configuró: las automáticas son los XMLTV públicos de iptv-org, que la búsqueda ya consulta.
    private void RememberGuideChannels(Configuration.PluginConfiguration config, EpgSource source, IReadOnlyList<EpgSourceChannel> channels)
    {
        if (!config.EpgSources.Any(s => string.Equals(s.Id, source.Id, StringComparison.Ordinal)))
        {
            return;
        }

        try
        {
            GuideChannelCatalog.Save(
                ChannelsPath(source.Id),
                channels.Select(c => new GuideChannelEntry(source.Name, c.Id, c.Names, c.Logo)).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.Warn($"[EPG] No se pudo guardar la lista de canales de '{source.Name}': {ex.Message}");
        }
    }

    private string CachePath(string sourceId)
    {
        var dir = Path.GetDirectoryName(_epgFilePath) ?? string.Empty;
        var safe = new string(sourceId.Where(static c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return Path.Combine(dir, "cache", safe + ".xml");
    }

    /// <summary>
    /// Guarda el resultado filtrado de una fuente para poder usarlo si la siguiente descarga falla.
    /// </summary>
    private void SaveCache(string sourceId, FilteredSource content)
    {
        try
        {
            var path = CachePath(sourceId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(false)))
            {
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<tv>\n");
                foreach (var c in content.Channels)
                {
                    w.Write(c.Xml);
                    w.Write('\n');
                }

                foreach (var p in content.Programmes)
                {
                    w.Write(p.Xml);
                    w.Write('\n');
                }

                w.Write("</tv>");
            }

            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.Warn($"[EPG] No se pudo guardar la copia de la fuente {sourceId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Carga la última copia buena de una fuente (como máximo de <see cref="MaxCacheAge"/>).
    /// </summary>
    private FilteredSource? LoadCache(string sourceId, Dictionary<string, (ChannelKind Kind, string Name, string? Country)> channelCtx, out TimeSpan age)
    {
        age = TimeSpan.Zero;
        try
        {
            var path = CachePath(sourceId);
            if (!File.Exists(path))
            {
                return null;
            }

            age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > MaxCacheAge)
            {
                return null;
            }

            var channels = new List<ChannelFragment>();
            var programmes = new List<XmlProgrammeRecord>();
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name == "channel")
                {
                    var id = reader.GetAttribute("id") ?? string.Empty;
                    channels.Add(new ChannelFragment(id, reader.ReadOuterXml(), "tvg-id"));
                }
                else if (reader.Name == "programme")
                {
                    var channel = reader.GetAttribute("channel") ?? string.Empty;
                    var start = reader.GetAttribute("start") ?? string.Empty;
                    var stop = reader.GetAttribute("stop");
                    var xml = reader.ReadOuterXml();
                    channelCtx.TryGetValue(channel, out var ctx);
                    programmes.Add(new XmlProgrammeRecord(xml, channel, start, stop, ParseProgrammeData(xml), ctx.Kind, string.IsNullOrEmpty(ctx.Name) ? channel : ctx.Name, ctx.Country));
                }
            }

            return programmes.Count == 0 && channels.Count == 0 ? null : new FilteredSource(channels, programmes, null, 0);
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            PluginLog.Warn($"[EPG] La copia guardada de {sourceId} no se pudo leer: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Descarga una fuente XMLTV a disco (con soporte de .gz), empareja sus canales con los del plugin y la
    /// transmite en streaming conservando solo los canales emparejados y sus programas. El enriquecimiento se hace
    /// después, una sola vez, sobre la guía ya fusionada.
    /// </summary>
    /// <param name="sourceUrl">URL del XMLTV.</param>
    /// <param name="targets">Canales del plugin que necesitan guía.</param>
    /// <param name="channelCtx">Contexto (tipo/nombre) por clave de guía.</param>
    /// <param name="onSourceChannels">Recibe todos los canales que declara la fuente, antes de filtrarlos.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Contenido filtrado (canales y programas de la fuente, sin enriquecer).</returns>
    private async Task<FilteredSource> DownloadFilteredAsync(
        string sourceUrl,
        List<EpgTarget> targets,
        Dictionary<string, (ChannelKind Kind, string Name, string? Country)> channelCtx,
        Action<IReadOnlyList<EpgSourceChannel>> onSourceChannels,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        PluginLog.Info($"[EPG] Downloading XMLTV: {sourceUrl}");

        var downloaded = Path.Combine(Path.GetTempPath(), "jellytv-epg-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            using (var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                dlCts.CancelAfter(TimeSpan.FromMinutes(XmltvTimeoutMinutes));
                using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, dlCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var body = await response.Content.ReadAsStreamAsync(dlCts.Token).ConfigureAwait(false);
                using var file = new FileStream(downloaded, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await body.CopyToAsync(file, dlCts.Token).ConfigureAwait(false);
            }

            // Pasada 1: lee todos los <channel> del archivo (algunas fuentes los intercalan con los <programme>).
            var sourceChannels = await ReadSourceChannelsAsync(downloaded).ConfigureAwait(false);
            onSourceChannels(sourceChannels);
            var matches = EpgChannelMatcher.Match(targets, sourceChannels);
            var byName = matches.Values.Count(static m => m.Method == "nombre");
            PluginLog.Info($"[EPG] '{sourceUrl}': {sourceChannels.Count} canales en la fuente → {matches.Count} emparejados ({byName} por nombre)");

            // Pasada 2: streaming completo filtrando por los canales emparejados.
            var channels = new List<ChannelFragment>();
            var programmes = new List<XmlProgrammeRecord>();
            var writerSettings = new XmlWriterSettings { Async = true, OmitXmlDeclaration = true, Indent = false, Encoding = Encoding.UTF8 };
            var xmlSettings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Ignore };

            using var input = OpenMaybeGzip(downloaded);
            using var reader = XmlReader.Create(input, xmlSettings);
            // Skip deja el lector ya en el nodo siguiente: sin este indicador un archivo minificado (Tubi) perdería el
            // elemento que va pegado al omitido.
            var advance = true;
            while (!advance ? !reader.EOF : await reader.ReadAsync().ConfigureAwait(false))
            {
                advance = true;
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name == "channel")
                {
                    var id = reader.GetAttribute("id") ?? string.Empty;
                    if (!matches.TryGetValue(id, out var match))
                    {
                        await reader.SkipAsync().ConfigureAwait(false);
                        advance = false;
                        continue;
                    }

                    var xml = await SerializeElementAsync(reader, writerSettings).ConfigureAwait(false);
                    channels.Add(new ChannelFragment(match.Key, RewriteAttribute(xml, "id", id, match.Key), match.Method, id));
                }
                else if (reader.Name == "programme")
                {
                    var channel = reader.GetAttribute("channel") ?? string.Empty;
                    if (!matches.TryGetValue(channel, out var match))
                    {
                        await reader.SkipAsync().ConfigureAwait(false);
                        advance = false;
                        continue;
                    }

                    var start = reader.GetAttribute("start") ?? string.Empty;
                    var stop = reader.GetAttribute("stop");
                    var xml = RewriteAttribute(await SerializeElementAsync(reader, writerSettings).ConfigureAwait(false), "channel", channel, match.Key);
                    var data = ParseProgrammeData(xml);
                    channelCtx.TryGetValue(match.Key, out var chCtx);
                    var chName = string.IsNullOrEmpty(chCtx.Name) ? match.Key : chCtx.Name;
                    programmes.Add(new XmlProgrammeRecord(xml, match.Key, start, stop, data, chCtx.Kind, chName, chCtx.Country));
                }
            }

            PluginLog.Info($"[EPG] Filter result for {sourceUrl}: {channels.Count} channels kept, {programmes.Count} programmes kept");
            return new FilteredSource(channels, programmes, null, byName);
        }
        finally
        {
            try
            {
                File.Delete(downloaded);
            }
            catch (IOException)
            {
                // Archivo temporal: el sistema lo limpia.
            }
        }
    }

    /// <summary>
    /// Abre el archivo descargado, descomprimiéndolo si es gzip (muchas guías se publican como .xml.gz).
    /// </summary>
    private static Stream OpenMaybeGzip(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var first = file.ReadByte();
        var second = file.ReadByte();
        file.Position = 0;
        return first == 0x1F && second == 0x8B ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    /// <summary>
    /// Lee los canales declarados al inicio de una fuente XMLTV (id y nombres) sin recorrer sus programas.
    /// </summary>
    private static async Task<List<EpgSourceChannel>> ReadSourceChannelsAsync(string path)
    {
        var list = new List<EpgSourceChannel>();
        using var input = OpenMaybeGzip(path);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Ignore });
        while (!reader.EOF)
        {
            // Hay fuentes (Plex, Tubi) que intercalan cada <channel> con sus <programme>, así que se recorre todo el archivo
            // saltando los programas sin leerlos. Skip ya deja el lector en el nodo siguiente: no se vuelve a leer.
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme")
            {
                await reader.SkipAsync().ConfigureAwait(false);
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Name == "channel")
            {
                var id = reader.GetAttribute("id") ?? string.Empty;
                var names = new List<string>();
                string? logo = null;
                using (var sub = reader.ReadSubtree())
                {
                    while (await sub.ReadAsync().ConfigureAwait(false))
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name" && !sub.IsEmptyElement)
                        {
                            var text = (await sub.ReadElementContentAsStringAsync().ConfigureAwait(false)).Trim();
                            if (text.Length > 0)
                            {
                                names.Add(text);
                            }
                        }
                        else if (sub.NodeType == XmlNodeType.Element && sub.Name == "icon" && logo is null)
                        {
                            var src = sub.GetAttribute("src")?.Trim();
                            logo = src is not null && src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? src : null;
                        }
                    }
                }

                if (id.Length > 0)
                {
                    list.Add(new EpgSourceChannel(id, names, EpgChannelMatcher.CountryFromId(id), logo));
                }
            }

            await reader.ReadAsync().ConfigureAwait(false);
        }

        return list;
    }

    /// <summary>
    /// Cambia el valor de un atributo de la etiqueta de apertura de un fragmento XML ya serializado.
    /// </summary>
    private static string RewriteAttribute(string xml, string attribute, string oldValue, string newValue)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return xml;
        }

        var tagEnd = xml.IndexOf('>', StringComparison.Ordinal);
        if (tagEnd < 0)
        {
            return xml;
        }

        var needle = attribute + "=\"" + SecurityElement.Escape(oldValue) + "\"";
        var at = xml.IndexOf(needle, 0, tagEnd, StringComparison.Ordinal);
        return at < 0
            ? xml
            : xml[..at] + attribute + "=\"" + SecurityElement.Escape(newValue) + "\"" + xml[(at + needle.Length)..];
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
    internal static EpgProgrammeData ParseProgrammeData(string xml)
    {
        string? titleEs = null, titleEn = null, desc = null, category = null, iconUrl = null, episodeNum = null;
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

                    case "episode-num":
                        if (episodeNum is null && !reader.IsEmptyElement)
                        {
                            episodeNum = reader.ReadElementContentAsString().Trim();
                        }

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
            EpisodeNum = string.IsNullOrWhiteSpace(episodeNum) ? null : episodeNum,
        };
    }

    /// <summary>
    /// Persists the EPG identities and programmes of one source so the guide stays aligned with the
    /// channel catalog in the active store. Best effort: a storage failure never aborts the sync.
    /// </summary>
    /// <param name="source">Configured EPG source.</param>
    /// <param name="priority">Configuration order of the source.</param>
    /// <param name="content">Identities and programmes kept by the filter.</param>
    private static void PersistEpgAlignment(EpgSource source, int priority, FilteredSource content)
    {
        var storage = Plugin.Instance?.Storage;
        if (storage is null)
        {
            return;
        }

        try
        {
            var byKey = new Dictionary<string, ChannelEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in Plugin.Instance?.Configuration.Channels ?? new List<ChannelEntry>())
            {
                byKey[EpgChannelMatcher.KeyFor(channel)] = channel;
            }

            var identities = new List<EpgChannelRecord>(content.Channels.Count);
            foreach (var fragment in content.Channels)
            {
                byKey.TryGetValue(fragment.Id, out var channel);
                identities.Add(new EpgChannelRecord(
                    source.Id,
                    fragment.Id,
                    channel?.Id,
                    channel?.Name ?? fragment.Id,
                    channel?.LogoUrl,
                    channel is null ? "none" : fragment.Method));
            }

            var now = DateTimeOffset.UtcNow;
            var entries = new List<EpgProgrammeRecord>(content.Programmes.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in content.Programmes)
            {
                byKey.TryGetValue(record.Channel, out var channel);
                var start = ParseXmltvTime(record.Start, now);

                // La clave de la tabla es (fuente, canal, inicio): una fuente que repite un inicio haría fallar todo el lote.
                if (!seen.Add(record.Channel + "|" + start.UtcTicks.ToString(CultureInfo.InvariantCulture)))
                {
                    continue;
                }

                var stop = ParseXmltvTime(record.Stop, start.AddMinutes(30));
                if (stop <= start)
                {
                    stop = start.AddMinutes(30);
                }

                entries.Add(new EpgProgrammeRecord(
                    source.Id,
                    record.Channel,
                    channel?.Id,
                    start,
                    stop,
                    string.IsNullOrWhiteSpace(record.Data.BestTitle) ? record.ChannelName : record.Data.BestTitle,
                    record.Data.Description,
                    record.Data.Category,
                    record.Data.ExistingIconUrl,
                    record.Data.Year));
            }

            storage.Store.SaveEpgSync(
                new EpgSourceState(
                    source.Id,
                    source.Name,
                    source.Url,
                    source.IsEnabled,
                    priority,
                    source.LastSyncedAt ?? now,
                    source.LastSyncSummary,
                    source.LastSyncError,
                    identities.Count,
                    entries.Count),
                identities,
                entries);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[EPG] No se pudo persistir la alineación de '{source.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Parses an XMLTV timestamp ("20261018060000 -0600") into an absolute instant.
    /// </summary>
    /// <param name="value">Raw attribute value.</param>
    /// <param name="fallback">Value used when the timestamp cannot be parsed.</param>
    /// <returns>The parsed instant.</returns>
    private static DateTimeOffset ParseXmltvTime(string? value, DateTimeOffset fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !DateTime.TryParseExact(parts[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return fallback;
        }

        var offset = TimeSpan.Zero;
        if (parts.Length > 1 && parts[1].Length == 5
            && int.TryParse(parts[1].AsSpan(1, 2), out var hours)
            && int.TryParse(parts[1].AsSpan(3, 2), out var minutes))
        {
            var sign = parts[1][0] == '-' ? -1 : 1;
            offset = TimeSpan.FromMinutes(sign * ((hours * 60) + minutes));
        }

        return new DateTimeOffset(local, offset);
    }

    private sealed class ChannelFragment
    {
        internal ChannelFragment(string id, string xml, string method, string? xmltvId = null)
        {
            Id = id;
            Xml = xml;
            Method = method;
            XmltvId = xmltvId ?? id;
        }

        internal string XmltvId { get; }

        internal string Id { get; }

        internal string Xml { get; }

        internal string Method { get; }
    }

    private sealed class FilteredSource
    {
        internal FilteredSource(List<ChannelFragment> channels, List<XmlProgrammeRecord> programmes, EpgEnrichmentStats? stats, int byName)
        {
            Channels = channels;
            Programmes = programmes;
            Stats = stats;
            ByName = byName;
        }

        internal List<ChannelFragment> Channels { get; }

        internal List<XmlProgrammeRecord> Programmes { get; }

        internal EpgEnrichmentStats? Stats { get; }

        internal int ByName { get; }
    }
}
