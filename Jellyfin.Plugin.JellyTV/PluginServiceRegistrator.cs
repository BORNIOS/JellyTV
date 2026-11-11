using Jellyfin.Plugin.JellyTV.ScheduledTask;
using Jellyfin.Plugin.JellyTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
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

        // Ruta nativa: JellyTVLiveTvService se registra siempre, pero solo publica canales cuando
        // LiveTvMode = "Native". En modo "M3u" devuelve una lista vacía, así que nunca duplica los
        // canales del tuner M3U. La EPG nativa sale de GetProgramsAsync (epg.xml local).
        serviceCollection.AddSingleton<ILiveTvService, JellyTVLiveTvService>();

        // Jellyfin fuerza IsInterlaced=true en los canales de plugins y los perfiles de cliente lo
        // rechazan: estos filtros devuelven DirectPlay cuando el perfil del cliente sí lo admite.
        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.Add<PlaybackInfoProfileCaptureFilter>();
            options.Filters.Add<PlaybackInfoDirectFilter>();
        });
    }
}
