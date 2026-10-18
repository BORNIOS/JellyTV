using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Enriquecimiento TMDB 100% local y con contexto: usa el tipo de canal (sports/news/kids/movies)
/// para no reclasificar programas (un debate en un canal deportivo no debe volverse "movie"), evita
/// placeholders del provider y solo inyecta arte/metadata TMDB en canales generales/cine y cuando el
/// programa no parece una transmisión en vivo. Además garantiza la categoría del canal (p. ej. sports)
/// cuando el provider no la define.
/// </summary>
public sealed class TmdbEnrichmentService
{
    private static readonly string[] SearchLanguages = ["es-MX", "es-ES", "en-US"];
    private static readonly string[] PreferredOriginalLanguages = ["es", "en", "pt"];

    // Saltar contenido que suele ser ruido no cinematográfico en la EPG en vivo.
    private static readonly Regex SkipRe = new(
        @"\b(test\s*card|publicidad|anuncios?|infomercial|paid\s*programming|shopping|teleshopping|sign\s*off|weather|local\s*news|promo|trailer\s*loop|filler|sin\s*informacion|tba|to\s*be\s*announced|programa\s*por\s*definir)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimestampLikeRe = new(
        @"^\s*(\d{1,2}:\d{2}(\s*[ap]\.??m\.?)?|\d{1,2}/\d{1,2}(/\d{2,4})?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChannelLikeRe = new(
        @"\b(hd|uhd|4k|canal|channel|tv)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords =
    [
        "the", "with", "from", "that", "this", "para", "como", "sobre", "entre", "donde", "cuando", "programa", "episodio", "capitulo"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbEnrichmentService> _logger;

    private readonly TmdbNormalizationService _normalizer;
    private readonly Tmdb.MatchEvaluator _matchEvaluator;
    private readonly NegativeCacheService _negativeCache;
    private readonly TmdbMetadataMapper _metadataMapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbEnrichmentService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Fábrica de clientes HTTP.</param>
    /// <param name="logger">Logger.</param>
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

    /// <summary>
    /// Creates a run-scoped context that holds shared state (rate limiter, memory cache,
    /// TMDB client) across all EPG sources processed in a single sync run.
    /// </summary>
    /// <returns>Contexto del run.</returns>
    internal TmdbEnrichmentRunContext CreateRunContext()
    {
        var cfg = Plugin.Instance?.Configuration;
        return new TmdbEnrichmentRunContext(
            tmdbApiKey: cfg?.TmdbApiKey?.Trim() ?? string.Empty,
            httpClient: _httpClientFactory.CreateClient("JellyTV"),
            maxConcurrency: Math.Clamp(cfg?.TmdbMaxConcurrency ?? 6, 1, 24),
            noMatchTtl: TimeSpan.FromMinutes(Math.Clamp(cfg?.TmdbNoMatchTtlMinutes ?? 180, 5, 1440)),
            logger: _logger);
    }

    /// <summary>
    /// Procesa los programas filtrados de una fuente:
    /// 1. Normaliza la categoría del canal (sports/news/kids/movies) cuando el provider no la define.
    /// 2. Solo en canales generales/cine, consulta TMDB y, si el match es seguro y el programa no
    ///    parece una transmisión en vivo, inyecta arte y metadata.
    /// Los placeholders (No Data / ayuda del provider) nunca se tocan.
    /// </summary>
    /// <param name="records">Programas filtrados (XML editable + contexto de canal).</param>
    /// <param name="ctx">Contexto del run.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Estadísticas del enriquecimiento.</returns>
    internal async Task<EpgEnrichmentStats> EnrichXmlProgrammesAsync(
        IReadOnlyList<XmlProgrammeRecord> records,
        TmdbEnrichmentRunContext ctx,
        CancellationToken ct)
    {
        var stats = new EpgEnrichmentStats { ProgrammesInput = records.Count };
        if (records.Count == 0)
        {
            return stats;
        }

        // Agrupar por título normalizado (sin placeholders) para no repetir lookups por re-emisiones.
        var groups = new Dictionary<string, List<XmlProgrammeRecord>>(StringComparer.Ordinal);
        foreach (var rec in records)
        {
            var title = rec.Data.BestTitle;
            if (string.IsNullOrWhiteSpace(title)
                || EpgClassifier.IsPlaceholder(title, rec.Data.Description))
            {
                continue;
            }

            var normalized = _normalizer.NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!groups.TryGetValue(normalized, out var list))
            {
                list = new List<XmlProgrammeRecord>();
                groups.Add(normalized, list);
            }

            list.Add(rec);
        }

        stats.UniqueAfterDedup = groups.Count;
        if (groups.Count == 0)
        {
            return stats;
        }

        int enrichedCount = 0, skippedCount = 0, memoryCacheHitCount = 0;
        int negativeCacheSkipCount = 0, noMatchCount = 0, errorCount = 0;

        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions { MaxDegreeOfParallelism = ctx.MaxConcurrency, CancellationToken = ct },
            async (kv, token) =>
            {
                var normalizedTitle = kv.Key;
                var recList = kv.Value;
                var item = ToItem(recList[0].Data);
                try
                {
                    if (ShouldSkip(item.Title))
                    {
                        Interlocked.Increment(ref skippedCount);
                        return;
                    }

                    // TMDB solo aporta valor en canales generales/cine; en deportivos/noticias/kids
                    // el riesgo de reclasificar (p. ej. "movie/Terror" en un debate) es demasiado alto.
                    var representative = recList[0];
                    var wantsTmdb = representative.Kind is ChannelKind.General or ChannelKind.Movies;

                    var meta = TmdbMetadata.NoMatch;
                    if (wantsTmdb)
                    {
                        if (ctx.MemoryCache.TryGetValue(normalizedTitle, out var cached))
                        {
                            Interlocked.Increment(ref memoryCacheHitCount);
                            meta = cached;
                        }
                        else if (_negativeCache.ShouldSkip(normalizedTitle, DateTimeOffset.UtcNow))
                        {
                            Interlocked.Increment(ref negativeCacheSkipCount);
                            meta = TmdbMetadata.NoMatch;
                            ctx.MemoryCache[normalizedTitle] = meta;
                        }
                        else
                        {
                            meta = await LookupTmdbAsync(item, ctx.TmdbApiKey, ctx.TmdbClient, token).ConfigureAwait(false);
                            ctx.MemoryCache[normalizedTitle] = meta;
                            if (!meta.IsMatch)
                            {
                                _negativeCache.MarkNoMatch(normalizedTitle, DateTimeOffset.UtcNow, ctx.NoMatchTtl);
                                Interlocked.Increment(ref noMatchCount);
                            }
                        }
                    }

                    var effectiveMeta = wantsTmdb && meta.IsMatch ? meta : null;

                    foreach (var rec in recList)
                    {
                        var rewritten = RewriteProgrammeFragment(rec, effectiveMeta);
                        if (rewritten is not null)
                        {
                            rec.Xml = rewritten;
                            Interlocked.Increment(ref enrichedCount);
                        }
                    }
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

        stats.Enriched = enrichedCount;
        stats.SkippedByHeuristics = skippedCount;
        stats.MemoryCacheHits = memoryCacheHitCount;
        stats.NegativeCacheSkips = negativeCacheSkipCount;
        stats.NoMatch = noMatchCount;
        stats.Errors = errorCount;
        stats.TmdbRequests = ctx.TmdbClient.Metrics.Requests;
        stats.Retries = ctx.TmdbClient.Metrics.Retries;

        _logger.LogInformation(
            "[JellyTV] TmdbEnrich done: programmes={Programmes} unique={Unique} enriched={Enriched} errors={Errors} tmdbReq={TmdbReq} retries={Retries}",
            stats.ProgrammesInput,
            stats.UniqueAfterDedup,
            stats.Enriched,
            stats.Errors,
            stats.TmdbRequests,
            stats.Retries);

        return stats;
    }

    /// <summary>
    /// Reescribe un &lt;programme&gt; según el contexto:
    /// - Añade la categoría del tipo de canal (sports/news/kids/movies) si el provider no la trae.
    /// - Si <paramref name="meta"/> trae un match TMDB válido y el programa no parece una transmisión
    ///   en vivo, inyecta arte (backdrop/poster), descripción corta, categorías y créditos.
    /// Devuelve null si no hay cambios.
    /// </summary>
    /// <param name="rec">Registro del programa (XML + metadatos + contexto).</param>
    /// <param name="meta">Metadata TMDB con match, o null si no aplica.</param>
    /// <returns>XML reescrito o null.</returns>
    private static string? RewriteProgrammeFragment(XmlProgrammeRecord rec, TmdbMetadata? meta)
    {
        var data = rec.Data;

        // Si la fuente ya enriqueció este programa desde TMDB, no lo tocamos.
        if (data.HasTmdbIcon)
        {
            return null;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(rec.Xml, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return null;
        }

        var programme = doc.Root;
        if (programme is null)
        {
            return null;
        }

        var changed = false;

        // 1) Categoría base según el tipo de canal (lo que colorea la guía de Jellyfin).
        var bucket = rec.Kind switch
        {
            ChannelKind.Sports => "sports",
            ChannelKind.News => "news",
            ChannelKind.Kids => "kids",
            ChannelKind.Movies => "movie",
            _ => null
        };

        if (bucket is not null)
        {
            var hasBucket = programme.Elements("category")
                .Any(c => string.Equals(c.Value.Trim(), bucket, StringComparison.OrdinalIgnoreCase));
            if (!hasBucket)
            {
                programme.Add(new XElement("category", bucket));
                changed = true;
            }
        }

        // 2) Enriquecimiento TMDB solo cuando aplica y no parece en vivo/show.
        if (meta is not null
            && meta.IsMatch
            && !EpgClassifier.LooksLikeLiveShow(data.BestTitle, data.Description))
        {
            var imageUrl = BuildImageUrl(meta.BackdropPath, "w1280") ?? BuildImageUrl(meta.PosterPath, "w500");
            if (!string.IsNullOrEmpty(imageUrl))
            {
                programme.Elements("icon").Remove();
                programme.Add(new XElement("icon", new XAttribute("src", imageUrl)));
                changed = true;
            }

            var overview = meta.Overview;
            if (!string.IsNullOrWhiteSpace(overview))
            {
                var descs = programme.Elements("desc").ToList();
                var target = descs.FirstOrDefault(d => string.Equals(
                    (string?)d.Attribute("lang"), "es", StringComparison.OrdinalIgnoreCase)) ?? descs.FirstOrDefault();

                if (target is null)
                {
                    programme.Add(new XElement("desc", new XAttribute("lang", "es"), overview));
                    changed = true;
                }
                else
                {
                    var current = target.Value.Trim();
                    if (current.Length < 40 && overview.Length > current.Length)
                    {
                        target.Value = overview;
                        changed = true;
                    }
                }
            }

            var existingCats = new HashSet<string>(
                programme.Elements("category").Select(c => (c.Value ?? string.Empty).Trim()),
                StringComparer.OrdinalIgnoreCase);

            var mediaTypeCat = meta.MediaType == "movie" ? "movie" : meta.MediaType == "tv" ? "series" : null;
            if (mediaTypeCat is not null && existingCats.Add(mediaTypeCat))
            {
                programme.Add(new XElement("category", mediaTypeCat));
                changed = true;
            }

            foreach (var genre in meta.Genres)
            {
                if (string.IsNullOrWhiteSpace(genre) || existingCats.Count >= 8)
                {
                    break;
                }

                if (existingCats.Add(genre))
                {
                    programme.Add(new XElement("category", genre));
                    changed = true;
                }
            }

            if (programme.Element("credits") is null && meta.Cast.Count > 0)
            {
                var credits = new XElement("credits");
                foreach (var member in meta.Cast)
                {
                    if (string.Equals(member.Role, "director", StringComparison.OrdinalIgnoreCase))
                    {
                        credits.Add(new XElement("director", member.Name));
                    }
                }

                foreach (var member in meta.Cast.Take(6))
                {
                    if (!string.Equals(member.Role, "director", StringComparison.OrdinalIgnoreCase))
                    {
                        credits.Add(new XElement("actor", member.Name));
                    }
                }

                if (credits.HasElements)
                {
                    programme.Add(credits);
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return null;
        }

        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        using (var xw = XmlWriter.Create(sw, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            doc.WriteTo(xw);
        }

        return sw.ToString();
    }

    /// <summary>
    /// Construye la URL completa hacia image.tmdb.org para un path relativo y un ancho dados.
    /// </summary>
    /// <param name="path">Path relativo (p. ej. /abc.jpg) o null.</param>
    /// <param name="width">Ancho TMDB (p. ej. w500, w1280).</param>
    /// <returns>URL completa o null.</returns>
    private static string? BuildImageUrl(string? path, string width)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return "https://image.tmdb.org/t/p/" + width + path;
    }

    /// <summary>
    /// Convierte los metadatos ricos de un programa en el item que consume el buscador TMDB.
    /// </summary>
    /// <param name="p">Metadatos del programa.</param>
    /// <returns>Item de búsqueda.</returns>
    private static UncachedProgrammeItem ToItem(EpgProgrammeData p)
        => new(p.BestTitle, p.Description, p.Category, p.Year, p.FallbackTitle);

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
                if (results.Count == 0)
                {
                    continue;
                }

                var candidate = _matchEvaluator.SelectBest(item, results, PreferredOriginalLanguages);
                if (candidate == null)
                {
                    continue;
                }

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
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return TmdbMetadata.NoMatch;
        }

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

    private HashSet<string> BuildQueryVariants(UncachedProgrammeItem item)
    {
        var title = item.Title.Trim();
        var normalized = _normalizer.NormalizeTitle(title);

        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? v)
        {
            if (string.IsNullOrWhiteSpace(v))
            {
                return;
            }

            var trimmed = v.Trim();
            if (trimmed.Length < 2)
            {
                return;
            }

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

        // Título secundario (p. ej. español cuando el principal es inglés) como variante adicional.
        if (!string.IsNullOrWhiteSpace(item.FallbackTitle))
        {
            Add(item.FallbackTitle);
            Add(_normalizer.NormalizeTitle(item.FallbackTitle));
            if (item.Year.HasValue)
            {
                Add($"{item.FallbackTitle} {item.Year.Value}");
            }
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

    private static int? ExtractYear(string text)
    {
        var m = Regex.Match(text, @"\b(19|20)\d{2}\b");
        if (!m.Success)
        {
            return null;
        }

        return int.TryParse(m.Value, out var y) ? y : null;
    }

    private static bool ShouldSkip(string title)
    {
        var t = title.Trim();
        if (t.Length < 3)
        {
            return true;
        }

        if (TimestampLikeRe.IsMatch(t))
        {
            return true;
        }

        if (SkipRe.IsMatch(t))
        {
            return true;
        }

        if (ChannelLikeRe.IsMatch(t) && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2)
        {
            return true;
        }

        return false;
    }
}
