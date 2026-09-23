using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

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
    /// Segments from DIFFERENT sessions also have to line up, now that
    /// they're mixed in <see cref="VideoCache"/> (e.g. the first 30s from
    /// <see cref="VideoPrecacher"/>, the rest from the session a real play
    /// starts at segment 15). Three things make segment N always hold
    /// exactly [N*segmentSeconds, (N+1)*segmentSeconds) on the video's
    /// absolute timeline, whichever session produced it - confirmed in
    /// production that without them, playback stuttered and audio drifted
    /// right at the precache boundary:
    /// <list type="bullet">
    /// <item><c>-segment_times</c> (an explicit cut grid) instead of
    /// <c>-segment_time</c>: the latter skipped the very first cut (segment 0
    /// came out 4s long) and cut at ANY keyframe past its target, so every
    /// x264 scene-cut keyframe added an extra short segment and shifted
    /// every later index off the playlist's grid.</item>
    /// <item><c>-sc_threshold 0</c> (libx264): no scene-cut keyframes at all,
    /// only the forced ones on the grid.</item>
    /// <item><c>-initial_offset</c>: after an input seek ffmpeg rebases output
    /// timestamps to ~0, so a session started at segment 15 used to emit
    /// timestamps going BACKWARDS from the cached segment 14 before it.
    /// This shifts them back to the absolute position (applied after the
    /// segment muxer's cut decisions, so the grid above stays relative to
    /// the session's own start).</item>
    /// </list>
    ///
    /// <paramref name="videoEncoder"/> "h264_nvenc" (see <see cref="GpuEncoder"/>,
    /// the default whenever usable) also decodes on the GPU and keeps frames
    /// there end to end. <paramref name="scaleToOutputHeight"/> downscales to
    /// <paramref name="outputHeight"/> first - the libx264 fallback for a
    /// &gt;1080p source when the GPU isn't usable.
    ///
    /// The session stops after <paramref name="segmentCount"/> segments
    /// (<c>-t</c>) - see <see cref="HlsPackagerSessionManager"/> for why it
    /// never runs on into segments that are already cached.
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
    public static Process StartContinuousSegmenter(string ffmpegPath, string videoUrl, string audioUrl, double startSeconds, int startSegmentNumber, int segmentCount, int segmentSeconds, string outputDir, string videoEncoder, int outputHeight, bool scaleToOutputHeight, bool isSameNetwork)
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

        var isNvenc = string.Equals(videoEncoder, GpuEncoder.Encoder, StringComparison.Ordinal);
        if (isNvenc)
        {
            // Input option - applies to the video input right below only.
            // Falls back to software decoding on its own if CUDA can't
            // decode this codec; h264_nvenc takes system-memory frames too.
            psi.ArgumentList.Add("-hwaccel");
            psi.ArgumentList.Add("cuda");
            psi.ArgumentList.Add("-hwaccel_output_format");
            psi.ArgumentList.Add("cuda");
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
        else if (scaleToOutputHeight && outputHeight > 0)
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale=-2:{outputHeight.ToString(CultureInfo.InvariantCulture)}");
        }

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(videoEncoder);

        if (string.Equals(videoEncoder, "libx264", StringComparison.Ordinal))
        {
            // Speed over compression efficiency: segments are produced
            // one at a time to keep up with real-time playback, not stored
            // long-term, so a fast preset matters more than bitrate savings.
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("veryfast");

            // Keyframes only where -force_key_frames puts them (see the
            // doc comment above).
            psi.ArgumentList.Add("-sc_threshold");
            psi.ArgumentList.Add("0");

            // 21 -> 19: a real quality bump for a marginal CPU cost at
            // "veryfast" - x264's speed/quality curve is fairly flat in this
            // range, the preset dominates encode time far more than a 2-point
            // CRF change does. The maxrate/bufsize cap below still protects
            // both tiers from CRF picking an unbounded bitrate on high-motion
            // segments - CRF only lowers the *target* for everything below
            // that cap.
            psi.ArgumentList.Add("-crf");
            psi.ArgumentList.Add("19");

            // maxrate/bufsize on top of CRF as a real VBV ceiling - the
            // standard "capped CRF" recipe. Originally remote-only (to fit
            // the server's own upload link), now applied to LAN too: on a
            // real 4K/high-motion source, veryfast/CRF-19 libx264 can take
            // longer than the segment's own real-time duration to encode
            // on typical hardware with no ceiling at all, which stalls
            // playback exactly like a cache miss would. The same-network
            // ladder's ceiling is still far above what a LAN needs to
            // saturate (up to 28Mbps for >1440p), so this costs quality only
            // on genuinely extreme source bitrates, not ordinary 1080p/4K
            // content.
            var bitrateBps = PickVideoBitrateBps(outputHeight, isSameNetwork);
            psi.ArgumentList.Add("-maxrate");
            psi.ArgumentList.Add(bitrateBps.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-bufsize");
            psi.ArgumentList.Add((bitrateBps * 2).ToString(CultureInfo.InvariantCulture));
        }
        else if (isNvenc)
        {
            // Measured on the real server (RTX 4070 Ti, 4K60 VP9 source):
            // p4 encodes ~1.6x real time, p5 was slower than real time -
            // and YouTube itself only serves the 4K source at ~1.5x, so a
            // slower preset would buy nothing but stalls. Same capped-
            // quality recipe as libx264: constant quality, bounded by the
            // ladder's ceiling.
            var bitrateBps = PickVideoBitrateBps(outputHeight, isSameNetwork);
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("p4");
            psi.ArgumentList.Add("-tune");
            psi.ArgumentList.Add("hq");
            psi.ArgumentList.Add("-profile:v");
            psi.ArgumentList.Add("high");
            psi.ArgumentList.Add("-rc");
            psi.ArgumentList.Add("vbr");
            psi.ArgumentList.Add("-cq");
            psi.ArgumentList.Add("21");
            psi.ArgumentList.Add("-b:v");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-maxrate");
            psi.ArgumentList.Add(bitrateBps.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-bufsize");
            psi.ArgumentList.Add((bitrateBps * 2).ToString(CultureInfo.InvariantCulture));

            // NVENC's equivalents of -sc_threshold 0 above, plus making the
            // -force_key_frames ones real IDR frames so every segment is
            // independently decodable.
            psi.ArgumentList.Add("-no-scenecut");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-forced-idr");
            psi.ArgumentList.Add("1");
        }
        else if (string.Equals(videoEncoder, "libopenh264", StringComparison.Ordinal))
        {
            // libopenh264's rate control needs to be explicitly switched into
            // bitrate mode - its default ("quality" mode) ignores -b:v
            // entirely (confirmed via real encode: -b:v alone had no effect
            // on output size) - so unlike libx264 above, this encoder can't
            // just drop the cap for same-network playback; it always needs
            // an explicit target. It gets the same-network ladder's much
            // higher ceiling instead, which is still a real cap (openh264's
            // VBV-style enforcement is considerably softer than x264's -
            // treat this as best-effort either way).
            var bitrateBps = PickVideoBitrateBps(outputHeight, isSameNetwork);
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

        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(((long)segmentCount * segmentSeconds).ToString(CultureInfo.InvariantCulture));

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("segment");
        if (segmentCount > 1)
        {
            var cutTimes = new StringBuilder();
            for (var i = 1; i < segmentCount; i++)
            {
                if (i > 1)
                {
                    cutTimes.Append(',');
                }

                cutTimes.Append(((long)i * segmentSeconds).ToString(CultureInfo.InvariantCulture));
            }

            psi.ArgumentList.Add("-segment_times");
            psi.ArgumentList.Add(cutTimes.ToString());
        }

        psi.ArgumentList.Add("-initial_offset");
        psi.ArgumentList.Add(startSeconds.ToString("F3", CultureInfo.InvariantCulture));
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
    /// A H.264 bitrate ceiling per source height. Two ladders:
    /// <paramref name="isSameNetwork"/> false (remote/proxied playback) uses
    /// a conservative ceiling, in the same range common streaming ladders
    /// use (YouTube/Netflix-style), so the encode fits through a typical
    /// home upload link instead of an unbounded encode matching (or
    /// exceeding) the source's own bitrate - that link is usually the
    /// tightest one in the path for a remote client. <paramref name="isSameNetwork"/>
    /// true (LAN playback) uses a much higher ceiling instead, since a local
    /// client shares the LAN's bandwidth rather than the server's own
    /// uplink - there's no equivalent bottleneck to protect there, and the
    /// old conservative ceiling was needlessly capping local quality below
    /// what the source (and the network) could actually support. Only
    /// actually load-bearing for libopenh264 - libx264 skips the cap
    /// entirely for the same-network case (see call site).
    /// </summary>
    public static int PickVideoBitrateBps(int height, bool isSameNetwork)
    {
        return isSameNetwork
            ? height switch
            {
                <= 0 => 9_000_000,
                <= 360 => 1_800_000,
                <= 480 => 3_000_000,
                <= 720 => 6_000_000,
                <= 1080 => 9_000_000,
                <= 1440 => 16_000_000,
                _ => 28_000_000
            }
            : height switch
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
