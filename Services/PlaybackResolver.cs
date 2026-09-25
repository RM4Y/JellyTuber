using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    /// <summary>Video codec of <see cref="VideoUrl"/> (e.g. "avc1.640028"), null if not applicable.</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Audio codec of <see cref="AudioUrl"/> (e.g. "mp4a.40.2"), null if not applicable.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Height in pixels of <see cref="VideoUrl"/>, 0 if unknown. Used to pick a sane bitrate ceiling when re-encoding for <see cref="HlsPackagerSessionManager"/>.</summary>
    public int Height { get; init; }

    /// <summary>
    /// True when <see cref="HlsPackagerSessionManager"/> can package this
    /// source into HLS segments: audio is stream-copied into MPEG-TS, so it
    /// has to be AAC (Opus isn't reliably supported there); video is always
    /// re-encoded to H.264 anyway (see
    /// <see cref="FfmpegMuxer.StartContinuousSegmenter"/>), so any codec
    /// ffmpeg decodes works - VP9/AV1 is what YouTube serves above 1080p.
    /// Anything else falls back to the continuous single-stream mux.
    /// </summary>
    public bool IsHlsSegmentable =>
        VideoCodec is not null
        && (VideoCodec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
            || VideoCodec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase)
            || VideoCodec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase)
            || VideoCodec.StartsWith("av01", StringComparison.OrdinalIgnoreCase))
        && AudioCodec is not null && AudioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase);

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

    private static readonly ConcurrentDictionary<string, CacheEntry<string?>> DirectCache = new();

    /// <summary>Once the cache grows past this many entries, expired ones are swept out.</summary>
    private const int CacheSweepThreshold = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public PlaybackResolver(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
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
        DirectCache.TryRemove(videoId, out _);
    }

    /// <summary>
    /// Resolves straight to the HLS master manifest URL, the fast path the
    /// old YouTubeFast plugin always used: the client does its own adaptive
    /// bitrate against it (up to 1080p60), no muxing or proxying involved at
    /// all. The caller 302-redirects the client straight to the result.
    ///
    /// Deliberately narrower than YouTubeFast's own fallback chain: that one
    /// also tried a single combined premuxed URL when no HLS manifest
    /// existed, but the only such format YouTube reliably offers is itag 18
    /// (360p) - a real quality regression from what the DASH+proxy pipeline
    /// can reach (up to MaxHeight, including 4K). Returning null here instead
    /// lets the caller fall through to that pipeline, which is what most
    /// videos hit anyway since combined HLS is the exception, not the rule.
    ///
    /// Only safe when the requesting client will reach googlevideo from the
    /// same IP that resolved the URL here (see
    /// <see cref="LocalNetworkDetector"/>) - these signed URLs are IP-locked,
    /// so a client on a different network gets a 403 following the redirect.
    /// </summary>
    public async Task<string?> ResolveDirectAsync(string videoId, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (DirectCache.TryGetValue(videoId, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Value;
        }

        var url = await RunYtDlpDirectAsync(config, videoId, ct).ConfigureAwait(false);

        // Cache a negative result too, not just a successful one: most
        // videos have no combined HLS manifest at all (that's the common
        // case, not the exception - see BuildFormat's doc comment), and
        // without this every SAME-NETWORK play of such a video re-paid this
        // ~2s yt-dlp manifest probe forever, on every single play, never
        // just once. A `null` cache hit means "confirmed no manifest," and
        // is returned exactly like a fresh negative result would be.
        DirectCache[videoId] = new CacheEntry<string?>
        {
            Value = url,
            ExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, config.LinkCacheMinutes))
        };

        return url;
    }

    private async Task<string?> RunYtDlpDirectAsync(PluginConfiguration config, string videoId, CancellationToken ct)
    {
        var ytDlpPath = await ExternalTools.EnsureYtDlpAsync(_httpClientFactory, _logger, ct).ConfigureAwait(false);
        if (ytDlpPath is null)
        {
            _logger.LogError("yt-dlp is not available (download failed); cannot resolve {VideoId}", videoId);
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Select an HLS format, then print ITS master manifest URL (shared by
        // all variants) rather than a single variant. The client then does
        // adaptive bitrate up to the best H.264 variant on its own.
        //
        // [acodec!=none] is load-bearing, not optional: without it this
        // selector's "bv*[protocol^=m3u8]" fallback happily matches a
        // VIDEO-ONLY m3u8 format (confirmed live - YouTube exposes only
        // separate video-only/audio-only m3u8 tracks for regular VOD videos,
        // never a real combined one) and hands the client a redirect straight
        // to a silent, uncapped-resolution manifest (reproduced: a 4K VP9
        // video-only track with zero audio) instead of falling through to
        // the DASH pipeline below - which is what should happen essentially
        // always, per this method's own doc comment about combined HLS being
        // rare. This is what was making same-network playback look "blurry"/
        // broken across the board, not a quality tradeoff.
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(manifest_url)s");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("b[protocol^=m3u8][acodec!=none]");

        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--socket-timeout");
        psi.ArgumentList.Add("15");

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

        var denoPath = await ExternalTools.EnsureDenoAsync(_httpClientFactory, _logger, ct).ConfigureAwait(false);
        if (denoPath is not null)
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add($"deno:{denoPath}");
        }

        foreach (var arg in ExternalTools.StripJsRuntimesArg(config.YtDlpExtraArgs))
        {
            psi.ArgumentList.Add(arg);
        }

        using var cookieFile = CookieFile.Apply(psi, config.YouTubeCookies, _logger);
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
                _logger.LogWarning("yt-dlp (HLS manifest) failed for {VideoId}: {Error}", videoId, stderr.Trim());
                return null;
            }

            // First non-empty http(s) line is the playable URL / manifest.
            string? manifestUrl = null;
            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    manifestUrl = trimmed;
                    break;
                }
            }

            if (manifestUrl is null)
            {
                return null;
            }

            if (await HasAmbiguousAudioLanguageAsync(manifestUrl, videoId, ct).ConfigureAwait(false))
            {
                // Multiple audio languages (e.g. an auto-dub) with no track
                // marked DEFAULT=YES - which one plays is then entirely up to
                // the player's own tie-break (often just "whichever is listed
                // first"), and that's frequently the dub, not the original.
                // We can't rewrite this manifest ourselves since the caller
                // redirects the client straight to it without passing back
                // through us - falling back to the DASH pipeline instead,
                // whose format selector (see BuildFormat) reliably honours
                // yt-dlp's own original-vs-dub language_preference.
                _logger.LogInformation(
                    "Skipping direct HLS redirect for {VideoId}: manifest exposes multiple audio languages with no explicit default (would let the player pick, e.g. a dub instead of the original) - falling back to the DASH pipeline",
                    videoId);
                return null;
            }

            return manifestUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch yt-dlp at '{Path}'", ytDlpPath);
            return null;
        }
    }

    private static readonly Regex AudioLanguageRegex = new("LANGUAGE=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// True if <paramref name="manifestUrl"/>'s HLS master playlist lists more
    /// than one audio language (via <c>#EXT-X-MEDIA:TYPE=AUDIO</c>) and none
    /// of them is marked <c>DEFAULT=YES</c> - the ambiguous case a real-world
    /// video with an auto-dub track produces (confirmed against a live
    /// manifest: the dub and the original both come back as
    /// <c>DEFAULT=NO,AUTOSELECT=YES</c>). On any fetch/parse failure, assumes
    /// unambiguous so a network hiccup here can't block the fast path.
    /// </summary>
    private async Task<bool> HasAmbiguousAudioLanguageAsync(string manifestUrl, string videoId, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var text = await http.GetStringAsync(manifestUrl, timeoutCts.Token).ConfigureAwait(false);

            var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anyDefaultYes = false;

            foreach (var line in text.Split('\n'))
            {
                if (line.IndexOf("EXT-X-MEDIA", StringComparison.Ordinal) < 0
                    || line.IndexOf("TYPE=AUDIO", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var langMatch = AudioLanguageRegex.Match(line);
                if (langMatch.Success)
                {
                    languages.Add(langMatch.Groups[1].Value);
                }

                if (line.Contains("DEFAULT=YES", StringComparison.Ordinal))
                {
                    anyDefaultYes = true;
                }
            }

            return languages.Count > 1 && !anyDefaultYes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect HLS manifest audio tracks for {VideoId}; using it as-is", videoId);
            return false;
        }
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
        var ytDlpPath = await ExternalTools.EnsureYtDlpAsync(_httpClientFactory, _logger, ct).ConfigureAwait(false);
        if (ytDlpPath is null)
        {
            _logger.LogError("yt-dlp is not available (download failed); cannot resolve {VideoId}", videoId);
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
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

        var denoPath = await ExternalTools.EnsureDenoAsync(_httpClientFactory, _logger, ct).ConfigureAwait(false);
        if (denoPath is not null)
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add($"deno:{denoPath}");
        }

        // Any advanced extra args the user configured.
        foreach (var arg in ExternalTools.StripJsRuntimesArg(config.YtDlpExtraArgs))
        {
            psi.ArgumentList.Add(arg);
        }

        using var cookieFile = CookieFile.Apply(psi, config.YouTubeCookies, _logger);
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
            _logger.LogError(ex, "Failed to launch yt-dlp at '{Path}'", ytDlpPath);
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
                    VideoCodec = video.Vcodec,
                    AudioCodec = audio.Acodec,
                    Height = video.Height ?? 0,
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
    /// Builds a yt-dlp format selector honouring MaxHeight.
    ///
    /// A combined HLS format (video+audio already muxed by YouTube) is tried
    /// FIRST, in case a given video happens to expose one - that resolves to
    /// <see cref="ResolvedStream.DirectUrl"/> instead of separate
    /// <see cref="ResolvedStream.VideoUrl"/>/<see cref="ResolvedStream.AudioUrl"/>,
    /// letting <see cref="Api.PlaybackController"/> take its plain proxy
    /// relay path with no ffmpeg involved at all. In practice regular
    /// (non-live) YouTube videos essentially never expose this, so this is
    /// a cheap opportunistic check, not something to rely on.
    ///
    /// Separate DASH video+audio (needing ffmpeg muxing) is the format
    /// actually used for the overwhelming majority of videos, and strongly
    /// prefers H.264 video + AAC audio at every height: that's the only
    /// combination <see cref="HlsPackagerSessionManager"/> can safely
    /// stream-copy into MPEG-TS for segmented playback (VP9/AV1 video and
    /// Opus audio aren't reliably supported inside MPEG-TS). YouTube only
    /// publishes H.264 up to 1080p60 - above that, VP9/AV1 are the only
    /// codecs it exposes at all. When MaxHeight allows it and
    /// <see cref="GpuEncoder"/> is usable, a &gt;1080p SDR VP9/AV1 source
    /// (+ AAC) is tried FIRST, ahead of everything else: the segmenter
    /// re-encodes it to H.264 on the GPU (see
    /// <see cref="ResolvedStream.IsHlsSegmentable"/>). Without that
    /// preference, the H.264-first chain below always settled on 1080p even
    /// with MaxHeight=2160 (confirmed on a real 4K60 video: 299+140,
    /// 1080p60). HDR is excluded: h264_nvenc can't take its 10-bit frames,
    /// and YouTube always publishes an SDR version alongside it. Without a
    /// usable GPU encoder, &gt;1080p is never requested: libx264 can't
    /// encode it in real time (see <see cref="GpuEncoder"/>).
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

        var hls = $"b[protocol^=m3u8][vcodec^=avc1][height<=?{hh}]/b[protocol^=m3u8][height<=?{hh}]/";

        var aboveHd = h > 1080 && GpuEncoder.IsUsable
            ? $"bv*[height>1080][height<=?{hh}][dynamic_range=SDR][protocol=https]+ba[acodec^=mp4a]/"
            : string.Empty;

        return aboveHd + hls +
               $"bv*[vcodec^=avc1][height<=?{hh}]+ba[acodec^=mp4a]/" +
               $"bv*[vcodec^=avc1][height<=?{hh}]+ba/" +
               $"bv*[height<=?{hh}]+ba/" +
               $"b[height<=?{hh}]/best";
    }

    private sealed class CacheEntry
    {
        public ResolvedStream Stream { get; set; } = null!;

        public DateTime ExpiresUtc { get; set; }
    }

    private sealed class CacheEntry<T>
    {
        public T Value { get; set; } = default!;

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

        [JsonPropertyName("height")]
        public int? Height { get; set; }

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
