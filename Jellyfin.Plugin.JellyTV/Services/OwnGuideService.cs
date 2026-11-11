using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services.Identity;
using Jellyfin.Plugin.JellyTV.Services.Tmdb;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Obtiene la programación de un canal desde la dirección que el usuario declaró en su ficha (la web oficial del canal):
/// un JSON cuyo formato se detecta solo, un XMLTV o una página HTML leída con una receta. Es una fuente más, que compite
/// con las guías XMLTV por canal.
/// </summary>
internal sealed class OwnGuideService
{
    private const int MaxBytes = 8 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;

    internal OwnGuideService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Descarga y convierte la guía propia de un canal.
    /// </summary>
    /// <param name="channel">Canal con su dirección de guía.</param>
    /// <param name="key">Clave bajo la que se publica la guía del canal.</param>
    /// <param name="country">País del canal.</param>
    /// <param name="kind">Tipo del canal, para clasificar los programas.</param>
    /// <param name="zone">Zona horaria para las horas sin offset; null significa UTC.</param>
    /// <param name="recipesJson">Recetas HTML del usuario (lista JSON).</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>La guía, o un resultado con el motivo del fallo.</returns>
    internal async Task<OwnGuide> FetchAsync(ChannelEntry channel, string key, string? country, ChannelKind kind, TimeZoneInfo? zone, string? recipesJson, CancellationToken ct)
    {
        var template = channel.EpgPageUrl?.Trim() ?? string.Empty;
        var collected = await CollectAsync(template, channel, key, country, kind, zone, recipesJson, ct).ConfigureAwait(false);
        var records = collected.Xml;
        foreach (var program in WebGuideExtractor.Finish(collected.Web))
        {
            records.Add(ToRecord(program, key, channel.Name, kind, country, zone));
        }

        var channelElement = new XElement("channel", new XAttribute("id", key), new XElement("display-name", channel.Name));
        if (!string.IsNullOrWhiteSpace(collected.Logo))
        {
            channelElement.Add(new XElement("icon", new XAttribute("src", collected.Logo)));
        }

        var channelXml = channelElement.ToString(SaveOptions.DisableFormatting);
        return records.Count == 0
            ? new OwnGuide(channelXml, records, collected.Error ?? "No se reconoció ninguna programación en la respuesta.")
            : new OwnGuide(channelXml, records, null);
    }

    /// <summary>
    /// Pide la dirección tal como lo haría una sincronización y devuelve lo que reconoce, sin guardar nada.
    /// </summary>
    /// <param name="url">Dirección de la guía.</param>
    /// <param name="channelName">Nombre del canal, usado para elegir canal en un XMLTV de varios.</param>
    /// <param name="zone">Zona horaria para las horas sin offset; null significa UTC.</param>
    /// <param name="recipesJson">Recetas HTML del usuario (lista JSON).</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Formato detectado, total y los primeros programas.</returns>
    internal async Task<GuidePreview> PreviewAsync(string url, string channelName, TimeZoneInfo? zone, string? recipesJson, CancellationToken ct)
    {
        var channel = new ChannelEntry { Name = channelName, EpgPageUrl = url };
        // La prueba pide solo la primera página y tiene un límite global: comprobar una dirección no debe tardar más que eso.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(25));
        Collected collected;
        try
        {
            collected = await CollectAsync(url.Trim(), channel, "preview", null, ChannelKind.General, zone, recipesJson, limit.Token, firstPageOnly: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GuidePreview("—", 0, Array.Empty<GuidePreviewItem>(), "La web no respondió en 25 s desde este servidor. Puede estar lenta o bloqueando sus peticiones.");
        }

        var items = new List<GuidePreviewItem>();
        foreach (var p in WebGuideExtractor.Finish(collected.Web))
        {
            items.Add(new GuidePreviewItem(p.Start, p.Stop ?? p.Start.AddHours(1), p.Title));
        }

        foreach (var record in collected.Xml)
        {
            var start = EpgFilterService.ParseXmltvTimeOrNull(record.Start);
            var stop = EpgFilterService.ParseXmltvTimeOrNull(record.Stop) ?? start?.AddMinutes(30);
            if (start is { } s && stop is { } e)
            {
                items.Add(new GuidePreviewItem(s, e, record.Data.BestTitle));
            }
        }

        items = items.OrderBy(static i => i.Start).ToList();
        return new GuidePreview(collected.Format, items.Count, items.Take(10).ToList(), items.Count == 0 ? collected.Error ?? "No se reconoció ninguna programación en la respuesta." : null);
    }

    private static string Stamp(DateTimeOffset value, TimeZoneInfo? zone)
    {
        // Si hay zona configurada, el lector de la guía interpreta la hora de pared en esa zona e ignora el offset; por eso
        // se escribe la hora de pared de esa zona y no la UTC.
        if (zone is null)
        {
            return value.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
        }

        var local = TimeZoneInfo.ConvertTime(value, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        return local.DateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + $" {sign}{Math.Abs(offset.Hours):00}{Math.Abs(offset.Minutes):00}";
    }

    private static XmlProgrammeRecord ToRecord(WebProgramme program, string key, string channelName, ChannelKind kind, string? country, TimeZoneInfo? zone)
    {
        var stop = program.Stop ?? program.Start.AddHours(1);
        var element = new XElement(
            "programme",
            new XAttribute("start", Stamp(program.Start, zone)),
            new XAttribute("stop", Stamp(stop, zone)),
            new XAttribute("channel", key),
            new XElement("title", new XAttribute("lang", "es"), program.Title));
        if (!string.IsNullOrWhiteSpace(program.Description))
        {
            element.Add(new XElement("desc", new XAttribute("lang", "es"), program.Description));
        }

        if (!string.IsNullOrWhiteSpace(program.Image))
        {
            element.Add(new XElement("icon", new XAttribute("src", program.Image)));
        }

        var data = new EpgProgrammeData { TitleEs = program.Title, Description = program.Description, ExistingIconUrl = program.Image };
        return new XmlProgrammeRecord(element.ToString(SaveOptions.DisableFormatting), key, Stamp(program.Start, zone), Stamp(stop, zone), data, kind, channelName, country);
    }

    private static List<XmlProgrammeRecord> FromXmltv(string xml, ChannelEntry channel, string key, string? country, ChannelKind kind)
    {
        var result = new List<XmlProgrammeRecord>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return result;
        }

        var channels = doc.Descendants("channel").ToList();
        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(channel.TvgId))
        {
            chosen = channels.Select(static c => (string?)c.Attribute("id")).FirstOrDefault(id => string.Equals(id, channel.TvgId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        chosen ??= channels.Count == 1 ? (string?)channels[0].Attribute("id") : channels
            .Select(c => (Id: (string?)c.Attribute("id"), Score: c.Elements("display-name").Select(n => NameMatcher.Similarity(channel.Name, n.Value)).DefaultIfEmpty(0).Max()))
            .Where(static x => x.Score >= EpgChannelMatcher.NameThreshold)
            .OrderByDescending(static x => x.Score)
            .Select(static x => x.Id)
            .FirstOrDefault();
        if (chosen is null)
        {
            return result;
        }

        foreach (var programme in doc.Descendants("programme").Where(p => string.Equals((string?)p.Attribute("channel"), chosen, StringComparison.OrdinalIgnoreCase)))
        {
            programme.SetAttributeValue("channel", key);
            var text = programme.ToString(SaveOptions.DisableFormatting);
            result.Add(new XmlProgrammeRecord(text, key, (string?)programme.Attribute("start") ?? string.Empty, (string?)programme.Attribute("stop"), EpgFilterService.ParseProgrammeData(text), kind, channel.Name, country));
        }

        return result;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new System.IO.MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
            if (buffer.Length > MaxBytes)
            {
                break;
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<Collected> CollectAsync(string template, ChannelEntry channel, string key, string? country, ChannelKind kind, TimeZoneInfo? zone, string? recipesJson, CancellationToken ct, bool firstPageOnly = false)
    {
        var web = new List<WebProgramme>();
        var xmlProgrammes = new List<XmlProgrammeRecord>();
        if (!Uri.TryCreate(template.Replace("{", "x", StringComparison.Ordinal).Replace("}", "x", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new Collected("—", web, xmlProgrammes, "La dirección de la guía no es una URL http(s) válida.", null);
        }

        var recipe = HtmlGuideRecipes.Find(template, recipesJson);
        var client = _httpClientFactory.CreateClient("JellyTV");

        // El día "de hoy" es el de la zona de la página: la de la receta si se lee HTML, la configurada en el resto.
        var dayZone = zone ?? TimeZoneInfo.Utc;
        if (recipe is not null)
        {
            try
            {
                dayZone = TimeZoneInfo.FindSystemTimeZoneById(recipe.Zone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                dayZone = TimeZoneInfo.Utc;
            }
        }

        var today = TimeZoneInfo.ConvertTime(DateTime.UtcNow, dayZone).Date;
        var urls = new List<(string Url, DateTime Day)>();
        if (recipe is not null && WebGuideExtractor.IsDated(recipe.DayUrl))
        {
            var baseUrl = template.TrimEnd('/');
            var past = Math.Clamp(recipe.PastDays, 0, 2);
            foreach (var offset in Enumerable.Range(-past, Math.Clamp(recipe.Days, 1, 7) + past))
            {
                var day = today.AddDays(offset);
                var dayTemplate = HtmlGuideRecipes.ApplyUrlParts(recipe, baseUrl, recipe.DayUrl.Replace("{url}", baseUrl, StringComparison.Ordinal));
                urls.Add((WebGuideExtractor.ExpandUrl(dayTemplate, day), day));
            }
        }
        else if (WebGuideExtractor.IsDated(template))
        {
            // Con marcador de fecha se pide el día anterior (el programa que cruza la medianoche), hoy y los dos siguientes.
            urls.AddRange(Enumerable.Range(-1, 4).Select(offset => (WebGuideExtractor.ExpandUrl(template, today.AddDays(offset)), today.AddDays(offset))));
        }
        else
        {
            urls.Add((template, today));
        }

        if (firstPageOnly && urls.Count > 1)
        {
            // Solo la página de hoy: con marcador de fecha la lista empieza ayer, que no es lo que se quiere comprobar.
            var current = urls.FindAll(u => u.Day == today);
            urls = current.Count > 0 ? current.GetRange(0, 1) : urls.GetRange(0, 1);
        }

        var format = "—";
        string? lastError = null;
        string? logo = null;
        for (var i = 0; i < urls.Count; i++)
        {
            var (url, day) = urls[i];
            try
            {
                if (i > 0 && recipe is { DelayMs: > 0 })
                {
                    await Task.Delay(Math.Min(recipe.DelayMs, 5000), ct).ConfigureAwait(false);
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"{(int)response.StatusCode} al pedir {url}";
                    continue;
                }

                var body = await ReadLimitedAsync(response, cts.Token).ConfigureAwait(false);
                var trimmed = body.TrimStart();
                if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<tv", StringComparison.OrdinalIgnoreCase))
                {
                    format = "XMLTV";
                    xmlProgrammes.AddRange(FromXmltv(body, channel, key, country, kind));
                }
                else if (trimmed.StartsWith('<'))
                {
                    if (recipe is null)
                    {
                        lastError = $"La respuesta es una página HTML y no hay una receta para {uri.Host}. Añade una en Ajustes → Recetas de guías HTML.";
                        break;
                    }

                    format = "HTML · " + (string.IsNullOrWhiteSpace(recipe.Name) ? recipe.Host : recipe.Name);
                    web.AddRange(HtmlGuideExtractor.Extract(body, recipe, day));
                    logo ??= HtmlGuideExtractor.ExtractLogo(body, recipe);
                }
                else
                {
                    format = "JSON";
                    web.AddRange(WebGuideExtractor.FromJsonRaw(body, zone, day));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                lastError = ex is TaskCanceledException ? $"Sin respuesta de {uri.Host} (tiempo agotado)." : $"No se pudo conectar con {uri.Host}: {ex.Message}";
            }
        }

        return new Collected(format, web, xmlProgrammes, lastError, logo);
    }

    private sealed record Collected(string Format, List<WebProgramme> Web, List<XmlProgrammeRecord> Xml, string? Error, string? Logo);
}
