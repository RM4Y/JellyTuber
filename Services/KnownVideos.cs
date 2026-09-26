using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// The set of video ids that actually belong to the library - the only ones
/// the anonymous Stream/Segment endpoints will resolve and encode. Those
/// endpoints must stay reachable without a token (the .strm files and
/// Jellyfin's own ffmpeg don't carry one), so without this anyone who could
/// reach the server could make it run yt-dlp + an encode for ANY YouTube
/// video, filling the cache and burning CPU/GPU.
///
/// Built lazily from the .ytmeta markers on disk plus the user-added videos
/// in config, fed live by the sync task as it writes new videos, and rescanned
/// on a miss at most once per <see cref="RescanMinInterval"/> (so a burst of
/// requests for unknown ids costs at most one disk walk per minute).
/// </summary>
internal static class KnownVideos
{
    private static readonly Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    private static readonly TimeSpan RescanMinInterval = TimeSpan.FromMinutes(1);

    private static readonly object Lock = new();

    private static HashSet<string>? _ids;

    private static DateTime _lastScanUtc;

    /// <summary>True if <paramref name="videoId"/> has the shape of a YouTube video id (11 chars of [A-Za-z0-9_-]).</summary>
    public static bool IsValidId(string? videoId) => videoId is not null && VideoIdPattern.IsMatch(videoId);

    /// <summary>Records a video the sync just wrote, so it's playable right away without waiting for a rescan.</summary>
    public static void Add(string videoId)
    {
        lock (Lock)
        {
            // Not loaded yet: the first Contains() scans the disk and finds it there.
            _ids?.Add(videoId);
        }
    }

    public static bool Contains(string videoId)
    {
        if (!IsValidId(videoId))
        {
            return false;
        }

        lock (Lock)
        {
            if (_ids is not null
                && (_ids.Contains(videoId) || DateTime.UtcNow - _lastScanUtc < RescanMinInterval))
            {
                return _ids.Contains(videoId);
            }

            _ids = Scan();
            _lastScanUtc = DateTime.UtcNow;
            return _ids.Contains(videoId);
        }
    }

    private static HashSet<string> Scan()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return ids;
        }

        List<string> roots;
        lock (Plugin.ConfigLock)
        {
            roots = config.Sources
                .Select(s => s.DestinationFolder)
                .Append(config.LibraryFolder)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            ids.UnionWith(config.UserVideos.Select(v => v.VideoId));
        }

        foreach (var root in roots)
        {
            try
            {
                ids.UnionWith(LibraryWriter.ListAllVideoIds(root));
            }
            catch (Exception)
            {
                // unreadable root - whatever else was found still counts
            }
        }

        return ids;
    }
}
