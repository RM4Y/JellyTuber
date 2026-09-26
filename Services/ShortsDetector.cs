using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Detects YouTube Shorts.
///
/// Primary signal (cheap, no yt-dlp): a regular video opened via its
/// /shorts/{id} URL is redirected by YouTube to /watch?v={id}, whereas a real
/// Short stays on /shorts/. We send YouTube's standard "consent already
/// given" cookie values (the same ones a browser stores after clicking
/// through the EU cookie banner) purely so the redirect is observable in a
/// single request; no restricted or non-public content is involved.
///
/// Fallback signal: a Short is always shot vertically (height &gt; width), read
/// from yt-dlp (which handles consent/region) when the HTTP probe is
/// inconclusive.
///
/// We only call this for videos short enough to plausibly be a Short (the
/// caller pre-filters by duration), and cache results in memory.
/// </summary>
public class ShortsDetector
{
    private static readonly ConcurrentDictionary<string, bool> Cache = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public ShortsDetector(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _http = httpClientFactory.CreateClient();
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the video is a Short. On any error, returns false (we
    /// prefer keeping a video over hiding real content by mistake).
    /// </summary>
    public async Task<bool> IsShortAsync(string videoId, CancellationToken ct)
    {
        if (Cache.TryGetValue(videoId, out var cached))
        {
            return cached;
        }

        // 1) Definitive, cheap HTTP probe.
        var http = await HttpProbeAsync(videoId, ct).ConfigureAwait(false);
        if (http.HasValue)
        {
            Cache[videoId] = http.Value;
            return http.Value;
        }

        // 2) Fallback: yt-dlp vertical-aspect probe. A failed/timed-out probe
        // keeps the video but isn't cached, so the next sync asks again
        // instead of treating a real Short as a regular video until restart.
        var result = await ProbeAsync(videoId, ct).ConfigureAwait(false);
        if (result is null)
        {
            return false;
        }

        Cache[videoId] = result.Value;
        return result.Value;
    }

    /// <summary>
    /// Returns true (Short), false (not a Short) or null (inconclusive) by
    /// checking whether /shorts/{id} stays on /shorts or redirects to /watch.
    /// </summary>
    private async Task<bool?> HttpProbeAsync(string videoId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.youtube.com/shorts/{videoId}");
            // Pre-accept the EU cookie-consent interstitial so the real
            // /shorts vs /watch redirect is observable in one request.
            req.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI; CONSENT=YES+1");
            req.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

            using var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            // Final URL after any redirects the HttpClient followed.
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

            if (finalUrl.Contains("/watch", StringComparison.OrdinalIgnoreCase))
            {
                return false; // redirected to the normal watch page -> not a Short
            }

            if (finalUrl.Contains("/shorts/", StringComparison.OrdinalIgnoreCase)
                && (int)resp.StatusCode is >= 200 and < 300)
            {
                return true; // stayed on /shorts -> Short
            }

            return null; // consent / sorry / unexpected -> let the fallback decide
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HTTP Shorts probe failed for {VideoId}; using fallback.", videoId);
            return null;
        }
    }

    /// <summary>
    /// Lists the video ids in a channel's dedicated Shorts tab via yt-dlp
    /// (flat, fast). This is the most reliable Shorts signal for a channel:
    /// the ids returned ARE Shorts. Returns an empty set on any error so the
    /// caller can fall back to per-video probing.
    /// </summary>
    public async Task<HashSet<string>> ListChannelShortIdsAsync(string channelId, int max, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return ids;
        }

        var ytDlpPath = await ExternalTools.EnsureYtDlpAsync(_httpClientFactory, _logger, ct).ConfigureAwait(false);
        if (ytDlpPath is null)
        {
            _logger.LogWarning("yt-dlp is not available (download failed); skipping channel Shorts listing for {ChannelId}.", channelId);
            return ids;
        }

        var psi = new ProcessStartInfo { FileName = ytDlpPath };
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--playlist-end");
        psi.ArgumentList.Add(Math.Max(1, max).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(id)s");
        using var cookieFile = CookieFile.Apply(psi, Plugin.Instance?.Configuration.YouTubeCookies, _logger);
        psi.ArgumentList.Add($"https://www.youtube.com/channel/{channelId}/shorts");

        try
        {
            var result = await ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (result is null)
            {
                _logger.LogWarning("Channel Shorts listing timed out for {ChannelId}.", channelId);
                return ids;
            }

            foreach (var line in result.Stdout.Split('\n'))
            {
                var id = line.Trim();
                if (id.Length == 11)
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Channel Shorts listing failed for {ChannelId}.", channelId);
        }

        return ids;
    }

    /// <summary>True/false from the video's aspect ratio, or null if yt-dlp failed or timed out (inconclusive).</summary>
    private async Task<bool?> ProbeAsync(string videoId, CancellationToken ct)
    {
        var ytDlpPath = await ExternalTools.EnsureYtDlpAsync(_httpClientFactory, _logger, ct).ConfigureAwait(false);
        if (ytDlpPath is null)
        {
            _logger.LogWarning("yt-dlp is not available (download failed); keeping {VideoId}.", videoId);
            return null;
        }

        var psi = new ProcessStartInfo { FileName = ytDlpPath };
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--skip-download");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(width)s %(height)s");
        using var cookieFile = CookieFile.Apply(psi, Plugin.Instance?.Configuration.YouTubeCookies, _logger);
        psi.ArgumentList.Add($"https://www.youtube.com/watch?v={videoId}");

        try
        {
            // Hard timeout so a slow/stuck yt-dlp call can never freeze the sync.
            var result = await ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (result is null)
            {
                _logger.LogWarning("Shorts probe timed out for {VideoId}; keeping it.", videoId);
                return null;
            }

            var stdout = result.Stdout.Trim();
            if (result.ExitCode != 0 || string.IsNullOrEmpty(stdout))
            {
                return null;
            }

            // Output: "<width> <height>" (first line).
            var parts = stdout.Split('\n')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                && w > 0 && h > 0)
            {
                // Vertical aspect => Short.
                return h > w;
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Shorts probe failed for {VideoId}; keeping it.", videoId);
            return null;
        }
    }
}
