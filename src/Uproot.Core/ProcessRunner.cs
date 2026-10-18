using System.Diagnostics;
using System.Text;

namespace Uproot.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>Runs a console process and captures its output as UTF-8.</summary>
public static class ProcessRunner
{
    /// <param name="onOutputLine">
    /// Called with each line of standard output as it arrives, for progress. A bare carriage
    /// return ends a line too, so each redraw of a progress bar arrives separately. Called on a
    /// thread-pool thread, one line at a time.
    /// </param>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action<string>? onOutputLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // Without this the child inherits this process's current directory, which for a
            // portable run is wherever the exe was launched from — often inside a user profile.
            // After elevating on a machine where the signed-in user is not an administrator the
            // app runs as a different account, and an inaccessible working directory fails the
            // launch with "Access is denied" that reads as though the executable were the
            // problem. The system directory is readable by every account.
            WorkingDirectory = Environment.SystemDirectory
        };

        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            stdout.AppendLine(e.Data);

            // A failure in a progress display must not lose the output or the exit code.
            try { onOutputLine?.Invoke(e.Data); }
            catch (Exception) { }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {fileName}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            // Distinguish "the caller cancelled" from "the process ran too long".
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{fileName} did not exit within {timeout.TotalSeconds:0}s.");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* the process exited between the check and the kill */ }
    }
}
