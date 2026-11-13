using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Pluto TV, consultado en su propia API: <c>boot.pluto.tv</c> abre una sesión (con un JWT que dura unas 24 horas) y
/// <c>service-channels.clusters.pluto.tv</c> da canales, categorías y programación. El catálogo y su idioma salen de la IP.
/// </summary>
internal sealed class PlutoProvider : IChannelProvider
{
    private const int GuideBatchSize = 40;

    /// <inheritdoc/>
    public string Id => "pluto";

    /// <inheritdoc/>
    public string Name => "Pluto TV";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderChannel>> ListChannelsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var session = await BootAsync(client, cancellationToken).ConfigureAwait(false);
        var headers = AuthHeader(session);
        var channels = await ProviderHttp.GetAsync(client, $"{session.ChannelsServer}/v2/guide/channels?channelIds=&offset=0&limit=1000&sort=number%3Aasc", headers, cancellationToken).ConfigureAwait(false);
        var categories = await ProviderHttp.GetAsync(client, $"{session.ChannelsServer}/v2/guide/categories", headers, cancellationToken).ConfigureAwait(false);
        return ParseChannels(channels, categories, session.Country);
    }

    /// <inheritdoc/>
    public async Task<ProviderStream> ResolveStreamAsync(string channelId, HttpClient client, CancellationToken cancellationToken)
    {
        // Una sesión nueva por canal: así cada reproducción tiene su propio id de sesión, como una persona viendo la web.
        var session = await BootAsync(client, cancellationToken).ConfigureAwait(false);
        return new ProviderStream(StreamUrl(session, channelId), session.Token, JwtExpiry(session.Token));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderProgramme>> GetProgrammesAsync(HttpClient client, IReadOnlyList<ProviderChannel> channels, int hours, CancellationToken cancellationToken)
    {
        var session = await BootAsync(client, cancellationToken).ConfigureAwait(false);
        var headers = AuthHeader(session);
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH':00:00.000Z'", CultureInfo.InvariantCulture);
        var result = new List<ProviderProgramme>();
        foreach (var batch in channels.Chunk(GuideBatchSize))
        {
            var ids = string.Join(',', batch.Select(static c => c.Id));
            var json = await ProviderHttp.GetAsync(client, $"{session.ChannelsServer}/v2/guide/timelines?start={start}&channelIds={ids}&duration={hours * 60}", headers, cancellationToken).ConfigureAwait(false);
            result.AddRange(ParseTimelines(json));
        }

        return result;
    }

    /// <summary>Lee la respuesta de <c>boot</c>.</summary>
    /// <param name="json">Respuesta JSON.</param>
    /// <returns>La sesión.</returns>
    internal static PlutoSession ParseBoot(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var token = ProviderJson.Str(root, "sessionToken") ?? throw new InvalidOperationException("Pluto no devolvió sessionToken.");
        var servers = ProviderJson.Obj(root, "servers");
        return new PlutoSession(
            token,
            ProviderJson.Str(servers, "channels") ?? "https://service-channels.clusters.pluto.tv",
            ProviderJson.Str(servers, "stitcher") ?? throw new InvalidOperationException("Pluto no devolvió el servidor de streams."),
            ProviderJson.Str(root, "stitcherParams") ?? string.Empty,
            ProviderJson.Str(ProviderJson.Obj(root, "session"), "countryCode"));
    }

    /// <summary>Lee la lista de canales y sus categorías.</summary>
    /// <param name="channelsJson">Respuesta de <c>guide/channels</c>.</param>
    /// <param name="categoriesJson">Respuesta de <c>guide/categories</c>.</param>
    /// <param name="country">País del catálogo.</param>
    /// <returns>Los canales.</returns>
    internal static List<ProviderChannel> ParseChannels(string channelsJson, string categoriesJson, string? country)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var categories = JsonDocument.Parse(categoriesJson))
        {
            foreach (var category in ProviderJson.Items(categories.RootElement, "data"))
            {
                var id = ProviderJson.Str(category, "id");
                var name = ProviderJson.Str(category, "name");
                if (id is not null && name is not null)
                {
                    groups[id] = name;
                }
            }
        }

        var result = new List<ProviderChannel>();
        using var channels = JsonDocument.Parse(channelsJson);
        foreach (var channel in ProviderJson.Items(channels.RootElement, "data"))
        {
            var id = ProviderJson.Str(channel, "id");
            var name = ProviderJson.Str(channel, "name");
            if (id is null || name is null)
            {
                continue;
            }

            string? group = null;
            foreach (var categoryId in ProviderJson.Items(channel, "categoryIDs"))
            {
                if (categoryId.ValueKind == JsonValueKind.String && groups.TryGetValue(categoryId.GetString()!, out var found))
                {
                    group = found;
                    break;
                }
            }

            result.Add(new ProviderChannel(id, name, ProviderJson.Int(channel, "number"), Logo(channel), group, ProviderJson.Str(channel, "summary"), country));
        }

        return result;
    }

    /// <summary>Lee la programación de <c>guide/timelines</c>.</summary>
    /// <param name="json">Respuesta JSON.</param>
    /// <returns>Los programas.</returns>
    internal static List<ProviderProgramme> ParseTimelines(string json)
    {
        var result = new List<ProviderProgramme>();
        using var document = JsonDocument.Parse(json);
        foreach (var entry in ProviderJson.Items(document.RootElement, "data"))
        {
            var channelId = ProviderJson.Str(entry, "channelId");
            if (channelId is null)
            {
                continue;
            }

            foreach (var item in ProviderJson.Items(entry, "timelines"))
            {
                if (!TryParseTime(ProviderJson.Str(item, "start"), out var start) || !TryParseTime(ProviderJson.Str(item, "stop"), out var stop))
                {
                    continue;
                }

                var episode = ProviderJson.Obj(item, "episode");
                var title = ProviderJson.Str(item, "title") ?? ProviderJson.Str(episode, "name") ?? "Sin título";
                var episodeName = ProviderJson.Str(episode, "name");
                result.Add(new ProviderProgramme(
                    channelId,
                    start,
                    stop,
                    title,
                    episodeName is not null && !string.Equals(episodeName, title, StringComparison.Ordinal) ? episodeName : null,
                    ProviderJson.Str(episode, "description"),
                    ProviderJson.Str(episode, "genre"),
                    ProviderJson.Str(ProviderJson.Obj(episode, "poster"), "path") ?? ProviderJson.Str(ProviderJson.Obj(episode, "thumbnail"), "path")));
            }
        }

        return result;
    }

    /// <summary>Calcula cuándo caduca un JWT, leyendo su campo <c>exp</c>.</summary>
    /// <param name="jwt">El token.</param>
    /// <returns>La caducidad, o <c>null</c> si no se puede leer.</returns>
    internal static DateTimeOffset? JwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return ProviderJson.Long(document.RootElement, "exp") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Arma la dirección de reproducción de un canal con una sesión abierta.</summary>
    /// <param name="session">Sesión de Pluto.</param>
    /// <param name="channelId">Id del canal.</param>
    /// <returns>La lista maestra HLS, con el JWT en la dirección.</returns>
    internal static string StreamUrl(PlutoSession session, string channelId)
        => $"{session.StitcherServer}/v2/stitch/hls/channel/{Uri.EscapeDataString(channelId)}/master.m3u8?{session.StitcherParams}&jwt={session.Token}&masterJWTPassthrough=true&includeExtendedEvents=true";

    private static async Task<PlutoSession> BootAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var clientId = Guid.NewGuid().ToString();
        var query = "appName=web&appVersion=9.18.0-29b0c2b3de1d7a7a8d49cd1a87ca4ea4f2927ea2&deviceVersion=145.0.0&deviceModel=web&deviceMake=chrome&deviceType=web"
            + $"&clientID={clientId}&clientModelNumber=1.0.0&serverSideAds=false&drmCapabilities=widevine%3AL3&blockingMode=&notificationVersion=1&appLaunchCount=0&lastAppLaunchDate=";
        return ParseBoot(await ProviderHttp.GetAsync(client, "https://boot.pluto.tv/v4/start?" + query, null, cancellationToken).ConfigureAwait(false));
    }

    private static Dictionary<string, string> AuthHeader(PlutoSession session) => new() { ["Authorization"] = "Bearer " + session.Token };

    private static string? Logo(JsonElement channel)
    {
        string? fallback = null;
        foreach (var image in ProviderJson.Items(channel, "images"))
        {
            var url = ProviderJson.Str(image, "url");
            if (url is null)
            {
                continue;
            }

            var type = ProviderJson.Str(image, "type");
            if (string.Equals(type, "colorLogoPNG", StringComparison.Ordinal))
            {
                return url;
            }

            if (fallback is null && string.Equals(type, "solidLogoPNG", StringComparison.Ordinal))
            {
                fallback = url;
            }
        }

        return fallback;
    }

    private static bool TryParseTime(string? text, out DateTimeOffset moment)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out moment);

    /// <summary>Sesión abierta en <c>boot.pluto.tv</c>.</summary>
    /// <param name="Token">JWT de la sesión.</param>
    /// <param name="ChannelsServer">Servidor de canales y guía.</param>
    /// <param name="StitcherServer">Servidor de los streams.</param>
    /// <param name="StitcherParams">Parámetros que Pluto pide en cada dirección de stream.</param>
    /// <param name="Country">País que Pluto dedujo de la IP.</param>
    internal sealed record PlutoSession(string Token, string ChannelsServer, string StitcherServer, string StitcherParams, string? Country);
}
