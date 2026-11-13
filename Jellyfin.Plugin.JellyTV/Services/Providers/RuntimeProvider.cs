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
/// Canales lineales de Runtime, consultados en la API de su propia web (plataforma Ottera). El sitio entrega a cualquier visitante un
/// token público (<c>cs_auth_token</c>) que acompaña a cada petición de su reproductor; se pide igual que lo haría el navegador.
/// El id del canal es <c>idDelVideo-idDelCanal</c>: el primero abre el reproductor y el segundo pide la programación.
/// </summary>
internal sealed class RuntimeProvider : IChannelProvider
{
    private const string Api = "https://api-ott.runtime.tv";
    private const string Site = "https://www.runtime.tv";
    private const string TokenPage = Site + "/es/linear/runtime-espanol";
    private const int GuideParallelism = 6;
    private const int GuideSegments = 36;

    private static readonly Regex AuthToken = new("\"cs_auth_token\":\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Playlist = new(@"https://[^""'\s\\]+\.m3u8[^""'\s\\]*", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <inheritdoc/>
    public string Id => "runtime";

    /// <inheritdoc/>
    public string Name => "Runtime";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderChannel>> ListChannelsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var headers = await SessionHeadersAsync(client, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.PostFormAsync(client, Api + "/search", Parameters(("object_type", "video"), ("video_type", "linear"), ("max", "200")), headers, cancellationToken).ConfigureAwait(false);
        return ParseChannels(json);
    }

    /// <inheritdoc/>
    public async Task<ProviderStream> ResolveStreamAsync(string channelId, HttpClient client, CancellationToken cancellationToken)
    {
        var headers = await SessionHeadersAsync(client, cancellationToken).ConfigureAwait(false);
        var script = await ProviderHttp.GetAsync(
            client,
            Api + "/embeddedVideoPlayer?" + Parameters(("div_id", "video_player"), ("content_page_url", Uri.EscapeDataString(Site + "/")), ("image_width", "1280"), ("max_bitrate", "10000"), ("id", VideoId(channelId))),
            headers,
            cancellationToken).ConfigureAwait(false);
        var url = ExtractPlaylistUrl(script)
            ?? throw new InvalidOperationException($"Runtime no entregó la dirección del canal {channelId} (¿no está disponible en este país?).");
        return new ProviderStream(url, null, null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderProgramme>> GetProgrammesAsync(HttpClient client, IReadOnlyList<ProviderChannel> channels, int hours, CancellationToken cancellationToken)
    {
        var headers = await SessionHeadersAsync(client, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var until = now.AddHours(hours);
        using var gate = new SemaphoreSlim(GuideParallelism);
        var tasks = channels.Select(async channel =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var json = await ProviderHttp.GetAsync(
                    client,
                    Api + "/getvideosegments?" + Parameters(("parent_type", "linear_channel"), ("linear_channel_id", ChannelNumber(channel.Id)), ("max", GuideSegments.ToString(CultureInfo.InvariantCulture))),
                    headers,
                    cancellationToken).ConfigureAwait(false);
                return ParseProgrammes(json, channel.Id);
            }
            catch (HttpRequestException)
            {
                return []; // un canal sin guía no debe tirar la de los demás
            }
            finally
            {
                gate.Release();
            }
        });

        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        return all.SelectMany(static p => p).Where(p => p.Stop > now && p.Start < until).ToList();
    }

    /// <summary>Lee los canales de una respuesta de <c>search</c> (videos lineales).</summary>
    /// <param name="json">Respuesta JSON.</param>
    /// <returns>Los canales.</returns>
    internal static List<ProviderChannel> ParseChannels(string json)
    {
        var result = new List<ProviderChannel>();
        using var document = JsonDocument.Parse(json);
        foreach (var video in ProviderJson.Items(document.RootElement, "objects"))
        {
            var videoId = ProviderJson.Str(video, "id");
            var channelNumber = ProviderJson.Str(video, "linear_channel_id");
            var name = ProviderJson.Str(video, "name");
            if (videoId is null || channelNumber is null || name is null)
            {
                continue;
            }

            var logo = ProviderJson.Str(video, "widescreen_thumbnail_url") ?? ProviderJson.Str(video, "logo");
            result.Add(new ProviderChannel($"{videoId}-{channelNumber}", name, null, logo, null, ProviderJson.Str(video, "short_description"), null));
        }

        return result;
    }

    /// <summary>Lee la programación de una respuesta de <c>getvideosegments</c>.</summary>
    /// <param name="json">Respuesta JSON.</param>
    /// <param name="channelId">Id del canal al que pertenece.</param>
    /// <returns>Los programas.</returns>
    internal static List<ProviderProgramme> ParseProgrammes(string json, string channelId)
    {
        var result = new List<ProviderProgramme>();
        using var document = JsonDocument.Parse(json);
        foreach (var segment in ProviderJson.Items(document.RootElement, "objects"))
        {
            if (!long.TryParse(ProviderJson.Str(segment, "segment_start_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
                || !long.TryParse(ProviderJson.Str(segment, "segment_end_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stop)
                || stop <= start)
            {
                continue;
            }

            result.Add(new ProviderProgramme(
                channelId,
                DateTimeOffset.FromUnixTimeSeconds(start),
                DateTimeOffset.FromUnixTimeSeconds(stop),
                ProviderJson.Str(segment, "name") ?? "Sin título",
                null,
                ProviderJson.Str(segment, "short_description") ?? ProviderJson.Str(segment, "long_description"),
                null,
                null));
        }

        return result;
    }

    /// <summary>Busca en el script del reproductor la lista HLS maestra del canal.</summary>
    /// <param name="script">Respuesta de <c>embeddedVideoPlayer</c>.</param>
    /// <returns>La dirección, o <c>null</c> si el canal no se ofrece.</returns>
    internal static string? ExtractPlaylistUrl(string script)
    {
        var text = script.Replace("\\/", "/", StringComparison.Ordinal).Replace("\\u0026", "&", StringComparison.Ordinal);
        var match = Playlist.Match(text);
        return match.Success ? match.Value : null;
    }

    private static string VideoId(string channelId) => channelId.Split('-')[0];

    private static string ChannelNumber(string channelId)
    {
        var parts = channelId.Split('-');
        return parts.Length > 1 ? parts[1] : parts[0];
    }

    private static string Parameters(params (string Name, string Value)[] extra)
    {
        var common = new (string Name, string Value)[]
        {
            ("version", "13"), ("device_type", "desktop"), ("platform", "web"), ("partner", "internal"), ("language", "en"),
            ("connection", "wifi"), ("timezone", "+0000"), ("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
        };
        return string.Join('&', common.Concat(extra).Select(static p => p.Name + "=" + p.Value));
    }

    private static async Task<IReadOnlyDictionary<string, string>> SessionHeadersAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var page = await ProviderHttp.GetAsync(client, TokenPage, null, cancellationToken).ConfigureAwait(false);
        var token = AuthToken.Match(page);
        if (!token.Success)
        {
            throw new InvalidOperationException("Runtime cambió su página: no se encontró el token público del reproductor.");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Origin"] = Site,
            ["Referer"] = Site + "/",
            ["ottera-cs-auth"] = token.Groups[1].Value,
            ["ottera-referrer"] = "runtime.tv"
        };
    }
}
