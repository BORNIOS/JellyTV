using Jellyfin.Plugin.JellyTV.Services;

/// <summary>Names taken from the real public per-country XMLTV files: the automatic guide must pair them with the channels.</summary>
internal static class AutoGuideRegression
{
    internal static void Run()
    {
        // Dos fuentes reales, cada una con su estilo de nombres: la de iptv-epg.org ("MX - TUDN Mexico") y la de epgshare01.
        var iptvEpg = new List<EpgSourceChannel>
        {
            new("TUDNMexico.mx", ["MX - TUDN Mexico"], "MX"),
            new("LasEstrellas.mx", ["MX - Las Estrellas"], "MX"),
            new("FOXSports.mx", ["MX - FOX Sports"], "MX"),
            new("FOXSports2.mx", ["MX - FOX Sports 2"], "MX"),
            new("AztecaUno.mx", ["MX - Azteca Uno"], "MX"),
            new("ESPN.mx", ["MX - ESPN"], "MX"),
            new("ESPN2.mx", ["MX - ESPN 2"], "MX"),
        };
        var epgShare = new List<EpgSourceChannel>
        {
            new("Canal.TUDN.(México).mx", ["Canal TUDN (México)"], "MX"),
            new("Canal.Fox.Sports.(México).mx", ["Canal Fox Sports (México)"], "MX"),
            new("Canal.Fox.Sports.2.(México).mx", ["Canal Fox Sports 2 (México)"], "MX"),
            new("Canal.Azteca.Uno.mx", ["Canal Azteca Uno"], "MX"),
            new("ESPN.MX.mx", ["ESPN MX"], "MX"),
            new("ESPN.2.mx", ["ESPN 2"], "MX"),
        };
        var targets = new List<EpgTarget>
        {
            new("TUDN.mx", "TUDN", "TUDN.mx", "MX"),
            new("FoxSports.mx", "Fox Sports", "FoxSports.mx", "MX"),
            new("FoxSports2.mx", "Fox Sports 2", "FoxSports2.mx", "MX"),
            new("AztecaUno.mx", "Azteca Uno", "AztecaUno.mx", "MX"),
            new("LasEstrellas.mx", "Las Estrellas", "LasEstrellas.mx", "MX"),
            new("ESPN.mx", "ESPN", "ESPN.mx", "MX"),
            new("ESPN2.mx", "ESPN 2", "ESPN2.mx", "MX"),
        };
        foreach (var (label, source) in new[] { ("iptv-epg.org", iptvEpg), ("epgshare01", epgShare) })
        {
            var match = EpgChannelMatcher.Match(targets, source);
            if (match.Values.Select(static m => m.Key).Distinct().Count() != match.Count)
            {
                throw new InvalidOperationException("two guide channels claimed the same channel in " + label);
            }

            foreach (var key in source == iptvEpg ? targets.Select(static t => t.Key) : targets.Select(static t => t.Key).Where(static k => k != "LasEstrellas.mx"))
            {
                if (!match.Values.Any(m => m.Key == key))
                {
                    throw new InvalidOperationException($"automatic guide ({label}) did not pair {key}");
                }

                Console.WriteLine($"PASS automatic guide ({label}) pairs {key}");
            }
        }
    }
}

/// <summary>Opt-in check (<c>--live</c>) against the real public services; kept out of the default run because it needs the network.</summary>
internal static class AutoGuideLiveCheck
{
    internal static async Task RunAsync()
    {
        var http = new SimpleFactory();
        using var catalog = new Jellyfin.Plugin.JellyTV.Services.Identity.IptvOrgCatalog(http, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var tudn = await catalog.GetByIdAsync("TUDN.mx", CancellationToken.None);
        Console.WriteLine($"LIVE iptv-org TUDN.mx -> {tudn?.Name} | {tudn?.Country} | gatotv={tudn?.GatoTv} | logo={tudn?.Logo}");
        var found = await catalog.FindAsync("Fox Deportes", null, CancellationToken.None);
        Console.WriteLine("LIVE iptv-org 'Fox Deportes' -> " + string.Join(", ", found.Where(c => c.Name.Contains("Deportes", StringComparison.OrdinalIgnoreCase)).Select(c => c.Id)));
        var urls = await new AutoGuideCatalog(http).UrlsForAsync(["MX", "GB", "US"], CancellationToken.None);
        Console.WriteLine("LIVE auto guide files -> " + string.Join(", ", urls.Select(u => u[(u.LastIndexOf('/') + 1)..])));
    }

    private sealed class SimpleFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(3) };
    }
}
