using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.JellyTuber.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyTuber;

/// <summary>
/// JellyTuber - indexes channels/playlists through the YouTube Data API v3
/// and resolves playback on demand with yt-dlp.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        MigrateLegacyYtDlpSettings();

        var dataFolderPath = DataFolderPath;
        System.Threading.Tasks.Task.Run(() => Services.VideoCache.PurgeLegacyCaches(dataFolderPath));
    }

    public override string Name => "JellyTuber";

    public override string Description =>
        "Index YouTube channels and playlists via the YouTube Data API, stream on demand with yt-dlp.";

    // Keep this GUID stable across releases so Jellyfin recognises updates.
    public override Guid Id => Guid.Parse("b9f8e1a2-3c4d-4e5f-8a7b-1c2d3e4f5a6b");

    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Guards every read-modify-write sequence against config.Sources /
    /// UserChannels / UserVideos (plain Lists, not thread-safe) so concurrent
    /// self-service requests and the background sync task can't interleave
    /// and corrupt them or throw during enumeration.
    /// </summary>
    public static readonly object ConfigLock = new();

    /// <summary>Persist the current configuration (callable from controllers).</summary>
    public void Save() => SaveConfiguration();

    /// <summary>
    /// The dashboard page reads the whole configuration, edits only the admin
    /// settings, and posts the whole thing back - so a channel, video or share
    /// code a user added in between used to be silently wiped by the stale
    /// copy. Those lists are only ever edited through the self-service
    /// endpoints, so the live ones always win here. They're carried over as
    /// the same List instances, so an edit racing with this swap still lands.
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        lock (ConfigLock)
        {
            if (configuration is PluginConfiguration incoming)
            {
                var current = Configuration;
                incoming.UserChannels = current.UserChannels;
                incoming.UserVideos = current.UserVideos;
                incoming.ShareCodes = current.ShareCodes;
            }

            base.UpdateConfiguration(configuration);
        }
    }

    /// <summary>
    /// Older versions of this plugin required manually setting a yt-dlp
    /// binary path and a <c>--js-runtimes deno:&lt;path&gt;</c> flag; both
    /// yt-dlp and Deno are now downloaded and managed automatically (see
    /// <see cref="Services.ExternalTools"/>). A config saved by an older
    /// version can still have that flag sitting in
    /// <see cref="PluginConfiguration.YtDlpExtraArgs"/> - strip it out once
    /// so the settings page reflects that nothing manual is needed anymore,
    /// and so it can never shadow the path this plugin resolves itself.
    /// </summary>
    private void MigrateLegacyYtDlpSettings()
    {
        var args = Configuration.YtDlpExtraArgs;
        if (string.IsNullOrEmpty(args) || !args.Contains("--js-runtimes", StringComparison.Ordinal))
        {
            return;
        }

        Configuration.YtDlpExtraArgs = string.Join(' ', Services.ExternalTools.StripJsRuntimesArg(args));
        SaveConfiguration();
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }
}
