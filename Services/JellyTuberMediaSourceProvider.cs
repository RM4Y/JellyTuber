using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Declares <see cref="MediaSourceInfo"/> directly for JellyTuber's own
/// .strm items (path pointing at /JellyTuber/Stream/{videoId}), instead of
/// letting Jellyfin discover the format by running ffprobe against the
/// stream on first playback.
///
/// That probe is expensive specifically for this plugin: a cold ffprobe
/// request forces a full yt-dlp resolve plus the first HLS segment's real
/// encode before it gets anything to analyze - measured at ~7-8s on a real
/// video, confirmed via server logs to be the dominant cost of "video
/// startup" (the actual client request that follows is served from the
/// warmed cache/session in ~0ms). Declaring the format ourselves with
/// <see cref="MediaSourceInfo.SupportsProbing"/> = false skips that pass
/// entirely for the very first playback of a video.
///
/// This is a deliberate best-effort declaration, not a guarantee: the exact
/// codec actually served depends on which internal path
/// <see cref="Api.PlaybackController.Stream"/> takes for a given request
/// (own H.264/AAC HLS re-encode in the common case; occasionally a
/// same-network redirect straight to YouTube's own combined format; rarely
/// a VP9/Opus Matroska fallback for &gt;1080p sources with no H.264
/// available) - none of which is known yet at this point, since resolving
/// it is exactly the slow step being skipped. H.264/AAC/HLS is declared
/// because it is what the overwhelming majority of requests actually get.
/// If a request lands on the rare mismatched fallback, Jellyfin's own
/// transcode path (left enabled via <see cref="MediaSourceInfo.SupportsTranscoding"/>)
/// is the safety net.
/// </summary>
public class JellyTuberMediaSourceProvider : IMediaSourceProvider
{
    private static readonly Regex StreamUrlPattern = new(@"/JellyTuber/Stream/[A-Za-z0-9_-]+", RegexOptions.Compiled);

    private readonly ILogger<JellyTuberMediaSourceProvider> _logger;

    public JellyTuberMediaSourceProvider(ILogger<JellyTuberMediaSourceProvider> logger)
    {
        _logger = logger;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        // item.Path is the .strm FILE's own path on disk (as with any other
        // library item) - the actual playback URL is the file's CONTENT.
        // Confirmed via logging: an earlier version matched against
        // item.Path directly and never matched a single real item.
        var strmPath = item.Path;
        if (string.IsNullOrEmpty(strmPath) || !strmPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        string url;
        try
        {
            url = (await File.ReadAllTextAsync(strmPath, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (IOException)
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var isMatch = StreamUrlPattern.IsMatch(url);
        _logger.LogInformation(
            "JellyTuberMediaSourceProvider.GetMediaSources for item {ItemId} ({ItemName}): strm url={Url}, isJellyTuberItem={IsMatch}",
            item.Id,
            item.Name,
            url,
            isMatch);

        if (!isMatch)
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var source = new MediaSourceInfo
        {
            Id = item.Id.ToString("N"),
            Path = url,
            Protocol = MediaProtocol.Http,
            Container = "hls",
            Name = item.Name,
            IsRemote = true,
            RunTimeTicks = item.RunTimeTicks,
            SupportsProbing = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            RequiresOpening = false,
            RequiresClosing = false,
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    IsAVC = true,
                    IsDefault = true,
                },
                new()
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    IsDefault = true,
                },
            },
        };

        return new[] { source };
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken) =>
        throw new NotSupportedException("JellyTuber media sources are declared with RequiresOpening=false and should never need opening.");
}
