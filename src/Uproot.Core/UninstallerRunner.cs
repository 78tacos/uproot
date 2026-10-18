using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Uproot.Core;

public sealed record UninstallerRunResult
{
    public bool Started { get; init; }
    public int? ExitCode { get; init; }

    /// <summary>The person stopped waiting before the uninstaller's processes had all exited.</summary>
    public bool StoppedWaiting { get; init; }

    /// <summary>The administrator prompt was declined.</summary>
    public bool Cancelled { get; init; }

    public bool RestartRequired => ExitCode is 3010 or 1641;

    public required string Message { get; init; }
}

/// <summary>
/// Runs a program's own uninstaller and waits until it has really finished.
///
/// Waiting for the process that was started is not enough. NSIS and Inno Setup uninstallers copy
/// themselves to Temp, relaunch from there so they can delete their own folder, and exit at once;
/// a scan started then would race the actual uninstall. So the whole tree of processes descended
/// from the one started is followed, by parent process id, until every one has exited — and then,
/// because msiexec hands the real work to the Windows Installer service, until no installation is
/// in progress either.
///
/// Browsers and Explorer are not followed: plenty of uninstallers open a "sorry to see you go"
/// page on the way out, and waiting for the person to close their browser would be absurd.
/// </summary>
public static class UninstallerRunner
{
    private static readonly HashSet<string> NotFollowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "iexplore.exe",
        "explorer.exe", "msedgewebview2.exe", "WerFault.exe", "OneDrive.exe"
    };

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan InstallerServiceGrace = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs the command and waits. Cancelling <paramref name="stopWaiting"/> stops the waiting,
    /// not the uninstaller: killing an uninstaller part-way leaves a program half removed, which is
    /// worse than either state.
    /// </summary>
    public static Task<UninstallerRunResult> RunAsync(
        UninstallCommand command, IProgress<string>? progress = null, CancellationToken stopWaiting = default) =>
        RunAsync(command, progress, stopWaiting, hideWindow: false);

    /// <summary>With <paramref name="hideWindow"/> for tests, so they do not flash consoles.</summary>
    internal static async Task<UninstallerRunResult> RunAsync(
        UninstallCommand command, IProgress<string>? progress, CancellationToken stopWaiting, bool hideWindow)
    {
        var startInfo = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            // Shell execution, so an uninstaller whose manifest requires administrator rights
            // raises the UAC prompt instead of failing with "requires elevation".
            UseShellExecute = true,
            WorkingDirectory = WorkingDirectoryFor(command),
            WindowStyle = hideWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new UninstallerRunResult { Cancelled = true, Message = "The administrator prompt was declined, so nothing was uninstalled." };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new UninstallerRunResult { Message = $"The uninstaller could not be started: {ex.Message}" };
        }

        if (process is null)
        {
            return new UninstallerRunResult
            {
                Started = true,
                Message = "The uninstaller was handed to Windows but could not be followed. Scan for leftovers once it has finished."
            };
        }

        using (process)
        {
            var watcher = new ProcessTree(process);

            try
            {
                while (true)
                {
                    stopWaiting.ThrowIfCancellationRequested();

                    var alive = watcher.Refresh();
                    if (alive.Count == 0) break;

                    progress?.Report(alive.Count == 1
                        ? $"Waiting for {alive[0]} to finish…"
                        : $"Waiting for the uninstaller ({alive.Count} processes) to finish…");

                    await Task.Delay(PollInterval, stopWaiting).ConfigureAwait(false);
                }

                // msiexec returns once the service has the job, but the service may still be
                // committing. Give it a bounded while: another installation entirely could be
                // what holds the lock, and that is no reason to wait forever.
                var grace = Stopwatch.StartNew();
                while (IsInstallationInProgress() && grace.Elapsed < InstallerServiceGrace)
                {
                    progress?.Report("Waiting for Windows Installer to finish…");
                    await Task.Delay(PollInterval, stopWaiting).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return new UninstallerRunResult
                {
                    Started = true,
                    StoppedWaiting = true,
                    Message = "Stopped waiting. The uninstaller may still be running; results can change if it is."
                };
            }

            int? exitCode = null;
            try { exitCode = process.ExitCode; }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }

            return new UninstallerRunResult
            {
                Started = true,
                ExitCode = exitCode,
                Message = exitCode switch
                {
                    0 or null => "The uninstaller finished.",
                    3010 or 1641 => "The uninstaller finished. Windows needs to restart to complete it.",
                    1602 => "The uninstall was cancelled in its own window.",
                    1605 => "Windows Installer says this product is not installed.",
                    _ => $"The uninstaller finished with exit code {exitCode}."
                }
            };
        }
    }

    /// <summary>
    /// Always the system folder, never the program's own. Child processes inherit the working
    /// directory, and Windows will not delete a folder that is some process's current directory —
    /// so starting an uninstaller inside its install folder stops the copy it relaunches from Temp
    /// from ever removing that folder.
    /// </summary>
    private static string WorkingDirectoryFor(UninstallCommand command) => Environment.SystemDirectory;

    /// <summary>True while any Windows Installer transaction holds the machine-wide lock.</summary>
    public static bool IsInstallationInProgress()
    {
        try
        {
            if (!Mutex.TryOpenExisting(@"Global\_MSIExecute", out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Unable to open it means it exists.
            return true;
        }
    }

    /// <summary>A started process and every process descended from it.</summary>
    private sealed class ProcessTree
    {
        private readonly Dictionary<int, (DateTime Created, string Name)> _tracked = [];

        public ProcessTree(Process root)
        {
            var created = CreationTime(root.Id) ?? DateTime.UtcNow.AddSeconds(-5);
            _tracked[root.Id] = (created, Path.GetFileName(SafeName(root)));
        }

        /// <summary>Picks up new descendants and returns the names of the tracked processes still running.</summary>
        public IReadOnlyList<string> Refresh()
        {
            var snapshot = Snapshot();

            // A parent's children are recognised by parent id even after the parent has exited,
            // which is exactly the relaunch-from-Temp case. The creation-time check stops a
            // recycled process id adopting an unrelated process.
            var added = true;
            while (added)
            {
                added = false;

                foreach (var entry in snapshot)
                {
                    if (_tracked.ContainsKey(entry.Id) || !_tracked.TryGetValue(entry.ParentId, out var parent)) continue;
                    if (NotFollowed.Contains(entry.Name)) continue;

                    var created = CreationTime(entry.Id);
                    if (created is { } time && time < parent.Created) continue;

                    _tracked[entry.Id] = (created ?? DateTime.UtcNow, entry.Name);
                    added = true;
                }
            }

            var alive = new List<string>();
            foreach (var entry in snapshot)
            {
                if (!_tracked.TryGetValue(entry.Id, out var tracked)) continue;

                // Same id, but a different process now: the one we followed has gone.
                if (CreationTime(entry.Id) is { } created && Math.Abs((created - tracked.Created).TotalSeconds) > 2) continue;

                alive.Add(tracked.Name);
            }

            return alive;
        }

        private static string SafeName(Process process)
        {
            try { return process.ProcessName + ".exe"; }
            catch (InvalidOperationException) { return "the uninstaller"; }
        }
    }

    // ---- Native ------------------------------------------------------------------

    private readonly record struct ProcessEntry(int Id, int ParentId, string Name);

    private static List<ProcessEntry> Snapshot()
    {
        var entries = new List<ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return entries;

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return entries;

            do
            {
                entries.Add(new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return entries;
    }

    private static DateTime? CreationTime(int processId)
    {
        var handle = OpenProcess(0x1000, false, processId);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return null;

        try
        {
            return GetProcessTimes(handle, out var created, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(created)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
