using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JellyTV.Models;
using Microsoft.Extensions.Logging;

namespace JellyTV.Services;

/// <summary>
/// Fetches all configured M3U sources and returns a combined, rewritten playlist
/// where every channel URL goes through /JellyTV/stream so token renewal works.
/// </summary>
public class M3uAggregatorService
{
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
    /// Each channel URL is rewritten to /JellyTV/stream?url=&lt;base64&gt;
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

        var enabledSources = config.Sources.FindAll(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url));
        PluginLog.Info($"[M3U] Building combined M3U from {enabledSources.Count} source(s)");

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        var client = _httpClientFactory.CreateClient("JellyTV");
        var totalChannels = 0;
        var timeoutSeconds = Math.Clamp(config.RequestTimeoutSeconds, 5, 120);

        foreach (var source in enabledSources)
        {
            try
            {
                PluginLog.Info($"[M3U] Fetching source '{source.Name}'");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var content = await client.GetStringAsync(source.Url, timeoutCts.Token).ConfigureAwait(false);
                var channels = ParseAndRewrite(content, source, proxyBaseUrl);
                sb.Append(channels.content);
                totalChannels += channels.count;
                source.LastFetched = DateTimeOffset.UtcNow;
                source.LastChannelCount = channels.count;
                source.LastError = null;
                PluginLog.Info($"[M3U] Source '{source.Name}': {channels.count} channels OK");
            }
            catch (TaskCanceledException ex)
            {
                source.LastError = "Timeout o cancelacion al descargar la fuente.";
                _logger.LogWarning(ex, "[JellyTV] M3U source '{Name}' timed out/cancelled", source.Name);
                PluginLog.Warn($"[M3U] Source '{source.Name}' timeout/cancelada.");
            }
            catch (Exception ex)
            {
                source.LastError = ex.Message;
                _logger.LogError(ex, "[JellyTV] Failed to fetch M3U source '{Name}': {Url}", source.Name, source.Url);
                PluginLog.Error($"[M3U] Source '{source.Name}' failed: {ex.Message}");
            }
        }

        // Persist updated source stats
        try { Plugin.Instance?.SaveConfiguration(); } catch { }

        _cachedM3u = sb.ToString();
        _cachedAt = DateTimeOffset.UtcNow;

        PluginLog.Info($"[M3U] Combined M3U ready: {totalChannels} total channels");
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
        return $"{proxyBaseUrl}/stream?url={encoded}";
    }
}