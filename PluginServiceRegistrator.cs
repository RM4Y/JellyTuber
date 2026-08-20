using Jellyfin.Plugin.JellyTuber.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyTuber;

/// <summary>
/// Registers this plugin's own DI services with Jellyfin's host.
///
/// Deliberately a separate, standalone class rather than a method on
/// <see cref="Plugin"/> itself: Jellyfin's PluginManager instantiates
/// whatever implements <see cref="IPluginServiceRegistrator"/> via
/// <c>Activator.CreateInstance</c> with NO constructor arguments, entirely
/// independently of the already-constructed <see cref="Plugin"/> singleton
/// (which needs <c>IApplicationPaths</c>/<c>IXmlSerializer</c> and has no
/// parameterless constructor). Implementing this interface directly on
/// <see cref="Plugin"/> makes that instantiation throw
/// <see cref="System.MissingMethodException"/> at startup and, confirmed
/// against a real server, gets the WHOLE plugin disabled - not just this
/// registration - since PluginManager treats a failed service registration
/// as a fatal load error for the plugin.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Consulted whenever a client asks for playback info on one of our
        // .strm items - see JellyTuberMediaSourceProvider's own doc comment
        // for why this exists (skips Jellyfin's slow first-play ffprobe).
        serviceCollection.AddSingleton<IMediaSourceProvider, JellyTuberMediaSourceProvider>();

        // Tears down a video's HLS session immediately on a real playback
        // stop instead of waiting for the idle sweep - see
        // PlaybackStopSessionCleaner's own doc comment.
        serviceCollection.AddHostedService<PlaybackStopSessionCleaner>();
    }
}
