using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTuber.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// What a video resolves to. YouTube only publishes premuxed (single-URL,
/// video+audio combined) formats up to 360p; everything from 720p up is DASH
/// - separate video-only and audio-only URLs that need muxing. So resolution
/// yields either:
///   - VideoUrl + AudioUrl: the common case for anything above 360p. The
///     caller muxes them on the fly with ffmpeg (stream copy, no re-encode).
///   - DirectUrl: a single premuxed/HLS URL, kept as a fallback for the rare
///     video that only exposes that.
/// </summary>
public sealed class ResolvedStream
{
    public string? DirectUrl { get; init; }

    public string? VideoUrl { get; init; }

    public string? AudioUrl { get; init; }

    /// <summary>Video duration in seconds, 0 if unknown. Used to approximate seek offsets.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Estimated total byte size of the muxed output, 0 if unknown.</summary>
    public long EstimatedBytes { get; init; }
}

/// <summary>
/// Resolves a YouTube video id to a playable source using yt-dlp, caching the
/// result for a short window (YouTube URLs are time-limited anyway).
/// </summary>
public class PlaybackResolver
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    /// <summary>Once the cache grows past this many entries, expired ones are swept out.</summary>
    private const int CacheSweepThreshold = 500;

    private readonly ILogger _logger;

    public PlaybackResolver(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<ResolvedStream?> ResolveAsync(string videoId, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (Cache.TryGetValue(videoId, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Stream;
        }

        var resolved = await RunYtDlpAsync(config, videoId, ct).ConfigureAwait(false);

        if (resolved is not null)
        {
            Cache[videoId] = new CacheEntry
            {
                Stream = resolved,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, config.LinkCacheMinutes))
            };

            SweepExpiredIfLarge();
        }

        return resolved;
    }

    /// <summary>
    /// Drops a cached resolution so the next <see cref="ResolveAsync"/> call
    /// re-resolves from scratch. YouTube's signed URLs can stop working well
    /// before the nominal validity window we cache them for (some formats
    /// start returning 403 within minutes), so a caller that hits that should
    /// invalidate and retry rather than keep reusing a URL that's already
    /// dead until the cache entry naturally expires.
    /// </summary>
    public static void Invalidate(string videoId)
    {
        Cache.TryRemove(videoId, out _);
    }

    private static void SweepExpiredIfLarge()
    {
        if (Cache.Count <= CacheSweepThreshold)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var kvp in Cache)
        {
            if (kvp.Value.ExpiresUtc <= now)
            {
                Cache.TryRemove(kvp.Key, out _);
            }
        }
    }

    private async Task<ResolvedStream?> RunYtDlpAsync(PluginConfiguration config, string videoId, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // -j dumps the resolved format info as JSON instead of downloading -
        // gives us the URL(s) for the selected format PLUS duration/filesize,
        // which we need to approximate seek offsets when muxing on the fly.
        psi.ArgumentList.Add("-j");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(BuildFormat(config));

        // NOTE: we deliberately do NOT force the iOS player client. YouTube now
        // gates its iOS HTTPS formats behind a GVS PO token, so those formats get
        // skipped and resolution fails. The default client already exposes
        // DASH formats up to 4K/8K that resolve cleanly.

        // --- Speed flags ---
        // Never expand playlists, keep output terse, and fail fast on stalls.
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--socket-timeout");
        psi.ArgumentList.Add("15");

        // Persistent cache dir: yt-dlp reuses the solved player JS / nsig between
        // calls instead of re-extracting it each cold start (big win, esp. in
        // Docker where the service user's home may not be writable).
        try
        {
            var cacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellyfin-ytdlp-cache");
            System.IO.Directory.CreateDirectory(cacheDir);
            psi.ArgumentList.Add("--cache-dir");
            psi.ArgumentList.Add(cacheDir);
        }
        catch
        {
            // ignore - fall back to yt-dlp's default cache location
        }

        // Any advanced extra args the user configured.
        if (!string.IsNullOrWhiteSpace(config.YtDlpExtraArgs))
        {
            foreach (var arg in config.YtDlpExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        psi.ArgumentList.Add($"https://www.youtube.com/watch?v={videoId}");

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("yt-dlp failed for {VideoId}: {Error}", videoId, stderr.Trim());
                return null;
            }

            return ParseInfo(stdout, videoId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch yt-dlp at '{Path}'", config.YtDlpPath);
            return null;
        }
    }

    private ResolvedStream? ParseInfo(string stdout, string videoId)
    {
        // -j prints exactly one JSON object per line; with --no-playlist there's
        // exactly one video, so take the first non-empty line.
        var line = stdout.Split('\n').FirstOrDefault(l => l.Trim().StartsWith('{'));
        if (line is null)
        {
            _logger.LogWarning("yt-dlp returned no format info for {VideoId}", videoId);
            return null;
        }

        YtDlpInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<YtDlpInfo>(line);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse yt-dlp output for {VideoId}", videoId);
            return null;
        }

        if (info is null)
        {
            return null;
        }

        var duration = info.Duration ?? 0;

        if (info.RequestedFormats is { Count: >= 2 } formats)
        {
            var video = formats.FirstOrDefault(f => !string.IsNullOrEmpty(f.Vcodec) && f.Vcodec != "none");
            var audio = formats.FirstOrDefault(f => !string.IsNullOrEmpty(f.Acodec) && f.Acodec != "none" && f != video);

            if (video?.Url is not null && audio?.Url is not null)
            {
                var bytes = EstimateBytes(video, duration) + EstimateBytes(audio, duration);
                return new ResolvedStream
                {
                    VideoUrl = video.Url,
                    AudioUrl = audio.Url,
                    DurationSeconds = duration,
                    EstimatedBytes = bytes
                };
            }
        }

        if (!string.IsNullOrEmpty(info.Url))
        {
            return new ResolvedStream
            {
                DirectUrl = info.Url,
                DurationSeconds = duration,
                EstimatedBytes = EstimateBytes(info, duration)
            };
        }

        _logger.LogWarning("yt-dlp resolved {VideoId} but returned no usable URL", videoId);
        return null;
    }

    private static long EstimateBytes(IHasFormatSize f, double durationSeconds)
    {
        if (f.Filesize is > 0)
        {
            return (long)f.Filesize.Value;
        }

        if (f.FilesizeApprox is > 0)
        {
            return (long)f.FilesizeApprox.Value;
        }

        if (f.Tbr is > 0 && durationSeconds > 0)
        {
            // tbr is in kbit/s.
            return (long)(f.Tbr.Value * 1000 / 8 * durationSeconds);
        }

        return 0;
    }

    /// <summary>
    /// Builds a yt-dlp format selector honouring MaxHeight. YouTube only
    /// publishes H.264 up to 1080p60; above that, VP9/AV1 are the only
    /// codecs it exposes at all (1440p/2160p), so a hard preference for H.264
    /// would silently cap 1440p/2160p requests back down to 1080p. Below
    /// 1080p we still prefer H.264+AAC for maximum client compatibility; at
    /// or above 1080p we take the best available codec so higher heights are
    /// actually reachable.
    ///
    /// Deliberately NOT forcing AAC audio at every height (which would let
    /// Jellyfin Direct Play instead of wrapping our stream in its own
    /// transcode): Jellyfin's own ffmpeg verifies the real timestamp it
    /// lands on after an input seek and corrects for it, but a raw client
    /// player doing Direct Play has no such correction - it just trusts our
    /// approximate byte-offset-to-time math outright. That traded a slower
    /// but correct seek for a faster one that could land in the wrong place
    /// entirely (or find nothing there). Slower-but-correct wins.
    ///
    /// A premuxed single format is kept as a last resort. Honours the
    /// user's manual override.
    /// </summary>
    private static string BuildFormat(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.YtDlpFormatOverride))
        {
            return config.YtDlpFormatOverride.Trim();
        }

        var h = config.MaxHeight > 0 ? config.MaxHeight : 1080;
        var hh = h.ToString(CultureInfo.InvariantCulture);

        if (h < 1080)
        {
            return $"bv*[vcodec^=avc1][height<=?{hh}]+ba[acodec^=mp4a]/" +
                   $"bv*[height<=?{hh}]+ba/" +
                   $"b[height<=?{hh}]/best";
        }

        return $"bv*[height<=?{hh}]+ba/b[height<=?{hh}]/best";
    }

    private sealed class CacheEntry
    {
        public ResolvedStream Stream { get; set; } = null!;

        public DateTime ExpiresUtc { get; set; }
    }

    private interface IHasFormatSize
    {
        double? Filesize { get; }

        double? FilesizeApprox { get; }

        double? Tbr { get; }
    }

    private sealed class YtDlpFormatInfo : IHasFormatSize
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("vcodec")]
        public string? Vcodec { get; set; }

        [JsonPropertyName("acodec")]
        public string? Acodec { get; set; }

        [JsonPropertyName("filesize")]
        public double? Filesize { get; set; }

        [JsonPropertyName("filesize_approx")]
        public double? FilesizeApprox { get; set; }

        [JsonPropertyName("tbr")]
        public double? Tbr { get; set; }
    }

    private sealed class YtDlpInfo : IHasFormatSize
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        [JsonPropertyName("requested_formats")]
        public System.Collections.Generic.List<YtDlpFormatInfo>? RequestedFormats { get; set; }

        [JsonPropertyName("filesize")]
        public double? Filesize { get; set; }

        [JsonPropertyName("filesize_approx")]
        public double? FilesizeApprox { get; set; }

        [JsonPropertyName("tbr")]
        public double? Tbr { get; set; }
    }
}
