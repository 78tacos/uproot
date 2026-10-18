using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Uproot.Core;

/// <summary>
/// Microsoft Store and other MSIX apps installed for this account, which Apps &amp; Features lists
/// but the Uninstall keys do not.
///
/// Windows removes a packaged app completely — its install folder and its data folder under
/// AppData\Local\Packages both belong to Windows — so there is no uninstaller to analyse. What is
/// left for the leftover scan is whatever the app wrote outside its package: firewall rules,
/// startup entries, a folder of its own in Documents.
/// </summary>
public static partial class StoreApps
{
    private const string RepositoryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>
    /// Package full names are "Name_Version_Architecture_ResourceId_PublisherId": letters, digits,
    /// dots, hyphens, underscores and a tilde for a neutral resource. Anything else is refused
    /// before it goes near a command line.
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z0-9.\-_~]{3,300}\z")]
    private static partial Regex FullNamePattern();

    public static bool IsValidFullName(string? name) => name is not null && FullNamePattern().IsMatch(name);

    /// <summary>
    /// Packages Windows and other programs depend on, which Get-AppxPackage lists as removable
    /// but which Settings does not offer to uninstall: the Store, winget and its sources, the
    /// Windows App SDK runtime, the widgets platform. They are left out of the list.
    /// </summary>
    private static readonly string[] InfrastructurePrefixes =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.Winget.",
        "Microsoft.WindowsAppRuntime", "MicrosoftCorporationII.WinAppRuntime", "Microsoft.WidgetsPlatformRuntime",
        "Microsoft.Services.Store.", "Microsoft.StoreExperienceHost", "Microsoft.NET.", "Microsoft.VCLibs", "Microsoft.UI.Xaml"
    ];

    internal static bool IsInfrastructure(string packageName) =>
        InfrastructurePrefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // One unreadable package must not empty the list, so errors are reported per package rather
    // than stopping the script; and setting the output encoding is not allowed in constrained
    // language mode, where the list still works without it.
    private const string ListScript = """
        $ErrorActionPreference = 'Continue'
        try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
        @(Get-AppxPackage -PackageTypeFilter Main -ErrorAction SilentlyContinue |
            Where-Object { -not $_.NonRemovable -and -not $_.IsFramework -and [string]$_.SignatureKind -ne 'System' } |
            ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    FullName = $_.PackageFullName
                    FamilyName = $_.PackageFamilyName
                    Version = [string]$_.Version
                    InstallLocation = $_.InstallLocation
                    SignatureKind = [string]$_.SignatureKind
                }
            }) | ConvertTo-Json -Compress -Depth 2
        """;

    private sealed record PackageRow(string? Name, string? FullName, string? FamilyName, string? Version, string? InstallLocation, string? SignatureKind);

    /// <summary>
    /// The removable apps installed for the account this runs as. Takes a second or two: it asks
    /// PowerShell. Throws with the reason when the list cannot be read at all, rather than passing
    /// an empty list off as "no Store apps".
    /// </summary>
    public static async Task<IReadOnlyList<InstalledProgram>> ReadAllAsync(CancellationToken ct = default)
    {
        var (exit, output, error) = await RunPowerShellAsync(ListScript, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

        var json = output.Trim();
        if (exit != 0 && json.Length == 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows PowerShell could not list them." : error.Trim());

        if (json.Length == 0) return [];

        List<PackageRow>? rows;
        try
        {
            // ConvertTo-Json writes a lone object, not an array, when there is only one.
            rows = json.StartsWith('[')
                ? JsonSerializer.Deserialize<List<PackageRow>>(json)
                : JsonSerializer.Deserialize<PackageRow>(json) is { } single ? [single] : [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Windows PowerShell's list could not be read: {ex.Message}");
        }

        var apps = new System.Collections.Concurrent.ConcurrentBag<InstalledProgram>();

        // Resolving a display name loads the package's resources, which is slow one at a time.
        Parallel.ForEach(rows ?? [], new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, row =>
        {
            if (!IsValidFullName(row.FullName) || row.Name is null || IsInfrastructure(row.Name)) return;

            var manifest = ReadManifest(row.InstallLocation, row.FullName!, row.Name);

            // A sparse package only lends a desktop program an identity for its shell extensions;
            // Apps & Features does not list it, and removing it would not remove the program.
            if (manifest.IsSparse) return;

            // Get-AppxPackage lists this process's account's packages: tagged when that is not
            // the person signed in.
            apps.Add(ProgramOwner.Tag(new InstalledProgram
            {
                Key = RegistryLocation.CurrentUser($@"{RepositoryPath}\{row.FullName}"),
                PackageFullName = row.FullName,
                DisplayName = manifest.DisplayName,
                Publisher = manifest.Publisher,
                DisplayVersion = row.Version,
                InstallLocation = row.InstallLocation,
                InstallDate = InstallDate(row.InstallLocation),
                Scope = ProgramScope.User,
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PackageFamilyName"] = row.FamilyName ?? string.Empty,
                    ["SignatureKind"] = row.SignatureKind ?? string.Empty
                }
            }));
        });

        return apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// A packaged app's install folder, when it is where Windows puts packages: a WindowsApps
    /// folder on some drive. A package registered from anywhere else — in developer mode, say —
    /// gets no install folder, and its leftovers are looked for by name.
    /// </summary>
    public static string? PackageFolder(InstalledProgram app)
    {
        var folder = PathRules.Normalize(app.InstallLocation);
        var parent = folder is null ? null : Path.GetFileName(Path.GetDirectoryName(folder));
        return parent?.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) == true ? folder : null;
    }

    private static DateOnly? InstallDate(string? folder)
    {
        try
        {
            return folder is not null && Directory.Exists(folder) ? DateOnly.FromDateTime(Directory.GetCreationTime(folder)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record ManifestFacts(string DisplayName, string? Publisher, bool IsSparse);

    /// <summary>
    /// The name and publisher the app shows, from its manifest, with resource references resolved;
    /// and whether it is a sparse package, which only gives a desktop program an identity.
    /// </summary>
    private static ManifestFacts ReadManifest(string? installLocation, string fullName, string packageName)
    {
        try
        {
            var manifest = installLocation is null ? null : Path.Combine(installLocation, "AppxManifest.xml");
            if (manifest is null || !File.Exists(manifest)) return new ManifestFacts(packageName, null, false);

            var document = XDocument.Load(manifest);
            var properties = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            var display = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;
            var publisher = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value;
            var sparse = properties?.Elements().Any(e => e.Name.LocalName == "AllowExternalContent"
                                                         && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) ?? false;

            if (sparse) return new ManifestFacts(packageName, null, true);

            return new ManifestFacts(Resolve(display, fullName, packageName) ?? packageName, Resolve(publisher, fullName, packageName), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new ManifestFacts(packageName, null, false);
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);

    /// <summary>
    /// "ms-resource:AppName" and its variants, looked up in the package's resources the way the
    /// Start menu does. Unresolvable references give null rather than the raw reference.
    /// </summary>
    private static string? Resolve(string? value, string fullName, string packageName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value.Trim();

        var reference = value["ms-resource:".Length..];
        var candidates = reference.StartsWith("//", StringComparison.Ordinal)
            ? new[] { "ms-resource:" + reference }
            : reference.StartsWith('/')
                ? new[] { $"ms-resource://{packageName}{reference}" }
                : new[] { $"ms-resource://{packageName}/resources/{reference}", $"ms-resource://{packageName}/{reference}" };

        foreach (var candidate in candidates)
        {
            var buffer = new StringBuilder(1024);
            if (SHLoadIndirectString($"@{{{fullName}? {candidate}}}", buffer, buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0)
                return buffer.ToString();
        }

        return null;
    }

    /// <summary>
    /// What removal amounts to, for showing only: <see cref="UninstallAsync"/> is what runs, with
    /// the name passed in the environment rather than on a command line.
    /// </summary>
    public static UninstallCommand RemoveCommand(string fullName) =>
        new(PowerShellPath, $"-NoProfile -Command Remove-AppxPackage -Package {fullName}");

    /// <summary>
    /// Removes the app for the account this runs as and waits for Windows to finish. Stopping the
    /// wait leaves Windows to carry on, as it does for any other uninstaller.
    /// </summary>
    public static async Task<UninstallerRunResult> UninstallAsync(InstalledProgram app, IProgress<string>? progress, CancellationToken stopWaiting)
    {
        var fullName = app.PackageFullName;
        if (!IsValidFullName(fullName)) return new UninstallerRunResult { Message = "This app's package name could not be read." };

        progress?.Report($"Removing {app.DisplayName}…");

        // The name travels in the environment, not the script, even though it has been validated.
        var script = """
            $ErrorActionPreference = 'Stop'
            try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
            try { Remove-AppxPackage -Package $env:UPROOT_PACKAGE; 'REMOVED' } catch { 'FAILED ' + $_.Exception.Message }
            """;

        string output, error;
        try
        {
            (_, output, error) = await RunPowerShellAsync(script, TimeSpan.FromMinutes(10), stopWaiting, ("UPROOT_PACKAGE", fullName!)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopWaiting.IsCancellationRequested)
        {
            return new UninstallerRunResult
            {
                Started = true,
                StoppedWaiting = true,
                Message = $"Stopped waiting. Windows may still be removing {app.DisplayName}."
            };
        }
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

        if (line.StartsWith("REMOVED", StringComparison.Ordinal))
            return new UninstallerRunResult { Started = true, ExitCode = 0, Message = $"Windows removed {app.DisplayName}." };

        var reason = line.StartsWith("FAILED ", StringComparison.Ordinal) ? line["FAILED ".Length..] : error.Trim();
        return new UninstallerRunResult
        {
            Started = true,
            ExitCode = 1,
            Message = $"Windows could not remove {app.DisplayName}: {(string.IsNullOrWhiteSpace(reason) ? "no reason given." : reason)}"
        };
    }

    private static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellAsync(
        string script, TimeSpan timeout, CancellationToken ct, params (string Name, string Value)[] environment)
    {
        if (!File.Exists(PowerShellPath)) return (-1, string.Empty, "Windows PowerShell is not available.");

        var startInfo = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory
        };

        foreach (var (name, value) in environment) startInfo.Environment[name] = value;

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, string.Empty, "PowerShell did not answer in time.");
            }

            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
