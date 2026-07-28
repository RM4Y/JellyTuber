using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Builds a synthetic on-demand HLS media playlist for a DASH source (separate
/// video-only and audio-only URLs) that JellyTuber re-segments and muxes
/// itself, one short segment at a time, instead of piping one continuous
/// muxed stream and faking byte-range seeks on top of it.
///
/// Real HLS segments are each independently addressable by time, so a seek
/// is just a request for a different segment URL - no byte-offset-to-time
/// guessing, and no risk of a player trying to splice together two
/// unrelated ffmpeg-generated streams under one HTTP byte range (which is
/// what made big seeks land in the wrong place or crash the player: each
/// seek restarted ffmpeg and produced a brand new Matroska stream that the
/// client had been told, via a 206 Partial Content response, was merely a
/// continuation of the one it already had).
/// </summary>
internal static class OnDemandHlsPackager
{
    /// <summary>
    /// Target length of each generated segment, in seconds. Kept short
    /// deliberately: the first segment can only be served once this many
    /// seconds of source video have been re-encoded, and most HLS players
    /// wait for several segments (not just one) to buffer before starting
    /// playback at all - so this constant is the single biggest lever on
    /// perceived startup latency for remote/proxied playback. Measured
    /// directly against a real YouTube source: 6s segments took ~3.3s of
    /// real encode time before segment 0 was ready; 3s took ~1s; 2s took
    /// ~0.6s. Going shorter still helps a little but starts trading that off
    /// against more frequent forced keyframes (worse compression efficiency)
    /// and more HTTP requests per video - 2s is close to the point of
    /// diminishing returns.
    /// </summary>
    public const int SegmentSeconds = 2;

    public static string BuildPlaylist(double durationSeconds, Func<int, string> segmentUrl)
    {
        var count = Math.Max(1, (int)Math.Ceiling(durationSeconds / SegmentSeconds));
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append("#EXT-X-TARGETDURATION:").Append(SegmentSeconds).Append('\n');
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");

        for (var i = 0; i < count; i++)
        {
            var segStart = i * (double)SegmentSeconds;
            var segLength = Math.Min(SegmentSeconds, durationSeconds - segStart);
            if (segLength <= 0)
            {
                segLength = SegmentSeconds;
            }

            sb.Append("#EXTINF:").Append(segLength.ToString("F6", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(segmentUrl(i)).Append('\n');
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }
}
