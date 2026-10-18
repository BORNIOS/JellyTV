using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public sealed class SlidingWindowTmdbRateLimiter : ITmdbRateLimiter
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _hits = new();
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private int _waitEvents;

    public SlidingWindowTmdbRateLimiter(int maxRequests = 40, TimeSpan? window = null)
    {
        _maxRequests = maxRequests;
        _window = window ?? TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc/>
    public int WaitEvents => _waitEvents;

    /// <inheritdoc/>
    public async Task WaitTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan? wait = null;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                while (_hits.Count > 0 && now - _hits.Peek() >= _window)
                {
                    _hits.Dequeue();
                }

                if (_hits.Count < _maxRequests)
                {
                    _hits.Enqueue(now);
                    return;
                }

                var oldest = _hits.Peek();
                wait = _window - (now - oldest);
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }
            }

            Interlocked.Increment(ref _waitEvents);
            await Task.Delay(wait.Value, ct).ConfigureAwait(false);
        }
    }
}
