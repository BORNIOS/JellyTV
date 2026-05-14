using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using JellyTV.Configuration;
using JellyTV.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace JellyTV;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public RedirectResolverService RedirectResolver { get; }
    public HlsRewriterService HlsRewriter { get; }
    public SessionManagerService SessionManager { get; }
    public M3uAggregatorService M3uAggregator { get; }
    public ChannelHealthCheckService ChannelHealthCheck { get; }

    // Exposed for scheduled tasks
    public IHttpClientFactory HttpClientFactory { get; }

    // Jellyfin tuner host manager -- used to auto-register the proxy tuner host
    private readonly ITunerHostManager _tunerHostManager;

    // GUID fijo (formato "N" = 32 hex sin guiones, igual al que usa Guid.NewGuid().ToString("N")).
    // Jellyfin usa el Id para decidir update-vs-insert en SaveTunerHost, garantizando upsert idempotente.
    private const string TunerGuid = "f8a3c1d79b2e4a5f8c6d3e1b7f4a2d9d";
    private readonly object _tunerRegistrationSync = new();
    private bool _tunerRegistered;
    private DateTimeOffset _nextTunerAttemptUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Base URL of the Jellyfin server, populated on the first /m3u request.
    /// Used by scheduled tasks that need to construct proxy URLs.
    /// </summary>
    public string? KnownBaseUrl { get; private set; }

    public override string Name => "JellyTV";
    public override Guid Id => Guid.Parse("f8a3c1d7-9b2e-4a5f-8c6d-3e1b7f4a2d9c");
    public override string Description => "Manages IPTV sources with automatic token renewal.";

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ITunerHostManager tunerHostManager)
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
        _tunerHostManager = tunerHostManager;
        HttpClientFactory = httpClientFactory;

        try
        {
            var localFactory = new LocalHttpClientFactory(
                httpClientFactory,
                Configuration?.UserAgent ?? "JellyTV/1.0");

            var proxyPool = new ProxyPoolService(Configuration?.UserAgent ?? "JellyTV/1.0");
            RedirectResolver = new RedirectResolverService(localFactory, loggerFactory.CreateLogger<RedirectResolverService>(), proxyPool);
            HlsRewriter      = new HlsRewriterService(localFactory, proxyPool, loggerFactory.CreateLogger<HlsRewriterService>());
            SessionManager   = new SessionManagerService(RedirectResolver, loggerFactory.CreateLogger<SessionManagerService>());
            M3uAggregator    = new M3uAggregatorService(localFactory,     loggerFactory.CreateLogger<M3uAggregatorService>());
            ChannelHealthCheck = new ChannelHealthCheckService(localFactory, loggerFactory.CreateLogger<ChannelHealthCheckService>(), SessionManager, applicationPaths);

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

    /// <summary>
    /// Registers (or updates) a single M3U tuner in Jellyfin pointing to
    /// /JellyTV/m3u so the Live TV section auto-populates from our sources.
    /// Call this after Jellyfin is fully started (e.g., from the /m3u endpoint on first hit).
    /// </summary>
    public void EnsureTunerRegistered(string jellyfinBaseUrl)
    {
        if (Configuration?.AutoRegisterTuner != true) return;

        if (_tunerRegistered || DateTimeOffset.UtcNow < _nextTunerAttemptUtc)
        {
            return;
        }

        // Evita ráfagas de intentos simultáneos cuando /m3u recibe muchas solicitudes.
        if (!System.Threading.Monitor.TryEnter(_tunerRegistrationSync))
        {
            return;
        }

        try
        {
            if (_tunerRegistered || DateTimeOffset.UtcNow < _nextTunerAttemptUtc)
            {
                return;
            }

            // SaveTunerHost en Jellyfin hace upsert basado en Id: si ya existe un
            // TunerHostInfo con el mismo Id lo actualiza; si no, lo inserta.
            // Usar un Id constante garantiza que cada reinicio actualiza el registro
            // existente en lugar de crear un duplicado. ITunerHostManager.TunerHosts
            // devuelve ITunerHost (solo Name/Type), no TunerHostInfo, por lo que no
            // es posible detectar de forma fiable la URL/FriendlyName del tuner existente.
            KnownBaseUrl = jellyfinBaseUrl;
            var tunerUrl = $"{jellyfinBaseUrl}/JellyTV/m3u";
            var tuner = new TunerHostInfo
            {
                Id           = TunerGuid,
                Type         = "m3u",
                Url          = tunerUrl,
                FriendlyName = "JellyTV (auto)",
                Source       = "JellyTV Plugin",
                // For Jellyfin's M3U tuner host, TunerCount == 0 keeps the source
                // eligible for direct play instead of treating it like a managed
                // finite-tuner source that is funneled through the live TV pipeline.
                TunerCount   = 0
            };

            _tunerHostManager.SaveTunerHost(tuner, true).GetAwaiter().GetResult();
            _tunerRegistered = true;
            PluginLog.Info($"[TUNER] Tuner registrado/actualizado → {tunerUrl}");
        }
        catch (Exception ex)
        {
            // Evita reintentos continuos cuando Jellyfin aún no está listo.
            _nextTunerAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(2);
            PluginLog.Error("[TUNER] Failed to register/update tuner", ex);
        }
        finally
        {
            System.Threading.Monitor.Exit(_tunerRegistrationSync);
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
