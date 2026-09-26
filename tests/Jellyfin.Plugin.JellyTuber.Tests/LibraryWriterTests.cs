using Jellyfin.Plugin.JellyTuber.Configuration;
using Jellyfin.Plugin.JellyTuber.Services;
using Jellyfin.Plugin.JellyTuber.YouTube;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyTuber.Tests;

public sealed class LibraryWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jt-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryWriter _writer = new(new HttpClient(), NullLogger.Instance);
    private readonly SourceItem _series = new() { Mode = "Series", Name = "Chan" };

    public LibraryWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static YouTubeVideo Video(string id, string title, int day) =>
        new() { VideoId = id, Title = title, PublishedAt = new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc) };

    private Task<bool> Write(string channelRoot, YouTubeVideo v, int episode, IDictionary<string, string>? index = null) =>
        _writer.WriteVideoAsync(channelRoot, _series, v, episode, "http://x/JellyTuber/Stream/" + v.VideoId, index ?? _writer.BuildVideoIndex(channelRoot), CancellationToken.None);

    [Fact]
    public async Task Two_videos_with_the_same_title_get_two_folders()
    {
        var chan = Path.Combine(_root, "Chan");
        Assert.True(await Write(chan, Video("AAAAAAAAAAA", "Live", 1), 1));
        Assert.True(await Write(chan, Video("BBBBBBBBBBB", "Live", 2), 2));

        Assert.True(File.Exists(Path.Combine(chan, "Live", "Live.strm")));
        Assert.True(File.Exists(Path.Combine(chan, "Live [BBBBBBBBBBB]", "Live [BBBBBBBBBBB].strm")));

        // A second sync is a no-op for both, and neither gets renamed.
        Assert.False(await Write(chan, Video("AAAAAAAAAAA", "Live", 1), 1));
        Assert.False(await Write(chan, Video("BBBBBBBBBBB", "Live", 2), 2));
        Assert.Equal(2, Directory.GetDirectories(chan).Length);
    }

    [Fact]
    public async Task Existing_nfo_follows_episode_shift()
    {
        var chan = Path.Combine(_root, "Chan");
        await Write(chan, Video("AAAAAAAAAAA", "One", 1), 2);
        await Write(chan, Video("AAAAAAAAAAA", "One", 1), 1);
        Assert.Contains("<episode>1</episode>", File.ReadAllText(Path.Combine(chan, "One", "One.nfo")));
    }

    [Fact]
    public async Task Removing_a_short_never_deletes_another_video_sharing_its_title()
    {
        var chan = Path.Combine(_root, "Chan");
        await Write(chan, Video("AAAAAAAAAAA", "Same", 1), 1);
        Assert.False(_writer.RemoveVideoIfExists(chan, Video("BBBBBBBBBBB", "Same", 2)));
        Assert.True(Directory.Exists(Path.Combine(chan, "Same")));
        Assert.True(_writer.RemoveVideoIfExists(chan, Video("AAAAAAAAAAA", "Same", 1)));
        Assert.False(Directory.Exists(Path.Combine(chan, "Same")));
    }

    [Fact]
    public async Task Retention_keeps_the_newest_n()
    {
        var chan = Path.Combine(_root, "Chan");
        for (var d = 1; d <= 5; d++)
        {
            await Write(chan, Video(new string((char)('A' + d), 11), "V" + d, d), d);
        }

        _writer.CleanupExcessVideos(chan, 2);
        Assert.Equal(new[] { "V4", "V5" }, Directory.GetDirectories(chan).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_channel_marker_at_the_library_root_never_deletes_the_library()
    {
        // What a ".." channel name used to leave behind.
        await _writer.WriteChannelRootAsync(_root, "evil", "", null, true, CancellationToken.None);
        var user = Path.Combine(_root, "user", "Chan");
        await Write(user, Video("AAAAAAAAAAA", "Keep me", 1), 1);
        await _writer.WriteChannelRootAsync(user, "Chan", "", null, true, CancellationToken.None);

        _writer.CleanupRemovedChannels(new[] { user }, new[] { _root });

        Assert.True(File.Exists(Path.Combine(user, "Keep me", "Keep me.strm")));

        // A channel that really was removed still goes.
        _writer.CleanupRemovedChannels(Array.Empty<string>(), new[] { _root });
        Assert.False(Directory.Exists(user));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task Window_cleanup_removes_gone_videos_but_keeps_older_ones_outside_the_window()
    {
        var chan = Path.Combine(_root, "Chan");
        await Write(chan, Video("OLDOLDOLDOL", "Old but valid", 1), 1);   // before the window
        await Write(chan, Video("GONEGONEGON", "Deleted on YouTube", 10), 2);
        await Write(chan, Video("KEEPKEEPKEE", "Kept", 12), 3);

        var keep = new HashSet<string> { "KEEPKEEPKEE" };
        _writer.CleanupRemovedVideos(chan, keep, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new[] { "Kept", "Old but valid" }, Directory.GetDirectories(chan).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Dot_titles_stay_inside_the_channel_folder()
    {
        var chan = Path.Combine(_root, "Chan");
        await Write(chan, Video("AAAAAAAAAAA", "..", 1), 1);
        Assert.True(File.Exists(Path.Combine(chan, "video", "video.strm")));
        Assert.Empty(Directory.GetFiles(_root));
    }
}
