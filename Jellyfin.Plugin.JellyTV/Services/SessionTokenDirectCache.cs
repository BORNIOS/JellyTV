using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Resolves "token de sesión" sources and hands the final media URL straight to Jellyfin, so the
/// player talks to the provider (Direct Play) instead of going through the relay. A session token
/// lives for hours, so every client of a channel shares the same one: it is reused for
/// <see cref="ReuseWindow"/>, refreshed in the background once it is older than
/// <see cref="RefreshAfter"/> (clients never wait for it) and concurrent requests share one resolution.
/// </summary>
public static class SessionTokenDirectCache
{
    private static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<string, (string Url, DateTimeOffset ResolvedAt, DateTimeOffset ValidUntil)> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> InFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets a direct-play URL for a session-token source.
    /// </summary>
    /// <param name="source">Source to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The final media URL, or <c>null</c> when the source needs the relay (ephemeral token, FAST or a
    /// gateway that could not be resolved).
    /// </returns>
    public static async Task<string?> TryGetDirectUrlAsync(ChannelSource source, CancellationToken cancellationToken = default)
    {
        if (source is null || !ChannelEntry.IsTokenizedType(source.Type) || !source.SessionToken)
        {
            return null;
        }

        if (DeliveryPolicy.IsDirectMediaUrl(source.Url))
        {
            return source.Url;
        }

        if (Cache.TryGetValue(source.Url, out var cached) && cached.ValidUntil > DateTimeOffset.UtcNow)
        {
            if (DateTimeOffset.UtcNow - cached.ResolvedAt > RefreshAfter)
            {
                // Refresco anticipado: este cliente recibe el token vigente y el siguiente uno nuevo.
                _ = InFlight.GetOrAdd(source.Url, url => new Lazy<Task<string?>>(() => RefreshAsync(url))).Value;
            }

            return cached.Url;
        }

        var resolution = InFlight.GetOrAdd(source.Url, url => new Lazy<Task<string?>>(() => ResolveAsync(url)));
        try
        {
            return await resolution.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (resolution.IsValueCreated && resolution.Value.IsCompleted)
            {
                InFlight.TryRemove(new System.Collections.Generic.KeyValuePair<string, Lazy<Task<string?>>>(source.Url, resolution));
            }
        }
    }

    /// <summary>
    /// Blocking variant for the legacy M3U playlist, which is built synchronously.
    /// </summary>
    /// <param name="source">Source to resolve.</param>
    /// <returns>The final media URL, or <c>null</c> when the source needs the relay.</returns>
    public static string? TryGetDirectUrl(ChannelSource source) =>
        TryGetDirectUrlAsync(source).GetAwaiter().GetResult();

    /// <summary>
    /// Drops the cached URL of a source (for example after a manual edit).
    /// </summary>
    /// <param name="sourceUrl">Original source URL.</param>
    public static void Invalidate(string sourceUrl) => Cache.TryRemove(sourceUrl, out _);

    private static async Task<string?> RefreshAsync(string gatewayUrl)
    {
        try
        {
            return await ResolveAsync(gatewayUrl).ConfigureAwait(false);
        }
        finally
        {
            InFlight.TryRemove(gatewayUrl, out _);
        }
    }

    private static async Task<string?> ResolveAsync(string gatewayUrl)
    {
        try
        {
            var resolver = Plugin.Instance?.RedirectResolver;
            if (resolver is null)
            {
                return null;
            }

            using var cts = new CancellationTokenSource(ResolveTimeout);
            var resolved = await resolver.ResolveAsync(gatewayUrl, cts.Token).ConfigureAwait(false);
            if (resolved is null || !DeliveryPolicy.IsDirectMediaUrl(resolved.FinalUrl))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var validUntil = now + ReuseWindow;
            if (resolved.TokenExpiry is { } expiry && expiry - ExpiryMargin < validUntil)
            {
                validUntil = expiry - ExpiryMargin;
            }

            Cache[gatewayUrl] = (resolved.FinalUrl, now, validUntil);
            PluginLog.Info($"[DIRECT] Token de sesión resuelto; se entrega URL final directa al cliente ({PluginLog.Redact(resolved.FinalUrl)}).");
            return resolved.FinalUrl;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[DIRECT] No se pudo resolver el token de sesión para entrega directa: {ex.Message}");
            return null;
        }
    }
}
