using System.ComponentModel;
using System.Diagnostics;

namespace Uproot.Core.Elevation;

public sealed record RelaunchOutcome(bool Started, string Message, bool Cancelled = false);

/// <summary>
/// The process's own administrator status, and the ability to restart with it.
///
/// Uproot starts without administrator rights: listing programs and scanning a profile need
/// none, and most per-user programs uninstall without them. Program Files, HKLM, services and
/// other accounts' profiles do, and for those it restarts itself through the administrator prompt.
/// </summary>
public static class ElevationState
{
    private static readonly Lazy<bool> Elevated = new(ElevationValidation.IsCurrentProcessElevated);

    /// <summary>
    /// True when this process holds administrator rights. Cached: a process cannot gain or lose
    /// elevation during its lifetime, so re-checking would only cost a token query.
    /// </summary>
    public static bool IsElevated => Elevated.Value;

    public static string Describe() => IsElevated
        ? "Running as administrator"
        : "Running as a standard user";

    /// <summary>
    /// Restarts this executable with a UAC prompt. The caller is expected to shut down when
    /// <see cref="RelaunchOutcome.Started"/> is true, since two instances must not run at once.
    /// </summary>
    public static RelaunchOutcome RelaunchAsAdministrator(params string[] arguments)
    {
        if (IsElevated) return new RelaunchOutcome(false, "Uproot is already running as administrator.");

        var executable = ExecutablePath();
        if (executable is null)
            return new RelaunchOutcome(false, "Could not determine the path to Uproot.exe.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,   // required for the runas verb
            Verb = "runas"
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        // Tells the new instance not to wait on a mutex the old one has not released yet.
        startInfo.ArgumentList.Add("--restarted");

        try
        {
            var process = Process.Start(startInfo);

            return process is null
                ? new RelaunchOutcome(false, "The elevated instance did not start.")
                : new RelaunchOutcome(true, "Restarting with administrator rights…");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt. Not a failure worth shouting about.
            return new RelaunchOutcome(false, "The administrator prompt was cancelled.", Cancelled: true);
        }
        catch (Exception ex)
        {
            return new RelaunchOutcome(false, $"Could not restart as administrator: {ex.Message}");
        }
    }

    private static string? ExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return path;

        return Process.GetCurrentProcess().MainModule?.FileName;
    }
}
