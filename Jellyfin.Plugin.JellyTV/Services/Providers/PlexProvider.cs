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
/// Canales gratuitos de Plex, consultados en su propia API: un token anónimo de <c>clients.plex.tv</c> abre
/// <c>epg.provider.plex.tv</c>, que da el catálogo (por país, según la IP), la dirección de cada canal y su guía.
/// </summary>
internal sealed class PlexProvider : IChannelProvider
{
    private const int GuideConcurrency = 6;

    private static readonly Regex CountryInTitle = new(@"\bin ([A-Za-z]{2})\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <inheritdoc/>
    public string Id => "plex";

    /// <inheritdoc/>
    public string Name => "Plex";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderChannel>> ListChannelsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var headers = await AuthHeadersAsync(client, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.GetAsync(client, "https://epg.provider.plex.tv/lineups/plex/channels", headers, cancellationToken).ConfigureAwait(false);
        return ParseChannels(json);
    }

    /// <inheritdoc/>
    public async Task<ProviderStream> ResolveStreamAsync(string channelId, HttpClient client, CancellationToken cancellationToken)
    {
        // La dirección de la parte devuelve la lista maestra directamente; basta un token anónimo reciente.
        var token = (await AuthHeadersAsync(client, cancellationToken).ConfigureAwait(false))["X-Plex-Token"];
        return new ProviderStream($"https://epg.provider.plex.tv/library/parts/{Uri.EscapeDataString(channelId)}.m3u8?X-Plex-Token={token}", token, null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderProgramme>> GetProgrammesAsync(HttpClient client, IReadOnlyList<ProviderChannel> channels, int hours, CancellationToken cancellationToken)
    {
        var headers = await AuthHeadersAsync(client, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var until = now.AddHours(hours);
        var dates = Enumerable.Range(0, (until.UtcDateTime.Date - now.UtcDateTime.Date).Days + 1)
            .Select(offset => now.UtcDateTime.Date.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();

        // La API de la guía pide un canal y un día por petición: se hacen pocas a la vez para no saturarla.
        using var gate = new SemaphoreSlim(GuideConcurrency);
        var tasks = channels
            .SelectMany(channel => dates.Select(date => (channel, date)))
            .Select(async job =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var gridKey = GridKey(job.channel.Id);
                    var json = await ProviderHttp.GetAsync(client, $"https://epg.provider.plex.tv/grid?channelGridKey={gridKey}&date={job.date}", headers, cancellationToken).ConfigureAwait(false);
                    return ParseGrid(json, job.channel.Id);
                }
                catch (HttpRequestException ex)
                {
                    PluginLog.Warn($"[PROVIDER] Plex: guía de {job.channel.Name} ({job.date}) no disponible: {ex.Message}");
                    return new List<ProviderProgramme>();
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToList();

        var seen = new HashSet<(string, DateTimeOffset)>();
        return (await Task.WhenAll(tasks).ConfigureAwait(false))
            .SelectMany(static list => list)
            .Where(p => p.Stop > now && p.Start < until && seen.Add((p.ChannelId, p.Start)))
            .ToList();
    }

    /// <summary>Lee la lista de canales del catálogo.</summary>
    /// <param name="json">Respuesta de <c>lineups/plex/channels</c>.</param>
    /// <returns>Los canales.</returns>
    internal static List<ProviderChannel> ParseChannels(string json)
    {
        using var document = JsonDocument.Parse(json);
        var container = ProviderJson.Obj(document.RootElement, "MediaContainer");
        var match = CountryInTitle.Match(ProviderJson.Str(container, "title") ?? string.Empty);
        var country = match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;

        var result = new List<ProviderChannel>();
        foreach (var channel in ProviderJson.Items(container, "Channel"))
        {
            var id = ProviderJson.Str(channel, "id");
            var name = ProviderJson.Str(channel, "title");
            if (id is null || name is null || (channel.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True))
            {
                continue;
            }

            var number = int.TryParse(ProviderJson.Str(channel, "vcn"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : (int?)null;
            result.Add(new ProviderChannel(id, name, number, ProviderJson.Str(channel, "thumb"), null, ProviderJson.Str(channel, "summary"), country));
        }

        return result;
    }

    /// <summary>Lee la programación de un canal y un día.</summary>
    /// <param name="json">Respuesta de <c>grid</c>.</param>
    /// <param name="channelId">Id del canal.</param>
    /// <returns>Los programas.</returns>
    internal static List<ProviderProgramme> ParseGrid(string json, string channelId)
    {
        var result = new List<ProviderProgramme>();
        using var document = JsonDocument.Parse(json);
        foreach (var item in ProviderJson.Items(ProviderJson.Obj(document.RootElement, "MediaContainer"), "Metadata"))
        {
            var media = ProviderJson.Items(item, "Media").FirstOrDefault();
            if (ProviderJson.Long(media, "beginsAt") is not { } begins || ProviderJson.Long(media, "endsAt") is not { } ends)
            {
                continue;
            }

            var title = ProviderJson.Str(item, "title") ?? "Sin título";
            var series = ProviderJson.Str(item, "grandparentTitle");
            var genre = ProviderJson.Items(item, "Genre").Select(static g => ProviderJson.Str(g, "tag")).FirstOrDefault(static t => t is not null);
            result.Add(new ProviderProgramme(
                channelId,
                DateTimeOffset.FromUnixTimeSeconds(begins),
                DateTimeOffset.FromUnixTimeSeconds(ends),
                series ?? title,
                series is null ? null : title,
                ProviderJson.Str(item, "summary"),
                genre,
                ProviderJson.Str(item, "thumb")));
        }

        return result;
    }

    // El id de un canal es "{proveedor}-{clave de guía}"; la guía se pide por la clave.
    private static string GridKey(string channelId) => channelId[(channelId.LastIndexOf('-') + 1)..];

    private static async Task<Dictionary<string, string>> AuthHeadersAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var clientId = Guid.NewGuid().ToString("N");
        var common = new Dictionary<string, string>
        {
            ["Accept"] = "application/json",
            ["X-Plex-Product"] = "Plex Web",
            ["X-Plex-Version"] = "4.150.0",
            ["X-Plex-Client-Identifier"] = clientId,
            ["X-Plex-Platform"] = "Web"
        };

        var json = await ProviderHttp.SendAsync(
            client,
            $"https://clients.plex.tv/api/v2/users/anonymous?X-Plex-Product=Plex%20Web&X-Plex-Client-Identifier={clientId}",
            common,
            HttpMethod.Post,
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        common["X-Plex-Token"] = ProviderJson.Str(document.RootElement, "authToken") ?? throw new InvalidOperationException("Plex no devolvió un token anónimo.");
        return common;
    }
}
