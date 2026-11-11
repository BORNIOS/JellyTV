using System;
using System.Net.Http;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Gives each download the HTTP client it must use: the normal one, or one that goes through a proxy of the country the
/// source has to exit through. A source with an exit is downloaded entirely through that proxy (resolution, playlists
/// and segments) because a geoblocked CDN refuses every request that does not come from the allowed country.
/// </summary>
public sealed class UpstreamClientService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProxyPoolService _proxyPool;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpstreamClientService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory of the normal clients.</param>
    /// <param name="proxyPool">Pool the exit proxies come from.</param>
    public UpstreamClientService(IHttpClientFactory httpClientFactory, ProxyPoolService proxyPool)
    {
        _httpClientFactory = httpClientFactory;
        _proxyPool = proxyPool;
    }

    /// <summary>
    /// Tells whether a source with this exit country must go through a proxy.
    /// </summary>
    /// <param name="exitCountry">Exit country of the source, if any.</param>
    /// <returns><c>true</c> when the source has an exit.</returns>
    public static bool RequiresExit(string? exitCountry) => ProxyPoolService.NormalizeCountry(exitCountry).Length > 0;

    /// <summary>
    /// Returns the client for a stream: through a proxy of the exit country when it has one, directly otherwise.
    /// </summary>
    /// <param name="exitCountry">Exit country of the source, if any.</param>
    /// <param name="stickyKey">Stable identifier of the stream, so it keeps the same proxy.</param>
    /// <returns>The client to download with.</returns>
    public HttpClient For(string? exitCountry, string stickyKey)
    {
        var country = ProxyPoolService.NormalizeCountry(exitCountry);
        if (country.Length == 0)
        {
            return _httpClientFactory.CreateClient("JellyTV");
        }

        // The client tries the proxies of the country in order on every request, so a dead one never fails a stream
        // while another proxy of the same country works.
        return new HttpClient(new ExitFailoverHandler(_proxyPool, country, stickyKey), disposeHandler: true);
    }

    /// <summary>
    /// Returns the client a relay session downloads with.
    /// </summary>
    /// <param name="session">Relay session.</param>
    /// <returns>The client to download with.</returns>
    public HttpClient For(StreamSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return For(session.ExitCountry, session.UpstreamUrl);
    }
}
