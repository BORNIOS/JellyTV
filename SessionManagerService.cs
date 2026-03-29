using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IptvProxy.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IptvProxy.Services;

/// <summary>
/// Manages active stream sessions and handles proactive token renewal
/// before expiry to prevent playback interruptions.
/// </summary>
public class SessionManagerService : BackgroundService
{
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly RedirectResolverService _resolver;
    private readonly ILogger<SessionManagerService> _logger;

    // Sessions inactive longer than this are cleaned up
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionManagerService"/> class.
    /// </summary>
    public SessionManagerService(
        RedirectResolverService resolver,
        ILogger<SessionManagerService> logger)
    {
        _resolver = resolver;
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
            session.LastResolved = await _resolver.ResolveAsync(session.UpstreamUrl, ct).ConfigureAwait(false);
            _logger.LogInformation("[IptvProxy] Token renewed for session {Id}. New URL: {Url}",
                session.SessionId, session.LastResolved.FinalUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IptvProxy] Token renewal failed for session {Id}", session.SessionId);
            throw;
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

            // Proactively renew tokens that are about to expire
            if (NeedsRenewal(session))
            {
                try
                {
                    await RenewSessionAsync(session, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IptvProxy] Background renewal failed for session {Id}", session.SessionId);
                }
            }
        }
    }
}
