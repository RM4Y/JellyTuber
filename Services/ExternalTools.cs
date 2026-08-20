using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Provisions the yt-dlp and Deno binaries the plugin needs to resolve
/// playback, so nothing has to be pre-installed on the host and there is no
/// path setting for the user to get wrong. Both are downloaded straight from
/// their official GitHub releases into the plugin's data folder on first
/// use, checksum-verified, marked executable, and reused after that - with a
/// periodic best-effort refresh so yt-dlp keeps up with YouTube's changes.
///
/// yt-dlp ships fully self-contained "standalone" builds per platform (no
/// system Python required), which is what makes bundling it this way
/// practical. Deno backs yt-dlp's <c>--js-runtimes</c> flag, required to
/// solve YouTube's n-challenge so 1080p+ formats resolve at all; when the
/// host architecture has no Deno build (e.g. 32-bit ARM), <see cref="EnsureDenoAsync"/>
/// returns null and callers simply omit the flag.
/// </summary>
public static class ExternalTools
{
    private const string YtDlpBaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download";
    private const string DenoBaseUrl = "https://github.com/denoland/deno/releases/latest/download";

    /// <summary>How often an already-downloaded binary is re-checked against the latest release.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private static readonly SemaphoreSlim YtDlpLock = new(1, 1);
    private static readonly SemaphoreSlim DenoLock = new(1, 1);

    private static string BinDir => Path.Combine(Plugin.Instance!.DataFolderPath, "bin");

    private static string YtDlpLocalFileName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    private static string DenoLocalFileName => OperatingSystem.IsWindows() ? "deno.exe" : "deno";

    /// <summary>Returns the path to a ready-to-run yt-dlp binary, downloading/updating it first if needed. Null only if the download failed and no prior copy exists.</summary>
    public static async Task<string?> EnsureYtDlpAsync(IHttpClientFactory httpClientFactory, ILogger logger, CancellationToken ct)
    {
        var path = Path.Combine(BinDir, YtDlpLocalFileName);

        if (File.Exists(path) && !IsUpdateDue(path))
        {
            return path;
        }

        await YtDlpLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && !IsUpdateDue(path))
            {
                return path;
            }

            var haveExisting = File.Exists(path);
            Directory.CreateDirectory(BinDir);
            var tmp = path + ".download";

            try
            {
                using var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromMinutes(2);

                var asset = YtDlpAssetName();
                await DownloadToFileAsync(http, $"{YtDlpBaseUrl}/{asset}", tmp, ct).ConfigureAwait(false);
                await VerifyChecksumAsync(http, $"{YtDlpBaseUrl}/SHA2-256SUMS", asset, tmp, logger, ct).ConfigureAwait(false);
                MakeExecutable(tmp);
                File.Move(tmp, path, overwrite: true);
                TouchUpdateMarker(path);
                logger.LogInformation("yt-dlp ready at {Path} ({Asset})", path, asset);
                return path;
            }
            catch (Exception ex)
            {
                if (haveExisting)
                {
                    logger.LogDebug(ex, "yt-dlp update check failed; keeping existing binary at {Path}", path);
                    return path;
                }

                logger.LogError(ex, "Failed to download yt-dlp");
                return null;
            }
            finally
            {
                TryDelete(tmp);
            }
        }
        finally
        {
            YtDlpLock.Release();
        }
    }

    /// <summary>Returns the path to a ready-to-run Deno binary, or null if this host's OS/architecture has no Deno build, or the download failed and no prior copy exists.</summary>
    public static async Task<string?> EnsureDenoAsync(IHttpClientFactory httpClientFactory, ILogger logger, CancellationToken ct)
    {
        var asset = DenoAssetName();
        if (asset is null)
        {
            return null;
        }

        var path = Path.Combine(BinDir, DenoLocalFileName);

        if (File.Exists(path) && !IsUpdateDue(path))
        {
            return path;
        }

        await DenoLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && !IsUpdateDue(path))
            {
                return path;
            }

            var haveExisting = File.Exists(path);
            Directory.CreateDirectory(BinDir);
            var zipPath = path + ".zip.download";
            var extractDir = path + ".extract";

            try
            {
                using var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromMinutes(3);

                await DownloadToFileAsync(http, $"{DenoBaseUrl}/{asset}", zipPath, ct).ConfigureAwait(false);
                await VerifyChecksumAsync(http, $"{DenoBaseUrl}/{asset}.sha256sum", null, zipPath, logger, ct).ConfigureAwait(false);

                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, recursive: true);
                }

                ZipFile.ExtractToDirectory(zipPath, extractDir);

                var extracted = Directory
                    .GetFiles(extractDir, DenoLocalFileName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (extracted is null)
                {
                    throw new InvalidOperationException("Deno archive did not contain a deno binary");
                }

                MakeExecutable(extracted);
                File.Move(extracted, path, overwrite: true);
                TouchUpdateMarker(path);
                logger.LogInformation("Deno ready at {Path} ({Asset})", path, asset);
                return path;
            }
            catch (Exception ex)
            {
                if (haveExisting)
                {
                    logger.LogDebug(ex, "Deno update check failed; keeping existing binary at {Path}", path);
                    return path;
                }

                logger.LogWarning(ex, "Failed to provision Deno; formats needing YouTube's n-challenge (1080p+) may fail to resolve");
                return null;
            }
            finally
            {
                TryDelete(zipPath);
                if (Directory.Exists(extractDir))
                {
                    try
                    {
                        Directory.Delete(extractDir, recursive: true);
                    }
                    catch
                    {
                        // best effort
                    }
                }
            }
        }
        finally
        {
            DenoLock.Release();
        }
    }

    /// <summary>
    /// Splits a space-separated yt-dlp extra-args string, dropping any
    /// <c>--js-runtimes &lt;value&gt;</c> pair. The plugin always wires its own
    /// <c>--js-runtimes deno:&lt;bundled path&gt;</c> in right after this
    /// (see <see cref="EnsureDenoAsync"/> callers); yt-dlp keeps only the
    /// last occurrence of a repeated flag, so a leftover or manually typed
    /// one here - most likely pointing at a path that no longer exists, now
    /// that Deno is bundled - would silently win and break n-challenge
    /// resolution instead of being harmless. Also used once at plugin
    /// startup to clean a config saved by an older version of this plugin.
    /// </summary>
    public static IEnumerable<string> StripJsRuntimesArg(string? extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs))
        {
            yield break;
        }

        var tokens = extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], "--js-runtimes", StringComparison.Ordinal))
            {
                i++; // also drop its value, if any
                continue;
            }

            yield return tokens[i];
        }
    }

    private static string YtDlpAssetName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "yt-dlp.exe";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }

        // Linux (and anything else POSIX-y): pick the matching standalone build.
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "yt-dlp_linux_aarch64",
            Architecture.Arm => "yt-dlp_linux_armv7l",
            _ => "yt-dlp_linux"
        };
    }

    /// <summary>Null when this OS/architecture combination has no official Deno build (e.g. 32-bit ARM).</summary>
    private static string? DenoAssetName()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => null
        };

        if (arch is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return $"deno-{arch}-pc-windows-msvc.zip";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"deno-{arch}-apple-darwin.zip";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"deno-{arch}-unknown-linux-gnu.zip";
        }

        return null;
    }

    private static async Task DownloadToFileAsync(HttpClient http, string url, string destPath, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies <paramref name="downloadedPath"/> against a published checksum, when one can be
    /// fetched. <paramref name="assetFileName"/> selects the matching line in a combined SUMS
    /// file (yt-dlp); pass null when <paramref name="checksumUrl"/> already points at a
    /// single-file checksum (Deno's <c>*.sha256sum</c> assets). Best-effort: a checksum file
    /// that can't be fetched is not treated as a verification failure, only an actual mismatch is.
    /// </summary>
    private static async Task VerifyChecksumAsync(HttpClient http, string checksumUrl, string? assetFileName, string downloadedPath, ILogger logger, CancellationToken ct)
    {
        string? expected;
        try
        {
            var text = await http.GetStringAsync(checksumUrl, ct).ConfigureAwait(false);
            expected = ParseChecksum(text, assetFileName);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not fetch checksum from {Url}; skipping verification", checksumUrl);
            return;
        }

        if (expected is null)
        {
            return;
        }

        var actual = await Sha256HexAsync(downloadedPath, ct).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Checksum mismatch for '{downloadedPath}' (expected {expected}, got {actual})");
        }
    }

    private static string? ParseChecksum(string sumsFileText, string? assetFileName)
    {
        foreach (var rawLine in sumsFileText.Split('\n'))
        {
            var parts = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            // sha256sum-style lines: "<hash>  <filename>" (filename optionally prefixed '*' for binary mode).
            if (assetFileName is null || parts[^1].TrimStart('*').Equals(assetFileName, StringComparison.Ordinal))
            {
                return parts[0];
            }
        }

        return null;
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string MarkerPath(string binaryPath) => binaryPath + ".checked";

    private static bool IsUpdateDue(string binaryPath)
    {
        var marker = MarkerPath(binaryPath);
        if (!File.Exists(marker))
        {
            return true;
        }

        return DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) > UpdateCheckInterval;
    }

    private static void TouchUpdateMarker(string binaryPath)
    {
        try
        {
            File.WriteAllText(MarkerPath(binaryPath), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch
        {
            // best effort - worst case we just re-check next time
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
