using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Orchestrates TMDB enrichment for uncached EPG programmes.
/// Heavy matching and TMDB IO run in Jellyfin server, Worker only stores cache in D1.
/// </summary>
public sealed class TmdbEnrichmentService
{
    private const int BatchChunkSize = 200;
    private const int UncachedTitlesLimit = 3000;

    private static readonly string[] SearchLanguages = ["es-MX", "es-ES", "en-US"];
    private static readonly string[] PreferredOriginalLanguages = ["es", "en", "pt"];

    // Skip content that is usually non-cinematic filler/noise in live EPG.
    private static readonly Regex SkipRe = new(
        @"\b(test\s*card|publicidad|anuncios?|infomercial|paid\s*programming|shopping|teleshopping|sign\s*off|weather|local\s*news|promo|trailer\s*loop|filler|sin\s*informacion|tba|to\s*be\s*announced|programa\s*por\s*definir)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimestampLikeRe = new(
        @"^\s*(\d{1,2}:\d{2}(\s*[ap]\.??m\.?)?|\d{1,2}/\d{1,2}(/\d{2,4})?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChannelLikeRe = new(
        @"\b(hd|uhd|4k|canal|channel|tv)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbEnrichmentService> _logger;

    private readonly ITmdbNormalizationService _normalizer;
    private readonly IMatchEvaluator _matchEvaluator;
    private readonly INegativeCacheService _negativeCache;
    private readonly ITmdbMetadataMapper _metadataMapper;

    public TmdbEnrichmentService(
        IHttpClientFactory httpClientFactory,
        ILogger<TmdbEnrichmentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _normalizer = new TmdbNormalizationService();
        _matchEvaluator = new JellyTV.Services.Tmdb.MatchEvaluator(_normalizer);
        _negativeCache = new NegativeCacheService();
        _metadataMapper = new TmdbMetadataMapper();
    }

    public async Task<EnrichmentRunSummary> EnrichAsync(
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var summary = new EnrichmentRunSummary();

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            summary.Error = "Plugin configuration unavailable";
            return summary;
        }

        var tmdbKey = cfg.TmdbApiKey?.Trim() ?? string.Empty;
        var workerUrl = cfg.WorkerBaseUrl?.TrimEnd('/') ?? string.Empty;
        var apiKey = cfg.WorkerApiKey?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(tmdbKey))
        {
            summary.Error = "TmdbApiKey not configured";
            return summary;
        }

        if (string.IsNullOrWhiteSpace(workerUrl))
        {
            summary.Error = "WorkerBaseUrl not configured";
            return summary;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            summary.Error = "WorkerApiKey not configured";
            return summary;
        }

        var programmes = await GetUncachedProgrammesAsync(workerUrl, apiKey, ct).ConfigureAwait(false);
        if (programmes.Count == 0)
        {
            _logger.LogInformation("[JellyTV] TmdbEnrich: no titles need enrichment");
            progress?.Report(100);
            return summary;
        }

        summary.TitlesFound = programmes.Count;

        // Deduplicate by normalized title before hitting TMDB.
        var unique = new Dictionary<string, UncachedProgrammeItem>(StringComparer.Ordinal);
        foreach (var p in programmes)
        {
            var normalized = _normalizer.NormalizeTitle(p.Title);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (!unique.ContainsKey(normalized)) unique[normalized] = p;
        }

        summary.TitlesUnique = unique.Count;
        if (unique.Count == 0)
        {
            progress?.Report(100);
            return summary;
        }

        var memoryCache = new ConcurrentDictionary<string, TmdbMetadata>(StringComparer.Ordinal);
        var enrichedCount = 0;
        var errorCount = 0;
        var skippedByHeuristicsCount = 0;
        var memoryCacheHitCount = 0;
        var negativeCacheSkipCount = 0;

        var maxConcurrency = Math.Clamp(cfg.TmdbMaxConcurrency, 1, 24);
        var noMatchTtl = TimeSpan.FromMinutes(Math.Clamp(cfg.TmdbNoMatchTtlMinutes, 5, 1440));

        var rateLimiter = new SlidingWindowTmdbRateLimiter(maxRequests: 40, window: TimeSpan.FromSeconds(10));
        var tmdbClient = new TmdbClient(
            _httpClientFactory.CreateClient("JellyTV"),
            rateLimiter,
            _logger);

        var resultRows = new ConcurrentBag<EnrichBatchEntry>();
        var processed = 0;

        await Parallel.ForEachAsync(
            unique,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
            async (kv, token) =>
            {
                var normalizedTitle = kv.Key;
                var item = kv.Value;
                try
                {
                    if (ShouldSkip(item.Title))
                    {
                        Interlocked.Increment(ref skippedByHeuristicsCount);
                        return;
                    }

                    if (memoryCache.TryGetValue(normalizedTitle, out var cached))
                    {
                        Interlocked.Increment(ref memoryCacheHitCount);
                        if (cached.IsMatch)
                        {
                            resultRows.Add(ToBatchEntry(normalizedTitle, cached));
                            Interlocked.Increment(ref enrichedCount);
                        }
                        return;
                    }

                    if (_negativeCache.ShouldSkip(normalizedTitle, DateTimeOffset.UtcNow))
                    {
                        Interlocked.Increment(ref negativeCacheSkipCount);
                        memoryCache[normalizedTitle] = TmdbMetadata.NoMatch;
                        return;
                    }

                    var meta = await LookupTmdbAsync(item, tmdbKey, tmdbClient, token).ConfigureAwait(false);
                    memoryCache[normalizedTitle] = meta;

                    if (!meta.IsMatch)
                    {
                        _negativeCache.MarkNoMatch(normalizedTitle, DateTimeOffset.UtcNow, noMatchTtl);
                        return;
                    }

                    resultRows.Add(ToBatchEntry(normalizedTitle, meta));
                    Interlocked.Increment(ref enrichedCount);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[JellyTV] TmdbEnrich: failed for title '{Title}'", item.Title);
                    Interlocked.Increment(ref errorCount);
                }
                finally
                {
                    var done = Interlocked.Increment(ref processed);
                    progress?.Report(10.0 + (done / (double)unique.Count) * 80.0);
                }
            }).ConfigureAwait(false);

        summary.Enriched = enrichedCount;
        summary.Errors = errorCount;
        summary.SkippedByHeuristics = skippedByHeuristicsCount;
        summary.MemoryCacheHits = memoryCacheHitCount;
        summary.NegativeCacheSkips = negativeCacheSkipCount;

        var batches = resultRows
            .Where(r => r.TmdbId.HasValue)
            .DistinctBy(r => r.TitleNormalized)
            .Chunk(BatchChunkSize)
            .ToList();

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                summary.Saved += await PushEnrichBatchAsync(workerUrl, apiKey, batch, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[JellyTV] TmdbEnrich: failed to push batch of {Count}", batch.Length);
                summary.PushErrors += batch.Length;
            }
        }

        summary.TmdbRequests = tmdbClient.Metrics.Requests;
        summary.RetryCount = tmdbClient.Metrics.Retries;
        summary.RateLimitWaits = rateLimiter.WaitEvents;

        sw.Stop();
        var hitRate = summary.TitlesUnique == 0 ? 0 : (double)summary.Enriched / summary.TitlesUnique;
        var missRate = 1 - hitRate;
        var avgLatency = summary.TmdbRequests == 0 ? 0 : sw.Elapsed.TotalMilliseconds / summary.TmdbRequests;

        _logger.LogInformation(
            "[JellyTV] TmdbEnrich done: found={Found} unique={Unique} enriched={Enriched} saved={Saved} errors={Errors} pushErrors={PushErrors} hitRate={HitRate:P1} missRate={MissRate:P1} tmdbReq={TmdbReq} retries={Retries} rlWaits={RlWaits} avgReqMs={AvgReqMs:F1} elapsedMs={Elapsed}",
            summary.TitlesFound,
            summary.TitlesUnique,
            summary.Enriched,
            summary.Saved,
            summary.Errors,
            summary.PushErrors,
            hitRate,
            missRate,
            summary.TmdbRequests,
            summary.RetryCount,
            summary.RateLimitWaits,
            avgLatency,
            sw.ElapsedMilliseconds);

        progress?.Report(100);
        return summary;
    }

    private async Task<TmdbMetadata> LookupTmdbAsync(
        UncachedProgrammeItem item,
        string apiKey,
        ITmdbClient client,
        CancellationToken ct)
    {
        var queryVariants = BuildQueryVariants(item).Take(8).ToArray();
        CandidateScore? best = null;
        string bestLanguage = SearchLanguages[0];

        foreach (var query in queryVariants)
        {
            foreach (var language in SearchLanguages)
            {
                ct.ThrowIfCancellationRequested();

                var search = await client.SearchMultiAsync(apiKey, query, language, ct).ConfigureAwait(false);
                var results = search?.Results ?? [];
                if (results.Count == 0) continue;

                var candidate = _matchEvaluator.SelectBest(item, results, PreferredOriginalLanguages);
                if (candidate == null) continue;

                if (best == null || candidate.Score > best.Score)
                {
                    best = candidate;
                    bestLanguage = language;
                }
            }
        }

        if (best == null)
        {
            _logger.LogDebug("[JellyTV] TmdbEnrich no match title='{Title}'", item.Title);
            return TmdbMetadata.NoMatch;
        }

        var mediaType = best.Candidate.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType)) return TmdbMetadata.NoMatch;

        var detail = await client.GetDetailAsync(apiKey, mediaType, best.Candidate.Id, bestLanguage, ct).ConfigureAwait(false);
        var mapped = _metadataMapper.Map(best.Candidate, detail);

        _logger.LogDebug(
            "[JellyTV] TmdbEnrich matched title='{Title}' -> id={TmdbId} type={Type} score={Score:F3} ({Breakdown})",
            item.Title,
            mapped.TmdbId,
            mapped.MediaType,
            best.Score,
            best.Breakdown);

        return mapped;
    }

    private IEnumerable<string> BuildQueryVariants(UncachedProgrammeItem item)
    {
        var title = item.Title.Trim();
        var normalized = _normalizer.NormalizeTitle(title);

        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var trimmed = v.Trim();
            if (trimmed.Length < 2) return;
            variants.Add(trimmed);
        }

        Add(title);
        Add(normalized);

        if (item.Year.HasValue)
        {
            Add($"{title} {item.Year.Value}");
        }

        var clean = Regex.Replace(title, "[\\[\\]\\(\\)\\{\\}]", " ").Trim();
        Add(clean);

        if (!string.IsNullOrWhiteSpace(item.Category))
        {
            Add($"{title} {item.Category}");
        }

        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            var keywords = ExtractDescriptionKeywords(item.Description, limit: 6);
            if (keywords.Length > 0)
            {
                Add($"{title} {string.Join(' ', keywords)}");
            }

            var year = ExtractYear(item.Description);
            if (year.HasValue)
            {
                Add($"{title} {year.Value}");
            }
        }

        // Add the secondary language title (e.g. Spanish when primary is English)
        // as additional search variants — critical for titles only known in one language.
        if (!string.IsNullOrWhiteSpace(item.FallbackTitle))
        {
            Add(item.FallbackTitle);
            Add(_normalizer.NormalizeTitle(item.FallbackTitle));
            if (item.Year.HasValue)
                Add($"{item.FallbackTitle} {item.Year.Value}");
        }

        return variants;
    }

    private static string[] ExtractDescriptionKeywords(string description, int limit)
    {
        return description
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '"', '\'', '!', '?', '(', ')', '[', ']'))
            .Where(w => w.Length >= 4)
            .Where(w => !StopWords.Contains(w.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private static readonly HashSet<string> StopWords =
    [
        "the", "with", "from", "that", "this", "para", "como", "sobre", "entre", "donde", "cuando", "programa", "episodio", "capitulo"
    ];

    private static int? ExtractYear(string text)
    {
        var m = Regex.Match(text, @"\b(19|20)\d{2}\b");
        if (!m.Success) return null;
        return int.TryParse(m.Value, out var y) ? y : null;
    }

    private static bool ShouldSkip(string title)
    {
        var t = title.Trim();
        if (t.Length < 3) return true;
        if (TimestampLikeRe.IsMatch(t)) return true;
        if (SkipRe.IsMatch(t)) return true;

        if (ChannelLikeRe.IsMatch(t) && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2)
        {
            return true;
        }

        return false;
    }

    private EnrichBatchEntry ToBatchEntry(string normalizedTitle, TmdbMetadata meta)
    {
        return new EnrichBatchEntry
        {
            TitleNormalized = normalizedTitle,
            TmdbId = meta.TmdbId,
            MediaType = meta.MediaType,
            Overview = meta.Overview,
            PosterPath = meta.PosterPath,
            BackdropPath = meta.BackdropPath,
            LogoPath = meta.LogoPath,
            Genres = meta.Genres,
            Cast = meta.Cast,
            OriginCountry = meta.OriginCountry,
            ReleaseYear = meta.ReleaseYear,
            Tagline = meta.Tagline,
            ImdbId = meta.ImdbId,
            Runtime = meta.Runtime,
            VoteAverage = meta.VoteAverage,
            VoteCount = meta.VoteCount,
            Popularity = meta.Popularity,
            OriginalLanguage = meta.OriginalLanguage,
            ProductionCompanies = meta.ProductionCompanies,
            ReleaseDate = meta.ReleaseDate,
            Trailers = meta.Trailers,
            Certifications = meta.Certifications,
            NumberOfSeasons = meta.NumberOfSeasons,
            NumberOfEpisodes = meta.NumberOfEpisodes,
            Networks = meta.Networks,
            Status = meta.Status,
            EpisodeRuntimes = meta.EpisodeRuntimes,
            CreatedBy = meta.CreatedBy,
            ExternalIds = meta.ExternalIds,
            Translations = meta.Translations,
            AlternativeTitles = meta.AlternativeTitles,
            Keywords = meta.Keywords
        };
    }

    private async Task<List<UncachedProgrammeItem>> GetUncachedProgrammesAsync(
        string workerUrl,
        string apiKey,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        var url = $"{workerUrl}/api/epg/uncached-titles?limit={UncachedTitlesLimit}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-api-key", apiKey);

        try
        {
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[JellyTV] TmdbEnrich: uncached-titles returned {Status}", (int)resp.StatusCode);
                return [];
            }

            var body = await resp.Content.ReadFromJsonAsync<UncachedTitlesResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (body?.Data == null) return [];

            // Backward-compatible: old Worker returns titles[], new Worker may return programmes[].
            if (body.Data.Programmes?.Count > 0)
            {
                return body.Data.Programmes
                    .Where(x => !string.IsNullOrWhiteSpace(x.Title))
                    .Select(x => new UncachedProgrammeItem(x.Title!, x.Description, x.Category, x.Year))
                    .ToList();
            }

            return (body.Data.Titles ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => new UncachedProgrammeItem(x.Trim()))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[JellyTV] TmdbEnrich: failed to fetch uncached titles");
            return [];
        }
    }

    private async Task<int> PushEnrichBatchAsync(
        string workerUrl,
        string apiKey,
        IEnumerable<EnrichBatchEntry> entries,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        var url = $"{workerUrl}/api/epg/enrich-batch";
        var payload = JsonSerializer.Serialize(
            new { entries },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-api-key", apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<EnrichBatchResponse>(cancellationToken: ct).ConfigureAwait(false);
        return body?.Data?.Saved ?? 0;
    }

    // ── Inline EPG enrichment API ──────────────────────────────────────────────

    /// <summary>
    /// Creates a run-scoped context that holds shared state (rate limiter, memory cache,
    /// TMDB client) across multiple calls to <see cref="EnrichProgrammesAsync"/>.
    /// Create once per sync run and reuse for every EPG source processed in that run.
    /// </summary>
    internal TmdbEnrichmentRunContext CreateRunContext()
    {
        var cfg = Plugin.Instance?.Configuration;
        return new TmdbEnrichmentRunContext(
            tmdbApiKey:     cfg?.TmdbApiKey?.Trim() ?? string.Empty,
            httpClient:     _httpClientFactory.CreateClient("JellyTV"),
            maxConcurrency: Math.Clamp(cfg?.TmdbMaxConcurrency ?? 6, 1, 24),
            noMatchTtl:     TimeSpan.FromMinutes(Math.Clamp(cfg?.TmdbNoMatchTtlMinutes ?? 180, 5, 1440)),
            logger:         _logger);
    }

    /// <summary>
    /// Enriches a list of EPG programmes with TMDB metadata using the rich inline data
    /// (English title, Spanish title, description, year, category) extracted during XMLTV filtering.
    /// Returns the enrichment stats and the batch entries ready to push to the Worker.
    /// </summary>
    internal async Task<(EpgEnrichmentStats Stats, List<EnrichBatchEntry> Entries)> EnrichProgrammesAsync(
        IReadOnlyList<EpgProgrammeData> programmes,
        TmdbEnrichmentRunContext ctx,
        CancellationToken ct)
    {
        var stats = new EpgEnrichmentStats { ProgrammesInput = programmes.Count };
        if (programmes.Count == 0) return (stats, []);

        // Deduplicate by normalized BestTitle (English preferred for better TMDB coverage).
        var unique = new Dictionary<string, UncachedProgrammeItem>(StringComparer.Ordinal);
        foreach (var p in programmes)
        {
            var title = p.BestTitle;
            if (string.IsNullOrWhiteSpace(title)) continue;
            var normalized = _normalizer.NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            unique.TryAdd(normalized, new UncachedProgrammeItem(
                title, p.Description, p.Category, p.Year, p.FallbackTitle));
        }

        stats.UniqueAfterDedup = unique.Count;
        if (unique.Count == 0) return (stats, []);

        var resultRows = new ConcurrentBag<EnrichBatchEntry>();
        int enrichedCount = 0, skippedCount = 0, memoryCacheHitCount = 0;
        int negativeCacheSkipCount = 0, noMatchCount = 0, errorCount = 0;

        await Parallel.ForEachAsync(
            unique,
            new ParallelOptions { MaxDegreeOfParallelism = ctx.MaxConcurrency, CancellationToken = ct },
            async (kv, token) =>
            {
                var normalizedTitle = kv.Key;
                var item = kv.Value;
                try
                {
                    if (ShouldSkip(item.Title))
                    {
                        Interlocked.Increment(ref skippedCount);
                        return;
                    }

                    if (ctx.MemoryCache.TryGetValue(normalizedTitle, out var cached))
                    {
                        Interlocked.Increment(ref memoryCacheHitCount);
                        if (cached.IsMatch)
                        {
                            resultRows.Add(ToBatchEntry(normalizedTitle, cached));
                            Interlocked.Increment(ref enrichedCount);
                        }
                        return;
                    }

                    if (_negativeCache.ShouldSkip(normalizedTitle, DateTimeOffset.UtcNow))
                    {
                        Interlocked.Increment(ref negativeCacheSkipCount);
                        ctx.MemoryCache[normalizedTitle] = TmdbMetadata.NoMatch;
                        return;
                    }

                    var meta = await LookupTmdbAsync(item, ctx.TmdbApiKey, ctx.TmdbClient, token).ConfigureAwait(false);
                    ctx.MemoryCache[normalizedTitle] = meta;

                    if (!meta.IsMatch)
                    {
                        _negativeCache.MarkNoMatch(normalizedTitle, DateTimeOffset.UtcNow, ctx.NoMatchTtl);
                        Interlocked.Increment(ref noMatchCount);
                        return;
                    }

                    resultRows.Add(ToBatchEntry(normalizedTitle, meta));
                    Interlocked.Increment(ref enrichedCount);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[JellyTV] TmdbEnrich: error enriching '{Title}'", item.Title);
                    Interlocked.Increment(ref errorCount);
                }
            }).ConfigureAwait(false);

        stats.Enriched            = enrichedCount;
        stats.SkippedByHeuristics = skippedCount;
        stats.MemoryCacheHits     = memoryCacheHitCount;
        stats.NegativeCacheSkips  = negativeCacheSkipCount;
        stats.NoMatch             = noMatchCount;
        stats.Errors              = errorCount;
        stats.TmdbRequests        = ctx.TmdbClient.Metrics.Requests;
        stats.Retries             = ctx.TmdbClient.Metrics.Retries;

        var entries = resultRows
            .Where(r => r.TmdbId.HasValue)
            .DistinctBy(r => r.TitleNormalized)
            .ToList();

        return (stats, entries);
    }

    /// <summary>
    /// Pushes a batch of enrichment entries to the Worker's /api/epg/enrich-batch endpoint.
    /// Accessible from <see cref="EpgFilterService"/> for inline pushes after each EPG source.
    /// </summary>
    internal Task<int> PushEnrichBatchToWorkerAsync(
        string workerUrl, string? apiKey, IEnumerable<EnrichBatchEntry> entries, CancellationToken ct)
        => PushEnrichBatchAsync(workerUrl, apiKey ?? string.Empty, entries, ct);
}

internal sealed class UncachedTitlesResponse
{
    public UncachedTitlesData? Data { get; set; }
}

internal sealed class UncachedTitlesData
{
    public List<string>? Titles { get; set; }
    public List<UncachedProgrammeDto>? Programmes { get; set; }
}

internal sealed class UncachedProgrammeDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public int? Year { get; set; }
}

// ── Run-scoped shared state for inline enrichment ─────────────────────────────

/// <summary>
/// Holds per-run shared resources (rate limiter, memory cache, TMDB client) so that
/// all EPG sources processed during a single sync run share the same sliding-window
/// rate limit and avoid redundant TMDB lookups for titles appearing in multiple sources.
/// Create once via <see cref="TmdbEnrichmentService.CreateRunContext"/> and pass to
/// every <see cref="TmdbEnrichmentService.EnrichProgrammesAsync"/> call in the run.
/// </summary>
internal sealed class TmdbEnrichmentRunContext
{
    internal ConcurrentDictionary<string, TmdbMetadata> MemoryCache { get; }
        = new(StringComparer.Ordinal);

    internal SlidingWindowTmdbRateLimiter RateLimiter { get; }
    internal ITmdbClient TmdbClient { get; }
    internal int MaxConcurrency { get; }
    internal TimeSpan NoMatchTtl { get; }
    internal string TmdbApiKey { get; }

    internal TmdbEnrichmentRunContext(
        string tmdbApiKey,
        HttpClient httpClient,
        int maxConcurrency,
        TimeSpan noMatchTtl,
        ILogger logger)
    {
        TmdbApiKey     = tmdbApiKey;
        MaxConcurrency = maxConcurrency;
        NoMatchTtl     = noMatchTtl;
        RateLimiter    = new SlidingWindowTmdbRateLimiter(maxRequests: 40, window: TimeSpan.FromSeconds(10));
        TmdbClient     = new TmdbClient(httpClient, RateLimiter, logger);
    }
}

internal sealed class EnrichBatchResponse
{
    public EnrichBatchData? Data { get; set; }
}

internal sealed class EnrichBatchData
{
    public int Saved { get; set; }
}
