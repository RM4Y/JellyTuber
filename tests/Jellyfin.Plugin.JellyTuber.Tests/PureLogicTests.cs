using Jellyfin.Plugin.JellyTuber.Services;
using Jellyfin.Plugin.JellyTuber.YouTube;
using Xunit;

namespace Jellyfin.Plugin.JellyTuber.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData("..", "fb")]
    [InlineData(".", "fb")]
    [InlineData("...", "fb")]
    [InlineData("", "fb")]
    [InlineData("   ", "fb")]
    [InlineData(null, "fb")]
    [InlineData("a/b", "a_b")]
    [InlineData("a\\b", "a_b")]
    [InlineData("Titre: sous-titre", "Titre_ sous-titre")]
    [InlineData("..foo", "..foo")]
    public void Segment_never_escapes_its_folder(string? input, string expected)
    {
        Assert.Equal(expected, PathSafety.Segment(input, "fb"));
    }

    [Theory]
    [InlineData("/lib/user/chan", "/lib/user", true)]
    [InlineData("/lib/user/chan/", "/lib/user/", true)]
    [InlineData("/lib/user", "/lib/user", false)]
    [InlineData("/lib/user/..", "/lib/user", false)]
    [InlineData("/lib/user2", "/lib/user", false)]
    [InlineData("/lib", "/lib/user", false)]
    public void IsStrictlyInside(string child, string parent, bool expected)
    {
        Assert.Equal(expected, PathSafety.IsStrictlyInside(child, parent));
    }
}

public class VideoIdTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("a-b_c123XYZ", true)]
    [InlineData("..", false)]
    [InlineData("dQw4w9WgXc", false)]
    [InlineData("dQw4w9WgXcQQ", false)]
    [InlineData("dQw4w9W/XcQ", false)]
    [InlineData(null, false)]
    public void IsValidId(string? id, bool expected)
    {
        Assert.Equal(expected, KnownVideos.IsValidId(id));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://example.com/", null)]
    public void ExtractVideoId(string url, string? expected)
    {
        Assert.Equal(expected, YouTubeApiClient.ExtractVideoId(url));
    }
}

public class NfoEscapeTests
{
    [Fact]
    public void Escapes_markup_and_drops_invalid_xml_chars_but_keeps_emoji()
    {
        Assert.Equal("a &amp; b &lt;i&gt; c😀", LibraryWriter.Esc("a & b <i>\u0008 c😀"));
    }
}

public class HlsTests
{
    [Fact]
    public void Playlist_covers_duration_with_short_last_segment()
    {
        var playlist = OnDemandHlsPackager.BuildPlaylist(5, i => $"s{i}.ts");
        Assert.Contains("#EXTINF:2.000000,\ns0.ts", playlist);
        Assert.Contains("#EXTINF:1.000000,\ns2.ts", playlist);
        Assert.DoesNotContain("s3.ts", playlist);
        Assert.EndsWith("#EXT-X-ENDLIST\n", playlist);
    }

    [Fact]
    public void Rewriter_proxies_segments_and_every_uri_attribute()
    {
        var master = "#EXTM3U\n"
            + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",URI=\"audio/index.m3u8\"\n"
            + "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=1,URI=\"https://x.googlevideo.com/if.m3u8\"\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2\n"
            + "video/index.m3u8\n";

        var result = HlsPlaylistRewriter.Rewrite(master, new Uri("https://x.googlevideo.com/master/m.m3u8"), u => "P(" + u + ")");

        Assert.Contains("URI=\"P(https://x.googlevideo.com/master/audio/index.m3u8)\"", result);
        Assert.Contains("URI=\"P(https://x.googlevideo.com/if.m3u8)\"", result);
        Assert.Contains("\nP(https://x.googlevideo.com/master/video/index.m3u8)", result);
        Assert.DoesNotContain("\"audio/index.m3u8\"", result);
    }
}
