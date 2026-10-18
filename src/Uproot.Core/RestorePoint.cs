using System.Diagnostics;
using System.Text;

namespace Uproot.Core;

public sealed record RestorePointOutcome(bool Created, string Message);

/// <summary>
/// Creates a System Restore point before uninstalling, the way Revo and Windows Installer do.
///
/// Goes through Windows PowerShell's Checkpoint-Computer rather than WMI directly so no
/// management package is needed. Windows allows one restore point per 24 hours by default and
/// silently declines the rest, so the count is compared before and after to report what really
/// happened rather than assuming success.
/// </summary>
public static class RestorePoint
{
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        try {
            $before = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue).Count
            Checkpoint-Computer -Description $env:UPROOT_RESTORE_POINT -RestorePointType APPLICATION_UNINSTALL -WarningVariable warnings -WarningAction SilentlyContinue
            $after = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue).Count
            if ($after -gt $before) { 'CREATED' }
            elseif ($warnings) { 'SKIPPED ' + $warnings[0] }
            else { 'SKIPPED Windows did not create one. System Restore may be turned off for this drive.' }
        } catch {
            'FAILED ' + $_.Exception.Message
        }
        """;

    public static async Task<RestorePointOutcome> CreateAsync(string description, CancellationToken ct = default)
    {
        if (!Elevation.ElevationValidation.IsCurrentProcessElevated())
            return new RestorePointOutcome(false, "Creating a restore point needs administrator rights.");

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return new RestorePointOutcome(false, "Windows PowerShell is not available.");

        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory
        };

        // The description travels in the environment, never inside the script text, so a
        // program name with quotes in it cannot become PowerShell.
        startInfo.Environment["UPROOT_RESTORE_POINT"] = Truncate(description, 250);

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

            if (line.StartsWith("CREATED", StringComparison.Ordinal))
                return new RestorePointOutcome(true, "Created a restore point.");

            if (line.StartsWith("SKIPPED ", StringComparison.Ordinal))
                return new RestorePointOutcome(false, "No restore point: " + line["SKIPPED ".Length..]);

            if (line.StartsWith("FAILED ", StringComparison.Ordinal))
                return new RestorePointOutcome(false, "Could not create a restore point: " + line["FAILED ".Length..]);

            return new RestorePointOutcome(false, "Could not create a restore point.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return new RestorePointOutcome(false, $"Could not create a restore point: {ex.Message}");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
