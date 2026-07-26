using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTuber.YouTube;

/// <summary>A single video resolved from the API, normalised for our writer.</summary>
public class YouTubeVideo
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>ISO-8601 duration string (e.g. "PT12M3S"), when details are fetched.</summary>
    public string? Duration { get; set; }

    /// <summary>
    /// Parsed duration in seconds, or null if unknown. Used to detect Shorts.
    /// </summary>
    public int? DurationSeconds()
    {
        if (string.IsNullOrEmpty(Duration))
        {
            return null;
        }

        try
        {
            return (int)System.Xml.XmlConvert.ToTimeSpan(Duration).TotalSeconds;
        }
        catch (System.FormatException)
        {
            return null;
        }
    }
}

/// <summary>Channel-level metadata.</summary>
public class YouTubeChannel
{
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string UploadsPlaylistId { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
}

// ---- Raw API response shapes (only the fields we use) ----

internal class ChannelListResponse
{
    [JsonPropertyName("items")]
    public List<ChannelResource>? Items { get; set; }
}

internal class ChannelResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("snippet")]
    public ChannelSnippet? Snippet { get; set; }

    [JsonPropertyName("contentDetails")]
    public ChannelContentDetails? ContentDetails { get; set; }
}

internal class ChannelSnippet
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("thumbnails")]
    public ThumbnailSet? Thumbnails { get; set; }
}

internal class ChannelContentDetails
{
    [JsonPropertyName("relatedPlaylists")]
    public RelatedPlaylists? RelatedPlaylists { get; set; }
}

internal class RelatedPlaylists
{
    [JsonPropertyName("uploads")]
    public string? Uploads { get; set; }
}

internal class PlaylistItemsResponse
{
    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("items")]
    public List<PlaylistItem>? Items { get; set; }
}

internal class PlaylistItem
{
    [JsonPropertyName("snippet")]
    public PlaylistItemSnippet? Snippet { get; set; }

    [JsonPropertyName("contentDetails")]
    public PlaylistItemContentDetails? ContentDetails { get; set; }
}

internal class PlaylistItemSnippet
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("thumbnails")]
    public ThumbnailSet? Thumbnails { get; set; }
}

internal class PlaylistItemContentDetails
{
    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("videoPublishedAt")]
    public DateTime? VideoPublishedAt { get; set; }
}

internal class VideosResponse
{
    [JsonPropertyName("items")]
    public List<VideoResource>? Items { get; set; }
}

internal class VideoResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("snippet")]
    public VideoSnippet? Snippet { get; set; }

    [JsonPropertyName("contentDetails")]
    public VideoContentDetails? ContentDetails { get; set; }
}

internal class VideoSnippet
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("channelTitle")]
    public string? ChannelTitle { get; set; }

    [JsonPropertyName("thumbnails")]
    public ThumbnailSet? Thumbnails { get; set; }
}

internal class VideoContentDetails
{
    [JsonPropertyName("duration")]
    public string? Duration { get; set; }
}

internal class ThumbnailSet
{
    [JsonPropertyName("maxres")]
    public Thumbnail? Maxres { get; set; }

    [JsonPropertyName("standard")]
    public Thumbnail? Standard { get; set; }

    [JsonPropertyName("high")]
    public Thumbnail? High { get; set; }

    [JsonPropertyName("medium")]
    public Thumbnail? Medium { get; set; }

    [JsonPropertyName("default")]
    public Thumbnail? Default { get; set; }

    /// <summary>Best available thumbnail URL.</summary>
    public string? Best() =>
        Maxres?.Url ?? Standard?.Url ?? High?.Url ?? Medium?.Url ?? Default?.Url;
}

internal class Thumbnail
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal class SearchResponse
{
    [JsonPropertyName("items")]
    public List<SearchItem>? Items { get; set; }
}

internal class SearchItem
{
    [JsonPropertyName("id")]
    public SearchItemId? Id { get; set; }

    [JsonPropertyName("snippet")]
    public SearchItemSnippet? Snippet { get; set; }
}

internal class SearchItemId
{
    [JsonPropertyName("channelId")]
    public string? ChannelId { get; set; }
}

internal class SearchItemSnippet
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("channelId")]
    public string? ChannelId { get; set; }

    [JsonPropertyName("thumbnails")]
    public ThumbnailSet? Thumbnails { get; set; }
}
