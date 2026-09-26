using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Materializes <see cref="Configuration.PluginConfiguration.YouTubeCookies"/>
/// as a private cookies.txt for a single yt-dlp run and adds the matching
/// <c>--cookies</c> flag. Each run gets its own copy because yt-dlp rewrites
/// the cookie file on exit: concurrent resolves sharing one file could
/// interleave those writes and corrupt it. Dispose after the process exits:
/// that's when cookies YouTube rotated during the run are carried back into
/// the configuration (see <see cref="Dispose"/>).
/// </summary>
public sealed class CookieFile : IDisposable
{
    private static readonly TimeSpan BotCheckWarningInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Minimum gap between two write-backs of rotated cookies into the
    /// configuration - YouTube hands out a fresh session cookie on almost
    /// every request, and each write-back is a full config save.
    /// </summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(10);

    private static long _lastBotCheckWarningTicks;

    private static long _lastPersistTicks;

    private readonly string? _path;

    private readonly string? _original;

    private readonly ILogger? _logger;

    private CookieFile(string? path, string? original, ILogger? logger)
    {
        _path = path;
        _original = original;
        _logger = logger;
    }

    /// <summary>
    /// Adds <c>--cookies &lt;temp file&gt;</c> to <paramref name="psi"/> when
    /// cookies are configured; otherwise does nothing. Never throws - a
    /// failure to write the file just means the run goes without cookies.
    /// </summary>
    public static CookieFile Apply(ProcessStartInfo psi, string? cookies, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(cookies))
        {
            return new CookieFile(null, null, null);
        }

        try
        {
            // Under the plugin's own data folder rather than the shared /tmp,
            // and created owner-only from the start (not chmod'ed after the
            // fact, which left a window where the cookies were world-readable).
            var dir = Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "cookies");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");

            // yt-dlp expects LF line endings; a leading Netscape header keeps
            // it from rejecting a paste that dropped the first comment line.
            var body = cookies.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
            if (!body.StartsWith("# Netscape HTTP Cookie File", StringComparison.Ordinal)
                && !body.StartsWith("# HTTP Cookie File", StringComparison.Ordinal))
            {
                body = "# Netscape HTTP Cookie File\n" + body;
            }

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var writer = new StreamWriter(path, options))
            {
                writer.Write(body);
            }

            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(path);
            return new CookieFile(path, cookies, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the YouTube cookies file; running yt-dlp without cookies");
            return new CookieFile(null, null, null);
        }
    }

    /// <summary>
    /// Logs one actionable line when yt-dlp hit YouTube's "not a bot" wall:
    /// with cookies configured that almost always means they expired or were
    /// invalidated; without, that the server's IP got flagged. Throttled to
    /// once per <see cref="BotCheckWarningInterval"/> since players retry a
    /// failing stream every couple of seconds.
    /// </summary>
    public static void ReportBotCheck(string stderr, string? cookies, ILogger logger)
    {
        if (!stderr.Contains("confirm you", StringComparison.OrdinalIgnoreCase)
            || !stderr.Contains("not a bot", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastBotCheckWarningTicks);
        if (now - last < BotCheckWarningInterval.Ticks
            || Interlocked.CompareExchange(ref _lastBotCheckWarningTicks, now, last) != last)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(cookies))
        {
            logger.LogError("YouTube is blocking this server as a bot. Paste a cookies.txt from a signed-in YouTube account into the JellyTuber settings (\"YouTube cookies\").");
        }
        else
        {
            logger.LogError("YouTube is blocking this server as a bot despite the configured cookies: they have most likely expired or been invalidated (account signed out/used elsewhere). Re-export cookies.txt from a private window and paste it again into the JellyTuber settings (\"YouTube cookies\").");
        }
    }

    public void Dispose()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            PersistRotatedCookies();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not carry rotated YouTube cookies back into the configuration");
        }

        try
        {
            File.Delete(_path);
        }
        catch
        {
            // best effort - it's a temp file
        }
    }

    /// <summary>
    /// yt-dlp writes the cookie jar back on exit, including cookies YouTube
    /// rotated during the run. Throwing that copy away (as this class used
    /// to) meant the configured cookies went stale much faster than they had
    /// to. Only written back if the cookies themselves changed, if the admin
    /// hasn't pasted new ones in the meantime, and at most once per
    /// <see cref="PersistInterval"/>.
    /// </summary>
    private void PersistRotatedCookies()
    {
        if (_original is null || !File.Exists(_path))
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastPersistTicks);
        if (now - last < PersistInterval.Ticks)
        {
            return;
        }

        var updated = File.ReadAllText(_path!);
        var updatedCookies = CookieLines(updated);
        if (updatedCookies.Count == 0 || updatedCookies.SetEquals(CookieLines(_original)))
        {
            return;
        }

        var plugin = Plugin.Instance;
        if (plugin is null || Interlocked.CompareExchange(ref _lastPersistTicks, now, last) != last)
        {
            return;
        }

        lock (Plugin.ConfigLock)
        {
            if (!string.Equals(plugin.Configuration.YouTubeCookies, _original, StringComparison.Ordinal))
            {
                return; // replaced by the admin since this run started
            }

            plugin.Configuration.YouTubeCookies = updated.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";
            plugin.Save();
        }

        _logger?.LogInformation("Saved YouTube cookies rotated during a yt-dlp run back into the JellyTuber settings");
    }

    /// <summary>The cookie entries of a Netscape cookies.txt (comments dropped, but "#HttpOnly_" lines are cookies).</summary>
    private static HashSet<string> CookieLines(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => !l.StartsWith('#') || l.StartsWith("#HttpOnly_", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);
}
