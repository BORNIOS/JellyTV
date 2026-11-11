using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Pool of proxies. Proxies without a country rotate (round-robin) for the chain resolution of tokenized channels;
/// proxies with a country are exit proxies, used only by the sources that ask to exit through that country.
/// When a proxy times out, it is placed in a 5-minute cooldown so subsequent
/// resolve calls skip it immediately (no 3-second timeout overhead per hop).
/// </summary>
public sealed class ProxyPoolService
{
    private static readonly TimeSpan ProxyCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProxyKnownGood = TimeSpan.FromMinutes(15);

    private readonly string _userAgent;
    private readonly object _indexLock = new();

    // One HttpClient pair per proxy key (Host:Port), built lazily and cached.
    private readonly ConcurrentDictionary<string, (HttpClient NoRedirect, HttpClient AutoRedirect)> _pool = new();
    // Timestamp of last failure per proxy key. Entries expire after ProxyCooldown.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedAt = new();
    // When each proxy last answered a real request: proxies that answered recently are tried first.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _okAt = new();

    private int _roundRobinIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyPoolService"/> class.
    /// </summary>
    /// <param name="userAgent">User-Agent applied to proxy HTTP clients.</param>
    public ProxyPoolService(string userAgent)
    {
        _userAgent = userAgent;
    }

    /// <summary>
    /// Normalizes a country to its two-letter uppercase code, or an empty string when it is not one.
    /// </summary>
    /// <param name="country">Country as typed by the administrator.</param>
    /// <returns>The code, such as "US", or an empty string.</returns>
    public static string NormalizeCountry(string? country)
    {
        var trimmed = country?.Trim();
        return trimmed is { Length: 2 } && char.IsLetter(trimmed[0]) && char.IsLetter(trimmed[1])
            ? trimmed.ToUpperInvariant()
            : string.Empty;
    }

    /// <summary>
    /// Returns the key and client pair for the next healthy proxy in strict round-robin order,
    /// or (null, null, null) when no proxies are available or all are in cooldown.
    /// </summary>
    /// <returns></returns>
    public (string? ProxyKey, HttpClient? NoRedirect, HttpClient? AutoRedirect) GetNextProxy()
    {
        var config = Plugin.Instance?.Configuration;
        var now = DateTimeOffset.UtcNow;

        var available = config?.Proxies?
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Host) && NormalizeCountry(p.Country).Length == 0)
            .Where(p =>
            {
                var k = $"{p.Host}:{p.Port}";
                return !_failedAt.TryGetValue(k, out var failedAt) || (now - failedAt) > ProxyCooldown;
            })
            .ToList();

        if (available is null || available.Count == 0)
        {
            if (config?.Proxies?.Any(p => p.IsEnabled && NormalizeCountry(p.Country).Length == 0) is true)
            {
                PluginLog.Info("[PROXY] All proxies in cooldown — going direct");
            }

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
    /// <returns></returns>
    public HttpClient? GetSegmentClient()
    {
        return GetNextSegmentProxy().AutoRedirect;
    }

    /// <summary>
    /// Returns the next healthy proxy key and auto-redirect client, or null when none are available.
    /// </summary>
    /// <returns></returns>
    public (string? ProxyKey, HttpClient? AutoRedirect) GetNextSegmentProxy()
    {
        var config = Plugin.Instance?.Configuration;
        var now = DateTimeOffset.UtcNow;

        var available = config?.Proxies?
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Host) && NormalizeCountry(p.Country).Length == 0)
            .Where(p =>
            {
                var k = $"{p.Host}:{p.Port}";
                return !_failedAt.TryGetValue(k, out var failedAt) || (now - failedAt) > ProxyCooldown;
            })
            .ToList();

        if (available is null || available.Count == 0)
        {
            return (null, null);
        }

        ProxyEntry selected;
        lock (_indexLock)
        {
            _roundRobinIndex %= available.Count;
            selected = available[_roundRobinIndex++];
        }

        var key = $"{selected.Host}:{selected.Port}";
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

    /// <summary>
    /// Lists the countries that have at least one enabled proxy, so the channel form only offers exits that exist.
    /// </summary>
    /// <returns>Country codes in alphabetical order.</returns>
    public IReadOnlyList<string> GetCountries()
        => (Plugin.Instance?.Configuration?.Proxies ?? [])
            .Where(static p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Host))
            .Select(static p => NormalizeCountry(p.Country))
            .Where(static c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Returns the healthy proxies of a country, starting with the one this stream always uses. The same stream keeps
    /// the same exit address (providers often bind the token to it); the rest are the failover order.
    /// </summary>
    /// <param name="country">Country the stream must exit through.</param>
    /// <param name="stickyKey">Stable identifier of the stream, which decides which proxy it starts with.</param>
    /// <returns>Proxy key and client pairs, preferred first; empty when no proxy of that country is available.</returns>
    public IReadOnlyList<(string Key, HttpClient Client)> GetCandidates(string country, string stickyKey)
    {
        var wanted = NormalizeCountry(country);
        var now = DateTimeOffset.UtcNow;
        var available = (Plugin.Instance?.Configuration?.Proxies ?? [])
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Host) && string.Equals(NormalizeCountry(p.Country), wanted, StringComparison.Ordinal))
            .OrderBy(static p => $"{p.Host}:{p.Port}", StringComparer.Ordinal)
            .Where(p => !_failedAt.TryGetValue($"{p.Host}:{p.Port}", out var failedAt) || (now - failedAt) > ProxyCooldown)
            .ToList();
        if (wanted.Length == 0 || available.Count == 0)
        {
            return [];
        }

        // The proxies that answered recently go first, so a pool with dead entries does not make every stream wait for
        // them; within each group the stream always starts at the same proxy.
        var known = available.Where(p => _okAt.TryGetValue($"{p.Host}:{p.Port}", out var okAt) && (now - okAt) < ProxyKnownGood).ToList();
        var unknown = available.Except(known).ToList();
        var ordered = new List<(string Key, HttpClient Client)>(available.Count);
        foreach (var group in new[] { known, unknown })
        {
            if (group.Count == 0)
            {
                continue;
            }

            var start = (int)(StableHash(stickyKey) % (uint)group.Count);
            for (var offset = 0; offset < group.Count; offset++)
            {
                var proxy = group[(start + offset) % group.Count];
                var key = $"{proxy.Host}:{proxy.Port}";
                ordered.Add((key, _pool.GetOrAdd(key, _ => BuildPair(proxy)).AutoRedirect));
            }
        }

        return ordered;
    }

    /// <summary>
    /// Records that a proxy answered, which takes it out of cooldown and moves it to the front of its country.
    /// </summary>
    /// <param name="proxyKey">Host and port of the proxy.</param>
    public void MarkOk(string proxyKey)
    {
        _okAt[proxyKey] = DateTimeOffset.UtcNow;
        _failedAt.TryRemove(proxyKey, out _);
    }

    /// <summary>
    /// Tests a proxy from this server. With a URL it downloads it through the proxy, which proves the proxy reaches the
    /// stream from where the stream is blocked; without one it only proves the proxy accepts connections.
    /// </summary>
    /// <param name="entry">Proxy to test; it does not need to be saved.</param>
    /// <param name="url">Address to download through the proxy, or null to only test the connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, in the administrator's words.</returns>
    public async Task<ProxyTestResult> TestAsync(ProxyEntry entry, string? url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            if (string.IsNullOrWhiteSpace(url))
            {
                await ConnectAsync(entry, timeout.Token).ConfigureAwait(false);
                MarkOk($"{entry.Host}:{entry.Port}");
                return new ProxyTestResult(
                    true,
                    null,
                    watch.ElapsedMilliseconds,
                    entry.UseHttps
                        ? "El proxy responde por TLS. Para comprobar que sale al país correcto, pruébalo con la dirección de un canal."
                        : "El proxy acepta conexiones. Para comprobar que sale al país correcto, pruébalo con la dirección de un canal.");
            }

            using var handler = CreateHandler(CreateProxy(entry), new CookieContainer(), allowRedirect: true);
            using var client = new HttpClient(handler, disposeHandler: false);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _userAgent);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new ProxyTestResult(false, status, watch.ElapsedMilliseconds, $"El proxy conectó, pero el destino respondió HTTP {status}: probablemente ese país tampoco tiene acceso.");
            }

            var buffer = new byte[16384];
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            MarkOk($"{entry.Host}:{entry.Port}");
            return new ProxyTestResult(true, status, watch.ElapsedMilliseconds, $"Descargó {read} bytes del canal por este proxy (HTTP {status}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            MarkFailed($"{entry.Host}:{entry.Port}");
            return new ProxyTestResult(false, null, watch.ElapsedMilliseconds, $"El proxy no respondió a tiempo desde este servidor ({watch.Elapsed.TotalSeconds:F0} s).");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or AuthenticationException or IOException)
        {
            MarkFailed($"{entry.Host}:{entry.Port}");
            return new ProxyTestResult(false, null, watch.ElapsedMilliseconds, ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static uint StableHash(string value)
    {
        // FNV-1a: the same text always gives the same number, unlike string.GetHashCode, so a stream keeps its proxy.
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash;
    }

    private static WebProxy CreateProxy(ProxyEntry entry)
    {
        var scheme = entry.UseHttps ? "https" : "http";
        var proxy = new WebProxy($"{scheme}://{entry.Host}:{entry.Port}", false);
        if (!string.IsNullOrEmpty(entry.Username))
        {
            proxy.Credentials = new NetworkCredential(entry.Username, entry.Password);
        }

        return proxy;
    }

    private static async Task ConnectAsync(ProxyEntry entry, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(entry.Host, entry.Port, cancellationToken).ConfigureAwait(false);
        if (!entry.UseHttps)
        {
            return;
        }

        using var tls = new SslStream(tcp.GetStream(), false, static (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = entry.Host }, cancellationToken).ConfigureAwait(false);
    }

    private static SocketsHttpHandler CreateHandler(WebProxy proxy, CookieContainer cookies, bool allowRedirect)
        => new()
        {
            UseProxy = true,
            Proxy = proxy,
            AllowAutoRedirect = allowRedirect,
            MaxAutomaticRedirections = 15,
            MaxConnectionsPerServer = 16,
            UseCookies = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(6),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
        };

    private (HttpClient NoRedirect, HttpClient AutoRedirect) BuildPair(ProxyEntry entry)
    {
        var proxy = CreateProxy(entry);
        var cookies = new CookieContainer();
        return (
            NoRedirect: Build(proxy, cookies, allowRedirect: false),
            AutoRedirect: Build(proxy, cookies, allowRedirect: true));
    }

    private HttpClient Build(WebProxy proxy, CookieContainer cookies, bool allowRedirect)
    {
        var handler = CreateHandler(proxy, cookies, allowRedirect);
        var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _userAgent);
        return client;
    }
}
