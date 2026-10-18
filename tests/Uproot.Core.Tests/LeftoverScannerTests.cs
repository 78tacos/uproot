using System.Diagnostics;
using Microsoft.Win32;
using Uproot.Core;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>
/// The scanner and remover end to end, against a pretend machine: temporary folders stand in for
/// Program Files, ProgramData and a profile, and a throwaway key under HKCU stands in for the
/// registry roots. Nothing outside those is read as a candidate or touched.
/// </summary>
public sealed class LeftoverScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uproot-tests", Guid.NewGuid().ToString("N"));
    private readonly string _registryRoot = @"Software\Uproot.Tests\" + Guid.NewGuid().ToString("N");

    private string ProgramFiles => Path.Combine(_root, "Program Files");
    private string ProgramData => Path.Combine(_root, "ProgramData");
    private string Profile => Path.Combine(_root, "Users", "tester");
    private string Roaming => Path.Combine(Profile, "AppData", "Roaming");
    private string Local => Path.Combine(Profile, "AppData", "Local");
    private string Temp => Path.Combine(Local, "Temp");
    private string StartMenu => Path.Combine(Roaming, "Microsoft", "Windows", "Start Menu", "Programs");
    private string InstallFolder => Path.Combine(ProgramFiles, "Contoso", "Widget");
    private string GadgetFolder => Path.Combine(ProgramFiles, "Contoso", "Gadget");

    private RegistryLocation Machine => RegistryLocation.CurrentUser(_registryRoot + @"\HKLM\SOFTWARE");
    private RegistryLocation User => RegistryLocation.CurrentUser(_registryRoot + @"\HKCU\Software");
    private RegistryLocation RunKey => User.Child(@"Microsoft\Windows\CurrentVersion\Run");
    private RegistryLocation Entry => RegistryLocation.CurrentUser(_registryRoot + @"\Uninstall\ContosoWidget");
    private RegistryLocation EnvironmentKey => RegistryLocation.CurrentUser(_registryRoot + @"\HKCU\Environment");
    private RegistryLocation MuiCache => User.Child(@"Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
    private string WidgetExe => Path.Combine(InstallFolder, "Widget.exe");

    private readonly FakeFirewall _firewall = new();

    public LeftoverScannerTests()
    {
        foreach (var folder in new[] { InstallFolder, GadgetFolder, Temp, StartMenu })
            Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(InstallFolder, "Widget.exe"), "stand-in");
        File.WriteAllText(Path.Combine(GadgetFolder, "Gadget.exe"), "stand-in");

        // What an uninstaller typically leaves.
        Touch(ProgramData, "Contoso", "Widget", "settings.ini");     // Publisher\Product
        Touch(ProgramData, "Contoso", "Gadget", "settings.ini");     // someone else's
        Touch(Roaming, "Contoso Widget", "config.json");             // product name at the root
        Touch(Local, "Contoso", "Widget Cache", "blob.bin");         // publisher\product-prefix
        Touch(Temp, "Contoso Widget", "crash.dmp");                  // Expert only
        Touch(Roaming, "Unrelated", "keep.txt");                     // nothing to do with it
        File.WriteAllBytes(Path.Combine(StartMenu, "Contoso Widget.lnk"),
            UninstallerAnalyzerTests.Shortcut(Path.Combine(InstallFolder, "Widget.exe")));

        Key(Machine.Child(@"Contoso\Widget")).SetValue("Version", "1.0");
        Key(Machine.Child(@"Contoso\Gadget")).SetValue("Version", "2.0");
        Key(User.Child("Contoso Widget")).SetValue("LastOpened", "today");
        Key(RunKey).SetValue("ContosoWidgetTray", $"\"{Path.Combine(InstallFolder, "Widget.exe")}\" --tray");
        Key(RunKey).SetValue("SomethingElse", @"""C:\Elsewhere\thing.exe""");
        Key(Entry).SetValue("DisplayName", "Contoso Widget");
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Touch(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "leftover");
    }

    private static RegistryKey Key(RegistryLocation location) =>
        Registry.CurrentUser.CreateSubKey(location.SubKey, writable: true)!;

    private LeftoverScanContext Context() => new()
    {
        Machine = new ProfileScope
        {
            DataFolders = [ProgramFiles, ProgramData],
            SoftwareKeys = [Machine]
        },
        Profiles =
        [
            new ProfileScope
            {
                Owner = "tester",
                ProfileRoot = Profile,
                DataFolders = [Roaming, Local],
                ShortcutFolders = [StartMenu],
                TempFolders = [Temp],
                SoftwareKeys = [User],
                RunKeys = [RunKey],
                EnvironmentKey = EnvironmentKey,
                TraceKeys = [MuiCache]
            }
        ],
        Firewall = _firewall
    };

    private LeftoverSafety Safety() => new(new SafetyRules
    {
        ProtectedFolders = [ProgramFiles, ProgramData, Profile, Roaming, Local, Temp, StartMenu],
        ProtectedTrees = [Path.Combine(_root, "Windows")],
        EnvironmentKeys = [EnvironmentKey]
    }, [new ProgramClaim("Contoso Gadget", [GadgetFolder], ["Contoso Gadget", "Gadget"])]);

    private UninstallTarget Target()
    {
        var program = new InstalledProgram
        {
            Key = Entry,
            DisplayName = "Contoso Widget",
            Publisher = "Contoso Ltd",
            InstallLocation = InstallFolder,
            Scope = ProgramScope.User
        };

        return UninstallTarget.From(new UninstallerProfile { Program = program, InstallLocation = InstallFolder });
    }

    private LeftoverScanResult Scan(ScanMode mode) =>
        new LeftoverScanner(Context(), Safety()).Scan(Target(), new LeftoverScanOptions { Mode = mode });

    private static LeftoverItem? Find(LeftoverScanResult result, string path) =>
        result.Items.FirstOrDefault(i => string.Equals(i.DisplayPath, path, StringComparison.OrdinalIgnoreCase));

    // ---- Scanning ------------------------------------------------------------

    [Fact]
    public void Safe_mode_finds_only_what_is_provably_the_programs()
    {
        var result = Scan(ScanMode.Safe);

        Assert.Equal(LeftoverConfidence.Certain, Find(result, InstallFolder)?.Confidence);
        Assert.Equal(LeftoverConfidence.Certain, Find(result, Entry.DisplayPath)?.Confidence);
        Assert.Equal(LeftoverConfidence.Certain, Find(result, Path.Combine(StartMenu, "Contoso Widget.lnk"))?.Confidence);
        Assert.Equal(LeftoverConfidence.Certain, Find(result, RunKey.DisplayPath + @"\ContosoWidgetTray")?.Confidence);

        // Publisher\Product is a strong enough pattern for Safe.
        Assert.NotNull(Find(result, Path.Combine(ProgramData, "Contoso", "Widget")));
        Assert.NotNull(Find(result, Machine.Child(@"Contoso\Widget").DisplayPath));

        // A bare product-name folder needs Moderate.
        Assert.Null(Find(result, Path.Combine(Roaming, "Contoso Widget")));
        Assert.All(result.Items, i => Assert.Equal(ScanMode.Safe, i.Mode));
    }

    [Fact]
    public void Moderate_mode_adds_name_matches_in_the_usual_places()
    {
        var result = Scan(ScanMode.Moderate);

        var roaming = Find(result, Path.Combine(Roaming, "Contoso Widget"));
        Assert.Equal(LeftoverConfidence.Likely, roaming?.Confidence);
        Assert.Equal("tester", roaming?.Owner);

        Assert.NotNull(Find(result, Path.Combine(Local, "Contoso", "Widget Cache")));
        Assert.NotNull(Find(result, User.Child("Contoso Widget").DisplayPath));

        // Nothing in Temp until Expert.
        Assert.Null(Find(result, Path.Combine(Temp, "Contoso Widget")));
    }

    [Fact]
    public void Expert_mode_adds_temp_and_never_preselects_it()
    {
        var result = Scan(ScanMode.Expert);
        var temp = Find(result, Path.Combine(Temp, "Contoso Widget"));

        Assert.NotNull(temp);
        Assert.Equal(LeftoverConfidence.Possible, temp.Confidence);
        Assert.False(temp.Recommended);
    }

    [Fact]
    public void Another_programs_files_and_keys_are_never_offered()
    {
        var result = Scan(ScanMode.Expert);

        Assert.DoesNotContain(result.Items, i => !i.IsProtected && i.Path.Contains("Gadget", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Items, i => i.Path.Contains("Unrelated", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Items, i => i.ValueName == "SomethingElse");
    }

    [Fact]
    public void The_shared_publisher_folder_is_offered_only_if_it_ends_up_empty()
    {
        var result = Scan(ScanMode.Moderate);

        // ProgramData\Contoso also holds Gadget's folder: offered only-if-empty, never whole.
        var publisher = Find(result, Path.Combine(ProgramData, "Contoso"));
        Assert.NotNull(publisher);
        Assert.True(publisher.OnlyIfEmpty);

        // Program Files\Contoso contains Gadget, which is still installed, so it is not even listed.
        Assert.DoesNotContain(result.Items, i => !i.IsProtected && PathRules.SamePath(i.Path, Path.Combine(ProgramFiles, "Contoso")));
    }

    [Fact]
    public void Standard_folders_are_never_offered()
    {
        var result = Scan(ScanMode.Expert);

        foreach (var standard in new[] { ProgramFiles, ProgramData, Roaming, Local, Temp, StartMenu, Profile })
            Assert.DoesNotContain(result.Items, i => !i.IsProtected && PathRules.SamePath(i.Path, standard));
    }

    [Fact]
    public void A_program_known_only_by_name_is_found_without_an_entry()
    {
        Directory.Delete(InstallFolder, recursive: true);

        var program = InstalledProgram.ForName("Contoso Widget", "Contoso Ltd");
        var target = UninstallTarget.From(new UninstallerProfile { Program = program });
        var result = new LeftoverScanner(Context(), Safety()).Scan(target, new LeftoverScanOptions { Mode = ScanMode.Moderate });

        Assert.NotNull(Find(result, Path.Combine(Roaming, "Contoso Widget")));
        Assert.NotNull(Find(result, Path.Combine(ProgramData, "Contoso", "Widget")));
        Assert.DoesNotContain(result.Items, i => i.Kind == LeftoverKind.RegistryKey && i.Path.Contains("Uninstall"));
    }

    [Fact]
    public void A_short_name_is_trusted_under_its_publisher_but_not_on_its_own()
    {
        // "Fabrikam Notes" yields "Notes", which is right for Fabrikam\Notes and says nothing
        // about a folder called Notes that some other program keeps in AppData.
        Touch(Roaming, "Notes", "someone-elses.txt");
        Touch(Roaming, "Fabrikam", "Notes", "settings.json");

        var program = new InstalledProgram
        {
            Key = Entry,
            DisplayName = "Fabrikam Notes",
            Publisher = "Fabrikam Inc",
            Scope = ProgramScope.User
        };
        var target = UninstallTarget.From(new UninstallerProfile { Program = program });

        Assert.Contains("Notes", target.ProductNames);
        Assert.DoesNotContain("Notes", target.PrimaryNames);

        var scanner = new LeftoverScanner(Context(), Safety());
        var moderate = scanner.Scan(target, new LeftoverScanOptions { Mode = ScanMode.Moderate });
        var expert = scanner.Scan(target, new LeftoverScanOptions { Mode = ScanMode.Expert });

        Assert.NotNull(Find(moderate, Path.Combine(Roaming, "Fabrikam", "Notes")));
        Assert.Null(Find(moderate, Path.Combine(Roaming, "Notes")));

        var loose = Find(expert, Path.Combine(Roaming, "Notes"));
        Assert.NotNull(loose);
        Assert.Equal(LeftoverConfidence.Possible, loose.Confidence);
        Assert.False(loose.Recommended);
    }

    private RegistryLocation Classes => User.Child("Classes");

    private LeftoverScanContext ContextWithClasses() => Context() with
    {
        Profiles = [Context().Profiles[0] with { ClassesKeys = [Classes] }]
    };

    [Fact]
    public void A_verb_added_to_someone_elses_file_type_goes_without_the_file_type()
    {
        var widget = $"\"{Path.Combine(InstallFolder, "Widget.exe")}\" \"%1\"";

        // An image type every viewer shares, with one extra verb the program added.
        Key(Classes.Child(@"jpegfile\shell\open\command")).SetValue("", @"C:\Windows\System32\mspaint.exe ""%1""");
        Key(Classes.Child(@"jpegfile\shell\ContosoEdit\command")).SetValue("", widget);

        // The program's own file type: everything in it points at the program.
        Key(Classes.Child(@"ContosoWidget.Document\shell\open\command")).SetValue("", widget);

        var result = new LeftoverScanner(ContextWithClasses(), Safety()).Scan(Target(), new LeftoverScanOptions { Mode = ScanMode.Moderate });

        Assert.Null(Find(result, Classes.Child("jpegfile").DisplayPath));
        Assert.NotNull(Find(result, Classes.Child(@"jpegfile\shell\ContosoEdit").DisplayPath));
        Assert.NotNull(Find(result, Classes.Child("ContosoWidget.Document").DisplayPath));
    }

    [Fact]
    public void A_recorded_key_under_someone_elses_progid_is_not_widened_to_it()
    {
        Key(Classes.Child(@"jpegfile\shell\open\command")).SetValue("", @"C:\Windows\System32\mspaint.exe ""%1""");
        Key(Classes.Child(@"jpegfile\shell\ContosoEdit")).SetValue("MUIVerb", "Edit with Widget");
        Key(Classes.Child(@"jpegfile\shell\ContosoEdit\command")).SetValue("", "widget.exe");

        // Outside Classes: a key named only like the program ("Widget Tools") is not promoted to.
        Key(Machine.Child(@"Contoso\Widget Tools\Settings")).SetValue("Level", "1");

        var footprint = new MsiFootprint
        {
            ProductCode = "{11111111-2222-3333-4444-555555555555}",
            Registry =
            [
                new FootprintRegistryEntry(Classes.Child(@"jpegfile\shell\ContosoEdit"), "MUIVerb", false),
                new FootprintRegistryEntry(Classes.Child(@"jpegfile\shell\ContosoEdit\command"), null, false),
                new FootprintRegistryEntry(Machine.Child(@"Contoso\Widget Tools\Settings"), "Level", false)
            ]
        };

        var program = Target().Program;
        var target = UninstallTarget.From(new UninstallerProfile { Program = program, InstallLocation = InstallFolder, Msi = footprint });
        var result = new LeftoverScanner(Context(), Safety()).Scan(target, new LeftoverScanOptions { Mode = ScanMode.Safe });

        Assert.Null(Find(result, Classes.Child("jpegfile").DisplayPath));
        Assert.NotNull(Find(result, Classes.Child(@"jpegfile\shell\ContosoEdit").DisplayPath));
        Assert.Null(Find(result, Machine.Child(@"Contoso\Widget Tools").DisplayPath));
        Assert.NotNull(Find(result, Machine.Child(@"Contoso\Widget Tools\Settings").DisplayPath + @"\Level"));
    }

    [Fact]
    public void A_folder_refused_as_another_programs_stays_listed_and_its_parent_is_never_offered_whole()
    {
        Touch(ProgramData, "Contoso", "Widget Pro", "data.bin");

        var safety = new LeftoverSafety(new SafetyRules
        {
            ProtectedFolders = [ProgramFiles, ProgramData, Profile, Roaming, Local, Temp, StartMenu]
        }, [new ProgramClaim("Contoso Widget Pro", [], ["Contoso Widget Pro", "Widget Pro"])]);

        var result = new LeftoverScanner(Context(), safety).Scan(Target(), new LeftoverScanOptions { Mode = ScanMode.Expert });

        var pro = Find(result, Path.Combine(ProgramData, "Contoso", "Widget Pro"));
        Assert.NotNull(pro);
        Assert.True(pro.IsProtected);

        var publisher = Find(result, Path.Combine(ProgramData, "Contoso"));
        Assert.True(publisher is null || publisher.OnlyIfEmpty || publisher.IsProtected);
    }

    [Fact]
    public void A_service_is_checked_by_the_name_that_gets_deleted()
    {
        // The key says one thing, the name another: the check must follow the name, which is
        // what sc.exe deletes. Winmgmt runs svchost from System32 and must be refused.
        var forged = new LeftoverItem
        {
            Kind = LeftoverKind.Service,
            Path = "Winmgmt",
            RegistryKey = Machine.Child(@"Contoso\Widget"),
            Confidence = LeftoverConfidence.Certain,
            Reason = "forged"
        };

        var outcome = Remove([forged]).Outcomes.Single();

        Assert.Equal(RemovalStatus.Refused, outcome.Status);
    }

    [Fact]
    public void Sizes_are_not_counted_twice()
    {
        var folder = new LeftoverItem { Kind = LeftoverKind.Folder, Path = @"C:\A", Confidence = LeftoverConfidence.Likely, Reason = "", SizeBytes = 100 };
        var inside = new LeftoverItem { Kind = LeftoverKind.File, Path = @"C:\A\b.txt", Confidence = LeftoverConfidence.Certain, Reason = "", SizeBytes = 40 };
        var elsewhere = new LeftoverItem { Kind = LeftoverKind.File, Path = @"C:\C\d.txt", Confidence = LeftoverConfidence.Certain, Reason = "", SizeBytes = 5 };

        Assert.Equal(105, LeftoverScanResult.TotalBytes([folder, inside, elsewhere]));
    }

    // ---- Removing ------------------------------------------------------------

    private RemovalReport Remove(IReadOnlyList<LeftoverItem> items, bool allowProtected = false) =>
        new LeftoverRemover(Safety(), _firewall).Remove(items, new RemovalOptions
        {
            UseRecycleBin = false,
            BackupRoot = Path.Combine(_root, "Backups"),
            ProgramName = "Contoso Widget",
            AllowProtected = allowProtected
        });

    private RestoreReport Restore(RemovalReport report)
    {
        var record = RemovalHistory.Read(report.BackupFolder!)!;
        return new RemovalRestorer(Safety(), _firewall).Restore(record, record.Restorable);
    }

    [Fact]
    public void Removing_takes_the_selected_items_and_backs_up_registry_keys_first()
    {
        var result = Scan(ScanMode.Moderate);
        var report = Remove(result.Items.Where(i => i.Recommended).ToList());

        Assert.Equal(0, report.Failed);
        Assert.False(Directory.Exists(InstallFolder));
        Assert.False(Directory.Exists(Path.Combine(Roaming, "Contoso Widget")));
        Assert.False(Machine.Child(@"Contoso\Widget").Exists());
        Assert.False(Entry.Exists());

        using (var run = RunKey.Open())
        {
            Assert.DoesNotContain("ContosoWidgetTray", run!.GetValueNames());
            Assert.Contains("SomethingElse", run.GetValueNames());   // its neighbour stays
        }

        // Someone else's things are untouched.
        Assert.True(Directory.Exists(GadgetFolder));
        Assert.True(File.Exists(Path.Combine(ProgramData, "Contoso", "Gadget", "settings.ini")));
        Assert.True(Machine.Child(@"Contoso\Gadget").Exists());

        // Every registry removal left a .reg file that puts it back.
        Assert.NotNull(report.BackupFolder);
        Assert.NotEmpty(Directory.GetFiles(report.BackupFolder, "*.reg"));
        Assert.True(File.Exists(Path.Combine(report.BackupFolder, "manifest.json")));
    }

    [Fact]
    public void A_publisher_folder_goes_only_once_it_is_empty()
    {
        var result = Scan(ScanMode.Moderate);
        var publisher = Find(result, Path.Combine(ProgramData, "Contoso"))!;
        var widget = Find(result, Path.Combine(ProgramData, "Contoso", "Widget"))!;

        var report = Remove([publisher, widget]);

        // Gadget's folder is still in there, so the publisher folder stays.
        Assert.Equal(RemovalStatus.Skipped, report.Outcomes.Single(o => o.Item == publisher).Status);
        Assert.True(Directory.Exists(Path.Combine(ProgramData, "Contoso")));

        Directory.Delete(Path.Combine(ProgramData, "Contoso", "Gadget"), recursive: true);
        Remove([publisher]);

        Assert.False(Directory.Exists(Path.Combine(ProgramData, "Contoso")));
    }

    [Fact]
    public void A_tampered_list_still_cannot_reach_a_protected_location()
    {
        // An item that never came from a scan: the remover re-checks everything itself.
        var forged = new[]
        {
            new LeftoverItem { Kind = LeftoverKind.Folder, Path = ProgramData, Confidence = LeftoverConfidence.Certain, Reason = "forged" },
            new LeftoverItem { Kind = LeftoverKind.Folder, Path = GadgetFolder, Confidence = LeftoverConfidence.Certain, Reason = "forged" }
        };

        var report = Remove(forged);

        Assert.All(report.Outcomes, o => Assert.Equal(RemovalStatus.Refused, o.Status));
        Assert.True(Directory.Exists(ProgramData));
        Assert.True(Directory.Exists(GadgetFolder));
    }

    [Fact]
    public void A_junction_is_removed_as_a_link_and_its_target_survives()
    {
        var target = Path.Combine(_root, "Precious");
        Touch(target, "keep.txt");

        var link = Path.Combine(Roaming, "Contoso Widget Link");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link));

        var item = new LeftoverItem { Kind = LeftoverKind.Folder, Path = link, Confidence = LeftoverConfidence.Likely, Reason = "test" };

        // Even with the Recycle Bin allowed, a link is never handed to the shell.
        var report = new LeftoverRemover(Safety()).Remove([item], new RemovalOptions { UseRecycleBin = true, BackupRoot = Path.Combine(_root, "Backups") });

        Assert.Equal(RemovalStatus.Removed, report.Outcomes.Single().Status);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Already_removed_items_are_skipped_not_failed()
    {
        var gone = new LeftoverItem
        {
            Kind = LeftoverKind.Folder,
            Path = Path.Combine(Roaming, "Never Existed"),
            Confidence = LeftoverConfidence.Likely,
            Reason = "test"
        };

        Assert.Equal(RemovalStatus.Skipped, Remove([gone]).Outcomes.Single().Status);
    }

    // ---- PATH, firewall and Windows' records ------------------------------------

    [Fact]
    public void A_PATH_entry_into_the_program_is_taken_out_and_nothing_else_changes()
    {
        var bin = Path.Combine(InstallFolder, "bin");
        Key(EnvironmentKey).SetValue("Path", $@"C:\Keep\One;{bin};%SystemRoot%\Keep", RegistryValueKind.ExpandString);

        var entry = Scan(ScanMode.Safe).Items.Single(i => i.Kind == LeftoverKind.PathEntry);

        Assert.Equal(bin, entry.Path);
        Assert.Equal(LeftoverConfidence.Certain, entry.Confidence);
        Assert.Equal(LeftoverCategory.Environment, entry.Category);
        Assert.True(entry.Recommended);

        var report = Remove([entry]);
        Assert.Equal(RemovalStatus.Removed, report.Outcomes.Single().Status);

        using (var key = EnvironmentKey.Open())
        {
            Assert.Equal(@"C:\Keep\One;%SystemRoot%\Keep", key!.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Path"));
        }

        // And it can be put back.
        Assert.Equal(1, Restore(report).Restored);

        using (var key = EnvironmentKey.Open())
            Assert.Contains(bin, EnvironmentPath.Split(key!.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string));
    }

    [Fact]
    public void Firewall_rules_go_by_name_only_when_every_rule_of_that_name_is_the_programs()
    {
        _firewall.Rules.AddRange(
        [
            new FirewallRuleInfo { Name = "Contoso Widget", ApplicationName = WidgetExe, Protocol = 6 },
            new FirewallRuleInfo { Name = "Contoso Widget", ApplicationName = WidgetExe, Protocol = 17 },
            new FirewallRuleInfo { Name = "Contoso Gadget", ApplicationName = Path.Combine(GadgetFolder, "Gadget.exe") },
            new FirewallRuleInfo { Name = "Shared name", ApplicationName = WidgetExe },
            new FirewallRuleInfo { Name = "Shared name", ApplicationName = @"C:\Elsewhere\other.exe" },
            new FirewallRuleInfo { Name = "Widget via Windows", ApplicationName = WidgetExe, Grouping = "@FirewallAPI.dll,-28502" }
        ]);

        var result = Scan(ScanMode.Safe);

        var rule = result.Items.Single(i => i is { Kind: LeftoverKind.FirewallRule, Path: "Contoso Widget" });
        Assert.True(rule.Recommended);
        Assert.DoesNotContain(result.Items, i => i.Path is "Contoso Gadget" or "Shared name");

        // A rule in one of Windows' groups is shown, and kept whatever anyone chooses.
        var windows = result.Items.Single(i => i.Path == "Widget via Windows");
        Assert.True(windows.KeptAlways);

        var report = Remove([rule, windows], allowProtected: true);

        Assert.Equal(RemovalStatus.Removed, report.Outcomes.Single(o => o.Item == rule).Status);
        Assert.Equal(RemovalStatus.Refused, report.Outcomes.Single(o => o.Item == windows).Status);
        Assert.DoesNotContain(_firewall.Rules, r => r.Name == "Contoso Widget");
        Assert.Contains(_firewall.Rules, r => r.Name == "Contoso Gadget");

        Assert.Equal(1, Restore(report).Restored);
        Assert.Equal(2, _firewall.Rules.Count(r => r.Name == "Contoso Widget"));
    }

    [Fact]
    public void What_Windows_recorded_about_the_program_is_found_in_moderate_mode()
    {
        Key(MuiCache).SetValue(WidgetExe + ".FriendlyAppName", "Widget");
        Key(MuiCache).SetValue(@"C:\Elsewhere\other.exe.FriendlyAppName", "Other");

        Assert.DoesNotContain(Scan(ScanMode.Safe).Items, i => i.RegistryKey?.Equals(MuiCache) == true);

        var trace = Scan(ScanMode.Moderate).Items.Single(i => i.RegistryKey?.Equals(MuiCache) == true);

        Assert.Equal(WidgetExe + ".FriendlyAppName", trace.ValueName);
        Assert.Equal(LeftoverCategory.WindowsRecords, trace.Category);
        Assert.True(trace.Recommended);
    }

    // ---- Turning protection off ------------------------------------------------------

    [Fact]
    public void A_protected_item_goes_only_once_protection_is_turned_off()
    {
        var folder = Path.Combine(ProgramData, "Contoso", "Gadget");
        var item = new LeftoverItem
        {
            Kind = LeftoverKind.Folder,
            Path = folder,
            Confidence = LeftoverConfidence.Likely,
            Reason = "test",
            ProtectedBecause = "Matches Contoso Gadget, which is still installed, more closely."
        };

        Assert.Equal(RemovalStatus.Refused, Remove([item]).Outcomes.Single().Status);
        Assert.True(Directory.Exists(folder));

        var outcome = Remove([item], allowProtected: true).Outcomes.Single();

        Assert.Equal(RemovalStatus.Removed, outcome.Status);
        Assert.True(outcome.Overridden);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Turning_protection_off_never_reaches_what_is_kept_always_or_newly_protected()
    {
        var always = new LeftoverItem
        {
            Kind = LeftoverKind.Folder, Path = Path.Combine(ProgramData, "Contoso", "Widget"), Confidence = LeftoverConfidence.Certain,
            Reason = "test", ProtectedBecause = "forged", KeptAlways = true
        };

        // Not protected when the list was made, protected now: nobody has seen why.
        var newly = new LeftoverItem
        {
            Kind = LeftoverKind.Folder, Path = GadgetFolder, Confidence = LeftoverConfidence.Certain, Reason = "test"
        };

        // A standard folder is absolute however the item is marked.
        var standard = new LeftoverItem
        {
            Kind = LeftoverKind.Folder, Path = ProgramData, Confidence = LeftoverConfidence.Certain,
            Reason = "test", ProtectedBecause = "forged as overridable"
        };

        var report = Remove([always, newly, standard], allowProtected: true);

        Assert.All(report.Outcomes, o => Assert.Equal(RemovalStatus.Refused, o.Status));
        Assert.True(Directory.Exists(Path.Combine(ProgramData, "Contoso", "Widget")));
        Assert.True(Directory.Exists(GadgetFolder));
        Assert.True(Directory.Exists(ProgramData));
    }

    [Fact]
    public void Another_programs_folder_can_be_removed_on_purpose_with_protection_off()
    {
        // Gadget's folder is protected, but by a judgement a person may overrule, not by an absolute rule.
        var verdict = Safety().JudgePath(GadgetFolder);
        Assert.NotNull(verdict);
        Assert.False(verdict.Absolute);

        var gadget = new LeftoverItem
        {
            Kind = LeftoverKind.Folder, Path = GadgetFolder, Confidence = LeftoverConfidence.Likely, Reason = "test",
            ProtectedBecause = verdict.Reason
        };

        Assert.True(gadget.CanOverride);
        Assert.Equal(RemovalStatus.Removed, Remove([gadget], allowProtected: true).Outcomes.Single().Status);
        Assert.False(Directory.Exists(GadgetFolder));
    }

    // ---- Undoing a removal -----------------------------------------------------------

    [Fact]
    public void A_removal_can_be_undone_from_its_backup()
    {
        var result = Scan(ScanMode.Moderate);
        var key = Find(result, Machine.Child(@"Contoso\Widget").DisplayPath)!;
        var value = Find(result, RunKey.DisplayPath + @"\ContosoWidgetTray")!;

        var report = Remove([key, value]);
        Assert.False(Machine.Child(@"Contoso\Widget").Exists());

        var record = RemovalHistory.Read(report.BackupFolder!)!;
        Assert.Equal(RemovalManifest.CurrentFormat, record.Manifest.Format);
        Assert.All(record.Removed, i => Assert.NotNull(i.Backup));

        var restore = Restore(report);

        Assert.Equal(2, restore.Restored);
        using (var widget = Machine.Child(@"Contoso\Widget").Open())
            Assert.Equal("1.0", widget?.GetValue("Version"));

        using (var run = RunKey.Open())
        {
            Assert.Contains("ContosoWidgetTray", run!.GetValueNames());
            Assert.Contains("SomethingElse", run.GetValueNames());
        }

        // Doing it twice puts nothing back twice.
        Assert.All(Restore(report).Outcomes, o => Assert.Equal(RestoreStatus.Skipped, o.Status));
    }

    [Fact]
    public void A_backup_that_reaches_beyond_its_own_key_is_never_imported()
    {
        var key = Find(Scan(ScanMode.Moderate), Machine.Child(@"Contoso\Widget").DisplayPath)!;
        var report = Remove([key]);

        var record = RemovalHistory.Read(report.BackupFolder!)!;
        var file = Path.Combine(report.BackupFolder!, record.Removed.Single().Backup!);
        var elsewhere = User.Child("Planted");

        File.WriteAllText(file, $"Windows Registry Editor Version 5.00\r\n\r\n[{key.RegistryKey!.DisplayPath.Replace("HKCU", "HKEY_CURRENT_USER")}]\r\n\r\n"
                                + $"[{elsewhere.DisplayPath.Replace("HKCU", "HKEY_CURRENT_USER")}]\r\n\"Evil\"=\"1\"\r\n");

        var outcome = Restore(report).Outcomes.Single();

        Assert.Equal(RestoreStatus.Refused, outcome.Status);
        Assert.False(elsewhere.Exists());
    }

    [Fact]
    public void A_removal_of_files_alone_still_leaves_a_record_to_undo_it_from()
    {
        var folder = Find(Scan(ScanMode.Moderate), Path.Combine(Roaming, "Contoso Widget"))!;
        var report = Remove([folder]);

        Assert.NotNull(report.BackupFolder);
        Assert.NotNull(RemovalHistory.Read(report.BackupFolder));
    }

    // ---- A program known only by its folder ---------------------------------------------

    [Fact]
    public void A_program_known_by_its_folder_has_that_folder_and_what_points_into_it_found()
    {
        // The analyzer would set this from the folder given; it declines folders under Temp, as
        // these test folders are, since uninstallers run from there.
        var program = InstalledProgram.ForName("Contoso Widget", "Contoso Ltd", InstallFolder);
        var analysis = new UninstallerProfile { Program = program, InstallLocation = program.InstallLocation };

        var result = new LeftoverScanner(Context(), Safety()).Scan(UninstallTarget.From(analysis), new LeftoverScanOptions { Mode = ScanMode.Safe });

        Assert.Equal(LeftoverConfidence.Certain, Find(result, InstallFolder)?.Confidence);
        Assert.Equal(LeftoverConfidence.Certain, Find(result, RunKey.DisplayPath + @"\ContosoWidgetTray")?.Confidence);
    }

    [Fact]
    public void A_shell_extension_shared_with_another_copy_of_the_program_stays()
    {
        // The 32-bit build's class points into this folder; the 64-bit build registered the same
        // CLSID elsewhere, and the context-menu handler serves both.
        const string clsid = "{23170F69-40C1-278A-1000-000100020000}";
        var machineClasses = Machine.Child("Classes");

        Key(Classes.Child($@"CLSID\{clsid}\InprocServer32")).SetValue("", Path.Combine(InstallFolder, "7-zip32.dll"));
        Key(machineClasses.Child($@"CLSID\{clsid}\InprocServer32")).SetValue("", @"C:\Program Files\7-Zip\7-zip.dll");
        Key(Classes.Child(@"*\shellex\ContextMenuHandlers\7-Zip")).SetValue("", clsid);

        var context = ContextWithClasses() with { Machine = Context().Machine with { ClassesKeys = [machineClasses] } };
        var result = new LeftoverScanner(context, Safety()).Scan(Target(), new LeftoverScanOptions { Mode = ScanMode.Moderate });

        Assert.NotNull(Find(result, Classes.Child($@"CLSID\{clsid}").DisplayPath));
        Assert.Null(Find(result, Classes.Child(@"*\shellex\ContextMenuHandlers\7-Zip").DisplayPath));

        // With the other registration gone, the handler is this program's alone.
        Registry.CurrentUser.DeleteSubKeyTree(machineClasses.Child($@"CLSID\{clsid}").SubKey);
        result = new LeftoverScanner(context, Safety()).Scan(Target(), new LeftoverScanOptions { Mode = ScanMode.Moderate });
        Assert.NotNull(Find(result, Classes.Child(@"*\shellex\ContextMenuHandlers\7-Zip").DisplayPath));
    }

    [Fact]
    public void Overriding_protection_never_takes_a_program_that_was_not_named()
    {
        var parent = Path.Combine(ProgramFiles, "Contoso");
        var safety = new LeftoverSafety(new SafetyRules { ProtectedFolders = [ProgramFiles, ProgramData] },
        [
            new ProgramClaim("Contoso Gadget", [GadgetFolder], ["Contoso Gadget"]),
            new ProgramClaim("Contoso Widget", [InstallFolder], ["Contoso Widget"])
        ]);

        var verdict = safety.JudgePath(parent)!;
        Assert.Contains("Contoso Gadget", verdict.Reason);
        Assert.Contains("Contoso Widget", verdict.Reason);

        // Shown as holding only Gadget — say Widget was installed after the scan.
        var item = new LeftoverItem
        {
            Kind = LeftoverKind.Folder, Path = parent, Confidence = LeftoverConfidence.Possible, Reason = "test",
            ProtectedBecause = "Contains Contoso Gadget, which is still installed."
        };

        var outcome = new LeftoverRemover(safety, _firewall).Remove([item], new RemovalOptions
        {
            UseRecycleBin = false, BackupRoot = Path.Combine(_root, "Backups"), AllowProtected = true
        }).Outcomes.Single();

        Assert.Equal(RemovalStatus.Refused, outcome.Status);
        Assert.Contains("Contoso Widget", outcome.Message);
        Assert.True(Directory.Exists(InstallFolder));
    }

    [Fact]
    public void A_PATH_entry_is_matched_by_its_text_not_by_what_it_expands_to()
    {
        var bin = Path.Combine(InstallFolder, "bin");
        Environment.SetEnvironmentVariable("UPROOT_TEST_HOME", InstallFolder);

        try
        {
            Key(EnvironmentKey).SetValue("Path", $@"{bin};%UPROOT_TEST_HOME%\bin", RegistryValueKind.ExpandString);

            var literal = new LeftoverItem
            {
                Kind = LeftoverKind.PathEntry, Path = bin, RegistryKey = EnvironmentKey, ValueName = "Path",
                Confidence = LeftoverConfidence.Certain, Reason = "test"
            };

            Assert.Equal(RemovalStatus.Removed, Remove([literal]).Outcomes.Single().Status);

            using var key = EnvironmentKey.Open();
            Assert.Equal(@"%UPROOT_TEST_HOME%\bin", key!.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        finally
        {
            Environment.SetEnvironmentVariable("UPROOT_TEST_HOME", null);
        }
    }

    [Fact]
    public void A_key_holding_a_registry_link_is_never_deleted_or_written_through()
    {
        var target = User.Child("Link Target");
        Key(target).SetValue("Precious", "keep");

        var container = User.Child("Contoso Widget");
        var link = container.Child("Shortcut");
        TestRegistryLink.Create(link, target);

        try
        {
            Assert.True(RegistryLinks.HasLink(link, includeSubtree: false));
            Assert.True(RegistryLinks.HasLink(container, includeSubtree: true));
            Assert.False(RegistryLinks.HasLink(container, includeSubtree: false));

            var key = new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = container.DisplayPath, RegistryKey = container, Confidence = LeftoverConfidence.Likely, Reason = "test" };
            var value = new LeftoverItem { Kind = LeftoverKind.RegistryValue, Path = link.DisplayPath, RegistryKey = link, ValueName = "Precious", Confidence = LeftoverConfidence.Likely, Reason = "test" };

            Assert.All(Remove([key, value]).Outcomes, o => Assert.Equal(RemovalStatus.Refused, o.Status));

            using var open = target.Open();
            Assert.Equal("keep", open!.GetValue("Precious"));
        }
        finally
        {
            TestRegistryLink.Delete(link);
        }
    }

    [Fact]
    public void A_protected_folder_named_as_the_install_folder_makes_nothing_the_programs()
    {
        // Named at the whole profile: everything in it would otherwise "point into the program".
        Key(RunKey).SetValue("SomeoneElsesTray", $"\"{Path.Combine(Roaming, "Unrelated", "tray.exe")}\"");

        var program = InstalledProgram.ForName("Zqxv Tool", null, Profile);
        var analysis = new UninstallerProfile { Program = program, InstallLocation = program.InstallLocation };

        var result = new LeftoverScanner(Context(), Safety()).Scan(UninstallTarget.From(analysis), new LeftoverScanOptions { Mode = ScanMode.Moderate });

        Assert.Null(Find(result, RunKey.DisplayPath + @"\SomeoneElsesTray"));
        Assert.Null(Find(result, RunKey.DisplayPath + @"\ContosoWidgetTray"));

        // The folder itself is shown, and kept whatever anyone chooses.
        var folder = Find(result, Profile);
        Assert.True(folder is null || folder.KeptAlways);
        Assert.DoesNotContain(result.Items, i => i.Recommended);
    }
}

/// <summary>
/// Creates and deletes a registry symbolic link inside the test key. The link is volatile, so
/// even if a test is killed part-way it is gone at the next sign-in.
/// </summary>
internal static class TestRegistryLink
{
    private const int RegOptionVolatile = 0x1, RegOptionCreateLink = 0x2, RegOptionOpenLink = 0x8;
    private const int KeyAllAccess = 0xF003F, DeleteAccess = 0x10000, KeyCreateLink = 0x20, KeySetValue = 0x2;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegCreateKeyExW(IntPtr key, string subKey, int reserved, string? className, int options, int access,
        IntPtr security, out Microsoft.Win32.SafeHandles.SafeRegistryHandle result, out int disposition);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegSetValueExW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegOpenKeyExW(IntPtr key, string subKey, int options, int access, out Microsoft.Win32.SafeHandles.SafeRegistryHandle result);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key);

    private static readonly IntPtr CurrentUser = unchecked((IntPtr)(int)0x80000001);

    public static void Create(RegistryLocation link, RegistryLocation target)
    {
        Registry.CurrentUser.CreateSubKey(link.Parent!.SubKey)!.Dispose();

        var result = RegCreateKeyExW(CurrentUser, link.SubKey, 0, null, RegOptionVolatile | RegOptionCreateLink,
            KeyAllAccess | KeyCreateLink, IntPtr.Zero, out var handle, out _);
        Assert.Equal(0, result);

        using (handle)
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            var destination = System.Text.Encoding.Unicode.GetBytes($@"\REGISTRY\USER\{sid}\{target.SubKey}");
            Assert.Equal(0, RegSetValueExW(handle, "SymbolicLinkValue", 0, 6, destination, destination.Length));
        }
    }

    /// <summary>Deletes the link itself, never what it points at.</summary>
    public static void Delete(RegistryLocation link)
    {
        if (RegOpenKeyExW(CurrentUser, link.SubKey, RegOptionOpenLink, DeleteAccess | KeySetValue, out var handle) != 0) return;
        using (handle) NtDeleteKey(handle);
    }
}

/// <summary>A firewall held in a list, so tests never touch the real one.</summary>
internal sealed class FakeFirewall : IFirewall
{
    public List<FirewallRuleInfo> Rules { get; } = [];

    public IReadOnlyList<FirewallRuleInfo> ReadAll(CancellationToken ct = default) => Rules.ToList();

    public bool RemoveAll(string name)
    {
        Rules.RemoveAll(r => r.Name == name);
        return true;
    }

    public void Add(FirewallRuleInfo rule) => Rules.Add(rule);
}

public class UninstallerRunnerTests
{
    [Fact]
    public async Task It_waits_for_an_uninstaller_that_relaunches_itself_and_exits()
    {
        // cmd starts ping and exits at once, as an NSIS uninstaller hands over to its copy in
        // Temp. Waiting only for cmd would return immediately; ping runs for about two seconds.
        var command = new UninstallCommand(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/c start \"\" /b ping -n 3 127.0.0.1");

        var stopwatch = Stopwatch.StartNew();
        var result = await UninstallerRunner.RunAsync(command, null, CancellationToken.None, hideWindow: true);

        Assert.True(result.Started);
        Assert.False(result.StoppedWaiting);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.5), $"Returned after {stopwatch.Elapsed.TotalSeconds:0.0}s, before the child finished.");
    }

    [Fact]
    public async Task Stopping_the_wait_leaves_the_uninstaller_alone_and_says_so()
    {
        var command = new UninstallCommand(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 6 127.0.0.1");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

        var result = await UninstallerRunner.RunAsync(command, null, stop.Token, hideWindow: true);

        Assert.True(result.StoppedWaiting);
        Assert.Contains("may still be running", result.Message);
    }

    [Fact]
    public async Task A_missing_uninstaller_is_reported_rather_than_thrown()
    {
        var result = await UninstallerRunner.RunAsync(
            new UninstallCommand(@"C:\Definitely\Not\There\unins000.exe", ""), null, CancellationToken.None, hideWindow: true);

        Assert.False(result.Started);
        Assert.Contains("could not be started", result.Message);
    }
}
