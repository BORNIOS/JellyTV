using JellyTV.ScheduledTask;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace JellyTV;

/// <summary>
/// Registers plugin services with the Jellyfin DI container.
/// Jellyfin discovers and calls this automatically when loading the assembly.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IScheduledTask, SourceSyncTask>();
    }
}
