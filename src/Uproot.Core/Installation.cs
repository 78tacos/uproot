using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Uproot.Core.Elevation;

namespace Uproot.Core;

/// <summary>One file an install copies in, by name, with a way to read it.</summary>
public sealed record InstallSource(string Name, Func<Stream> Open);

/// <summary>What an install or removal did.</summary>
public sealed record InstallOutcome(bool Succeeded, string Message)
{
    /// <summary>What did not work in a run that otherwise did: the shortcut, the PATH entry.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public int ExitCode => !Succeeded ? Installation.ExitCodes.Failed : Notes.Count > 0 ? Installation.ExitCodes.DoneWithNotes : Installation.ExitCodes.Done;
}

/// <summary>The copy installed for everyone, as found on this PC.</summary>
public sealed record InstalledCopy(string ExecutablePath, BuildInfo? Build);

/// <summary>
/// Where an install puts each of its parts. <see cref="ForThisMachine"/> is the real one; tests
/// supply a temporary folder and keys under HKCU, so everything but the administrator check can
/// be run without rights.
/// </summary>
public sealed record InstallLayout
{
    public required string Folder { get; init; }
    public required string ShortcutPath { get; init; }
    public required RegistryKey UninstallHive { get; init; }
    public required string UninstallKeyPath { get; init; }
    public required RegistryKey EnvironmentHive { get; init; }
    public required string EnvironmentKeyPath { get; init; }

    /// <summary>Tell running programs PATH changed. Off in tests: the broadcast can wait on hung windows.</summary>
    public bool Broadcast { get; init; }

    public string AppPath => Path.Combine(Folder, Installation.AppFile);
    public string CliPath => Path.Combine(Folder, Installation.CliFile);

    public static InstallLayout ForThisMachine { get; } = new()
    {
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Uproot"),
        ShortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Uproot.lnk"),
        UninstallHive = Registry.LocalMachine,
        UninstallKeyPath = Installation.UninstallKeyPath,
        EnvironmentHive = Registry.LocalMachine,
        EnvironmentKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
        Broadcast = true
    };
}

/// <summary>
/// Installing Uproot for everyone, and removing it again.
///
/// There is one install: Program Files, for every account. The reason to install at all is that
/// Uproot often restarts itself with administrator rights, and a copy in Downloads or on the
/// Desktop is a file any program running as you could replace before you answer the prompt.
/// Program Files can only be changed by administrators, so the copy there is the one that runs
/// elevated. A per-user install would sit in a folder you can change, and solve nothing.
///
/// The install adds four things, and removing it takes away exactly those four: the folder with
/// Uproot.exe and uproot.com, an Apps &amp; Features entry, a Start menu shortcut for all users,
/// and the folder on the machine PATH, so "uproot" works in any shell. Backups of past removals
/// are not part of the install and are never touched by it.
/// </summary>
public static class Installation
{
    public const string AppFile = "Uproot.exe";
    public const string CliFile = "uproot.com";
    public const string LogFile = "setup.log";

    internal const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uproot";

    /// <summary>The internal switches the elevated half of an install or removal runs with.</summary>
    public const string InstallElevatedSwitch = "--install-elevated";
    public const string UninstallElevatedSwitch = "--uninstall-elevated";
    public const string ThenRestartSwitch = "--then-restart";

    /// <summary>
    /// What the elevated half reports through its exit code. It has no other way back to the
    /// process that started it, which may be running as a different account.
    /// </summary>
    public static class ExitCodes
    {
        public const int Done = 0;
        public const int DoneWithNotes = 1;
        public const int NotElevated = 2;
        public const int BadSource = 64;
        public const int Failed = 70;

        public static string Describe(int code, bool install) => code switch
        {
            Done => install ? $"Uproot is installed for everyone in {InstallLayout.ForThisMachine.Folder}." : "Uproot was removed.",
            DoneWithNotes => install
                ? $"Uproot is installed in {InstallLayout.ForThisMachine.Folder}, but the Start menu shortcut or the PATH entry could not be added."
                : "Uproot was removed, but part of it could not be: see Apps & Features and the Start menu.",
            NotElevated => "The elevated step did not get administrator rights.",
            BadSource => "This copy of Uproot cannot be installed: it is not a complete published build.",
            _ => install ? "Installing Uproot failed." : "Removing Uproot failed."
        };
    }

    public static string Folder => InstallLayout.ForThisMachine.Folder;

    // ---- What is there ------------------------------------------------------

    /// <summary>The copy installed for everyone, or null when there is none.</summary>
    public static InstalledCopy? FindInstalled() => FindInstalled(InstallLayout.ForThisMachine);

    internal static InstalledCopy? FindInstalled(InstallLayout layout)
    {
        try
        {
            using var key = layout.UninstallHive.OpenSubKey(layout.UninstallKeyPath);
            if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }

        return File.Exists(layout.AppPath) ? new InstalledCopy(layout.AppPath, BuildInfo.FromFile(layout.AppPath)) : null;
    }

    /// <summary>The Uproot.exe this process belongs to: itself, or the one beside uproot.com.</summary>
    public static string? CurrentAppPath()
    {
        var process = Environment.ProcessPath;
        if (process is not null && Path.GetFileName(process).Equals(AppFile, StringComparison.OrdinalIgnoreCase)) return process;

        var beside = Path.Combine(AppContext.BaseDirectory, AppFile);
        return File.Exists(beside) ? beside : null;
    }

    /// <summary>True when this process runs from the installed folder.</summary>
    public static bool IsRunningInstalled() =>
        Environment.ProcessPath is { } process && PathRules.SamePath(Path.GetDirectoryName(process), Folder);

    /// <summary>Whether <paramref name="program"/> is Uproot's own Apps &amp; Features entry.</summary>
    public static bool IsThisProgram(InstalledProgram program) =>
        program.Key.Hive == RegistryHive.LocalMachine
        && program.Key.SubKey.Equals(UninstallKeyPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why the files in <paramref name="folder"/> cannot be installed, or null when they can: both
    /// executables there, published as single files, and the same build.
    /// </summary>
    public static string? ProblemWithFolder(string folder)
    {
        folder = folder.TrimEnd(Path.DirectorySeparatorChar);

        foreach (var name in new[] { AppFile, CliFile })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) return $"{name} is not in {folder}. Install from a folder holding both {AppFile} and {CliFile}, as the release zip does.";
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return $"{path} is a link, not a file.";
        }

        // A build run from Visual Studio or dotnet build is an apphost and a pile of DLLs beside
        // it; copying the two executables alone would install something that cannot start.
        if (File.Exists(Path.Combine(folder, "Uproot.dll")) || File.Exists(Path.Combine(folder, "uproot-cli.dll")))
            return "This is a development build. Install from a published build (publish.ps1, or a release zip).";

        return ProblemWithBuilds(Path.Combine(folder, AppFile), Path.Combine(folder, CliFile));
    }

    private static string? ProblemWithBuilds(string app, string cli)
    {
        var appBuild = BuildInfo.FromFile(app);
        var cliBuild = BuildInfo.FromFile(cli);

        if (appBuild is null || cliBuild is null) return "The version of Uproot.exe or uproot.com could not be read.";
        if (!appBuild.IsSameBuild(cliBuild)) return $"Uproot.exe is {appBuild.Display} but uproot.com is {cliBuild.Display}; a half-copied folder is not installed.";
        return null;
    }

    /// <summary>The two executables in <paramref name="folder"/>, to install from.</summary>
    public static IReadOnlyList<InstallSource> FromFolder(string folder) =>
    [
        new(AppFile, () => File.OpenRead(Path.Combine(folder, AppFile))),
        new(CliFile, () => File.OpenRead(Path.Combine(folder, CliFile)))
    ];

    // ---- Installing -----------------------------------------------------------

    /// <summary>Installs for everyone. Must run elevated.</summary>
    public static InstallOutcome InstallForEveryone(IReadOnlyList<InstallSource> files)
    {
        if (!ElevationValidation.IsCurrentProcessElevated())
            return new InstallOutcome(false, "Installing for everyone needs administrator rights.");

        return Install(files, InstallLayout.ForThisMachine);
    }

    /// <summary>
    /// Copies the files in and registers them. With no files — run from the installed folder
    /// itself — it only puts the registration, shortcut and PATH entry right again.
    /// </summary>
    internal static InstallOutcome Install(IReadOnlyList<InstallSource> files, InstallLayout layout)
    {
        var folder = layout.Folder;

        try
        {
            if (Directory.Exists(folder) && new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return new InstallOutcome(false, $"{folder} is a link, so nothing was installed into it.");

            Directory.CreateDirectory(folder);
            RemoveReplaced(folder);

            if (files.Count > 0 && Stage(files, folder) is { } problem)
                return new InstallOutcome(false, problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InstallOutcome(false, $"Could not write to {folder}: {ex.Message}");
        }

        if (!File.Exists(layout.AppPath))
            return new InstallOutcome(false, $"{layout.AppPath} is missing, so there is nothing to register.");

        var build = BuildInfo.FromFile(layout.AppPath);

        if (Register(layout, build) is { } registryProblem)
            return new InstallOutcome(false, registryProblem);

        var notes = new List<string>();
        if (CreateShortcut(layout) is { } shortcutProblem) notes.Add(shortcutProblem);
        if (AddToPath(layout) is { } pathProblem) notes.Add(pathProblem);

        var message = $"Installed {build?.Display ?? "Uproot"} for everyone in {folder}.";
        return new InstallOutcome(true, message) { Notes = notes };
    }

    /// <summary>
    /// Writes each file beside its target as .new, checks the two are one build, then moves them
    /// into place. A file in use — the installed copy, still open — is renamed aside first, which
    /// Windows allows for a running program, and deleted at the next restart.
    /// </summary>
    private static string? Stage(IReadOnlyList<InstallSource> files, string folder)
    {
        var staged = new List<(string New, string Target)>();

        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(folder, file.Name);
                var next = target + ".new";

                using (var input = file.Open())
                using (var output = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }

                staged.Add((next, target));
            }

            var app = staged.FirstOrDefault(s => Path.GetFileName(s.Target).Equals(AppFile, StringComparison.OrdinalIgnoreCase));
            var cli = staged.FirstOrDefault(s => Path.GetFileName(s.Target).Equals(CliFile, StringComparison.OrdinalIgnoreCase));
            if (app.New is null || cli.New is null) return $"An install needs both {AppFile} and {CliFile}.";

            if (ProblemWithBuilds(app.New, cli.New) is { } mismatch) return mismatch;

            foreach (var (next, target) in staged) MoveIn(next, target);
            staged.Clear();
            return null;
        }
        finally
        {
            foreach (var (next, _) in staged)
            {
                try { File.Delete(next); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void MoveIn(string next, string target)
    {
        try
        {
            File.Move(next, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var aside = $"{target}.replaced-{DateTime.UtcNow.Ticks}";
            File.Move(target, aside);
            File.Move(next, target);
            DeleteAtRestart(aside);
        }
    }

    /// <summary>Files an earlier update renamed aside, now that nothing should be running from them.</summary>
    private static void RemoveReplaced(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.replaced-*"))
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string? Register(InstallLayout layout, BuildInfo? build)
    {
        try
        {
            using var key = layout.UninstallHive.CreateSubKey(layout.UninstallKeyPath, writable: true);
            if (key is null) return "Could not create the Apps & Features entry.";

            var app = Quote(layout.AppPath);

            key.SetValue("DisplayName", "Uproot");
            key.SetValue("Publisher", "Uproot");
            key.SetValue("DisplayVersion", build?.DottedVersion ?? BuildInfo.Current.DottedVersion);
            key.SetValue("InstallLocation", layout.Folder);
            key.SetValue("DisplayIcon", layout.AppPath + ",0");
            key.SetValue("UninstallString", $"{app} self-uninstall");
            key.SetValue("QuietUninstallString", $"{app} self-uninstall --quiet");
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

            // Modify and Repair would need an installer to run; reinstalling is the repair.
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            var bytes = new[] { layout.AppPath, layout.CliPath }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);

            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"Could not add Uproot to Apps & Features: {ex.Message}";
        }
    }

    private static string? CreateShortcut(InstallLayout layout)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(layout.ShortcutPath)!);

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return "Could not add the Start menu shortcut: Windows Script Host is not available.";

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(layout.ShortcutPath);
            shortcut.TargetPath = layout.AppPath;
            shortcut.WorkingDirectory = layout.Folder;
            shortcut.Description = "Uninstall programs, then remove what their uninstallers leave behind";
            shortcut.IconLocation = layout.AppPath + ",0";
            shortcut.Save();
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not add the Start menu shortcut: {ex.Message}";
        }
    }

    /// <summary>Adds the folder to the end of PATH, unless it is there already, keeping the value's type.</summary>
    private static string? AddToPath(InstallLayout layout)
    {
        try
        {
            using var key = layout.EnvironmentHive.CreateSubKey(layout.EnvironmentKeyPath, writable: true);
            var current = key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var kind = current is null ? RegistryValueKind.ExpandString : key.GetValueKind("Path");

            var updated = EnvironmentPath.With(current, layout.Folder);
            if (updated == current) return null;

            key.SetValue("Path", updated, kind);
            if (layout.Broadcast) EnvironmentPath.Broadcast();
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"Could not add {layout.Folder} to PATH: {ex.Message}";
        }
    }

    // ---- Removing -------------------------------------------------------------

    /// <summary>Removes the install for everyone. Must run elevated.</summary>
    public static InstallOutcome UninstallForEveryone()
    {
        if (!ElevationValidation.IsCurrentProcessElevated())
            return new InstallOutcome(false, "Removing Uproot needs administrator rights.");

        return Uninstall(InstallLayout.ForThisMachine, cleanUpAfterExit: true);
    }

    /// <summary>
    /// Takes away what <see cref="Install"/> added, and nothing else: the entry, the shortcut, the
    /// PATH entry, and the folder's own files. Files still in use — this process, when it runs
    /// from there — are deleted once it exits, by a PowerShell loop started for the purpose.
    /// </summary>
    internal static InstallOutcome Uninstall(InstallLayout layout, bool cleanUpAfterExit)
    {
        var notes = new List<string>();

        try
        {
            layout.UninstallHive.DeleteSubKeyTree(layout.UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new InstallOutcome(false, $"Could not remove the Apps & Features entry: {ex.Message}");
        }

        try
        {
            if (File.Exists(layout.ShortcutPath)) File.Delete(layout.ShortcutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not remove the Start menu shortcut: {ex.Message}");
        }

        if (RemoveFromPath(layout) is { } pathProblem) notes.Add(pathProblem);

        var folder = layout.Folder;
        var waiting = false;

        if (Directory.Exists(folder) && !new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            var left = DeleteOwnFiles(folder);

            if (left.Count > 0 && cleanUpAfterExit)
            {
                waiting = StartCleanupAfterExit(folder);
                if (!waiting) notes.Add($"{string.Join(", ", left.Select(Path.GetFileName))} could not be deleted from {folder}.");
            }
            else if (left.Count > 0)
            {
                notes.Add($"{string.Join(", ", left.Select(Path.GetFileName))} could not be deleted from {folder}.");
            }
        }

        var message = waiting
            ? $"Uproot was removed. Its files in {folder} go as soon as Uproot closes."
            : "Uproot was removed.";

        return new InstallOutcome(true, message + " Backups of past removals were kept.") { Notes = notes };
    }

    private static string? RemoveFromPath(InstallLayout layout)
    {
        try
        {
            using var key = layout.EnvironmentHive.OpenSubKey(layout.EnvironmentKeyPath, writable: true);
            if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string current) return null;

            var updated = EnvironmentPath.Without(current, layout.Folder, out var removed);
            if (removed == 0) return null;

            key.SetValue("Path", updated, key.GetValueKind("Path"));
            if (layout.Broadcast) EnvironmentPath.Broadcast();
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"Could not take {layout.Folder} out of PATH: {ex.Message}";
        }
    }

    /// <summary>The files an install puts in its folder, by name, and any it renamed aside.</summary>
    private static IEnumerable<string> OwnFiles(string folder) =>
        new[] { AppFile, CliFile, LogFile, AppFile + ".new", CliFile + ".new" }
            .Select(n => Path.Combine(folder, n))
            .Concat(Directory.EnumerateFiles(folder, "*.replaced-*"));

    /// <summary>Deletes the folder's own files, and the folder once nothing else is in it. Returns what is left of them.</summary>
    private static List<string> DeleteOwnFiles(string folder)
    {
        var left = new List<string>();

        foreach (var file in OwnFiles(folder).ToList())
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(file);
            }
        }

        if (left.Count == 0)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return left;
    }

    /// <summary>
    /// Starts Windows PowerShell, hidden, to delete the folder's own files once nothing is running
    /// from them, trying each second for ten minutes. Cmdlets only, so it works under constrained
    /// language mode too. The folder is Program Files\Uproot, not anything passed in.
    /// </summary>
    private static bool StartCleanupAfterExit(string folder)
    {
        var literal = "'" + folder.Replace("'", "''") + "'";
        var script = $$"""
            $dir = {{literal}}
            $names = @('{{AppFile}}', '{{CliFile}}', '{{LogFile}}', '{{AppFile}}.new', '{{CliFile}}.new')
            for ($i = 0; $i -lt 600; $i++) {
                $left = 0
                foreach ($n in $names) {
                    $p = Join-Path $dir $n
                    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue; if (Test-Path -LiteralPath $p) { $left++ } }
                }
                Get-ChildItem -LiteralPath $dir -Filter '*.replaced-*' -File -ErrorAction SilentlyContinue | ForEach-Object {
                    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                    if (Test-Path -LiteralPath $_.FullName) { $left++ }
                }
                if ($left -eq 0) {
                    if (-not (Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue }
                    break
                }
                Start-Sleep -Seconds 1
            }
            """;

        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell)) return false;

        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory
        };

        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                     "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ---- The elevated half ------------------------------------------------------

    /// <summary>
    /// Runs the elevated half and waits for it: <paramref name="executable"/> started through the
    /// administrator prompt with <see cref="InstallElevatedSwitch"/> or
    /// <see cref="UninstallElevatedSwitch"/>. For an install that is this copy, since it is the
    /// one being installed; for a removal it is the installed copy, which only administrators can
    /// change, never a copy somewhere a standard account can.
    /// </summary>
    /// <param name="thenRestart">
    /// After installing, the elevated half starts the installed copy, which inherits its rights:
    /// restarting as administrator by way of an install then takes one prompt, not two.
    /// </param>
    public static InstallOutcome RunElevated(bool install, string executable, bool quiet, bool thenRestart = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.SystemDirectory
        };

        startInfo.ArgumentList.Add(install ? InstallElevatedSwitch : UninstallElevatedSwitch);
        if (quiet) startInfo.ArgumentList.Add("--quiet");
        if (install && thenRestart) startInfo.ArgumentList.Add(ThenRestartSwitch);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new InstallOutcome(false, "The elevated step did not start.");

            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
                return new InstallOutcome(false, "The elevated step did not finish within five minutes.");

            var code = process.ExitCode;
            var message = ExitCodes.Describe(code, install);

            return new InstallOutcome(code is ExitCodes.Done or ExitCodes.DoneWithNotes, message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new InstallOutcome(false, "The administrator prompt was cancelled, so nothing changed.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new InstallOutcome(false, $"Could not start the elevated step: {ex.Message}");
        }
    }

    /// <summary>Appends a line to setup.log in the installed folder, which only administrators can write to.</summary>
    public static void Log(string line)
    {
        try
        {
            var folder = Folder;
            if (!Directory.Exists(folder) || new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

            File.AppendAllText(Path.Combine(folder, LogFile), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Quote(string value) => "\"" + value + "\"";

    private const uint DelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string? replacement, uint flags);

    /// <summary>
    /// Only for the uniquely named files renamed aside: a pending delete of Uproot.exe itself
    /// would also delete a copy installed again before the restart.
    /// </summary>
    private static void DeleteAtRestart(string path) => MoveFileExW(path, null, DelayUntilReboot);
}
