using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTuber.Services;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Api;

/// <summary>
/// Resolver endpoint. The .strm files point at Stream/{videoId}; when a
/// client plays one, we ask yt-dlp for the real stream URL.
///
/// googlevideo.com URLs are signed and IP-locked to whoever resolved them -
/// i.e. this server. For a client reaching the internet through the same
/// router as the server (the common case: same LAN/NAT, so the same public
/// IP), a redirect straight to that URL works exactly as well as it did for
/// this server and is far faster - no proxy overhead, no muxing, the client
/// does its own adaptive bitrate. <see cref="LocalNetworkDetector"/> decides
/// this per request by comparing the client's IP against the server's own
/// public IP.
///
/// For a client on a genuinely different network (mobile data, another
/// building - no VPN), that redirect would 403, so those requests fall back
/// to PROXYING the bytes instead, keeping every request to YouTube's CDN
/// originating from this server. When the resolved format is HLS, the
/// playlist is rewritten so its segment URIs also route back through
/// <see cref="Proxy"/> for the same reason.
/// </summary>
[ApiController]
[Route("JellyTuber")]
public class PlaybackController : ControllerBase
{
    private static readonly string[] AllowedUpstreamHostSuffixes =
    {
        ".googlevideo.com",
        ".youtube.com",
        ".ytimg.com"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<PlaybackController> _logger;

    public PlaybackController(IHttpClientFactory httpClientFactory, IMediaEncoder mediaEncoder, ILogger<PlaybackController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// GET /JellyTuber/Stream/{videoId}
    ///
    /// AllowAnonymous so the server's own transcoder/clients can fetch the
    /// stream without juggling an auth token inside the .strm. Keep the endpoint
    /// on a trusted network.
    /// </summary>
    [HttpGet("Stream/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult> Stream([FromRoute] string videoId, CancellationToken ct)
    {
        var resolver = new PlaybackResolver(_logger);

        var clientIp = GetClientIp();
        var isSameNetwork = await LocalNetworkDetector.IsSameNetworkAsync(_httpClientFactory, clientIp, _logger, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "JellyTuber Stream {VideoId}: X-Forwarded-For={Xff}, RemoteIpAddress={RemoteIp}, resolved clientIp={ClientIp}, classified as {Classification}",
            videoId,
            Request.Headers.TryGetValue("X-Forwarded-For", out var xffHeader) ? xffHeader.ToString() : "(none)",
            HttpContext.Connection.RemoteIpAddress,
            clientIp,
            isSameNetwork ? "SAME NETWORK (will try direct redirect first)" : "REMOTE (proxy pipeline only)");

        if (isSameNetwork)
        {
            var directUrl = await resolver.ResolveDirectAsync(videoId, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(directUrl))
            {
                return Redirect(directUrl!);
            }

            // No HLS manifest for this video - the common case, since most
            // regular (non-live) videos don't expose one. Fall through to
            // the proxied DASH pipeline below: same-network clients still
            // reach it faster than remote ones would (no extra network hop
            // to get there), and it reaches full quality (up to MaxHeight)
            // instead of the 360p a plain redirect would be stuck with.
        }

        var resolveSw = Stopwatch.StartNew();
        var resolved = await resolver.ResolveAsync(videoId, ct).ConfigureAwait(false);
        _logger.LogInformation("yt-dlp resolve for {VideoId} took {ElapsedMs}ms (cache hit if this is near 0ms)", videoId, resolveSw.ElapsedMilliseconds);

        if (resolved is null)
        {
            return NotFound($"Could not resolve video {videoId}");
        }

        if (resolved.VideoUrl is not null && resolved.AudioUrl is not null)
        {
            if (resolved.DurationSeconds > 0 && resolved.IsTsCompatible)
            {
                // Known duration and an H.264+AAC source: hand out a
                // playlist that re-segments this DASH source into
                // independently time-addressable HLS segments (see Segment
                // below) instead of one continuous muxed stream with
                // byte-range-approximated seeking.
                await WritePlaylistAsync(videoId, resolved, ct).ConfigureAwait(false);
            }
            else
            {
                // Either no duration (can't build a fixed-length segment
                // playlist) or a codec MPEG-TS can't carry (VP9/AV1 video,
                // Opus audio - typically heights above 1080p, where YouTube
                // doesn't offer H.264). Fall back to muxing the whole thing
                // as one continuous stream; seeking on it is only
                // approximate.
                var succeeded = await RelayMuxedAsync(resolved, ct).ConfigureAwait(false);

                // YouTube's signed URLs can go bad (403) well before the
                // window we cache them for - a seek minutes after the
                // initial resolve can land on an already-dead URL, and
                // without this it would keep failing the same way until the
                // cache entry naturally expires (up to LinkCacheMinutes
                // later). Drop the stale entry and retry once against a
                // freshly resolved URL before giving up. Safe to retry: a
                // failed attempt never got as far as writing response
                // headers.
                if (!succeeded && !Response.HasStarted && !ct.IsCancellationRequested)
                {
                    PlaybackResolver.Invalidate(videoId);
                    var fresh = await resolver.ResolveAsync(videoId, ct).ConfigureAwait(false);

                    if (fresh?.VideoUrl is not null && fresh.AudioUrl is not null)
                    {
                        await RelayMuxedAsync(fresh, ct).ConfigureAwait(false);
                    }
                    else if (fresh?.DirectUrl is not null)
                    {
                        await RelayAsync(fresh.DirectUrl, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        else if (resolved.DirectUrl is not null)
        {
            await RelayAsync(resolved.DirectUrl, ct).ConfigureAwait(false);
        }
        else
        {
            return NotFound($"Could not resolve video {videoId}");
        }

        return new EmptyResult();
    }

    /// <summary>
    /// The scheme to use when building an absolute URL back to this plugin
    /// (playlist segment URIs, proxy URIs). <see cref="HttpRequest.Scheme"/>
    /// reflects what Kestrel itself received, which behind a reverse proxy
    /// terminating TLS is "http" even though the public-facing request was
    /// HTTPS - building URLs from that lands on the wrong port (80 instead
    /// of 443) and fails outright rather than just being insecure. Prefer
    /// the proxy's own X-Forwarded-Proto when present.
    /// </summary>
    private string GetExternalScheme()
    {
        if (Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && proto.Count > 0)
        {
            var first = proto[0]?.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first))
            {
                return first;
            }
        }

        return Request.Scheme;
    }

    /// <summary>
    /// The requesting client's real IP, for <see cref="LocalNetworkDetector"/>.
    /// Behind a reverse proxy, <see cref="ConnectionInfo.RemoteIpAddress"/> is
    /// the proxy's own address, not the client's - X-Forwarded-For (set by
    /// every standard reverse proxy) carries the real originating IP as its
    /// first, leftmost entry.
    /// </summary>
    private IPAddress? GetClientIp()
    {
        if (Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) && forwardedFor.Count > 0)
        {
            var first = forwardedFor[0]?.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first) && IPAddress.TryParse(first, out var parsed))
            {
                return parsed;
            }
        }

        return HttpContext.Connection.RemoteIpAddress;
    }

    /// <summary>
    /// Writes the synthetic HLS playlist for a DASH source, referencing
    /// <see cref="Segment"/> for each piece. Generated fresh per request (not
    /// cached) since it's cheap to build and always reflects the current
    /// resolved duration.
    /// </summary>
    private async Task WritePlaylistAsync(string videoId, ResolvedStream resolved, CancellationToken ct)
    {
        var segmentBaseUrl = $"{GetExternalScheme()}://{Request.Host}/JellyTuber/Segment/{videoId}/";
        var playlist = OnDemandHlsPackager.BuildPlaylist(resolved.DurationSeconds, i => segmentBaseUrl + i.ToString(CultureInfo.InvariantCulture) + ".ts");

        PrefetchFirstSegment(videoId, resolved);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/vnd.apple.mpegurl";
        await Response.WriteAsync(playlist, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Kicks off the HLS packaging session for segment 0 as soon as the
    /// playlist is handed out, instead of waiting for the client's first
    /// Segment request to trigger it. Shaves the round-trip of the client
    /// fetching/parsing the playlist off the perceived startup latency,
    /// since that time otherwise passes with ffmpeg not yet even started.
    /// Fire-and-forget deliberately: this request's own CancellationToken
    /// dies with the response, but the packaging session needs to keep
    /// running for the Segment requests that follow it.
    /// </summary>
    private void PrefetchFirstSegment(string videoId, ResolvedStream resolved)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HlsPackagerSessionManager.GetSegmentAsync(_mediaEncoder, videoId, resolved, 0, _logger, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Prefetch of first HLS segment failed for {VideoId} (non-fatal, the real request will just start the session itself)", videoId);
            }
        });
    }

    /// <summary>
    /// GET /JellyTuber/Segment/{videoId}/{index}.ts
    ///
    /// Mux-and-serve one <see cref="OnDemandHlsPackager.SegmentSeconds"/>-long
    /// slice of a DASH source, addressed purely by time (via
    /// <paramref name="index"/>) rather than an approximated byte offset.
    /// This is what makes seeking reliable: jumping anywhere in the video is
    /// just a request for a different, independent segment URL, so there's
    /// nothing to splice together and nothing for a player to misinterpret
    /// as a broken continuation of a previous stream.
    ///
    /// The .ts suffix is required, not cosmetic: ffmpeg's own HLS demuxer
    /// (which is what Jellyfin's server-side transcoder uses to read this
    /// plugin's playlist when a client can't direct-play it) rejects segment
    /// URLs whose extension isn't on its allowed_segment_extensions list.
    /// </summary>
    [HttpGet("Segment/{videoId}/{index:int}.ts")]
    [AllowAnonymous]
    public async Task<ActionResult> Segment([FromRoute] string videoId, [FromRoute] int index, CancellationToken ct)
    {
        if (index < 0)
        {
            return NotFound();
        }

        var resolver = new PlaybackResolver(_logger);
        var resolved = await resolver.ResolveAsync(videoId, ct).ConfigureAwait(false);

        if (resolved?.VideoUrl is null || resolved.AudioUrl is null)
        {
            return NotFound($"Could not resolve video {videoId}");
        }

        var path = await HlsPackagerSessionManager.GetSegmentAsync(_mediaEncoder, videoId, resolved, index, _logger, ct).ConfigureAwait(false);

        // Same rationale as the retry in Stream(): a signed URL can go
        // stale mid-playback well before its cache entry expires. Also
        // covers a genuinely missing segment (e.g. an index past the end).
        if (path is null && !ct.IsCancellationRequested)
        {
            PlaybackResolver.Invalidate(videoId);
            HlsPackagerSessionManager.Invalidate(videoId);
            var fresh = await resolver.ResolveAsync(videoId, ct).ConfigureAwait(false);

            if (fresh?.VideoUrl is not null && fresh.AudioUrl is not null)
            {
                path = await HlsPackagerSessionManager.GetSegmentAsync(_mediaEncoder, videoId, fresh, index, _logger, ct).ConfigureAwait(false);
            }
        }

        if (path is null)
        {
            return ct.IsCancellationRequested ? new EmptyResult() : NotFound();
        }

        FileStream fileStream;
        try
        {
            fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            // Session was torn down (idle sweep/restart) between us learning
            // the path and opening it - vanishingly rare given how recently
            // it was written. Treat like any other missed segment.
            return NotFound();
        }

        await using (fileStream.ConfigureAwait(false))
        {
            Response.ContentType = "video/mp2t";
            Response.ContentLength = fileStream.Length;

            try
            {
                await fileStream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or seeked away mid-stream; not an error.
            }
        }

        return new EmptyResult();
    }

    /// <summary>How long to wait for ffmpeg to produce its first bytes before giving up on a seek target.</summary>
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Requests for fewer bytes than this are treated as a capability/header
    /// probe rather than real playback - see the comment in
    /// <see cref="RelayMuxedAsync"/> for why that distinction matters.
    /// </summary>
    private const long ProbeRangeThresholdBytes = 1024;

    /// <summary>
    /// Muxes YouTube's separate video-only and audio-only DASH URLs into one
    /// stream-copied Matroska stream via ffmpeg, piping stdout straight to the
    /// response. This is the path used for anything above 360p, since YouTube
    /// only publishes a single premuxed URL up to 360p.
    ///
    /// Seeking is approximate: since ffmpeg is remuxing live rather than
    /// serving a fixed file, an incoming byte Range is translated to a time
    /// offset using the estimated total size/duration, and ffmpeg is
    /// restarted with an input seek to roughly that point.
    ///
    /// A player needs a Content-Length to even compute what byte range to
    /// ask for when scrubbing (that's why we advertise one), but the real
    /// number of bytes ffmpeg produces for a given slice is only an estimate
    /// - YouTube's bitrate isn't perfectly constant, so it can come up
    /// short. Declaring a Content-Length we then fail to deliver leaves the
    /// client waiting forever for bytes that will never arrive - that's what
    /// left playback stuck/paused with no way to recover. So instead of
    /// hoping the estimate is right, we enforce it ourselves: never write
    /// more than promised (stop early and drop the rest if ffmpeg has more),
    /// and never write less (pad with silence if ffmpeg runs dry first). The
    /// Content-Length promise always holds by construction.
    ///
    /// Separately, because that same estimate can also land at or past the
    /// real end of the video, a seek can ask ffmpeg for a moment with no
    /// content left at all - ffmpeg then exits immediately with nothing to
    /// mux. We peek for the first chunk of output BEFORE committing to
    /// response headers so that case fails cleanly (a normal HTTP error the
    /// player can recover from) instead of promising a body that never
    /// arrives.
    /// </summary>
    private async Task<bool> RelayMuxedAsync(ResolvedStream resolved, CancellationToken ct)
    {
        var ffmpegPath = _mediaEncoder.EncoderPath;
        var total = resolved.EstimatedBytes;
        var declaredTotal = total > 0 ? (long)(total * 1.03) : 0;

        double? seekSeconds = null;
        long start = 0;
        long? explicitEnd = null;
        var isRangeRequest = false;

        var hasRange = declaredTotal > 0
            && Request.Headers.TryGetValue("Range", out var rangeHeader)
            && rangeHeader.Count > 0
            && TryParseRange(rangeHeader.ToString(), out start, out explicitEnd);

        // Some clients send a tiny explicit range (classically "bytes=0-1")
        // purely to sniff Range support / content type before issuing the
        // real request for whatever position they actually want - no real
        // player ever consumes video in chunks this small. Servicing one of
        // these the normal way still pays the full cost of a seek (a fresh
        // ffmpeg process and a new googlevideo connection) for bytes nobody
        // reads. Short-circuit it with a synthetic body instead - this is
        // pure response-shaping before any process is spawned, so it can't
        // affect the actual mux path.
        if (hasRange && explicitEnd is long endByte)
        {
            var probeStart = Math.Min(start, declaredTotal - 1);
            var probeEnd = Math.Min(endByte, declaredTotal - 1);
            var requestedLength = probeEnd - probeStart + 1;

            if (requestedLength is > 0 and <= ProbeRangeThresholdBytes)
            {
                await WriteProbeResponseAsync(probeStart, probeEnd, declaredTotal, ct).ConfigureAwait(false);
                return true;
            }
        }

        if (hasRange && start > 0)
        {
            start = Math.Min(start, declaredTotal - 1);
            isRangeRequest = true;

            if (resolved.DurationSeconds > 0 && declaredTotal > 0)
            {
                // Invert using declaredTotal, not total: declaredTotal (the
                // padded value) is what we handed the client as
                // Content-Length, so it's also what the client used to turn
                // its desired seek TIME into the byte offset it sent us.
                // Inverting with the unpadded total instead would recover a
                // time systematically ~3% later than intended - a small
                // absolute error early in the video that grows the deeper
                // the jump, e.g. landing over a minute past a seek target
                // near the end of a long video.
                var target = resolved.DurationSeconds * ((double)start / declaredTotal);

                // Leave a couple of seconds of headroom so an estimate that
                // lands right on (or past) the real end still resolves to
                // something ffmpeg can actually mux, instead of nothing.
                seekSeconds = Math.Clamp(target, 0, Math.Max(0, resolved.DurationSeconds - 2));
            }
        }

        double? remainingDuration = resolved.DurationSeconds > 0
            ? Math.Max(0, resolved.DurationSeconds - (seekSeconds ?? 0))
            : null;

        // The promised byte count for this response: everything from `start`
        // to the (padded) declared end, or the whole padded total for a
        // plain (non-range) play from the beginning.
        long? promisedLength = declaredTotal > 0 ? declaredTotal - start : null;

        Process process;
        try
        {
            process = FfmpegMuxer.Start(ffmpegPath, resolved.VideoUrl!, resolved.AudioUrl!, seekSeconds, remainingDuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch ffmpeg at '{Path}'", ffmpegPath);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return false;
        }

        using (process)
        {
            var stderrTask = LogStderrAsync(process);
            var stdout = process.StandardOutput.BaseStream;
            var buffer = new byte[81920];
            int firstReadCount;

            try
            {
                firstReadCount = await ReadWithTimeoutAsync(stdout, buffer, FirstByteTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                KillProcess(process);
                await stderrTask.ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("ffmpeg took too long to produce data for this seek target");
                KillProcess(process);
                await stderrTask.ConfigureAwait(false);
                Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                return false;
            }

            if (firstReadCount == 0)
            {
                // Nothing to mux. Often a seek target beyond the real end of
                // the video, but this is also what a stale/expired source
                // URL looks like (ffmpeg fails to even open the input) - the
                // caller retries once against a freshly resolved URL before
                // treating it as a real end-of-video case.
                _logger.LogWarning("ffmpeg produced no output (seek target past the end of the video, or the source URL went stale)");
                KillProcess(process);
                await stderrTask.ConfigureAwait(false);
                Response.StatusCode = isRangeRequest ? StatusCodes.Status416RangeNotSatisfiable : StatusCodes.Status502BadGateway;
                return false;
            }

            Response.ContentType = "video/x-matroska";
            Response.Headers["Accept-Ranges"] = declaredTotal > 0 ? "bytes" : "none";

            if (isRangeRequest)
            {
                Response.StatusCode = StatusCodes.Status206PartialContent;
                Response.Headers["Content-Range"] = $"bytes {start}-{declaredTotal - 1}/{declaredTotal}";
            }
            else
            {
                Response.StatusCode = StatusCodes.Status200OK;
            }

            if (promisedLength is long len)
            {
                Response.ContentLength = len;
            }

            try
            {
                await RelayExactAsync(stdout, Response.Body, buffer, firstReadCount, promisedLength, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or seeked away mid-stream; not an error.
            }
            finally
            {
                KillProcess(process);
                await stderrTask.ConfigureAwait(false);
            }

            return true;
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>,
    /// starting with the already-read <paramref name="firstChunk"/>, capped
    /// to exactly <paramref name="exactLength"/> bytes when given: extra
    /// source data past that point is discarded, and if the source runs dry
    /// first the remainder is padded with zero bytes. This is what lets a
    /// declared Content-Length always be honoured exactly, even though the
    /// real amount of muxed output can't be known upfront.
    /// </summary>
    private static async Task RelayExactAsync(Stream source, Stream destination, byte[] firstChunk, int firstChunkLength, long? exactLength, CancellationToken ct)
    {
        long written = 0;

        async Task<bool> WriteAsync(byte[] data, int length)
        {
            var toWrite = exactLength is long cap ? (int)Math.Clamp(cap - written, 0, length) : length;
            if (toWrite > 0)
            {
                await destination.WriteAsync(data.AsMemory(0, toWrite), ct).ConfigureAwait(false);
                written += toWrite;
            }

            return exactLength is null || written < exactLength;
        }

        if (!await WriteAsync(firstChunk, firstChunkLength).ConfigureAwait(false))
        {
            return;
        }

        var buffer = new byte[81920];
        int n;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (!await WriteAsync(buffer, n).ConfigureAwait(false))
            {
                return;
            }
        }

        if (exactLength is long total && written < total)
        {
            var zeros = new byte[81920];
            while (written < total)
            {
                var chunk = (int)Math.Min(zeros.Length, total - written);
                await destination.WriteAsync(zeros.AsMemory(0, chunk), ct).ConfigureAwait(false);
                written += chunk;
            }
        }
    }

    /// <summary>
    /// Answers a tiny probe range with a synthetic zero-filled body of
    /// exactly the requested length, matching the headers a real request for
    /// the same range would get - without spawning ffmpeg or touching
    /// googlevideo at all.
    /// </summary>
    private async Task WriteProbeResponseAsync(long start, long end, long declaredTotal, CancellationToken ct)
    {
        var length = end - start + 1;

        Response.ContentType = "video/x-matroska";
        Response.Headers["Accept-Ranges"] = "bytes";
        Response.StatusCode = StatusCodes.Status206PartialContent;
        Response.Headers["Content-Range"] = $"bytes {start}-{end}/{declaredTotal}";
        Response.ContentLength = length;

        var body = new byte[length];
        await Response.Body.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        return await stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
    }

    private static void KillProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
    }

    private async Task LogStderrAsync(Process process)
    {
        try
        {
            var err = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(err))
            {
                _logger.LogWarning("ffmpeg: {Error}", err.Trim());
            }
        }
        catch
        {
            // Process was killed mid-read; nothing useful to log.
        }
    }

    private static bool TryParseRange(string? rangeHeader, out long start, out long? end)
    {
        start = 0;
        end = null;

        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = rangeHeader.Substring("bytes=".Length).Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash <= 0 || !long.TryParse(spec.AsSpan(0, dash), out start))
        {
            return false;
        }

        var endSpan = spec.AsSpan(dash + 1);
        if (endSpan.Length > 0 && long.TryParse(endSpan, out var parsedEnd))
        {
            end = parsedEnd;
        }

        return true;
    }

    /// <summary>
    /// GET /JellyTuber/Proxy?u=&lt;absolute googlevideo/youtube URL&gt;
    ///
    /// Used for HLS segment/init-segment fetches referenced by a rewritten
    /// playlist. The target host is allow-listed so this can't be used as an
    /// open proxy to arbitrary URLs.
    /// </summary>
    [HttpGet("Proxy")]
    [AllowAnonymous]
    public async Task<ActionResult> Proxy([FromQuery] string u, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(u) || !TryValidateUpstreamUrl(u, out var target))
        {
            return BadRequest("Invalid or disallowed upstream URL.");
        }

        await RelayAsync(target!, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>
    /// Fetches <paramref name="url"/> (forwarding the client's Range header),
    /// and either rewrites-and-returns it as an HLS playlist, or streams the
    /// response body straight through to the client with the upstream status
    /// code, Content-Type, Content-Length/Content-Range preserved.
    /// </summary>
    private async Task RelayAsync(string url, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient();

        // HttpClient.Timeout (default 100s) covers the whole request,
        // including streaming the response body - which for a multi-minute
        // video would otherwise abort playback partway through. Cancellation
        // is instead tied to the incoming request's own lifetime via ct.
        http.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (Request.Headers.TryGetValue("Range", out var range) && range.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", range.ToString());
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Upstream fetch failed for {Url}", url);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (upstream)
        {
            var contentType = upstream.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var isPlaylist = contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || IsM3u8Path(url);

            if (isPlaylist)
            {
                var text = await upstream.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var proxyBase = $"{GetExternalScheme()}://{Request.Host}/JellyTuber/Proxy?u=";
                var rewritten = HlsPlaylistRewriter.Rewrite(
                    text,
                    new Uri(url),
                    original => proxyBase + Uri.EscapeDataString(original));

                Response.StatusCode = StatusCodes.Status200OK;
                Response.ContentType = "application/vnd.apple.mpegurl";
                await Response.WriteAsync(rewritten, ct).ConfigureAwait(false);
                return;
            }

            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType = string.IsNullOrEmpty(contentType) ? "video/mp4" : contentType;
            if (upstream.Content.Headers.ContentLength is long len)
            {
                Response.ContentLength = len;
            }

            if (upstream.Content.Headers.ContentRange is { } contentRange)
            {
                Response.Headers["Content-Range"] = contentRange.ToString();
            }

            Response.Headers["Accept-Ranges"] = "bytes";

            var body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                try
                {
                    await body.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected or seeked away mid-stream; not an error.
                }
            }
        }
    }

    private static bool IsM3u8Path(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.AbsolutePath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateUpstreamUrl(string value, out string? url)
    {
        url = null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        var host = uri.Host;
        var allowed = false;
        foreach (var suffix in AllowedUpstreamHostSuffixes)
        {
            // Matches the bare domain ("youtube.com") as well as any subdomain.
            if (host.Equals(suffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }

        if (!allowed)
        {
            return false;
        }

        url = uri.ToString();
        return true;
    }
}
