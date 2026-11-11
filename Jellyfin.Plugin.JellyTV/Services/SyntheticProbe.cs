using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Reproductor sintético: pide un canal por la misma URL que Jellyfin entrega a los clientes y mide lo que un
/// espectador viviría: tiempo hasta poder pintar, si los segmentos bajan más rápido que el tiempo real, si el vivo
/// avanza y si la limpieza (balizas, anuncios) dejó basura en la lista. Es la medición de "lo que se siente", independiente
/// de que haya usuarios mirando.
/// </summary>
internal static class SyntheticProbe
{
    private const int HistoryPerChannel = 20;
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<ProbeResult>> History = new(StringComparer.Ordinal);

    /// <summary>Últimas mediciones guardadas de un canal (más reciente primero).</summary>
    /// <param name="channelId">Id del canal.</param>
    /// <returns>Mediciones.</returns>
    internal static IReadOnlyList<ProbeResult> Recent(string channelId) =>
        History.TryGetValue(channelId, out var queue) ? [.. queue.Reverse()] : [];

    /// <summary>Mide un canal y guarda el resultado.</summary>
    /// <param name="http">Cliente HTTP (sigue redirecciones, como un reproductor).</param>
    /// <param name="baseUrl">URL pública del servidor, tal como la usa Jellyfin en el Path.</param>
    /// <param name="channel">Canal a medir.</param>
    /// <param name="sampleSegments">Cuántos segmentos del borde en vivo descargar.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>El resultado (nunca lanza por fallos de red: van en <c>Error</c>).</returns>
    internal static async Task<ProbeResult> RunAsync(HttpClient http, string baseUrl, ChannelEntry channel, int sampleSegments, CancellationToken ct)
    {
        var active = channel.Sources.FirstOrDefault(static s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Url));
        var now = DateTimeOffset.UtcNow;
        if (active is null)
        {
            return Save(Failed(channel, now, "El canal no tiene fuentes habilitadas."));
        }

        var keyQuery = Plugin.Instance?.Configuration?.RelayKey is { Length: > 0 } relayKey ? $"&key={Uri.EscapeDataString(relayKey)}" : string.Empty;
        var entry = $"{baseUrl.TrimEnd('/')}/JellyTV/stream.m3u8?url={Encode(active.Url)}&ch={channel.Id}{keyQuery}";
        var total = Stopwatch.StartNew();
        try
        {
            var (first, firstUrl, playlistMs) = await GetTextAsync(http, entry, ct).ConfigureAwait(false);
            double? variantMs = null;
            var media = first;
            var mediaUrl = firstUrl;
            if (first.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                var variant = Lines(first).FirstOrDefault(static l => l.Length > 0 && l[0] != '#')
                    ?? throw new InvalidOperationException("Master sin variantes.");
                mediaUrl = new Uri(new Uri(firstUrl), variant).AbsoluteUri;
                (media, mediaUrl, var vMs) = await GetTextAsync(http, mediaUrl, ct).ConfigureAwait(false);
                variantMs = vMs;
            }

            var info = Parse(media, mediaUrl);
            if (info.SegmentUrls.Count == 0)
            {
                throw new InvalidOperationException("La playlist no lista segmentos.");
            }

            var picked = info.SegmentUrls.TakeLast(Math.Max(1, sampleSegments)).ToList();
            var samples = new List<SegmentSample>();
            for (var i = 0; i < picked.Count; i++)
            {
                samples.Add(await MeasureSegmentAsync(http, picked[i], i, info.TargetDuration, ct).ConfigureAwait(false));
            }

            // ¿El vivo avanza? Una playlist que repite la misma ventana es un canal congelado aunque responda 200.
            bool? advanced = null;
            if (info.MediaSequence is { } sequence)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(info.TargetDuration, 2, 10)), ct).ConfigureAwait(false);
                var (again, _, _) = await GetTextAsync(http, mediaUrl, ct).ConfigureAwait(false);
                advanced = Parse(again, mediaUrl).MediaSequence > sequence;
            }

            var startup = playlistMs + (variantMs ?? 0) + (samples.Count > 0 ? samples[^1].TotalMs : 0);
            var result = new ProbeResult(
                channel.Id,
                channel.Name,
                now,
                Math.Round(playlistMs),
                variantMs is { } v ? Math.Round(v) : null,
                Math.Round(startup),
                info.TargetDuration,
                info.SegmentUrls.Count,
                info.Discontinuities,
                info.AdTags,
                info.BeaconUrls,
                advanced,
                info.PdtAgeSeconds is { } age ? Math.Round(age, 1) : null,
                samples,
                null,
                string.Empty);
            return Save(result with { Verdict = Judge(result) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Save(Failed(channel, now, $"{ex.GetType().Name}: {ex.Message} (tras {total.ElapsedMilliseconds} ms)"));
        }
    }

    private static string Judge(ProbeResult r)
    {
        if (r.Samples.Any(static s => s.Status is < 200 or >= 300))
        {
            return "caido";
        }

        if (r.LiveAdvanced == false)
        {
            return "congelado";
        }

        // Un segmento que baja a menos de 1,5x el tiempo real deja un colchón menor que un solo segmento: se traba.
        var slowest = r.Samples.Count > 0 ? r.Samples.Min(static s => s.RealtimeFactor) : 0;
        if (slowest < 1.0)
        {
            return "lento";
        }

        return slowest < 1.5 || r.StartupMs > 5000 ? "fragil" : "ok";
    }

    private static ProbeResult Failed(ChannelEntry channel, DateTimeOffset at, string error) =>
        new(channel.Id, channel.Name, at, 0, null, 0, 0, 0, 0, 0, 0, null, null, [], error, "caido");

    private static ProbeResult Save(ProbeResult result)
    {
        var queue = History.GetOrAdd(result.ChannelId, static _ => new ConcurrentQueue<ProbeResult>());
        queue.Enqueue(result);
        while (queue.Count > HistoryPerChannel && queue.TryDequeue(out _))
        {
        }

        return result;
    }

    private static async Task<(string Body, string FinalUrl, double Ms)> GetTextAsync(HttpClient http, string url, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return (body, response.RequestMessage?.RequestUri?.AbsoluteUri ?? url, timer.Elapsed.TotalMilliseconds);
    }

    private static async Task<SegmentSample> MeasureSegmentAsync(HttpClient http, string url, int index, double targetDuration, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new SegmentSample(index, timer.Elapsed.TotalMilliseconds, timer.Elapsed.TotalMilliseconds, 0, 0, 0, (int)response.StatusCode, false);
            }

            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            var read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            var firstByte = timer.Elapsed.TotalMilliseconds;
            var sync = read > 0 && (buffer[0] == 0x47 || !url.Contains(".ts", StringComparison.OrdinalIgnoreCase));
            long bytes = read;
            while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                bytes += read;
            }

            var ms = timer.Elapsed.TotalMilliseconds;
            return new SegmentSample(index, Math.Round(firstByte), Math.Round(ms), bytes, Math.Round(bytes * 8 / 1000.0 / Math.Max(ms, 1), 2), Math.Round(targetDuration * 1000 / Math.Max(ms, 1), 2), (int)response.StatusCode, sync);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new SegmentSample(index, 0, Math.Round(timer.Elapsed.TotalMilliseconds), 0, 0, 0, 0, false);
        }
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(static l => l.Trim());

    private static PlaylistInfo Parse(string text, string baseUrl)
    {
        double target = 6;
        long? sequence = null;
        var discontinuities = 0;
        var adTags = 0;
        var beacons = 0;
        DateTimeOffset? lastPdt = null;
        var segments = new List<string>();
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                target = t;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var s))
            {
                sequence = s;
            }
            else if (line.Equals("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase))
            {
                discontinuities++;
            }
            else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(line["#EXT-X-PROGRAM-DATE-TIME:".Length..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pdt))
            {
                lastPdt = pdt;
            }
            else if (line.StartsWith("#EXT-X-DATERANGE", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("#EXT-X-CUE-", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("#EXT-OATCLS-SCTE35", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("#EXT-X-AD", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("#EXT-X-ASSET", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("#EXT-X-BEACON", StringComparison.OrdinalIgnoreCase))
            {
                adTags++;
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                var absolute = new Uri(new Uri(baseUrl), line).AbsoluteUri;
                if (HlsRewriterService.UnwrapRedirectUrl(absolute) != absolute)
                {
                    beacons++;
                }

                segments.Add(absolute);
            }
        }

        return new PlaylistInfo(target, sequence, discontinuities, adTags, beacons, lastPdt is { } p ? (DateTimeOffset.UtcNow - p).TotalSeconds : null, segments);
    }

    private static string Encode(string url) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Resultado de medir un segmento.</summary>
    /// <param name="Index">Posición en la lista (0 = el más antiguo de los medidos).</param>
    /// <param name="FirstByteMs">Hasta el primer byte.</param>
    /// <param name="TotalMs">Hasta el último byte.</param>
    /// <param name="Bytes">Tamaño.</param>
    /// <param name="Mbps">Velocidad efectiva.</param>
    /// <param name="RealtimeFactor">Duración del segmento / tiempo de descarga; menor que 1 = no sostiene el vivo.</param>
    /// <param name="Status">Código HTTP (0 = fallo de red).</param>
    /// <param name="TsSync">El primer byte es 0x47 (MPEG-TS válido) o el contenedor no es TS.</param>
    internal sealed record SegmentSample(int Index, double FirstByteMs, double TotalMs, long Bytes, double Mbps, double RealtimeFactor, int Status, bool TsSync);

    /// <summary>Resultado completo de una medición.</summary>
    /// <param name="ChannelId">Id del canal.</param>
    /// <param name="Name">Nombre del canal.</param>
    /// <param name="At">Momento de la medición.</param>
    /// <param name="PlaylistMs">Primera playlist (master o media), incluida la espera del relay.</param>
    /// <param name="VariantMs">Playlist de variante cuando la primera era un master.</param>
    /// <param name="StartupMs">Estimación de tiempo hasta el primer fotograma: playlists + primer segmento.</param>
    /// <param name="TargetDuration">EXT-X-TARGETDURATION.</param>
    /// <param name="Segments">Segmentos en la ventana.</param>
    /// <param name="Discontinuities">EXT-X-DISCONTINUITY visibles (saltos, típicos de anuncios insertados).</param>
    /// <param name="AdTagsLeft">Etiquetas de anuncio/baliza que la limpieza debía quitar y siguen en la lista.</param>
    /// <param name="BeaconUrlsLeft">URLs de baliza que siguen como segmentos.</param>
    /// <param name="LiveAdvanced">La secuencia avanzó tras esperar una duración de segmento (<c>null</c> = no medido).</param>
    /// <param name="PdtAgeSeconds">Edad del último PROGRAM-DATE-TIME frente a ahora (<c>null</c> = la lista no lo trae).</param>
    /// <param name="Samples">Segmentos medidos.</param>
    /// <param name="Error">Primer error encontrado.</param>
    /// <param name="Verdict">Resumen: ok, lento, fragil, congelado o caido.</param>
    internal sealed record ProbeResult(
        string ChannelId,
        string Name,
        DateTimeOffset At,
        double PlaylistMs,
        double? VariantMs,
        double StartupMs,
        double TargetDuration,
        int Segments,
        int Discontinuities,
        int AdTagsLeft,
        int BeaconUrlsLeft,
        bool? LiveAdvanced,
        double? PdtAgeSeconds,
        IReadOnlyList<SegmentSample> Samples,
        string? Error,
        string Verdict);

    private sealed record PlaylistInfo(double TargetDuration, long? MediaSequence, int Discontinuities, int AdTags, int BeaconUrls, double? PdtAgeSeconds, List<string> SegmentUrls);
}
