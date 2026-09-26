using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Runs a short-lived helper process (yt-dlp) to completion, reading stdout
/// AND stderr concurrently - leaving either pipe unread lets a chatty process
/// fill its buffer and block forever - and killing it if it outlives
/// <c>timeout</c> or the caller cancels. Without the kill, an aborted playback
/// request left its yt-dlp running as an orphan.
/// </summary>
internal static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// The process's exit code and output, or null if it timed out (it's
    /// killed first). Throws <see cref="OperationCanceledException"/> if
    /// <paramref name="ct"/> was cancelled (also killed first), and whatever
    /// <see cref="Process.Start()"/> throws if it can't be launched.
    /// </summary>
    public static async Task<Result?> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var process = new Process { StartInfo = psi };
        process.Start();

        // No token on the reads: killing the process closes the pipes,
        // which completes them on its own.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // exited between the timeout and the kill
            }

            ct.ThrowIfCancellationRequested();
            return null;
        }

        return new Result(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }
}
