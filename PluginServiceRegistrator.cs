using Jellyfin.Plugin.JellyTuber.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyTuber;

/// <summary>
/// Auto-discovered by Jellyfin's plugin host at startup - registers
/// <see cref="JellyTuberMediaSourceProvider"/> so it's consulted whenever a
/// client asks for playback info on one of our .strm items.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IMediaSourceProvider, JellyTuberMediaSourceProvider>();
    }
}
