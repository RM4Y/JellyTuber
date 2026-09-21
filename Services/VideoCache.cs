using System;
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

    private static string RootDir => Path.Combine(Plugin.Instance!.DataFolderPath, "videocache");

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
    public static void EnsureMeta(string videoId, double durationSeconds, int totalSegments)
    {
        if (TryGetMeta(videoId) is not null)
        {
            return;
        }

        SaveMeta(videoId, new CacheMeta
        {
            DurationSeconds = durationSeconds,
            TotalSegments = totalSegments,
            Completed = false,
            LastAccessUtc = DateTime.UtcNow
        });
    }

    public static void MarkCompleted(string videoId, double durationSeconds, int totalSegments)
    {
        SaveMeta(videoId, new CacheMeta
        {
            DurationSeconds = durationSeconds,
            TotalSegments = totalSegments,
            Completed = true,
            LastAccessUtc = DateTime.UtcNow
        });
    }

    public static void TouchAccess(string videoId)
    {
        var meta = TryGetMeta(videoId);
        if (meta is null)
        {
            return;
        }

        meta.LastAccessUtc = DateTime.UtcNow;
        SaveMeta(videoId, meta);
    }

    /// <summary>Deletes a video's entire cache folder. Used both by disk-cap eviction and by the library retention hooks in <see cref="LibraryWriter"/>.</summary>
    public static void Delete(string videoId)
    {
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

    private static void SaveMeta(string videoId, CacheMeta meta)
    {
        try
        {
            Directory.CreateDirectory(GetVideoDir(videoId));
            File.WriteAllText(Path.Combine(GetVideoDir(videoId), MetaFileName), JsonSerializer.Serialize(meta));
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static string Sanitize(string videoId)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            videoId = videoId.Replace(c, '_');
        }

        return videoId;
    }

    internal sealed class CacheMeta
    {
        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }

        [JsonPropertyName("totalSegments")]
        public int TotalSegments { get; set; }

        [JsonPropertyName("completed")]
        public bool Completed { get; set; }

        [JsonPropertyName("lastAccessUtc")]
        public DateTime LastAccessUtc { get; set; }
    }
}
