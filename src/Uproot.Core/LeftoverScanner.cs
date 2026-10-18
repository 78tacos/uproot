using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Uproot.Core;

/// <summary>
/// Finds what a program left behind.
///
/// Evidence is gathered strongest first. What the installer itself recorded, and anything that
/// points into the program's own folder, is certain. A folder or key named after the program in
/// a place programs keep their own things is likely. A partial match, or a mention somewhere in
/// the registry, is possible and left for a person to judge. The mode decides how far down that
/// list the scan goes; it never changes how an item is judged, so the same folder gets the same
/// confidence whichever mode found it.
///
/// Every candidate then goes through <see cref="LeftoverSafety"/>. Items it refuses are kept in the
/// results, marked protected with the reason, so nothing is skipped without saying so.
/// </summary>
public sealed class LeftoverScanner(LeftoverScanContext context, LeftoverSafety safety)
{
    private static readonly TimeSpan DeepSearchBudget = TimeSpan.FromSeconds(60);
    private const int MaxFolderEntries = 20_000;

    public Task<LeftoverScanResult> ScanAsync(
        UninstallTarget target, LeftoverScanOptions options, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Scan(target, options, progress, ct), ct);

    public LeftoverScanResult Scan(
        UninstallTarget target, LeftoverScanOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var run = new Run(WithoutProtectedFolder(target), options, context, safety, progress, ct);

        run.Step("Checking the Apps & Features entry", run.UninstallEntry);
        run.Step("Checking the install folder", run.InstallFolders);
        run.Step("Checking what the installer recorded", run.MsiFootprint);
        run.Step("Checking the setup log", run.InnoLog);
        run.Step("Searching program folders", run.FolderNames);
        run.Step("Searching the registry", run.RegistryNames);
        run.Step("Checking shortcuts", run.Shortcuts);
        run.Step("Checking startup entries", run.RunEntries);
        run.Step("Checking App Paths", run.AppPaths);
        run.Step("Checking services", run.Services);
        run.Step("Checking scheduled tasks", run.ScheduledTasks);
        run.Step("Checking COM and file-type registrations", run.Classes);
        run.Step("Checking shell extensions and Default Apps", run.ShellRegistrations);
        run.Step("Checking PATH", run.PathEntries);
        run.Step("Checking firewall rules", run.FirewallRules);
        run.Step("Checking what Windows recorded about it", run.WindowsRecords);
        run.Step("Searching Temp and Documents", run.TempAndDocuments);
        run.Step("Searching the registry for the install path", run.DeepRegistry);

        // Not a Step: a failure here must fail the scan, not pass for a clean result.
        progress?.Report("Reviewing what was found");
        var reviewing = Stopwatch.StartNew();
        run.Finish();
        run.Timings.Add(("Reviewing what was found", reviewing.Elapsed));

        return new LeftoverScanResult(run.Items, run.Notes, stopwatch.Elapsed) { Timings = run.Timings };
    }

    /// <summary>
    /// A target with nothing taken from an install folder the policy protects outright — a
    /// profile, Documents, Program Files — whether an entry recorded it or a person named it. Such
    /// a folder is not one program's: everything points into it, its name ("tester", "Documents")
    /// would match folders that have nothing to do with the program, and neither would the folder
    /// above it or the executables inside it.
    /// </summary>
    private UninstallTarget WithoutProtectedFolder(UninstallTarget target)
    {
        if (target.InstallLocation is not { } folder || safety.JudgePath(folder) is not { Absolute: true }) return target;

        var folderName = ProgramNames.Normalize(ProgramNames.InstallFolderName(folder));
        var displayName = ProgramNames.Normalize(ProgramNames.CleanDisplayName(target.Program.DisplayName));
        bool FromFolder(string name) => ProgramNames.Normalize(name) is var n && n == folderName && n != displayName;

        return target with
        {
            InstallLocation = null,
            ReferenceFolders = target.ReferenceFolders.Where(f => safety.JudgePath(f) is not { Absolute: true }).ToList(),
            ProductNames = target.ProductNames.Where(n => !FromFolder(n)).ToList(),
            PrimaryNames = target.PrimaryNames.Where(n => !FromFolder(n)).ToList(),
            PublisherNames = ProgramNames.PublisherNames(target.Program.Publisher ?? target.Analysis?.Msi?.Manufacturer, vendorFolder: null),
            ExecutableNames = []
        };
    }

    /// <summary>One scan's state, so the scanner itself stays reusable.</summary>
    private sealed class Run(
        UninstallTarget target, LeftoverScanOptions options, LeftoverScanContext context,
        LeftoverSafety safety, IProgress<string>? progress, CancellationToken ct)
    {
        private readonly Dictionary<string, LeftoverItem> _items = new(StringComparer.Ordinal);
        private readonly HashSet<string> _leftoverClsids = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _leftoverProgIds = new(StringComparer.OrdinalIgnoreCase);
        private int _windowsItemsSkipped;
        private int _sharedDllsKept;

        public List<string> Notes { get; } = [];

        public List<(string Stage, TimeSpan Elapsed)> Timings { get; } = [];

        /// <summary>The reviewed items, once <see cref="Finish"/> has run.</summary>
        public IReadOnlyList<LeftoverItem> Items { get; private set; } = [];

        private string ProgramName => target.Program.DisplayName;
        private bool Wants(ScanMode mode) => options.Mode >= mode;

        public void Step(string stage, Action action)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(stage);

            var watch = Stopwatch.StartNew();
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken source must not sink the scan; say which part could not be checked.
                Notes.Add($"{stage} failed: {ex.Message}");
            }
            finally
            {
                Timings.Add((stage, watch.Elapsed));
            }
        }

        // ---- Recording candidates ------------------------------------------------

        private void Add(LeftoverItem item)
        {
            if (!Wants(item.Mode)) return;

            if (_items.TryGetValue(item.Identity, out var existing))
            {
                // The same thing found twice keeps its strongest evidence.
                var better = Rank(item) > Rank(existing) ? item : existing;
                _items[item.Identity] = better with { Mode = (ScanMode)Math.Min((int)existing.Mode, (int)item.Mode) };
                return;
            }

            _items[item.Identity] = item;
        }

        private static int Rank(LeftoverItem item) =>
            (int)item.Confidence * 4 + (item.OnlyIfEmpty ? 0 : 2) + (item.Caution is null ? 1 : 0);

        private void AddFolder(string path, LeftoverConfidence confidence, ScanMode mode, string reason,
            string? owner, bool onlyIfEmpty = false, string? caution = null)
        {
            Add(new LeftoverItem
            {
                Kind = LeftoverKind.Folder,
                Path = path,
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                Owner = owner,
                OnlyIfEmpty = onlyIfEmpty,
                Caution = caution
            });
        }

        private void AddFile(string path, LeftoverConfidence confidence, ScanMode mode, string reason, string? owner)
        {
            Add(new LeftoverItem
            {
                Kind = LeftoverKind.File,
                Path = path,
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                Owner = owner
            });
        }

        private void AddKey(RegistryLocation key, LeftoverConfidence confidence, ScanMode mode, string reason,
            string? owner, bool onlyIfEmpty = false, string? caution = null)
        {
            Add(new LeftoverItem
            {
                Kind = LeftoverKind.RegistryKey,
                Path = key.DisplayPath,
                RegistryKey = key,
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                Owner = owner,
                OnlyIfEmpty = onlyIfEmpty,
                Caution = caution
            });
        }

        private void AddValue(RegistryLocation key, string valueName, LeftoverConfidence confidence, ScanMode mode, string reason, string? owner)
        {
            Add(new LeftoverItem
            {
                Kind = LeftoverKind.RegistryValue,
                Path = key.DisplayPath,
                RegistryKey = key,
                ValueName = valueName,
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                Owner = owner
            });
        }

        /// <summary>
        /// The program's own folders, as far as anything pointing into them can be trusted to be
        /// its: a folder another installed program also claims is left out, since a verb or task
        /// pointing there could be either's; and so is a folder the policy protects outright — a
        /// profile, Documents, Program Files — which everything points into, and which an entry
        /// or a person naming it as the install folder cannot make one program's.
        /// </summary>
        private readonly List<string> _references = target.ReferenceFolders
            .Where(folder => !safety.OtherPrograms.Any(o => o.Folders.Any(claim => PathRules.IsWithin(folder, claim))))
            .Where(folder => safety.JudgePath(folder) is not { Absolute: true })
            .ToList();

        /// <summary>Other programs installed inside this one's folder, whose own things must not be taken for its.</summary>
        private readonly List<string> _nestedClaims = safety.OtherPrograms
            .SelectMany(o => o.Folders)
            .Where(claim => target.ReferenceFolders.Any(folder => PathRules.IsStrictlyWithin(claim, folder)))
            .ToList();

        /// <summary>True when a path, command or value mentions one of the program's own folders.</summary>
        private bool PointsIntoProgram(string? text) =>
            !string.IsNullOrEmpty(text)
            && _references.Any(f => PathRules.References(text, f))
            && !_nestedClaims.Any(n => PathRules.References(text, n));

        private static string? OwnerOf(RegistryLocation key, ProfileScope scope) =>
            key.Hive == RegistryHive.LocalMachine ? null : scope.Owner;

        // ---- The Apps & Features entry -------------------------------------------

        public void UninstallEntry()
        {
            var key = target.Program.Key;
            if (!target.Program.IsRegistered || !key.Exists()) return;

            AddKey(key, LeftoverConfidence.Certain, ScanMode.Safe,
                options.ProgramStillInstalled
                    ? "The program's Apps & Features entry. Removing it takes the program off the list; it does not uninstall it."
                    : "The Apps & Features entry is still there after the uninstaller ran.",
                target.Program.Scope == ProgramScope.User ? target.Program.Owner ?? CurrentOwner() : null);
        }

        private string? CurrentOwner() => context.Profiles.FirstOrDefault()?.Owner;

        // ---- Install folders -----------------------------------------------------

        public void InstallFolders()
        {
            foreach (var folder in target.ReferenceFolders)
            {
                if (!Directory.Exists(folder)) continue;

                AddFolder(folder, LeftoverConfidence.Certain, ScanMode.Safe,
                    !target.Program.IsRegistered
                        ? "The folder you named as the program's install folder."
                        : options.ProgramStillInstalled
                            ? "The program's install folder. It is still installed, so this is a forced removal."
                            : "The program's install folder, still there after the uninstaller ran.",
                    OwnerForPath(folder));
            }
        }

        private string? OwnerForPath(string path) =>
            context.Profiles.FirstOrDefault(p => p.ProfileRoot is { } root && PathRules.IsWithin(path, root))?.Owner;

        // ---- Windows Installer record ---------------------------------------------

        public void MsiFootprint()
        {
            if (target.Analysis?.Msi is not { } msi) return;

            var product = msi.ProductName ?? ProgramName;
            var reason = $"Installed by {product}, according to Windows Installer's own record.";

            // Files and folders: remaining, unshared items, collapsed to the highest folder that
            // holds nothing but this product's files. A file SharedDLLs still counts for another
            // program was kept by Windows Installer on purpose, and is kept here too.
            var sharedDlls = SharedDlls.Read();
            var ownUses = options.ProgramStillInstalled ? 1 : 0;

            var files = msi.Files.Where(f => !f.Shared && File.Exists(f.Path)).Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _sharedDllsKept += files.RemoveWhere(f => SharedDlls.IsInUseByOthers(sharedDlls, f, ownUses));

            var folders = msi.Folders.Where(f => !f.Shared && Directory.Exists(f.Path)).Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _windowsItemsSkipped += msi.Files.Count(f => f.Shared && File.Exists(f.Path));

            // The folders between the package's own and the first protected one are judged too:
            // a dictionary folder holding only per-language component folders contains no
            // component itself, but is still nothing but this product's.
            var evaluate = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                var parent = Path.GetDirectoryName(folder);
                while (parent is not null && safety.CheckPath(parent) is null && evaluate.Add(parent))
                    parent = Path.GetDirectoryName(parent);
            }

            var ours = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in evaluate.OrderByDescending(f => f.Length))
            {
                ct.ThrowIfCancellationRequested();
                ours[folder] = HoldsOnly(folder, files, ours);
            }

            var chosen = ours.Where(o => o.Value)
                .Select(o => o.Key)
                .Where(f => Path.GetDirectoryName(f) is not { } parent || !ours.GetValueOrDefault(parent))
                .ToList();

            foreach (var folder in chosen)
                AddFolder(folder, LeftoverConfidence.Certain, ScanMode.Safe, reason + " Nothing else is in it.", OwnerForPath(folder));

            foreach (var file in files.Where(f => !chosen.Any(c => PathRules.IsWithin(f, c))))
                AddFile(file, LeftoverConfidence.Certain, ScanMode.Safe, reason, OwnerForPath(file));

            // Folders that also hold something else go only if they end up empty.
            foreach (var folder in folders.Where(f => !ours.GetValueOrDefault(f) && !chosen.Any(c => PathRules.IsWithin(f, c))))
            {
                AddFolder(folder, LeftoverConfidence.Certain, ScanMode.Safe,
                    reason + " It also holds other files, so it is removed only if it ends up empty.",
                    OwnerForPath(folder), onlyIfEmpty: true);
            }

            foreach (var shortcut in msi.Shortcuts.Where(File.Exists))
                AddFile(shortcut, LeftoverConfidence.Certain, ScanMode.Safe, $"Shortcut created by {product}'s installer.", OwnerForPath(shortcut));

            // Registry: grouped by key, so a key is only offered whole when the product wrote
            // everything in it.
            var entries = msi.Registry.Where(r => !r.Shared).ToList();
            var recordedKeys = entries.Select(r => r.Key).ToHashSet();

            foreach (var group in entries.GroupBy(r => r.Key))
            {
                ct.ThrowIfCancellationRequested();
                RecordedKey(group.Key, group.Select(r => r.ValueName).ToList(), recordedKeys, reason, LeftoverConfidence.Certain, ScanMode.Safe);
            }

            if (context.ServicesKey is { } services)
            {
                foreach (var name in msi.Services)
                {
                    using var key = services.Child(name).Open();
                    if (key is null) continue;

                    AddService(name, key.GetValue("ImagePath") as string, LeftoverConfidence.Certain, ScanMode.Safe,
                        $"Service installed by {product}'s installer.");
                }
            }
        }

        /// <summary>
        /// True when a folder contains nothing but files from <paramref name="files"/> and
        /// subfolders already found to be the same.
        /// </summary>
        private static bool HoldsOnly(string folder, HashSet<string> files, Dictionary<string, bool> ours)
        {
            try
            {
                var count = 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    if (++count > MaxFolderEntries) return false;

                    var isOurs = Directory.Exists(entry)
                        ? ours.GetValueOrDefault(entry)
                        : files.Contains(entry);

                    if (!isOurs) return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Turns a key the installer recorded into the right thing to offer. A key named after the
        /// product is offered whole; a COM class or ProgID is offered whole; a key the product only
        /// added values to — someone else's key — is offered value by value, so nothing it did not
        /// write goes with it.
        /// </summary>
        private void RecordedKey(RegistryLocation key, IReadOnlyList<string?> valueNames, IReadOnlySet<RegistryLocation> recordedKeys,
            string reason, LeftoverConfidence confidence, ScanMode mode)
        {
            using var open = key.Open();
            if (open is null) return;

            var owner = key.Hive == RegistryHive.LocalMachine ? null : CurrentOwner();

            if (PromoteKey(key, recordedKeys) is { } promoted)
            {
                AddKey(promoted, confidence, mode, reason, owner);
                return;
            }

            var recorded = valueNames.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

            string[] present, subKeys;
            try
            {
                present = open.GetValueNames();
                subKeys = open.GetSubKeyNames();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return;
            }

            // Everything in it was recorded: its values, and any subkeys, which are then handled
            // on their own and go first, being deeper.
            var onlyOurs = present.All(v => recorded.Contains(v) || (v.Length == 0 && valueNames.Contains(null)))
                           && subKeys.All(s => recordedKeys.Contains(key.Child(s)))
                           && safety.CheckRegistryKey(key) is null;

            // Whole only when the key's own name ties it to the program, or it is a GUID
            // registration. Office\14.0\Word holding nothing but Acrobat's add-in values is still
            // Word's key, and goes value by value.
            if (onlyOurs && subKeys.Length == 0 && !IsWellKnownSharedKey(key) && NamedForProgram(key.LeafName))
            {
                AddKey(key, confidence, mode, reason, owner);
                return;
            }

            // Values the installer wrote into a key it does not own. Windows Installer removes
            // those itself, so one still present afterwards may have been rewritten by something
            // else since — likely, not certain.
            var valueConfidence = options.ProgramStillInstalled ? confidence : (LeftoverConfidence)Math.Min((int)confidence, (int)LeftoverConfidence.Likely);

            foreach (var value in present.Where(recorded.Contains))
                AddValue(key, value, valueConfidence, mode, reason, owner);

            // And the key itself, if taking those values leaves it empty.
            if (onlyOurs)
            {
                AddKey(key, confidence, mode, reason + " Removed only if nothing else is left in it.", owner, onlyIfEmpty: true);
            }
        }

        /// <summary>A key name that is a GUID, or contains one of the program's names.</summary>
        private bool NamedForProgram(string name) =>
            (name.Length == 38 && name.StartsWith('{') && Guid.TryParse(name, out _))
            || ProgramNames.Best(name, target.ProductNames).Match != NameMatch.None;

        private static readonly HashSet<string> RegistrationContainers = new(StringComparer.OrdinalIgnoreCase)
        {
            "CLSID", "TypeLib", "Interface", "AppID"
        };

        /// <summary>
        /// A broader key to offer in place of a recorded one, or null to leave the recorded key to
        /// be judged on its contents.
        ///
        /// Under Classes the walk goes from the top down and takes the first key that is provably
        /// this program's own: a COM registration under CLSID, TypeLib, Interface or AppID; a key
        /// the installer itself recorded writing to; or one named exactly after the program. So a
        /// verb an editor added to jpegfile yields jpegfile\shell\FooEdit — never jpegfile, which
        /// every image viewer shares. Outside Classes only an exact product name counts, and only
        /// when no other installed program matches it as well.
        /// </summary>
        private RegistryLocation? PromoteKey(RegistryLocation key, IReadOnlySet<RegistryLocation> recordedKeys)
        {
            var segments = key.SubKey.Split('\\');
            var classesIndex = Array.FindIndex(segments, s => s.Equals("Classes", StringComparison.OrdinalIgnoreCase));
            var userClasses = key.Hive == RegistryHive.Users && segments[0].EndsWith("_Classes", StringComparison.OrdinalIgnoreCase);

            if (classesIndex >= 0 || userClasses)
            {
                var first = userClasses ? 1 : classesIndex + 1;

                // Ancestors only: the recorded key itself is judged by what is in it.
                for (var i = first; i < segments.Length - 1; i++)
                {
                    var segment = segments[i];
                    if (segment.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) continue;

                    var candidate = new RegistryLocation(key.Hive, string.Join('\\', segments.Take(i + 1)));

                    // A generic name is never one program's: Adobe writes under HKCR\SOFTWARE\…,
                    // and "Classes\SOFTWARE" must never be what gets offered.
                    if (ProgramNames.IsGeneric(segment) || safety.CheckRegistryKey(candidate) is not null) continue;

                    var isRegistration = i > first && RegistrationContainers.Contains(segments[i - 1]);

                    if (isRegistration || recordedKeys.Contains(candidate) || IsNamedExactly(segment)) return candidate;
                }

                return null;
            }

            var softwareIndex = Array.FindIndex(segments, s => s.Equals("Software", StringComparison.OrdinalIgnoreCase));
            if (softwareIndex < 0) return null;

            for (var i = softwareIndex + 1; i < segments.Length; i++)
            {
                if (segments[i].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) continue;

                var isProductCode = target.Program.ProductCode is { } code && segments[i].Equals(code, StringComparison.OrdinalIgnoreCase);
                if (!isProductCode && !IsNamedExactly(segments[i])) continue;

                var candidate = new RegistryLocation(key.Hive, string.Join('\\', segments.Take(i + 1)));
                if (safety.CheckRegistryKey(candidate) is null) return candidate;
            }

            return null;
        }

        /// <summary>Named exactly after the program, and not just as well after another installed one.</summary>
        private bool IsNamedExactly(string name) =>
            ProgramNames.Best(name, target.ProductNames).Match == NameMatch.Exact
            && !safety.CheckName(name, NameMatch.Exact).Blocked
            && safety.CheckName(name, NameMatch.Exact).Note is null;

        /// <summary>Keys other programs are known to add values to, never offered whole.</summary>
        private static bool IsWellKnownSharedKey(RegistryLocation key)
        {
            var path = key.SubKey;
            return path.Contains(@"\Internet Explorer", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\CurrentVersion\Explorer", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\SharedDLLs", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\Windows\CurrentVersion", StringComparison.OrdinalIgnoreCase);
        }

        // ---- Inno Setup log ------------------------------------------------------

        public void InnoLog()
        {
            if (target.Analysis?.Inno is not { } log) return;

            var reason = "Recorded in the program's Inno Setup log as installed by it.";

            foreach (var path in log.Files)
            {
                if (target.InstallLocation is { } install && PathRules.IsWithin(path, install)) continue;

                if (File.Exists(path)) AddFile(path, LeftoverConfidence.Certain, ScanMode.Safe, reason, OwnerForPath(path));
                else if (Directory.Exists(path))
                    AddFolder(path, LeftoverConfidence.Certain, ScanMode.Safe, reason + " Removed only if empty.", OwnerForPath(path), onlyIfEmpty: true);
            }

            // The log does not say which hive a key was in, so try the one the program was
            // registered in. Its flags are not visible either — the key may have been a place
            // it only added a value to — so these are likely rather than certain.
            var hiveRoot = target.Program.Scope == ProgramScope.User
                ? context.Profiles.FirstOrDefault()?.SoftwareKeys.FirstOrDefault()?.Parent
                : RegistryLocation.Machine(string.Empty);

            if (hiveRoot is null) return;

            var logged = log.RegistryKeys
                .Select(k => !log.Is64Bit && hiveRoot.Hive == RegistryHive.LocalMachine ? hiveRoot.Child(k).As32BitWrite() : hiveRoot.Child(k))
                .ToHashSet();

            foreach (var key in logged.Where(k => k.Exists()))
            {
                // With no values to judge by, the logged key itself goes only when its own name
                // ties it to the program or it is a class of its own; otherwise it may be a key
                // the setup merely added a value to.
                var offered = PromoteKey(key, logged) ?? (IsLoggedKeyItsOwn(key) ? key : null);
                if (offered is null) continue;

                AddKey(offered, LeftoverConfidence.Likely, ScanMode.Moderate,
                    "Recorded in the program's Inno Setup log.", OwnerOf(offered, context.Profiles.FirstOrDefault() ?? context.Machine));
            }
        }

        private bool IsLoggedKeyItsOwn(RegistryLocation key)
        {
            if (safety.CheckRegistryKey(key) is not null || IsWellKnownSharedKey(key) || ProgramNames.IsGeneric(key.LeafName)) return false;

            var segments = key.SubKey.Split('\\');
            var directlyUnderClasses = segments.Length >= 2
                                       && (segments[^2].Equals("Classes", StringComparison.OrdinalIgnoreCase)
                                           || (segments.Length == 2 && segments[0].EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)));

            return directlyUnderClasses || NamedForProgram(key.LeafName);
        }

        // ---- Folders by name -------------------------------------------------------

        public void FolderNames()
        {
            foreach (var scope in context.AllScopes)
            {
                foreach (var root in scope.DataFolders)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var child in Directories(root)) NameMatchedFolder(child, root, scope.Owner);
                }
            }
        }

        private void NameMatchedFolder(string folder, string root, string? owner)
        {
            var name = Path.GetFileName(folder);

            // Directly in a shared root only the strong names count; the publisher-stripped one
            // ("Notes" from "Fabrikam Notes") is too ordinary a word to trust there.
            var (product, _) = ProgramNames.Best(name, target.PrimaryNames);

            switch (product)
            {
                case NameMatch.Exact:
                    NamedFolder(folder, name, product, LeftoverConfidence.Likely, ScanMode.Moderate,
                        $"Named after the program, directly in {root}.", owner);
                    return;

                case NameMatch.Prefix:
                case NameMatch.Contains:
                    NamedFolder(folder, name, product, LeftoverConfidence.Possible, ScanMode.Expert,
                        $"Its name includes the program's name.", owner);
                    return;
            }

            if (ProgramNames.Best(name, target.ProductNames).Match == NameMatch.Exact)
            {
                NamedFolder(folder, name, NameMatch.Exact, LeftoverConfidence.Possible, ScanMode.Expert,
                    "Has the program's short name, but directly in a shared folder rather than under its publisher's.", owner);
                return;
            }

            var (publisher, _) = ProgramNames.Best(name, target.PublisherNames);
            if (publisher != NameMatch.Exact) return;

            // A publisher's folder: look inside for the product.
            var found = false;
            var matchedAny = false;

            foreach (var inner in Directories(folder))
            {
                var innerName = Path.GetFileName(inner);
                var (match, _) = ProgramNames.Best(innerName, target.ProductNames);
                matchedAny |= match != NameMatch.None;

                switch (match)
                {
                    case NameMatch.Exact:
                        found |= NamedFolder(inner, innerName, match, LeftoverConfidence.Likely, ScanMode.Safe,
                            $"The program's own folder under its publisher's, {name}.", owner);
                        break;

                    case NameMatch.Prefix:
                        found |= NamedFolder(inner, innerName, match, LeftoverConfidence.Likely, ScanMode.Moderate,
                            $"Under the publisher's folder, named like the program.", owner);
                        break;

                    case NameMatch.Contains:
                        found |= NamedFolder(inner, innerName, match, LeftoverConfidence.Possible, ScanMode.Expert,
                            $"Under the publisher's folder, and its name includes the program's.", owner);
                        break;
                }
            }

            if (found && !ProgramNames.IsGeneric(name))
            {
                AddFolder(folder, LeftoverConfidence.Likely, ScanMode.Moderate,
                    "The publisher's folder. Removed only if nothing else is left in it.", owner, onlyIfEmpty: true);
            }
            // The whole folder only when nothing inside matched at all. If something matched but
            // was refused — another program's folder by name — offering its parent would offer it.
            else if (!matchedAny && Wants(ScanMode.Expert) && !ProgramNames.IsGeneric(name))
            {
                AddFolder(folder, LeftoverConfidence.Possible, ScanMode.Expert,
                    "The publisher's folder. Nothing inside was matched to this program, so it may belong to their other products.",
                    owner, caution: "May hold files for the publisher's other products.");
            }
        }

        private bool NamedFolder(string folder, string name, NameMatch match, LeftoverConfidence confidence,
            ScanMode mode, string reason, string? owner)
        {
            if (!Wants(mode)) return false;

            var verdict = safety.CheckName(name, match);

            Add(new LeftoverItem
            {
                Kind = LeftoverKind.Folder,
                Path = folder,
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                Owner = owner,
                ProtectedBecause = verdict.Blocked ? verdict.Note : null,
                Caution = verdict.Blocked ? null : verdict.Note
            });

            return !verdict.Blocked;
        }

        // ---- Registry by name ------------------------------------------------------

        private static readonly HashSet<string> SkippedSoftwareKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Classes", "WOW6432Node", "Policies", "RegisteredApplications", "Clients"
        };

        public void RegistryNames()
        {
            foreach (var scope in context.AllScopes)
            {
                foreach (var root in scope.SoftwareKeys)
                {
                    ct.ThrowIfCancellationRequested();

                    foreach (var name in SubKeyNames(root))
                    {
                        if (SkippedSoftwareKeys.Contains(name)) continue;
                        NameMatchedKey(root, name, scope);
                    }
                }
            }
        }

        private void NameMatchedKey(RegistryLocation root, string name, ProfileScope scope)
        {
            var key = root.Child(name);
            var owner = OwnerOf(key, scope);
            var (product, _) = ProgramNames.Best(name, target.PrimaryNames);

            switch (product)
            {
                case NameMatch.Exact:
                    NamedKey(key, name, product, LeftoverConfidence.Likely, ScanMode.Moderate, "Settings key named after the program.", owner);
                    return;

                case NameMatch.Prefix:
                case NameMatch.Contains:
                    NamedKey(key, name, product, LeftoverConfidence.Possible, ScanMode.Expert, "Its name includes the program's name.", owner);
                    return;
            }

            if (ProgramNames.Best(name, target.ProductNames).Match == NameMatch.Exact)
            {
                NamedKey(key, name, NameMatch.Exact, LeftoverConfidence.Possible, ScanMode.Expert,
                    "Has the program's short name, but directly under Software rather than under its publisher's key.", owner);
                return;
            }

            var (publisher, _) = ProgramNames.Best(name, target.PublisherNames);
            if (publisher != NameMatch.Exact) return;

            var found = false;

            foreach (var innerName in SubKeyNames(key))
            {
                var inner = key.Child(innerName);
                var (match, _) = ProgramNames.Best(innerName, target.ProductNames);

                found |= match switch
                {
                    NameMatch.Exact => NamedKey(inner, innerName, match, LeftoverConfidence.Likely, ScanMode.Safe,
                        $"The program's own settings key under its publisher's, {name}.", owner),
                    NameMatch.Prefix => NamedKey(inner, innerName, match, LeftoverConfidence.Likely, ScanMode.Moderate,
                        "Under the publisher's key, named like the program.", owner),
                    NameMatch.Contains => NamedKey(inner, innerName, match, LeftoverConfidence.Possible, ScanMode.Expert,
                        "Under the publisher's key, and its name includes the program's.", owner),
                    _ => false
                };
            }

            if (found && !ProgramNames.IsGeneric(name))
            {
                AddKey(key, LeftoverConfidence.Likely, ScanMode.Moderate,
                    "The publisher's key. Removed only if nothing else is left in it.", owner, onlyIfEmpty: true);
            }
        }

        private bool NamedKey(RegistryLocation key, string name, NameMatch match, LeftoverConfidence confidence,
            ScanMode mode, string reason, string? owner)
        {
            if (!Wants(mode)) return false;

            var verdict = safety.CheckName(name, match);

            Add(new LeftoverItem
            {
                Kind = LeftoverKind.RegistryKey,
                Path = key.DisplayPath,
                RegistryKey = key,
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                Owner = owner,
                ProtectedBecause = verdict.Blocked ? verdict.Note : null,
                Caution = verdict.Blocked ? null : verdict.Note
            });

            return !verdict.Blocked;
        }

        // ---- Shortcuts -------------------------------------------------------------

        public void Shortcuts()
        {
            var iconGroup = target.Program.Values.GetValueOrDefault("Inno Setup: Icon Group");

            foreach (var scope in context.AllScopes)
            {
                foreach (var root in scope.ShortcutFolders)
                {
                    if (!Directory.Exists(root)) continue;
                    ct.ThrowIfCancellationRequested();

                    foreach (var shortcut in Files(root, "*.lnk", depth: 3))
                    {
                        var shortcutTarget = ShortcutFile.ReadTarget(shortcut);
                        var name = Path.GetFileNameWithoutExtension(shortcut);

                        if (shortcutTarget is not null && PointsIntoProgram(shortcutTarget))
                        {
                            AddFile(shortcut, LeftoverConfidence.Certain, ScanMode.Safe, $"Shortcut to {shortcutTarget}.", scope.Owner);
                            continue;
                        }

                        var (match, _) = ProgramNames.Best(name, target.ProductNames);
                        if (match < NameMatch.Prefix) continue;

                        var dead = shortcutTarget is null || (!File.Exists(shortcutTarget) && !Directory.Exists(shortcutTarget));
                        if (!dead) continue;

                        var verdict = safety.CheckName(name, match);
                        Add(new LeftoverItem
                        {
                            Kind = LeftoverKind.File,
                            Path = shortcut,
                            Confidence = LeftoverConfidence.Likely,
                            Mode = ScanMode.Moderate,
                            Reason = shortcutTarget is null
                                ? "Shortcut named after the program, with no target of its own."
                                : $"Shortcut named after the program, pointing at {shortcutTarget}, which no longer exists.",
                            Owner = scope.Owner,
                            ProtectedBecause = verdict.Blocked ? verdict.Note : null,
                            Caution = verdict.Blocked ? null : verdict.Note
                        });
                    }

                    // Start menu folders named after the program or its publisher.
                    foreach (var folder in Directories(root))
                    {
                        var name = Path.GetFileName(folder);

                        if (iconGroup is not null && name.Equals(iconGroup, StringComparison.OrdinalIgnoreCase))
                        {
                            AddFolder(folder, LeftoverConfidence.Certain, ScanMode.Safe, "The Start menu folder the program's setup created.", scope.Owner);
                            continue;
                        }

                        var (match, _) = ProgramNames.Best(name, target.PrimaryNames);
                        var anyName = ProgramNames.Best(name, target.ProductNames).Match;

                        if (match == NameMatch.Exact)
                            NamedFolder(folder, name, match, LeftoverConfidence.Likely, ScanMode.Moderate, "Start menu folder named after the program.", scope.Owner);
                        else if (match == NameMatch.Prefix || anyName == NameMatch.Exact)
                            NamedFolder(folder, name, NameMatch.Prefix, LeftoverConfidence.Possible, ScanMode.Expert, "Start menu folder named like the program.", scope.Owner);
                        else if (ProgramNames.Best(name, target.PublisherNames).Match == NameMatch.Exact && !ProgramNames.IsGeneric(name))
                            AddFolder(folder, LeftoverConfidence.Likely, ScanMode.Moderate,
                                "The publisher's Start menu folder. Removed only if nothing else is left in it.", scope.Owner, onlyIfEmpty: true);
                    }
                }
            }
        }

        // ---- Startup entries -------------------------------------------------------

        public void RunEntries()
        {
            foreach (var scope in context.AllScopes)
            {
                foreach (var runKey in scope.RunKeys)
                {
                    using var key = runKey.Open();
                    if (key is null) continue;

                    foreach (var valueName in ValueNames(key))
                    {
                        var data = key.GetValue(valueName) as string;
                        var owner = OwnerOf(runKey, scope);

                        if (PointsIntoProgram(data))
                        {
                            AddValue(runKey, valueName, LeftoverConfidence.Certain, ScanMode.Safe,
                                $"Starts {data} at sign-in, from the program's folder.", owner);
                            continue;
                        }

                        var (match, _) = ProgramNames.Best(valueName, target.ProductNames);
                        var exe = PathRules.CommandExecutable(data);
                        if (exe is null || File.Exists(exe)) continue;

                        if (match >= NameMatch.Prefix)
                        {
                            AddValue(runKey, valueName, LeftoverConfidence.Likely, ScanMode.Moderate,
                                $"A startup entry named after the program, for {exe}, which no longer exists.", owner);
                        }
                        else if (FolderMatch(exe) != NameMatch.None)
                        {
                            AddValue(runKey, valueName, LeftoverConfidence.Likely, ScanMode.Moderate,
                                $"Starts {exe}, from a folder named after the program, which no longer exists.", owner);
                        }
                    }
                }
            }
        }

        // ---- App Paths -------------------------------------------------------------

        public void AppPaths()
        {
            foreach (var root in context.AppPathsKeys)
            {
                foreach (var name in SubKeyNames(root))
                {
                    var location = root.Child(name);
                    using var key = location.Open();
                    if (key is null) continue;

                    var registered = key.GetValue(null) as string;
                    var path = key.GetValue("Path") as string;
                    var owner = location.Hive == RegistryHive.LocalMachine ? null : CurrentOwner();

                    if (PointsIntoProgram(registered) || PointsIntoProgram(path))
                    {
                        AddKey(location, LeftoverConfidence.Certain, ScanMode.Safe,
                            $"Registers {name} to run from the program's folder.", owner);
                    }
                    else if (target.ExecutableNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                             && PathRules.CommandExecutable(registered) is { } exe && !File.Exists(exe))
                    {
                        AddKey(location, LeftoverConfidence.Likely, ScanMode.Moderate,
                            $"Registers {name}, one of the program's executables, at a path that no longer exists.", owner);
                    }
                }
            }
        }

        // ---- Services --------------------------------------------------------------

        public void Services()
        {
            if (context.ServicesKey is not { } root) return;

            foreach (var name in SubKeyNames(root))
            {
                using var key = root.Child(name).Open();
                if (key is null) continue;

                // Only real services: Win32 own/shared process types, not drivers.
                if (key.GetValue("Type") is not int type || (type & 0x30) == 0) continue;

                var image = key.GetValue("ImagePath") as string;

                if (PointsIntoProgram(image))
                {
                    AddService(name, image, LeftoverConfidence.Certain, ScanMode.Safe, "A service that runs from the program's folder.");
                    continue;
                }

                var display = key.GetValue("DisplayName") as string;
                if (display?.StartsWith('@') == true) display = null;

                var exe = PathRules.CommandExecutable(image);
                var missing = exe is not null && !File.Exists(exe);

                // A missing executable is evidence enough for any of the names; a live one needs a strong name.
                var names = missing ? target.ProductNames : target.PrimaryNames;
                var match = new[] { name, display }.OfType<string>()
                    .Select(n => ProgramNames.Best(n, names).Match)
                    .DefaultIfEmpty(NameMatch.None)
                    .Max();

                if (match < NameMatch.Prefix)
                {
                    if (missing && FolderMatch(exe!) != NameMatch.None)
                    {
                        AddService(name, image, LeftoverConfidence.Likely, ScanMode.Moderate,
                            $"A service for {exe}, in a folder named after the program, which no longer exists.");
                    }

                    continue;
                }

                AddService(name, image,
                    missing ? LeftoverConfidence.Likely : LeftoverConfidence.Possible,
                    missing ? ScanMode.Moderate : ScanMode.Expert,
                    missing
                        ? $"A service named after the program, for {exe}, which no longer exists."
                        : "A service named after the program. Its program is still present, so check before removing it.");
            }
        }

        private void AddService(string name, string? imagePath, LeftoverConfidence confidence, ScanMode mode, string reason)
        {
            var verdict = safety.JudgeService(name, imagePath);

            Add(new LeftoverItem
            {
                Kind = LeftoverKind.Service,
                Path = name,
                RegistryKey = context.ServicesKey?.Child(name),
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                ProtectedBecause = verdict?.Reason,
                KeptAlways = verdict?.Absolute ?? false,
                NeedsElevation = true
            });
        }

        // ---- Scheduled tasks -------------------------------------------------------

        public void ScheduledTasks()
        {
            if (!context.IncludeScheduledTasks) return;

            foreach (var task in ScheduledTaskReader.ReadAll(ct))
            {
                if (safety.CheckTask(task.Path) is not null) continue;

                if (task.Commands.Any(PointsIntoProgram))
                {
                    AddTask(task, LeftoverConfidence.Certain, ScanMode.Safe, $"Runs {task.Commands.First(PointsIntoProgram)}, from the program's folder.");
                    continue;
                }

                var (match, _) = ProgramNames.Best(task.Name, target.PrimaryNames);
                var anyName = ProgramNames.Best(task.Name, target.ProductNames).Match;
                var executables = task.Commands.Select(PathRules.CommandExecutable).OfType<string>().ToList();
                var missing = executables.FirstOrDefault(e => !File.Exists(e));
                var folderMatch = executables.Select(FolderMatch).DefaultIfEmpty(NameMatch.None).Max();

                if (match is NameMatch.Exact or NameMatch.Prefix || (missing is not null && anyName >= NameMatch.Prefix))
                {
                    AddTask(task,
                        LeftoverConfidence.Likely, ScanMode.Moderate,
                        missing is not null
                            ? $"A scheduled task named after the program, for {missing}, which no longer exists."
                            : "A scheduled task named after the program.");
                }
                else if (missing is not null && folderMatch != NameMatch.None)
                {
                    AddTask(task, LeftoverConfidence.Likely, ScanMode.Moderate,
                        $"Runs {missing}, from a folder named after the program, which no longer exists.");
                }
                else if (match == NameMatch.Contains || anyName >= NameMatch.Prefix || folderMatch >= NameMatch.Prefix)
                {
                    AddTask(task, LeftoverConfidence.Possible, ScanMode.Expert, "A scheduled task whose name or program path includes the program's name.");
                }
            }
        }

        /// <summary>
        /// How well the folders an executable sits in match the program: a task left pointing at
        /// "…\Adobe Creative Cloud Experience\CCXProcess.exe" belongs to Creative Cloud even
        /// though its own name says nothing.
        /// </summary>
        private NameMatch FolderMatch(string executable)
        {
            var best = NameMatch.None;
            var folder = Path.GetDirectoryName(executable);

            while (!string.IsNullOrEmpty(folder) && Path.GetPathRoot(folder) != folder)
            {
                var segment = Path.GetFileName(folder);

                // Any name may match a folder exactly; only a strong one may match part of it.
                var match = ProgramNames.Best(segment, target.PrimaryNames).Match;
                if (ProgramNames.Best(segment, target.ProductNames).Match == NameMatch.Exact) match = NameMatch.Exact;

                if (match > best) best = match;
                folder = Path.GetDirectoryName(folder);
            }

            return best;
        }

        private void AddTask(ScheduledTaskInfo task, LeftoverConfidence confidence, ScanMode mode, string reason)
        {
            // Found by name, it may still run another installed program; then it is theirs.
            var verdict = safety.JudgeTask(task.Path, task.Commands);

            Add(new LeftoverItem
            {
                Kind = LeftoverKind.ScheduledTask,
                Path = task.Path,
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                ProtectedBecause = verdict?.Reason,
                KeptAlways = verdict?.Absolute ?? false,

                // A task the account created for itself it can also delete.
                NeedsElevation = !task.RunsAsCurrentUser
            });
        }

        // ---- COM classes and file types -------------------------------------------

        private static readonly string[] SharedVerbContainers = ["*", "Directory", @"Directory\Background", "Drive", "Folder", "AllFilesystemObjects"];

        public void Classes()
        {
            if (!Wants(ScanMode.Moderate) || _references.Count == 0) return;

            // Every root's COM classes first, so that what refers to a class by its CLSID is judged
            // knowing every registration of it, in every hive and both registry views.
            foreach (var scope in context.AllScopes)
            {
                foreach (var classes in scope.ClassesKeys)
                {
                    ct.ThrowIfCancellationRequested();
                    ComClasses(classes.Child("CLSID"), OwnerOf(classes, scope));
                }
            }

            FindSharedClsids();

            foreach (var scope in context.AllScopes)
            {
                foreach (var classes in scope.ClassesKeys)
                {
                    ct.ThrowIfCancellationRequested();
                    var owner = OwnerOf(classes, scope);

                    TypeLibraries(classes.Child("TypeLib"), owner);

                    // The WOW6432Node view only redirects CLSIDs; its other keys are shared.
                    if (classes.LeafName.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) continue;

                    ProgIds(classes, owner);

                    foreach (var container in SharedVerbContainers) Verbs(classes.Child(container), owner);
                }
            }
        }

        /// <summary>The program's COM classes that are registered nowhere else for anything else.</summary>
        private readonly HashSet<string> _exclusiveClsids = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A CLSID can be registered in several places: 7-Zip's 32-bit and 64-bit builds use the
        /// same one, and so do a per-user and a machine-wide copy. The registration pointing into
        /// this program is its leftover, but a context-menu handler, shell-extension approval or
        /// ProgID naming that CLSID serves the other copy too, and stays while any registration of
        /// it points somewhere else.
        /// </summary>
        private void FindSharedClsids()
        {
            _exclusiveClsids.Clear();

            foreach (var clsid in _leftoverClsids)
            {
                var elsewhere = context.AllScopes
                    .SelectMany(s => s.ClassesKeys)
                    .SelectMany(classes => new[] { "InprocServer32", "LocalServer32" }.Select(server => classes.Child($@"CLSID\{clsid}\{server}")))
                    .Any(location =>
                    {
                        using var key = location.Open();
                        return key?.GetValue(null) is string server && server.Trim().Length > 0 && !PointsIntoProgram(server);
                    });

                if (!elsewhere) _exclusiveClsids.Add(clsid);
            }
        }

        private void ComClasses(RegistryLocation clsidRoot, string? owner)
        {
            foreach (var clsid in SubKeyNames(clsidRoot))
            {
                var location = clsidRoot.Child(clsid);

                foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                {
                    using var key = location.Child(server).Open();
                    if (!PointsIntoProgram(key?.GetValue(null) as string)) continue;

                    _leftoverClsids.Add(clsid);
                    AddKey(location, LeftoverConfidence.Certain, ScanMode.Moderate, "A COM class served from the program's folder.", owner);
                    break;
                }
            }
        }

        private void TypeLibraries(RegistryLocation typeLibRoot, string? owner)
        {
            foreach (var library in SubKeyNames(typeLibRoot))
            {
                var location = typeLibRoot.Child(library);
                var found = false;

                foreach (var version in SubKeyNames(location))
                {
                    foreach (var locale in SubKeyNames(location.Child(version)))
                    {
                        foreach (var platform in new[] { "win32", "win64" })
                        {
                            using var key = location.Child(version).Child(locale).Child(platform).Open();
                            if (PointsIntoProgram(key?.GetValue(null) as string)) found = true;
                        }
                    }
                }

                if (found) AddKey(location, LeftoverConfidence.Certain, ScanMode.Moderate, "A type library registered from the program's folder.", owner);
            }
        }

        private void ProgIds(RegistryLocation classes, string? owner)
        {
            var names = SubKeyNames(classes);

            foreach (var name in names)
            {
                if (name.StartsWith('.') || name.Equals("CLSID", StringComparison.OrdinalIgnoreCase)) continue;

                var location = classes.Child(name);

                using (var clsid = location.Child("CLSID").Open())
                {
                    if (clsid?.GetValue(null) is string id && _exclusiveClsids.Contains(id))
                    {
                        _leftoverProgIds.Add(name);
                        AddKey(location, LeftoverConfidence.Certain, ScanMode.Moderate, $"Names the COM class {id}, which is being removed.", owner);
                        continue;
                    }
                }

                ProgIdVerbs(location, owner);
            }

            // File extensions keep their keys, but lose the parts that name what is being removed.
            foreach (var extension in names.Where(n => n.StartsWith('.')))
            {
                var location = classes.Child(extension);

                using (var key = location.Open())
                {
                    if (key?.GetValue(null) is string progId && _leftoverProgIds.Contains(progId))
                    {
                        AddValue(location, string.Empty, LeftoverConfidence.Likely, ScanMode.Moderate,
                            $"Makes {extension} files open with {progId}, which is being removed.", owner);
                    }
                }

                using (var openWith = location.Child("OpenWithProgids").Open())
                {
                    if (openWith is null) continue;

                    foreach (var value in ValueNames(openWith).Where(_leftoverProgIds.Contains))
                    {
                        AddValue(location.Child("OpenWithProgids"), value, LeftoverConfidence.Certain, ScanMode.Moderate,
                            $"Lists {value}, which is being removed, in {extension}'s Open with menu.", owner);
                    }
                }
            }
        }

        private void Verbs(RegistryLocation container, string? owner)
        {
            var shell = container.Child("shell");

            foreach (var verb in SubKeyNames(shell))
            {
                using var command = shell.Child(verb).Child("command").Open();
                if (PointsIntoProgram(command?.GetValue(null) as string))
                {
                    AddKey(shell.Child(verb), LeftoverConfidence.Certain, ScanMode.Moderate,
                        "A right-click menu entry that runs the program.", owner);
                }
            }

            var handlers = container.Child(@"shellex\ContextMenuHandlers");

            foreach (var handler in SubKeyNames(handlers))
            {
                using var key = handlers.Child(handler).Open();
                var clsid = key?.GetValue(null) as string;

                if (_exclusiveClsids.Contains(handler) || (clsid is not null && _exclusiveClsids.Contains(clsid)))
                {
                    AddKey(handlers.Child(handler), LeftoverConfidence.Certain, ScanMode.Moderate,
                        "A right-click menu handler whose COM class is being removed.", owner);
                }
            }
        }

        /// <summary>
        /// A ProgID is the program's own only when everything it does points at the program: every
        /// verb, and its icon if it has one. Then it goes whole. Otherwise the program has only
        /// added to someone else's file type — "Edit with Notepad++" on VBSFile — and just the
        /// verbs and icon that point at it go, leaving VBSFile itself alone.
        /// </summary>
        private void ProgIdVerbs(RegistryLocation progId, string? owner)
        {
            var shell = progId.Child("shell");
            var verbs = SubKeyNames(shell);
            var ours = new List<string>();

            foreach (var verb in verbs)
            {
                using var command = shell.Child(verb).Child("command").Open();
                if (PointsIntoProgram(command?.GetValue(null) as string)) ours.Add(verb);
            }

            string? icon;
            bool hasIcon;
            using (var iconKey = progId.Child("DefaultIcon").Open())
            {
                hasIcon = iconKey is not null;
                icon = iconKey?.GetValue(null) as string;
            }

            var iconOurs = PointsIntoProgram(icon);

            if (ours.Count == 0 && !iconOurs) return;

            if (ours.Count == verbs.Length && (iconOurs || !hasIcon) && ours.Count > 0)
            {
                _leftoverProgIds.Add(progId.LeafName);
                AddKey(progId, LeftoverConfidence.Certain, ScanMode.Moderate, "A file type that only opens with the program.", owner);
                return;
            }

            foreach (var verb in ours)
            {
                AddKey(shell.Child(verb), LeftoverConfidence.Certain, ScanMode.Moderate,
                    $"An action the program added to {progId.LeafName}, which runs it.", owner);
            }

            if (iconOurs)
            {
                AddValue(progId.Child("DefaultIcon"), string.Empty, LeftoverConfidence.Likely, ScanMode.Moderate,
                    $"Gives {progId.LeafName} files an icon from the program's folder.", owner);
            }
        }

        // ---- Shell extensions and Default Apps ------------------------------------

        public void ShellRegistrations()
        {
            if (!Wants(ScanMode.Moderate)) return;

            foreach (var scope in context.AllScopes)
            {
                foreach (var software in scope.SoftwareKeys)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_exclusiveClsids.Count == 0) break;

                    var owner = OwnerOf(software, scope);
                    var currentVersion = software.Child(@"Microsoft\Windows\CurrentVersion");

                    // Explorer loads only the shell extensions it has approved, and the approval
                    // outlives the class it names.
                    var approved = currentVersion.Child(@"Shell Extensions\Approved");
                    using (var key = approved.Open())
                    {
                        if (key is not null)
                        {
                            foreach (var value in ValueNames(key).Where(_exclusiveClsids.Contains))
                            {
                                AddValue(approved, value, LeftoverConfidence.Certain, ScanMode.Moderate,
                                    $"Approves the shell extension {value}, whose COM class is being removed.", owner);
                            }
                        }
                    }

                    foreach (var (container, what) in new[]
                             {
                                 (@"Explorer\ShellIconOverlayIdentifiers", "An Explorer icon overlay"),
                                 (@"Explorer\Browser Helper Objects", "An Internet Explorer add-on")
                             })
                    {
                        var root = currentVersion.Child(container);

                        foreach (var name in SubKeyNames(root))
                        {
                            using var key = root.Child(name).Open();
                            var clsid = key?.GetValue(null) as string;

                            if (_exclusiveClsids.Contains(name.Trim()) || (clsid is not null && _exclusiveClsids.Contains(clsid)))
                            {
                                AddKey(root.Child(name), LeftoverConfidence.Certain, ScanMode.Moderate,
                                    $"{what} whose COM class is being removed.", owner);
                            }
                        }
                    }
                }

                DefaultApps(scope);
            }
        }

        /// <summary>
        /// A program's line in Default Apps: a RegisteredApplications value naming its capabilities
        /// key. Offered when that key is already gone, or is itself being removed. The list is
        /// shared by both registry views, so only the first Software key is read.
        /// </summary>
        private void DefaultApps(ProfileScope scope)
        {
            if (scope.SoftwareKeys.FirstOrDefault() is not { } software || software.Parent is not { } hiveRoot) return;

            var registered = software.Child("RegisteredApplications");
            using var key = registered.Open();
            if (key is null) return;

            foreach (var name in ValueNames(key))
            {
                if (key.GetValue(name) is not string capabilities || capabilities.Trim('\\').Length == 0) continue;
                if (ProgramNames.Best(name, target.PrimaryNames).Match != NameMatch.Exact) continue;

                var capabilitiesKey = hiveRoot.Child(capabilities.Trim('\\'));
                var gone = !capabilitiesKey.Exists();
                var going = !options.ProgramStillInstalled && _items.Values.Any(i =>
                    i is { Kind: LeftoverKind.RegistryKey, OnlyIfEmpty: false, Recommended: true } && capabilitiesKey.IsWithin(i.RegistryKey!));

                if (!gone && !going) continue;

                AddValue(registered, name, LeftoverConfidence.Likely, ScanMode.Moderate,
                    gone
                        ? $"Lists {name} in Default Apps, pointing at settings that no longer exist."
                        : $"Lists {name} in Default Apps, pointing at settings that are being removed.",
                    OwnerOf(registered, scope));
            }
        }

        // ---- PATH --------------------------------------------------------------------

        public void PathEntries()
        {
            foreach (var scope in context.AllScopes)
            {
                if (scope.EnvironmentKey is not { } environment) continue;

                string? raw;
                using (var key = environment.Open())
                {
                    raw = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                }

                if (raw is null) continue;

                foreach (var entry in EnvironmentPath.Split(raw))
                {
                    var expanded = ExpandFor(entry, scope);
                    if (expanded is null) continue;

                    if (PointsIntoProgram(expanded))
                    {
                        AddPathEntry(environment, entry, expanded, LeftoverConfidence.Certain, ScanMode.Safe,
                            $"Adds {expanded} to PATH, inside the program's folder.", scope);
                        continue;
                    }

                    // A folder named after the program that no longer exists: the classic entry an
                    // uninstaller forgets, which every console window then searches in vain.
                    if (!Directory.Exists(expanded) && FolderMatch(Path.Combine(expanded, "x")) != NameMatch.None)
                    {
                        AddPathEntry(environment, entry, expanded, LeftoverConfidence.Likely, ScanMode.Moderate,
                            $"Adds {expanded} to PATH: a folder named after the program, which no longer exists.", scope);
                    }
                }
            }
        }

        /// <summary>
        /// A PATH entry as it applies to the profile it belongs to. Another account's
        /// %USERPROFILE% is their folder, not this process's.
        /// </summary>
        private static string? ExpandFor(string entry, ProfileScope scope)
        {
            var text = entry.Trim().Trim('"');

            if (scope.ProfileRoot is { } root)
            {
                text = text
                    .Replace("%USERPROFILE%", root, StringComparison.OrdinalIgnoreCase)
                    .Replace("%LOCALAPPDATA%", Path.Combine(root, "AppData", "Local"), StringComparison.OrdinalIgnoreCase)
                    .Replace("%APPDATA%", Path.Combine(root, "AppData", "Roaming"), StringComparison.OrdinalIgnoreCase);
            }

            return PathRules.Normalize(text);
        }

        private void AddPathEntry(RegistryLocation environment, string entry, string expanded, LeftoverConfidence confidence,
            ScanMode mode, string reason, ProfileScope scope)
        {
            var verdict = safety.JudgePathEntry(environment, "Path", expanded);

            Add(new LeftoverItem
            {
                Kind = LeftoverKind.PathEntry,
                Path = entry,
                RegistryKey = environment,
                ValueName = "Path",
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                Owner = OwnerOf(environment, scope),
                ProtectedBecause = verdict?.Reason,
                KeptAlways = verdict?.Absolute ?? false
            });
        }

        // ---- Firewall ------------------------------------------------------------------

        public void FirewallRules()
        {
            if (context.Firewall is not { } firewall) return;

            foreach (var group in firewall.ReadSummaries(ct).GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var rules = group.ToList();
                var apps = rules.Select(r => PathRules.Normalize(r.ApplicationName)).ToList();

                // Rules are removed by name, so a name shared with a rule for anything else — or
                // for nothing in particular — is not the program's to remove.
                if (apps.Any(a => a is null)) continue;

                var programs = apps.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var count = rules.Count > 1 ? $" ({rules.Count} rules)" : string.Empty;

                if (programs.All(PointsIntoProgram))
                {
                    AddFirewall(group.Key, rules, LeftoverConfidence.Certain, ScanMode.Safe,
                        $"Lets {programs[0]} through Windows Firewall{count}.");
                }
                else if (programs.All(p => !File.Exists(p) && FolderMatch(p) != NameMatch.None))
                {
                    AddFirewall(group.Key, rules, LeftoverConfidence.Likely, ScanMode.Moderate,
                        $"Lets {programs[0]} through Windows Firewall{count}. It no longer exists, and its folder is named after the program.");
                }
            }
        }

        private void AddFirewall(string name, IReadOnlyList<FirewallRuleInfo> rules, LeftoverConfidence confidence, ScanMode mode, string reason)
        {
            var verdict = safety.JudgeFirewallRules(rules);

            Add(new LeftoverItem
            {
                Kind = LeftoverKind.FirewallRule,
                Path = name,
                Confidence = confidence,
                Mode = mode,
                Reason = reason,
                ProtectedBecause = verdict?.Reason,
                KeptAlways = verdict?.Absolute ?? false,
                NeedsElevation = true
            });
        }

        // ---- Windows' own records ------------------------------------------------------

        public void WindowsRecords()
        {
            if (!Wants(ScanMode.Moderate)) return;

            foreach (var scope in context.AllScopes)
            {
                foreach (var traceKey in scope.TraceKeys)
                {
                    ct.ThrowIfCancellationRequested();

                    // Windows Installer's folder list is its own bookkeeping while a product is
                    // installed; only once the product is gone is an entry for its folder stale.
                    var installerFolders = traceKey.SubKey.EndsWith(@"\Installer\Folders", StringComparison.OrdinalIgnoreCase);
                    if (installerFolders && options.ProgramStillInstalled) continue;

                    using var key = traceKey.Open();
                    if (key is null) continue;

                    foreach (var name in ValueNames(key))
                    {
                        var traced = LeftoverSafety.TracedPath(name);
                        if (!PointsIntoProgram(traced)) continue;

                        AddValue(traceKey, name, LeftoverConfidence.Likely, ScanMode.Moderate, TraceReason(traceKey, traced!), OwnerOf(traceKey, scope));
                    }
                }
            }

            // A shared-file count for a file that no longer exists counts nothing.
            foreach (var sharedKey in context.SharedDllsKeys)
            {
                using var key = sharedKey.Open();
                if (key is null) continue;

                foreach (var name in ValueNames(key))
                {
                    if (!PointsIntoProgram(name) || File.Exists(name)) continue;

                    AddValue(sharedKey, name, LeftoverConfidence.Likely, ScanMode.Moderate,
                        $"Windows' count of the programs sharing {name}, which no longer exists.", null);
                }
            }

            EventLogSources();
        }

        private static string TraceReason(RegistryLocation key, string traced) => key.SubKey switch
        {
            var k when k.EndsWith(@"\MuiCache", StringComparison.OrdinalIgnoreCase) => $"The name Explorer cached for {traced}, to show in menus.",
            var k when k.EndsWith(@"\Store", StringComparison.OrdinalIgnoreCase) => $"Windows' compatibility record for {traced}.",
            var k when k.EndsWith(@"\Layers", StringComparison.OrdinalIgnoreCase) => $"Compatibility settings chosen for {traced}.",
            var k when k.EndsWith(@"\Folders", StringComparison.OrdinalIgnoreCase) => $"Windows Installer's record of the folder {traced}.",
            _ => $"Windows' record of {traced}."
        };

        private void EventLogSources()
        {
            if (context.EventLogKey is not { } eventLog) return;

            foreach (var log in SubKeyNames(eventLog))
            {
                foreach (var source in SubKeyNames(eventLog.Child(log)))
                {
                    ct.ThrowIfCancellationRequested();
                    if (source.Equals(log, StringComparison.OrdinalIgnoreCase)) continue;

                    var location = eventLog.Child(log).Child(source);
                    List<string> files;

                    using (var key = location.Open())
                    {
                        if (key is null) continue;

                        files = new[] { "EventMessageFile", "CategoryMessageFile", "ParameterMessageFile" }
                            .Select(v => key.GetValue(v) as string)
                            .OfType<string>()
                            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            .Select(PathRules.Normalize)
                            .OfType<string>()
                            .ToList();
                    }

                    if (files.Count == 0) continue;

                    // .NET's shared message file says nothing about whose a source is; such a
                    // source is matched by its name instead.
                    var specific = files.Where(f => !LeftoverSafety.IsGenericMessageFile(f)).ToList();

                    if (specific.Count > 0 && specific.All(PointsIntoProgram))
                    {
                        AddKey(location, LeftoverConfidence.Likely, ScanMode.Moderate,
                            $"Registers \"{source}\" in the {log} event log, with its messages in the program's folder.", null);
                    }
                    else if (specific.Count == 0 && ProgramNames.Best(source, target.PrimaryNames).Match == NameMatch.Exact
                             && safety.CheckName(source, NameMatch.Exact) is { Blocked: false } verdict)
                    {
                        // Only the name ties it to the program — Windows' own sources use the
                        // shared file too — so it is offered but never pre-selected.
                        AddKey(location, LeftoverConfidence.Likely, ScanMode.Moderate,
                            $"Registers \"{source}\", named after the program, in the {log} event log.", null,
                            caution: verdict.Note ?? "Its messages come from .NET's shared file, so only its name ties it to the program.");
                    }
                }
            }
        }

        // ---- Expert: Temp, Documents, deep registry --------------------------------

        public void TempAndDocuments()
        {
            if (!Wants(ScanMode.Expert)) return;

            foreach (var scope in context.AllScopes)
            {
                foreach (var root in scope.TempFolders)
                {
                    foreach (var child in Directories(root))
                    {
                        var name = Path.GetFileName(child);
                        var (match, _) = ProgramNames.Best(name, target.PrimaryNames);
                        if (match >= NameMatch.Prefix)
                            NamedFolder(child, name, match, LeftoverConfidence.Possible, ScanMode.Expert, "A temporary folder named after the program.", scope.Owner);
                    }
                }

                foreach (var root in scope.DocumentFolders)
                {
                    foreach (var child in Directories(root))
                    {
                        var name = Path.GetFileName(child);
                        var (match, _) = ProgramNames.Best(name, target.PrimaryNames);
                        if (match != NameMatch.Exact) continue;

                        Add(new LeftoverItem
                        {
                            Kind = LeftoverKind.Folder,
                            Path = child,
                            Confidence = LeftoverConfidence.Possible,
                            Mode = ScanMode.Expert,
                            Reason = "A folder in Documents named after the program.",
                            Owner = scope.Owner,
                            Caution = "Documents holds your own files. Look inside before removing this."
                        });
                    }
                }
            }
        }

        public void DeepRegistry()
        {
            if (!Wants(ScanMode.Expert) || _references.Count == 0) return;

            var deadline = Stopwatch.StartNew();
            var truncated = false;

            foreach (var scope in context.AllScopes)
            {
                foreach (var root in scope.SoftwareKeys)
                {
                    if (!Walk(root, depth: 0, scope)) truncated = true;
                    if (deadline.Elapsed > DeepSearchBudget) break;
                }
            }

            if (truncated)
                Notes.Add($"The registry search for the install path stopped after {DeepSearchBudget.TotalSeconds:0} seconds, so it may not be complete.");

            bool Walk(RegistryLocation location, int depth, ProfileScope scope)
            {
                if (deadline.Elapsed > DeepSearchBudget) return false;
                ct.ThrowIfCancellationRequested();

                if (depth > 0 && SkipInDeepSearch(location)) return true;

                using (var key = location.Open())
                {
                    if (key is null) return true;

                    foreach (var valueName in ValueNames(key))
                    {
                        object? data;
                        try { data = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { continue; }

                        var text = data switch
                        {
                            string s => Environment.ExpandEnvironmentVariables(s),
                            string[] lines => string.Join('\n', lines),
                            _ => null
                        };

                        if (PointsIntoProgram(text))
                        {
                            AddValue(location, valueName, LeftoverConfidence.Possible, ScanMode.Expert,
                                "Mentions the program's install folder.", OwnerOf(location, scope));
                        }
                    }
                }

                if (depth >= 6) return true;

                foreach (var name in SubKeyNames(location))
                {
                    if (!Walk(location.Child(name), depth + 1, scope)) return false;
                }

                return true;
            }
        }

        private static bool SkipInDeepSearch(RegistryLocation location)
        {
            var path = location.SubKey;
            return path.EndsWith(@"\Classes", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(@"\WOW6432Node", StringComparison.OrdinalIgnoreCase) && location.Depth <= 3
                   || path.Contains(@"\CurrentVersion\Installer", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\CurrentVersion\Uninstall", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(@"\Policies", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\Windows\CurrentVersion\Explorer\", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\Windows\CurrentVersion\UFH", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\Windows\CurrentVersion\Search", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\Windows NT\CurrentVersion\AppCompatFlags", StringComparison.OrdinalIgnoreCase);
        }

        // ---- Finishing -------------------------------------------------------------

        public void Finish()
        {
            var processProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var reviewed = new List<LeftoverItem>();

            // Everything refused, including what is not listed, so no folder or key that contains
            // one of them can be offered whole.
            var refused = new List<LeftoverItem>();

            foreach (var item in _items.Values)
            {
                ct.ThrowIfCancellationRequested();

                // Whatever flagged it when it was found, and the policy's view of where it is; the
                // firmer of the two stands, so an absolute rule is never softened by a milder one.
                var found = item.IsProtected ? new SafetyVerdict(item.ProtectedBecause!, item.KeptAlways) : null;
                var verdict = SafetyVerdict.Firmest(found, JudgeWhere(item));

                if (verdict is not null) refused.Add(item with { ProtectedBecause = verdict.Reason, KeptAlways = verdict.Absolute });

                // Windows' own files and keys that an installer happened to record are noise, not
                // news: count them rather than list them.
                if (verdict is { Absolute: true } && verdict.Reason.StartsWith("Part of Windows", StringComparison.Ordinal))
                {
                    _windowsItemsSkipped++;
                    continue;
                }

                // A protected "only if empty" folder or key was never going to be touched; listing
                // it would only explain a non-event.
                if (verdict is not null && item.OnlyIfEmpty) continue;

                // Services and firewall rules always need rights; a task was judged by whose it is
                // when it was found.
                var needsElevation = item.Kind switch
                {
                    LeftoverKind.File or LeftoverKind.Folder => !PathRules.IsWithin(item.Path, processProfile),
                    LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.PathEntry
                        => item.RegistryKey!.Hive != RegistryHive.CurrentUser,
                    LeftoverKind.ScheduledTask => item.NeedsElevation,
                    _ => true
                };

                reviewed.Add(item with
                {
                    ProtectedBecause = verdict?.Reason,
                    KeptAlways = verdict?.Absolute ?? false,
                    Caution = verdict is null ? item.Caution : null,
                    NeedsElevation = needsElevation
                });
            }

            // A folder or key holding something that must be kept cannot go whole: removing it
            // would take the protected item with it. Found by one route and refused by another —
            // a publisher folder whose product subfolder turned out to be another program's — is
            // exactly how that would otherwise slip through. It inherits the child's firmness: a
            // folder holding something Windows needs is kept whatever anyone chooses.
            for (var i = 0; i < reviewed.Count; i++)
            {
                var parent = reviewed[i];
                if (parent.OnlyIfEmpty || parent.KeptAlways || parent.Kind is not (LeftoverKind.Folder or LeftoverKind.RegistryKey)) continue;

                var kept = refused
                    .Where(child => Covers(parent, child))
                    .OrderByDescending(child => child.KeptAlways)
                    .FirstOrDefault();

                if (kept is null || (parent.IsProtected && !kept.KeptAlways)) continue;

                reviewed[i] = parent with
                {
                    ProtectedBecause = $"Contains {kept.DisplayPath}, which must be kept: {kept.ProtectedBecause}",
                    KeptAlways = kept.KeptAlways,
                    Caution = null
                };
            }

            var collapsed = Collapse(reviewed);

            // Only-if-empty folders are not sized: whatever is in them is either listed on its own
            // or is not being removed, and counting it would inflate the total.
            var sized = collapsed
                .Select(i => i.Kind switch
                {
                    LeftoverKind.Folder when !i.IsProtected && !i.OnlyIfEmpty => i with { SizeBytes = FolderSize(i.Path) },
                    LeftoverKind.File => i with { SizeBytes = FileSize(i.Path) },
                    _ => i
                })
                .OrderBy(i => i.Category)
                .ThenBy(i => i.IsProtected)
                .ThenByDescending(i => i.Confidence)
                .ThenBy(i => i.DisplayPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (_windowsItemsSkipped > 0)
            {
                Notes.Add($"{_windowsItemsSkipped:N0} item(s) inside Windows' own folders or configuration were left alone, "
                          + "including shared components the installer recorded.");
            }

            if (_sharedDllsKept > 0)
            {
                Notes.Add($"{_sharedDllsKept:N0} file(s) the installer recorded are still counted as in use by other programs "
                          + "(SharedDLLs), so Windows kept them on purpose and they were left alone.");
            }

            if (options.ProgramStillInstalled)
            {
                Notes.Add($"{ProgramName} is still installed. Removing these items breaks it rather than uninstalling it "
                          + "cleanly — only do that if its own uninstaller will not work.");
            }

            Items = sized;
        }

        /// <summary>
        /// Drops items already covered by a folder or key being removed whole, as long as that
        /// covering item is at least as certain. A certain file inside a merely possible folder
        /// stays listed, so selecting the certain things never depends on accepting a guess.
        /// </summary>
        private static List<LeftoverItem> Collapse(List<LeftoverItem> items)
        {
            var covering = items
                .Where(i => !i.IsProtected && !i.OnlyIfEmpty && i.Kind is LeftoverKind.Folder or LeftoverKind.RegistryKey)
                .ToList();

            // A protected item is never folded away: it is the explanation for what is kept.
            return items.Where(item => item.IsProtected || !covering.Any(parent =>
                    !ReferenceEquals(parent, item)
                    && parent.Confidence >= item.Confidence
                    && Covers(parent, item)))
                .ToList();
        }

        /// <summary>
        /// The policy's view of where an item is. Services and firewall rules were judged when
        /// they were found, by what they run, which the item itself does not record.
        /// </summary>
        private SafetyVerdict? JudgeWhere(LeftoverItem item) => item.Kind switch
        {
            LeftoverKind.File or LeftoverKind.Folder => safety.JudgePath(item.Path),
            LeftoverKind.RegistryKey => safety.JudgeRegistryKey(item.RegistryKey!),
            LeftoverKind.RegistryValue => safety.JudgeRegistryValue(item.RegistryKey!, item.ValueName),
            LeftoverKind.ScheduledTask => safety.JudgeTask(item.Path),
            LeftoverKind.PathEntry => safety.JudgePathEntry(item.RegistryKey!, item.ValueName, item.Path),
            _ => null
        };

        private static bool Covers(LeftoverItem parent, LeftoverItem item)
        {
            if (parent.Kind == LeftoverKind.Folder && item.Kind is LeftoverKind.File or LeftoverKind.Folder)
                return PathRules.IsStrictlyWithin(item.Path, parent.Path);

            if (parent.Kind == LeftoverKind.RegistryKey && item.RegistryKey is { } key
                && item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue)
            {
                return item.Kind == LeftoverKind.RegistryValue
                    ? key.IsWithin(parent.RegistryKey!)
                    : key.IsWithin(parent.RegistryKey!) && !key.Equals(parent.RegistryKey);
            }

            return false;
        }

        // ---- Safe enumeration ------------------------------------------------------

        private static readonly EnumerationOptions TopLevel = new()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        private static IEnumerable<string> Directories(string root)
        {
            if (!Directory.Exists(root)) return [];

            try { return Directory.EnumerateDirectories(root, "*", TopLevel).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }

        private static IEnumerable<string> Files(string root, string pattern, int depth)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = depth,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).Take(5000).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        private static string[] SubKeyNames(RegistryLocation location)
        {
            using var key = location.Open();
            if (key is null) return [];

            try { return key.GetSubKeyNames(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return []; }
        }

        private static string[] ValueNames(RegistryKey key)
        {
            try { return key.GetValueNames(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return []; }
        }

        private static long? FileSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>A folder's size, without following links and giving up past a sensible limit.</summary>
        private long? FolderSize(string path)
        {
            try
            {
                long total = 0;
                var count = 0;
                var watch = Stopwatch.StartNew();

                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint
                         }))
                {
                    total += file.Length;
                    if (++count % 1000 == 0 && (watch.Elapsed > TimeSpan.FromSeconds(3) || ct.IsCancellationRequested)) return total;
                }

                return total;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}

/// <summary>A scheduled task's path, name, the commands its actions run, and whether it runs as the current account.</summary>
public sealed record ScheduledTaskInfo(string Path, string Name, IReadOnlyList<string> Commands, bool RunsAsCurrentUser = false);

/// <summary>Reads every scheduled task through the Task Scheduler's COM interface, late-bound like the helper installer.</summary>
public static class ScheduledTaskReader
{
    public static IReadOnlyList<ScheduledTaskInfo> ReadAll(CancellationToken ct = default)
    {
        var tasks = new List<ScheduledTaskInfo>();

        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return tasks;

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            Visit(service.GetFolder(@"\"), tasks, ct, depth: 0);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }

        return tasks;
    }

    /// <summary>A task principal naming this account, by name ("DOMAIN\user" or "user") or by SID.</summary>
    private static bool IsCurrentUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var name = identity.Name;
        var shortName = name[(name.IndexOf('\\') + 1)..];

        return userId.Equals(name, StringComparison.OrdinalIgnoreCase)
               || userId.Equals(shortName, StringComparison.OrdinalIgnoreCase)
               || userId.Equals(identity.User?.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static void Visit(dynamic folder, List<ScheduledTaskInfo> tasks, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 8) return;

        try
        {
            string folderPath = folder.Path;
            if (folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

            foreach (dynamic task in folder.GetTasks(1))
            {
                try
                {
                    var commands = new List<string>();
                    foreach (dynamic action in task.Definition.Actions)
                    {
                        if ((int)action.Type != 0) continue;   // TASK_ACTION_EXEC
                        string path = action.Path ?? string.Empty;
                        string arguments = action.Arguments ?? string.Empty;
                        commands.Add(Environment.ExpandEnvironmentVariables($"\"{path.Trim('"')}\" {arguments}".Trim()));
                    }

                    string? userId = null;
                    try { userId = task.Definition.Principal.UserId as string; }
                    catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }

                    tasks.Add(new ScheduledTaskInfo((string)task.Path, (string)task.Name, commands, IsCurrentUser(userId)));
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // A task whose definition this account cannot read is skipped, not fatal.
                }
            }

            foreach (dynamic child in folder.GetFolders(0)) Visit(child, tasks, ct, depth + 1);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }
    }
}
