using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// The public per-country XMLTV files that the automatic guide downloads for the countries of the channels. The main
/// provider is iptv-epg.org (its channel ids follow the same <c>Name.cc</c> scheme as iptv-org); a country it does not
/// publish falls back to the epgshare01.online files. The list of files is read from each host's index, never hardcoded.
/// </summary>
internal sealed class AutoGuideCatalog
{
    private const string MainBase = "https://iptv-epg.org/";
    private const string FallbackBase = "https://epgshare01.online/epgshare01/";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    // files/epg-mx.xml.gz: un archivo por país (código ISO en minúsculas).
    private static readonly Regex MainPattern = new(
        "files/(?<file>epg-(?<cc>[a-z]{2})\\.xml\\.gz)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    // epg_ripper_MX1.xml.gz, epg_ripper_US2.xml.gz, epg_ripper_US_SPORTS1.xml.gz ...: país, y de deportes si lo indica.
    private static readonly Regex FallbackPattern = new(
        "href=\"(?<file>epg_ripper_(?<cc>[A-Z]{2})(?:_SPORTS)?\\d*\\.xml\\.gz)\"",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private readonly IHttpClientFactory _httpClientFactory;
    private Dictionary<string, List<string>> _main = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _fallback = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt;
    private DateTimeOffset _failedAt;

    public AutoGuideCatalog(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    /// <summary>
    /// XMLTV files that cover the given countries.
    /// </summary>
    /// <param name="countries">ISO 3166-1 country codes of the channels.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute URLs (empty when the hosts are unreachable and nothing was cached).</returns>
    public async Task<IReadOnlyList<string>> UrlsForAsync(IEnumerable<string> countries, CancellationToken ct)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        var urls = new List<string>();
        foreach (var country in countries.Where(static c => !string.IsNullOrWhiteSpace(c)).Select(static c => c.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal))
        {
            if (_main.TryGetValue(country, out var files))
            {
                urls.AddRange(files.Select(static f => MainBase + "files/" + f));
            }
            else if (_fallback.TryGetValue(country == "GB" ? "UK" : country, out var spare))
            {
                // El índice de respaldo usa UK donde ISO dice GB.
                urls.AddRange(spare.Select(static f => FallbackBase + f));
            }
        }

        return urls.Distinct(StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, List<string>> Parse(string html, Regex pattern)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in pattern.Matches(html))
        {
            var cc = match.Groups["cc"].Value;
            if (!map.TryGetValue(cc, out var list))
            {
                list = new List<string>();
                map[cc] = list;
            }

            if (!list.Contains(match.Groups["file"].Value))
            {
                list.Add(match.Groups["file"].Value);
            }
        }

        return map;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if ((_main.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < Lifetime) || DateTimeOffset.UtcNow - _failedAt < FailureCooldown)
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("JellyTV");
            var main = Parse(await client.GetStringAsync(MainBase, ct).ConfigureAwait(false), MainPattern);
            var fallback = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                fallback = Parse(await client.GetStringAsync(FallbackBase, ct).ConfigureAwait(false), FallbackPattern);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                PluginLog.Warn($"[EPG] El índice de respaldo de guías automáticas no responde: {ex.Message}");
            }

            if (main.Count > 0)
            {
                _main = main;
                _fallback = fallback;
                _loadedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _failedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _failedAt = DateTimeOffset.UtcNow;
            PluginLog.Warn($"[EPG] No se pudo leer el índice de guías automáticas: {ex.Message}");
        }
    }
}
