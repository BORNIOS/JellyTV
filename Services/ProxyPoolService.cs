using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using JellyTV.Models;

namespace JellyTV.Services;

/// <summary>
/// Round-robin HTTPS proxy pool for chain resolution requests.
/// Segment downloads bypass the proxy entirely.
/// When a proxy times out, it is placed in a 5-minute cooldown so subsequent
/// resolve calls skip it immediately (no 3-second timeout overhead per hop).
/// </summary>
public sealed class ProxyPoolService
{
    private static readonly TimeSpan ProxyCooldown = TimeSpan.FromMinutes(5);

    private readonly string _userAgent;
    private int _roundRobinIndex;
    private readonly object _indexLock = new();

    // One HttpClient pair per proxy key (Host:Port), built lazily and cached.
    private readonly ConcurrentDictionary<string, (HttpClient NoRedirect, HttpClient AutoRedirect)> _pool = new();
    // Timestamp of last failure per proxy key. Entries expire after ProxyCooldown.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedAt = new();

    public ProxyPoolService(string userAgent)
    {
        _userAgent = userAgent;
    }

    /// <summary>
    /// Returns the key and client pair for the next healthy proxy in strict round-robin order,
    /// or (null, null, null) when no proxies are available or all are in cooldown.
    /// </summary>
    public (string? ProxyKey, HttpClient? NoRedirect, HttpClient? AutoRedirect) GetNextProxy()
    {
        var config = Plugin.Instance?.Configuration;
        var now = DateTimeOffset.UtcNow;

        var available = config?.Proxies?
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Host))
            .Where(p => {
                var k = $"{p.Host}:{p.Port}";
                return !_failedAt.TryGetValue(k, out var failedAt) || (now - failedAt) > ProxyCooldown;
            })
            .ToList();

        if (available is null || available.Count == 0)
        {
            if (config?.Proxies?.Any(p => p.IsEnabled) is true)
                PluginLog.Info("[PROXY] All proxies in cooldown — going direct");
            return (null, null, null);
        }

        ProxyEntry selected;
        lock (_indexLock)
        {
            _roundRobinIndex %= available.Count;
            selected = available[_roundRobinIndex++];
        }

        var key = $"{selected.Host}:{selected.Port}";
        var pair = _pool.GetOrAdd(key, _ => BuildPair(selected));

        PluginLog.Info($"[PROXY] Using proxy {key} for chain resolution");
        return (key, pair.NoRedirect, pair.AutoRedirect);
    }

    /// <summary>
    /// Returns the auto-redirect client for the next healthy proxy without logging,
    /// used to rotate IPs for segment downloads. Returns null when no proxies are configured.
    /// </summary>
    public HttpClient? GetSegmentClient()
    {
        return GetNextSegmentProxy().AutoRedirect;
    }

    /// <summary>
    /// Returns the next healthy proxy key and auto-redirect client, or null when none are available.
    /// </summary>
    public (string? ProxyKey, HttpClient? AutoRedirect) GetNextSegmentProxy()
    {
        var config = Plugin.Instance?.Configuration;
        var now    = DateTimeOffset.UtcNow;

        var available = config?.Proxies?
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Host))
            .Where(p =>
            {
                var k = $"{p.Host}:{p.Port}";
                return !_failedAt.TryGetValue(k, out var failedAt) || (now - failedAt) > ProxyCooldown;
            })
            .ToList();

        if (available is null || available.Count == 0) return (null, null);

        ProxyEntry selected;
        lock (_indexLock)
        {
            _roundRobinIndex %= available.Count;
            selected = available[_roundRobinIndex++];
        }

        var key  = $"{selected.Host}:{selected.Port}";
        var pair = _pool.GetOrAdd(key, _ => BuildPair(selected));
        return (key, pair.AutoRedirect);
    }

    /// <summary>
    /// Marks a proxy as failed. It will be skipped for <see cref="ProxyCooldown"/>.
    /// </summary>
    public void MarkFailed(string proxyKey)
    {
        _failedAt[proxyKey] = DateTimeOffset.UtcNow;
        PluginLog.Warn($"[PROXY] {proxyKey} marked failed — cooldown {ProxyCooldown.TotalMinutes:F0} min");
    }

    private (HttpClient, HttpClient) BuildPair(ProxyEntry entry)
    {
        var scheme = entry.UseHttps ? "https" : "http";
        var proxy = new WebProxy($"{scheme}://{entry.Host}:{entry.Port}", false);
        if (!string.IsNullOrEmpty(entry.Username))
            proxy.Credentials = new NetworkCredential(entry.Username, entry.Password);

        var cookies = new CookieContainer();
        return (
            Build(proxy, cookies, allowRedirect: false),
            Build(proxy, cookies, allowRedirect: true)
        );
    }

    private HttpClient Build(WebProxy proxy, CookieContainer cookies, bool allowRedirect)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = proxy,
            AllowAutoRedirect = allowRedirect,
            MaxAutomaticRedirections = 15,
            UseCookies = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
        };
        var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        return client;
    }
}
