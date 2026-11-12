using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Performs real stream health checks using ffmpeg (preferred, fast) or fallback real download.
/// ffmpeg: decodes a small slice of the live stream to verify playback starts quickly.
/// Fallback: downloads actual video data (HLS segments or MPEG-TS chunks) if ffmpeg unavailable.
/// Uses in-memory cache to avoid excessive checks on the same channels.
/// </summary>
public class ChannelHealthCheckService
{
    // Hardcoded constants
    private const int CACHETTLMINUTES = 30;
    private const int MAXBYTESTODOWNLOAD = 5_000_000; // 5 MB max per fallback check
    private const int MINBYTESFORSUCCESS = 200_000;   // 200 KB minimum to consider "alive"
    private const int QuickCheckTimeoutSeconds = 20;
    private const int RESOLVETIMEOUTSECONDS = 20;
    private const int REQUESTTIMEOUTSECONDS = 30;
    private const int FFMPEGTIMEOUTSECONDS = 18;
    private const int FFMPEGNETWORKTIMEOUTMICROSECONDS = 5_000_000;
    private const int FFMPEGPROBEDURATIONSECONDS = 2;

    private static readonly string[] NewLineSeparators = ["\r\n", "\r", "\n"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChannelHealthCheckService> _logger;
    private readonly UpstreamClientService _upstream;
    private readonly SessionManagerService _sessionManager;
    private readonly IApplicationPaths _applicationPaths;

    // In-memory cache: key = ChannelUrl, value = (ChannelHealthStatus, CachedAt)
    private readonly ConcurrentDictionary<string, (ChannelHealthStatus, DateTimeOffset)> _healthCache = new();

    private string? _ffmpegPath;
    private DateTimeOffset _lastFfmpegCheck = DateTimeOffset.MinValue;

    // Avance de la revisión en curso (fuentes comprobadas / fuentes totales) para la barra del panel.
    private TaskProgress _progress = TaskProgress.Idle;
    private int _running;
    private int _done;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelHealthCheckService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory used for upstream health probes.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="sessionManager">Session manager used to resolve tokenized channels.</param>
    /// <param name="upstream">Gives the client that probes a source through its exit proxy.</param>
    /// <param name="applicationPaths">Application paths used to locate the ffmpeg binary.</param>
    public ChannelHealthCheckService(
        IHttpClientFactory httpClientFactory,
        ILogger<ChannelHealthCheckService> logger,
        SessionManagerService sessionManager,
        UpstreamClientService upstream,
        IApplicationPaths applicationPaths)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _sessionManager = sessionManager;
        _upstream = upstream;
        _applicationPaths = applicationPaths;
    }

    /// <summary>Gets a value indicating whether a health check is running.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Gets the progress of the running (or last finished) health check.</summary>
    /// <returns>Progress snapshot.</returns>
    public TaskProgress GetProgress()
    {
        var p = Volatile.Read(ref _progress);
        return p.Running ? p with { Done = Volatile.Read(ref _done) } : p;
    }

    /// <summary>
    /// Checks health of a single channel. Direct streams use a quick HTTP check;
    /// tokenized streams go through full session resolution + ffmpeg + fallback.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    public async Task<ChannelHealthStatus> CheckChannelAsync(
        string channelName,
        string channelUrl,
        int channelType = 0,
        bool sessionToken = false,
        bool forceRefresh = false,
        string? exitCountry = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channelUrl))
        {
            return new ChannelHealthStatus
            {
                ChannelName = channelName,
                ChannelUrl = channelUrl,
                HttpStatus = 0,
                CheckedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "URL vacía"
            };
        }

        // Check cache
        if (!forceRefresh && _healthCache.TryGetValue(channelUrl, out var cached))
        {
            var age = DateTimeOffset.UtcNow - cached.Item2;
            if (age.TotalMinutes < CACHETTLMINUTES)
            {
                PluginLog.Info($"[HEALTH] Cache hit for {channelName}");
                return cached.Item1;
            }
        }

        // Direct (non-token) streams: quick HTTP check, no session resolution.
        if (channelType is ChannelEntry.DirectType or ChannelEntry.FastType)
        {
            var quick = await QuickCheckWithRetryAsync(channelName, channelUrl, exitCountry, ct).ConfigureAwait(false);
            _healthCache[channelUrl] = (quick, DateTimeOffset.UtcNow);
            return quick;
        }

        // Token de sesión (dura horas): se comprueba con el mismo token compartido que reciben los clientes,
        // sin crear sesión de relay ni gastar otro token del proveedor.
        if (ChannelEntry.IsTokenizedType(channelType) && sessionToken)
        {
            return await CheckSessionTokenSourceAsync(channelName, channelUrl, channelType, ct).ConfigureAwait(false);
        }

        var result = await PerformRealHealthCheckAsync(channelName, channelUrl, channelType, sessionToken, ct).ConfigureAwait(false);
        _healthCache[channelUrl] = (result, DateTimeOffset.UtcNow);
        return result;
    }

    /// <summary>
    /// Checks a session-token source with the shared token clients receive, without a relay session or
    /// another provider token. Also used by the editor before saving a channel.
    /// </summary>
    /// <param name="channelName">Channel name, for the result.</param>
    /// <param name="channelUrl">Gateway URL of the source.</param>
    /// <param name="channelType">Source type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The health of the source.</returns>
    public async Task<ChannelHealthStatus> CheckSessionTokenSourceAsync(string channelName, string channelUrl, int channelType, CancellationToken ct = default)
    {
        var source = new ChannelSource { Url = channelUrl, Type = channelType, SessionToken = true };
        var finalUrl = await SessionTokenDirectCache.TryGetDirectUrlAsync(source, ct).ConfigureAwait(false);
        var status = finalUrl is null
            ? new ChannelHealthStatus
            {
                ChannelName = channelName,
                ChannelUrl = channelUrl,
                HttpStatus = 0,
                CheckedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "No se pudo resolver el token de sesión"
            }
            : await QuickCheckWithRetryAsync(channelName, finalUrl, null, ct).ConfigureAwait(false);
        status.ChannelUrl = channelUrl;
        _healthCache[channelUrl] = (status, DateTimeOffset.UtcNow);
        return status;
    }

    /// <summary>
    /// A channel is not declared dead for one slow answer: timeouts, network errors, 429 and 5xx are
    /// retried once after a short pause (many checks run at the same time and providers hiccup).
    /// </summary>
    private async Task<ChannelHealthStatus> QuickCheckWithRetryAsync(string channelName, string channelUrl, string? exitCountry, CancellationToken ct)
    {
        var result = await QuickHttpCheckAsync(channelName, channelUrl, exitCountry, ct).ConfigureAwait(false);
        var transient = !result.IsAlive && (result.HttpStatus == 0 || result.HttpStatus == 429 || result.HttpStatus >= 500);
        if (!transient)
        {
            return result;
        }

        await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        return await QuickHttpCheckAsync(channelName, channelUrl, exitCountry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Quick HTTP GET check for direct streams. Alive = URL responds 2xx within timeout.
    /// </summary>
    private async Task<ChannelHealthStatus> QuickHttpCheckAsync(string channelName, string channelUrl, string? exitCountry, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ChannelHealthStatus
        {
            ChannelName = channelName,
            ChannelUrl = channelUrl,
            CheckedAt = DateTimeOffset.UtcNow,
            StreamType = channelUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "mpeg2ts"
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(QuickCheckTimeoutSeconds));

            // Una fuente con salida por país se comprueba por el mismo proxy con el que se reproduce: desde aquí,
            // directo, un canal geobloqueado aparecería caído aunque funcione.
            var client = UpstreamClientService.RequiresExit(exitCountry) ? _upstream.For(exitCountry, channelUrl) : _httpClientFactory.CreateClient("JellyTV");
            using var request = new HttpRequestMessage(HttpMethod.Get, channelUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            result.HttpStatus = (int)response.StatusCode;
            result.BytesDownloaded = response.IsSuccessStatusCode ? MINBYTESFORSUCCESS : 0;
            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = $"HTTP {(int)response.StatusCode}";
            }
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = $"Timeout ({QuickCheckTimeoutSeconds}s)";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.InnerException is { } inner && ex is HttpRequestException ? $"{ex.Message} ({inner.Message})" : ex.Message;
        }

        sw.Stop();
        result.DurationMs = (int)sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Tries to read the cached health status for a channel URL.
    /// </summary>
    /// <param name="channelUrl">Channel URL used as cache key.</param>
    /// <param name="status">Receives the cached status when found.</param>
    /// <returns><c>true</c> when a cached status exists.</returns>
    public bool TryGetCachedChannelStatus(string channelUrl, out ChannelHealthStatus status)
    {
        status = default!;

        if (string.IsNullOrWhiteSpace(channelUrl))
        {
            return false;
        }

        if (!_healthCache.TryGetValue(channelUrl, out var cached))
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - cached.Item2;
        if (age.TotalMinutes >= CACHETTLMINUTES)
        {
            return false;
        }

        status = cached.Item1;
        return true;
    }

    /// <summary>
    /// Checks health of all channels in a source by downloading real stream data in parallel (4 channels at a time).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    public async Task<List<ChannelHealthStatus>> CheckSourceChannelsAsync(
        string sourceId,
        List<(string Name, string Url, int Type, bool SessionToken, string? ExitCountry)> channels,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        PluginLog.Info($"[HEALTH] Starting health check for {channels.Count} channels from source {sourceId}");

        var results = new List<ChannelHealthStatus>();

        var enabledProxyCount = Plugin.Instance?.Configuration?.Proxies?.Count(p => p.IsEnabled) ?? 0;
        var maxParallel = Math.Clamp(Math.Max(6, enabledProxyCount), 6, 24);
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);

        PluginLog.Info($"[HEALTH] Parallel checks configured: {maxParallel} (enabled proxies: {enabledProxyCount})");

        try
        {
            var tasks = channels.Select(async ch =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = TryAssumeAliveFromActiveSession(ch.Name, ch.Url, ch.Type)
                        ?? await CheckChannelAsync(ch.Name, ch.Url, ch.Type, ch.SessionToken, forceRefresh, ch.ExitCountry, ct).ConfigureAwait(false);
                    lock (results)
                    {
                        results.Add(result);
                    }
                }
                finally
                {
                    Interlocked.Increment(ref _done);
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PluginLog.Warn($"[HEALTH] Health check cancelled for source {sourceId}");
        }

        var alive = results.Count(r => r.IsAlive);
        var dead = results.Count - alive;
        PluginLog.Info($"[HEALTH] Health check complete: {alive} vivos, {dead} muertos");

        return results;
    }

    /// <summary>
    /// A tokenized channel somebody is watching right now is alive by definition. Probing it would
    /// mint another token and, on providers that allow one per account, cut the viewer off.
    /// </summary>
    private ChannelHealthStatus? TryAssumeAliveFromActiveSession(string name, string url, int type)
    {
        if (!ChannelEntry.IsTokenizedType(type))
        {
            return null;
        }

        var watching = _sessionManager.GetAllSessions().Any(sess =>
            string.Equals(sess.UpstreamUrl, url, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - sess.LastAccessedAt < TimeSpan.FromMinutes(2)
            && !sess.IsInvalid);
        if (!watching)
        {
            return null;
        }

        return new ChannelHealthStatus
        {
            ChannelName = name,
            ChannelUrl = url,
            HttpStatus = 200,
            BytesDownloaded = 200_000,
            CheckedAt = DateTimeOffset.UtcNow,
            StreamType = "session"
        };
    }

    /// <summary>
    /// Checks every enabled source of the given channels and applies the results to each source's
    /// health. Channels with no live source are auto-disabled; a channel that was auto-disabled is
    /// re-enabled as soon as any of its sources comes back online. Channels the user switched off are checked too,
    /// only to record whether they could be switched on again; their state is never changed. Mutates the passed channel list.
    /// </summary>
    /// <param name="label">Name of the run, for the log.</param>
    /// <param name="channels">Channels to check.</param>
    /// <param name="force">Ignore the 30-minute cache for every channel (manual runs).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Channel and source counts; zeros when another check is already running.</returns>
    public async Task<(int ChannelsAlive, int ChannelsDead, int SourcesChecked, int SourcesAlive)> CheckConfiguredChannelsAsync(
        string label,
        IReadOnlyList<ChannelEntry> channels,
        bool force = false,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            return (0, 0, 0, 0);
        }

        var startedAt = DateTimeOffset.UtcNow;
        Volatile.Write(ref _done, 0);
        Volatile.Write(ref _progress, new TaskProgress(true, "Comprobando fuentes", string.Empty, 0, 0, startedAt, null, null));
        try
        {
            var result = await CheckConfiguredCoreAsync(label, channels, force, startedAt, ct).ConfigureAwait(false);
            Volatile.Write(ref _progress, new TaskProgress(false, string.Empty, string.Empty, Volatile.Read(ref _done), Volatile.Read(ref _done), startedAt, DateTimeOffset.UtcNow, $"{result.ChannelsAlive} vivos · {result.ChannelsDead} caídos"));
            return result;
        }
        catch
        {
            Volatile.Write(ref _progress, new TaskProgress(false, string.Empty, string.Empty, Volatile.Read(ref _done), Volatile.Read(ref _done), startedAt, DateTimeOffset.UtcNow, "Interrumpida"));
            throw;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<(int ChannelsAlive, int ChannelsDead, int SourcesChecked, int SourcesAlive)> CheckConfiguredCoreAsync(
        string label,
        IReadOnlyList<ChannelEntry> channels,
        bool force,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        var candidates = channels.ToList();
        // Los canales AutoDisabled (caídos) se re-chequean SIN caché en cada corrida para detectar
        // el regreso cuanto antes; los habilitados respetan la caché (30 min) para no martirizar
        // los upstreams. Una corrida manual lo comprueba todo de nuevo.
        var toForce = new List<(string Name, string Url, int Type, bool SessionToken, string? ExitCountry)>();
        var toCache = new List<(string Name, string Url, int Type, bool SessionToken, string? ExitCountry)>();
        foreach (var ch in candidates)
        {
            ch.EnsureMigratedSources();
            var target = ch.AutoDisabled || force ? toForce : toCache;
            foreach (var s in ch.Sources)
            {
                if (s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url))
                {
                    target.Add((ch.Name, s.Url, s.Type, s.SessionToken, s.ExitCountry));
                }
            }
        }

        Volatile.Write(ref _progress, new TaskProgress(true, "Comprobando fuentes", string.Empty, 0, toCache.Count + toForce.Count, startedAt, null, null));

        var results = new List<ChannelHealthStatus>();
        if (toCache.Count > 0)
        {
            results.AddRange(await CheckSourceChannelsAsync(label, toCache, false, ct).ConfigureAwait(false));
        }

        if (toForce.Count > 0)
        {
            results.AddRange(await CheckSourceChannelsAsync(label, toForce, true, ct).ConfigureAwait(false));
        }

        var resultByUrl = results
            .GroupBy(r => r.ChannelUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int channelsAlive = 0, channelsDead = 0, sourcesChecked = 0, sourcesAlive = 0;
        foreach (var ch in candidates)
        {
            ch.EnsureMigratedSources();
            var enabledSources = ch.Sources.Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
            if (enabledSources.Count == 0)
            {
                continue;
            }

            bool anyAlive = false;
            string? lastError = null;
            DateTimeOffset? lastChecked = null;
            foreach (var s in enabledSources)
            {
                if (!resultByUrl.TryGetValue(s.Url, out var r))
                {
                    continue;
                }

                s.LastCheckedAt = r.CheckedAt;
                s.IsAlive = r.IsAlive;
                s.LastError = r.IsAlive ? null : r.ErrorMessage;
                sourcesChecked++;
                if (r.IsAlive)
                {
                    sourcesAlive++;
                    anyAlive = true;
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(lastError) ? r.ErrorMessage : lastError;
                }

                lastChecked = r.CheckedAt;
            }

            ch.LastCheckedAt = lastChecked;
            ch.LastCheckError = lastError;
            ch.LastCheckAlive = enabledSources.Any(s => s.IsAlive == true) ? true : enabledSources.Any(s => s.IsAlive == false) ? false : null;

            if (anyAlive)
            {
                channelsAlive++;
                if (ch.AutoDisabled)
                {
                    ch.AutoDisabled = false;
                    ch.IsEnabled = true;
                }
            }
            else
            {
                channelsDead++;
                if (ch.IsEnabled)
                {
                    ch.AutoDisabled = true;
                    ch.IsEnabled = false;
                }
            }
        }

        PluginLog.Info($"[HEALTH] {label}: {channelsAlive} vivos / {channelsDead} caidos (fuentes: {sourcesAlive}/{sourcesChecked})");
        Plugin.Instance?.ChannelCatalog?.RecordHealth(candidates);
        return (channelsAlive, channelsDead, sourcesChecked, sourcesAlive);
    }

    /// <summary>
    /// Performs the actual health check using ffmpeg (preferred) or fallback real download.
    /// ffmpeg verifies that the stream can actually start decoding within a short window.
    /// </summary>
    private async Task<ChannelHealthStatus> PerformRealHealthCheckAsync(
        string channelName,
        string channelUrl,
        int channelType,
        bool sessionToken,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ChannelHealthStatus
        {
            ChannelName = channelName,
            ChannelUrl = channelUrl,
            CheckedAt = DateTimeOffset.UtcNow
        };

        try
        {
            // Resolve URL with its own timeout budget.
            using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            resolveCts.CancelAfter(TimeSpan.FromSeconds(RESOLVETIMEOUTSECONDS));

            // Get or create session for this channel (handles token renewal)
            var session = await _sessionManager.GetOrCreateSessionAsync(channelUrl, channelType, sessionToken, null, resolveCts.Token).ConfigureAwait(false);
            if (session?.LastResolved is null)
            {
                result.HttpStatus = 0;
                result.ErrorMessage = "No se pudo resolver la URL";
                return result;
            }

            var resolvedUrl = session.LastResolved.FinalUrl;
            var resolvedStreamType = session.LastResolved.IsHls ? "hls" : "mpeg2ts";

            // Probe with ffmpeg under a dedicated timeout budget.
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(FFMPEGTIMEOUTSECONDS + 3));
            var (ffmpegResult, ffmpegTimedOut) = await TryCheckWithFfmpegAsync(channelName, resolvedUrl, probeCts.Token).ConfigureAwait(false);
            if (ffmpegResult is not null)
            {
                result.HttpStatus = ffmpegResult.HttpStatus;
                result.BytesDownloaded = ffmpegResult.BytesDownloaded;
                result.ErrorMessage = ffmpegResult.ErrorMessage;
                // ffmpeg valido confirma reproduccion real; aqui si es seguro etiquetar tipo.
                result.StreamType = resolvedStreamType;
                return result;
            }

            // Fallback gets a fresh timeout budget so ffmpeg probe time doesn't starve it.
            using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fallbackCts.CancelAfter(TimeSpan.FromSeconds(REQUESTTIMEOUTSECONDS));

            // When ffmpeg timed out, the token is now ~18s old and likely expired.
            // Pre-renew here so the fallback starts with a guaranteed fresh token,
            // eliminating the 401-renew-401 double-round-trip in CheckHlsStreamAsync.
            if (ffmpegTimedOut && ChannelEntry.IsTokenizedType(channelType))
            {
                PluginLog.Info($"[HEALTH] Pre-renewing session after ffmpeg timeout for {channelName}");
                await _sessionManager.RenewSessionAsync(session, fallbackCts.Token).ConfigureAwait(false);
            }

            // Use the (potentially refreshed) final URL for direct-stream fallback.
            var fallbackUrl = session.LastResolved?.FinalUrl ?? resolvedUrl;

            // Fallback: download real data if ffmpeg is unavailable or failed.
            if (session.LastResolved?.IsHls == true)
            {
                result = await CheckHlsStreamAsync(channelName, session, fallbackCts.Token).ConfigureAwait(false);
            }
            else
            {
                result = await CheckDirectStreamAsync(channelName, fallbackUrl, fallbackCts.Token).ConfigureAwait(false);
            }

            result.ChannelName = channelName;
            result.ChannelUrl = channelUrl;
            result.CheckedAt = DateTimeOffset.UtcNow;

            // Nunca etiquetar tipo en checks fallidos para evitar falsos positivos.
            if (!result.IsAlive)
            {
                result.StreamType = "unknown";
            }
            else if (string.IsNullOrWhiteSpace(result.StreamType) ||
                     string.Equals(result.StreamType, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                result.StreamType = resolvedStreamType;
            }
        }
        catch (OperationCanceledException)
        {
            result.HttpStatus = 0;
            result.ErrorMessage = $"Timeout ({REQUESTTIMEOUTSECONDS}s)";
        }
        catch (HttpRequestException ex)
        {
            result.HttpStatus = (int?)ex.StatusCode ?? 0;
            result.ErrorMessage = ex.Message;
            PluginLog.Warn($"[HEALTH] HTTP error for {channelName}: {ex.Message}");
        }
        catch (Exception ex)
        {
            result.HttpStatus = 0;
            result.ErrorMessage = ex.Message;
            PluginLog.Error($"[HEALTH] Unexpected error checking {channelName}", ex);
        }
        finally
        {
            sw.Stop();
            result.DurationMs = (int)sw.ElapsedMilliseconds;
        }

        return result;
    }

    /// <summary>
    /// Attempts to check stream health using ffmpeg.
    /// Returns (null, false) if ffmpeg is unavailable or exits with error (fallback proceeds with current token).
    /// Returns (null, true) if ffmpeg TIMED OUT (token likely expired; caller should pre-renew before fallback).
    /// Returns (status, false) on success.
    /// </summary>
    private async Task<(ChannelHealthStatus? Status, bool TimedOut)> TryCheckWithFfmpegAsync(string channelName, string streamUrl, CancellationToken ct)
    {
        var ffmpeg = await GetFfmpegPathAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return (null, false); // ffmpeg not available
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = BuildFfmpegArgs(streamUrl),
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(processInfo);
            if (process is null)
            {
                return (null, false);
            }

            // Wait for ffmpeg to finish (with timeout)
            var completed = await Task.Run(
                () => process.WaitForExit(FFMPEGTIMEOUTSECONDS * 1000),
                ct).ConfigureAwait(false);

            sw.Stop();

            if (!completed)
            {
                process.Kill();
                PluginLog.Warn($"[HEALTH] ffmpeg timeout channel={channelName} url={streamUrl}");
                // TimedOut=true signals caller to pre-renew (token is now ~18s old, likely expired)
                return (null, true);
            }

            if (process.ExitCode != 0)
            {
                return (null, false); // Not a fatal error, try fallback with current token
            }

            PluginLog.Info($"[HEALTH] ffmpeg OK channel={channelName} in ~{sw.ElapsedMilliseconds}ms for {streamUrl}");
            return (new ChannelHealthStatus
            {
                HttpStatus = 200,
                BytesDownloaded = MINBYTESFORSUCCESS,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, false);
        }
        catch (Exception ex)
        {
            PluginLog.Info($"[HEALTH] ffmpeg check failed channel={channelName}: {ex.Message}");
            return (null, false); // Fallback to real download
        }
    }

    /// <summary>
    /// Builds ffmpeg command-line arguments for a short decode probe.
    /// </summary>
    private static string BuildFfmpegArgs(string streamUrl)
    {
        return $@"-v error -hide_banner -nostats -nostdin -timeout {FFMPEGNETWORKTIMEOUTMICROSECONDS} -i ""{streamUrl}"" -t {FFMPEGPROBEDURATIONSECONDS} -f null -";
    }

    /// <summary>
    /// Finds ffmpeg executable from Jellyfin-managed locations first.
    /// Caches the result to avoid repeated filesystem checks.
    /// </summary>
    private async Task<string?> GetFfmpegPathAsync(CancellationToken ct)
    {
        // Cache check every 1 hour
        if (_ffmpegPath is not null || DateTimeOffset.UtcNow - _lastFfmpegCheck < TimeSpan.FromHours(1))
        {
            return _ffmpegPath;
        }

        _lastFfmpegCheck = DateTimeOffset.UtcNow;

        foreach (var candidate in BuildJellyfinFfmpegCandidates())
        {
            try
            {
                if (File.Exists(candidate))
                {
                    PluginLog.Info($"[HEALTH] Found ffmpeg at: {candidate}");
                    _ffmpegPath = candidate;
                    return _ffmpegPath;
                }
            }
            catch
            {
                // Ignore filesystem errors
            }
        }

        // Last resort only: rely on process PATH if Jellyfin paths were not enough.
        foreach (var candidate in new[] { "ffmpeg", "ffmpeg.exe" })
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(info);
                if (process is null)
                {
                    continue;
                }

                var completed = await Task.Run(() => process.WaitForExit(1500), ct).ConfigureAwait(false);
                if (completed && process.ExitCode == 0)
                {
                    PluginLog.Info($"[HEALTH] Using ffmpeg from PATH as fallback: {candidate}");
                    _ffmpegPath = candidate;
                    return _ffmpegPath;
                }
            }
            catch
            {
                // Ignore and continue fallback probing.
            }
        }

        PluginLog.Warn("[HEALTH] ffmpeg no encontrado en rutas de Jellyfin; se usa fallback por descarga real");
        _ffmpegPath = string.Empty; // Mark as checked but not found
        return null;
    }

    private IEnumerable<string> BuildJellyfinFfmpegCandidates()
    {
        var isWindows = OperatingSystem.IsWindows();
        var exeName = isWindows ? "ffmpeg.exe" : "ffmpeg";
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new List<string>();

        // 1) Binarios junto al servidor Jellyfin.
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            candidates.Add(Path.Combine(baseDir, exeName));
            candidates.Add(Path.Combine(baseDir, "ffmpeg", exeName));
            candidates.Add(Path.Combine(baseDir, "ffmpeg", "bin", exeName));
        }

        // 2) Binarios administrados por Jellyfin dentro de ProgramDataPath.
        if (!string.IsNullOrWhiteSpace(_applicationPaths.ProgramDataPath))
        {
            var ffmpegRoot = Path.Combine(_applicationPaths.ProgramDataPath, "ffmpeg");
            candidates.Add(Path.Combine(ffmpegRoot, exeName));
            candidates.Add(Path.Combine(ffmpegRoot, "bin", exeName));

            try
            {
                if (Directory.Exists(ffmpegRoot))
                {
                    foreach (var dir in Directory.EnumerateDirectories(ffmpegRoot))
                    {
                        candidates.Add(Path.Combine(dir, exeName));
                        candidates.Add(Path.Combine(dir, "bin", exeName));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No se pudo enumerar directorios de ffmpeg en ProgramDataPath");
            }
        }

        return candidates
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads HLS playlist and first segment to verify the stream is working.
    /// </summary>
    private async Task<ChannelHealthStatus> CheckHlsStreamAsync(
        string channelName,
        StreamSession session,
        CancellationToken ct)
    {
        var result = new ChannelHealthStatus { StreamType = "unknown" };
        // Set to true after a renewal so attempt=2 uses a lightweight playlist-only check.
        // Segment downloads fail without the original proxy/cookie context used during resolution;
        // a fresh m3u8 with live segments is sufficient proof the channel is streaming.
        bool isPostRenewal = false;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var resolved = session.LastResolved;
                if (resolved is null)
                {
                    result.HttpStatus = 0;
                    result.ErrorMessage = "Sesion sin URL resuelta";
                    return result;
                }

                var client = _httpClientFactory.CreateClient("JellyTV");
                var playlistUrl = resolved.FinalUrl;
                var playlistBaseUrl = resolved.BaseUrl;

                // 1. Download playlist (usually small, ~5-10 KB)
                using var playlistResp = await client.GetAsync(playlistUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!playlistResp.IsSuccessStatusCode)
                {
                    if (ChannelEntry.IsTokenizedType(session.ChannelType)
                        && attempt == 1 && (int)playlistResp.StatusCode is 401 or 403)
                    {
                        PluginLog.Warn($"[HEALTH] Playlist auth {(int)playlistResp.StatusCode} channel={channelName}; renewing session and retrying once");
                        await _sessionManager.RenewSessionAsync(session, ct).ConfigureAwait(false);
                        continue;
                    }

                    result.HttpStatus = (int)playlistResp.StatusCode;
                    result.ErrorMessage = $"Playlist HTTP {(int)playlistResp.StatusCode}";
                    return result;
                }

                var playlistContent = await playlistResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                result.BytesDownloaded = Encoding.UTF8.GetByteCount(playlistContent);

                // Master playlist: pick best variant first (same strategy as previous broadcast path).
                if (playlistContent.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
                {
                    SourceCodecs.RecordFromMaster(session.UpstreamUrl, playlistContent);
                    var variantUrl = ParseBestVariantUrl(playlistContent, playlistBaseUrl);
                    if (!string.IsNullOrWhiteSpace(variantUrl))
                    {
                        using var variantResp = await client.GetAsync(variantUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (!variantResp.IsSuccessStatusCode)
                        {
                            if (ChannelEntry.IsTokenizedType(session.ChannelType)
                                && attempt == 1 && (int)variantResp.StatusCode is 401 or 403)
                            {
                                PluginLog.Warn($"[HEALTH] Variant auth {(int)variantResp.StatusCode} channel={channelName}; renewing session and retrying once");
                                await _sessionManager.RenewSessionAsync(session, ct).ConfigureAwait(false);
                                continue;
                            }

                            result.HttpStatus = (int)variantResp.StatusCode;
                            result.ErrorMessage = $"Variant HTTP {(int)variantResp.StatusCode}";
                            return result;
                        }

                        playlistContent = await variantResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        result.BytesDownloaded += Encoding.UTF8.GetByteCount(playlistContent);
                        playlistBaseUrl = BuildBaseUrl(variantUrl);
                    }
                }

                PlaylistWindows.Record(session.UpstreamUrl, playlistContent);

                // 2. Extract and try multiple segments (first segment can already be stale in live HLS).
                var segmentUrls = ExtractHlsSegments(playlistContent, playlistBaseUrl, maxCount: 5);
                if (segmentUrls.Count == 0)
                {
                    result.HttpStatus = 200;
                    result.ErrorMessage = "No se encontraron segmentos en la playlist";
                    return result;
                }

                // Post-renewal lightweight check: a fresh m3u8 with segments present means the
                // channel is actively streaming. We skip segment download because the health-check
                // HttpClient lacks the proxy/cookie context used during resolution, which causes
                // false 401s on segment requests even with a valid fresh token.
                if (isPostRenewal)
                {
                    result.HttpStatus = 200;
                    result.BytesDownloaded = MINBYTESFORSUCCESS;
                    result.StreamType = "hls";
                    PluginLog.Info($"[HEALTH] HLS playlist OK channel={channelName}: {segmentUrls.Count} segments found (post-renewal lightweight check)");
                    return result;
                }

                var lastSegmentStatus = 0;
                var mustRenew = false;

                foreach (var rawSegmentUrl in segmentUrls)
                {
                    // Handle beacon/tracking wrappers: .../beacon?...&redirect_url=https%3A%2F%2Freal-segment.ts
                    var segmentUrl = ExtractRealSegmentUrl(rawSegmentUrl);

                    using var segmentResp = await client.GetAsync(segmentUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!segmentResp.IsSuccessStatusCode)
                    {
                        lastSegmentStatus = (int)segmentResp.StatusCode;

                        // Tokenized streams may require one forced renewal between checks.
                        if (ChannelEntry.IsTokenizedType(session.ChannelType)
                            && attempt == 1 && lastSegmentStatus is 401 or 403)
                        {
                            PluginLog.Warn($"[HEALTH] Segment auth {lastSegmentStatus} channel={channelName}; renewing session and retrying once");
                            mustRenew = true;
                            break;
                        }

                        // For 404 on live playlists, try next segment before failing the channel.
                        if (lastSegmentStatus == 404)
                        {
                            continue;
                        }

                        continue;
                    }

                    long segmentBytes = 0;
                    using var stream = await segmentResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var buffer = new byte[64 * 1024]; // 64 KB chunks
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                    {
                        segmentBytes += read;
                        if (segmentBytes >= MAXBYTESTODOWNLOAD)
                        {
                            break;
                        }
                    }

                    result.HttpStatus = 200;
                    result.BytesDownloaded += segmentBytes;
                    result.StreamType = "hls";
                    PluginLog.Info($"[HEALTH] HLS check OK channel={channelName}: {result.BytesDownloaded} bytes");
                    return result;
                }

                if (mustRenew)
                {
                    await _sessionManager.RenewSessionAsync(session, ct).ConfigureAwait(false);
                    isPostRenewal = true;
                    continue;
                }

                result.HttpStatus = lastSegmentStatus == 0 ? 0 : lastSegmentStatus;
                result.ErrorMessage = lastSegmentStatus == 0
                    ? "No se pudo descargar un segmento valido"
                    : $"Segmento HTTP {lastSegmentStatus}";
                return result;
            }
            catch (Exception ex)
            {
                result.HttpStatus = 0;
                result.ErrorMessage = ex.Message;
                if (attempt == 2)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private static string ExtractRealSegmentUrl(string url)
    {
        const string marker = "redirect_url=";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return url;
        }

        var start = idx + marker.Length;
        var end = url.IndexOf('&', start);
        var encoded = end < 0 ? url[start..] : url[start..end];

        try
        {
            var decoded = Uri.UnescapeDataString(encoded);
            if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return decoded;
            }
        }
        catch
        {
            // Keep original URL when malformed.
        }

        return url;
    }

    private static string? ParseBestVariantUrl(string masterText, string baseUrl)
    {
        string? bestUrl = null;
        long bestBw = -1;
        int bestTier = int.MaxValue;

        using var reader = new StringReader(masterText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                continue;
            }

            var bw = ParseBandwidth(line);
            var tier = GetCodecTier(ParseCodecs(line));
            var uriLine = reader.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(uriLine))
            {
                continue;
            }

            if (tier < bestTier || (tier == bestTier && bw > bestBw))
            {
                bestTier = tier;
                bestBw = bw;
                bestUrl = uriLine.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? uriLine
                    : new Uri(new Uri(baseUrl), uriLine).ToString();
            }
        }

        return bestUrl;
    }

    private static string? ParseCodecs(string streamInfLine)
    {
        const string marker = "CODECS=\"";
        var idx = streamInfLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + marker.Length;
        var end = streamInfLine.IndexOf('"', start);
        return end < 0 ? streamInfLine[start..] : streamInfLine[start..end];
    }

    private static int GetCodecTier(string? codecs)
    {
        if (string.IsNullOrEmpty(codecs))
        {
            return 2;
        }

        var lower = codecs.ToLowerInvariant();
        if (lower.Contains("hvc1", StringComparison.Ordinal) || lower.Contains("hev1", StringComparison.Ordinal) ||
            lower.Contains("dvh1", StringComparison.Ordinal) || lower.Contains("dvhe", StringComparison.Ordinal) ||
            lower.Contains("av01", StringComparison.Ordinal) || lower.Contains("vp09", StringComparison.Ordinal))
        {
            return 3;
        }

        var isAvc = lower.Contains("avc1", StringComparison.Ordinal) || lower.Contains("avc2", StringComparison.Ordinal);
        if (isAvc && lower.Contains("mp4a", StringComparison.Ordinal))
        {
            return 0;
        }

        if (isAvc)
        {
            return 1;
        }

        return 2;
    }

    private static long ParseBandwidth(string streamInfLine)
    {
        const string marker = "BANDWIDTH=";
        var idx = streamInfLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return 0;
        }

        var start = idx + marker.Length;
        var end = start;
        while (end < streamInfLine.Length && char.IsDigit(streamInfLine[end]))
        {
            end++;
        }

        return long.TryParse(streamInfLine[start..end], out var bw) ? bw : 0L;
    }

    private static string BuildBaseUrl(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        var path = q >= 0 ? url[..q] : url;
        var last = path.LastIndexOf('/');
        return last > 8 ? path[..(last + 1)] : path;
    }

    /// <summary>
    /// Downloads first 2-5 MB of a direct stream (MPEG-TS or similar) to verify it's working.
    /// </summary>
    private async Task<ChannelHealthStatus> CheckDirectStreamAsync(
        string channelName,
        string streamUrl,
        CancellationToken ct)
    {
        var result = new ChannelHealthStatus { StreamType = "unknown" };

        try
        {
            var client = _httpClientFactory.CreateClient("JellyTV");

            using var resp = await client.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                result.HttpStatus = (int)resp.StatusCode;
                result.ErrorMessage = $"Stream HTTP {(int)resp.StatusCode}";
                return result;
            }

            long bytesDownloaded = 0;
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[128 * 1024]; // 128 KB chunks
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                bytesDownloaded += read;
                if (bytesDownloaded >= MAXBYTESTODOWNLOAD)
                {
                    break;
                }
            }

            result.HttpStatus = 200;
            result.BytesDownloaded = bytesDownloaded;
            result.StreamType = "mpeg2ts";
            PluginLog.Info($"[HEALTH] Direct stream check OK channel={channelName}: {bytesDownloaded} bytes");
        }
        catch (Exception ex)
        {
            result.HttpStatus = 0;
            result.ErrorMessage = ex.Message;
            throw;
        }

        return result;
    }

    /// <summary>
    /// Extracts up to <paramref name="maxCount"/> segment URLs from an HLS playlist.
    /// Resolves relative URLs to absolute using the base URL.
    /// </summary>
    private static List<string> ExtractHlsSegments(string playlistContent, string baseUrl, int maxCount)
    {
        var segments = new List<string>(maxCount);

        foreach (var line in playlistContent.Split(NewLineSeparators, StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            // Found a segment URL
            var segmentUrl = trimmed;
            if (!segmentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Relative URL — resolve against base
                if (!baseUrl.EndsWith('/'))
                {
                    baseUrl += "/";
                }

                segmentUrl = baseUrl + segmentUrl.TrimStart('/');
            }

            segments.Add(segmentUrl);
            if (segments.Count >= maxCount)
            {
                break;
            }
        }

        return segments;
    }

    /// <summary>
    /// Clears all cached health check results.
    /// </summary>
    public void ClearCache()
    {
        _healthCache.Clear();
        PluginLog.Info("[HEALTH] Health check cache cleared");
    }

    /// <summary>
    /// Returns count of cached entries (for diagnostics).
    /// </summary>
    /// <returns></returns>
    public int GetCacheSize() => _healthCache.Count;
}
