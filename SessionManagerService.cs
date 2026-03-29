using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IptvProxy.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.IptvProxy.Services;

/// <summary>
/// Manages active stream sessions and handles proactive token renewal
/// before expiry to prevent playback interruptions.
/// </summary>
public class SessionManagerService : BackgroundService
{
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly RedirectResolverService _resolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SessionManagerService> _logger;

    // Sessions inactive longer than this are cleaned up
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionManagerService"/> class.
    /// </summary>
    public SessionManagerService(
        RedirectResolverService resolver,
        IHttpClientFactory httpClientFactory,
        ILogger<SessionManagerService> logger)
    {
        _resolver = resolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Creates or retrieves a session for the given upstream URL.
    /// Follows redirects on creation to obtain the initial resolved stream.
    /// </summary>
    public async Task<StreamSession> GetOrCreateSessionAsync(string upstreamUrl, CancellationToken ct = default)
    {
        // Use URL as key so the same channel reuses its session
        var key = upstreamUrl;

        if (_sessions.TryGetValue(key, out var existing))
        {
            existing.LastAccessedAt = DateTimeOffset.UtcNow;
            _logger.LogDebug("[IptvProxy] Reusing session {Id} for {Url}", existing.SessionId, upstreamUrl);
            return existing;
        }

        var session = new StreamSession { UpstreamUrl = upstreamUrl };
        session.LastResolved = await _resolver.ResolveAsync(upstreamUrl, ct).ConfigureAwait(false);

        _sessions[key] = session;
        _logger.LogInformation("[IptvProxy] Created session {Id} for {Url}", session.SessionId, upstreamUrl);

        // Start prefetch/concat worker if configured
        try
        {
            var cfg = Plugin.Instance?.Configuration as Jellyfin.Plugin.IptvProxy.Configuration.PluginConfiguration;
            if (cfg is not null && cfg.EnableConcatMode)
            {
                // Setup concat directory
                var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "iptv_concat", session.SessionId);
                System.IO.Directory.CreateDirectory(dir);
                session.ConcatDirectory = dir;
                session.CombinedFilePath = System.IO.Path.Combine(dir, "combined.ts");
                session.PrefetchCts = new System.Threading.CancellationTokenSource();
                var token = session.PrefetchCts.Token;
                // Start background prefetch task
                session.PrefetchTask = Task.Run(() => PrefetchAndConcatLoop(session, token), token);
                _logger.LogInformation("[IptvProxy] Started concat prefetch worker for session {Id}", session.SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IptvProxy] Could not start concat worker for session {Id}", session.SessionId);
        }

        return session;
    }

    /// <summary>
    /// Retrieves a session by its unique session ID.
    /// </summary>
    public StreamSession? GetSessionById(string sessionId)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.SessionId == sessionId)
            {
                session.LastAccessedAt = DateTimeOffset.UtcNow;
                return session;
            }
        }
        return null;
    }

    /// <summary>
    /// Forces an immediate token renewal for the given session.
    /// Thread-safe: only one renewal runs at a time per session.
    /// </summary>
    public async Task RenewSessionAsync(StreamSession session, CancellationToken ct = default)
    {
        if (session.IsRenewing)
        {
            _logger.LogDebug("[IptvProxy] Renewal already in progress for session {Id}", session.SessionId);
            return;
        }

        session.IsRenewing = true;
        try
        {
            _logger.LogInformation("[IptvProxy] Renewing token for session {Id}", session.SessionId);
            // Prefer resolving from the last known final URL (the actual provider),
            // because the original upstream URL may merely redirect and renewal
            // needs to happen against the provider that issues tokens (e.g., nerdtv).
            var startUrl = session.LastResolved?.FinalUrl ?? session.UpstreamUrl;
            _logger.LogDebug("[IptvProxy] Renewal will start from: {Url}", startUrl);
            var resolved = await _resolver.ResolveAsync(startUrl, ct).ConfigureAwait(false);
            session.LastResolved = resolved;
            _logger.LogInformation("[IptvProxy] Token renewed for session {Id}. New URL: {Url}",
                session.SessionId, session.LastResolved.FinalUrl);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden || ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Upstream denied the renewal attempt (403/401). Keep the existing LastResolved and continue.
            _logger.LogWarning(ex, "[IptvProxy] Renewal returned {Status} for session {Id}; keeping existing URL.", ex.StatusCode, session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IptvProxy] Token renewal failed for session {Id}", session.SessionId);
            // Do not rethrow — background renewals should not crash the service.
        }
        finally
        {
            session.IsRenewing = false;
        }
    }

    /// <summary>
    /// Checks whether the session's token needs renewal based on configured margin.
    /// </summary>
    public bool NeedsRenewal(StreamSession session)
    {
        var resolved = session.LastResolved;
        if (resolved is null) return true;

        var marginSeconds = Plugin.Instance?.Configuration.TokenRefreshMarginSeconds ?? 30;

        // If we have an explicit expiry, use it
        if (resolved.TokenExpiry.HasValue)
        {
            return DateTimeOffset.UtcNow >= resolved.TokenExpiry.Value - TimeSpan.FromSeconds(marginSeconds);
        }

        // Fallback: refresh every 5 minutes if no expiry information
        return DateTimeOffset.UtcNow >= resolved.ResolvedAt + TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[IptvProxy] Session manager background service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

            await ProcessSessionsAsync(stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("[IptvProxy] Session manager background service stopped.");
    }

    private async Task ProcessSessionsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _sessions)
        {
            var session = kvp.Value;

            // Remove stale sessions
            if (now - session.LastAccessedAt > SessionTimeout)
            {
                _sessions.TryRemove(kvp.Key, out _);
                _logger.LogInformation("[IptvProxy] Cleaned up idle session {Id}", session.SessionId);
                continue;
            }

            // NOTE: Background proactive renewal disabled.
            // We rely on on-demand renewal when the client encounters 401/403 while fetching segments
            // (see ProxySegmentAsync which calls Sessions.RenewSessionAsync on first token rejection).
            // This avoids forcing the provider to renew tokens from the redirector URL and prevents
            // unnecessary renewal attempts against services that block automated refreshes.
        }
    }

    private async Task PrefetchAndConcatLoop(StreamSession session, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration as Jellyfin.Plugin.IptvProxy.Configuration.PluginConfiguration;
        if (cfg is null) return;

        var client = _httpClientFactory.CreateClient("IptvProxy");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Fetch playlist
                var playlistUrl = session.LastResolved?.FinalUrl ?? session.UpstreamUrl;
                if (string.IsNullOrEmpty(playlistUrl))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
                }

                var playlist = string.Empty;
                try
                {
                    playlist = await client.GetStringAsync(playlistUrl, ct).ConfigureAwait(false);
                }
                catch
                {
                    // ignore and retry later
                }

                if (!string.IsNullOrEmpty(playlist))
                {
                    using var reader = new StringReader(playlist);
                    string? line;
                    while ((line = reader.ReadLine()) is not null && !ct.IsCancellationRequested)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                        var segUrl = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? line
                            : new Uri(new Uri(playlistUrl), line).ToString();

                        if (session.DownloadedSegments.ContainsKey(segUrl)) continue;

                        // Download segment
                        try
                        {
                            var segResp = await client.GetAsync(segUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            if (segResp.IsSuccessStatusCode)
                            {
                                var fileName = Path.GetFileName(new Uri(segUrl).AbsolutePath);
                                var path = Path.Combine(session.ConcatDirectory!, fileName);
                                await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    await (await segResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false)).CopyToAsync(fs, ct).ConfigureAwait(false);
                                }
                                session.DownloadedSegments[segUrl] = true;
                                _logger.LogInformation("[IptvProxy] Downloaded segment for session {Id}: {File}", session.SessionId, fileName);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[IptvProxy] Failed to download segment {Url}", segUrl);
                        }
                    }

                    // Build concat list and run ffmpeg
                    try
                    {
                        var listPath = Path.Combine(session.ConcatDirectory!, "list.txt");
                        var files = Directory.GetFiles(session.ConcatDirectory!).Where(f => f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList();
                        if (files.Count > 0)
                        {
                            await File.WriteAllLinesAsync(listPath, files.Select(f => $"file '{f.Replace("'","'\\''")}"));
                            var outTmp = session.CombinedFilePath! + ".tmp";
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = cfg.FfmpegPath ?? "ffmpeg",
                                Arguments = $"-y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outTmp}\"",
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var proc = System.Diagnostics.Process.Start(psi);
                            if (proc is not null)
                            {
                                var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                                if (proc.ExitCode == 0)
                                {
                                    File.Move(outTmp, session.CombinedFilePath!, true);
                                    _logger.LogInformation("[IptvProxy] Concat produced for session {Id}", session.SessionId);
                                }
                                else
                                {
                                    _logger.LogWarning("[IptvProxy] ffmpeg exit {Code} for session {Id}: {Err}", proc.ExitCode, session.SessionId, stderr);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[IptvProxy] Concat failure for session {Id}", session.SessionId);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(cfg.ConcatIntervalSeconds), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[IptvProxy] Prefetch loop error for session {Id}", session.SessionId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }
}
