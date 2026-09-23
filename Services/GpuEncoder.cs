using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Whether NVIDIA's hardware H.264 encoder can be used - for every HLS
/// session when it is (see <see cref="HlsPackagerSessionManager"/>), and
/// what makes sources above 1080p possible at all. YouTube only
/// publishes H.264 up to 1080p; above that the source is VP9/AV1 and has to
/// be re-encoded, and measured on the real server (Ryzen 7 5800X) libx264
/// manages only ~0.28x real time on 4K60 - unwatchable - while h264_nvenc
/// (RTX 4070 Ti, preset p4) does ~1.6x, faster than YouTube even serves
/// the 4K source (~1.5x). H.264 rather than HEVC on purpose: it's the only
/// codec every client accepts inside the MPEG-TS segments this plugin
/// serves (Apple's native players reject HEVC in MPEG-TS), and it keeps
/// what <see cref="JellyTuberMediaSourceProvider"/> declares to Jellyfin
/// true.
///
/// <see cref="IMediaEncoder.SupportsEncoder"/> isn't enough on its own:
/// jellyfin-ffmpeg always lists h264_nvenc, GPU or not. So this runs one
/// real tiny encode at startup, and temporarily disables the GPU path after
/// a real session fails to start on it (e.g. every NVENC session already
/// taken by Jellyfin's own transcodes) - callers then fall back to libx264
/// (capped at 1080p for a &gt;1080p source).
/// </summary>
internal static class GpuEncoder
{
    public const string Encoder = "h264_nvenc";

    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private static volatile bool _probedOk;
    private static long _disabledUntilTicks;

    /// <summary>True once the startup probe succeeded and no recent real session failed on it.</summary>
    public static bool IsUsable => _probedOk && DateTime.UtcNow.Ticks >= Interlocked.Read(ref _disabledUntilTicks);

    public static void ReportFailure(ILogger logger)
    {
        Interlocked.Exchange(ref _disabledUntilTicks, (DateTime.UtcNow + FailureCooldown).Ticks);
        logger.LogWarning(
            "{Encoder} failed to start an HLS session - falling back to libx264 (capped at 1080p) for the next {Minutes} minutes",
            Encoder,
            FailureCooldown.TotalMinutes);
    }

    public static async Task ProbeAsync(string ffmpegPath, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=1280x720", "-frames:v", "1", "-c:v", Encoder, "-f", "null", "-" })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                logger.LogInformation("{Encoder} probe timed out - sources above 1080p will be capped to 1080p", Encoder);
                return;
            }

            _probedOk = process.ExitCode == 0;
            if (_probedOk)
            {
                logger.LogInformation("{Encoder} available - all HLS sessions will be GPU-encoded, including sources above 1080p (up to MaxHeight)", Encoder);
            }
            else
            {
                logger.LogInformation(
                    "{Encoder} not usable ({Error}) - sources above 1080p will be capped to 1080p",
                    Encoder,
                    (await stderrTask.ConfigureAwait(false)).Trim());
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "{Encoder} probe failed - sources above 1080p will be capped to 1080p", Encoder);
        }
    }
}

/// <summary>Runs <see cref="GpuEncoder.ProbeAsync"/> once at startup, in the background.</summary>
public sealed class GpuEncoderProbeService : IHostedService
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<GpuEncoderProbeService> _logger;

    public GpuEncoderProbeService(IMediaEncoder mediaEncoder, ILogger<GpuEncoderProbeService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            // Jellyfin may not have located ffmpeg yet this early in startup.
            for (var i = 0; i < 60 && string.IsNullOrEmpty(_mediaEncoder.EncoderPath); i++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(_mediaEncoder.EncoderPath))
            {
                await GpuEncoder.ProbeAsync(_mediaEncoder.EncoderPath, _logger, CancellationToken.None).ConfigureAwait(false);
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
