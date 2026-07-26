using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.YouTube;

/// <summary>
/// Thin wrapper around the YouTube Data API v3.
///
/// Quota notes (free tier = 10,000 units/day):
///   channels.list      = 1 unit
///   playlistItems.list = 1 unit per page (50 videos)
///   videos.list        = 1 unit per page (50 videos)  [only if FetchVideoDetails]
///
/// We deliberately AVOID search.list (100 units) by deriving the uploads
/// playlist from the channel, which keeps daily cost tiny.
/// </summary>
public class YouTubeApiClient
{
    private const string ApiBase = "https://www.googleapis.com/youtube/v3";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger _logger;

    public YouTubeApiClient(HttpClient http, string apiKey, ILogger logger)
    {
        _http = http;
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <summary>
    /// Search channels by name (limit 1..50). NOTE: search.list costs 100 quota
    /// units per call, so results are cached briefly by the caller.
    /// Returns (channelId, title, thumbnailUrl) tuples.
    /// </summary>
    public async Task<List<(string ChannelId, string Title, string Thumbnail)>> SearchChannelsAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var results = new List<(string, string, string)>();
        var limit = Math.Clamp(maxResults, 1, 50);
        var url = $"{ApiBase}/search?part=snippet&type=channel&maxResults={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&q={Uri.EscapeDataString(query)}&key={_apiKey}";

        var resp = await _http.GetFromJsonAsync<SearchResponse>(url, ct).ConfigureAwait(false);
        if (resp?.Items is null)
        {
            return results;
        }

        foreach (var item in resp.Items)
        {
            var channelId = item.Id?.ChannelId ?? item.Snippet?.ChannelId;
            var title = item.Snippet?.Title;
            if (!string.IsNullOrEmpty(channelId) && !string.IsNullOrEmpty(title))
            {
                results.Add((channelId, title, item.Snippet?.Thumbnails?.Best() ?? string.Empty));
            }
        }

        return results;
    }

    /// <summary>
    /// Resolve a channel from any common URL form and return its metadata
    /// (including the uploads playlist used to enumerate videos).
    /// Returns null if it could not be resolved.
    /// </summary>
    public async Task<YouTubeChannel?> ResolveChannelAsync(string url, CancellationToken ct)
    {
        var (kind, value) = ParseChannelUrl(url);
        string query;

        switch (kind)
        {
            case "id":
                query = $"id={Uri.EscapeDataString(value)}";
                break;
            case "handle":
                query = $"forHandle={Uri.EscapeDataString(value)}";
                break;
            case "user":
                query = $"forUsername={Uri.EscapeDataString(value)}";
                break;
            default:
                _logger.LogWarning("Could not parse channel URL: {Url}", url);
                return null;
        }

        var requestUrl = $"{ApiBase}/channels?part=snippet,contentDetails&{query}&key={_apiKey}";
        var resp = await _http.GetFromJsonAsync<ChannelListResponse>(requestUrl, ct).ConfigureAwait(false);

        var item = resp?.Items is { Count: > 0 } ? resp.Items[0] : null;
        if (item is null)
        {
            _logger.LogWarning("Channel not found for URL: {Url}", url);
            return null;
        }

        return new YouTubeChannel
        {
            ChannelId = item.Id ?? string.Empty,
            Title = item.Snippet?.Title ?? string.Empty,
            Description = item.Snippet?.Description ?? string.Empty,
            UploadsPlaylistId = item.ContentDetails?.RelatedPlaylists?.Uploads ?? string.Empty,
            ThumbnailUrl = item.Snippet?.Thumbnails?.Best() ?? string.Empty
        };
    }

    /// <summary>
    /// Enumerate videos of a playlist (works for an uploads playlist or any
    /// regular playlist), newest first, stopping once videos fall outside the
    /// age window. <paramref name="cutoffUtc"/> = null means no age limit.
    /// </summary>
    public async Task<List<YouTubeVideo>> GetPlaylistVideosAsync(
        string playlistId,
        DateTime? cutoffUtc,
        CancellationToken ct)
    {
        var results = new List<YouTubeVideo>();
        string? pageToken = null;

        do
        {
            var url =
                $"{ApiBase}/playlistItems?part=snippet,contentDetails" +
                $"&maxResults=50&playlistId={Uri.EscapeDataString(playlistId)}&key={_apiKey}";
            if (!string.IsNullOrEmpty(pageToken))
            {
                url += $"&pageToken={pageToken}";
            }

            var page = await _http.GetFromJsonAsync<PlaylistItemsResponse>(url, ct).ConfigureAwait(false);
            if (page?.Items is null)
            {
                break;
            }

            var hitOldVideo = false;
            foreach (var item in page.Items)
            {
                var videoId = item.ContentDetails?.VideoId;
                if (string.IsNullOrEmpty(videoId))
                {
                    continue;
                }

                var published =
                    item.ContentDetails?.VideoPublishedAt
                    ?? item.Snippet?.PublishedAt
                    ?? DateTime.MinValue;

                if (cutoffUtc.HasValue && published < cutoffUtc.Value)
                {
                    // Uploads playlist is newest-first, so we can stop early.
                    hitOldVideo = true;
                    break;
                }

                results.Add(new YouTubeVideo
                {
                    VideoId = videoId,
                    Title = item.Snippet?.Title ?? videoId,
                    Description = item.Snippet?.Description ?? string.Empty,
                    PublishedAt = published.ToUniversalTime(),
                    ThumbnailUrl = item.Snippet?.Thumbnails?.Best() ?? string.Empty
                });
            }

            pageToken = hitOldVideo ? null : page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken) && !ct.IsCancellationRequested);

        return results;
    }

    /// <summary>
    /// Optionally enrich videos with content details (duration) in batches of 50.
    /// Mutates the passed list in place.
    /// </summary>
    public async Task EnrichWithDetailsAsync(List<YouTubeVideo> videos, CancellationToken ct)
    {
        for (var i = 0; i < videos.Count; i += 50)
        {
            var batch = videos.GetRange(i, Math.Min(50, videos.Count - i));
            var ids = string.Join(",", batch.ConvertAll(v => v.VideoId));
            var url = $"{ApiBase}/videos?part=contentDetails&id={ids}&key={_apiKey}";

            var resp = await _http.GetFromJsonAsync<VideosResponse>(url, ct).ConfigureAwait(false);
            if (resp?.Items is null)
            {
                continue;
            }

            var byId = new Dictionary<string, string?>();
            foreach (var r in resp.Items)
            {
                if (!string.IsNullOrEmpty(r.Id))
                {
                    byId[r.Id] = r.ContentDetails?.Duration;
                }
            }

            foreach (var v in batch)
            {
                if (byId.TryGetValue(v.VideoId, out var dur))
                {
                    v.Duration = dur;
                }
            }
        }
    }

    /// <summary>
    /// Fetch a single video's metadata (title, description, thumbnail, date)
    /// by id. Costs 1 quota unit (videos.list). Returns null if not found.
    /// </summary>
    public async Task<YouTubeVideo?> GetVideoAsync(string videoId, CancellationToken ct)
    {
        var url = $"{ApiBase}/videos?part=snippet,contentDetails&id={Uri.EscapeDataString(videoId)}&key={_apiKey}";
        var resp = await _http.GetFromJsonAsync<VideosResponse>(url, ct).ConfigureAwait(false);
        var item = resp?.Items is { Count: > 0 } ? resp.Items[0] : null;
        if (item is null || string.IsNullOrEmpty(item.Id))
        {
            return null;
        }

        return new YouTubeVideo
        {
            VideoId = item.Id,
            Title = item.Snippet?.Title ?? item.Id,
            Description = item.Snippet?.Description ?? string.Empty,
            PublishedAt = (item.Snippet?.PublishedAt ?? DateTime.UtcNow).ToUniversalTime(),
            ThumbnailUrl = item.Snippet?.Thumbnails?.Best() ?? string.Empty,
            Duration = item.ContentDetails?.Duration
        };
    }

    /// <summary>
    /// Extract an 11-char video id from any common YouTube URL form
    /// (watch?v=, youtu.be/, /shorts/, /embed/, /v/), or return the input
    /// if it already looks like a bare video id. Null if nothing matches.
    /// </summary>
    public static string? ExtractVideoId(string urlOrId)
    {
        if (string.IsNullOrWhiteSpace(urlOrId))
        {
            return null;
        }

        urlOrId = urlOrId.Trim();

        // Already a bare id?
        if (Regex.IsMatch(urlOrId, @"^[\w-]{11}$"))
        {
            return urlOrId;
        }

        // watch?v=ID (or any &v=ID)
        var vParam = Regex.Match(urlOrId, @"[?&]v=([\w-]{11})");
        if (vParam.Success)
        {
            return vParam.Groups[1].Value;
        }

        // youtu.be/ID, /shorts/ID, /embed/ID, /v/ID, /live/ID
        var pathId = Regex.Match(urlOrId, @"(?:youtu\.be/|/shorts/|/embed/|/v/|/live/)([\w-]{11})");
        if (pathId.Success)
        {
            return pathId.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Extract a playlist id from a playlist URL (?list=...), or return the
    /// raw value if it already looks like a playlist id.
    /// </summary>
    public static string? ExtractPlaylistId(string url)
    {
        if (url.StartsWith("PL", StringComparison.Ordinal) ||
            url.StartsWith("UU", StringComparison.Ordinal) ||
            url.StartsWith("FL", StringComparison.Ordinal) ||
            url.StartsWith("OL", StringComparison.Ordinal))
        {
            return url;
        }

        try
        {
            var uri = new Uri(url);
            var list = HttpUtility.ParseQueryString(uri.Query).Get("list");
            return string.IsNullOrEmpty(list) ? null : list;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns (kind, value) where kind is "id" | "handle" | "user" | "unknown".
    /// Handles /channel/UC..., /@handle, /user/name, and a bare UC.../@handle.
    /// </summary>
    private static (string Kind, string Value) ParseChannelUrl(string url)
    {
        url = url.Trim();

        if (url.StartsWith("UC", StringComparison.Ordinal) && url.Length >= 20 && !url.Contains('/'))
        {
            return ("id", url);
        }

        if (url.StartsWith("@", StringComparison.Ordinal))
        {
            return ("handle", url);
        }

        var channelMatch = Regex.Match(url, @"/channel/(UC[\w-]+)");
        if (channelMatch.Success)
        {
            return ("id", channelMatch.Groups[1].Value);
        }

        var handleMatch = Regex.Match(url, @"/(@[\w.\-]+)");
        if (handleMatch.Success)
        {
            return ("handle", handleMatch.Groups[1].Value);
        }

        var userMatch = Regex.Match(url, @"/user/([\w.\-]+)");
        if (userMatch.Success)
        {
            return ("user", userMatch.Groups[1].Value);
        }

        // /c/CustomName has no direct API lookup; user should paste the
        // /channel/UC... or @handle form instead.
        return ("unknown", string.Empty);
    }
}
