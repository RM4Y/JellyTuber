using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTuber.Configuration;

/// <summary>
/// A single channel or playlist the user wants to sync.
/// </summary>
public class SourceItem
{
    /// <summary>Full YouTube channel or playlist URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Display name (also used as the top-level folder name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// How videos are presented. Plain string ("Series" or "Movies") to keep
    /// JSON/XML round-tripping rock solid (enums can silently break config save).
    /// </summary>
    public string Mode { get; set; } = "Series";

    /// <summary>
    /// Exclude YouTube Shorts from this source. On by default. When true, each
    /// short-enough candidate is verified against the real /shorts/ URL.
    /// </summary>
    public bool ExcludeShorts { get; set; } = true;

    /// <summary>
    /// Optional destination root for this source. When empty, the global
    /// LibraryFolder is used. Point different sources at different folders to
    /// build per-user libraries (each folder = its own Jellyfin library, with
    /// access restricted per user).
    /// </summary>
    public string DestinationFolder { get; set; } = string.Empty;

    /// <summary>
    /// How many of the most recent videos to keep in the library for this
    /// channel/playlist. Clamped to 10-50 at sync time. Older videos beyond
    /// this count are pruned from disk.
    /// </summary>
    public int MaxVideos { get; set; } = 25;
}

/// <summary>
/// Plugin settings, persisted by Jellyfin as XML.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>YouTube Data API v3 key (from Google Cloud Console).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Folder inside one of your Jellyfin libraries where items are written.</summary>
    public string LibraryFolder { get; set; } = "/media/youtube";

    /// <summary>
    /// Address your playback devices use to reach this Jellyfin server.
    /// Written into the .strm files so clients know where to resolve streams.
    /// </summary>
    public string JellyfinAddress { get; set; } = "http://localhost:8096";

    /// <summary>
    /// Maximum number of channels returned by the self-service search box.
    /// YouTube allows 1..50 per call (search.list costs 100 quota units each).
    /// </summary>
    public int SearchResultsLimit { get; set; } = 20;

    /// <summary>
    /// When true, the sync task automatically creates a Jellyfin "Movies"
    /// library named "Youtube@{User}" pointing at LibraryFolder/{User} for each
    /// user that has content, if such a library does not already exist.
    /// </summary>
    public bool AutoCreateUserLibraries { get; set; } = true;

    /// <summary>
    /// How long (minutes) a resolved stream URL is cached before re-asking
    /// yt-dlp. YouTube URLs stay valid ~6h, so a long cache makes re-plays (and
    /// the gap between Jellyfin's probe and actual playback) instant. Kept under
    /// 6h to avoid ever serving an expired link.
    /// </summary>
    public int LinkCacheMinutes { get; set; } = 300;

    /// <summary>Fetch per-video details (duration etc.). Costs a little extra quota.</summary>
    public bool FetchVideoDetails { get; set; } = true;

    /// <summary>
    /// Only probe videos at or below this duration (seconds) for "is it a Short".
    /// Anything longer is assumed to be a normal video (max Short length is 3 min).
    /// Keeps the /shorts/ check cheap on big channels.
    /// </summary>
    public int ShortsMaxProbeSeconds { get; set; } = 180;

    /// <summary>
    /// Maximum video height to request (e.g. 720, 1080, 1440, 2160).
    /// Jellyfin then handles any transcoding/HW acceleration itself.
    /// </summary>
    public int MaxHeight { get; set; } = 1080;

    /// <summary>
    /// Optional verbatim yt-dlp format selector for the HLS path. If set,
    /// overrides the automatic selector. Leave empty to use the automatic logic.
    /// </summary>
    public string YtDlpFormatOverride { get; set; } = string.Empty;

    /// <summary>
    /// Extra yt-dlp arguments (advanced), space-separated. yt-dlp and Deno
    /// are both bundled and self-managed by the plugin - no path setup
    /// needed - and the Deno JS runtime flag (required for YouTube's
    /// n-challenge so 1080p+ formats resolve) is wired in automatically.
    /// This field is only for further optional tweaks.
    /// </summary>
    public string YtDlpExtraArgs { get; set; } = string.Empty;

    /// <summary>
    /// Maximum total size (GB) of the persistent video cache
    /// (see <see cref="Services.VideoCache"/>) before least-recently-accessed
    /// videos get evicted. 0 = unlimited.
    /// </summary>
    public int VideoCacheMaxGB { get; set; } = 20;

    /// <summary>
    /// How many videos may keep encoding in the background (no active
    /// viewer) at once, towards completing their cache. Keeps a burst of
    /// stopped/idle sessions from piling up and starving CPU from newly
    /// started playback - see <see cref="Services.PlaybackStopSessionCleaner"/>.
    /// </summary>
    public int MaxConcurrentBackgroundEncodes { get; set; } = 2;

    /// <summary>The channels / playlists to sync.</summary>
    public List<SourceItem> Sources { get; set; } = new();

    /// <summary>
    /// Channels added by Jellyfin users themselves via the self-service page.
    /// Each user's channels sync into a per-user folder (LibraryFolder/UserName).
    /// </summary>
    public List<UserChannel> UserChannels { get; set; } = new();

    /// <summary>
    /// Individual videos added by users via the self-service page (by URL).
    /// They sync into LibraryFolder/UserName/Mes Videos.
    /// </summary>
    public List<UserVideo> UserVideos { get; set; } = new();

    /// <summary>
    /// Per-user secret codes for /JellyTuber/Share?code=..&amp;url=.., which
    /// adds a video without a Jellyfin session (iOS Shortcut share flow).
    /// </summary>
    public List<UserShareCode> ShareCodes { get; set; } = new();
}

/// <summary>A user's secret share code (see <see cref="PluginConfiguration.ShareCodes"/>).</summary>
public class UserShareCode
{
    public string UserId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

/// <summary>A channel a specific Jellyfin user subscribed to via the self-service page.</summary>
public class UserChannel
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool ExcludeShorts { get; set; } = true;

    /// <summary>Channel avatar URL, shown on the self-service page.</summary>
    public string Thumbnail { get; set; } = string.Empty;

    /// <summary>
    /// How many of the most recent videos to keep in the library for this
    /// channel. User-editable on the self-service page, clamped to 10-50.
    /// </summary>
    public int MaxVideos { get; set; } = 25;
}

/// <summary>A single video a Jellyfin user added by URL via the self-service page.</summary>
public class UserVideo
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Thumbnail { get; set; } = string.Empty;

    /// <summary>Video description, written into the .nfo plot.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Original publish date (used for the .nfo). Stored at add time.</summary>
    public System.DateTime PublishedAt { get; set; } = System.DateTime.UtcNow;
}
