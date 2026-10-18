using Jellyfin.Plugin.JellyTV.ScheduledTask;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyTV;

/// <summary>
/// Registers plugin services with the Jellyfin DI container.
/// Jellyfin discovers and calls this automatically when loading the assembly.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Dos tareas con cadencia y naturaleza distintas:
        //  - Health/M3U (ChannelHealthCheckTask): frecuente (minutos), ligera; regenera tuner.m3u.
        //  - EPG (SourceSyncTask): pesada (horas, default 24 h); regenera epg.xml.
        // Cada una encola el "Refresh Guide" de Jellyfin cuando produce cambios (GuideRefresh).
        serviceCollection.AddSingleton<IScheduledTask, ChannelHealthCheckTask>();
        serviceCollection.AddSingleton<IScheduledTask, SourceSyncTask>();

        // Ruta B (M3U Tuner nativo): los canales se entregan SOLO vía /JellyTV/tuner.m3u registrado
        // como tuner M3U en Jellyfin. NO se registra JellyTVLiveTvService como ILiveTvService para
        // evitar canales duplicados (nativo + tuner). La EPG se enlaza añadiendo
        // {DataPath}/JellyTV/epg.xml como proveedor XMLTV (mapeo por tvg-id).
        // serviceCollection.AddSingleton<ILiveTvService, JellyTVLiveTvService>();
    }
}
