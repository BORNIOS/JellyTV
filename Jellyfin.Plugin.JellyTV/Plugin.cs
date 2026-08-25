using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV;

/// <summary>
/// JellyTV plugin — manages individual TV channels and injects them directly
/// into Jellyfin Live TV via <see cref="ILiveTvService"/>, bypassing the M3U tuner.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public RedirectResolverService RedirectResolver { get; }
    public HlsRewriterService HlsRewriter { get; }
    public SessionManagerService SessionManager { get; }
    public ChannelHealthCheckService ChannelHealthCheck { get; }
    public WorkerReporterService WorkerReporter { get; }
    public EpgFilterService EpgFilter { get; }
    public TmdbEnrichmentService TmdbEnrichment { get; }

    public IHttpClientFactory HttpClientFactory { get; }

    /// <summary>Base URL of the Jellyfin server, set on first access.</summary>
    public string? KnownBaseUrl { get; set; }

    // ══ BasePlugin overrides ════════════════════════════════════════════
    public override string Name => "JellyTV";
    public override Guid Id => Guid.Parse("f8a3c1d7-9b2e-4a5f-8c6d-3e1b7f4a2d9c");
    public override string Description => "TV channel manager with automatic token renewal for IPTV streams.";

    // ══ Constructor ════════════════════════════════════════════════════

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        ConfigureLogDirectory(applicationPaths);
        PluginLog.Info("=== JellyTV Plugin: constructor START ===");
        PluginLog.Info($"Plugin log file: {PluginLog.CurrentLogPath}");
        try
        {
            var asm = GetType().Assembly.GetName();
            PluginLog.Info($"Assembly loaded: {asm.Name} v{asm.Version} @ {GetType().Assembly.Location}");
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Could not read assembly metadata: {ex.Message}");
        }

        Instance = this;
        HttpClientFactory = httpClientFactory;

        try
        {
            var localFactory = new LocalHttpClientFactory(
                httpClientFactory,
                Configuration?.UserAgent ?? "JellyTV/1.0");

            var proxyPool = new ProxyPoolService(Configuration?.UserAgent ?? "JellyTV/1.0");
            RedirectResolver = new RedirectResolverService(localFactory, loggerFactory.CreateLogger<RedirectResolverService>(), proxyPool);
            HlsRewriter      = new HlsRewriterService(localFactory, proxyPool, RedirectResolver, loggerFactory.CreateLogger<HlsRewriterService>());
            SessionManager   = new SessionManagerService(RedirectResolver, loggerFactory.CreateLogger<SessionManagerService>());
            ChannelHealthCheck = new ChannelHealthCheckService(localFactory, loggerFactory.CreateLogger<ChannelHealthCheckService>(), SessionManager, applicationPaths);
            WorkerReporter    = new WorkerReporterService(localFactory, loggerFactory.CreateLogger<WorkerReporterService>());
            EpgFilter         = new EpgFilterService(localFactory, loggerFactory.CreateLogger<EpgFilterService>());
            TmdbEnrichment    = new TmdbEnrichmentService(localFactory, loggerFactory.CreateLogger<TmdbEnrichmentService>());

            SessionManager.StartAsync(default).GetAwaiter().GetResult();
            PluginLog.Info("All services started OK");
        }
        catch (Exception ex)
        {
            PluginLog.Error("FATAL: Plugin failed to initialize", ex);
            throw;
        }

        PluginLog.Info("=== JellyTV Plugin: ACTIVE ===");
    }

    private static void ConfigureLogDirectory(IApplicationPaths applicationPaths)
    {
        foreach (var candidate in GetLogDirectoryCandidates(applicationPaths))
        {
            PluginLog.SetLogDirectory(candidate);
            if (PluginLog.CurrentLogPath.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private static IEnumerable<string> GetLogDirectoryCandidates(IApplicationPaths applicationPaths)
    {
        if (!string.IsNullOrWhiteSpace(applicationPaths.LogDirectoryPath))
        {
            yield return applicationPaths.LogDirectoryPath;
        }

        if (!string.IsNullOrWhiteSpace(applicationPaths.ProgramDataPath))
        {
            yield return Path.Combine(applicationPaths.ProgramDataPath, "log");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "jellyfin", "log");
            yield return Path.Combine(localAppData, "Jellyfin", "Server", "log");
        }

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonAppData))
        {
            yield return Path.Combine(commonAppData, "Jellyfin", "Server", "log");
        }
    }

    // ── HTTP client factory ───────────────────────────────────────────────────

    private sealed class LocalHttpClientFactory : IHttpClientFactory
    {
        private readonly IHttpClientFactory _fallback;
        private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
        private readonly CookieContainer _sharedCookies = new();

        public LocalHttpClientFactory(IHttpClientFactory fallback, string userAgent)
        {
            _fallback = fallback;

            _clients["IptvNoRedirect"] = Build(userAgent, allowRedirect: false, _sharedCookies);
            _clients["JellyTV"]        = Build(userAgent, allowRedirect: true, _sharedCookies);
        }

        private static HttpClient Build(string userAgent, bool allowRedirect, CookieContainer cookieContainer)
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = allowRedirect,
                MaxAutomaticRedirections = 15,
                UseCookies = true,
                CookieContainer = cookieContainer,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
            }, disposeHandler: false);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            return client;
        }

        public HttpClient CreateClient(string name) =>
            _clients.TryGetValue(name, out var c) ? c : _fallback.CreateClient(name);
    }

    // ── Plugin pages ──────────────────────────────────────────────────────────

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "JellyTV",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.ConfigPage.html"
        }
    ];
}
