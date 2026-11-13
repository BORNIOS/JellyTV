using Jellyfin.Plugin.JellyTV.Services;

/// <summary>Las guías locales (Pluto, Plex, Tubi...) usan su propio id de canal: la búsqueda de identidad debe ofrecerlo con su logo.</summary>
internal static class GuideIdentityRegression
{
    internal static async Task RunAsync()
    {
        // Lectura real de un canal de Pluto: el <icon> debe llegar junto con el id y el nombre.
        var xml = "<tv>\n  <channel id=\"5dcdde78f080d900098550e4\">\n    <display-name>Pluto TV Cine Comedia</display-name>\n"
            + "    <icon src=\"https://images.pluto.tv/channels/5dcdde78f080d900098550e4/colorLogoPNG_1762281945243.png\"/>\n  </channel>\n"
            + "  <channel id=\"sin-logo\"><display-name>Canal sin logo</display-name></channel>\n</tv>";
        var path = Path.Combine(Path.GetTempPath(), "jellytv-guide-" + Guid.NewGuid().ToString("N") + ".xml");
        await File.WriteAllTextAsync(path, xml);
        try
        {
            var read = typeof(EpgFilterService).GetMethod("ReadSourceChannelsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var channels = await (Task<List<EpgSourceChannel>>)read.Invoke(null, [path])!;
            Check(channels.Count == 2, "se leen los dos canales");
            Check(channels[0].Id == "5dcdde78f080d900098550e4" && channels[0].Logo!.EndsWith("colorLogoPNG_1762281945243.png", StringComparison.Ordinal), "el icon de la fuente se conserva");
            Check(channels[1].Logo is null, "un canal sin icon queda sin logo");
        }
        finally
        {
            File.Delete(path);
        }

        var entries = new List<GuideChannelEntry>
        {
            new("Pluto", "5dcdde78f080d900098550e4", ["Pluto TV Cine Comedia"], "https://images.pluto.tv/logo.png"),
            new("Pluto", "5dcdde78f080d900098550e5", ["Pluto TV Cine Terror"], null),
            new("Mi guía", "ESPN.mx", ["ESPN México"], null),
        };

        var found = GuideChannelCatalog.Search(entries, "Cine Comedia", null, null);
        Check(found.Count > 0 && found[0].TvgId == "5dcdde78f080d900098550e4", "el nombre encuentra el canal y devuelve su id de guía");
        Check(found[0].LogoUrl == "https://images.pluto.tv/logo.png" && found[0].Sources == "guía Pluto", "devuelve el logo y la guía de origen");
        Check(found.All(c => c.TvgId != "5dcdde78f080d900098550e5"), "otro canal de la misma guía no se confunde");
        Check(GuideChannelCatalog.Search(entries, "Canal inexistente", null, null).Count == 0, "sin parecido no hay candidatos");
        Check(GuideChannelCatalog.Search(entries, "otro nombre", null, "5dcdde78f080d900098550e5").Any(c => c.Score == 1.0), "el id que ya tiene el canal gana a cualquier nombre");
        Check(GuideChannelCatalog.Search(entries, "ESPN", "US", null).Count == 0, "un id con sufijo de otro país no se ofrece");
        Check(GuideChannelCatalog.Search(entries, "ESPN", "MX", null).Count == 1, "el mismo país sí");

        Console.WriteLine("PASS: guías locales como candidatos de identidad (id de canal, logo, guía de origen, país).");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
