using System;
using System.Diagnostics;
using System.Globalization;

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
