using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTuber.Services;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.ScheduledTasks;

/// <summary>
/// One-off maintenance task: precaches the first 30s of every video already
/// in the library that isn't cached yet - the retroactive counterpart to the
/// precache <see cref="YouTubeSyncTask"/> now does automatically for videos
/// as they're first synced. Manual trigger only (no default schedule): a
/// full catalog can be sizeable, and this exists to be run once (from
/// Dashboard -> Scheduled Tasks) after upgrading to pick up everything that
/// predates sync-time precache, not on a recurring basis - re-running it is
/// safe (already-cached videos are skipped cheaply) but pointless once the
/// whole library has been covered.
/// </summary>
public class BackfillPrecacheTask : IScheduledTask
{
    /// <summary>Matches <see cref="YouTubeSyncTask"/>'s own precache window.</summary>
    private const int PrecacheSeconds = 30;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<BackfillPrecacheTask> _logger;

    public BackfillPrecacheTask(IHttpClientFactory httpClientFactory, IMediaEncoder mediaEncoder, ILogger<BackfillPrecacheTask> logger)
    {
        _httpClientFactory = httpClientFactory;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public string Name => "Precache existing library (JellyTuber)";

    public string Key => "JellyTuberBackfillPrecache";

    public string Description => "One-off: pre-encodes the first 30s of every already-synced video that isn't cached yet, so it starts instantly on first play. Safe to re-run.";

    public string Category => "JellyTuber";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogWarning("No configuration found; aborting.");
            return;
        }

        var http = _httpClientFactory.CreateClient();
        var writer = new LibraryWriter(http, _logger);

        // config.LibraryFolder recursively covers every per-user folder
        // (they're always nested under it) - only an admin source with an
        // explicit custom DestinationFolder can land outside it.
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.LibraryFolder };
        foreach (var source in config.Sources)
        {
            if (!string.IsNullOrWhiteSpace(source.DestinationFolder))
            {
                roots.Add(source.DestinationFolder);
            }
        }

        var videoIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var id in writer.ListAllVideoIds(root))
            {
                videoIds.Add(id);
            }
        }

        _logger.LogInformation("Precache backfill: {Count} videos found across the library", videoIds.Count);

        if (videoIds.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var total = videoIds.Count;
        var done = 0;

        await Parallel.ForEachAsync(
            videoIds,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, config.MaxConcurrentBackgroundEncodes),
                CancellationToken = cancellationToken
            },
            async (videoId, ct) =>
            {
                try
                {
                    await VideoPrecacher
                        .PrecacheAsync(_httpClientFactory, _mediaEncoder, videoId, PrecacheSeconds, _logger, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Precache backfill failed for {VideoId} (non-fatal - the first real play will just encode it then)", videoId);
                }

                var current = Interlocked.Increment(ref done);
                progress.Report(current * 100.0 / total);
            }).ConfigureAwait(false);

        _logger.LogInformation("Precache backfill done: {Count} videos processed", total);
    }
}
