using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Uproot.Core;

/// <summary>A registry value or key a package recorded writing.</summary>
public sealed record FootprintRegistryEntry(RegistryLocation Key, string? ValueName, bool Shared);

/// <summary>A file or folder a package recorded installing.</summary>
public sealed record FootprintPath(string Path, bool Shared);

/// <summary>
/// What a Windows Installer package says it installed on this machine, read from its cached
/// database before it is uninstalled.
///
/// This is the strongest evidence available: every item here was put in place by this product,
/// and Windows Installer knows it. After the uninstaller runs, anything in this list still on
/// disk is a leftover beyond reasonable doubt — except the items marked shared, which Windows
/// Installer deliberately left because another product uses them too, or because the package
/// declared them permanent. Acrobat's package includes the Visual C++ runtime in System32;
/// deleting that because Acrobat "left it behind" would break half the machine.
/// </summary>
public sealed partial record MsiFootprint
{
    public required string ProductCode { get; init; }
    public string? ProductName { get; init; }
    public string? Manufacturer { get; init; }
    public string? UpgradeCode { get; init; }
    public string? LocalPackage { get; init; }
    public string? InstallLocation { get; init; }
    public bool PerMachine { get; init; }

    public int ComponentCount { get; init; }
    public int InstalledComponentCount { get; init; }
    public int SharedComponentCount { get; init; }
    public int CustomActionCount { get; init; }
    public int PatchCount { get; init; }

    public IReadOnlyList<FootprintPath> Files { get; init; } = [];
    public IReadOnlyList<FootprintPath> Folders { get; init; } = [];
    public IReadOnlyList<FootprintRegistryEntry> Registry { get; init; } = [];
    public IReadOnlyList<string> Services { get; init; } = [];
    public IReadOnlyList<string> Shortcuts { get; init; } = [];

    /// <summary>Other installed products sharing the upgrade code: language packs, older versions.</summary>
    public IReadOnlyList<string> RelatedProducts { get; init; } = [];

    /// <summary>Shared items that sit under the Windows folder, for the analysis to point out.</summary>
    public int SharedSystemFileCount { get; init; }

    private const int AttributePermanent = 0x10;
    private const int Attribute64Bit = 0x100;

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex FormattedProperty();

    /// <summary>
    /// Reads the footprint of an installed product. Null when the product or its cached package
    /// cannot be found — a broken registration, or a package cache someone cleaned.
    /// </summary>
    public static MsiFootprint? Read(string productCode, CancellationToken ct = default)
    {
        var localPackage = MsiInterop.GetProductInfo(productCode, "LocalPackage");
        if (localPackage is null || !File.Exists(localPackage)) return null;

        using var db = MsiInterop.Database.Open(localPackage);
        if (db is null) return null;

        var properties = db.Query("SELECT `Property`, `Value` FROM `Property`", 2)
            .Where(r => r[0] is not null)
            .GroupBy(r => r[0]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()[1] ?? string.Empty, StringComparer.Ordinal);

        properties["ProductCode"] = productCode;

        var perMachine = MsiInterop.GetProductInfo(productCode, "AssignmentType") == "1";

        // ---- Components: which are installed here, where, and whether anyone else uses them.
        var components = new Dictionary<string, ComponentInfo>(StringComparer.Ordinal);
        var sharedDlls = SharedDlls.Read();
        var sharedCount = 0;

        foreach (var row in db.Query("SELECT `Component`, `ComponentId`, `Directory_`, `Attributes` FROM `Component`", 4))
        {
            ct.ThrowIfCancellationRequested();
            if (row[0] is not { } name) continue;

            var attributes = int.TryParse(row[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0;
            var info = new ComponentInfo(name, row[1], row[2], attributes);

            if (!string.IsNullOrEmpty(info.ComponentId))
            {
                var (state, path) = MsiInterop.GetComponentPath(productCode, info.ComponentId);
                info.Installed = state == MsiInterop.InstallStateLocal;
                info.KeyPath = path;

                if (info.Installed)
                {
                    var otherClients = MsiInterop.EnumClients(info.ComponentId)
                        .Count(c => !c.Equals(productCode, StringComparison.OrdinalIgnoreCase));

                    // Shared if permanent, installed by another MSI product too, or counted in
                    // SharedDLLs by more than this product's own single reference.
                    info.Shared = (attributes & AttributePermanent) != 0
                                  || otherClients > 0
                                  || (path is not null && MsiInterop.MsiRegistryPath(path) is null
                                      && SharedDlls.IsInUseByOthers(sharedDlls, path, ownUses: 1));
                    if (info.Shared) sharedCount++;
                }
            }

            components[name] = info;
        }

        var directories = ResolveDirectories(db, components, perMachine);

        // ---- Files.
        var files = new List<FootprintPath>();
        foreach (var row in db.Query("SELECT `Component_`, `FileName` FROM `File`", 2))
        {
            if (row[0] is null || !components.TryGetValue(row[0]!, out var component) || !component.Installed) continue;
            if (component.Directory is null || !directories.TryGetValue(component.Directory, out var folder)) continue;

            files.Add(new FootprintPath(Path.Combine(folder, LongName(row[1])), component.Shared));
        }

        // ---- Folders: every directory an installed component lives in or creates.
        var folderSet = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        void AddFolder(string? directoryKey, bool shared)
        {
            if (directoryKey is null || !directories.TryGetValue(directoryKey, out var path)) return;
            folderSet[path] = folderSet.TryGetValue(path, out var existing) ? existing || shared : shared;
        }

        foreach (var component in components.Values.Where(c => c.Installed)) AddFolder(component.Directory, component.Shared);

        foreach (var row in db.Query("SELECT `Directory_`, `Component_` FROM `CreateFolder`", 2))
        {
            if (row[1] is not null && components.TryGetValue(row[1]!, out var component) && component.Installed)
                AddFolder(row[0], component.Shared);
        }

        // ---- Registry.
        var registry = new List<FootprintRegistryEntry>();
        foreach (var row in db.Query("SELECT `Root`, `Key`, `Name`, `Component_` FROM `Registry`", 4))
        {
            if (row[3] is null || !components.TryGetValue(row[3]!, out var component) || !component.Installed) continue;
            if (!int.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var root)) continue;

            var key = Format(row[1], properties);
            if (key is null) continue;

            var location = RegistryRoot(root, key, perMachine);
            if (location is null) continue;

            if ((component.Attributes & Attribute64Bit) == 0) location = location.As32BitWrite();

            // "+", "-" and "*" as a name are instructions about the key itself, not values.
            var valueName = row[2] is null or "" or "+" or "-" or "*" ? null : Format(row[2], properties);

            registry.Add(new FootprintRegistryEntry(location, valueName, component.Shared));
        }

        // Components whose key path is itself a registry key contribute that key too.
        foreach (var component in components.Values.Where(c => c.Installed))
        {
            if (MsiInterop.MsiRegistryPath(component.KeyPath) is not { } decoded) continue;

            var location = decoded.Hive switch
            {
                RegistryHive.ClassesRoot => perMachine
                    ? RegistryLocation.Machine(@"SOFTWARE\Classes\" + decoded.SubKey)
                    : RegistryLocation.CurrentUser(@"Software\Classes\" + decoded.SubKey),
                RegistryHive.LocalMachine => RegistryLocation.Machine(decoded.SubKey),
                RegistryHive.CurrentUser => RegistryLocation.CurrentUser(decoded.SubKey),
                _ => null
            };

            if (location is null) continue;
            if (!decoded.Is64Bit) location = location.As32BitWrite();

            registry.Add(new FootprintRegistryEntry(location, null, component.Shared));
        }

        // ---- Services and shortcuts.
        var services = db.Query("SELECT `Name`, `Component_` FROM `ServiceInstall`", 2)
            .Where(r => r[1] is not null && components.TryGetValue(r[1]!, out var c) && c.Installed)
            .Select(r => Format(r[0], properties))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shortcuts = db.Query("SELECT `Directory_`, `Name`, `Component_` FROM `Shortcut`", 3)
            .Where(r => r[2] is not null && components.TryGetValue(r[2]!, out var c) && c.Installed)
            .Where(r => r[0] is not null && directories.ContainsKey(r[0]!))
            .Select(r => Path.Combine(directories[r[0]!], LongName(r[1]) + ".lnk"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var customActions = db.Query("SELECT `Action` FROM `CustomAction`", 1).Count;

        var upgradeCode = properties.GetValueOrDefault("UpgradeCode");
        var related = upgradeCode is null
            ? []
            : MsiInterop.EnumRelatedProducts(upgradeCode)
                .Where(p => !p.Equals(productCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        return new MsiFootprint
        {
            ProductCode = productCode,
            ProductName = properties.GetValueOrDefault("ProductName"),
            Manufacturer = properties.GetValueOrDefault("Manufacturer"),
            UpgradeCode = upgradeCode,
            LocalPackage = localPackage,
            InstallLocation = MsiInterop.GetProductInfo(productCode, "InstallLocation"),
            PerMachine = perMachine,
            ComponentCount = components.Count,
            InstalledComponentCount = components.Values.Count(c => c.Installed),
            SharedComponentCount = sharedCount,
            CustomActionCount = customActions,
            PatchCount = MsiInterop.CountPatches(productCode),
            Files = files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            Folders = folderSet.Select(f => new FootprintPath(f.Key, f.Value)).ToList(),
            Registry = registry.DistinctBy(r => (r.Key, r.ValueName?.ToUpperInvariant())).ToList(),
            Services = services,
            Shortcuts = shortcuts,
            RelatedProducts = related,
            SharedSystemFileCount = files.Count(f => f.Shared && PathRules.IsWithin(f.Path, windows))
        };
    }

    private sealed class ComponentInfo(string name, string? componentId, string? directory, int attributes)
    {
        public string Name { get; } = name;
        public string? ComponentId { get; } = componentId;
        public string? Directory { get; } = directory;
        public int Attributes { get; } = attributes;
        public bool Installed { get; set; }
        public bool Shared { get; set; }
        public string? KeyPath { get; set; }
    }

    // ---- Directory resolution ------------------------------------------------

    /// <summary>
    /// Works out where each directory in the package actually ended up.
    ///
    /// The Directory table only holds names and parents; the real locations were decided at
    /// install time from properties that are long gone. The installed components' key paths are
    /// the ground truth, so those seed the map, parents are inferred upwards from them, and
    /// anything left is resolved downwards from the standard system folders.
    /// </summary>
    private static Dictionary<string, string> ResolveDirectories(
        MsiInterop.Database db, Dictionary<string, ComponentInfo> components, bool perMachine)
    {
        var table = db.Query("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`", 3)
            .Where(r => r[0] is not null)
            .GroupBy(r => r[0]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Parent: NullIfEmpty(g.First()[1]), Name: TargetName(g.First()[2])), StringComparer.Ordinal);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        // Seeds: a component's key path file sits in the component's directory.
        foreach (var component in components.Values)
        {
            if (!component.Installed || component.Directory is null || component.KeyPath is null) continue;
            if (MsiInterop.MsiRegistryPath(component.KeyPath) is not null) continue;

            var path = component.KeyPath.EndsWith('\\')
                ? component.KeyPath.TrimEnd('\\')
                : Path.GetDirectoryName(component.KeyPath);

            if (!string.IsNullOrEmpty(path)) resolved.TryAdd(component.Directory, path);
        }

        // Upwards: a resolved child named as the table says tells us its parent.
        for (var pass = 0; pass < 8; pass++)
        {
            var added = false;

            foreach (var (key, (parent, name)) in table)
            {
                if (parent is null || resolved.ContainsKey(parent) || !resolved.TryGetValue(key, out var path)) continue;

                string? parentPath;
                if (name is null) parentPath = path;
                else if (Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)) parentPath = Path.GetDirectoryName(path);
                else continue;

                if (string.IsNullOrEmpty(parentPath)) continue;

                resolved[parent] = parentPath;
                added = true;
            }

            if (!added) break;
        }

        // Downwards: standard folders, then children of anything resolved.
        foreach (var (key, known) in StandardFolders(perMachine))
            resolved.TryAdd(key, known);

        string? Resolve(string key, int depth)
        {
            if (resolved.TryGetValue(key, out var path)) return path;
            if (depth > 32 || !table.TryGetValue(key, out var entry) || entry.Parent is null) return null;

            var parentPath = Resolve(entry.Parent, depth + 1);
            if (parentPath is null) return null;

            path = entry.Name is null ? parentPath : Path.Combine(parentPath, entry.Name);
            resolved[key] = path;
            return path;
        }

        foreach (var key in table.Keys.ToList()) Resolve(key, 0);

        return resolved;
    }

    /// <summary>
    /// The target long name from a DefaultDir value such as "ACROBA~1|Acrobat DC:SourceDir".
    /// Null for "." and SourceDir, which mean "the parent itself".
    /// </summary>
    private static string? TargetName(string? defaultDir)
    {
        if (string.IsNullOrEmpty(defaultDir)) return null;

        var target = defaultDir.Split(':')[0];
        var name = target.Contains('|') ? target[(target.IndexOf('|') + 1)..] : target;

        return name is "." or "SourceDir" or "" ? null : name;
    }

    private static string LongName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return string.Empty;
        return fileName.Contains('|') ? fileName[(fileName.IndexOf('|') + 1)..] : fileName;
    }

    private static IEnumerable<(string Key, string Path)> StandardFolders(bool perMachine)
    {
        string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        var programFiles64 = Environment.GetEnvironmentVariable("ProgramW6432") ?? Folder(Environment.SpecialFolder.ProgramFiles);
        var programFiles32 = Folder(Environment.SpecialFolder.ProgramFilesX86);
        var windows = Folder(Environment.SpecialFolder.Windows);

        yield return ("ProgramFiles64Folder", programFiles64);
        yield return ("ProgramFilesFolder", programFiles32);
        yield return ("CommonFiles64Folder", Path.Combine(programFiles64, "Common Files"));
        yield return ("CommonFilesFolder", Path.Combine(programFiles32, "Common Files"));
        yield return ("System64Folder", Path.Combine(windows, "System32"));
        yield return ("SystemFolder", Path.Combine(windows, Environment.Is64BitOperatingSystem ? "SysWOW64" : "System32"));
        yield return ("WindowsFolder", windows);
        yield return ("WindowsVolume", Path.GetPathRoot(windows) ?? @"C:\");
        yield return ("FontsFolder", Folder(Environment.SpecialFolder.Fonts));
        yield return ("TempFolder", Path.GetTempPath().TrimEnd('\\'));
        yield return ("CommonAppDataFolder", Folder(Environment.SpecialFolder.CommonApplicationData));
        yield return ("AppDataFolder", Folder(Environment.SpecialFolder.ApplicationData));
        yield return ("LocalAppDataFolder", Folder(Environment.SpecialFolder.LocalApplicationData));
        yield return ("PersonalFolder", Folder(Environment.SpecialFolder.MyDocuments));

        yield return ("ProgramMenuFolder", Folder(perMachine ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs));
        yield return ("StartMenuFolder", Folder(perMachine ? Environment.SpecialFolder.CommonStartMenu : Environment.SpecialFolder.StartMenu));
        yield return ("StartupFolder", Folder(perMachine ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup));
        yield return ("DesktopFolder", Folder(perMachine ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory));
        yield return ("AdminToolsFolder", Folder(perMachine ? Environment.SpecialFolder.CommonAdminTools : Environment.SpecialFolder.AdminTools));
    }

    // ---- Formatted strings and registry roots ----------------------------------

    /// <summary>
    /// Expands [Property] references from the package's Property table. Null when anything is
    /// left unresolved: a key built from a property set at install time cannot be known now, and
    /// guessing would point the scanner at the wrong key.
    /// </summary>
    private static string? Format(string? value, Dictionary<string, string> properties)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!value.Contains('[')) return value;

        var unresolved = false;

        var expanded = FormattedProperty().Replace(value, m =>
        {
            if (properties.TryGetValue(m.Groups[1].Value, out var replacement)) return replacement;
            unresolved = true;
            return m.Value;
        });

        return unresolved || string.IsNullOrWhiteSpace(expanded) ? null : expanded.Trim('\\');
    }

    /// <summary>
    /// The Registry table's Root column: -1 means HKLM for a per-machine install and HKCU for a
    /// per-user one, and HKCR resolves the same way to the matching Software\Classes.
    /// </summary>
    private static RegistryLocation? RegistryRoot(int root, string key, bool perMachine) => root switch
    {
        -1 => perMachine ? RegistryLocation.Machine(key) : RegistryLocation.CurrentUser(key),
        0 => perMachine
            ? RegistryLocation.Machine(@"SOFTWARE\Classes\" + key)
            : RegistryLocation.CurrentUser(@"Software\Classes\" + key),
        1 => RegistryLocation.CurrentUser(key),
        2 => RegistryLocation.Machine(key),
        _ => null
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
