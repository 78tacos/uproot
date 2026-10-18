using System.Globalization;
using System.Text.RegularExpressions;

namespace Uproot.Core;

/// <summary>Who an installed program was installed for.</summary>
public enum ProgramScope
{
    /// <summary>Registered under HKLM: installed for everyone, usually needs administrator rights.</summary>
    Machine,

    /// <summary>Registered under one account's own hive.</summary>
    User
}

/// <summary>The installer framework behind an uninstaller, as far as it can be told.</summary>
public enum InstallerTechnology
{
    Unknown,
    WindowsInstaller,
    InnoSetup,
    Nsis,
    InstallShield,
    WixBurn,
    Squirrel,
    ChromiumSetup,
    ClickToRun,

    /// <summary>A Microsoft Store or other MSIX package, which Windows itself removes.</summary>
    Msix
}

/// <summary>
/// One entry from the Uninstall keys — the same list Apps &amp; Features shows — plus the
/// values an uninstaller analysis needs.
/// </summary>
public sealed partial record InstalledProgram
{
    /// <summary>Stable identity on this machine: the registry path of the entry.</summary>
    public string Id => Key.DisplayPath;

    /// <summary>The Uninstall subkey itself.</summary>
    public required RegistryLocation Key { get; init; }

    /// <summary>The subkey's name: a product code for MSI packages, "AppId_is1" for Inno Setup.</summary>
    public string KeyName => Key.LeafName;

    public required string DisplayName { get; init; }
    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? InstallSource { get; init; }
    public string? DisplayIcon { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? ModifyPath { get; init; }
    public DateOnly? InstallDate { get; init; }
    public long? EstimatedSizeKb { get; init; }
    public bool IsWindowsInstaller { get; init; }
    public bool IsSystemComponent { get; init; }

    /// <summary>Set for updates, hotfixes and the hidden parts of a bundle.</summary>
    public bool IsUpdateOrComponent { get; init; }

    public required ProgramScope Scope { get; init; }

    /// <summary>True for an entry under WOW6432Node: a 32-bit installer wrote it.</summary>
    public bool Is32Bit { get; init; }

    /// <summary>
    /// The account a per-user entry belongs to, when it is not the person signed in: elevated with
    /// an administrator's credentials, HKCU and the Store answer for that administrator's account.
    /// See <see cref="ProgramOwner"/>.
    /// </summary>
    public string? Owner { get; init; }

    /// <summary>The SID of <see cref="Owner"/>, so its leftovers are looked for in that account's profile.</summary>
    public string? OwnerSid { get; init; }

    /// <summary>Who it is installed for, in a few words: "Everyone", "Just me", or "LFND_Admin only".</summary>
    public string InstalledFor => Scope == ProgramScope.Machine
        ? "Everyone"
        : Owner is { } owner ? $"{ShortAccount(owner)} only" : "Just me";

    /// <summary>"LFND_Admin" from "LFND\LFND_Admin": on a label this short the domain says nothing.</summary>
    public static string ShortAccount(string account) =>
        account.LastIndexOf('\\') is var slash and >= 0 && slash < account.Length - 1 ? account[(slash + 1)..] : account;

    /// <summary>Every string value in the key, for installer-specific ones like Inno's App Path.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// False for a program described only by name — one already uninstalled, or never listed —
    /// whose leftovers are being hunted. It has no entry to run or remove.
    /// </summary>
    public bool IsRegistered { get; init; } = true;

    /// <summary>
    /// The package's full name, for a Microsoft Store or other MSIX app. Set only by
    /// <see cref="StoreApps"/> from Windows' own package list — never from registry values, which
    /// any Uninstall entry could carry.
    /// </summary>
    public string? PackageFullName { get; init; }

    /// <summary>A Microsoft Store or other MSIX app, removed by Windows rather than by an uninstaller of its own.</summary>
    public bool IsStoreApp => PackageFullName is not null;

    /// <summary>
    /// A program known only by its name and publisher — and, when someone knows it, the folder it
    /// was installed in — for finding what a program removed some other way left behind. Its
    /// <see cref="Key"/> names no real entry.
    /// </summary>
    public static InstalledProgram ForName(string name, string? publisher, string? installFolder = null) => new()
    {
        Key = RegistryLocation.Machine($@"SOFTWARE\Uproot\Unregistered\{name}"),
        DisplayName = name.Trim(),
        Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
        InstallLocation = string.IsNullOrWhiteSpace(installFolder) ? null : installFolder.Trim(),
        Scope = ProgramScope.Machine,
        IsRegistered = false
    };

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    internal static partial Regex GuidPattern();

    /// <summary>
    /// The MSI product code, when this is a Windows Installer package: the key name, or failing
    /// that the GUID in an msiexec uninstall command.
    /// </summary>
    public string? ProductCode
    {
        get
        {
            if (GuidPattern().IsMatch(KeyName) && KeyName.Length == 38 && IsWindowsInstaller)
                return KeyName.ToUpperInvariant();

            if (UninstallString is { } command
                && command.Contains("msiexec", StringComparison.OrdinalIgnoreCase)
                && GuidPattern().Match(command) is { Success: true } match)
            {
                return match.Value.ToUpperInvariant();
            }

            return null;
        }
    }

    /// <summary>A one-line summary for lists: publisher, version, size and date.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Publisher)) parts.Add(Publisher!);
            if (!string.IsNullOrWhiteSpace(DisplayVersion)) parts.Add(DisplayVersion!);
            if (EstimatedSizeKb is > 0 and var kb) parts.Add(Sizes.Format(kb * 1024));
            if (InstallDate is { } date) parts.Add(date.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            return string.Join("  ·  ", parts);
        }
    }

    public override string ToString() => DisplayName;
}
