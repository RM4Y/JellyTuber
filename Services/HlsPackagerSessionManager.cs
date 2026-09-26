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
    /// Upper bound on how many segments one ffmpeg process produces (one
    /// hour at 2s segments) - keeps the explicit <c>-segment_times</c> cut
    /// list <see cref="FfmpegMuxer.StartContinuousSegmenter"/> passes on the
    /// command line a sane length. A session that reaches it just chains
    /// into a fresh one (see <see cref="WatchForCompletion"/>); segments
    /// line up across sessions, so that's seamless.
    /// </summary>
    private const int MaxSegmentsPerSession = 1800;

    /// <summary>
    /// Minimum gap between two disk-cap checks. <see cref="SweepIdle"/> runs
    /// on every segment request, and the cap check walks every file in the
    /// cache - tens of thousands of 2s segments at the default 20GB.
    /// </summary>
    private static readonly TimeSpan SizeCapCheckInterval = TimeSpan.FromMinutes(1);

    private static long _lastSizeCapCheckTicks;

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

        var cached = TryGetCompletedSegmentPath(videoId, index);
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
        // forward jump past what's been packaged so far, or a GPU session
        // that failed to start. Restart right at the requested index (on
        // CPU, in the latter case) and try once more.
        ReportIfGpuStartFailed(session, logger);
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

    /// <summary>
    /// Returns the segment's path only if it's on disk AND fully written.
    /// <see cref="VideoCache.TryGetSegmentPath"/> alone (File.Exists) also
    /// matches the segment a live session is still in the middle of
    /// writing - serving that hands the player a truncated segment, i.e. a
    /// visible stutter and audio glitch. Null means "not ready": go through
    /// <see cref="GetSegmentAsync"/>, which waits for the session to finish
    /// it.
    /// </summary>
    public static string? TryGetCompletedSegmentPath(string videoId, int index)
    {
        var path = VideoCache.TryGetSegmentPath(videoId, index);
        if (path is null)
        {
            return null;
        }

        if (Sessions.TryGetValue(videoId, out var session) && session.IsWriting(index))
        {
            return null;
        }

        return path;
    }

    /// <summary>
    /// Drops <paramref name="videoId"/>'s cache up front if it was encoded at
    /// a different resolution than a new session would use now (see
    /// <see cref="PlanVideoEncode"/>) - e.g. 1080p segments precached before
    /// 4K support. Called when a playlist is handed out: otherwise the old
    /// segments get served straight from disk and the switch only happens at
    /// the first cache miss, i.e. mid-playback. No-op while a session is
    /// running for the video (it's already on the current plan).
    /// </summary>
    public static void EnsureCacheMatchesPlan(IMediaEncoder mediaEncoder, string videoId, ResolvedStream resolved, ILogger logger)
    {
        if (Sessions.ContainsKey(videoId))
        {
            return;
        }

        PlanVideoEncode(mediaEncoder, videoId, resolved, logger);
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
        // Past EndIndex, this session stops before ever getting there.
        if (index < session.BaseIndex || index >= session.EndIndex)
        {
            return false;
        }

        // Not a count of every *.ts in the folder: that folder is the
        // persistent cache now, so it also holds segments from earlier
        // sessions (e.g. the precached 0..14 below a session started at
        // 15), which inflated the frontier and made distant forward seeks
        // look reachable.
        return index <= session.FindFrontier() + ForwardLookaheadSegments;
    }

    private static Session StartSession(IMediaEncoder mediaEncoder, string videoId, ResolvedStream resolved, int baseIndex, ILogger logger)
    {
        if (Sessions.TryRemove(videoId, out var old))
        {
            ReleaseBackgroundSlot(old);
            old.Dispose();
        }

        var startSeconds = baseIndex * (double)OnDemandHlsPackager.SegmentSeconds;
        var (videoEncoder, outputHeight, scale) = PlanVideoEncode(mediaEncoder, videoId, resolved, logger);

        var dir = VideoCache.GetVideoDir(videoId);
        Directory.CreateDirectory(dir);

        var totalSegments = Math.Max(1, (int)Math.Ceiling(resolved.DurationSeconds / OnDemandHlsPackager.SegmentSeconds));
        VideoCache.EnsureMeta(videoId, resolved.DurationSeconds, totalSegments, outputHeight);

        // Stop right before the next segment that's already cached, instead
        // of re-encoding (and overwriting in place, possibly while it's
        // being served) segments we already have.
        var endIndex = Math.Min(
            Math.Min(totalSegments, baseIndex + MaxSegmentsPerSession),
            VideoCache.FindFirstCachedSegment(videoId, baseIndex + 1, totalSegments));
        endIndex = Math.Max(endIndex, baseIndex + 1);

        logger.LogInformation(
            "Starting HLS packaging session for {VideoId} at segments {BaseIndex}-{EndIndex} using {Encoder} ({SourceHeight}p source -> {OutputHeight}p)",
            videoId,
            baseIndex,
            endIndex - 1,
            videoEncoder,
            resolved.Height,
            outputHeight);

        // Always the higher (same-network) bitrate ceiling now - see
        // VideoCache's doc comment for why the cache is shared between
        // tiers instead of keeping two separate encodes.
        var process = FfmpegMuxer.StartContinuousSegmenter(
            mediaEncoder.EncoderPath,
            resolved.VideoUrl!,
            resolved.AudioUrl!,
            startSeconds,
            baseIndex,
            endIndex - baseIndex,
            OnDemandHlsPackager.SegmentSeconds,
            dir,
            videoEncoder,
            outputHeight,
            scale);

        DrainStderrInBackground(process, logger, videoId);

        var session = new Session(dir, baseIndex, endIndex, process, videoEncoder, mediaEncoder, resolved, totalSegments);
        Sessions[videoId] = session;
        WatchForCompletion(videoId, session, logger);
        return session;
    }

    /// <summary>
    /// Waits for this session's ffmpeg process to exit, then - unless it was
    /// already superseded by a restart - either marks the video fully cached
    /// (every segment is on disk), or, if the session simply reached its
    /// <see cref="Session.EndIndex"/> with segments still missing further
    /// on, chains straight into a new session at the next missing one
    /// (keeping its background/interactive status). This is what lets a
    /// video left encoding unattended (see <see cref="DemoteToBackground"/>)
    /// eventually finish and stop consuming a background slot on its own,
    /// with no viewer needing to ever come back and ask for the rest of it -
    /// and what keeps an interactive session one step ahead of the viewer
    /// across an already-cached stretch, instead of the player hitting a
    /// cold start right after it.
    /// </summary>
    private static void WatchForCompletion(string videoId, Session session, ILogger logger)
    {
        _ = Task.Run(async () =>
        {
            int exitCode;
            try
            {
                await session.Process.WaitForExitAsync().ConfigureAwait(false);
                exitCode = session.Process.ExitCode;
            }
            catch
            {
                return;
            }

            if (!Sessions.TryGetValue(videoId, out var current) || !ReferenceEquals(current, session))
            {
                return; // superseded by a restart already; that session owns completion now
            }

            ReportIfGpuStartFailed(session, logger);

            var nextMissing = VideoCache.FindFirstMissingSegment(videoId, 0, session.TotalSegments);
            if (nextMissing is null)
            {
                VideoCache.MarkCompleted(videoId, session.Resolved.DurationSeconds, session.TotalSegments);
                logger.LogInformation("Video cache: {VideoId} fully cached ({Segments} segments)", videoId, session.TotalSegments);
            }
            else if (exitCode == 0 && session.EndIndex < session.TotalSegments)
            {
                var next = VideoCache.FindFirstMissingSegment(videoId, session.EndIndex, session.TotalSegments);
                if (next is not null && await TryChainAsync(videoId, session, next.Value, logger).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (Sessions.TryRemove(new System.Collections.Generic.KeyValuePair<string, Session>(videoId, session)))
            {
                ReleaseBackgroundSlot(session);
            }
        });
    }

    private static async Task<bool> TryChainAsync(string videoId, Session finished, int nextIndex, ILogger logger)
    {
        var gate = Gates.GetOrAdd(videoId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Sessions.TryGetValue(videoId, out var current) || !ReferenceEquals(current, finished))
            {
                return true; // a real request already restarted it meanwhile
            }

            var wasBackground = finished.IsBackground;
            var lastAccess = finished.LastAccessUtc;

            // Releases finished's background slot (if any); re-taken below.
            var next = StartSession(finished.MediaEncoder, videoId, finished.Resolved, nextIndex, logger);
            next.LastAccessUtc = lastAccess;

            if (wasBackground)
            {
                lock (BackgroundLock)
                {
                    _backgroundCount++;
                    next.IsBackground = true;
                }

                TryLowerPriority(next.Process);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not chain HLS packaging session for {VideoId} at segment {Index}", videoId, nextIndex);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Picks the encoder and output height for a new session of
    /// <paramref name="videoId"/>.
    ///
    /// The GPU (<see cref="GpuEncoder"/>) is used for every resolution
    /// whenever <see cref="GpuEncoder.IsUsable"/> - the owner's explicit
    /// choice: it frees the CPU entirely and encodes far faster than real
    /// time. It shares NVENC with Jellyfin's own server-side transcodes,
    /// and an NVENC session once failed to initialise while one of those was
    /// running - so a failure to start is detected
    /// (<see cref="ReportIfGpuStartFailed"/>) and falls back to libx264,
    /// downscaled to 1080p for a &gt;1080p source since the CPU can't encode
    /// above that in real time.
    ///
    /// All of one video's cached segments must share one resolution, or
    /// playback switches resolution mid-video. If what's cached doesn't
    /// match the plan, the cache is dropped and re-encoded - except when the
    /// mismatch is only a temporary GPU fallback on a 4K cache, which then
    /// continues in 1080p rather than throwing the 4K segments away.
    /// </summary>
    private static (string Encoder, int OutputHeight, bool Scale) PlanVideoEncode(IMediaEncoder mediaEncoder, string videoId, ResolvedStream resolved, ILogger logger)
    {
        var sourceHeight = resolved.Height;
        var aboveHd = sourceHeight > MaxCpuHeight;

        var gpuEncoder = GpuEncoder.UsableEncoder;
        var plan = gpuEncoder is not null
            ? (gpuEncoder, sourceHeight, false)
            : (PickSoftwareEncoder(mediaEncoder), aboveHd ? MaxCpuHeight : sourceHeight, aboveHd);

        var meta = VideoCache.TryGetMeta(videoId);
        if (meta is not null)
        {
            var cachedHeight = meta.EncodedHeight ?? Math.Min(sourceHeight, MaxCpuHeight);
            var isGpuFallback = aboveHd && gpuEncoder is null && cachedHeight > MaxCpuHeight;
            if (cachedHeight != plan.Item2 && !isGpuFallback)
            {
                logger.LogInformation(
                    "Video cache: {VideoId} was cached at {CachedHeight}p, now encoding at {Height}p - dropping the old segments",
                    videoId,
                    cachedHeight,
                    plan.Item2);
                VideoCache.Delete(videoId);
            }
        }

        return plan;
    }

    /// <summary>Above this, libx264 can't encode in real time - see <see cref="GpuEncoder"/>.</summary>
    private const int MaxCpuHeight = 1080;

    /// <summary>
    /// A GPU session that exits with an error without producing a single
    /// segment failed to start (e.g. no free NVENC session) - disable the
    /// GPU path for a while so the restart that follows falls back to CPU.
    /// </summary>
    private static void ReportIfGpuStartFailed(Session session, ILogger logger)
    {
        if (!GpuEncoder.IsHardware(session.VideoEncoder)
            || !HasExitedSafe(session.Process)
            || session.FindFrontier() >= session.BaseIndex)
        {
            return;
        }

        try
        {
            if (session.Process.ExitCode == 0)
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return; // disposed by a concurrent restart - it was killed, not failed
        }

        GpuEncoder.ReportFailure(logger);
    }

    private static string PickSoftwareEncoder(IMediaEncoder mediaEncoder)
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
    /// then enforces the video-cache disk-size cap, if configured (at most
    /// once per <see cref="SizeCapCheckInterval"/>).
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

        var nowTicks = now.Ticks;
        var lastCheck = Interlocked.Read(ref _lastSizeCapCheckTicks);
        if (nowTicks - lastCheck < SizeCapCheckInterval.Ticks
            || Interlocked.CompareExchange(ref _lastSizeCapCheckTicks, nowTicks, lastCheck) != lastCheck)
        {
            return;
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
        public Session(string dir, int baseIndex, int endIndex, Process process, string videoEncoder, IMediaEncoder mediaEncoder, ResolvedStream resolved, int totalSegments)
        {
            VideoEncoder = videoEncoder;
            Dir = dir;
            BaseIndex = baseIndex;
            EndIndex = endIndex;
            Process = process;
            MediaEncoder = mediaEncoder;
            Resolved = resolved;
            TotalSegments = totalSegments;
            LastAccessUtc = DateTime.UtcNow;
        }

        public string Dir { get; }

        public int BaseIndex { get; }

        /// <summary>Exclusive: the process stops after writing segment EndIndex - 1.</summary>
        public int EndIndex { get; }

        public Process Process { get; }

        public string VideoEncoder { get; }

        public IMediaEncoder MediaEncoder { get; }

        public ResolvedStream Resolved { get; }

        public int TotalSegments { get; }

        public DateTime LastAccessUtc { get; set; }

        /// <summary>True once demoted by <see cref="DemoteToBackground"/> - counts against <see cref="_backgroundCount"/>.</summary>
        public bool IsBackground { get; set; }

        /// <summary>
        /// Highest index this session has started writing (BaseIndex - 1 if
        /// none yet). Every index in [BaseIndex, EndIndex) is written by
        /// this session alone (see StartSession's EndIndex), so a contiguous
        /// scan from BaseIndex only ever sees this session's own output.
        /// </summary>
        public int FindFrontier()
        {
            var i = BaseIndex;
            while (i < EndIndex && File.Exists(Path.Combine(Dir, i + ".ts")))
            {
                i++;
            }

            return i - 1;
        }

        /// <summary>
        /// Whether segment <paramref name="index"/> is the one this session is
        /// still writing: the segment muxer only creates file N+1 once N is
        /// closed, and the last one is only done when the process exits.
        /// </summary>
        public bool IsWriting(int index)
        {
            if (index < BaseIndex || index >= EndIndex || HasExitedSafe(Process))
            {
                return false;
            }

            return index == EndIndex - 1 || !File.Exists(Path.Combine(Dir, (index + 1) + ".ts"));
        }

        public void Dispose()
        {
            var killed = false;
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    killed = true;
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and the kill.
            }

            if (killed)
            {
                // Killed mid-write (a seek restart, a precache reaching its
                // target, the background slot cap...): the segment it was
                // writing is truncated, and left alone it would be served
                // from VideoCache as a permanently broken segment. Wait for
                // the process to actually be gone, then delete it.
                try
                {
                    Process.WaitForExit(2000);
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }

                var frontier = FindFrontier();
                if (frontier >= BaseIndex)
                {
                    try
                    {
                        File.Delete(Path.Combine(Dir, frontier + ".ts"));
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

            Process.Dispose();

            // Deliberately NOT deleting Dir: it's VideoCache's persistent
            // cache directory now, not a throwaway temp one. Eviction (the
            // disk-size sweep, or a video being removed from the library)
            // is what deletes it, via VideoCache.Delete.
        }
    }
}
