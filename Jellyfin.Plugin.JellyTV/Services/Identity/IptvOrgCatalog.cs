using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services.Identity;

/// <summary>
/// The iptv-org channel database (public domain, https://github.com/iptv-org/database), downloaded as three JSON
/// files, (channels, logos, guides) reduced to what the plugin needs, cached on disk for a week and indexed by name tokens.
/// </summary>
internal sealed class IptvOrgCatalog : IDisposable
{
    private const string ChannelsUrl = "https://iptv-org.github.io/api/channels.json";
    private const string LogosUrl = "https://iptv-org.github.io/api/logos.json";
    private const string GuidesUrl = "https://iptv-org.github.io/api/guides.json";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions DiskJson = new() { WriteIndented = false };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Index? _index;
    private DateTimeOffset _loadedAt;
    private DateTimeOffset _failedAt;

    public IptvOrgCatalog(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Channels that share at least one name token with <paramref name="query"/>, or the one whose id is
    /// <paramref name="tvgId"/>.
    /// </summary>
    /// <param name="query">Channel name.</param>
    /// <param name="tvgId">Optional guide id, compared with the database id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Possible channels (empty when the database is unavailable).</returns>
    internal async Task<IReadOnlyList<IptvOrgChannel>> FindAsync(string query, string? tvgId, CancellationToken ct)
    {
        var index = await GetIndexAsync(ct).ConfigureAwait(false);
        if (index is null)
        {
            return Array.Empty<IptvOrgChannel>();
        }

        var hits = new HashSet<IptvOrgChannel>();
        var id = (tvgId ?? string.Empty).Split('@')[0].Trim();
        if (id.Length > 0 && index.ById.TryGetValue(id, out var byId))
        {
            hits.Add(byId);
        }

        foreach (var token in NameMatcher.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (index.ByToken.TryGetValue(token, out var list))
            {
                hits.UnionWith(list);
            }
        }

        return hits.ToList();
    }

    /// <summary>
    /// The channel with that database id.
    /// </summary>
    /// <param name="id">iptv-org id (a trailing <c>@feed</c> is ignored).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The channel, or null when unknown or the database is unavailable.</returns>
    internal async Task<IptvOrgChannel?> GetByIdAsync(string id, CancellationToken ct)
    {
        var index = await GetIndexAsync(ct).ConfigureAwait(false);
        return index is not null && index.ById.TryGetValue(id.Split('@')[0].Trim(), out var channel) ? channel : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    // Jellyfin dibuja PNG/JPEG/WebP; un logo vigente y sin feed concreto es el más general.
    private static int LogoRank(JsonElement e)
    {
        var rank = 0;
        rank += e.TryGetProperty("in_use", out var u) && u.ValueKind == JsonValueKind.True ? 100 : 0;
        rank += e.TryGetProperty("feed", out var f) && f.ValueKind == JsonValueKind.Null ? 50 : 0;
        rank += Str(e, "format")?.ToUpperInvariant() is "PNG" or "JPEG" or "WEBP" ? 25 : 0;
        rank += e.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() >= 200 ? 10 : 0;
        return rank;
    }

    private async Task<Index?> GetIndexAsync(CancellationToken ct)
    {
        if (_index is not null && DateTimeOffset.UtcNow - _loadedAt < Lifetime)
        {
            return _index;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_index is not null && DateTimeOffset.UtcNow - _loadedAt < Lifetime)
            {
                return _index;
            }

            if (DateTimeOffset.UtcNow - _failedAt < FailureCooldown)
            {
                return _index;
            }

            var list = LoadFromDisk();
            if (list is null)
            {
                try
                {
                    list = await DownloadAsync(ct).ConfigureAwait(false);
                    SaveToDisk(list);
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("[JellyTV] iptv-org database loaded: {Count} channels", list.Count);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _failedAt = DateTimeOffset.UtcNow;
                    _logger.LogWarning("[JellyTV] iptv-org database unavailable: {Message}", ex.Message);
                    return _index; // una copia vieja en memoria sigue sirviendo
                }
            }

            _index = Build(list);
            _loadedAt = DateTimeOffset.UtcNow;
            return _index;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Index Build(List<IptvOrgChannel> list)
    {
        var byId = new Dictionary<string, IptvOrgChannel>(StringComparer.OrdinalIgnoreCase);
        var byToken = new Dictionary<string, List<IptvOrgChannel>>(StringComparer.Ordinal);
        foreach (var channel in list)
        {
            byId[channel.Id] = channel;
            var tokens = channel.AltNames.Append(channel.Name)
                .SelectMany(n => NameMatcher.Normalize(n).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                if (!byToken.TryGetValue(token, out var bucket))
                {
                    bucket = new List<IptvOrgChannel>();
                    byToken[token] = bucket;
                }

                bucket.Add(channel);
            }
        }

        return new Index(byId, byToken);
    }

    private async Task<List<IptvOrgChannel>> DownloadAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("JellyTV");
        using var logosDoc = await GetJsonAsync(client, LogosUrl, ct).ConfigureAwait(false);
        var bestLogo = new Dictionary<string, (int Rank, string Url)>(StringComparer.OrdinalIgnoreCase);
        foreach (var logo in logosDoc.RootElement.EnumerateArray())
        {
            var channel = Str(logo, "channel");
            var url = Str(logo, "url");
            if (channel is null || url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rank = LogoRank(logo);
            if (!bestLogo.TryGetValue(channel, out var current) || rank > current.Rank)
            {
                bestLogo[channel] = (rank, url);
            }
        }

        // Qué canales tienen página de canal en GatoTV (la única web de guías que el plugin sabe leer por sí mismo).
        var gatoTv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var guidesDoc = await GetJsonAsync(client, GuidesUrl, ct).ConfigureAwait(false))
        {
            foreach (var g in guidesDoc.RootElement.EnumerateArray())
            {
                var channel = Str(g, "channel");
                var siteId = Str(g, "site_id");
                if (channel is not null && siteId is not null && string.Equals(Str(g, "site"), "gatotv.com", StringComparison.OrdinalIgnoreCase))
                {
                    gatoTv.TryAdd(channel, siteId);
                }
            }
        }

        using var channelsDoc = await GetJsonAsync(client, ChannelsUrl, ct).ConfigureAwait(false);
        var result = new List<IptvOrgChannel>();
        foreach (var e in channelsDoc.RootElement.EnumerateArray())
        {
            // Los canales cerrados y para adultos no sirven como identidad.
            if ((e.TryGetProperty("closed", out var closed) && closed.ValueKind == JsonValueKind.String)
                || (e.TryGetProperty("is_nsfw", out var nsfw) && nsfw.ValueKind == JsonValueKind.True))
            {
                continue;
            }

            var id = Str(e, "id");
            var name = Str(e, "name");
            if (id is null || name is null)
            {
                continue;
            }

            var alts = e.TryGetProperty("alt_names", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
            result.Add(new IptvOrgChannel(id, name, alts, Str(e, "country"), Str(e, "website"), bestLogo.TryGetValue(id, out var l) ? l.Url : null, gatoTv.GetValueOrDefault(id)));
        }

        return result;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private static string CachePath()
    {
        // Es una copia desechable (se vuelve a bajar sola): va en la caché de Jellyfin, no en los datos del plugin.
        var dir = Plugin.Instance?.CachePath;
        return string.IsNullOrWhiteSpace(dir) ? string.Empty : Path.Combine(dir, "JellyTV", "iptvorg.json");
    }

    private static List<IptvOrgChannel>? LoadFromDisk()
    {
        RemoveObsoleteFiles();
        try
        {
            var path = CachePath();
            if (path.Length == 0 || !File.Exists(path) || DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) > Lifetime)
            {
                return null;
            }

            var list = JsonSerializer.Deserialize<List<IptvOrgChannel>>(File.ReadAllText(path), DiskJson);
            return list is { Count: > 0 } ? list : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Las copias de versiones anteriores (iptv-org y el directorio de TVMaze) vivían en los datos del plugin; ya no se usan.
    private static void RemoveObsoleteFiles()
    {
        try
        {
            var dir = Path.Combine(Plugin.Instance?.DataDirectory ?? string.Empty, "identity");
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Limpieza opcional: si no se puede borrar no pasa nada.
        }
    }

    private void SaveToDisk(List<IptvOrgChannel> list)
    {
        try
        {
            var path = CachePath();
            if (path.Length == 0 || list.Count == 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(list, DiskJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("[JellyTV] Could not cache the iptv-org database: {Message}", ex.Message);
        }
    }

    private sealed record Index(Dictionary<string, IptvOrgChannel> ById, Dictionary<string, List<IptvOrgChannel>> ByToken);
}
