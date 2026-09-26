using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Persistent on-disk cache of packaged HLS segments, keyed by video id
/// only - not by network tier. <see cref="HlsPackagerSessionManager"/>
/// always encodes at the higher (same-network) bitrate ceiling now, and
/// both LAN and remote viewers are served from this one cache: a single
/// encode per video costs half the CPU/disk of keeping two tiers, at the
/// cost of a remote viewer occasionally getting higher-bitrate segments
/// than the old capped "remote" ladder would have produced (accepted
/// trade-off).
///
/// Segments accumulate under a stable directory
/// (DataFolderPath/videocache/{videoId}/{index}.ts) instead of the
/// throwaway per-session temp directory this used to be
/// (Path.GetTempPath()/jellytuber-hls/{videoId}/{guid}), so a video only
/// ever needs to be resolved/encoded once - every later play, by the same
/// or a different viewer, is served straight from disk.
/// </summary>
internal static class VideoCache
{
    private const string MetaFileName = "meta.json";

    /// <summary>
    /// Bumped whenever segments already on disk stop being compatible with
    /// what a new session would produce - mixing the two in one video's
    /// folder is exactly what breaks playback at the boundary. v2: segments
    /// are cut on an exact 2s grid with absolute timestamps (see
    /// <see cref="FfmpegMuxer.StartContinuousSegmenter"/>); v1 segments
    /// drifted off that grid.
    /// </summary>
    private const string RootDirName = "videocache-v2";

    /// <summary>Earlier, incompatible cache roots - see <see cref="PurgeLegacyCaches"/>.</summary>
    private static readonly string[] LegacyRootDirNames = { "videocache" };

    /// <summary>
    /// How often a cache hit may rewrite meta.json's LastAccessUtc. It only
    /// feeds least-recently-used eviction, so minutes of precision is plenty
    /// - and it used to be rewritten on every single segment request.
    /// </summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Serializes meta.json read-modify-write sequences. Without it a
    /// TouchAccess that read Completed=false just before MarkCompleted wrote
    /// true would write false straight back.
    /// </summary>
    private static readonly object MetaLock = new();

    private static readonly ConcurrentDictionary<string, DateTime> LastTouchUtc = new(StringComparer.Ordinal);

    private static string RootDir => Path.Combine(Plugin.Instance!.DataFolderPath, RootDirName);

    public static string GetVideoDir(string videoId) => Path.Combine(RootDir, Sanitize(videoId));

    public static string GetSegmentPath(string videoId, int index) =>
        Path.Combine(GetVideoDir(videoId), index + ".ts");

    /// <summary>Returns the segment's path if it's already cached on disk (and bumps its access time). Null on a cache miss.</summary>
    public static string? TryGetSegmentPath(string videoId, int index)
    {
        var path = GetSegmentPath(videoId, index);
        if (!File.Exists(path))
        {
            return null;
        }

        TouchAccess(videoId);
        return path;
    }

    public static CacheMeta? TryGetMeta(string videoId)
    {
        var path = Path.Combine(GetVideoDir(videoId), MetaFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsCompleted(string videoId) => TryGetMeta(videoId)?.Completed == true;

    /// <summary>Writes an initial meta.json only if one doesn't already exist - never clobbers a completed cache's own record.</summary>
    public static void EnsureMeta(string videoId, double durationSeconds, int totalSegments, int encodedHeight)
    {
        lock (MetaLock)
        {
            if (TryGetMeta(videoId) is not null)
            {
                return;
            }

            SaveMeta(videoId, new CacheMeta
            {
                DurationSeconds = durationSeconds,
                TotalSegments = totalSegments,
                EncodedHeight = encodedHeight,
                Completed = false,
                LastAccessUtc = DateTime.UtcNow
            });
        }
    }

    public static void MarkCompleted(string videoId, double durationSeconds, int totalSegments)
    {
        lock (MetaLock)
        {
            SaveMeta(videoId, new CacheMeta
            {
                DurationSeconds = durationSeconds,
                TotalSegments = totalSegments,
                EncodedHeight = TryGetMeta(videoId)?.EncodedHeight,
                Completed = true,
                LastAccessUtc = DateTime.UtcNow
            });
        }
    }

    public static void TouchAccess(string videoId)
    {
        var now = DateTime.UtcNow;
        if (LastTouchUtc.TryGetValue(videoId, out var last) && now - last < TouchInterval)
        {
            return;
        }

        LastTouchUtc[videoId] = now;
        lock (MetaLock)
        {
            var meta = TryGetMeta(videoId);
            if (meta is null)
            {
                return;
            }

            meta.LastAccessUtc = now;
            SaveMeta(videoId, meta);
        }
    }

    /// <summary>Deletes a video's entire cache folder. Used both by disk-cap eviction and by the library retention hooks in <see cref="LibraryWriter"/>.</summary>
    public static void Delete(string videoId)
    {
        LastTouchUtc.TryRemove(videoId, out _);
        if (Plugin.Instance is null)
        {
            return; // not running inside Jellyfin (unit tests) - no cache to purge
        }

        var dir = GetVideoDir(videoId);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }

    /// <summary>First index in [<paramref name="from"/>, <paramref name="totalSegments"/>) with no segment on disk, or null if they're all there.</summary>
    public static int? FindFirstMissingSegment(string videoId, int from, int totalSegments)
    {
        for (var i = Math.Max(0, from); i < totalSegments; i++)
        {
            if (!File.Exists(GetSegmentPath(videoId, i)))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>First index in [<paramref name="from"/>, <paramref name="totalSegments"/>) that IS already on disk, or <paramref name="totalSegments"/> if none is.</summary>
    public static int FindFirstCachedSegment(string videoId, int from, int totalSegments)
    {
        for (var i = Math.Max(0, from); i < totalSegments; i++)
        {
            if (File.Exists(GetSegmentPath(videoId, i)))
            {
                return i;
            }
        }

        return totalSegments;
    }

    /// <summary>
    /// Deletes cache roots written by earlier plugin versions (see
    /// <see cref="RootDirName"/>). Best effort, meant to run once in the
    /// background at startup.
    /// </summary>
    public static void PurgeLegacyCaches(string dataFolderPath)
    {
        foreach (var name in LegacyRootDirNames)
        {
            try
            {
                var dir = Path.Combine(dataFolderPath, name);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // best effort
            }
            catch (UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// Sums the on-disk size of every cached video and evicts whole
    /// per-video folders, least-recently-accessed first, until back under
    /// <paramref name="maxBytes"/>. Never evicts a video id present in
    /// <paramref name="protectedVideoIds"/> (one currently being encoded).
    /// </summary>
    public static void EnforceSizeCap(long maxBytes, ISet<string> protectedVideoIds, ILogger logger)
    {
        if (maxBytes <= 0 || !Directory.Exists(RootDir))
        {
            return;
        }

        var entries = new List<(string Dir, string VideoId, long Size, DateTime LastAccess)>();
        long total = 0;

        foreach (var dir in Directory.EnumerateDirectories(RootDir))
        {
            var videoId = Path.GetFileName(dir);
            long size;
            try
            {
                size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var lastAccess = TryGetMeta(videoId)?.LastAccessUtc ?? Directory.GetLastWriteTimeUtc(dir);
            entries.Add((dir, videoId, size, lastAccess));
            total += size;
        }

        if (total <= maxBytes)
        {
            return;
        }

        foreach (var entry in entries.OrderBy(e => e.LastAccess))
        {
            if (total <= maxBytes)
            {
                break;
            }

            if (protectedVideoIds.Contains(entry.VideoId))
            {
                continue;
            }

            try
            {
                Directory.Delete(entry.Dir, recursive: true);
                total -= entry.Size;
                logger.LogInformation(
                    "Video cache: evicted {VideoId} ({SizeMB}MB) to stay under the {MaxMB}MB cap",
                    entry.VideoId, entry.Size / 1_000_000, maxBytes / 1_000_000);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Video cache: failed to evict {VideoId}", entry.VideoId);
            }
        }
    }

    /// <summary>
    /// Writes to a temp file then renames it over meta.json, so a concurrent
    /// <see cref="TryGetMeta"/> sees either the old or the new content, never
    /// a truncated file - which it would read as "no meta", letting
    /// <see cref="EnsureMeta"/> overwrite a completed cache's record.
    /// </summary>
    private static void SaveMeta(string videoId, CacheMeta meta)
    {
        var dir = GetVideoDir(videoId);
        var tmp = Path.Combine(dir, MetaFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(meta));
            File.Move(tmp, Path.Combine(dir, MetaFileName), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
            try
            {
                File.Delete(tmp);
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }

    /// <summary>Callers only ever pass validated 11-char ids (see <see cref="KnownVideos.IsValidId"/>); this just guarantees a single, non-"..", path segment regardless.</summary>
    private static string Sanitize(string videoId) => PathSafety.Segment(videoId, "_");

    internal sealed class CacheMeta
    {
        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }

        [JsonPropertyName("totalSegments")]
        public int TotalSegments { get; set; }

        /// <summary>
        /// Output height the cached segments were encoded at. Null on a
        /// meta.json written before 4K support - those were always encoded
        /// at the source height, which was never above 1080p back then.
        /// </summary>
        [JsonPropertyName("encodedHeight")]
        public int? EncodedHeight { get; set; }

        [JsonPropertyName("completed")]
        public bool Completed { get; set; }

        [JsonPropertyName("lastAccessUtc")]
        public DateTime LastAccessUtc { get; set; }
    }
}
