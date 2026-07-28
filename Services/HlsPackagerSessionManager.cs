using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Runs one continuous ffmpeg remux-to-segments process per video being
/// played, rather than spawning a fresh ffmpeg process for every individual
/// segment. See <see cref="FfmpegMuxer.StartContinuousSegmenter"/> for why
/// re-seeking independently on every segment was rejected (it drifted audio
/// out of sync with video every few seconds).
///
/// A session is restarted (old process killed, new one spawned with a fresh
/// input seek) only when the requested segment isn't one the current
/// session is already producing or heading towards - i.e. an actual seek,
/// not just normal forward playback.
/// </summary>
internal static class HlsPackagerSessionManager
{
    /// <summary>How long a session may sit unused before it's torn down.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long to wait for a requested segment file to materialize before giving up.</summary>
    private static readonly TimeSpan SegmentWaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Software encoders to try, in preference order. jellyfin-ffmpeg always ships libopenh264.</summary>
    private static readonly string[] SoftwareEncoderPriority = { "libx264", "libopenh264" };

    private static readonly ConcurrentDictionary<string, Session> Sessions = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    /// <summary>
    /// Returns the path to the completed segment file for <paramref name="index"/>,
    /// starting or restarting the packaging session for <paramref name="videoId"/>
    /// as needed. Null if the segment never arrives (source exhausted past
    /// the end of the video, or the upstream URL failed) - the caller should
    /// treat that as a hard failure.
    /// </summary>
    public static async Task<string?> GetSegmentAsync(IMediaEncoder mediaEncoder, string videoId, ResolvedStream resolved, int index, ILogger logger, CancellationToken ct)
    {
        SweepIdle();

        var sw = Stopwatch.StartNew();
        var session = await GetOrStartSessionAsync(mediaEncoder, videoId, resolved, index, forceRestart: false, logger, ct).ConfigureAwait(false);
        var afterSessionMs = sw.ElapsedMilliseconds;
        var path = await WaitForSegmentAsync(session, index, ct).ConfigureAwait(false);
        if (path is not null || ct.IsCancellationRequested)
        {
            logger.LogInformation(
                "HLS segment {VideoId}/{Index}: session ready in {SessionMs}ms, segment ready in {TotalMs}ms total",
                videoId,
                index,
                afterSessionMs,
                sw.ElapsedMilliseconds);
            return path;
        }

        // Didn't arrive in time from the current session - most likely a
        // forward jump past what's been packaged so far. Restart right at
        // the requested index and try once more.
        logger.LogWarning(
            "HLS segment {VideoId}/{Index} did not arrive from the existing session within {TimeoutS}s (after {ElapsedMs}ms) - forcing a session restart at this index",
            videoId,
            index,
            SegmentWaitTimeout.TotalSeconds,
            sw.ElapsedMilliseconds);

        session = await GetOrStartSessionAsync(mediaEncoder, videoId, resolved, index, forceRestart: true, logger, ct).ConfigureAwait(false);
        var result = await WaitForSegmentAsync(session, index, ct).ConfigureAwait(false);
        logger.LogInformation(
            "HLS segment {VideoId}/{Index}: ready in {TotalMs}ms total after forced restart (found={Found})",
            videoId,
            index,
            sw.ElapsedMilliseconds,
            result is not null);
        return result;
    }

    /// <summary>Tears down and forgets any packaging session for <paramref name="videoId"/>.</summary>
    public static void Invalidate(string videoId)
    {
        if (Sessions.TryRemove(videoId, out var session))
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// How many segments beyond what a session has already produced still
    /// count as "reusable" rather than triggering an immediate restart.
    /// Covers ordinary forward playback (a player fetching slightly ahead of
    /// what it's currently showing) without accepting a distant forward jump
    /// as merely "not there yet" - see <see cref="IsWithinReach"/>. Expressed
    /// in segments rather than seconds, so this scales with
    /// <see cref="OnDemandHlsPackager.SegmentSeconds"/> - 9 segments at the
    /// current 2s segment length keeps the same ~18s of absolute slack this
    /// had at the original 6s segment length.
    /// </summary>
    private const int ForwardLookaheadSegments = 9;

    private static async Task<Session> GetOrStartSessionAsync(IMediaEncoder mediaEncoder, string videoId, ResolvedStream resolved, int index, bool forceRestart, ILogger logger, CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(videoId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = Sessions.TryGetValue(videoId, out var s) ? s : null;
            var reusable = !forceRestart && existing is not null && IsWithinReach(existing, index);

            var session = reusable ? existing! : StartSession(mediaEncoder, videoId, resolved, index, logger);
            session.LastAccessUtc = DateTime.UtcNow;
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Whether <paramref name="index"/> is close enough to what
    /// <paramref name="session"/> has already produced (or is about to
    /// produce) that it's worth waiting on this session, instead of
    /// restarting ffmpeg with a fresh seek straight to the target.
    ///
    /// Without this check, ANY forward index (even one minutes past what the
    /// session has produced so far) looked "reusable" - a distant forward
    /// seek would sit waiting for the session's sequential, real-time-paced
    /// encode to slowly catch up one segment at a time, only restarting once
    /// <see cref="HlsPackagerSessionManager.GetSegmentAsync"/>'s full 20s
    /// wait had already been burned. Confirmed via a real seek against a
    /// live session: 24s round trip before the correct segment appeared,
    /// almost all of it spent in that doomed wait. A distant jump is always
    /// faster served by restarting immediately with an input seek (a few
    /// seconds) than by waiting for real-time sequential encoding to arrive.
    /// </summary>
    private static bool IsWithinReach(Session session, int index)
    {
        if (index < session.BaseIndex)
        {
            return false;
        }

        int producedCount;
        try
        {
            producedCount = Directory.GetFiles(session.Dir, "*.ts").Length;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var frontier = session.BaseIndex + producedCount - 1;
        return index <= frontier + ForwardLookaheadSegments;
    }

    private static Session StartSession(IMediaEncoder mediaEncoder, string videoId, ResolvedStream resolved, int baseIndex, ILogger logger)
    {
        if (Sessions.TryRemove(videoId, out var old))
        {
            old.Dispose();
        }

        var startSeconds = baseIndex * (double)OnDemandHlsPackager.SegmentSeconds;
        var dir = Path.Combine(Path.GetTempPath(), "jellytuber-hls", videoId, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var videoEncoder = PickVideoEncoder(mediaEncoder);
        logger.LogDebug("Starting HLS packaging session for {VideoId} at segment {BaseIndex} using {Encoder}", videoId, baseIndex, videoEncoder);

        var process = FfmpegMuxer.StartContinuousSegmenter(
            mediaEncoder.EncoderPath,
            resolved.VideoUrl!,
            resolved.AudioUrl!,
            startSeconds,
            baseIndex,
            OnDemandHlsPackager.SegmentSeconds,
            dir,
            videoEncoder,
            resolved.Height);

        DrainStderrInBackground(process, logger, videoId);

        var session = new Session(dir, baseIndex, process);
        Sessions[videoId] = session;
        return session;
    }

    /// <summary>
    /// Software only, deliberately: hardware encoders (nvenc/qsv/vaapi) sit
    /// on the same GPU Jellyfin's own server-side transcoder reaches for
    /// when a client can't direct-play this plugin's stream - and testing
    /// against the real server showed that contention is real, not
    /// theoretical. Our nvenc attempt failed to even initialise while
    /// Jellyfin's own hevc_nvenc transcode of the same source was running
    /// concurrently, and only got detected after the full segment-wait
    /// timeout, adding tens of seconds to every seek before the software
    /// fallback even started. Software sidesteps that entirely; segments
    /// are short (6s) and re-encoded one at a time, which a modern CPU
    /// handles in real time without needing the GPU at all.
    /// </summary>
    private static string PickVideoEncoder(IMediaEncoder mediaEncoder)
    {
        foreach (var candidate in SoftwareEncoderPriority)
        {
            if (mediaEncoder.SupportsEncoder(candidate))
            {
                return candidate;
            }
        }

        // Last-resort guess: jellyfin-ffmpeg always ships libopenh264 as its
        // software fallback encoder, even when SupportsEncoder's own probe
        // is inconclusive.
        return "libopenh264";
    }

    private static async Task<string?> WaitForSegmentAsync(Session session, int index, CancellationToken ct)
    {
        var path = Path.Combine(session.Dir, index + ".ts");
        var nextPath = Path.Combine(session.Dir, (index + 1) + ".ts");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SegmentWaitTimeout);

        try
        {
            while (true)
            {
                if (File.Exists(nextPath) || (session.Process.HasExited && File.Exists(path)))
                {
                    return path;
                }

                if (session.Process.HasExited && !File.Exists(path))
                {
                    return null;
                }

                await Task.Delay(150, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Either the client went away (ct) or we simply timed out
            // waiting for this segment to show up - both are "no segment".
            return null;
        }
    }

    private static void DrainStderrInBackground(Process process, ILogger logger, string videoId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    logger.LogWarning("ffmpeg [{VideoId}]: {Line}", videoId, line);
                }
            }
            catch
            {
                // Process was killed mid-read; nothing useful to log.
            }
        });
    }

    private static void SweepIdle()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in Sessions)
        {
            if (now - kvp.Value.LastAccessUtc > IdleTimeout && Sessions.TryRemove(kvp.Key, out var session))
            {
                session.Dispose();
            }
        }
    }

    private sealed class Session : IDisposable
    {
        public Session(string dir, int baseIndex, Process process)
        {
            Dir = dir;
            BaseIndex = baseIndex;
            Process = process;
            LastAccessUtc = DateTime.UtcNow;
        }

        public string Dir { get; }

        public int BaseIndex { get; }

        public Process Process { get; }

        public DateTime LastAccessUtc { get; set; }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and the kill.
            }

            Process.Dispose();

            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort; OS temp cleanup will catch it eventually.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort; OS temp cleanup will catch it eventually.
            }
        }
    }
}
