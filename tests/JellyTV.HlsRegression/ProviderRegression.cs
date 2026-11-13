using System.Net;
using System.Net.Http;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyTV.Configuration;
using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>Los proveedores FAST (Pluto, Plex, Tubi) se leen con respuestas reales guardadas; con --live se prueban contra la red.</summary>
internal static class ProviderRegression
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "providers", name));

    internal static async Task RunAsync()
    {
        // ── Direcciones internas ──
        const string plexId = "608049aefa2b8ae93c2c3a63-67db3546d66f778a752f7eaa";
        var channelUrl = ProviderUrl.Channel("plex", plexId);
        Check(ProviderUrl.IsProviderUrl(channelUrl) && !ProviderUrl.IsProviderUrl("https://example.com/a.m3u8"), "solo jtv-provider:// es una dirección interna");
        Check(ProviderUrl.TryParseChannel(channelUrl, out var parsedProvider, out var parsedId) && parsedProvider == "plex" && parsedId == plexId, "la dirección de un canal se lee de vuelta");
        Check(ProviderUrl.TryParseGuide(ProviderUrl.Guide("tubi"), out var guideProvider) && guideProvider == "tubi" && !ProviderUrl.TryParseChannel(ProviderUrl.Guide("tubi"), out _, out _), "la dirección de la guía no se confunde con la de un canal");

        // ── Pluto ──
        var session = PlutoProvider.ParseBoot(Fixture("pluto-boot.json"));
        Check(session.Country == "MX" && session.StitcherServer.StartsWith("https://", StringComparison.Ordinal), "boot: país y servidor de streams");
        Check(PlutoProvider.JwtExpiry(session.Token) == DateTimeOffset.FromUnixTimeSeconds(1794558655), "la caducidad se lee del JWT");
        Check(PlutoProvider.JwtExpiry("no-es-un-jwt") is null, "un token que no es JWT no rompe nada");
        var plutoStream = PlutoProvider.StreamUrl(session, "5dcde437229eff00091b6c30");
        Check(plutoStream.Contains("/v2/stitch/hls/channel/5dcde437229eff00091b6c30/master.m3u8?", StringComparison.Ordinal) && plutoStream.Contains("&jwt=" + session.Token, StringComparison.Ordinal), "dirección de reproducción de Pluto con su JWT");
        var plutoChannels = PlutoProvider.ParseChannels(Fixture("pluto-channels.json"), Fixture("pluto-categories.json"), session.Country);
        Check(plutoChannels.Count == 2 && plutoChannels[0].Id == "5dcde437229eff00091b6c30" && plutoChannels[0].Number == 18, "canales de Pluto");
        Check(plutoChannels[0].Logo!.Contains("colorLogoPNG", StringComparison.Ordinal) && plutoChannels[0].Group is not null && plutoChannels[0].Country == "MX", "logo a color, categoría y país");
        var plutoProgrammes = PlutoProvider.ParseTimelines(Fixture("pluto-timelines.json"));
        Check(plutoProgrammes.Count == 2 && plutoProgrammes.All(p => p.ChannelId == "5dcb62e63d4d8f0009f36881" && p.Stop > p.Start && p.Title.Length > 0), "guía de Pluto");

        // ── Plex ──
        var plexChannels = PlexProvider.ParseChannels(Fixture("plex-channels.json"));
        Check(plexChannels.Count == 2 && plexChannels.All(c => c.Country == "MX"), "Plex: los canales ocultos se omiten y el país sale del título");
        var pelimex = plexChannels.Single(c => c.Name == "PeliMex");
        Check(pelimex.Id == plexId && pelimex.Logo!.EndsWith(".png", StringComparison.Ordinal), "PeliMex con el id largo de Plex y su logo");
        var grid = PlexProvider.ParseGrid(Fixture("plex-grid.json"), plexId);
        Check(grid.Count == 2 && grid.All(p => p.ChannelId == plexId && p.Stop > p.Start), "guía de Plex");
        Check(grid[1].Title == "Serie de prueba" && grid[1].SubTitle == "Episodio 1" && grid[0].SubTitle is null, "un episodio lleva la serie como título y su nombre como subtítulo");

        // ── Tubi ──
        var groups = TubiProvider.ParseLivePage(Fixture("tubi-live.html"));
        Check(groups.Count == 2 && groups.Values.Contains("Noticias") && groups.Values.Contains("Deportes"), "Tubi: los ids y categorías salen del JSON de la página (con undefined y new Date)");
        var tubiChannels = TubiProvider.ParseChannels(Fixture("tubi-programming.json"), groups);
        Check(tubiChannels.Count == 2 && tubiChannels.All(c => c.Logo is not null && c.Group is not null), "canales de Tubi");
        Check(TubiProvider.ParseProgrammes(Fixture("tubi-programming.json")).Count >= 2, "guía de Tubi");
        using (var client = new HttpClient(new StubHandler(Fixture("tubi-programming.json"))))
        {
            var stream = await new TubiProvider().ResolveStreamAsync(tubiChannels[0].Id, client, CancellationToken.None);
            Check(stream.Url.StartsWith("https://", StringComparison.Ordinal) && !stream.Url.Contains('?', StringComparison.Ordinal) && stream.Token is null, "Tubi: la dirección va sin el token (se abre y redirige a uno nuevo)");
        }

        // ── Runtime ──
        var runtimeChannels = RuntimeProvider.ParseChannels(Fixture("runtime-search.json"));
        Check(runtimeChannels.Count == 2 && runtimeChannels[0].Id == "3270-3269" && runtimeChannels[0].Name == "Runtime Español", "Runtime: el id junta el del video (reproductor) y el del canal (guía)");
        var runtimeUrl = RuntimeProvider.ExtractPlaylistUrl(Fixture("runtime-player.js"));
        Check(runtimeUrl is not null && runtimeUrl.StartsWith("https://stream.ads.ottera.tv/playlist.m3u8?", StringComparison.Ordinal) && !runtimeUrl.Contains('\\', StringComparison.Ordinal), "Runtime: la dirección HLS sale del script del reproductor sin las barras escapadas");
        Check(RuntimeProvider.ExtractPlaylistUrl("sin dirección") is null, "Runtime: un canal que no se ofrece no inventa una dirección");
        var runtimeGuide = RuntimeProvider.ParseProgrammes(Fixture("runtime-segments.json"), "3270-3269");
        Check(runtimeGuide.Count == 2 && runtimeGuide.All(p => p.ChannelId == "3270-3269" && p.Stop > p.Start) && runtimeGuide[1].Description == "Descripción larga.", "Runtime: guía con la descripción larga si falta la corta");
        using (var client = new HttpClient(new StubHandler(Fixture("runtime-page.html"))))
        {
            // La página de prueba solo trae el token: la dirección sale de otro script, así que aquí se espera el error claro.
            string? message = null;
            try
            {
                await new RuntimeProvider().ResolveStreamAsync("3270-3269", client, CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
            }

            Check(message is not null && message.Contains("no entregó", StringComparison.Ordinal), "Runtime: sin dirección en la respuesta, el error lo explica");
        }

        // ── Guía en XMLTV ──
        var guidePath = Path.Combine(Path.GetTempPath(), "jellytv-provider-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            ProviderGuideWriter.Write(guidePath, plutoChannels, plutoProgrammes.Append(new ProviderProgramme("5dcde437229eff00091b6c30", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "Con\u0001control", null, "R&B <3", null, null)).ToList());
            var xml = XDocument.Load(guidePath).Root!;
            Check(xml.Elements("channel").Count() == 2 && xml.Elements("programme").Count() == 3, "XMLTV: canales y programas");
            Check(xml.Elements("channel").First().Attribute("id")!.Value == "5dcde437229eff00091b6c30", "el id del canal del XMLTV es el id del proveedor (el tvg-id)");
            Check(xml.Elements("programme").Any(p => p.Element("title")!.Value == "Concontrol" && p.Element("desc")!.Value == "R&B <3"), "XMLTV: caracteres de control fuera y texto escapado");
            Check(xml.Elements("programme").First().Attribute("start")!.Value.EndsWith(" +0000", StringComparison.Ordinal), "horas en UTC");
        }
        finally
        {
            File.Delete(guidePath);
        }

        // ── Importación ──
        var config = new PluginConfiguration();
        var pluto = new PlutoProvider();
        var result = ProviderCatalogService.Import(config, pluto, plutoChannels, ["5dcde437229eff00091b6c30", "no-existe"], "us");
        Check(result == (1, 0, 0, true), "se importa solo lo que está en el catálogo y se da de alta la guía");
        var imported = config.Channels.Single();
        Check(imported.Name == "Pluto TV Cine Estelar" && imported.Type == ChannelEntry.FastType && imported.TvgId == "5dcde437229eff00091b6c30" && imported.Country == "MX", "el canal importado lleva su nombre, tipo FAST, tvg-id y país");
        Check(imported.Sources.Count == 1 && imported.Sources[0].Url == ProviderUrl.Channel("pluto", "5dcde437229eff00091b6c30") && imported.Sources[0].ExitCountry == "US", "su única fuente es la dirección interna, con el país de salida normalizado");
        Check(config.EpgSources.Single().Url == ProviderUrl.Guide("pluto"), "la guía del proveedor queda como fuente EPG");
        var again = ProviderCatalogService.Import(config, pluto, plutoChannels, ["5dcde437229eff00091b6c30"], null);
        Check(again == (0, 1, 0, false) && config.Channels.Count == 1 && config.EpgSources.Count == 1, "importar dos veces no duplica ni el canal ni la guía");
        Check(ProviderCatalogService.CountImported(config, "pluto") == 1 && ProviderCatalogService.CountImported(config, "plex") == 0, "el conteo es por proveedor");

        // ── Categorías: las de cada proveedor se reducen a las de Jellyfin (más Music y General) ──
        Check(ProviderCategories.Map("Películas", "Pluto TV Cine Estelar") == "Movies" && ProviderCategories.Map("Noticias", "N+") == "News" && ProviderCategories.Map("Deportes", "Fox") == "Sports", "categorías de proveedor a Movies, News y Sports (con acentos)");
        Check(ProviderCategories.Map("Kids & Family", "Nick") == "Kids" && ProviderCategories.Map("Infantil", "x") == "Kids" && ProviderCategories.Map("Music", "x") == "Music" && ProviderCategories.Map("Drama", "x") == "Series", "Kids, Music y Series");
        Check(ProviderCategories.Map(null, "Runtime Crimen") == "Series" && ProviderCategories.Map(null, "AYM Sports TV") == "Sports" && ProviderCategories.Map("Variedad", "Canal X") == "General" && ProviderCategories.Map(null, null) == "General", "sin categoría se deduce del nombre y lo demás es General");
        Check(ProviderCategories.Map("Películas infantiles", "x") == "Kids", "un canal infantil de películas es Kids, no Movies");
        // ── Respaldo de un canal que ya existe ──
        var own = new ChannelEntry { Name = "Mi Canal", Type = ChannelEntry.DirectType, Url = "https://oficial.example/live.m3u8", IsEnabled = true };
        config.Channels.Add(own);
        var secondId = plutoChannels[1].Id;
        var backup = ProviderCatalogService.Import(config, pluto, plutoChannels, [secondId], "us", new Dictionary<string, string> { [secondId] = own.Id });
        Check(backup == (0, 0, 1, false) && config.Channels.Count == 2, "como respaldo no se crea un canal nuevo ni se toca la guía");
        Check(own.Sources.Count == 2 && own.Sources[0].Url == "https://oficial.example/live.m3u8" && own.Sources[1].Url == ProviderUrl.Channel("pluto", secondId), "la fuente oficial sigue primero y la del proveedor va de respaldo");
        Check(own.Sources[1].Type == ChannelEntry.FastType && own.Sources[1].ExitCountry == "US" && own.Sources[1].Label == "Pluto TV" && own.Type == ChannelEntry.DirectType, "el respaldo es FAST con su país de salida y el canal sigue siendo Direct");
        var repeated = ProviderCatalogService.Import(config, pluto, plutoChannels, [secondId], "us", new Dictionary<string, string> { [secondId] = own.Id });
        Check(repeated == (0, 1, 0, false) && own.Sources.Count == 2, "repetir el respaldo no duplica la fuente");
        var gone = ProviderCatalogService.Import(config, pluto, plutoChannels, ["5dcde437229eff00091b6c30"], null, new Dictionary<string, string> { ["5dcde437229eff00091b6c30"] = "ya-no-existe" });
        Check(gone.Existing == 1, "un canal ya importado no se vuelve a añadir aunque se pida como respaldo");

        Console.WriteLine("PASS: proveedores FAST (Pluto, Plex, Tubi, Runtime) -> canales, guía XMLTV, direcciones internas e importación.");
    }

    /// <summary>Prueba cada proveedor contra su servicio real: catálogo, dirección de reproducción, lista HLS y guía.</summary>
    internal static async Task LiveCheckAsync()
    {
        using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        foreach (IChannelProvider provider in new IChannelProvider[] { new PlutoProvider(), new PlexProvider(), new TubiProvider(), new RuntimeProvider() })
        {
            var channels = await provider.ListChannelsAsync(client, CancellationToken.None);
            Check(channels.Count > 0, provider.Name + ": el catálogo no puede venir vacío");
            var sample = channels.Take(3).ToList();
            var stream = await provider.ResolveStreamAsync(sample[0].Id, client, CancellationToken.None);
            var master = await client.GetStringAsync(stream.Url);
            Check(master.StartsWith("#EXTM3U", StringComparison.Ordinal), provider.Name + ": la dirección resuelta debe devolver una lista HLS");
            var programmes = await provider.GetProgrammesAsync(client, sample, 24, CancellationToken.None);
            Check(programmes.Count > 0, provider.Name + ": la guía no puede venir vacía");
            Console.WriteLine($"LIVE {provider.Name}: {channels.Count} canales ({channels[0].Country ?? "?"}), '{sample[0].Name}' reproduce, {programmes.Count} programas de {sample.Count} canales.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Responde siempre lo mismo, para probar a un proveedor sin red.</summary>
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
