using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Fetches all configured M3U sources and returns a combined, rewritten playlist
/// where every channel URL goes through /JellyTV/stream so token renewal works.
/// </summary>
public class M3uAggregatorService
{
    // Hardcoded constant (not configurable to avoid interference with token renewal logic)
    private const int REQUEST_TIMEOUT_SECONDS = 30;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<M3uAggregatorService> _logger;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);
    private string _cachedM3u = "#EXTM3U\n";
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public M3uAggregatorService(IHttpClientFactory httpClientFactory, ILogger<M3uAggregatorService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Builds a combined M3U playlist from all enabled sources.
    /// Each channel URL is rewritten to /JellyTV/stream.m3u8?url=&lt;base64&gt;
    /// so the proxy handles token renewal transparently.
    /// </summary>
    public async Task<string> BuildCombinedM3uAsync(string proxyBaseUrl, CancellationToken ct = default, bool forceRefresh = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && _cachedAt != DateTimeOffset.MinValue && (now - _cachedAt) < CacheTtl)
        {
            return _cachedM3u;
        }

        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh && _cachedAt != DateTimeOffset.MinValue && (now - _cachedAt) < CacheTtl)
            {
                return _cachedM3u;
            }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _cachedM3u = "#EXTM3U\n";
            _cachedAt = DateTimeOffset.UtcNow;
            return _cachedM3u;
        }

        var channels = config.Channels?.FindAll(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Url)) ?? new();
        PluginLog.Info($"[M3U] Building combined M3U from {channels.Count} channel(s)");

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var ch in channels)
        {
            var encodedUrl = Convert.ToBase64String(Encoding.UTF8.GetBytes(ch.Url))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            if (!string.IsNullOrWhiteSpace(ch.TvgId))
                sb.AppendLine($"#EXTINF:-1 tvg-id=\"{ch.TvgId}\" tvg-name=\"{ch.TvgName ?? ch.Name}\" tvg-logo=\"{ch.LogoUrl ?? ""}\" group-title=\"{ch.Category ?? ""}\",{ch.Name}");
            else
                sb.AppendLine($"#EXTINF:-1 tvg-name=\"{ch.TvgName ?? ch.Name}\" tvg-logo=\"{ch.LogoUrl ?? ""}\" group-title=\"{ch.Category ?? ""}\",{ch.Name}");

            sb.AppendLine($"{proxyBaseUrl}/stream.m3u8?url={encodedUrl}");
        }

        _cachedM3u = sb.ToString();
        _cachedAt = DateTimeOffset.UtcNow;

        PluginLog.Info($"[M3U] Combined M3U ready: {channels.Count} total channels");
        return _cachedM3u;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>
    /// Parses an M3U playlist and rewrites every stream URL to go through the proxy.
    /// Preserves all #EXTINF metadata lines unchanged.
    /// </summary>
    private (string content, int count) ParseAndRewrite(string m3u, IptvSource source, string proxyBaseUrl)
    {
        var sb = new StringBuilder();
        var lines = m3u.Split('\n');
        var count = 0;
        string? pendingExtInf = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                continue; // skip the header; we already wrote one

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pendingExtInf = line;
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
            {
                // Directives and blank lines: emit as-is (but buffer after EXTINF)
                if (pendingExtInf is not null)
                {
                    sb.AppendLine(pendingExtInf);
                    pendingExtInf = null;
                }
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine(line);
                continue;
            }

            // This is a stream URL line
            if (pendingExtInf is not null)
            {
                sb.AppendLine(pendingExtInf);
                pendingExtInf = null;
            }

            var proxyUrl = BuildProxyStreamUrl(line.Trim(), proxyBaseUrl);
            sb.AppendLine(proxyUrl);
            count++;
        }

        return (sb.ToString(), count);
    }

    private static string BuildProxyStreamUrl(string originalUrl, string proxyBaseUrl)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(originalUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{proxyBaseUrl}/stream.m3u8?url={encoded}";
    }
}