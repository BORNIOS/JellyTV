using Jellyfin.Plugin.JellyTV.Models;
using Jellyfin.Plugin.JellyTV.Services;

/// <summary>Una fuente Directo con horas de DVR en la playlist debe pasar por el relay; una normal no.</summary>
internal static class DvrWindowRegression
{
    internal static void Run()
    {
        const string url = "https://dvr.example/rtesp/playlist.m3u8";
        var source = new ChannelSource { Url = url, Type = 0 };
        Check(!DeliveryPolicy.RequiresRelay(source), "Directo sin medir sigue directo");

        PlaylistWindows.Record(url, MediaPlaylist(5));
        Check(!DeliveryPolicy.RequiresRelay(source), "ventana normal (5 segmentos) sigue directa");

        PlaylistWindows.Record(url, MediaPlaylist(10800));
        Check(DeliveryPolicy.RequiresRelay(source), "12 h de DVR (10 800 segmentos) pasa por el relay");

        PlaylistWindows.Record(url, MediaPlaylist(5));
        Check(!DeliveryPolicy.RequiresRelay(source), "si el proveedor acorta la ventana vuelve a directo");

        Console.WriteLine("PASS: ventana DVR desmesurada -> relay; ventana normal -> directo.");
    }

    private static string MediaPlaylist(int segments)
    {
        var sb = new System.Text.StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:1\n");
        for (var i = 0; i < segments; i++)
        {
            sb.Append("#EXTINF:4.000,\nseg_").Append(i).Append(".ts\n");
        }

        return sb.ToString();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
