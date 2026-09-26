using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.JellyTuber.Configuration;
using Jellyfin.Plugin.JellyTuber.YouTube;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Turns API results into a flat on-disk layout:
///
///   {User}/{Channel}/
///       poster.jpg
///       chaine.nfo            (channel metadata)
///       .ytchannel           (cleanup marker, written by us)
///       {Video Title}/
///           {Video Title}.strm
///           {Video Title}.nfo
///           {Video Title}.jpg
///           .ytmeta           (dedup / age-cleanup marker)
///
/// There is no "Season YYYY" level: every video folder lives directly under
/// its channel folder.
/// </summary>
public class LibraryWriter
{
    /// <summary>Marker file dropped at every channel root we create, used to
    /// safely identify (and clean up) folders this plugin owns.</summary>
    public const string ChannelMarker = ".ytchannel";

    private const string VideoMarker = ".ytmeta";

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public LibraryWriter(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Deletes a previously-written video folder if it exists. Used to remove
    /// Shorts that were imported before the source started excluding them.
    /// Returns true if something was deleted. The folder is located by video
    /// id (via <paramref name="videoIndex"/>, so it's found even if the title
    /// changed since it was written), or else by title - but only ever
    /// deleted if its own marker says it's THIS video, never another video
    /// that happens to share the title.
    /// </summary>
    public bool RemoveVideoIfExists(
        string sourceRoot,
        YouTubeVideo video,
        IDictionary<string, string>? videoIndex = null)
    {
        var candidates = new List<string>();
        if (videoIndex is not null && videoIndex.TryGetValue(video.VideoId, out var existingDir))
        {
            candidates.Add(existingDir);
        }

        candidates.Add(Path.Combine(sourceRoot, Sanitize(video.Title)));
        candidates.Add(Path.Combine(sourceRoot, DisambiguatedName(video)));

        foreach (var videoDir in candidates)
        {
            try
            {
                if (Directory.Exists(videoDir)
                    && PathSafety.IsStrictlyInside(videoDir, sourceRoot)
                    && ReadMarkerId(videoDir) == video.VideoId)
                {
                    Directory.Delete(videoDir, recursive: true);
                    VideoCache.Delete(video.VideoId);
                    _logger.LogInformation("Removed Short folder: {Dir}", videoDir);
                    videoIndex?.Remove(video.VideoId);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove Short folder {Dir}", videoDir);
            }
        }

        return false;
    }

    /// <summary>
    /// Scans <paramref name="sourceRoot"/> for existing video folders and
    /// returns a videoId -> folder path map, read from the .ytmeta markers.
    /// Used to detect a video whose title changed since the last sync, so its
    /// existing folder can be renamed instead of a duplicate being written
    /// under the new title.
    /// </summary>
    public Dictionary<string, string> BuildVideoIndex(string sourceRoot)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(sourceRoot))
        {
            return index;
        }

        foreach (var marker in Directory.EnumerateFiles(sourceRoot, VideoMarker, SearchOption.AllDirectories))
        {
            try
            {
                var id = File.ReadAllText(marker).Split('|')[0];
                var dir = Path.GetDirectoryName(marker);
                if (!string.IsNullOrEmpty(id) && dir is not null && PathSafety.IsStrictlyInside(dir, sourceRoot))
                {
                    index[id] = dir;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index marker {Marker}", marker);
            }
        }

        return index;
    }

    /// <summary>
    /// Finds video folders under <paramref name="sourceRoot"/> that share the
    /// same video id -- duplicates left over from a title change that predates
    /// the rename-in-place fix, where the old and new title each got their own
    /// folder -- and deletes all but the most recently written one. Safe to run
    /// on every sync; a no-op once nothing is left to merge.
    /// </summary>
    public void DeduplicateVideoFolders(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        var byId = new Dictionary<string, List<(string Dir, DateTime WrittenUtc)>>(StringComparer.Ordinal);

        foreach (var marker in Directory.EnumerateFiles(sourceRoot, VideoMarker, SearchOption.AllDirectories))
        {
            try
            {
                var parts = File.ReadAllText(marker).Split('|');
                var id = parts.Length > 0 ? parts[0] : string.Empty;
                var dir = Path.GetDirectoryName(marker);
                if (string.IsNullOrEmpty(id) || dir is null || !PathSafety.IsStrictlyInside(dir, sourceRoot))
                {
                    continue;
                }

                if (!byId.TryGetValue(id, out var list))
                {
                    list = new List<(string, DateTime)>();
                    byId[id] = list;
                }

                list.Add((dir, File.GetLastWriteTimeUtc(marker)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read marker {Marker} during dedup scan", marker);
            }
        }

        foreach (var dirs in byId.Values)
        {
            if (dirs.Count < 2)
            {
                continue;
            }

            // Keep the most recently written copy (most likely the one that
            // reflects the current title); drop the rest.
            var keeper = dirs.OrderByDescending(d => d.WrittenUtc).First().Dir;
            foreach (var (dir, _) in dirs)
            {
                if (string.Equals(dir, keeper, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation(
                        "Removed duplicate video folder left over from a title change: {Dir}", dir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove duplicate video folder {Dir}", dir);
                }
            }
        }
    }

    /// <summary>
    /// Writes the channel folder: chaine.nfo + poster.jpg + the .ytchannel
    /// marker. Called for every source (series and movies modes alike).
    /// </summary>
    public async Task WriteChannelRootAsync(
        string sourceRoot,
        string title,
        string description,
        string? thumbUrl,
        bool isSeries,
        CancellationToken ct)
    {
        Directory.CreateDirectory(sourceRoot);

        var rootTag = isSeries ? "tvshow" : "movie";
        var nfo = new StringBuilder();
        nfo.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        nfo.AppendLine($"<{rootTag}>");
        nfo.AppendLine($"  <title>{Esc(title)}</title>");
        nfo.AppendLine($"  <plot>{Esc(description)}</plot>");
        nfo.AppendLine($"</{rootTag}>");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "chaine.nfo"), nfo.ToString(), ct).ConfigureAwait(false);

        // Ownership marker so cleanup only ever touches folders we created.
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, ChannelMarker),
            title,
            ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(thumbUrl))
        {
            await DownloadAsync(thumbUrl, Path.Combine(sourceRoot, "poster.jpg"), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a single video into {sourceRoot}/{Title}/. Returns false if it
    /// already existed. strmTarget is the URL written into the .strm file.
    /// When <paramref name="videoIndex"/> is supplied and this video's id is
    /// already on disk under a different (older) title, the existing folder is
    /// renamed in place instead of writing a duplicate under the new title.
    /// </summary>
    public async Task<bool> WriteVideoAsync(
        string sourceRoot,
        SourceItem source,
        YouTubeVideo video,
        int episodeNumber,
        string strmTarget,
        IDictionary<string, string>? videoIndex,
        CancellationToken ct)
    {
        var safeTitle = VideoFolderName(sourceRoot, video, videoIndex);
        var videoDir = Path.Combine(sourceRoot, safeTitle);
        var isSeries = !string.Equals(source.Mode, "Movies", StringComparison.OrdinalIgnoreCase);

        if (videoIndex is not null
            && videoIndex.TryGetValue(video.VideoId, out var existingDir)
            && !string.Equals(Normalize(existingDir), Normalize(videoDir), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(existingDir)
            && RenameVideoFolder(existingDir, videoDir, safeTitle))
        {
            videoIndex[video.VideoId] = videoDir;
        }

        var strmPath = Path.Combine(videoDir, safeTitle + ".strm");
        var markerPath = Path.Combine(videoDir, VideoMarker);
        var nfoPath = Path.Combine(videoDir, safeTitle + ".nfo");
        var nfo = isSeries
            ? BuildEpisodeNfo(video, video.PublishedAt.Year, episodeNumber)
            : BuildMovieNfo(video);

        if (File.Exists(strmPath) && File.Exists(markerPath))
        {
            // Already synced (possibly just renamed for a title change).
            // Keep what can drift in step: the .nfo (title/description, and
            // the episode number, which shifts as the keep-last-N window
            // slides) and the .strm (JellyfinAddress may have changed). Only
            // rewritten when different, so an idle sync doesn't touch mtimes
            // and trigger needless Jellyfin rescans.
            await WriteIfChangedAsync(nfoPath, nfo, ct).ConfigureAwait(false);
            await WriteIfChangedAsync(strmPath, strmTarget, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(video.ThumbnailUrl))
            {
                await DownloadAsync(video.ThumbnailUrl, Path.Combine(videoDir, safeTitle + ".jpg"), ct).ConfigureAwait(false);
            }

            return false;
        }

        Directory.CreateDirectory(videoDir);

        await File.WriteAllTextAsync(strmPath, strmTarget, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            markerPath,
            $"{video.VideoId}|{video.PublishedAt.ToString("o", CultureInfo.InvariantCulture)}",
            ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(nfoPath, nfo, ct).ConfigureAwait(false);

        // Thumbnail ({Title}.jpg)
        if (!string.IsNullOrEmpty(video.ThumbnailUrl))
        {
            await DownloadAsync(video.ThumbnailUrl, Path.Combine(videoDir, safeTitle + ".jpg"), ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// The folder (and file) name for <paramref name="video"/>: its title, or
    /// "{title} [{videoId}]" when a DIFFERENT video already owns the plain
    /// title folder - otherwise two videos with the same title ("Live",
    /// "Podcast #...", "Private video") shared one folder and the second was
    /// silently treated as already synced. A video keeps the folder it already
    /// has if that's still one of its two valid names, so existing folders
    /// are never renamed just because of this.
    /// </summary>
    private static string VideoFolderName(string sourceRoot, YouTubeVideo video, IDictionary<string, string>? videoIndex)
    {
        var plain = Sanitize(video.Title);
        var disambiguated = DisambiguatedName(video);

        if (videoIndex is not null && videoIndex.TryGetValue(video.VideoId, out var existingDir))
        {
            var existingName = Path.GetFileName(existingDir);
            if (existingName == plain || existingName == disambiguated)
            {
                return existingName;
            }
        }

        var plainDir = Path.Combine(sourceRoot, plain);
        var owner = Directory.Exists(plainDir) ? ReadMarkerId(plainDir) : null;
        return owner is null || owner == video.VideoId ? plain : disambiguated;
    }

    private static string DisambiguatedName(YouTubeVideo video) => Sanitize(video.Title) + " [" + video.VideoId + "]";

    /// <summary>The video id recorded in <paramref name="videoDir"/>'s .ytmeta marker, or null if there's none.</summary>
    private static string? ReadMarkerId(string videoDir)
    {
        try
        {
            var marker = Path.Combine(videoDir, VideoMarker);
            return File.Exists(marker) ? File.ReadAllText(marker).Split('|')[0] : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task WriteIfChangedAsync(string path, string content, CancellationToken ct)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) == content)
        {
            return;
        }

        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps only the <paramref name="maxVideos"/> most recently published
    /// video folders under <paramref name="sourceRoot"/>, deleting the rest.
    /// Walks the source root recursively looking for .ytmeta markers. This is
    /// what enforces each channel's "keep the last N videos" setting, both
    /// pruning videos that aged out of the window and shrinking the library
    /// down when a user/admin lowers the count.
    /// </summary>
    public void CleanupExcessVideos(string sourceRoot, int maxVideos)
    {
        if (maxVideos <= 0 || !Directory.Exists(sourceRoot))
        {
            return;
        }

        var entries = new List<(string Dir, string VideoId, DateTime Published)>();

        foreach (var marker in Directory.EnumerateFiles(sourceRoot, VideoMarker, SearchOption.AllDirectories))
        {
            try
            {
                var parts = File.ReadAllText(marker).Split('|');
                var dir = Path.GetDirectoryName(marker);
                if (dir is null || parts.Length < 2 || !PathSafety.IsStrictlyInside(dir, sourceRoot))
                {
                    continue;
                }

                if (!DateTime.TryParse(
                        parts[1],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var published))
                {
                    continue;
                }

                entries.Add((dir, parts[0], published));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup failed reading marker {Marker}", marker);
            }
        }

        foreach (var (dir, videoId, _) in entries.OrderByDescending(e => e.Published).Skip(maxVideos))
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    VideoCache.Delete(videoId);
                    _logger.LogInformation("Removed video folder beyond the {Max}-video limit: {Dir}", maxVideos, dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove excess video folder {Dir}", dir);
            }
        }
    }

    /// <summary>
    /// Every video id found anywhere under <paramref name="rootFolder"/> (all
    /// channels/users beneath it, recursively) - for one-off maintenance
    /// passes over the whole library, like the precache backfill task, that
    /// need every video id rather than one channel folder's worth.
    /// </summary>
    public static IEnumerable<string> ListAllVideoIds(string rootFolder)
    {
        if (!Directory.Exists(rootFolder))
        {
            yield break;
        }

        foreach (var marker in Directory.EnumerateFiles(rootFolder, VideoMarker, SearchOption.AllDirectories))
        {
            string? videoId = null;
            try
            {
                var parts = File.ReadAllText(marker).Split('|');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    videoId = parts[0];
                }
            }
            catch (IOException)
            {
            }

            if (videoId is not null)
            {
                yield return videoId;
            }
        }
    }

    /// <summary>
    /// Removes video folders under <paramref name="channelRoot"/> whose stored
    /// video id is not in <paramref name="keepVideoIds"/>. Used to delete
    /// individually-added videos once a user removes them from their list,
    /// and channel videos that dropped out of the kept set (deleted/private
    /// on YouTube, or now a Short). With <paramref name="onlyPublishedSinceUtc"/>,
    /// videos published before it are left alone: the sync only sees a
    /// window of recent uploads, and an older video outside it is simply
    /// unknown, not gone - retention by count is what ages those out.
    /// </summary>
    public void CleanupRemovedVideos(string channelRoot, ISet<string> keepVideoIds, DateTime? onlyPublishedSinceUtc = null)
    {
        if (!Directory.Exists(channelRoot))
        {
            return;
        }

        foreach (var marker in Directory.EnumerateFiles(channelRoot, VideoMarker, SearchOption.AllDirectories))
        {
            try
            {
                var parts = File.ReadAllText(marker).Split('|');
                var id = parts.Length > 0 ? parts[0] : string.Empty;
                if (onlyPublishedSinceUtc is { } since
                    && (parts.Length < 2
                        || !DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var published)
                        || published.ToUniversalTime() < since))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(id) && !keepVideoIds.Contains(id))
                {
                    var dir = Path.GetDirectoryName(marker);
                    if (dir is not null && PathSafety.IsStrictlyInside(dir, channelRoot) && Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                        VideoCache.Delete(id);
                        _logger.LogInformation("Removed video no longer in the list: {Dir}", dir);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Video cleanup failed for marker {Marker}", marker);
            }
        }
    }

    /// <summary>
    /// Removes channel folders that this plugin created (identified by the
    /// .ytchannel marker) but that are no longer in <paramref name="expectedRoots"/>.
    /// This is what deletes a channel's files once a user removes it from their
    /// list. Only ever touches marked folders, so unrelated library content is
    /// safe. <paramref name="scanRoots"/> are the parent folders to look under
    /// (e.g. each per-user folder + the global library folder).
    /// </summary>
    public void CleanupRemovedChannels(IEnumerable<string> expectedRoots, IEnumerable<string> scanRoots)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in expectedRoots)
        {
            expected.Add(Normalize(r));
        }

        var scannedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scanRoot in scanRoots)
        {
            if (string.IsNullOrWhiteSpace(scanRoot) || !Directory.Exists(scanRoot))
            {
                continue;
            }

            if (!scannedParents.Add(Normalize(scanRoot)))
            {
                continue; // already handled this parent
            }

            // Find every channel folder we own underneath this parent.
            foreach (var marker in Directory.EnumerateFiles(scanRoot, ChannelMarker, SearchOption.AllDirectories))
            {
                var channelDir = Path.GetDirectoryName(marker);
                // A marker sitting at the scan root itself (left by an older
                // version that let a ".." name resolve upwards) would delete
                // the whole library - never touch anything but a subfolder.
                if (channelDir is null || !PathSafety.IsStrictlyInside(channelDir, scanRoot))
                {
                    continue;
                }

                if (expected.Contains(Normalize(channelDir)))
                {
                    continue; // still configured -> keep
                }

                try
                {
                    foreach (var videoId in ReadVideoIds(channelDir))
                    {
                        VideoCache.Delete(videoId);
                    }

                    Directory.Delete(channelDir, recursive: true);
                    _logger.LogInformation("Removed channel no longer in the list: {Dir}", channelDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove orphaned channel folder {Dir}", channelDir);
                }
            }

            RemoveEmptyDirectories(scanRoot);
        }
    }

    /// <summary>Reads every video id recorded in .ytmeta markers under <paramref name="channelDir"/>, so its cache can be purged alongside its files.</summary>
    private IEnumerable<string> ReadVideoIds(string channelDir)
    {
        foreach (var marker in Directory.EnumerateFiles(channelDir, VideoMarker, SearchOption.AllDirectories))
        {
            string id;
            try
            {
                id = File.ReadAllText(marker).Split('|')[0];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read marker {Marker} while collecting video ids to purge from cache", marker);
                continue;
            }

            if (!string.IsNullOrEmpty(id))
            {
                yield return id;
            }
        }
    }

    private void RemoveEmptyDirectories(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Moves an existing video folder to reflect a title change, renaming the
    /// {oldTitle}.* files inside to {newTitle}.* along the way. The .ytmeta
    /// marker keeps its fixed name so it doesn't need renaming. Returns false
    /// (leaving the old folder untouched) if the destination already exists,
    /// to avoid clobbering unrelated data.
    /// </summary>
    private bool RenameVideoFolder(string existingDir, string newDir, string newSafeTitle)
    {
        if (Directory.Exists(newDir))
        {
            _logger.LogWarning(
                "Cannot rename {Old} -> {New}: destination already exists; leaving both as-is",
                existingDir, newDir);
            return false;
        }

        try
        {
            var oldSafeTitle = Path.GetFileName(existingDir);
            Directory.Move(existingDir, newDir);

            foreach (var ext in new[] { ".strm", ".nfo", ".jpg" })
            {
                var oldFile = Path.Combine(newDir, oldSafeTitle + ext);
                var newFile = Path.Combine(newDir, newSafeTitle + ext);
                if (File.Exists(oldFile) && !string.Equals(oldFile, newFile, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(oldFile, newFile, overwrite: true);
                }
            }

            _logger.LogInformation("Renamed video folder for title change: {Old} -> {New}", existingDir, newDir);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rename video folder {Old} -> {New}", existingDir, newDir);
            return false;
        }
    }

    private static string BuildEpisodeNfo(YouTubeVideo v, int season, int episode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<episodedetails>");
        sb.AppendLine($"  <title>{Esc(v.Title)}</title>");
        sb.AppendLine($"  <season>{season.ToString(CultureInfo.InvariantCulture)}</season>");
        sb.AppendLine($"  <episode>{episode.ToString(CultureInfo.InvariantCulture)}</episode>");
        sb.AppendLine($"  <plot>{Esc(v.Description)}</plot>");
        sb.AppendLine($"  <aired>{v.PublishedAt:yyyy-MM-dd}</aired>");
        sb.AppendLine($"  <premiered>{v.PublishedAt:yyyy-MM-dd}</premiered>");
        sb.AppendLine("</episodedetails>");
        return sb.ToString();
    }

    private static string BuildMovieNfo(YouTubeVideo v)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<movie>");
        sb.AppendLine($"  <title>{Esc(v.Title)}</title>");
        sb.AppendLine($"  <plot>{Esc(v.Description)}</plot>");
        sb.AppendLine($"  <year>{v.PublishedAt.Year.ToString(CultureInfo.InvariantCulture)}</year>");
        sb.AppendLine($"  <premiered>{v.PublishedAt:yyyy-MM-dd}</premiered>");
        sb.AppendLine("</movie>");
        return sb.ToString();
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        try
        {
            if (File.Exists(destination))
            {
                return;
            }

            var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(destination, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail download failed: {Url}", url);
        }
    }

    /// <summary>
    /// XML-escapes <paramref name="s"/> and drops characters XML 1.0 can't
    /// hold at all (control characters YouTube descriptions sometimes
    /// contain) - left in, they make the whole .nfo unparseable.
    /// </summary>
    internal static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[++i]);
                continue;
            }

            if (!XmlConvert.IsXmlChar(c))
            {
                continue;
            }

            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private static string Sanitize(string name)
    {
        var safe = PathSafety.Segment(name, "video");
        return safe.Length > 120 ? PathSafety.Segment(safe.Substring(0, 120), "video") : safe;
    }
}
