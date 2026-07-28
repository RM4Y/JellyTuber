using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTuber.Configuration;
using Jellyfin.Plugin.JellyTuber.ScheduledTasks;
using Jellyfin.Plugin.JellyTuber.Services;
using Jellyfin.Plugin.JellyTuber.YouTube;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Api;

/// <summary>
/// Self-service endpoints for Jellyfin users to manage their own YouTube
/// channels, plus the HTML page that drives them.
/// </summary>
[ApiController]
public class UserController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, (DateTime When, List<ChannelResult> Results)> SearchCache = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<UserController> _logger;

    public UserController(IHttpClientFactory httpClientFactory, ITaskManager taskManager, ILogger<UserController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>The self-service web page (users log in client-side with Jellyfin creds).</summary>
    [HttpGet("JellyTuber")]
    [AllowAnonymous]
    public ContentResult App() => new()
    {
        ContentType = "text/html; charset=utf-8",
        Content = PageHtml.Html
    };

    /// <summary>
    /// Home-screen icon for the self-service page. iOS Safari's "Add to Home
    /// Screen" specifically looks for an apple-touch-icon link (see the
    /// &lt;link&gt; tag in <see cref="PageHtml"/>) rather than falling back to
    /// the regular favicon - without one, iOS just screenshots the page
    /// instead. Served as an actual cacheable file (not inlined as a data
    /// URI like the favicon) since apple-touch-icon support for data URIs is
    /// inconsistent across iOS versions, and inlining would otherwise add
    /// ~55KB of duplicate base64 to every single page load.
    /// </summary>
    [HttpGet("JellyTuber/apple-touch-icon.png")]
    [AllowAnonymous]
    public ActionResult AppleTouchIcon()
    {
        var resourceName = $"{typeof(Plugin).Namespace}.Configuration.AppleTouchIcon.png";
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=2592000";
        return File(stream, "image/png");
    }

    [HttpPost("JellyTuber/User/Search")]
    [Authorize]
    public async Task<ActionResult> Search([FromBody] SearchRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            return BadRequest("The administrator has not set a YouTube API key.");
        }

        var q = (req.Query ?? string.Empty).Trim();
        if (q.Length < 2)
        {
            return new JsonResult(new List<ChannelResult>());
        }

        if (SearchCache.TryGetValue(q, out var cached) && (DateTime.UtcNow - cached.When).TotalMinutes < 30)
        {
            return new JsonResult(cached.Results);
        }

        try
        {
            var api = new YouTubeApiClient(_httpClientFactory.CreateClient(), cfg.ApiKey, _logger);
            var limit = cfg.SearchResultsLimit > 0 ? cfg.SearchResultsLimit : 20;
            var hits = await api.SearchChannelsAsync(q, limit, HttpContext.RequestAborted).ConfigureAwait(false);
            var results = hits.Select(h => new ChannelResult
            {
                ChannelId = h.ChannelId,
                Name = h.Title,
                Thumbnail = h.Thumbnail
            }).ToList();

            SearchCache[q] = (DateTime.UtcNow, results);
            return new JsonResult(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Channel search failed for query '{Query}'", q);
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("JellyTuber/User/Channels")]
    [Authorize]
    public ActionResult Channels([FromQuery] string userId)
    {
        var cfg = Plugin.Instance!.Configuration;
        var mine = cfg.UserChannels
            .Where(c => c.UserId == userId)
            .Select(c => new { c.ChannelId, c.Name, c.ExcludeShorts, c.Thumbnail })
            .ToList();
        return new JsonResult(mine);
    }

    [HttpPost("JellyTuber/User/Add")]
    [Authorize]
    public ActionResult Add([FromBody] AddRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.ChannelId))
        {
            return BadRequest();
        }

        lock (Plugin.ConfigLock)
        {
            if (cfg.UserChannels.Any(c => c.UserId == req.UserId && c.ChannelId == req.ChannelId))
            {
                return new JsonResult(new { status = "exists" });
            }

            cfg.UserChannels.Add(new UserChannel
            {
                UserId = req.UserId,
                UserName = req.UserName ?? "user",
                ChannelId = req.ChannelId,
                Name = req.Name ?? req.ChannelId,
                Url = $"https://www.youtube.com/channel/{req.ChannelId}",
                ExcludeShorts = true,
                Thumbnail = req.Thumbnail ?? string.Empty
            });
            Plugin.Instance.Save();
        }

        return new JsonResult(new { status = "added" });
    }

    [HttpPost("JellyTuber/User/Remove")]
    [Authorize]
    public ActionResult Remove([FromBody] ChannelRef req)
    {
        var cfg = Plugin.Instance!.Configuration;
        int removed;
        lock (Plugin.ConfigLock)
        {
            removed = cfg.UserChannels.RemoveAll(c => c.UserId == req.UserId && c.ChannelId == req.ChannelId);
            if (removed > 0)
            {
                Plugin.Instance.Save();
            }
        }

        return new JsonResult(new { removed });
    }

    [HttpPost("JellyTuber/User/ToggleShorts")]
    [Authorize]
    public ActionResult ToggleShorts([FromBody] ToggleRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        lock (Plugin.ConfigLock)
        {
            var entry = cfg.UserChannels.FirstOrDefault(c => c.UserId == req.UserId && c.ChannelId == req.ChannelId);
            if (entry is null)
            {
                return NotFound();
            }

            entry.ExcludeShorts = req.ExcludeShorts;
            Plugin.Instance.Save();
        }

        // Re-sync so the change is reflected on disk/in the library: enabling
        // exclusion deletes existing Shorts, disabling it brings them back.
        _taskManager.CancelIfRunningAndQueue<YouTubeSyncTask>();
        return new JsonResult(new { status = "ok" });
    }

    /// <summary>Queue the "Sync YouTube (Fast)" scheduled task.</summary>
    [HttpPost("JellyTuber/User/Sync")]
    [Authorize]
    public ActionResult Sync()
    {
        _taskManager.CancelIfRunningAndQueue<YouTubeSyncTask>();
        return new JsonResult(new { status = "queued" });
    }

    // ---- Individual videos (added by URL) ----

    [HttpGet("JellyTuber/User/Videos")]
    [Authorize]
    public ActionResult Videos([FromQuery] string userId)
    {
        var cfg = Plugin.Instance!.Configuration;
        var mine = cfg.UserVideos
            .Where(v => v.UserId == userId)
            .Select(v => new { v.VideoId, v.Title, v.Thumbnail, v.Url })
            .ToList();
        return new JsonResult(mine);
    }

    [HttpPost("JellyTuber/User/AddVideo")]
    [Authorize]
    public async Task<ActionResult> AddVideo([FromBody] AddVideoRequest req)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            return BadRequest("The administrator has not set a YouTube API key.");
        }

        if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Url))
        {
            return BadRequest();
        }

        var videoId = YouTubeApiClient.ExtractVideoId(req.Url);
        if (string.IsNullOrEmpty(videoId))
        {
            return BadRequest("Lien vidéo YouTube invalide.");
        }

        lock (Plugin.ConfigLock)
        {
            if (cfg.UserVideos.Any(v => v.UserId == req.UserId && v.VideoId == videoId))
            {
                return new JsonResult(new { status = "exists" });
            }
        }

        // The page is "YouTube without Shorts": refuse Shorts here too. A
        // /shorts/ URL is a dead giveaway; otherwise probe to be sure.
        try
        {
            var isShort = req.Url.Contains("/shorts/", StringComparison.OrdinalIgnoreCase);
            if (!isShort)
            {
                var detector = new ShortsDetector(cfg.YtDlpPath, _httpClientFactory.CreateClient(), _logger);
                isShort = await detector.IsShortAsync(videoId, HttpContext.RequestAborted).ConfigureAwait(false);
            }

            if (isShort)
            {
                return new JsonResult(new { status = "short" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Shorts check failed for added video {Id}; allowing it.", videoId);
        }

        try
        {
            var api = new YouTubeApiClient(_httpClientFactory.CreateClient(), cfg.ApiKey, _logger);
            var meta = await api.GetVideoAsync(videoId, HttpContext.RequestAborted).ConfigureAwait(false);
            if (meta is null)
            {
                return BadRequest("Vidéo introuvable.");
            }

            var entry = new UserVideo
            {
                UserId = req.UserId,
                UserName = req.UserName ?? "user",
                VideoId = videoId,
                Title = meta.Title,
                Url = $"https://www.youtube.com/watch?v={videoId}",
                Thumbnail = meta.ThumbnailUrl,
                Description = meta.Description,
                PublishedAt = meta.PublishedAt
            };

            lock (Plugin.ConfigLock)
            {
                if (cfg.UserVideos.Any(v => v.UserId == req.UserId && v.VideoId == videoId))
                {
                    return new JsonResult(new { status = "exists" });
                }

                cfg.UserVideos.Add(entry);
                Plugin.Instance.Save();
            }

            return new JsonResult(new { status = "added", videoId, title = entry.Title, thumbnail = entry.Thumbnail });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add video failed for URL '{Url}'", req.Url);
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("JellyTuber/User/RemoveVideo")]
    [Authorize]
    public ActionResult RemoveVideo([FromBody] VideoRef req)
    {
        var cfg = Plugin.Instance!.Configuration;
        int removed;
        lock (Plugin.ConfigLock)
        {
            removed = cfg.UserVideos.RemoveAll(v => v.UserId == req.UserId && v.VideoId == req.VideoId);
            if (removed > 0)
            {
                Plugin.Instance.Save();
            }
        }

        return new JsonResult(new { removed });
    }

    public class SearchRequest
    {
        public string? Query { get; set; }
    }

    public class ChannelResult
    {
        public string ChannelId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Thumbnail { get; set; } = string.Empty;
    }

    public class AddRequest
    {
        public string UserId { get; set; } = string.Empty;
        public string? UserName { get; set; }
        public string ChannelId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Thumbnail { get; set; }
    }

    public class ChannelRef
    {
        public string UserId { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
    }

    public class ToggleRequest
    {
        public string UserId { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public bool ExcludeShorts { get; set; }
    }

    public class AddVideoRequest
    {
        public string UserId { get; set; } = string.Empty;
        public string? UserName { get; set; }
        public string Url { get; set; } = string.Empty;
    }

    public class VideoRef
    {
        public string UserId { get; set; } = string.Empty;
        public string VideoId { get; set; } = string.Empty;
    }
}
