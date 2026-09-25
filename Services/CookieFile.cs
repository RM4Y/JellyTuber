using System;
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
/// interleave those writes and corrupt it. Dispose after the process exits.
/// </summary>
public sealed class CookieFile : IDisposable
{
    private static readonly TimeSpan BotCheckWarningInterval = TimeSpan.FromMinutes(10);

    private static long _lastBotCheckWarningTicks;

    private readonly string? _path;

    private CookieFile(string? path) => _path = path;

    /// <summary>
    /// Adds <c>--cookies &lt;temp file&gt;</c> to <paramref name="psi"/> when
    /// cookies are configured; otherwise does nothing. Never throws - a
    /// failure to write the file just means the run goes without cookies.
    /// </summary>
    public static CookieFile Apply(ProcessStartInfo psi, string? cookies, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(cookies))
        {
            return new CookieFile(null);
        }

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "jellytuber-cookies");
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

            File.WriteAllText(path, body);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(path);
            return new CookieFile(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the YouTube cookies file; running yt-dlp without cookies");
            return new CookieFile(null);
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
            File.Delete(_path);
        }
        catch
        {
            // best effort - it's a temp file
        }
    }
}
