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
/// Main plugin class for the IPTV Proxy. Registers services and exposes configuration pages.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Singleton instance of the plugin (set during construction).
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Redirect resolver used to follow upstream redirects and extract final stream URLs.
    /// </summary>
    public RedirectResolverService RedirectResolver { get; }

    /// <summary>
    /// HLS rewriter used to rewrite playlists so segments are proxied through the plugin.
    /// </summary>
    public HlsRewriterService HlsRewriter { get; }

    /// <summary>
    /// Background session manager that tracks active stream sessions and renews tokens.
    /// </summary>
    public SessionManagerService SessionManager { get; }

    /// <inheritdoc />
    public override string Name => "IPTV Proxy";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <inheritdoc />
    public override string Description => "Proxies IPTV streams and auto-renews tokens.";

    /// <summary>
    /// Initializes the plugin and registers internal services.
    /// </summary>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        PluginLog.Info("=== IPTV Proxy Plugin: constructor START ===");
        PluginLog.Info($"Log file: {AppContext.BaseDirectory}iptv-proxy-debug.log");

        Instance = this;

        try
        {
            PluginLog.Info("Creating RedirectResolverService...");
            RedirectResolver = new RedirectResolverService(
                httpClientFactory,
                loggerFactory.CreateLogger<RedirectResolverService>());
            PluginLog.Info("RedirectResolverService OK");

            PluginLog.Info("Creating HlsRewriterService...");
            HlsRewriter = new HlsRewriterService(
                httpClientFactory,
                loggerFactory.CreateLogger<HlsRewriterService>());
            PluginLog.Info("HlsRewriterService OK");

            PluginLog.Info("Creating SessionManagerService...");
            SessionManager = new SessionManagerService(
                RedirectResolver,
                loggerFactory.CreateLogger<SessionManagerService>());
            PluginLog.Info("SessionManagerService OK");

            PluginLog.Info("Starting background token renewal loop...");
            SessionManager.StartAsync(default).GetAwaiter().GetResult();
            PluginLog.Info("Background service started OK");
        }
        catch (Exception ex)
        {
            PluginLog.Error("FATAL: Plugin failed to initialize", ex);
            throw;
        }

        PluginLog.Info("=== IPTV Proxy Plugin: constructor END — plugin is ACTIVE ===");
    }

    /// <summary>
    /// Returns the admin web pages (configuration UI) for the plugin.
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "IPTV Proxy",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        }
    ];
}