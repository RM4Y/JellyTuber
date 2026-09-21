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
/// Segments land in <see cref="VideoCache"/>'s persistent per-video
/// directory (not a throwaway temp one), and a session is no longer killed
/// the moment playback stops or goes idle - see <see cref="DemoteToBackground"/>.
/// That's what turns this into an actual cache: once a video's segments are
/// all on disk, later plays (by anyone) are served straight from
/// <see cref="VideoCache"/> without ever starting ffmpeg again.
///
/// Sessions are keyed by videoId alone now (previously also by network
/// tier): the cache is shared between LAN and remote viewers, always
/// encoded at the higher (same-network) bitrate ceiling - see
/// <see cref="VideoCache"/>'s own doc comment for that trade-off.
///
/// A session is restarted (old process killed, new one spawned with a fresh
/// input seek) only when the requested segment isn't one the current
/// session is already producing or heading towards - i.e. an actual seek,
/// not just normal forward playback.
/// </summary>
internal static class HlsPackagerSessionManager
{
    /// <summary>
    /// How long a session may sit unrequested before it's demoted to a
    /// background completion job (see <see cref="DemoteToBackground"/>).
    /// Was also the point a session got destroyed outright; it no longer is
    /// - an idle session just isn't "interactive" anymore, but still counts
    /// towards finishing its video's cache if a background slot is free.
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for a requested segment file to materialize before giving up.</summary>
    private static readonly TimeSpan SegmentWaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Software encoders to try, in preference order. jellyfin-ffmpeg always ships libopenh264.</summary>
    private static readonly string[] SoftwareEncoderPriority = { "libx264", "libopenh264" };

    private static readonly ConcurrentDictionary<string, Session> Sessions = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    /// <summary>Guards <see cref="_backgroundCount"/> against concurrent Demote/Invalidate/completion callbacks.</summary>
    private static readonly object BackgroundLock = new();

    /// <summary>How many sessions are currently running as a background completion job (no active viewer).</summary>
    private static int _backgroundCount;

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

    /// <summary>
    /// Returns the path to the completed segment file for <paramref name="index"/>,
    /// starting or restarting the packaging session for <paramref name="videoId"/>
    /// as needed. Checks <see cref="VideoCache"/> first - a video whose
    /// segments already landed on disk from a previous play (or from this
    /// same session running ahead of the viewer) never touches ffmpeg at
    /// all. Null if the segment never arrives (source exhausted past the end
    /// of the video, or the upstream URL failed) - the caller should treat
    /// that as a hard failure.
    /// </summary>
    public static async Task<string?> GetSegmentAsync(IMediaEncoder mediaEncoder, string videoId, ResolvedStream resolved, int index, ILogger logger, CancellationToken ct)
    {
        SweepIdle(logger);

        var cached = VideoCache.TryGetSegmentPath(videoId, index);
        if (cached is not null)
        {
            return cached;
        }

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

    /// <summary>Tears down and forgets a video's packaging session (process killed; cached segments on disk are kept, see <see cref="VideoCache"/>).</summary>
    public static void Invalidate(string videoId)
    {
        if (Sessions.TryRemove(videoId, out var session))
        {
            ReleaseBackgroundSlot(session);
            session.Dispose();
        }
    }

    /// <summary>
    /// Called on a real playback stop (or an idle timeout) for a video
    /// that isn't fully cached yet: instead of tearing the session down,
    /// keep its ffmpeg process running towards a complete cache, gated by
    /// <see cref="Configuration.PluginConfiguration.MaxConcurrentBackgroundEncodes"/>
    /// so this can never reintroduce the CPU pile-up that killing sessions
    /// on stop was originally written to prevent (see
    /// <see cref="PlaybackStopSessionCleaner"/>'s own doc comment) - once
    /// the cap is hit, additional videos simply stop where they are instead
    /// of piling up; whatever they already produced stays cached for next
    /// time.
    /// </summary>
    /// <returns>
    /// True if the session is now (or already was) continuing in the
    /// background - i.e. still alive, so a caller must NOT assume its
    /// process has exited or clean up any file it might still be writing.
    /// False if there was nothing to demote, the video was already fully
    /// cached, or the background slot cap forced it to be killed outright -
    /// in every false case the process is guaranteed gone.
    /// </returns>
    public static bool DemoteToBackground(string videoId)
    {
        if (!Sessions.TryGetValue(videoId, out var session))
        {
            return false;
        }

        if (VideoCache.IsCompleted(videoId))
        {
            Invalidate(videoId);
            return false;
        }

        lock (BackgroundLock)
        {
            if (session.IsBackground)
            {
                return true;
            }

            var max = Math.Max(1, Plugin.Instance?.Configuration.MaxConcurrentBackgroundEncodes ?? 2);
            if (_backgroundCount >= max)
            {
                if (Sessions.TryRemove(videoId, out var toStop))
                {
                    toStop.Dispose();
                }

                return false;
            }

            _backgroundCount++;
            session.IsBackground = true;
            TryLowerPriority(session.Process);
            return true;
        }
    }

    private static void ReleaseBackgroundSlot(Session session)
    {
        if (!session.IsBackground)
        {
            return;
        }

        lock (BackgroundLock)
        {
            _backgroundCount = Math.Max(0, _backgroundCount - 1);
        }
    }

    /// <summary>
    /// Background completion jobs sit on the same CPU as a freshly-starting
    /// interactive session; a lower OS scheduling priority lets the
    /// interactive one win contention for cycles without needing to kill
    /// the background job outright. Best-effort: some platforms/sandboxes
    /// don't allow changing another process's priority.
    /// </summary>
    private static void TryLowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // best effort
        }
    }

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
    /// <see cref="GetSegmentAsync"/>'s full 20s wait had already been
    /// burned. Confirmed via a real seek against a live session: 24s round
    /// trip before the correct segment appeared, almost all of it spent in
    /// that doomed wait. A distant jump is always faster served by
    /// restarting immediately with an input seek (a few seconds) than by
    /// waiting for real-time sequential encoding to arrive.
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
            ReleaseBackgroundSlot(old);
            old.Dispose();
        }

        var startSeconds = baseIndex * (double)OnDemandHlsPackager.SegmentSeconds;
        var dir = VideoCache.GetVideoDir(videoId);
        Directory.CreateDirectory(dir);

        var totalSegments = Math.Max(1, (int)Math.Ceiling(resolved.DurationSeconds / OnDemandHlsPackager.SegmentSeconds));
        VideoCache.EnsureMeta(videoId, resolved.DurationSeconds, totalSegments);

        var videoEncoder = PickVideoEncoder(mediaEncoder);
        logger.LogDebug(
            "Starting HLS packaging session for {VideoId} at segment {BaseIndex} using {Encoder}",
            videoId,
            baseIndex,
            videoEncoder);

        // Always the higher (same-network) bitrate ceiling now - see
        // VideoCache's doc comment for why the cache is shared between
        // tiers instead of keeping two separate encodes.
        var process = FfmpegMuxer.StartContinuousSegmenter(
            mediaEncoder.EncoderPath,
            resolved.VideoUrl!,
            resolved.AudioUrl!,
            startSeconds,
            baseIndex,
            OnDemandHlsPackager.SegmentSeconds,
            dir,
            videoEncoder,
            resolved.Height,
            isSameNetwork: true);

        DrainStderrInBackground(process, logger, videoId);

        var session = new Session(dir, baseIndex, process, resolved.DurationSeconds, totalSegments);
        Sessions[videoId] = session;
        WatchForCompletion(videoId, session, logger);
        return session;
    }

    /// <summary>
    /// Waits for this session's ffmpeg process to exit, then - unless it was
    /// already superseded by a restart - marks the video fully cached (if
    /// every segment up to <see cref="Session.TotalSegments"/> actually
    /// landed) and frees its background slot. This is what lets a video
    /// left encoding unattended (see <see cref="DemoteToBackground"/>)
    /// eventually finish and stop consuming a background slot on its own,
    /// with no viewer needing to ever come back and ask for the rest of it.
    /// </summary>
    private static void WatchForCompletion(string videoId, Session session, ILogger logger)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await session.Process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (!Sessions.TryGetValue(videoId, out var current) || !ReferenceEquals(current, session))
            {
                return; // superseded by a restart already; that session owns completion now
            }

            int produced;
            try
            {
                produced = Directory.GetFiles(session.Dir, "*.ts").Length;
            }
            catch
            {
                produced = 0;
            }

            if (produced >= session.TotalSegments)
            {
                VideoCache.MarkCompleted(videoId, session.DurationSeconds, session.TotalSegments);
                logger.LogInformation("Video cache: {VideoId} fully cached ({Segments} segments)", videoId, session.TotalSegments);
            }

            Sessions.TryRemove(videoId, out _);
            ReleaseBackgroundSlot(session);
        });
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
    /// are short (2s) and re-encoded one at a time, which a modern CPU
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
                var exited = HasExitedSafe(session.Process);

                if (File.Exists(nextPath) || (exited && File.Exists(path)))
                {
                    return path;
                }

                if (exited && !File.Exists(path))
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

    /// <summary>
    /// <see cref="Process.HasExited"/> throws <see cref="InvalidOperationException"/>
    /// ("No process is associated with this object"), not merely "true",
    /// once the process has been Dispose()'d - which is exactly what a
    /// concurrent restart on the SAME session key does mid-wait: two
    /// requests can be waiting on one shared session, one of them decides
    /// to force a restart (<see cref="StartSession"/> disposes the old
    /// <see cref="Session"/>, including its <see cref="Process"/>), and the
    /// other is still sitting right here reading <c>HasExited</c> off that
    /// same, now-disposed object. Confirmed via a real 500 in production
    /// logs, not theoretical. A racing dispose means the session is gone
    /// either way, so treat it the same as a self-terminated process.
    /// </summary>
    private static bool HasExitedSafe(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
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

    /// <summary>
    /// Demotes any session that's gone quiet (no segment request in
    /// <see cref="IdleTimeout"/>) to a background completion job - same
    /// path as a real playback stop, see <see cref="DemoteToBackground"/> -
    /// then enforces the video-cache disk-size cap, if configured.
    /// </summary>
    private static void SweepIdle(ILogger logger)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in Sessions)
        {
            if (!kvp.Value.IsBackground && now - kvp.Value.LastAccessUtc > IdleTimeout)
            {
                DemoteToBackground(kvp.Key);
            }
        }

        var maxGb = Plugin.Instance?.Configuration.VideoCacheMaxGB ?? 20;
        if (maxGb > 0)
        {
            var protectedIds = new System.Collections.Generic.HashSet<string>(Sessions.Keys, StringComparer.Ordinal);
            VideoCache.EnforceSizeCap(maxGb * 1024L * 1024 * 1024, protectedIds, logger);
        }
    }

    private sealed class Session : IDisposable
    {
        public Session(string dir, int baseIndex, Process process, double durationSeconds, int totalSegments)
        {
            Dir = dir;
            BaseIndex = baseIndex;
            Process = process;
            DurationSeconds = durationSeconds;
            TotalSegments = totalSegments;
            LastAccessUtc = DateTime.UtcNow;
        }

        public string Dir { get; }

        public int BaseIndex { get; }

        public Process Process { get; }

        public double DurationSeconds { get; }

        public int TotalSegments { get; }

        public DateTime LastAccessUtc { get; set; }

        /// <summary>True once demoted by <see cref="DemoteToBackground"/> - counts against <see cref="_backgroundCount"/>.</summary>
        public bool IsBackground { get; set; }

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

            // Deliberately NOT deleting Dir: it's VideoCache's persistent
            // cache directory now, not a throwaway temp one. Eviction (the
            // disk-size sweep, or a video being removed from the library)
            // is what deletes it, via VideoCache.Delete.
        }
    }
}
