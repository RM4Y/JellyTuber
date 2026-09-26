using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTuber.Configuration;
using Jellyfin.Plugin.JellyTuber.ScheduledTasks;
using Jellyfin.Plugin.JellyTuber.Services;
using Jellyfin.Plugin.JellyTuber.YouTube;
using MediaBrowser.Controller.Library;
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
    private readonly IUserManager _userManager;
    private readonly ILogger<UserController> _logger;

    public UserController(IHttpClientFactory httpClientFactory, ITaskManager taskManager, IUserManager userManager, ILogger<UserController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _taskManager = taskManager;
        _userManager = userManager;
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
    public ActionResult AppleTouchIcon() => EmbeddedPng("AppleTouchIcon.png");

    /// <summary>
    /// PWA icons referenced by <see cref="Manifest"/>. Full-bleed so they
    /// also work as "maskable" (Android crops them to the launcher shape).
    /// </summary>
    [HttpGet("JellyTuber/icon-{size:int}.png")]
    [AllowAnonymous]
    public ActionResult Icon([FromRoute] int size) => size switch
    {
        192 => EmbeddedPng("Icon192.png"),
        512 => EmbeddedPng("Icon512.png"),
        _ => NotFound()
    };

    /// <summary>
    /// Web app manifest. Once the page is installed as an app (Android
    /// Chrome: menu > "Installer l'application"), the share_target entry
    /// makes "JellyTuber" appear in the system share sheet: sharing a
    /// YouTube video opens /JellyTuber?title=..&amp;text=..&amp;url=.., and
    /// the page adds it automatically. Scope is "/JellyTuber" (no trailing
    /// slash) so it covers the page itself, which lives at /JellyTuber.
    /// </summary>
    [HttpGet("JellyTuber/manifest.webmanifest")]
    [AllowAnonymous]
    public ContentResult Manifest() => new()
    {
        ContentType = "application/manifest+json; charset=utf-8",
        Content = """
{
  "id": "/JellyTuber",
  "name": "JellyTuber",
  "short_name": "JellyTuber",
  "start_url": "/JellyTuber",
  "scope": "/JellyTuber",
  "display": "standalone",
  "background_color": "#0c1116",
  "theme_color": "#0c1116",
  "icons": [
    { "src": "/JellyTuber/icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any" },
    { "src": "/JellyTuber/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any" },
    { "src": "/JellyTuber/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ],
  "share_target": {
    "action": "/JellyTuber",
    "method": "GET",
    "params": { "title": "title", "text": "text", "url": "url" }
  }
}
"""
    };

    /// <summary>
    /// Minimal service worker - Chrome only offers to install a page as an
    /// app (a prerequisite for share_target) when one with a fetch handler
    /// controls it. It just passes navigations through to the network.
    /// Served from /JellyTuber/ but registered with scope "/JellyTuber",
    /// which is one level above its own directory, hence the
    /// Service-Worker-Allowed header.
    /// </summary>
    [HttpGet("JellyTuber/sw.js")]
    [AllowAnonymous]
    public ContentResult ServiceWorker()
    {
        Response.Headers["Service-Worker-Allowed"] = "/JellyTuber";
        Response.Headers.CacheControl = "no-cache";
        return new ContentResult
        {
            ContentType = "text/javascript; charset=utf-8",
            Content = """
self.addEventListener("install", function () { self.skipWaiting(); });
self.addEventListener("activate", function (e) { e.waitUntil(self.clients.claim()); });
self.addEventListener("fetch", function (e) {
  if (e.request.mode === "navigate") { e.respondWith(fetch(e.request)); }
});
"""
        };
    }

    private ActionResult EmbeddedPng(string fileName)
    {
        var resourceName = $"{typeof(Plugin).Namespace}.Configuration.{fileName}";
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
            if (SearchCache.Count > 200)
            {
                foreach (var kvp in SearchCache)
                {
                    if ((DateTime.UtcNow - kvp.Value.When).TotalMinutes >= 30)
                    {
                        SearchCache.TryRemove(kvp.Key, out _);
                    }
                }
            }
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
    public ActionResult Channels()
    {
        var userId = SessionUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        lock (Plugin.ConfigLock)
        {
            var mine = cfg.UserChannels
                .Where(c => IsUser(c.UserId, userId))
                .Select(c => new { c.ChannelId, c.Name, c.ExcludeShorts, c.Thumbnail, c.MaxVideos })
                .ToList();
            return new JsonResult(mine);
        }
    }

    [HttpPost("JellyTuber/User/Add")]
    [Authorize]
    public ActionResult Add([FromBody] AddRequest req)
    {
        var user = SessionUser();
        if (user is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(req.ChannelId))
        {
            return BadRequest();
        }

        lock (Plugin.ConfigLock)
        {
            if (cfg.UserChannels.Any(c => IsUser(c.UserId, user.Value.Id) && c.ChannelId == req.ChannelId))
            {
                return new JsonResult(new { status = "exists" });
            }

            cfg.UserChannels.Add(new UserChannel
            {
                UserId = user.Value.Id,
                UserName = user.Value.Name,
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
        var userId = SessionUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        int removed;
        lock (Plugin.ConfigLock)
        {
            removed = cfg.UserChannels.RemoveAll(c => IsUser(c.UserId, userId) && c.ChannelId == req.ChannelId);
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
        var userId = SessionUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        lock (Plugin.ConfigLock)
        {
            var entry = cfg.UserChannels.FirstOrDefault(c => IsUser(c.UserId, userId) && c.ChannelId == req.ChannelId);
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

    [HttpPost("JellyTuber/User/SetMaxVideos")]
    [Authorize]
    public ActionResult SetMaxVideos([FromBody] SetMaxVideosRequest req)
    {
        var userId = SessionUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        var maxVideos = Math.Clamp(req.MaxVideos, 5, 50);
        lock (Plugin.ConfigLock)
        {
            var entry = cfg.UserChannels.FirstOrDefault(c => IsUser(c.UserId, userId) && c.ChannelId == req.ChannelId);
            if (entry is null)
            {
                return NotFound();
            }

            entry.MaxVideos = maxVideos;
            Plugin.Instance.Save();
        }

        // Re-sync so raising/lowering the count is reflected on disk right away.
        _taskManager.CancelIfRunningAndQueue<YouTubeSyncTask>();
        return new JsonResult(new { status = "ok", maxVideos });
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
    public ActionResult Videos()
    {
        var userId = SessionUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        lock (Plugin.ConfigLock)
        {
            var mine = cfg.UserVideos
                .Where(v => IsUser(v.UserId, userId))
                .Select(v => new { v.VideoId, v.Title, v.Thumbnail, v.Url })
                .ToList();
            return new JsonResult(mine);
        }
    }

    [HttpPost("JellyTuber/User/AddVideo")]
    [Authorize]
    public async Task<ActionResult> AddVideo([FromBody] AddVideoRequest req)
    {
        var user = SessionUser();
        if (user is null)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(req.Url))
        {
            return BadRequest();
        }

        var result = await AddVideoCoreAsync(user.Value.Id, user.Value.Name, req.Url, HttpContext.RequestAborted).ConfigureAwait(false);
        return result.Status switch
        {
            "error" => BadRequest(result.Error),
            "failed" => StatusCode(500, result.Error),
            _ => new JsonResult(new { status = result.Status, videoId = result.VideoId, title = result.Title, thumbnail = result.Thumbnail })
        };
    }

    /// <summary>
    /// The current user's secret share code, created on first request. It
    /// lets /JellyTuber/Share add videos to this account without a session,
    /// so the user is taken from the session token here, never the request.
    /// </summary>
    [HttpGet("JellyTuber/User/ShareCode")]
    [Authorize]
    public ActionResult GetShareCode()
    {
        var userId = SessionUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        lock (Plugin.ConfigLock)
        {
            var entry = cfg.ShareCodes.FirstOrDefault(c => c.UserId == userId);
            if (entry is null)
            {
                entry = new UserShareCode { UserId = userId, Code = NewShareCode() };
                cfg.ShareCodes.Add(entry);
                Plugin.Instance.Save();
            }

            return new JsonResult(new { code = entry.Code });
        }
    }

    /// <summary>Replace the current user's share code, revoking the old one.</summary>
    [HttpPost("JellyTuber/User/ShareCode/Regenerate")]
    [Authorize]
    public ActionResult RegenerateShareCode()
    {
        var userId = SessionUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        var code = NewShareCode();
        lock (Plugin.ConfigLock)
        {
            cfg.ShareCodes.RemoveAll(c => c.UserId == userId);
            cfg.ShareCodes.Add(new UserShareCode { UserId = userId, Code = code });
            Plugin.Instance.Save();
        }

        return new JsonResult(new { code });
    }

    /// <summary>
    /// Session-less "add this video" link for the iOS Shortcut share flow:
    /// /JellyTuber/Share?code=SECRET&amp;url=https://youtu.be/... The code
    /// identifies the user; an unknown code is rejected before any YouTube
    /// call. Queues a sync on success and answers with a small HTML page.
    /// </summary>
    [HttpGet("JellyTuber/Share")]
    [AllowAnonymous]
    public async Task<ContentResult> Share([FromQuery] string? code, [FromQuery] string? url)
    {
        string? userId = null;
        if (!string.IsNullOrEmpty(code))
        {
            var given = Encoding.UTF8.GetBytes(code);
            lock (Plugin.ConfigLock)
            {
                userId = Plugin.Instance!.Configuration.ShareCodes
                    .FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c.Code), given))
                    ?.UserId;
            }
        }

        if (userId is null)
        {
            return SharePage(403, "Code invalide", "Ce lien de partage n'est pas (ou plus) valide. Copiez le nouveau depuis la page JellyTuber.");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return SharePage(400, "Aucun lien reçu", "Le raccourci n'a transmis aucun lien de vidéo.");
        }

        var userName = Guid.TryParse(userId, out var guid) ? _userManager.GetUserById(guid)?.Username : null;
        if (userName is null)
        {
            return SharePage(403, "Utilisateur introuvable", "Le compte lié à ce code n'existe plus.");
        }

        var result = await AddVideoCoreAsync(userId, userName, url, HttpContext.RequestAborted).ConfigureAwait(false);
        switch (result.Status)
        {
            case "added":
                _taskManager.CancelIfRunningAndQueue<YouTubeSyncTask>();
                return SharePage(200, "Vidéo ajoutée", (result.Title ?? string.Empty) + " — synchronisation lancée.");
            case "exists":
                return SharePage(200, "Déjà présente", "Cette vidéo est déjà dans votre bibliothèque.");
            case "short":
                return SharePage(200, "Short refusé", "C'est un Short — non ajouté (JellyTuber exclut les Shorts).");
            default:
                return SharePage(400, "Échec de l'ajout", result.Error ?? "Erreur inconnue.");
        }
    }

    private record AddVideoResult(string Status, string? Error = null, string? VideoId = null, string? Title = null, string? Thumbnail = null);

    /// <summary>
    /// Shared add-by-URL logic. Status is "added", "exists", "short",
    /// "error" (bad input, Error set) or "failed" (unexpected, Error set).
    /// </summary>
    private async Task<AddVideoResult> AddVideoCoreAsync(string userId, string userName, string url, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            return new AddVideoResult("error", "The administrator has not set a YouTube API key.");
        }

        var videoId = YouTubeApiClient.ExtractVideoId(url);
        if (string.IsNullOrEmpty(videoId))
        {
            return new AddVideoResult("error", "Lien vidéo YouTube invalide.");
        }

        lock (Plugin.ConfigLock)
        {
            if (cfg.UserVideos.Any(v => IsUser(v.UserId, userId) && v.VideoId == videoId))
            {
                return new AddVideoResult("exists");
            }
        }

        // The page is "YouTube without Shorts": refuse Shorts here too. A
        // /shorts/ URL is a dead giveaway; otherwise probe to be sure.
        try
        {
            var isShort = url.Contains("/shorts/", StringComparison.OrdinalIgnoreCase);
            if (!isShort)
            {
                var detector = new ShortsDetector(_httpClientFactory, _logger);
                isShort = await detector.IsShortAsync(videoId, ct).ConfigureAwait(false);
            }

            if (isShort)
            {
                return new AddVideoResult("short");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Shorts check failed for added video {Id}; allowing it.", videoId);
        }

        try
        {
            var api = new YouTubeApiClient(_httpClientFactory.CreateClient(), cfg.ApiKey, _logger);
            var meta = await api.GetVideoAsync(videoId, ct).ConfigureAwait(false);
            if (meta is null)
            {
                return new AddVideoResult("error", "Vidéo introuvable.");
            }

            var entry = new UserVideo
            {
                UserId = userId,
                UserName = userName,
                VideoId = videoId,
                Title = meta.Title,
                Url = $"https://www.youtube.com/watch?v={videoId}",
                Thumbnail = meta.ThumbnailUrl,
                Description = meta.Description,
                PublishedAt = meta.PublishedAt
            };

            lock (Plugin.ConfigLock)
            {
                if (cfg.UserVideos.Any(v => IsUser(v.UserId, userId) && v.VideoId == videoId))
                {
                    return new AddVideoResult("exists");
                }

                cfg.UserVideos.Add(entry);
                Plugin.Instance.Save();
            }

            return new AddVideoResult("added", VideoId: videoId, Title: entry.Title, Thumbnail: entry.Thumbnail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add video failed for URL '{Url}'", url);
            return new AddVideoResult("failed", ex.Message);
        }
    }

    /// <summary>
    /// The authenticated user's id from the Jellyfin session token, in the
    /// same "N" format the page gets from AuthenticateByName.
    /// </summary>
    private string? SessionUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out var id) && id != Guid.Empty ? id.ToString("N") : null;
    }

    /// <summary>
    /// The authenticated user's id and name, both from the server side - the
    /// name decides the on-disk folder their content is written to, so it's
    /// never taken from the request body.
    /// </summary>
    private (string Id, string Name)? SessionUser()
    {
        var userId = SessionUserId();
        if (userId is null)
        {
            return null;
        }

        var name = _userManager.GetUserById(Guid.ParseExact(userId, "N"))?.Username;
        return string.IsNullOrWhiteSpace(name) ? null : (userId, name);
    }

    /// <summary>Compares a stored user id (any Guid format) against a session user id in "N" format.</summary>
    private static bool IsUser(string storedUserId, string userId) =>
        Guid.TryParse(storedUserId, out var stored) && stored.ToString("N") == userId;

    private static string NewShareCode()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    private static ContentResult SharePage(int status, string title, string message) => new()
    {
        StatusCode = status,
        ContentType = "text/html; charset=utf-8",
        Content = $$"""
<!DOCTYPE html>
<html lang="fr"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
<title>JellyTuber</title>
<style>
  body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; padding:24px; box-sizing:border-box;
         background:#0c1116; color:#e9eff2; font-family:-apple-system, system-ui, sans-serif; }
  .card { max-width:420px; width:100%; background:#141b22; border:1px solid #243039; border-radius:20px; padding:28px; text-align:center; }
  h1 { margin:0 0 10px; font-size:22px; color:{{(status == 200 ? "#a793f4" : "#e0738a")}}; }
  p { margin:0; font-size:15px; line-height:1.45; color:#8aa0ab; }
</style></head>
<body><div class="card"><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p></div></body></html>
"""
    };

    [HttpPost("JellyTuber/User/RemoveVideo")]
    [Authorize]
    public ActionResult RemoveVideo([FromBody] VideoRef req)
    {
        var userId = SessionUserId();
        if (userId is null)
        {
            return Forbid();
        }

        var cfg = Plugin.Instance!.Configuration;
        int removed;
        lock (Plugin.ConfigLock)
        {
            removed = cfg.UserVideos.RemoveAll(v => IsUser(v.UserId, userId) && v.VideoId == req.VideoId);
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
        public string ChannelId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Thumbnail { get; set; }
    }

    public class ChannelRef
    {
        public string ChannelId { get; set; } = string.Empty;
    }

    public class ToggleRequest
    {
        public string ChannelId { get; set; } = string.Empty;
        public bool ExcludeShorts { get; set; }
    }

    public class SetMaxVideosRequest
    {
        public string ChannelId { get; set; } = string.Empty;
        public int MaxVideos { get; set; }
    }

    public class AddVideoRequest
    {
        public string Url { get; set; } = string.Empty;
    }

    public class VideoRef
    {
        public string VideoId { get; set; } = string.Empty;
    }
}
