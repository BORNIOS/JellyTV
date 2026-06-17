using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using JellyTV.Models;
using JellyTV.Services.Tmdb;
using Microsoft.Extensions.Logging;

namespace JellyTV.Services;

/// <summary>
/// Downloads large XMLTV files on the Jellyfin server (full CPU/RAM available),
/// filters them to only the channels present in the configured M3U sources,
/// and pushes the slim result to the Worker via POST /api/epg/import-xml.
///
/// This keeps the Cloudflare Worker free from processing 100-200 MB EPG files.
/// </summary>
public class EpgFilterService
{
    private const int M3U_TIMEOUT_SECONDS   = 30;
    private const int XMLTV_TIMEOUT_MINUTES = 15;
    private const int PUSH_TIMEOUT_SECONDS  = 120;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EpgFilterService> _logger;

    public EpgFilterService(IHttpClientFactory httpClientFactory, ILogger<EpgFilterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Syncs all enabled EPG sources to the Worker:
    ///  1. Collects tvg-ids from all M3U sources
    ///  2. For each EPG source, downloads + filters + pushes
    /// </summary>
    public async Task<EpgSyncResult> SyncAllToWorkerAsync(CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var result = new EpgSyncResult();

        if (config is null)
        {
            result.Error = "Plugin configuration unavailable";
            return result;
        }

        var workerBase = config.WorkerBaseUrl?.TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workerBase))
        {
            result.Error = "WorkerBaseUrl not configured";
            return result;
        }

        var epgSources = config.EpgSources?.FindAll(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url));
        if (epgSources is null || epgSources.Count == 0)
        {
            result.Error = "No EPG sources configured";
            return result;
        }

        // Step 1: collect all tvg-ids from M3U sources
        PluginLog.Info("[EPG] Collecting tvg-ids from M3U sources…");
        var tvgIds = await CollectTvgIdsAsync(ct);
        result.TvgIdsFound = tvgIds.Count;

        if (tvgIds.Count == 0)
        {
            result.Error = "No tvg-ids found in M3U sources — nothing to match against";
            return result;
        }

        // Create a shared TMDB run context for this sync run.
        // Rate limiter and memory cache are shared across all EPG sources in a single run
        // so we don't exceed TMDB quota and don't repeat lookups for cross-source duplicates.
        var tmdbService = Plugin.Instance?.TmdbEnrichment;
        var hasTmdb     = tmdbService != null && !string.IsNullOrWhiteSpace(config.TmdbApiKey);
        TmdbEnrichmentRunContext? tmdbCtx = hasTmdb ? tmdbService!.CreateRunContext() : null;

        // Step 2: for each EPG source, download → filter → enrich inline → push
        foreach (var epgSource in epgSources)
        {
            var sourceResult = new EpgSourceSyncResult { SourceId = epgSource.Id, SourceName = epgSource.Name };
            try
            {
                PluginLog.Info($"[EPG] Processing source '{epgSource.Name}' ({epgSource.Url})");
                var (filteredXml, channels, programmes, programmeData) =
                    await BuildFilteredXmltvAsync(epgSource.Url, tvgIds, ct);

                sourceResult.ChannelsKept   = channels;
                sourceResult.ProgrammesKept = programmes;

                var workerResponse = await PushToWorkerAsync(
                    workerBase,
                    config.WorkerApiKey,
                    filteredXml,
                    $"{epgSource.Name}|{epgSource.Id}",
                    ct);

                sourceResult.WorkerResponse = workerResponse;

                // ── Inline TMDB enrichment using rich EPG metadata ──────────────
                if (tmdbCtx != null && programmeData.Count > 0)
                {
                    try
                    {
                        var (stats, entries) = await tmdbService!.EnrichProgrammesAsync(programmeData, tmdbCtx, ct);
                        sourceResult.EnrichmentStats = stats;

                        if (entries.Count > 0)
                        {
                            foreach (var chunk in entries.Chunk(200))
                            {
                                try
                                {
                                    stats.Saved += await tmdbService.PushEnrichBatchToWorkerAsync(
                                        workerBase, config.WorkerApiKey, chunk, ct);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    PluginLog.Error($"[EPG] TMDB batch push failed for '{epgSource.Name}'", ex);
                                }
                            }
                        }

                        var hitRate = stats.UniqueAfterDedup == 0
                            ? 0.0
                            : (double)stats.Enriched / stats.UniqueAfterDedup;

                        PluginLog.Info(
                            $"[EPG] '{epgSource.Name}' enrichment: {programmeData.Count} programmes → " +
                            $"{stats.UniqueAfterDedup} unique → {stats.Enriched} enriched ({hitRate:P1}) · " +
                            $"cache: {stats.MemoryCacheHits} mem + {stats.NegativeCacheSkips} neg · " +
                            $"no-match: {stats.NoMatch} · skip: {stats.SkippedByHeuristics} · " +
                            $"TMDB: {stats.TmdbRequests} calls · saved: {stats.Saved}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        PluginLog.Error($"[EPG] TMDB enrichment failed for source '{epgSource.Name}'", ex);
                        sourceResult.EnrichmentError = ex.Message;
                    }
                }

                sourceResult.Success = true;

                // Update source metadata in config
                epgSource.LastSyncedAt    = DateTimeOffset.UtcNow;
                epgSource.LastSyncSummary = $"{channels} canales · {programmes} programas" +
                    (sourceResult.EnrichmentStats is { } s
                        ? $" · {s.Enriched} enriquecidos"
                        : string.Empty);
                epgSource.LastSyncError   = null;
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[EPG] Source '{epgSource.Name}' failed", ex);
                sourceResult.Error = ex.Message;
                epgSource.LastSyncError   = ex.Message;
                epgSource.LastSyncedAt    = DateTimeOffset.UtcNow;
                epgSource.LastSyncSummary = null;
            }

            result.Sources.Add(sourceResult);
        }

        // Persist updated source metadata
        try { Plugin.Instance?.SaveConfiguration(); } catch { /* non-critical */ }

        result.Success = result.Sources.Exists(s => s.Success);
        return result;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads all enabled M3U sources and extracts unique tvg-id values.
    /// </summary>
    internal async Task<HashSet<string>> CollectTvgIdsAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var tvgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config is null) return tvgIds;

        var client = _httpClientFactory.CreateClient("JellyTV");
        var sources = config.Sources?.FindAll(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)) ?? new();

        foreach (var source in sources)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(M3U_TIMEOUT_SECONDS));

                var m3uText = await client.GetStringAsync(source.Url, cts.Token);

                // Extract tvg-id="..." from EXTINF lines
                var matches = Regex.Matches(m3uText, @"tvg-id=""([^""]*)""", RegexOptions.IgnoreCase);
                foreach (Match m in matches)
                {
                    var id = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(id))
                        tvgIds.Add(id);
                }

                PluginLog.Info($"[EPG] Source '{source.Name}': found {matches.Count} tvg-id entries (total distinct so far: {tvgIds.Count})");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[EPG] Failed to read tvg-ids from M3U source '{source.Name}'", ex);
            }
        }

        return tvgIds;
    }

    /// <summary>
    /// Streams a XMLTV file, keeps only channels whose id is in <paramref name="tvgIds"/>
    /// and their associated programmes, and returns the filtered XML document.
    /// Uses XmlReader (SAX) — never loads the full document into memory.
    /// </summary>
    internal async Task<(string Xml, int Channels, int Programmes, List<EpgProgrammeData> ProgrammeData)> BuildFilteredXmltvAsync(
        string sourceUrl,
        HashSet<string> tvgIds,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");

        PluginLog.Info($"[EPG] Downloading XMLTV: {sourceUrl}");
        using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dlCts.CancelAfter(TimeSpan.FromMinutes(XMLTV_TIMEOUT_MINUTES));

        using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, dlCts.Token);
        response.EnsureSuccessStatusCode();

        await using var inputStream = await response.Content.ReadAsStreamAsync(dlCts.Token);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE tv SYSTEM \"xmltv.dtd\">\n");
        sb.Append("<tv generator-info-name=\"JellyTV Plugin\">\n");

        var matchedXmltvIds  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programmeDataList = new List<EpgProgrammeData>();
        int channelCount = 0, programmeCount = 0;

        var xmlSettings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        var elemWriterSettings = new XmlWriterSettings
        {
            Async = true,
            OmitXmlDeclaration = true,
            Indent = false,
            Encoding = Encoding.UTF8,
        };

        using var reader = XmlReader.Create(inputStream, xmlSettings);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name == "channel")
            {
                var id = reader.GetAttribute("id") ?? string.Empty;
                if (!string.IsNullOrEmpty(id) && tvgIds.Contains(id))
                {
                    matchedXmltvIds.Add(id);
                    channelCount++;
                    var elemSb = new StringBuilder();
                    await using var w = XmlWriter.Create(elemSb, elemWriterSettings);
                    await w.WriteNodeAsync(reader.ReadSubtree(), true).ConfigureAwait(false);
                    await w.FlushAsync().ConfigureAwait(false);
                    sb.Append(elemSb);
                    sb.Append('\n');
                }
                else
                {
                    await reader.SkipAsync().ConfigureAwait(false);
                }
            }
            else if (reader.Name == "programme")
            {
                var channel = reader.GetAttribute("channel") ?? string.Empty;
                if (!string.IsNullOrEmpty(channel) && matchedXmltvIds.Contains(channel))
                {
                    programmeCount++;
                    var elemSb = new StringBuilder();
                    await using var w = XmlWriter.Create(elemSb, elemWriterSettings);
                    await w.WriteNodeAsync(reader.ReadSubtree(), true).ConfigureAwait(false);
                    await w.FlushAsync().ConfigureAwait(false);
                    sb.Append(elemSb);
                    sb.Append('\n');

                    // Extract rich metadata for inline TMDB enrichment.
                    var pd = ParseProgrammeData(elemSb.ToString());
                    if (!string.IsNullOrWhiteSpace(pd.BestTitle))
                        programmeDataList.Add(pd);
                }
                else
                {
                    await reader.SkipAsync().ConfigureAwait(false);
                }
            }
        }

        sb.Append("</tv>");

        PluginLog.Info($"[EPG] Filter result for {sourceUrl}: {channelCount} channels kept, {programmeCount} programmes kept, {programmeDataList.Count} with parseable metadata");
        return (sb.ToString(), channelCount, programmeCount, programmeDataList);
    }

    /// <summary>
    /// Parses the rich metadata fields from a single serialized &lt;programme&gt; XML element.
    /// Called synchronously since the string is small (one programme) and avoids a second HTTP call.
    /// </summary>
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
                if (reader.NodeType != XmlNodeType.Element) continue;

                switch (reader.Name)
                {
                    case "title":
                        var lang      = reader.GetAttribute("lang") ?? string.Empty;
                        var titleText = reader.IsEmptyElement ? string.Empty : reader.ReadElementContentAsString();
                        if (lang.Equals("en", StringComparison.OrdinalIgnoreCase))
                            titleEn ??= titleText.Trim();
                        else
                            titleEs ??= titleText.Trim();
                        break;

                    case "desc":
                        if (desc is null && !reader.IsEmptyElement)
                            desc = reader.ReadElementContentAsString().Trim();
                        break;

                    case "category":
                        if (category is null && !reader.IsEmptyElement)
                        {
                            var cat = reader.ReadElementContentAsString().Trim();
                            if (!string.IsNullOrWhiteSpace(cat)) category = cat;
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
            // Use whatever was parsed successfully — incomplete data is better than a crash.
        }

        return new EpgProgrammeData
        {
            TitleEs        = string.IsNullOrWhiteSpace(titleEs) ? null : titleEs,
            TitleEn        = string.IsNullOrWhiteSpace(titleEn) ? null : titleEn,
            Description    = string.IsNullOrWhiteSpace(desc)    ? null : desc,
            Category       = string.IsNullOrWhiteSpace(category) ? null : category,
            Year           = year,
            ExistingIconUrl = iconUrl,
        };
    }

    /// <summary>
    /// POSTs the filtered XML to the Worker's /api/epg/import-xml endpoint.
    /// </summary>
    private async Task<string> PushToWorkerAsync(
        string workerBase,
        string? apiKey,
        string filteredXml,
        string sourceLabel,
        CancellationToken ct)
    {
        var endpoint = $"{workerBase}/api/epg/import-xml?source={Uri.EscapeDataString(sourceLabel)}";

        PluginLog.Info($"[EPG] Pushing {filteredXml.Length:N0} chars to Worker → {endpoint}");

        var client = _httpClientFactory.CreateClient("JellyTV");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(filteredXml, Encoding.UTF8, "application/xml");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("x-api-key", apiKey);

        using var pushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pushCts.CancelAfter(TimeSpan.FromSeconds(PUSH_TIMEOUT_SECONDS));

        using var httpResponse = await client.SendAsync(request, pushCts.Token).ConfigureAwait(false);
        var body = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            PluginLog.Error($"[EPG] Worker returned {(int)httpResponse.StatusCode}: {body}");
            throw new InvalidOperationException($"Worker returned HTTP {(int)httpResponse.StatusCode}: {body}");
        }

        PluginLog.Info($"[EPG] Worker accepted import: {body}");
        return body;
    }
}

// ── Result DTOs ───────────────────────────────────────────────────────────────

public class EpgSyncResult
{
    public bool Success { get; set; }
    public int TvgIdsFound { get; set; }
    public List<EpgSourceSyncResult> Sources { get; set; } = new();
    public string? Error { get; set; }
}

public class EpgSourceSyncResult
{
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int ChannelsKept { get; set; }
    public int ProgrammesKept { get; set; }
    public string? WorkerResponse { get; set; }
    public string? Error { get; set; }

    /// <summary>Inline TMDB enrichment stats. Null when TMDB is not configured.</summary>
    public EpgEnrichmentStats? EnrichmentStats { get; set; }

    /// <summary>Error message if enrichment failed (filtering still succeeds).</summary>
    public string? EnrichmentError { get; set; }
}
