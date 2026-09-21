using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Demotes a video's HLS packaging session (see
/// <see cref="HlsPackagerSessionManager"/>) to a background completion job
/// the moment Jellyfin reports that playback has actually stopped, instead
/// of leaving it running at full priority indefinitely or waiting on that
/// manager's own idle sweep (up to 5 minutes - see its <c>IdleTimeout</c>
/// doc comment for why that default is deliberately generous: it exists so
/// RESUMING a paused video is near-instant).
///
/// A still-encoding session left entirely alone would keep competing for a
/// full CPU core with every new video someone starts - exactly what was
/// observed as videos starting in a much lower quality than usual under
/// load, back when a "closed" session simply lingered until the idle sweep
/// caught it. <see cref="HlsPackagerSessionManager.DemoteToBackground"/>
/// avoids reintroducing that: it lowers the process's OS priority and caps
/// how many such background jobs can run at once
/// (<see cref="Configuration.PluginConfiguration.MaxConcurrentBackgroundEncodes"/>),
/// letting the rest simply pause (segments already produced stay cached)
/// until a slot frees up - rather than racing every new playback for the
/// same CPU the old "just kill it" behaviour used to free up instantly.
/// </summary>
public sealed class PlaybackStopSessionCleaner : IHostedService
{
    private static readonly Regex VideoIdFromStrmUrl = new(@"/JellyTuber/Stream/([A-Za-z0-9_-]+)", RegexOptions.Compiled);

    /// <summary>
    /// Below this reported position, a "stopped" event is treated as noise
    /// rather than a real stop - see <see cref="OnPlaybackStopped"/>.
    /// </summary>
    private static readonly long MinPositionTicksToInvalidate = TimeSpan.FromSeconds(3).Ticks;

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<PlaybackStopSessionCleaner> _logger;

    public PlaybackStopSessionCleaner(ISessionManager sessionManager, ILogger<PlaybackStopSessionCleaner> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("PlaybackStopSessionCleaner started: HLS sessions now demote to background completion on playback stop instead of waiting for the idle sweep.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        // item.Path is the .strm FILE's own path, same as in
        // JellyTuberMediaSourceProvider - only its CONTENT is the actual
        // playback URL, read below.
        var path = e.Item?.Path;
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Real bug found live: some clients (confirmed with JellyTV) fire a
        // spurious PlaybackStopped at position 0 as part of their own
        // negotiation/retry dance BEFORE actually settling on how to play a
        // video - not a real "user is done with this" stop. Tearing down the
        // session for one of those kills a first segment that just finished
        // encoding, forcing the client's very next attempt to pay the full
        // multi-second first-segment cost all over again - directly doubling
        // real-world startup latency (measured: two back-to-back ~4.6s waits
        // for the same video's segment 0, the second entirely avoidable).
        // Below a few real seconds of reported position, skip the teardown
        // and let the idle sweep (up to 5 minutes) catch it instead - the
        // worst case is a session lingering briefly for a genuine near-
        // instant real stop, which is far cheaper than this regression.
        if (e.PlaybackPositionTicks is null or < 0 || e.PlaybackPositionTicks < MinPositionTicksToInvalidate)
        {
            return;
        }

        // Fire-and-forget: this is an event handler, and the actual work
        // (a tiny file read plus killing a process) must never block
        // whatever else Jellyfin's SessionManager does with this event.
        _ = Task.Run(async () =>
        {
            try
            {
                var url = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
                var match = VideoIdFromStrmUrl.Match(url);
                if (!match.Success)
                {
                    return;
                }

                var videoId = match.Groups[1].Value;
                HlsPackagerSessionManager.DemoteToBackground(videoId);
                _logger.LogDebug(
                    "Playback stopped for {VideoId}; demoted its HLS session to background completion instead of waiting for the idle sweep",
                    videoId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not tear down HLS session after playback stop for {Path}", path);
            }
        });
    }
}
