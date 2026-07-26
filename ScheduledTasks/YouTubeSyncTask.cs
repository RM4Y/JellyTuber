using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTuber.Configuration;
using Jellyfin.Plugin.JellyTuber.Services;
using Jellyfin.Plugin.JellyTuber.YouTube;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.ScheduledTasks;

/// <summary>
/// Indexes every configured source via the YouTube Data API and writes the
/// library structure. Runs every 6h by default; also triggerable manually
/// from Dashboard -> Scheduled Tasks.
/// </summary>
public class YouTubeSyncTask : IScheduledTask
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<YouTubeSyncTask> _logger;

    public YouTubeSyncTask(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<YouTubeSyncTask> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public string Name => "Sync YouTube (Fast)";
    public string Key => "JellyTuberSync";
    public string Description => "Index configured YouTube channels/playlists via the Data API.";
    public string Category => "JellyTuber";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogWarning("No configuration found; aborting.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _logger.LogError("YouTube API key is not set. Configure it in the plugin settings.");
            return;
        }

        // Effective sources = admin-configured sources + each user's self-added
        // channels (routed into a per-user folder: LibraryFolder/UserName).
        // Snapshot config.Sources/UserChannels under the lock: they're plain
        // Lists that the self-service endpoints can mutate concurrently, and
        // enumerating one mid-mutation throws.
        var effectiveSources = new List<SourceItem>();
        lock (Plugin.ConfigLock)
        {
            effectiveSources.AddRange(config.Sources);
            foreach (var uc in config.UserChannels)
            {
                if (string.IsNullOrWhiteSpace(uc.Url))
                {
                    continue;
                }

                effectiveSources.Add(new SourceItem
                {
                    Name = uc.Name,
                    Url = uc.Url,
                    Mode = "Series",
                    ExcludeShorts = uc.ExcludeShorts,
                    DestinationFolder = Path.Combine(config.LibraryFolder, SanitizeUser(uc.UserName))
                });
            }
        }

        if (effectiveSources.Count == 0)
        {
            _logger.LogInformation("No sources configured; nothing to do.");
            progress.Report(100);
            return;
        }

        var http = _httpClientFactory.CreateClient();
        var api = new YouTubeApiClient(http, config.ApiKey, _logger);
        var writer = new LibraryWriter(http, _logger);

        DateTime? cutoff = config.KeepDays > 0
            ? DateTime.UtcNow.AddDays(-config.KeepDays)
            : null;

        var baseAddress = config.JellyfinAddress.TrimEnd('/');
        var total = effectiveSources.Count;
        var done = 0;

        // Track every channel folder we (re)write this pass, plus the parent
        // folders to scan, so we can delete channels that were removed from the
        // list since the previous sync.
        var expectedRoots = new List<string>();
        var scanRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track whether this run actually changed anything, so the (heavier)
        // image/metadata refresh only runs when there is new content.
        var newItems = 0;
        var librariesCreated = false;

        foreach (var source in effectiveSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parentRoot = string.IsNullOrWhiteSpace(source.DestinationFolder)
                ? config.LibraryFolder
                : source.DestinationFolder;
            scanRoots.Add(parentRoot);
            expectedRoots.Add(Path.Combine(parentRoot, Sanitize(source.Name)));

            try
            {
                newItems += await SyncSourceAsync(source, config, api, writer, cutoff, baseAddress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync source {Name} ({Url})", source.Name, source.Url);
            }

            done++;
            progress.Report(done * 100.0 / total);
        }

        // Individual user-added videos -> LibraryFolder/UserName/Mes Videos
        try
        {
            var (videoRoots, videoWrites) = await SyncUserVideosAsync(config, api, writer, baseAddress, cancellationToken)
                .ConfigureAwait(false);
            expectedRoots.AddRange(videoRoots);
            newItems += videoWrites;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync user-added videos");
        }

        // Always scan the global library folder too (covers per-user folders
        // that lost all their channels).
        scanRoots.Add(config.LibraryFolder);

        try
        {
            writer.CleanupRemovedChannels(expectedRoots, scanRoots);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup of removed channels failed");
        }

        // Ensure each user has a "Youtube@{User}" movies library pointing at
        // their folder (created if missing), before the scan picks it up.
        if (config.AutoCreateUserLibraries)
        {
            try
            {
                librariesCreated = await EnsureUserLibrariesAsync(config).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not ensure per-user libraries");
            }
        }

        // Ask Jellyfin to scan the libraries so the freshly-written .strm files
        // (and removals) show up without waiting for the next scheduled scan.
        try
        {
            _logger.LogInformation("Sync done; queuing a Jellyfin library scan.");
            _libraryManager.QueueLibraryScan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue the Jellyfin library scan");
        }

        // Refresh metadata/images on the per-user libraries so each item picks
        // up its local poster/thumbnail (poster.jpg / *.jpg). Only when this run
        // added new content or created a library -- not on every idle sync.
        if (newItems > 0 || librariesCreated)
        {
            try
            {
                RefreshUserLibrariesMetadata();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not queue metadata refresh for user libraries");
            }
        }

        progress.Report(100);
    }

    private async Task<int> SyncSourceAsync(
        SourceItem source,
        PluginConfiguration config,
        YouTubeApiClient api,
        LibraryWriter writer,
        DateTime? cutoff,
        string baseAddress,
        CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(source.DestinationFolder)
            ? config.LibraryFolder
            : source.DestinationFolder;
        var sourceRoot = Path.Combine(root, Sanitize(source.Name));

        // Resolve the playlist id we'll enumerate.
        string playlistId;
        string title = source.Name;
        string description = string.Empty;
        string? thumb = null;

        var directPlaylist = YouTubeApiClient.ExtractPlaylistId(source.Url);
        if (directPlaylist is not null && !source.Url.Contains("/channel/", StringComparison.OrdinalIgnoreCase)
                                       && !source.Url.Contains("/@", StringComparison.OrdinalIgnoreCase)
                                       && !source.Url.Contains("/user/", StringComparison.OrdinalIgnoreCase))
        {
            playlistId = directPlaylist;
        }
        else
        {
            var channel = await api.ResolveChannelAsync(source.Url, ct).ConfigureAwait(false);
            if (channel is null || string.IsNullOrEmpty(channel.UploadsPlaylistId))
            {
                _logger.LogWarning("Could not resolve channel for {Url}", source.Url);
                return 0;
            }

            playlistId = channel.UploadsPlaylistId;
            title = string.IsNullOrEmpty(channel.Title) ? source.Name : channel.Title;
            description = channel.Description;
            thumb = channel.ThumbnailUrl;
        }

        var videos = await api.GetPlaylistVideosAsync(playlistId, cutoff, ct).ConfigureAwait(false);
        _logger.LogInformation("Source {Name}: {Count} videos within window", source.Name, videos.Count);

        // Merge any duplicate folders left over from title changes before this
        // fix existed, then index what remains by video id so a video whose
        // title changes from here on gets its folder renamed instead of a
        // duplicate written under the new title.
        writer.DeduplicateVideoFolders(sourceRoot);
        var videoIndex = writer.BuildVideoIndex(sourceRoot);

        // We need durations to detect Shorts, so fetch details if either the user
        // asked for them or this source excludes Shorts.
        var needDetails = config.FetchVideoDetails || source.ExcludeShorts;
        if (needDetails && videos.Count > 0)
        {
            await api.EnrichWithDetailsAsync(videos, ct).ConfigureAwait(false);
        }

        // Drop Shorts when this source excludes them. We check candidate videos
        // (short enough to plausibly BE a Short) with yt-dlp, IN PARALLEL with a
        // small bound and a per-call timeout, so the sync never stalls.
        if (source.ExcludeShorts)
        {
            var shorts = new ShortsDetector(config.YtDlpPath, _httpClientFactory.CreateClient(), _logger);

            // (a) Most reliable: subtract the channel's dedicated Shorts tab.
            var channelId = DeriveChannelId(playlistId, source.Url);
            if (channelId is not null)
            {
                try
                {
                    var tabShorts = await shorts.ListChannelShortIdsAsync(channelId, 200, ct).ConfigureAwait(false);
                    if (tabShorts.Count > 0)
                    {
                        // Delete any Short already written (e.g. exclusion was
                        // just re-enabled), then drop them from this run.
                        foreach (var sv in videos.FindAll(v => tabShorts.Contains(v.VideoId)))
                        {
                            writer.RemoveVideoIfExists(sourceRoot, source, sv, videoIndex);
                        }

                        var beforeTab = videos.Count;
                        videos = videos.FindAll(v => !tabShorts.Contains(v.VideoId));
                        _logger.LogInformation(
                            "Source {Name}: removed {N} Shorts via the channel Shorts tab ({Total} listed)",
                            source.Name, beforeTab - videos.Count, tabShorts.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Channel Shorts-tab filtering failed for {Name}", source.Name);
                }
            }

            // (b) Safety net: per-video probe on remaining short-duration videos.
            var threshold = config.ShortsMaxProbeSeconds > 0 ? config.ShortsMaxProbeSeconds : 180;

            // Split into clearly-long (kept as-is) and candidates to probe.
            var candidates = new List<YouTubeVideo>();
            foreach (var v in videos)
            {
                var secs = v.DurationSeconds();
                if (secs is null || secs <= threshold)
                {
                    candidates.Add(v);
                }
            }

            // Safety net: never let Shorts detection explode into hundreds of
            // probes (e.g. if durations are unavailable). The HTTP probe is
            // cheap, so the cap is generous; in the normal case (durations
            // present) candidates are only genuinely short videos anyway.
            const int maxProbes = 200;
            if (candidates.Count > maxProbes)
            {
                _logger.LogWarning(
                    "Source {Name}: {Count} Shorts candidates exceed the probe cap ({Cap}); " +
                    "checking the first {Cap}. This usually means many videos lack duration data.",
                    source.Name, candidates.Count, maxProbes, maxProbes);
                candidates = candidates.GetRange(0, maxProbes);
            }

            var shortIds = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (v, token) =>
                {
                    if (await shorts.IsShortAsync(v.VideoId, token).ConfigureAwait(false))
                    {
                        shortIds[v.VideoId] = true;
                    }
                }).ConfigureAwait(false);

            if (!shortIds.IsEmpty)
            {
                var before = videos.Count;
                // Delete any Short already written, then drop them from this run.
                foreach (var v in videos.FindAll(v => shortIds.ContainsKey(v.VideoId)))
                {
                    writer.RemoveVideoIfExists(sourceRoot, source, v, videoIndex);
                }

                videos = videos.FindAll(v => !shortIds.ContainsKey(v.VideoId));
                _logger.LogInformation("Source {Name}: skipped {Removed} Shorts", source.Name, before - videos.Count);
            }
        }

        var isSeries = !string.Equals(source.Mode, "Movies", StringComparison.OrdinalIgnoreCase);
        await writer.WriteChannelRootAsync(sourceRoot, title, description, thumb, isSeries, ct).ConfigureAwait(false);

        // Flat layout (no season folders): one chronological counter per channel.
        // Oldest first so episode numbers increase over time.
        videos.Reverse();

        var written = 0;
        var episode = 0;
        foreach (var video in videos)
        {
            ct.ThrowIfCancellationRequested();

            episode++;
            var strmTarget = $"{baseAddress}/JellyTuber/Stream/{video.VideoId}";

            var created = await writer.WriteVideoAsync(sourceRoot, source, video, episode, strmTarget, videoIndex, ct)
                .ConfigureAwait(false);
            if (created)
            {
                written++;
            }
        }

        _logger.LogInformation("Source {Name}: {Written} new videos written", source.Name, written);

        if (cutoff.HasValue)
        {
            writer.CleanupOldVideos(sourceRoot, cutoff.Value);
        }

        return written;
    }

    /// <summary>
    /// Writes each user's individually-added videos into
    /// LibraryFolder/UserName/Mes Videos, and prunes ones they removed.
    /// Returns the channel-root folders written (to protect them from the
    /// removed-channel cleanup pass). Manually-added videos ignore KeepDays.
    /// </summary>
    private async Task<(List<string> Roots, int Written)> SyncUserVideosAsync(
        PluginConfiguration config,
        YouTubeApiClient api,
        LibraryWriter writer,
        string baseAddress,
        CancellationToken ct)
    {
        const string VideosFolderName = "Mes Videos";
        var roots = new List<string>();
        var configDirty = false;
        var written = 0;

        // Snapshot under the lock so a concurrent self-service Add/Remove can't
        // mutate config.UserVideos (a plain List) while we enumerate/group it.
        List<UserVideo> snapshot;
        lock (Plugin.ConfigLock)
        {
            snapshot = config.UserVideos.Where(v => !string.IsNullOrWhiteSpace(v.VideoId)).ToList();
        }

        var byUser = snapshot.GroupBy(v => SanitizeUser(v.UserName));

        foreach (var group in byUser)
        {
            ct.ThrowIfCancellationRequested();

            var channelRoot = Path.Combine(config.LibraryFolder, group.Key, Sanitize(VideosFolderName));
            roots.Add(channelRoot);

            await writer.WriteChannelRootAsync(
                channelRoot,
                VideosFolderName,
                "Videos ajoutees manuellement.",
                thumbUrl: null,
                isSeries: true,
                ct).ConfigureAwait(false);

            writer.DeduplicateVideoFolders(channelRoot);
            var videoIndex = writer.BuildVideoIndex(channelRoot);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var source = new SourceItem { Mode = "Series", Name = VideosFolderName };
            var episode = 0;

            foreach (var uv in group.OrderBy(v => v.PublishedAt))
            {
                ct.ThrowIfCancellationRequested();
                keep.Add(uv.VideoId);
                episode++;

                // Backfill metadata for videos added before it was captured
                // (e.g. the description). One API call, then persisted.
                if (string.IsNullOrWhiteSpace(uv.Description) || string.IsNullOrWhiteSpace(uv.Title))
                {
                    try
                    {
                        var fresh = await api.GetVideoAsync(uv.VideoId, ct).ConfigureAwait(false);
                        if (fresh is not null)
                        {
                            if (!string.IsNullOrWhiteSpace(fresh.Title))
                            {
                                uv.Title = fresh.Title;
                            }

                            uv.Description = fresh.Description;
                            if (string.IsNullOrWhiteSpace(uv.Thumbnail))
                            {
                                uv.Thumbnail = fresh.ThumbnailUrl;
                            }

                            configDirty = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not backfill metadata for video {Id}", uv.VideoId);
                    }
                }

                var video = new YouTubeVideo
                {
                    VideoId = uv.VideoId,
                    Title = string.IsNullOrWhiteSpace(uv.Title) ? uv.VideoId : uv.Title,
                    Description = uv.Description,
                    PublishedAt = uv.PublishedAt,
                    ThumbnailUrl = uv.Thumbnail
                };

                var strmTarget = $"{baseAddress}/JellyTuber/Stream/{uv.VideoId}";
                var created = await writer.WriteVideoAsync(channelRoot, source, video, episode, strmTarget, videoIndex, ct)
                    .ConfigureAwait(false);

                if (created)
                {
                    written++;
                }
                else
                {
                    // Already on disk -> make sure its .nfo reflects current metadata.
                    await writer.RefreshVideoNfoAsync(channelRoot, source, video, episode, ct)
                        .ConfigureAwait(false);
                }
            }

            // Remove videos the user deleted from their list (folder kept, items pruned).
            writer.CleanupRemovedVideos(channelRoot, keep);
        }

        if (configDirty)
        {
            lock (Plugin.ConfigLock)
            {
                Plugin.Instance?.Save();
            }
        }

        return (roots, written);
    }

    /// <summary>
    /// Creates a "Movies" library named "Youtube@{User}" for each user that has
    /// content, pointing at LibraryFolder/{User}, when none already exists.
    /// Jellyfin (10.11+) validates the path on add, so the folder is created
    /// first.
    /// </summary>
    private async Task<bool> EnsureUserLibrariesAsync(PluginConfiguration config)
    {
        var created = false;

        // Distinct users (by on-disk folder), keeping a display name.
        var users = config.UserChannels.Select(c => c.UserName)
            .Concat(config.UserVideos.Select(v => v.UserName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .GroupBy(SanitizeUser)
            .Select(g => new { Folder = g.Key, Display = g.First() })
            .ToList();

        if (users.Count == 0)
        {
            return false;
        }

        // Existing library names (case-insensitive).
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var vf in _libraryManager.GetVirtualFolders())
            {
                if (!string.IsNullOrEmpty(vf.Name))
                {
                    existing.Add(vf.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list existing libraries; skipping auto-create");
            return false;
        }

        foreach (var u in users)
        {
            var libraryName = $"Youtube@{u.Display}";
            if (existing.Contains(libraryName))
            {
                continue;
            }

            var path = Path.Combine(config.LibraryFolder, u.Folder);

            try
            {
                Directory.CreateDirectory(path); // AddVirtualFolder requires it to exist

                var options = new LibraryOptions
                {
                    EnableRealtimeMonitor = true,

                    // These are .strm/NFO libraries: never let ffmpeg grab frames
                    // from the YouTube stream. Remote metadata/image providers are
                    // disabled below via empty TypeOptions fetcher lists.
                    EnableChapterImageExtraction = false,
                    ExtractChapterImagesDuringLibraryScan = false,

                    PathInfos = new[] { new MediaPathInfo(path) },

                    // Movie type: empty fetcher lists => every remote metadata
                    // provider (TheMovieDb, The Open Movie Database) and every
                    // image fetcher (TheMovieDb, The Open Movie Database,
                    // Embedded Image Extractor, Screen Grabber) is disabled.
                    // Local NFO + local images (poster.jpg / *.jpg) still apply.
                    TypeOptions = new[]
                    {
                        new TypeOptions
                        {
                            Type = "Movie",
                            MetadataFetchers = Array.Empty<string>(),
                            MetadataFetcherOrder = Array.Empty<string>(),
                            ImageFetchers = Array.Empty<string>(),
                            ImageFetcherOrder = Array.Empty<string>()
                        }
                    }
                };

                // CollectionTypeOptions.movies => a "Movies/Film" library.
                await _libraryManager
                    .AddVirtualFolder(libraryName, CollectionTypeOptions.movies, options, refreshLibrary: false)
                    .ConfigureAwait(false);

                existing.Add(libraryName);
                created = true;
                _logger.LogInformation("Created library '{Name}' -> {Path}", libraryName, path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create library '{Name}' for {Path}", libraryName, path);
            }
        }

        return created;
    }

    /// <summary>
    /// Queues a metadata + image refresh on each "Youtube@{User}" library so
    /// items attach their local poster.jpg / *.jpg. Mirrors the dashboard's
    /// "Refresh metadata" action; remote providers are off, so only local
    /// readers/images run.
    /// </summary>
    private void RefreshUserLibrariesMetadata()
    {
        var dirService = new DirectoryService(_fileSystem);

        foreach (var vf in _libraryManager.GetVirtualFolders())
        {
            if (string.IsNullOrEmpty(vf.Name)
                || !vf.Name.StartsWith("Youtube@", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Guid.TryParse(vf.ItemId, out var id) || id == Guid.Empty)
            {
                continue;
            }

            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            var options = new MetadataRefreshOptions(dirService)
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = false,
                ReplaceAllMetadata = false,
                IsAutomated = false
            };

            _providerManager.QueueRefresh(item.Id, options, RefreshPriority.Normal);
            _logger.LogInformation("Queued image/metadata refresh for library '{Name}'", vf.Name);
        }
    }

    /// <summary>
    /// Best-effort channel id: from the uploads playlist (UU... -> UC...) or
    /// from a /channel/UC... URL. Null when only a playlist/handle is known.
    /// </summary>
    private static string? DeriveChannelId(string playlistId, string url)
    {
        if (!string.IsNullOrEmpty(playlistId)
            && playlistId.StartsWith("UU", StringComparison.Ordinal)
            && playlistId.Length > 2)
        {
            return "UC" + playlistId.Substring(2);
        }

        var m = System.Text.RegularExpressions.Regex.Match(url ?? string.Empty, @"/channel/(UC[\w-]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string SanitizeUser(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "user";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }
}
