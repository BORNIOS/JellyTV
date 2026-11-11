using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Small per-session cache of the newest segments. The first download happens while the token is
/// still fresh (prefetch), every viewer of the channel is served from memory afterwards, and a
/// provider that allows one connection per account sees a single upstream request per segment.
/// All caches of the process share one memory budget: past it, segments are simply not kept and the
/// viewer streams them straight from the provider.
/// </summary>
public sealed class SegmentCache
{
    /// <summary>Largest segment that is kept in memory.</summary>
    public const int MaxSegmentBytes = 16 * 1024 * 1024;

    /// <summary>Memory budget shared by every channel.</summary>
    public const long GlobalMaxBytes = 512L * 1024 * 1024;

    // Una ventana con audio aparte y una clave AES por segmento son ~20 objetos (5 vídeo + 5 audio + claves): con 8 se expulsaban antes de usarse.
    private const int MaxEntries = 24;
    private const long MaxSessionBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(3);

    private static long globalBytes;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the bytes currently held by every cache in the process.
    /// </summary>
    public static long GlobalBytes => Interlocked.Read(ref globalBytes);

    /// <summary>
    /// Waits for the cached (or in-flight) segment, if there is one.
    /// </summary>
    /// <param name="url">Upstream segment URL.</param>
    /// <returns>The segment, or <c>null</c> when it is not cached or its download failed.</returns>
    public async Task<CachedSegment?> TryGetAsync(string url)
    {
        if (!_entries.TryGetValue(url, out var entry))
        {
            return null;
        }

        try
        {
            return await entry.Download.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Starts downloading a segment unless it is already cached or in flight.
    /// </summary>
    /// <param name="url">Upstream segment URL.</param>
    /// <param name="download">Downloader; returns <c>null</c> on failure.</param>
    public void Prefetch(string url, Func<Task<CachedSegment?>> download)
    {
        // Comprobar antes de descargar: crear la Entry ejecuta download() y, si ya estaba, esa descarga se tiraba (otra ida al proveedor).
        if (_entries.ContainsKey(url))
        {
            return;
        }

        var created = new Entry(DateTimeOffset.UtcNow, download());
        if (!_entries.TryAdd(url, created))
        {
            return;
        }

        _ = created.Download.ContinueWith(
            task =>
            {
                if (task.IsFaulted || task.IsCanceled || task.Result is null || !TryAccount(created, task.Result))
                {
                    _entries.TryRemove(new KeyValuePair<string, Entry>(url, created));
                }
                else
                {
                    Trim();
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Drops everything this cache holds and gives the memory back to the shared budget.
    /// </summary>
    public void Clear()
    {
        foreach (var key in _entries.Keys.ToList())
        {
            Remove(key);
        }
    }

    private static bool TryAccount(Entry entry, CachedSegment segment)
    {
        var size = segment.Data.Length;
        if (Interlocked.Add(ref globalBytes, size) > GlobalMaxBytes)
        {
            Interlocked.Add(ref globalBytes, -size);
            return false;
        }

        entry.AccountedBytes = size;
        return true;
    }

    private void Remove(string key)
    {
        if (_entries.TryRemove(key, out var entry) && entry.AccountedBytes > 0)
        {
            Interlocked.Add(ref globalBytes, -entry.AccountedBytes);
            entry.AccountedBytes = 0;
        }
    }

    private void Trim()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _entries.Where(e => now - e.Value.CreatedAt > EntryLifetime).Select(e => e.Key).ToList())
        {
            Remove(expired);
        }

        while (true)
        {
            var kept = _entries
                .Where(e => e.Value.AccountedBytes > 0)
                .OrderBy(e => e.Value.CreatedAt)
                .ToList();
            var bytes = kept.Sum(e => (long)e.Value.AccountedBytes);
            if (kept.Count <= MaxEntries && bytes <= MaxSessionBytes)
            {
                return;
            }

            Remove(kept[0].Key);
        }
    }

    private sealed class Entry
    {
        public Entry(DateTimeOffset createdAt, Task<CachedSegment?> download)
        {
            CreatedAt = createdAt;
            Download = download;
        }

        public DateTimeOffset CreatedAt { get; }

        public Task<CachedSegment?> Download { get; }

        public int AccountedBytes { get; set; }
    }
}
