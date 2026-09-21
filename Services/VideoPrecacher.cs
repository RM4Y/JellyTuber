using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Pre-encodes the first few seconds of a video into <see cref="VideoCache"/>
/// ahead of any real play, so <see cref="Api.PlaybackController.Stream"/>
/// serves that window straight from disk instead of paying for a fresh
/// yt-dlp resolve + ffmpeg cold-start at that moment. Shared by
/// <see cref="ScheduledTasks.YouTubeSyncTask"/> (fires right after a video is
/// first synced) and <see cref="ScheduledTasks.BackfillPrecacheTask"/> (a
/// one-off sweep over videos that predate that sync-time precache).
/// </summary>
internal static class VideoPrecacher
{
    /// <summary>
    /// Resolves <paramref name="videoId"/> and encodes its first
    /// <paramref name="precacheSeconds"/> into <see cref="VideoCache"/>, then
    /// kills the ffmpeg session outright - unlike a real viewer stopping
    /// mid-video (see <see cref="HlsPackagerSessionManager.DemoteToBackground"/>),
    /// there's no viewer here at all, so there's nothing to justify letting
    /// the encode keep running unattended towards the full video (which is
    /// what used to happen whenever a background slot was free - turning a
    /// "30 seconds" precache into a full-length transcode for every video a
    /// sync pass discovers). A viewer who plays past the precached window
    /// just pays a fresh cold-start, same as any other cache miss. A no-op
    /// (after one cheap on-disk check, no yt-dlp call) if the target window
    /// is already cached - safe to call repeatedly on the same video.
    /// </summary>
    public static async Task PrecacheAsync(
        IHttpClientFactory httpClientFactory,
        IMediaEncoder mediaEncoder,
        string videoId,
        int precacheSeconds,
        ILogger logger,
        CancellationToken ct)
    {
        var existingMeta = VideoCache.TryGetMeta(videoId);
        if (existingMeta is not null)
        {
            var alreadyTarget = Math.Min(
                existingMeta.TotalSegments,
                (int)Math.Ceiling(precacheSeconds / (double)OnDemandHlsPackager.SegmentSeconds));

            if (alreadyTarget > 0 && VideoCache.TryGetSegmentPath(videoId, alreadyTarget - 1) is not null)
            {
                return;
            }
        }

        var resolver = new PlaybackResolver(httpClientFactory, logger);
        var resolved = await resolver.ResolveAsync(videoId, ct).ConfigureAwait(false);

        // Mirrors PlaybackController.Stream's own gating: only a DASH source
        // with a known duration and an H.264+AAC codec pair actually goes
        // through HlsPackagerSessionManager/VideoCache. Anything else (no
        // duration, VP9/AV1, a combined-HLS/direct URL) uses a different
        // playback path this cache doesn't cover, so there's nothing to
        // precache.
        if (resolved?.VideoUrl is null || resolved.AudioUrl is null
            || resolved.DurationSeconds <= 0 || !resolved.IsTsCompatible)
        {
            return;
        }

        var totalSegments = (int)Math.Ceiling(resolved.DurationSeconds / OnDemandHlsPackager.SegmentSeconds);
        var targetSegments = Math.Min(
            totalSegments,
            (int)Math.Ceiling(precacheSeconds / (double)OnDemandHlsPackager.SegmentSeconds));

        try
        {
            for (var i = 0; i < targetSegments; i++)
            {
                var path = await HlsPackagerSessionManager
                    .GetSegmentAsync(mediaEncoder, videoId, resolved, i, logger, ct)
                    .ConfigureAwait(false);
                if (path is null)
                {
                    // Source exhausted early or a transient failure -
                    // whatever already landed in VideoCache stays cached.
                    break;
                }
            }
        }
        finally
        {
            // Kill outright - no viewer is waiting on this session, so there's
            // nothing to justify letting it keep encoding towards the full
            // video. Invalidate() is a no-op if the session already finished
            // and removed itself (video was short enough to complete within
            // targetSegments).
            HlsPackagerSessionManager.Invalidate(videoId);

            // The process is confirmed gone. The continuous segmenter
            // creates the NEXT segment's file the moment it starts writing
            // it (that's the signal WaitForSegmentAsync waits on to know the
            // previous one is done) - so killing it right after the target
            // segment arrives reliably leaves a truncated/empty file one
            // index past it. Confirmed in production: a 0-byte segment left
            // behind this way is indistinguishable from a real cache hit to
            // VideoCache.TryGetSegmentPath (File.Exists alone), so it would
            // get served as a permanently broken segment instead of ever
            // being re-encoded. Delete it; harmless no-op if it doesn't
            // exist.
            TryDeleteStraySegment(videoId, targetSegments);
        }
    }

    private static void TryDeleteStraySegment(string videoId, int index)
    {
        try
        {
            var path = VideoCache.GetSegmentPath(videoId, index);
            if (File.Exists(path))
            {
                File.Delete(path);
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
