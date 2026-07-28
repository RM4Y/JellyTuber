using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Launches ffmpeg to mux a separate video-only and audio-only URL into a
/// single stream-copied (no re-encode) Matroska stream on stdout. Matroska
/// muxes cleanly to a pipe without needing a seekable output, unlike MP4's
/// moov atom.
///
/// Because the output pipe isn't seekable, ffmpeg can't back-patch the
/// container's Duration field once it knows how long the stream actually
/// ran (that's only possible on a seekable file). Left alone, that means
/// players have no idea how long the video is and can't show a working
/// seek bar/position. We already know the real duration from yt-dlp, so we
/// write it explicitly as a per-stream DURATION tag up front - the standard
/// way to signal duration on a streamed Matroska file.
/// </summary>
internal static class FfmpegMuxer
{
    public static Process Start(string ffmpegPath, string videoUrl, string audioUrl, double? seekSeconds, double? remainingDurationSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");

        AddInput(psi, videoUrl, seekSeconds);
        AddInput(psi, audioUrl, seekSeconds);

        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("1:a:0");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");

        if (remainingDurationSeconds is > 0)
        {
            var formatted = FormatDuration(remainingDurationSeconds.Value);
            psi.ArgumentList.Add("-metadata:s:v:0");
            psi.ArgumentList.Add($"DURATION={formatted}");
            psi.ArgumentList.Add("-metadata:s:a:0");
            psi.ArgumentList.Add($"DURATION={formatted}");
        }

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("matroska");
        psi.ArgumentList.Add("pipe:1");

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    /// <summary>Standard Linux render node for VAAPI, used when <paramref name="videoEncoder"/> is "h264_vaapi".</summary>
    private const string VaapiDevicePath = "/dev/dri/renderD128";

    /// <summary>
    /// Starts one continuous ffmpeg process that seeks to
    /// <paramref name="startSeconds"/> ONCE and then muxes the rest of the
    /// video/audio URLs straight through into a sequence of MPEG-TS segment
    /// files (<paramref name="outputDir"/>/0.ts, 1.ts, ...), for
    /// <see cref="HlsPackagerSessionManager"/>-driven playback.
    ///
    /// A single seek-then-run-to-completion process is deliberate: an
    /// earlier version re-seeked both URLs independently for every segment,
    /// but video and audio don't land on the same target time when
    /// seeked - video snaps back to the preceding keyframe, audio snaps
    /// forward to the next DASH fragment boundary - so two independent
    /// seeks to the same instant end up a couple of seconds apart. Paying
    /// that cost once per real jump is fine; paying it every few seconds
    /// during ordinary sequential playback drifted audio out of sync
    /// continuously. <c>-reset_timestamps 0</c> (not <c>-copyts</c>) is what
    /// keeps segments within one session contiguous: testing against the
    /// real server showed <c>-copyts</c> - which preserves the source's
    /// original, large absolute timestamp instead of rebasing near zero at
    /// the seek point - makes ffmpeg's segment muxer stop cutting at all
    /// past the first file (it silently keeps appending to segment N
    /// forever). <c>-reset_timestamps 0</c> alone still keeps every segment
    /// produced by this same process on one continuous, increasing
    /// timeline, without that large starting offset breaking the cut logic.
    ///
    /// Video is re-encoded with <paramref name="videoEncoder"/> (audio stays
    /// a stream copy) and <c>-force_key_frames</c> forces a real keyframe at
    /// every segment boundary. That's deliberate too: stream-copying video
    /// through <c>-f segment</c> relies on the source already having a
    /// keyframe at (or just after) each boundary, and testing against real
    /// YouTube DASH sources showed that assumption fails often enough to
    /// produce segments that don't start with a keyframe at all - undecodable
    /// junk that would glitch or crash playback the same way the byte-range
    /// approach used to. Forcing the keyframe ourselves makes every segment
    /// independently decodable by construction, at the cost of an actual
    /// encode pass instead of a copy.
    /// </summary>
    public static Process StartContinuousSegmenter(string ffmpegPath, string videoUrl, string audioUrl, double startSeconds, int startSegmentNumber, int segmentSeconds, string outputDir, string videoEncoder, int sourceHeight)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");

        var isVaapi = string.Equals(videoEncoder, "h264_vaapi", StringComparison.Ordinal);
        if (isVaapi)
        {
            psi.ArgumentList.Add("-vaapi_device");
            psi.ArgumentList.Add(VaapiDevicePath);
        }

        AddSegmenterInput(psi, videoUrl, startSeconds);
        AddSegmenterInput(psi, audioUrl, startSeconds);

        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("1:a:0");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("copy");

        if (isVaapi)
        {
            // VAAPI encoders take frames on the GPU surface, not plain system
            // memory - upload the software-decoded frames first.
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add("format=nv12,hwupload");
        }

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(videoEncoder);

        var bitrateBps = PickVideoBitrateBps(sourceHeight);

        if (string.Equals(videoEncoder, "libx264", StringComparison.Ordinal))
        {
            // Speed over compression efficiency: segments are produced
            // one at a time to keep up with real-time playback, not stored
            // long-term, so a fast preset matters more than bitrate savings.
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("veryfast");

            // CRF for quality, maxrate/bufsize as a real VBV ceiling on top -
            // the standard "capped CRF" recipe. This matters specifically for
            // remote (proxied) playback: the output has to fit through
            // whatever upload bandwidth the server's own connection has,
            // which is usually the tightest link in the whole path - letting
            // x264 pick an unbounded bitrate for high-motion 1080p60 source
            // can outrun a typical home uplink even though local playback
            // never notices.
            psi.ArgumentList.Add("-crf");
            psi.ArgumentList.Add("21");
            psi.ArgumentList.Add("-maxrate");
            psi.ArgumentList.Add(bitrateBps.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-bufsize");
            psi.ArgumentList.Add((bitrateBps * 2).ToString(CultureInfo.InvariantCulture));
        }
        else if (string.Equals(videoEncoder, "libopenh264", StringComparison.Ordinal))
        {
            // libopenh264's rate control needs to be explicitly switched into
            // bitrate mode - its default ("quality" mode) ignores -b:v
            // entirely (confirmed via real encode: -b:v alone had no effect
            // on output size). Even in bitrate mode its VBV-style ceiling is
            // considerably softer than x264's - treat this as a best-effort
            // cap for the software fallback encoder, not a hard guarantee.
            psi.ArgumentList.Add("-rc_mode");
            psi.ArgumentList.Add("bitrate");
            psi.ArgumentList.Add("-allow_skip_frames");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-b:v");
            psi.ArgumentList.Add(bitrateBps.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-maxrate");
            psi.ArgumentList.Add(bitrateBps.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-bufsize");
            psi.ArgumentList.Add((bitrateBps * 2).ToString(CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-force_key_frames");
        psi.ArgumentList.Add($"expr:gte(t,n_forced*{segmentSeconds.ToString(CultureInfo.InvariantCulture)})");

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("segment");
        psi.ArgumentList.Add("-segment_time");
        psi.ArgumentList.Add(segmentSeconds.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-segment_format");
        psi.ArgumentList.Add("mpegts");
        psi.ArgumentList.Add("-reset_timestamps");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-segment_start_number");
        psi.ArgumentList.Add(startSegmentNumber.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(Path.Combine(outputDir, "%d.ts"));

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    /// <summary>
    /// A conservative H.264 bitrate ceiling per source height, in the same
    /// range common streaming ladders use (YouTube/Netflix-style). Used to
    /// cap re-encode output so remote/proxied playback fits through a
    /// typical home upload link instead of an unbounded encode matching
    /// (or exceeding) the source's own bitrate.
    /// </summary>
    private static int PickVideoBitrateBps(int height)
    {
        return height switch
        {
            <= 0 => 4_500_000,
            <= 360 => 900_000,
            <= 480 => 1_500_000,
            <= 720 => 2_800_000,
            <= 1080 => 4_500_000,
            <= 1440 => 8_000_000,
            _ => 14_000_000
        };
    }

    private static void AddSegmenterInput(ProcessStartInfo psi, string url, double startSeconds)
    {
        psi.ArgumentList.Add("-reconnect");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-probesize");
        psi.ArgumentList.Add("256k");
        psi.ArgumentList.Add("-analyzeduration");
        psi.ArgumentList.Add("0");

        if (startSeconds > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startSeconds.ToString("F2", CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(url);
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static void AddInput(ProcessStartInfo psi, string url, double? seekSeconds)
    {
        // Tolerate brief network hiccups reading from googlevideo instead of
        // aborting the whole mux. Delay kept short so a seek doesn't sit
        // around waiting to retry - every seek already pays for a fresh
        // connection + input seek, so startup latency matters a lot here.
        psi.ArgumentList.Add("-reconnect");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max");
        psi.ArgumentList.Add("2");

        // We already know exactly which streams/codecs we're mapping (-map
        // 0:v:0/1:a:0, -c copy below), so skip ffmpeg's default full-input
        // analysis pass - it otherwise reads well more than it needs to
        // before producing any output, adding latency to every single seek.
        psi.ArgumentList.Add("-probesize");
        psi.ArgumentList.Add("256k");
        psi.ArgumentList.Add("-analyzeduration");
        psi.ArgumentList.Add("0");

        if (seekSeconds is > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(seekSeconds.Value.ToString("F2", CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(url);
    }
}
