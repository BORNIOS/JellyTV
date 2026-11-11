using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Medición del relay por canal (clave = id estable de sesión, <see cref="SessionManagerService.StableId"/>), en memoria y
/// sin dependencias. Todo lo que ve un espectador (apertura, playlist, segmentos) pasa por aquí para saber con números
/// dónde se pierde el tiempo, en vez de suponerlo. Se reinicia con el servidor.
/// </summary>
internal static class RelayMetrics
{
    private const int RingSize = 256;
    private static readonly ConcurrentDictionary<string, Series> All = new(StringComparer.Ordinal);

    /// <summary>Registra una duración (ms) bajo un nombre de serie, p. ej. "open", "playlist", "segment.origin".</summary>
    /// <param name="key">Id estable de sesión.</param>
    /// <param name="series">Nombre de la serie.</param>
    /// <param name="ms">Duración en milisegundos.</param>
    internal static void Time(string key, string series, double ms) => Get(key).Add(series, ms);

    /// <summary>Incrementa un contador (errores, cache hits, balizas descartadas…).</summary>
    /// <param name="key">Id estable de sesión.</param>
    /// <param name="counter">Nombre del contador.</param>
    /// <param name="by">Incremento.</param>
    internal static void Count(string key, string counter, long by = 1) => Get(key).Inc(counter, by);

    /// <summary>Foto de todo lo medido: contadores y percentiles por serie.</summary>
    /// <returns>Mapa clave → datos.</returns>
    internal static IReadOnlyDictionary<string, object> Snapshot() =>
        All.ToDictionary(static kv => kv.Key, static kv => kv.Value.Describe(), StringComparer.Ordinal);

    /// <summary>Foto de un canal concreto, o <c>null</c> si nunca se midió.</summary>
    /// <param name="key">Id estable de sesión.</param>
    /// <returns>Datos o <c>null</c>.</returns>
    internal static object? SnapshotOf(string key) => All.TryGetValue(key, out var s) ? s.Describe() : null;

    private static Series Get(string key) => All.GetOrAdd(key, static _ => new Series());

    private sealed class Series
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Ring> _rings = new(StringComparer.Ordinal);

        internal void Add(string name, double ms)
        {
            lock (_gate)
            {
                if (!_rings.TryGetValue(name, out var ring))
                {
                    _rings[name] = ring = new Ring();
                }

                ring.Add(ms);
            }
        }

        internal void Inc(string name, long by)
        {
            lock (_gate)
            {
                _counters[name] = _counters.GetValueOrDefault(name) + by;
            }
        }

        internal object Describe()
        {
            lock (_gate)
            {
                return new
                {
                    counters = new SortedDictionary<string, long>(_counters, StringComparer.Ordinal),
                    timings = _rings.ToDictionary(static kv => kv.Key, static kv => kv.Value.Stats(), StringComparer.Ordinal),
                };
            }
        }
    }

    private sealed class Ring
    {
        private readonly double[] _values = new double[RingSize];
        private long _total;

        internal void Add(double value) => _values[_total++ % RingSize] = value;

        internal object Stats()
        {
            var n = (int)Math.Min(_total, RingSize);
            var sorted = _values.Take(n).OrderBy(static v => v).ToArray();
            double P(double q) => sorted[Math.Min(n - 1, (int)Math.Floor(q * n))];
            return new { samples = _total, p50 = Math.Round(P(0.5)), p95 = Math.Round(P(0.95)), max = Math.Round(sorted[^1]) };
        }
    }
}
