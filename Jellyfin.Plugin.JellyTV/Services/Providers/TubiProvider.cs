using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Canales en vivo de Tubi, consultados en su propia web: <c>tubitv.com/live</c> lista los canales por categoría y
/// <c>oz/epg/programming</c> da cada canal con su manifiesto y su programación. No necesita cuenta. El catálogo depende del
/// país de la IP (desde México son pocos canales; el de Estados Unidos es mucho mayor).
/// </summary>
internal sealed class TubiProvider : IChannelProvider
{
    private const int BatchSize = 150;

    private static readonly Regex JsDate = new(@"new Date\(""([^""]*)""\)", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex JsUndefined = new(@"\bundefined\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <inheritdoc/>
    public string Id => "tubi";

    /// <inheritdoc/>
    public string Name => "Tubi";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderChannel>> ListChannelsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var page = await ProviderHttp.GetAsync(client, "https://tubitv.com/live", null, cancellationToken).ConfigureAwait(false);
        var groups = ParseLivePage(page);
        var result = new List<ProviderChannel>();
        foreach (var batch in groups.Keys.Chunk(BatchSize))
        {
            result.AddRange(ParseChannels(await FetchProgrammingAsync(client, batch, cancellationToken).ConfigureAwait(false), groups));
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<ProviderStream> ResolveStreamAsync(string channelId, HttpClient client, CancellationToken cancellationToken)
    {
        var json = await FetchProgrammingAsync(client, [channelId], cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var url = ProviderJson.Items(document.RootElement, "rows")
            .Select(static row => ManifestUrl(row))
            .FirstOrDefault(static u => u is not null)
            ?? throw new InvalidOperationException($"Tubi no ofrece ahora el canal {channelId} (¿otro país?).");

        // Sin el token de la dirección: esa dirección redirige a una con un token nuevo cada vez que se abre.
        return new ProviderStream(StripQuery(url), null, null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderProgramme>> GetProgrammesAsync(HttpClient client, IReadOnlyList<ProviderChannel> channels, int hours, CancellationToken cancellationToken)
    {
        var result = new List<ProviderProgramme>();
        foreach (var batch in channels.Select(static c => c.Id).Chunk(BatchSize))
        {
            result.AddRange(ParseProgrammes(await FetchProgrammingAsync(client, batch, cancellationToken).ConfigureAwait(false)));
        }

        var until = DateTimeOffset.UtcNow.AddHours(hours);
        return result.Where(p => p.Stop > DateTimeOffset.UtcNow && p.Start < until).ToList();
    }

    /// <summary>Lee de la página <c>/live</c> los ids de los canales y la categoría de cada uno.</summary>
    /// <param name="html">HTML de la página.</param>
    /// <returns>Categoría por id de canal, en el orden de la página.</returns>
    internal static Dictionary<string, string> ParseLivePage(string html)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var marker = html.IndexOf("window.__data", StringComparison.Ordinal);
        if (marker < 0)
        {
            return groups;
        }

        var start = html.IndexOf('{', marker);
        var end = html.IndexOf("</script>", marker, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            return groups;
        }

        var script = html[start..end];
        script = script[..(script.LastIndexOf('}') + 1)];
        script = JsDate.Replace(JsUndefined.Replace(script, "null"), "\"$1\"");

        using var document = JsonDocument.Parse(script);
        var containers = ProviderJson.Obj(ProviderJson.Obj(document.RootElement, "epg"), "contentIdsByContainer");
        if (containers.ValueKind != JsonValueKind.Object)
        {
            return groups;
        }

        foreach (var container in containers.EnumerateObject().Where(static c => c.Value.ValueKind == JsonValueKind.Array))
        {
            foreach (var category in container.Value.EnumerateArray())
            {
                var name = ProviderJson.Str(category, "name") ?? "Otros";
                foreach (var content in ProviderJson.Items(category, "contents"))
                {
                    var id = content.ValueKind == JsonValueKind.Number ? content.GetRawText() : content.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        groups.TryAdd(id, name);
                    }
                }
            }
        }

        return groups;
    }

    /// <summary>Lee los canales de una respuesta de <c>programming</c>.</summary>
    /// <param name="json">Respuesta JSON.</param>
    /// <param name="groups">Categoría por id de canal.</param>
    /// <returns>Los canales.</returns>
    internal static List<ProviderChannel> ParseChannels(string json, IReadOnlyDictionary<string, string> groups)
    {
        var result = new List<ProviderChannel>();
        using var document = JsonDocument.Parse(json);
        foreach (var row in ProviderJson.Items(document.RootElement, "rows"))
        {
            var id = ProviderJson.Str(row, "content_id");
            var name = ProviderJson.Str(row, "title");
            if (id is null || name is null || ManifestUrl(row) is null)
            {
                continue;
            }

            groups.TryGetValue(id, out var group);
            result.Add(new ProviderChannel(id, name, null, FirstString(ProviderJson.Obj(row, "images"), "thumbnail"), group, ProviderJson.Str(row, "description"), null));
        }

        return result;
    }

    /// <summary>Lee la programación de una respuesta de <c>programming</c>.</summary>
    /// <param name="json">Respuesta JSON.</param>
    /// <returns>Los programas.</returns>
    internal static List<ProviderProgramme> ParseProgrammes(string json)
    {
        var result = new List<ProviderProgramme>();
        using var document = JsonDocument.Parse(json);
        foreach (var row in ProviderJson.Items(document.RootElement, "rows"))
        {
            var channelId = ProviderJson.Str(row, "content_id");
            if (channelId is null)
            {
                continue;
            }

            foreach (var program in ProviderJson.Items(row, "programs"))
            {
                if (!TryParseTime(ProviderJson.Str(program, "start_time"), out var start) || !TryParseTime(ProviderJson.Str(program, "end_time"), out var stop))
                {
                    continue;
                }

                result.Add(new ProviderProgramme(
                    channelId,
                    start,
                    stop,
                    ProviderJson.Str(program, "title") ?? "Sin título",
                    ProviderJson.Str(program, "episode_title"),
                    ProviderJson.Str(program, "description"),
                    null,
                    null));
            }
        }

        return result;
    }

    private static Task<string> FetchProgrammingAsync(HttpClient client, IEnumerable<string> ids, CancellationToken cancellationToken)
        => ProviderHttp.GetAsync(client, "https://tubitv.com/oz/epg/programming?content_id=" + string.Join(',', ids), null, cancellationToken);

    private static string? ManifestUrl(JsonElement row)
    {
        var resource = ProviderJson.Items(row, "video_resources").FirstOrDefault();
        var url = ProviderJson.Str(ProviderJson.Obj(resource, "manifest"), "url");
        return url is null ? null : Uri.UnescapeDataString(url);
    }

    private static string StripQuery(string url)
    {
        var query = url.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? url : url[..query];
    }

    private static string? FirstString(JsonElement element, string name)
        => ProviderJson.Items(element, name).Select(static v => v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s));

    private static bool TryParseTime(string? text, out DateTimeOffset moment)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out moment);
}
