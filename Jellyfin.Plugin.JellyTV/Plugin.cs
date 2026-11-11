using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services;
using Jellyfin.Plugin.JellyTV.Services.Identity;
using Jellyfin.Plugin.JellyTV.Services.Storage;
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
/// into Jellyfin Live TV as a native provider without requiring an external M3U tuner.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// User-Agent por defecto (navegador). Fuente única; sobrescribible en Configuración →
    /// "User-Agent HTTP global". Nunca hardcodeado en la lógica de fetch/entrega.
    /// </summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    // ══ Constructor ════════════════════════════════════════════════════

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Serializer used for the plugin configuration.</param>
    /// <param name="httpClientFactory">Factory used for outbound HTTP requests.</param>
    /// <param name="loggerFactory">Factory used to create typed loggers.</param>
    /// <param name="serviceProvider">Server service provider used to discover the PostgreSQL provider.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
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
        DataDirectory = Path.Combine(applicationPaths.DataPath, "JellyTV");
        Storage = StorageBackend.Create(serviceProvider, Configuration, DataDirectory, Id);
        ApplyPersistentStore();
        ChannelCatalog = new ChannelCatalog(Storage, loggerFactory.CreateLogger<ChannelCatalog>());
        ChannelCatalog.Hydrate(Configuration.Channels);
        EnsureRelayKey();
        HttpClientFactory = httpClientFactory;

        try
        {
            var localFactory = new LocalHttpClientFactory(httpClientFactory, EffectiveUserAgent());

            var proxyPool = new ProxyPoolService(EffectiveUserAgent());
            ProxyPool = proxyPool;
            Upstream = new UpstreamClientService(localFactory, proxyPool);
            RedirectResolver = new RedirectResolverService(localFactory, loggerFactory.CreateLogger<RedirectResolverService>(), proxyPool);
            HlsRewriter = new HlsRewriterService(localFactory, proxyPool, Upstream, RedirectResolver, loggerFactory.CreateLogger<HlsRewriterService>());
            SessionManager = new SessionManagerService(RedirectResolver, Upstream, loggerFactory.CreateLogger<SessionManagerService>());
            ChannelHealthCheck = new ChannelHealthCheckService(localFactory, loggerFactory.CreateLogger<ChannelHealthCheckService>(), SessionManager, Upstream, applicationPaths);
            var epgFilePath = Path.Combine(DataDirectory, "epg.xml");
            EpgFilter = new EpgFilterService(localFactory, epgFilePath, loggerFactory.CreateLogger<EpgFilterService>());
            CachePath = applicationPaths.CachePath;
            TmdbEnrichment = new TmdbEnrichmentService(localFactory, loggerFactory.CreateLogger<TmdbEnrichmentService>());
            ChannelIdentity = new ChannelIdentityService(localFactory, loggerFactory.CreateLogger<ChannelIdentityService>());

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

    // ══ Estado y servicios expuestos ═════════════════════════════════════

    /// <summary>Gets the active plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets or sets the Jellyfin metadata provider manager, captured from DI when the Live TV service is built.</summary>
    internal static MediaBrowser.Controller.Providers.IProviderManager? NativeProviders { get; set; }

    /// <summary>Gets the pool of proxies that streams exit through.</summary>
    public ProxyPoolService ProxyPool { get; }

    /// <summary>Gets the service that picks the HTTP client (direct or through a proxy) for each download.</summary>
    public UpstreamClientService Upstream { get; }

    /// <summary>Gets the redirect resolver service.</summary>
    public RedirectResolverService RedirectResolver { get; }

    /// <summary>Gets the HLS playlist rewriter service.</summary>
    public HlsRewriterService HlsRewriter { get; }

    /// <summary>Gets the stream session manager.</summary>
    public SessionManagerService SessionManager { get; }

    /// <summary>Gets the channel health check service.</summary>
    public ChannelHealthCheckService ChannelHealthCheck { get; }

    /// <summary>Gets the local EPG filter service.</summary>
    public EpgFilterService EpgFilter { get; }

    /// <summary>Gets the TMDB enrichment service.</summary>
    public TmdbEnrichmentService TmdbEnrichment { get; }

    /// <summary>Gets the service that resolves channel identities (TVMaze and TMDB).</summary>
    public ChannelIdentityService ChannelIdentity { get; }

    /// <summary>Gets the HTTP client factory used by the plugin.</summary>
    public IHttpClientFactory HttpClientFactory { get; }

    /// <summary>Gets the active storage backend (PostgreSQL through PG Provider, or local JSON files).</summary>
    public StorageBackend? Storage { get; private set; }

    /// <summary>Gets the in-memory channel index used for health checks and failover decisions.</summary>
    public ChannelCatalog? ChannelCatalog { get; private set; }

    /// <summary>Gets directorio de datos del plugin (EPG, M3U tuner, etc.).</summary>
    public string DataDirectory { get; private set; } = string.Empty;

    /// <summary>Gets directorio de caché de Jellyfin (para invalidar la caché XMLTV tras regenerar la guía).</summary>
    public string CachePath { get; private set; } = string.Empty;

    /// <summary>Gets or sets base URL of the Jellyfin server, set on first access.</summary>
    public string? KnownBaseUrl { get; set; }

    /// <inheritdoc/>
    public override string Name => "JellyTV";

    /// <inheritdoc/>
    public override Guid Id => Guid.Parse("f8a3c1d7-9b2e-4a5f-8c6d-3e1b7f4a2d9c");

    /// <inheritdoc/>
    public override string Description => "TV channel manager with automatic token renewal for IPTV streams.";

    // ══ Helpers ═════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // La página de ajustes guarda la copia que cargó al abrirse: si entre tanto corrió un chequeo de salud,
        // esa copia trae el estado viejo y lo pisaría. Se conserva el resultado más reciente de cada fuente.
        if (configuration is PluginConfiguration incoming)
        {
            var known = Configuration.Channels
                .SelectMany(static c => c.Sources)
                .Where(static s => s.LastCheckedAt.HasValue)
                .GroupBy(static s => s.Id, StringComparer.Ordinal)
                .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
            foreach (var channel in incoming.Channels)
            {
                foreach (var source in channel.Sources)
                {
                    if (known.TryGetValue(source.Id, out var current)
                        && (!source.LastCheckedAt.HasValue || source.LastCheckedAt < current.LastCheckedAt))
                    {
                        source.IsAlive = current.IsAlive;
                        source.LastError = current.LastError;
                        source.LastCheckedAt = current.LastCheckedAt;
                    }
                }

                var enabled = channel.Sources.Where(static s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
                channel.LastCheckAlive = enabled.Any(static s => s.IsAlive == true) ? true : enabled.Any(static s => s.IsAlive == false) ? false : null;
                channel.LastCheckedAt = enabled.Where(static s => s.LastCheckedAt.HasValue).Select(static s => s.LastCheckedAt).Max();
                channel.LastCheckError = enabled.FirstOrDefault(static s => s.IsAlive == false)?.LastError;
            }
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc/>
    public override void SaveConfiguration(PluginConfiguration config)
    {
        // During the base constructor the XML fallback can call back here before the store exists.
        if (Storage is null)
        {
            base.SaveConfiguration(config);
            return;
        }

        var stored = false;
        try
        {
            Storage.Save(JellyTvStoreSnapshot.FromConfiguration(config));
            stored = true;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[STORAGE] Could not persist to {Storage.Description}: {ex.Message}");
        }

        // Con base de datos como almacén, el XML conserva solo los ajustes: los canales viven
        // únicamente en la DB. Si el guardado en la DB falló, el XML completo es el respaldo.
        if (stored && Storage.UsesDatabase)
        {
            SaveSettingsOnlyXml(config);
            return;
        }

        base.SaveConfiguration(config);
    }

    private void SaveSettingsOnlyXml(PluginConfiguration config)
    {
        try
        {
            XmlSerializer.SerializeToFile(config.CloneSettingsOnly(), ConfigurationFilePath);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[STORAGE] Could not write the settings-only XML: {ex.Message}");
        }
    }

    /// <summary>
    /// Hydrates the configuration from the active store, or seeds an empty store from the XML
    /// configuration. This keeps switching between PostgreSQL and JSON lossless.
    /// </summary>
    private void ApplyPersistentStore()
    {
        if (Storage is null)
        {
            return;
        }

        var snapshot = Storage.Store.Load();
        if (snapshot is { HasData: true })
        {
            Configuration.Channels = snapshot.Channels;
            Configuration.Proxies = snapshot.Proxies;
            Configuration.EpgSources = snapshot.EpgSources;
            PluginLog.Info($"[STORAGE] Loaded {snapshot.Channels.Count} channels, {snapshot.Proxies.Count} proxies and {snapshot.EpgSources.Count} EPG sources from {Storage.Description}.");
            try
            {
                Storage.WriteJsonBackup(snapshot);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"[STORAGE] Could not refresh the JSON backup: {ex.Message}");
            }

            if (Storage.UsesDatabase)
            {
                SaveSettingsOnlyXml(Configuration);
            }

            return;
        }

        if (Configuration.Channels.Count > 0 || Configuration.Proxies.Count > 0 || Configuration.EpgSources.Count > 0)
        {
            Storage.Save(JellyTvStoreSnapshot.FromConfiguration(Configuration));
            PluginLog.Info($"[STORAGE] Seeded {Storage.Description} from the plugin configuration.");
            if (Storage.UsesDatabase)
            {
                SaveSettingsOnlyXml(Configuration);
            }
        }
    }

    /// <summary>
    /// Ensures that a relay key exists so the anonymous relay endpoints cannot be
    /// used as an open proxy. The key is generated once and persisted in the plugin config.
    /// </summary>
    private void EnsureRelayKey()
    {
        var config = Configuration;
        if (!string.IsNullOrWhiteSpace(config.RelayKey))
        {
            return;
        }

        config.RelayKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            SaveConfiguration();
            PluginLog.Info("[SECURITY] Relay key generated.");
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[SECURITY] Could not persist relay key: {ex.Message}");
        }
    }

    /// <summary>
    /// User-Agent efectivo del plugin: el de Configuration si está definido, si no el default.
    /// Siempre saneado (sin CR/LF ni controles) para no romper cabeceras HTTP.
    /// </summary>
    /// <returns>El User-Agent a usar.</returns>
    public static string EffectiveUserAgent()
    {
        var configured = Instance?.Configuration?.UserAgent;
        return string.IsNullOrWhiteSpace(configured) ? DefaultUserAgent : SanitizeHeaderValue(configured);
    }

    /// <summary>Quita CR/LF y caracteres de control que romperían una cabecera HTTP.</summary>
    /// <param name="value">Cabecera cruda del usuario.</param>
    /// <returns>Cabecera saneada.</returns>
    public static string SanitizeHeaderValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7f)
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Invalida la caché XMLTV de Jellyfin que corresponde a guías generadas por este plugin.
    /// Jellyfin cachea cada proveedor XMLTV ~1 h (XmlTvListingsProvider); si una descarga coincidió
    /// con un archivo aún no generado, queda una copia vacía/vieja que impide ver la guía y mapear.
    /// Tras regenerar epg.xml se borran esas copias para que el Refresh Guide re-descargue la guía
    /// nueva. Se borran los .xml de la caché xmltv que sean de JellyTV (o de 0 bytes, inútiles).
    /// </summary>
    public void InvalidateJellyfinXmltvCache()
    {
        try
        {
            var dir = Path.Combine(CachePath, "xmltv");
            if (string.IsNullOrWhiteSpace(CachePath) || !Directory.Exists(dir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.xml"))
            {
                try
                {
                    var info = new FileInfo(file);
                    var isJellyTv = info.Length == 0;
                    if (!isJellyTv)
                    {
                        using var reader = new StreamReader(file);
                        string? line;
                        while ((line = reader.ReadLine()) is not null && !isJellyTv)
                        {
                            if (line.Contains("<tv ", StringComparison.Ordinal) && line.Contains("JellyTV", StringComparison.Ordinal))
                            {
                                isJellyTv = true;
                            }
                        }
                    }

                    if (isJellyTv)
                    {
                        File.Delete(file);
                        PluginLog.Info($"[EPG] Caché XMLTV de Jellyfin invalidada: {Path.GetFileName(file)}");
                    }
                }
                catch
                {
                    // Archivo en uso por Jellyfin: se reintentará en el próximo sync.
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[EPG] No se pudo invalidar la caché XMLTV de Jellyfin: {ex.Message}");
        }
    }

    /// <summary>
    /// Migrates legacy channel fields into <see cref="ChannelEntry.Sources"/> and purges invalid
    /// channels (without a name or without any usable source URL). Optionally persists the change.
    /// </summary>
    /// <param name="persist">true para guardar la configuración si hubo cambios.</param>
    /// <returns>true si la configuración cambió.</returns>
    public static bool NormalizeChannels(bool persist = true)
        => Instance is { } plugin ? NormalizeChannels(plugin.Configuration, persist) : false;

    /// <summary>
    /// Migrates legacy channel fields into <see cref="ChannelEntry.Sources"/> and purges invalid
    /// channels (without a name or without any usable source URL). Optionally persists the change.
    /// </summary>
    /// <param name="cfg">Configuración sobre la que trabajar.</param>
    /// <param name="persist">true para guardar la configuración si hubo cambios.</param>
    /// <returns>true si la configuración cambió.</returns>
    public static bool NormalizeChannels(PluginConfiguration cfg, bool persist = true)
    {
        var changed = false;
        var cleaned = new List<ChannelEntry>(cfg.Channels.Count);
        foreach (var ch in cfg.Channels)
        {
            ch.EnsureMigratedSources();
            var valid = ch.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Url)).ToList();
            if (valid.Count != ch.Sources.Count)
            {
                changed = true;
            }

            ch.Sources = valid;
            if (string.IsNullOrWhiteSpace(ch.Name) || valid.Count == 0)
            {
                changed = true;
                continue;
            }

            cleaned.Add(ch);
        }

        if (cleaned.Count != cfg.Channels.Count)
        {
            changed = true;
        }

        if (changed)
        {
            cfg.Channels = cleaned;
            if (persist)
            {
                Instance?.SaveConfiguration();
            }
        }

        return changed;
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

    // ══ Plugin pages ════════════════════════════════════════════════════

    /// <inheritdoc/>
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "JellyTV",
            DisplayName = "JellyTV",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            // Muestra el acceso directo al plugin en el menú principal (sidebar) del dashboard,
            // sin tener que entrar en Complementos → JellyTV → Configuración.
            EnableInMainMenu = true,
            MenuIcon = "tv"
        }
    ];

    // ══ HTTP client factory ─────────────────────────────────────────────

    private sealed class LocalHttpClientFactory : IHttpClientFactory
    {
        private readonly IHttpClientFactory _fallback;
        private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
        private readonly CookieContainer _sharedCookies = new();

        public LocalHttpClientFactory(IHttpClientFactory fallback, string userAgent)
        {
            _fallback = fallback;

            _clients["IptvNoRedirect"] = Build(userAgent, allowRedirect: false, _sharedCookies);
            _clients["JellyTV"] = Build(userAgent, allowRedirect: true, _sharedCookies);
        }

        private static HttpClient Build(string userAgent, bool allowRedirect, CookieContainer cookieContainer)
        {
            var client = new HttpClient(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = allowRedirect,
                    MaxAutomaticRedirections = 15,
                    UseCookies = true,
                    CookieContainer = cookieContainer,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
                },
                disposeHandler: false);
            // TryAddWithoutValidation: un User-Agent inválido del usuario no debe tumbar el plugin.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            return client;
        }

        public HttpClient CreateClient(string name) =>
            _clients.TryGetValue(name, out var c) ? c : _fallback.CreateClient(name);
    }
}
