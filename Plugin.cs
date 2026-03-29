using System;
using System.Collections.Generic;
using System.Net.Http;
using Jellyfin.Plugin.IptvProxy.Configuration;
using Jellyfin.Plugin.IptvProxy.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IptvProxy;

/// <summary>
/// Jellyfin IPTV Proxy Plugin entry point.
/// Owns and exposes all plugin services as singletons accessible via <see cref="Instance"/>.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Gets the plugin singleton.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the redirect resolver service.</summary>
    public RedirectResolverService RedirectResolver { get; }

    /// <summary>Gets the HLS rewriter service.</summary>
    public HlsRewriterService HlsRewriter { get; }

    /// <summary>Gets the session manager service.</summary>
    public SessionManagerService SessionManager { get; }

    /// <inheritdoc />
    public override string Name => "IPTV Proxy";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <inheritdoc />
    public override string Description =>
        "Proxies IPTV streams, follows HTTP redirects, and auto-renews time-limited tokens for uninterrupted playback.";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// Jellyfin injects <see cref="IApplicationPaths"/>, <see cref="IXmlSerializer"/>,
    /// <see cref="IHttpClientFactory"/>, and <see cref="ILoggerFactory"/> automatically.
    /// </summary>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _httpClientFactory = httpClientFactory;

        RedirectResolver = new RedirectResolverService(
            _httpClientFactory,
            loggerFactory.CreateLogger<RedirectResolverService>());

        HlsRewriter = new HlsRewriterService(
            _httpClientFactory,
            loggerFactory.CreateLogger<HlsRewriterService>());

        SessionManager = new SessionManagerService(
            RedirectResolver,
            loggerFactory.CreateLogger<SessionManagerService>());

        // Start the background token-renewal loop
        SessionManager.StartAsync(default).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "IPTV Proxy",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        ];
    }
}