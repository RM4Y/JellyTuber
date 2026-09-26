using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Rewrites an HLS (m3u8) playlist so every segment / init-segment URI routes
/// back through our own proxy endpoint instead of pointing straight at
/// YouTube's CDN.
///
/// This matters because googlevideo.com URLs are bound to the IP that
/// resolved them (via yt-dlp, on the Jellyfin server). Handing a raw
/// manifest to the playing client means the client's own device fetches
/// those segment URLs directly - which fails with 403s the moment the client
/// is on a different network/IP than the server (the common case for
/// anything but "phone on the same LAN as the server"). Routing every
/// segment fetch back through our proxy keeps the actual CDN request
/// originating from the server, matching the IP yt-dlp resolved against, no
/// matter which client or network is playing.
/// </summary>
internal static class HlsPlaylistRewriter
{
    private static readonly Regex UriAttribute = new("URI=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="playlist"/> with every URI line (and every
    /// tag's URI="..." attribute) replaced by
    /// <paramref name="proxyUrl"/>(absolute original URI). Relative URIs are
    /// resolved against <paramref name="baseUri"/> (the manifest's own URL)
    /// first.
    /// </summary>
    public static string Rewrite(string playlist, Uri baseUri, Func<string, string> proxyUrl)
    {
        var lines = playlist.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                // Any tag with a URI attribute: EXT-X-MAP/KEY in media
                // playlists, but also EXT-X-MEDIA (alternate audio renditions)
                // and EXT-X-I-FRAME-STREAM-INF in a master playlist - left
                // pointing at googlevideo, a remote client gets a 403 on them.
                if (line.Contains("URI=\"", StringComparison.Ordinal))
                {
                    lines[i] = UriAttribute.Replace(
                        line,
                        m => "URI=\"" + proxyUrl(Resolve(baseUri, m.Groups[1].Value)) + "\"");
                }

                continue;
            }

            lines[i] = proxyUrl(Resolve(baseUri, line));
        }

        return string.Join('\n', lines);
    }

    private static string Resolve(Uri baseUri, string uriOrRelative) =>
        Uri.TryCreate(uriOrRelative, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(baseUri, uriOrRelative).ToString();
}
