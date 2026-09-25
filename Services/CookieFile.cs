using System;
using System.Diagnostics;
using System.IO;
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
