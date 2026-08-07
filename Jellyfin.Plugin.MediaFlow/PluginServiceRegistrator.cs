using Jellyfin.Plugin.MediaFlow.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaFlow;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<QbittorrentClient>();
        serviceCollection.AddSingleton<MediaParser>();
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<MediaResolver>();
        serviceCollection.AddSingleton<PathMapper>();
        serviceCollection.AddSingleton<HardLinkService>();
        serviceCollection.AddSingleton<ImportStateStore>();
        serviceCollection.AddHostedService<MediaFlowWorker>();
    }
}
