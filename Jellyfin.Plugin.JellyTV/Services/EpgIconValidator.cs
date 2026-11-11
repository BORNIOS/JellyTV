using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Deja las imágenes de cada programa en buen estado: comprueba que las URLs que traen las fuentes existen de verdad
/// (algunas guías publican enlaces caducados y Jellyfin responde 500 al pedirlos) y anota el ancho y el alto de cada
/// una, para que luego se sepa si es un póster vertical o un fondo horizontal.
/// </summary>
internal static class EpgIconValidator
{
    private const int ProbeBytes = 65536;

    private static readonly TimeSpan GoodTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan BadTtl = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, (bool Ok, int Width, int Height, DateTime At)> Cache = new(StringComparer.Ordinal);
    private static readonly Regex IconSrc = new("<icon\\b[^>]*\\bsrc=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Comprueba que una URL responde con una imagen.
    /// </summary>
    /// <param name="url">URL de la imagen.</param>
    /// <param name="client">Cliente HTTP.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns><c>true</c> si existe y es una imagen.</returns>
    internal static async Task<bool> IsImageAsync(string url, HttpClient client, CancellationToken ct)
    {
        await ProbeAsync(url, client, ct).ConfigureAwait(false);
        return Cache.TryGetValue(url, out var probe) && probe.Ok;
    }

    /// <summary>Forgets which image URLs were found good or broken.</summary>
    internal static void Clear() => Cache.Clear();

    /// <summary>
    /// Quita las imágenes que no existen y anota las dimensiones de las que sí.
    /// </summary>
    /// <param name="records">Programas de la fuente, ya enriquecidos.</param>
    /// <param name="client">Cliente HTTP.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Número de imágenes rotas que se quitaron.</returns>
    internal static async Task<int> ValidateAsync(IReadOnlyList<XmlProgrammeRecord> records, HttpClient client, CancellationToken ct)
    {
        var urls = records
            .SelectMany(static r => IconSrc.Matches(r.Xml).Select(static m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)))
            .Where(static u => !u.Contains("image.tmdb.org", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (urls.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(
            urls,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (url, token) => await ProbeAsync(url, client, token).ConfigureAwait(false)).ConfigureAwait(false);

        var removed = 0;
        foreach (var record in records)
        {
            if (!IconSrc.IsMatch(record.Xml))
            {
                continue;
            }

            var rewritten = Rewrite(record.Xml, out var brokenHere);
            if (rewritten is null)
            {
                continue;
            }

            record.Xml = rewritten;
            removed += brokenHere;
            if (brokenHere > 0 && !IconSrc.IsMatch(rewritten))
            {
                record.Data = record.Data.WithoutIcon();
            }
        }

        if (removed > 0)
        {
            PluginLog.Info($"[EPG] {removed} imagen(es) de la fuente no existen y se quitaron.");
        }

        return removed;
    }

    private static string? Rewrite(string xml, out int broken)
    {
        broken = 0;
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var changed = false;
            foreach (var icon in doc.Root!.Elements("icon").ToList())
            {
                var src = (string?)icon.Attribute("src");
                if (string.IsNullOrWhiteSpace(src) || src.Contains("image.tmdb.org", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Cache.TryGetValue(src, out var probe))
                {
                    continue;
                }

                if (!probe.Ok)
                {
                    icon.Remove();
                    broken++;
                    changed = true;
                }
                else if (probe.Width > 0 && icon.Attribute("width") is null)
                {
                    icon.SetAttributeValue("width", probe.Width);
                    icon.SetAttributeValue("height", probe.Height);
                    changed = true;
                }
            }

            return changed ? doc.Root.ToString(SaveOptions.DisableFormatting) : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static async Task ProbeAsync(string url, HttpClient client, CancellationToken ct)
    {
        if (Cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.At < (cached.Ok ? GoodTtl : BadTtl))
        {
            return;
        }

        var ok = false;
        var width = 0;
        var height = 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            ok = response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
            if (ok)
            {
                using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[ProbeBytes];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = await body.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    read += n;
                }

                ImageSize.TryRead(buffer.AsSpan(0, read), out width, out height);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // Sin respuesta no se puede garantizar la imagen: se trata como rota.
            ct.ThrowIfCancellationRequested();
        }

        Cache[url] = (ok, width, height, DateTime.UtcNow);
    }
}
