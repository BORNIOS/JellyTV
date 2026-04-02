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
    public BroadcastService Broadcast { get; }

    // Jellyfin Live TV manager — may still be useful for service calls
    private readonly ILiveTvManager _liveTvManager;
    // Jellyfin tuner host manager -- used to auto-register the proxy tuner host
    private readonly ITunerHostManager _tunerHostManager;

    // The GUID we use for our auto-registered tuner so we can update it, not duplicate it
    private const string TunerGuid = "iptv-proxy-auto-tuner-v1";
    private readonly object _tunerRegistrationSync = new();
    private bool _tunerRegistered;
    private DateTimeOffset _nextTunerAttemptUtc = DateTimeOffset.MinValue;

    public override string Name => "JellyTV";
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    public override string Description => "Manages IPTV sources with automatic token renewal.";

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILiveTvManager liveTvManager,
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
        _liveTvManager = liveTvManager;
        _tunerHostManager = tunerHostManager;

        try
        {
            var localFactory = new LocalHttpClientFactory(
                httpClientFactory,
                Configuration?.UserAgent ?? "JellyTV/1.0");

            RedirectResolver = new RedirectResolverService(localFactory, loggerFactory.CreateLogger<RedirectResolverService>());
            HlsRewriter      = new HlsRewriterService(localFactory,      loggerFactory.CreateLogger<HlsRewriterService>());
            SessionManager   = new SessionManagerService(RedirectResolver, loggerFactory.CreateLogger<SessionManagerService>());
            M3uAggregator    = new M3uAggregatorService(localFactory,     loggerFactory.CreateLogger<M3uAggregatorService>());
            Broadcast        = new BroadcastService(SessionManager, localFactory);

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

        // Evita rafagas de intentos simultaneos cuando /m3u recibe muchas solicitudes.
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

            var tunerUrl = $"{jellyfinBaseUrl}/JellyTV/m3u";
            // Check by name+type so that if the plugin was reinstalled we recognize
            // the existing entry and don't register a second identical tuner.
            var existing = _tunerHostManager.TunerHosts
                .FirstOrDefault(t =>
                    string.Equals(t.Name, "JellyTV (auto)", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.Type, "m3u", StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                _tunerRegistered = true;
                return;
            }

            var tuner = new TunerHostInfo
            {
                Id           = TunerGuid,
                Type         = "m3u",
                Url          = tunerUrl,
                FriendlyName = "JellyTV (auto)",
                Source       = "JellyTV Plugin",
                TunerCount   = 10
            };

            _tunerHostManager.SaveTunerHost(tuner, true).GetAwaiter().GetResult();
            _tunerRegistered = true;
            PluginLog.Info($"[TUNER] Auto-registered tuner → {tunerUrl}");
        }
        catch (Exception ex)
        {
            // Evita reintentos continuos cuando Jellyfin aun no esta listo.
            _nextTunerAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(2);
            PluginLog.Error("[TUNER] Failed to auto-register tuner", ex);
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
                UseCookies = true,
                CookieContainer = cookieContainer,
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
