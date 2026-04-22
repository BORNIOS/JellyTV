using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JellyTV.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyTV.Services;

/// <summary>
/// Manages active stream sessions and handles proactive token renewal
/// before expiry to prevent playback interruptions.
/// </summary>
public class SessionManagerService : BackgroundService
{
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _resolveFailures = new();
    private readonly RedirectResolverService _resolver;
    private readonly ILogger<SessionManagerService> _logger;

    // Sessions inactive longer than this are cleaned up
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);

    // After a non-auth upstream failure (400, 5xx), wait this long before retrying
    private static readonly TimeSpan ResolveFailureBackoff = TimeSpan.FromSeconds(15);

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

        // Reject immediately if the upstream recently failed (non-auth) to avoid spamming the provider.
        if (_resolveFailures.TryGetValue(key, out var failedAt) &&
            DateTimeOffset.UtcNow - failedAt < ResolveFailureBackoff)
        {
            var remaining = (int)(ResolveFailureBackoff - (DateTimeOffset.UtcNow - failedAt)).TotalSeconds;
            _logger.LogWarning("[JellyTV] Upstream {Url} in failure backoff ({Sec}s remaining), skipping resolve", upstreamUrl, remaining);
            throw new HttpRequestException($"Upstream recently failed — backoff {remaining}s remaining.",
                null, System.Net.HttpStatusCode.BadGateway);
        }

        if (_sessions.TryGetValue(key, out var existing))
        {
            // Validate the cached session's UpstreamUrl matches the requested key.
            // If they differ the session was corrupted by the old code that overwrote UpstreamUrl
            // with an intermediate hop (e.g. alfa.7057460.xyz). Evict and re-create.
            if (!string.Equals(existing.UpstreamUrl, key, StringComparison.Ordinal))
            {
                _logger.LogWarning("[JellyTV] Evicting corrupted session {Id} (UpstreamUrl={Bad} != key={Key})",
                    existing.SessionId, existing.UpstreamUrl, key);
                _sessions.TryRemove(key, out _);
            }
            else
            {
                existing.LastAccessedAt = DateTimeOffset.UtcNow;
                _logger.LogDebug("[JellyTV] Reusing session {Id} for {Url}", existing.SessionId, upstreamUrl);
                return existing;
            }
        }

        var session = new StreamSession { UpstreamUrl = upstreamUrl };
        try
        {
            session.LastResolved = await _resolver.ResolveAsync(upstreamUrl, ct).ConfigureAwait(false);
            _resolveFailures.TryRemove(key, out _); // clear any previous failure on success
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is not (401 or 403))
        {
            // Non-auth failure (400, 5xx, network error) — record backoff so rapid retries don't spam upstream.
            _resolveFailures[key] = DateTimeOffset.UtcNow;
            _logger.LogWarning("[JellyTV] Resolve failed for {Url} (status={Status}), backoff {Sec}s",
                upstreamUrl, (int?)ex.StatusCode, (int)ResolveFailureBackoff.TotalSeconds);
            throw;
        }

        _sessions[key] = session;
        _logger.LogInformation("[JellyTV] Created session {Id} for {Url}", session.SessionId, upstreamUrl);
        return session;
    }

    /// <summary>
    /// Returns all active sessions for diagnostics.
    /// </summary>
    public StreamSession[] GetAllSessions() => [.. _sessions.Values];

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
            _logger.LogDebug("[JellyTV] Renewal already in progress for session {Id}", session.SessionId);
            return;
        }

        // Guard: if this upstream recently failed, skip the HTTP call entirely
        // so PollOnceAsync doesn't hammer the provider on a 4-5s ErrorBackoff loop.
        var url = session.UpstreamUrl;
        if (_resolveFailures.TryGetValue(url, out var failedAt) &&
            DateTimeOffset.UtcNow - failedAt < ResolveFailureBackoff)
        {
            var remaining = (int)(ResolveFailureBackoff - (DateTimeOffset.UtcNow - failedAt)).TotalSeconds;
            _logger.LogWarning("[JellyTV] Session {Id} upstream in failure backoff ({Sec}s remaining), skipping renewal",
                session.SessionId, remaining);
            return;
        }

        session.IsRenewing = true;
        try
        {
            _logger.LogInformation("[JellyTV] Renewing token for session {Id} upstream={Url}",
                session.SessionId, session.UpstreamUrl);

            HttpRequestException? lastAuthException = null;
            Exception? lastException = null;

            foreach (var candidate in BuildRenewalCandidates(session))
            {
                try
                {
                    var resolved = await _resolver.ResolveAsync(candidate, ct).ConfigureAwait(false);
                    session.LastResolved = resolved;

                    _resolveFailures.TryRemove(session.UpstreamUrl, out _);
                    _logger.LogInformation("[JellyTV] Token renewed for session {Id}. Seed={Seed} Final={Url}",
                        session.SessionId, candidate, resolved.FinalUrl);
                    return;
                }
                catch (HttpRequestException ex) when ((int?)ex.StatusCode is 401 or 403)
                {
                    lastAuthException = ex;
                    _logger.LogWarning("[JellyTV] Renewal auth-failed for session {Id} using seed {Seed} (status={Status})",
                        session.SessionId, candidate, (int?)ex.StatusCode);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "[JellyTV] Renewal attempt failed for session {Id} using seed {Seed}",
                        session.SessionId, candidate);
                }
            }

            // If every attempt failed with auth errors, apply backoff and do not throw.
            // Throwing here creates tight exception loops in BroadcastPump and floods logs.
            if (lastAuthException is not null && lastException is null)
            {
                _resolveFailures[session.UpstreamUrl] = DateTimeOffset.UtcNow;
                _logger.LogWarning("[JellyTV] Renewal exhausted auth candidates for session {Id}; entering backoff {Sec}s",
                    session.SessionId, (int)ResolveFailureBackoff.TotalSeconds);
                return;
            }

            if (lastException is not null)
            {
                throw lastException;
            }

            // Defensive fallback (no candidate succeeded but no explicit exception captured).
            _resolveFailures[session.UpstreamUrl] = DateTimeOffset.UtcNow;
            _logger.LogWarning("[JellyTV] Renewal produced no result for session {Id}; entering backoff {Sec}s",
                session.SessionId, (int)ResolveFailureBackoff.TotalSeconds);
            return;
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is not (401 or 403))
        {
            // Non-auth failure — record backoff to prevent tight retry loops.
            _resolveFailures[session.UpstreamUrl] = DateTimeOffset.UtcNow;
            _logger.LogError(ex, "[JellyTV] Token renewal failed for session {Id} (status={Status}), backoff {Sec}s",
                session.SessionId, (int?)ex.StatusCode, (int)ResolveFailureBackoff.TotalSeconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JellyTV] Token renewal failed for session {Id}", session.SessionId);
            throw;
        }
        finally
        {
            session.IsRenewing = false;
        }
    }

    /// <summary>
    /// Returns remaining backoff time for a session upstream if currently throttled.
    /// </summary>
    public bool TryGetRenewalBackoffRemaining(StreamSession session, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;

        if (!_resolveFailures.TryGetValue(session.UpstreamUrl, out var failedAt))
        {
            return false;
        }

        var elapsed = DateTimeOffset.UtcNow - failedAt;
        if (elapsed >= ResolveFailureBackoff)
        {
            return false;
        }

        remaining = ResolveFailureBackoff - elapsed;
        return true;
    }

    private static bool IsLikelyTokenizedUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("token=", StringComparison.OrdinalIgnoreCase)
               || url.Contains("access_token=", StringComparison.OrdinalIgnoreCase)
               || url.Contains("/hlsr/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildRenewalCandidates(StreamSession session)
    {
        // Only the stable origin URL. Chain hops (e.g. api.nerdtv, alfa) all route
        // through the same token router and get the same cached/stale token on rapid
        // sequential requests. Trying them adds HTTP requests without improving the
        // recovery rate — and the burst triggers stricter CDN rate-limiting, extending
        // the outage from ~2 s (single candidate) to 5+ s and risking a temporary ban.
        yield return session.UpstreamUrl;
    }

    /// <summary>
    /// Checks whether the session's token needs renewal based on configured margin.
    /// </summary>
    public bool NeedsRenewal(StreamSession session)
    {
        var resolved = session.LastResolved;
        if (resolved is null) return true;

        var marginSeconds = Plugin.Instance?.Configuration.TokenRefreshMarginSeconds ?? 30;
        // For short-lived rotating HLS tokens, a large margin causes aggressive early renewals.
        // Cap the proactive margin unless the user explicitly lowers it in config.
        if (!string.IsNullOrWhiteSpace(resolved.TokenValue) && marginSeconds > 12)
        {
            marginSeconds = 12;
        }

        // If we have an explicit expiry, use it
        if (resolved.TokenExpiry.HasValue)
        {
            return DateTimeOffset.UtcNow >= resolved.TokenExpiry.Value - TimeSpan.FromSeconds(marginSeconds);
        }

        // No expiry metadata:
        // - Streams without token should not be renewed proactively.
        // - Tokenized streams will renew reactively on 401/403 and/or via broadcast-estimated expiry.
        return false;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[JellyTV] Session manager background service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

            await ProcessSessionsAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("[JellyTV] Session manager background service stopped.");
    }

    private Task ProcessSessionsAsync()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _sessions)
        {
            var session = kvp.Value;

            // Remove stale sessions (no one has accessed them in SessionTimeout).
            if (now - session.LastAccessedAt > SessionTimeout)
            {
                _sessions.TryRemove(kvp.Key, out _);
                _logger.LogInformation("[JellyTV] Cleaned up idle session {Id}", session.SessionId);
            }

            // NOTE: Token renewal is handled on-demand in two places:
            //   1. JellyTVController.GetStream  — when a channel is opened.
            //   2. BroadcastPump.PollOnceAsync  — before every playlist fetch while streaming.
            // We intentionally do NOT proactively renew here because doing so would
            // keep hitting the upstream provider for sessions that are idle between
            // plays, and would trigger renewal for every session every 5 minutes
            // regardless of whether anyone is actually watching.
        }

        return Task.CompletedTask;
    }
}