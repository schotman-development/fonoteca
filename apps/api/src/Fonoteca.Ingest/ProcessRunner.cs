using System.ComponentModel;
using System.Diagnostics;

namespace Fonoteca.Ingest;

/// <summary>
/// Runs an external tool and collects what it said.
/// </summary>
/// <remarks>
/// The first subprocess in this application, and the pattern the other two —
/// <c>ffprobe</c> and <c>ffmpeg</c> — will follow. Four of the five things it
/// does are here because the obvious implementation is subtly wrong:
///
/// - <b>Arguments go through <c>ArgumentList</c>, never a joined string.</b>
///   .NET does the quoting; a hand-built command line meets
///   <c>Down the Road I Go (2000)</c> and <c>The House Is Rockin'</c> on the
///   first album it touches.
/// - <b>Both pipes are drained concurrently, before waiting for exit.</b> A pipe
///   holds about 64 KiB. Read stdout to the end and then stderr, and the moment
///   a tool fills stderr it blocks writing, never exits, and the wait never
///   returns — the classic deadlock, which shows up as "it works on small files".
/// - <b>Kill takes the process tree.</b> A cancelled pass must actually stop,
///   and a killed parent leaves its children running.
/// - <b>A <see cref="Win32Exception"/> from starting is a different failure from
///   a non-zero exit.</b> "The binary is not there" and "the binary rejected
///   this file" need opposite reactions, and only the caller can tell which
///   matters, so both are reported rather than merged.
///
/// Not public: this is an implementation detail of the adapters in this
/// assembly, not a general-purpose process API.
/// </remarks>
internal static class ProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> to completion, or kills it trying.</summary>
    /// <exception cref="ProcessStartFailedException">The executable could not be started.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> fired. The process is killed first.
    /// </exception>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception cause)
        {
            // ENOENT, EACCES and friends. Distinct from the process running and
            // failing, and the only failure that means "fix the configuration".
            throw new ProcessStartFailedException(fileName, cause);
        }

        // The timeout is the caller's deadline plus ours, as one token. Linked
        // rather than checked separately so a cancelled caller and an overrunning
        // tool take the same shutdown path.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            // Both pipes first, then exit. Reversing this is the deadlock above:
            // a process that has filled a pipe cannot exit until someone reads it.
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            // Which of the two tokens fired changes what this means, so say so:
            // the caller cancelling is not a fault, the tool overrunning is.
            cancellationToken.ThrowIfCancellationRequested();

            return new ProcessResult(ExitCode: -1, string.Empty, string.Empty, TimedOut: true);
        }

        return new ProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false),
            TimedOut: false);
    }

    /// <summary>Best-effort kill of the whole tree; a process that already exited is not an error.</summary>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill. Nothing to do.
        }
        catch (NotSupportedException)
        {
            // Cannot enumerate the tree on this platform. The parent is gone either way.
        }
        catch (Win32Exception)
        {
            // The OS refused. Reporting the original failure matters more.
        }
    }
}

/// <summary>What an external tool did.</summary>
internal readonly record struct ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,

    /// <summary>
    /// True when the tool was killed for overrunning. <see cref="ExitCode"/> is
    /// meaningless then, which is why this is a flag rather than a sentinel code.
    /// </summary>
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>The executable could not be started at all.</summary>
internal sealed class ProcessStartFailedException(string fileName, Exception innerException)
    : Exception($"Could not start '{fileName}'.", innerException)
{
    public string FileName { get; } = fileName;
}
