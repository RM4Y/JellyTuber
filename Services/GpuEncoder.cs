using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Which hardware H.264 encoder (if any) HLS sessions use - for every
/// session when one is usable (see <see cref="HlsPackagerSessionManager"/>),
/// and what makes sources above 1080p possible at all. YouTube only
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
/// The candidate comes from <see cref="Configuration.PluginConfiguration.HardwareEncoding"/>
/// ("Auto" tries NVENC, then QSV, then VAAPI; "Jellyfin" follows the
/// server's own transcoding setting). <see cref="IMediaEncoder.SupportsEncoder"/>
/// isn't enough on its own: jellyfin-ffmpeg always lists these encoders,
/// hardware or not. So each candidate gets one real tiny encode - at
/// startup and whenever either setting changes - and the GPU path is
/// temporarily disabled after a real session fails to start on it (e.g.
/// every NVENC session already taken by Jellyfin's own transcodes); callers
/// then fall back to libx264 (capped at 1080p for a &gt;1080p source).
/// </summary>
internal static class GpuEncoder
{
    public const string Nvenc = "h264_nvenc";
    public const string Qsv = "h264_qsv";
    public const string Vaapi = "h264_vaapi";

    private const string DefaultRenderDevice = "/dev/dri/renderD128";

    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim ProbeLock = new(1, 1);

    private static volatile string? _encoder;
    private static volatile string _renderDevice = DefaultRenderDevice;
    private static long _disabledUntilTicks;

    /// <summary>The hardware encoder to use right now, or null for software (none configured/working, or cooling down after a failure).</summary>
    public static string? UsableEncoder => DateTime.UtcNow.Ticks >= Interlocked.Read(ref _disabledUntilTicks) ? _encoder : null;

    public static bool IsUsable => UsableEncoder is not null;

    /// <summary>DRM render node for <see cref="Vaapi"/>/<see cref="Qsv"/> - Jellyfin's configured VAAPI device when set.</summary>
    public static string RenderDevice => _renderDevice;

    public static bool IsHardware(string encoder) => encoder is Nvenc or Qsv or Vaapi;

    public static void ReportFailure(ILogger logger)
    {
        Interlocked.Exchange(ref _disabledUntilTicks, (DateTime.UtcNow + FailureCooldown).Ticks);
        logger.LogWarning(
            "{Encoder} failed to start an HLS session - falling back to libx264 (capped at 1080p) for the next {Minutes} minutes",
            _encoder,
            FailureCooldown.TotalMinutes);
    }

    /// <summary>
    /// Arguments that must precede the inputs for <paramref name="encoder"/>
    /// (hardware device setup). Empty for NVENC/software.
    /// </summary>
    public static IEnumerable<string> GlobalArgs(string encoder) => encoder switch
    {
        Vaapi => new[] { "-vaapi_device", RenderDevice },
        Qsv => new[] { "-init_hw_device", "vaapi=va:" + RenderDevice, "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs" },
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// The -vf chain uploading software-decoded frames to the device for
    /// <paramref name="encoder"/>, or null when it takes system memory.
    /// </summary>
    public static string? UploadFilter(string encoder) => encoder switch
    {
        Vaapi => "format=nv12,hwupload",
        Qsv => "format=nv12,hwupload=extra_hw_frames=64",
        _ => null
    };

    /// <summary>Re-picks and probes the encoder from the current settings.</summary>
    public static async Task ProbeAsync(string ffmpegPath, string mode, EncodingOptions? jellyfinEncoding, ILogger logger, CancellationToken ct)
    {
        await ProbeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(jellyfinEncoding?.VaapiDevice))
            {
                _renderDevice = jellyfinEncoding!.VaapiDevice;
            }
            else
            {
                _renderDevice = DefaultRenderDevice;
            }

            var candidates = Candidates(mode, jellyfinEncoding);
            if (candidates.Length == 0)
            {
                _encoder = null;
                if (mode == "Jellyfin")
                {
                    logger.LogInformation(
                        "Hardware encoding disabled: Jellyfin's transcoding setting is {Type} (hardware encoding {Enabled}) - HLS sessions use the CPU, sources above 1080p are capped to 1080p",
                        jellyfinEncoding?.HardwareAccelerationType,
                        jellyfinEncoding?.EnableHardwareEncoding == true ? "on" : "off");
                }
                else
                {
                    logger.LogInformation("Hardware encoding disabled (mode {Mode}) - HLS sessions use the CPU, sources above 1080p are capped to 1080p", mode);
                }

                return;
            }

            foreach (var candidate in candidates)
            {
                var error = await TryEncodeAsync(ffmpegPath, candidate, ct).ConfigureAwait(false);
                if (error is null)
                {
                    _encoder = candidate;
                    Interlocked.Exchange(ref _disabledUntilTicks, 0);
                    logger.LogInformation("{Encoder} available (mode {Mode}) - all HLS sessions will be GPU-encoded, including sources above 1080p (up to MaxHeight)", candidate, mode);
                    return;
                }

                logger.LogInformation("{Encoder} not usable ({Error})", candidate, error);
            }

            _encoder = null;
            logger.LogWarning("No hardware encoder usable (mode {Mode}) - HLS sessions use the CPU, sources above 1080p are capped to 1080p", mode);
        }
        finally
        {
            ProbeLock.Release();
        }
    }

    private static string[] Candidates(string mode, EncodingOptions? jellyfinEncoding)
    {
        switch (mode)
        {
            case "None":
                return Array.Empty<string>();
            case "Nvenc":
                return new[] { Nvenc };
            case "Qsv":
                return new[] { Qsv };
            case "Vaapi":
                return new[] { Vaapi };
            case "Jellyfin":
                // amf (Windows), videotoolbox (macOS), v4l2m2m and rkmpp
                // aren't wired into the segmenter - software for those.
                if (jellyfinEncoding is null || !jellyfinEncoding.EnableHardwareEncoding)
                {
                    return Array.Empty<string>();
                }

                return jellyfinEncoding.HardwareAccelerationType.ToString() switch
                {
                    "nvenc" => new[] { Nvenc },
                    "qsv" => new[] { Qsv },
                    "vaapi" => new[] { Vaapi },
                    _ => Array.Empty<string>()
                };
            default:
                return new[] { Nvenc, Qsv, Vaapi };
        }
    }

    /// <summary>One real 1-frame encode; null on success, else the error.</summary>
    private static async Task<string?> TryEncodeAsync(string ffmpegPath, string encoder, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var arg in GlobalArgs(encoder))
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var arg in new[] { "-f", "lavfi", "-i", "color=black:s=1280x720" })
        {
            psi.ArgumentList.Add(arg);
        }

        if (UploadFilter(encoder) is { } filter)
        {
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(filter);
        }

        foreach (var arg in new[] { "-frames:v", "1", "-c:v", encoder, "-f", "null", "-" })
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
                return "probe timed out";
            }

            return process.ExitCode == 0 ? null : (await stderrTask.ConfigureAwait(false)).Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>
/// Runs <see cref="GpuEncoder.ProbeAsync"/> in the background at startup,
/// and again whenever the plugin's or Jellyfin's encoding settings change.
/// </summary>
public sealed class GpuEncoderProbeService : IHostedService
{
    private const string EncodingConfigKey = "encoding";

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<GpuEncoderProbeService> _logger;
    private string? _lastProbeKey;

    public GpuEncoderProbeService(IMediaEncoder mediaEncoder, IConfigurationManager configurationManager, ILogger<GpuEncoderProbeService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _configurationManager.NamedConfigurationUpdated += OnNamedConfigurationUpdated;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnPluginConfigurationChanged;
        }

        _ = Task.Run(async () =>
        {
            // Jellyfin may not have located ffmpeg yet this early in startup.
            for (var i = 0; i < 60 && string.IsNullOrEmpty(_mediaEncoder.EncoderPath); i++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }

            await ProbeIfChangedAsync().ConfigureAwait(false);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _configurationManager.NamedConfigurationUpdated -= OnNamedConfigurationUpdated;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnPluginConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void OnPluginConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e) => _ = ProbeIfChangedAsync();

    private void OnNamedConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs e)
    {
        if (string.Equals(e.Key, EncodingConfigKey, StringComparison.OrdinalIgnoreCase))
        {
            _ = ProbeIfChangedAsync();
        }
    }

    /// <summary>
    /// Probes only when the inputs that decide the encoder actually changed -
    /// the plugin config is saved for unrelated reasons all the time (every
    /// self-service channel/video add).
    /// </summary>
    private async Task ProbeIfChangedAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_mediaEncoder.EncoderPath))
            {
                return;
            }

            var mode = Plugin.Instance?.Configuration.HardwareEncoding ?? "Auto";
            var encoding = _configurationManager.GetConfiguration<EncodingOptions>(EncodingConfigKey);
            var key = $"{mode}|{encoding?.HardwareAccelerationType}|{encoding?.EnableHardwareEncoding}|{encoding?.VaapiDevice}";
            if (Interlocked.Exchange(ref _lastProbeKey, key) == key)
            {
                return;
            }

            await GpuEncoder.ProbeAsync(_mediaEncoder.EncoderPath, mode, encoding, _logger, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hardware encoder probe failed");
        }
    }
}
