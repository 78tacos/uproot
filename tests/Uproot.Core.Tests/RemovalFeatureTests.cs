using System.Text;
using Microsoft.Win32;
using Uproot.Core.Cli;
using Uproot.Core;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>
/// The newer parts of removal: how firmly each kind of item is protected, reading backups back,
/// editing PATH one entry at a time, finding things in the Recycle Bin, Store apps and the
/// command line for undoing a removal.
/// </summary>
public class RemovalFeatureTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "uproot-features");
    private static readonly string ProgramFiles = Path.Combine(Root, "Program Files");
    private static readonly string Windows = Path.Combine(Root, "Windows");
    private static readonly string Gadget = Path.Combine(ProgramFiles, "Contoso", "Gadget");

    private static LeftoverSafety Policy() => new(new SafetyRules
    {
        ProtectedTrees = [Windows],
        ProtectedFolders = [ProgramFiles, Path.Combine(ProgramFiles, "Common Files")]
    }, [new ProgramClaim("Contoso Gadget", [Gadget], ["Contoso Gadget"])]);

    // ---- How firmly things are protected -------------------------------------------

    [Fact]
    public void Another_programs_folder_can_be_overridden_but_Windows_and_standard_folders_cannot()
    {
        var safety = Policy();

        Assert.False(safety.JudgePath(Gadget)!.Absolute);
        Assert.True(safety.JudgePath(Path.Combine(Windows, "System32"))!.Absolute);
        Assert.True(safety.JudgePath(ProgramFiles)!.Absolute);
        Assert.True(safety.JudgePath(@"C:\")!.Absolute);
        Assert.True(safety.JudgePath(@"\\server\share\x")!.Absolute);
        Assert.Null(safety.JudgePath(Path.Combine(ProgramFiles, "Contoso", "Widget")));
    }

    [Fact]
    public void A_known_folder_moved_into_OneDrive_is_as_protected_as_one_in_the_profile()
    {
        var oneDrive = Path.Combine(Root, "OneDrive - Contoso");
        var documents = Path.Combine(oneDrive, "Documents");

        var rules = SafetyRules.ForThisMachine([Path.Combine(Root, "Users", "tester")], [oneDrive, documents]);
        var safety = new LeftoverSafety(rules, []);

        Assert.True(safety.JudgePath(documents)!.Absolute);
        Assert.True(safety.JudgePath(oneDrive)!.Absolute);
        Assert.Null(safety.JudgePath(Path.Combine(documents, "Contoso Widget")));
    }

    [Fact]
    public void A_folder_holding_a_standard_folder_is_kept_always()
    {
        // Root holds Program Files and Windows; nothing could make removing it safe.
        var verdict = Policy().JudgePath(Root);

        Assert.NotNull(verdict);
        Assert.True(verdict.Absolute);
    }

    [Fact]
    public void Windows_records_about_a_program_may_go_value_by_value_but_never_the_key()
    {
        var safety = Policy();
        var muiCache = RegistryLocation.CurrentUser(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
        var widget = Path.Combine(ProgramFiles, "Contoso", "Widget", "widget.exe");

        Assert.Null(safety.JudgeRegistryValue(muiCache, widget + ".FriendlyAppName"));
        Assert.True(safety.JudgeRegistryValue(muiCache, Path.Combine(Windows, "notepad.exe") + ".FriendlyAppName")!.Absolute);
        Assert.True(safety.JudgeRegistryValue(muiCache, "not a path")!.Absolute);
        Assert.True(safety.JudgeRegistryKey(muiCache)!.Absolute);

        // The same rules through another account's classes hive.
        var theirs = RegistryLocation.Users(@"S-1-5-21-1-2-3-1001_Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
        Assert.Null(safety.JudgeRegistryValue(theirs, widget + ".ApplicationCompany"));

        var folders = RegistryLocation.Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders");
        Assert.Null(safety.JudgeRegistryValue(folders, Path.Combine(ProgramFiles, "Contoso", "Widget") + @"\"));
        Assert.True(safety.JudgeRegistryValue(folders, ProgramFiles + @"\")!.Absolute);

        // Other values in Windows' trees stay.
        Assert.True(safety.JudgeRegistryValue(RegistryLocation.Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData"), "x")!.Absolute);
    }

    [Fact]
    public void A_SharedDLLs_count_goes_only_once_its_file_has()
    {
        var safety = Policy();
        var shared = RegistryLocation.Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs");
        var present = Path.GetTempFileName();

        try
        {
            Assert.True(safety.JudgeRegistryValue(shared, present)!.Absolute);
            Assert.Null(safety.JudgeRegistryValue(shared, Path.Combine(ProgramFiles, "Contoso", "Widget", "gone.dll")));
            Assert.True(safety.JudgeRegistryValue(shared, Path.Combine(Windows, "System32", "gone.dll"))!.Absolute);
        }
        finally
        {
            File.Delete(present);
        }
    }

    [Fact]
    public void An_event_source_goes_only_when_its_messages_are_the_programs()
    {
        var safety = Policy();
        var widgetDll = Path.Combine(ProgramFiles, "Contoso", "Widget", "messages.dll");
        var netMessages = Path.Combine(Windows, "Microsoft.NET", "Framework64", "v4.0.30319", "EventLogMessages.dll");

        Assert.Null(safety.JudgeEventLogSource("Application", "Contoso Widget", [widgetDll]));
        Assert.True(safety.JudgeEventLogSource("Application", "Application", [widgetDll])!.Absolute);
        Assert.True(safety.JudgeEventLogSource("System", "Disk", [Path.Combine(Windows, "System32", "disk.sys")])!.Absolute);
        Assert.True(safety.JudgeEventLogSource("Application", "Nothing", [])!.Absolute);
        Assert.False(safety.JudgeEventLogSource("Application", "Gadget", [Path.Combine(Gadget, "m.dll")])!.Absolute);

        // .NET's shared message file proves nothing either way; the scan goes by the name.
        Assert.Null(safety.JudgeEventLogSource("Application", "Contoso Widget", [netMessages]));
    }

    [Fact]
    public void A_PATH_entry_for_Windows_stays_whatever_anyone_chooses()
    {
        var safety = Policy();
        var environment = RegistryLocation.CurrentUser("Environment");

        Assert.Null(safety.JudgePathEntry(environment, "Path", Path.Combine(ProgramFiles, "Contoso", "Widget", "bin")));
        Assert.True(safety.JudgePathEntry(environment, "Path", Path.Combine(Windows, "System32"))!.Absolute);
        Assert.True(safety.JudgePathEntry(environment, "Path", "bin")!.Absolute);
        Assert.False(safety.JudgePathEntry(environment, "Path", Gadget)!.Absolute);

        // Only PATH, and only in an environment key.
        Assert.True(safety.JudgePathEntry(environment, "PSModulePath", Path.Combine(ProgramFiles, "Contoso"))!.Absolute);
        Assert.True(safety.JudgePathEntry(RegistryLocation.CurrentUser(@"Software\Contoso"), "Path", Path.Combine(ProgramFiles, "Contoso"))!.Absolute);
        Assert.Null(safety.JudgePathEntry(LeftoverSafety.MachineEnvironmentKey, "Path", Path.Combine(ProgramFiles, "Contoso", "Widget")));

        // Taking the whole variable out is never offered.
        Assert.True(safety.JudgeRegistryValue(environment, "Path")!.Absolute);
    }

    [Fact]
    public void Firewall_rules_in_Windows_groups_or_for_Windows_programs_are_kept_always()
    {
        var safety = Policy();
        var widget = Path.Combine(ProgramFiles, "Contoso", "Widget", "widget.exe");

        Assert.Null(safety.JudgeFirewallRules([new FirewallRuleInfo { Name = "W", ApplicationName = widget }]));
        Assert.True(safety.JudgeFirewallRules([new FirewallRuleInfo { Name = "W", ApplicationName = widget, Grouping = "@FirewallAPI.dll,-1" }])!.Absolute);
        Assert.True(safety.JudgeFirewallRules([new FirewallRuleInfo { Name = "W", ApplicationName = Path.Combine(Windows, "System32", "svchost.exe") }])!.Absolute);
        Assert.True(safety.JudgeFirewallRules([new FirewallRuleInfo { Name = "W", ApplicationName = widget }, new FirewallRuleInfo { Name = "W" }])!.Absolute);
        Assert.False(safety.JudgeFirewallRules([new FirewallRuleInfo { Name = "G", ApplicationName = Path.Combine(Gadget, "g.exe") }])!.Absolute);
    }

    [Fact]
    public void A_service_whose_program_cannot_be_told_can_be_overridden_but_a_Windows_one_cannot()
    {
        var safety = Policy();

        Assert.False(safety.JudgeService("ContosoSvc", null)!.Absolute);
        Assert.True(safety.JudgeService("Winmgmt", @"%SystemRoot%\system32\svchost.exe -k netsvcs")!.Absolute);
        Assert.True(safety.JudgeService(@"bad\name", "x.exe")!.Absolute);
        Assert.True(safety.JudgeTask(@"\Microsoft\Windows\Defrag\ScheduledDefrag")!.Absolute);
    }

    // ---- Reading backups back -------------------------------------------------------

    private const string Export = """
        Windows Registry Editor Version 5.00

        [HKEY_CURRENT_USER\Software\Contoso\Widget]
        @="default"
        "Name"="Contoso \"Widget\" C:\\Tools"
        "Count"=dword:0000002a
        "Big"=hex(b):01,00,00,00,00,00,00,00
        "Path"=hex(2):25,00,54,00,45,00,4d,00,50,00,25,00,00,00
        "List"=hex(7):61,00,00,00,62,00,00,00,00,00
        "Blob"=hex:de,ad,\
          be,ef

        [HKEY_CURRENT_USER\Software\Contoso\Widget\Sub]
        "X"="y"
        """;

    [Fact]
    public void A_reg_export_is_read_back_value_by_value()
    {
        var file = RegFile.Parse(Export);
        var key = RegistryLocation.CurrentUser(@"Software\Contoso\Widget");

        Assert.True(file.IsValid);
        Assert.Equal(2, file.Keys.Count);
        Assert.Equal("default", file.FindValue(key, "")!.Data);
        Assert.Equal("Contoso \"Widget\" C:\\Tools", file.FindValue(key, "Name")!.Data);
        Assert.Equal(42, file.FindValue(key, "count")!.Data);
        Assert.Equal(1L, file.FindValue(key, "Big")!.Data);
        Assert.Equal(RegistryValueKind.ExpandString, file.FindValue(key, "Path")!.Kind);
        Assert.Equal("%TEMP%", file.FindValue(key, "Path")!.Data);
        Assert.Equal(new[] { "a", "b" }, file.FindValue(key, "List")!.Data);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, file.FindValue(key, "Blob")!.Data);
    }

    [Fact]
    public void A_reg_file_is_trusted_only_for_its_own_key()
    {
        var file = RegFile.Parse(Export);

        Assert.True(file.OnlyTouches(RegistryLocation.CurrentUser(@"Software\Contoso\Widget")));
        Assert.True(file.OnlyTouches(RegistryLocation.CurrentUser(@"Software\Contoso")));
        Assert.False(file.OnlyTouches(RegistryLocation.CurrentUser(@"Software\Contoso\Widget\Sub")));

        Assert.False(RegFile.Parse(Export + "\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Evil]\r\n").OnlyTouches(RegistryLocation.CurrentUser(@"Software\Contoso")));
        Assert.False(RegFile.Parse(Export + "\r\n[-HKEY_CURRENT_USER\\Software\\Contoso\\Widget]\r\n").OnlyTouches(RegistryLocation.CurrentUser(@"Software\Contoso")));
        Assert.False(RegFile.Parse(Export + "\r\n\"Gone\"=-\r\n").OnlyTouches(RegistryLocation.CurrentUser(@"Software\Contoso")));
        Assert.False(RegFile.Parse("[HKEY_CLASSES_ROOT\\x]\r\n").OnlyTouches(RegistryLocation.CurrentUser("x")));
    }

    [Fact]
    public void Anything_a_genuine_export_would_not_contain_marks_a_reg_file_invalid()
    {
        var key = RegistryLocation.CurrentUser(@"Software\Contoso");
        const string head = "Windows Registry Editor Version 5.00\r\n\r\n";

        // The header has to come first.
        Assert.False(RegFile.Parse("[HKEY_CURRENT_USER\\Software\\Contoso]\r\n").IsValid);
        Assert.False(RegFile.Parse("; note\r\n" + head + "[HKEY_CURRENT_USER\\Software\\Contoso]\r\n").IsValid);

        // A header is one key to its last bracket — key names may hold brackets — so a second
        // header on the same line is just part of an odd key name, and not within Contoso.
        Assert.False(RegFile.Parse(head + "[HKEY_CURRENT_USER\\Software\\Contoso] [HKEY_LOCAL_MACHINE\\SOFTWARE]\r\n").OnlyTouches(key));
        Assert.False(RegFile.Parse(head + "[HKEY_CURRENT_USER\\Software\\Contoso] trailing\r\n").IsValid);

        // Nothing after a string's closing quote.
        Assert.False(RegFile.Parse(head + "[HKEY_CURRENT_USER\\Software\\Contoso]\r\n\"a\"=\"b\" \"c\"\r\n").IsValid);

        // A lone carriage return ends a line, so a header cannot hide on the end of another.
        var hidden = RegFile.Parse(head + "[HKEY_CURRENT_USER\\Software\\Contoso]\r\"a\"=\"b\"\r[HKEY_LOCAL_MACHINE\\SOFTWARE\\Evil]\r\n");
        Assert.False(hidden.OnlyTouches(key));

        // Only a hex value's data continues onto the next line.
        var joined = RegFile.Parse(head + "[HKEY_CURRENT_USER\\Software\\Contoso]\r\n; ,\\\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Evil]\r\n");
        Assert.False(joined.OnlyTouches(key));

        // Registry links, malformed numbers and malformed bytes are refused.
        Assert.False(RegFile.Parse(head + "[HKEY_CURRENT_USER\\Software\\Contoso]\r\n\"SymbolicLinkValue\"=hex(6):5c,00\r\n").IsValid);
        Assert.False(RegFile.Parse(head + "[HKEY_CURRENT_USER\\Software\\Contoso]\r\n\"n\"=dword:1\r\n").IsValid);
        Assert.False(RegFile.Parse(head + "[HKEY_CURRENT_USER\\Software\\Contoso]\r\n\"b\"=hex:1,2\r\n").IsValid);
    }

    [Fact]
    public void A_value_of_a_type_dotnet_has_no_kind_for_is_written_back_with_its_own_type()
    {
        var root = @"Software\Uproot.Tests\" + Guid.NewGuid().ToString("N");
        var key = RegistryLocation.CurrentUser(root + @"\Resources");

        try
        {
            // REG_DWORD_BIG_ENDIAN: a real type with no RegistryValueKind, which .NET reads back as bytes.
            var file = RegFile.Parse($"Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\{root}\\Resources]\r\n\"Big\"=hex(5):01,02,03,04\r\n\"Name\"=\"x\"\r\n");
            Assert.True(file.OnlyTouches(key));

            file.WriteWithin(key);

            using var open = key.Open();
            Assert.Equal(RegistryValueKind.Unknown, open!.GetValueKind("Big"));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, open.GetValue("Big"));
            Assert.Equal("x", open.GetValue("Name"));

            // Writing outside the key it was checked against is refused outright.
            Assert.Throws<InvalidOperationException>(() => file.WriteWithin(RegistryLocation.CurrentUser(root + @"\Other")));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    // ---- PATH ---------------------------------------------------------------------

    [Fact]
    public void A_PATH_entry_comes_out_alone_and_goes_back_on_the_end()
    {
        var before = @"C:\A;C:\Contoso\bin\;;%SystemRoot%;C:\contoso\BIN";

        var after = EnvironmentPath.Without(before, @"C:\Contoso\bin", out var removed);

        Assert.Equal(2, removed);
        Assert.Equal(@"C:\A;;%SystemRoot%", after);
        Assert.Equal(@"C:\A;;%SystemRoot%;C:\Contoso\bin", EnvironmentPath.With(after, @"C:\Contoso\bin"));
        Assert.False(EnvironmentPath.SameEntry(@"%SystemRoot%\System32", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")));
        Assert.Equal(before, EnvironmentPath.With(before, @"C:\CONTOSO\BIN"));
        Assert.Equal(@"C:\X", EnvironmentPath.With(null, @"C:\X"));
    }

    // ---- The Recycle Bin ------------------------------------------------------------

    [Fact]
    public void A_recycle_bin_record_says_where_the_item_came_from_and_when()
    {
        var original = @"C:\Users\tester\AppData\Roaming\Contoso Widget";
        var deleted = new DateTimeOffset(2026, 9, 24, 10, 55, 5, TimeSpan.Zero);

        var chars = original.Length + 1;
        var bytes = new byte[28 + chars * 2];
        BitConverter.GetBytes(2L).CopyTo(bytes, 0);
        BitConverter.GetBytes(4096L).CopyTo(bytes, 8);
        BitConverter.GetBytes(deleted.ToFileTime()).CopyTo(bytes, 16);
        BitConverter.GetBytes(chars).CopyTo(bytes, 24);
        Encoding.Unicode.GetBytes(original).CopyTo(bytes, 28);

        var file = Path.Combine(Path.GetTempPath(), $"$Iwgu{Guid.NewGuid():N}");
        File.WriteAllBytes(file, bytes);

        try
        {
            var record = RecycleBin.ReadRecord(file);

            Assert.NotNull(record);
            Assert.Equal(original, record.OriginalPath);
            Assert.Equal(deleted, record.DeletedAt);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- Store apps -------------------------------------------------------------------

    [Fact]
    public void A_store_app_is_removed_by_Windows_and_its_name_is_checked_first()
    {
        Assert.True(StoreApps.IsValidFullName("Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe"));
        Assert.True(StoreApps.IsValidFullName("Contoso.App_1.0.0.0_neutral_~_abcdefghijklm"));
        Assert.False(StoreApps.IsValidFullName("Contoso'; Remove-Item C:\\ -Recurse; '"));
        Assert.False(StoreApps.IsValidFullName("a b"));

        Assert.False(StoreApps.IsValidFullName("Contoso.App_1.0.0.0_x64__abc\n"));

        var app = new InstalledProgram
        {
            Key = RegistryLocation.CurrentUser(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Contoso.App_1.0.0.0_x64__abc"),
            PackageFullName = "Contoso.App_1.0.0.0_x64__abc",
            DisplayName = "Contoso App",
            InstallLocation = @"C:\Program Files",
            Scope = ProgramScope.User
        };

        Assert.Equal(InstallerTechnology.Msix, UninstallerAnalyzer.QuickDetect(app));

        var analysis = UninstallerAnalyzer.Analyze(app);
        Assert.True(analysis.CanUninstall);
        Assert.Contains("Contoso.App_1.0.0.0_x64__abc", analysis.InteractiveCommand!.Arguments);

        // Only a WindowsApps folder counts as a package's install folder.
        Assert.Null(analysis.InstallLocation);
        Assert.Equal(@"D:\WindowsApps\Contoso.App_1.0.0.0_x64__abc",
            StoreApps.PackageFolder(app with { InstallLocation = @"D:\WindowsApps\Contoso.App_1.0.0.0_x64__abc" }));

        var forged = app with { PackageFullName = "x\" & calc & \"" };
        Assert.False(UninstallerAnalyzer.Analyze(forged).CanUninstall);

        // An ordinary Uninstall entry carrying a value of that name is still an ordinary program.
        var impostor = new InstalledProgram
        {
            Key = RegistryLocation.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Impostor"),
            DisplayName = "Impostor",
            Scope = ProgramScope.User,
            UninstallString = @"C:\Tools\uninstall.exe",
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PackageFullName"] = "Contoso.App_1.0.0.0_x64__abc" }
        };
        Assert.False(impostor.IsStoreApp);
        Assert.NotEqual(InstallerTechnology.Msix, UninstallerAnalyzer.QuickDetect(impostor));

        Assert.True(StoreApps.IsInfrastructure("Microsoft.DesktopAppInstaller"));
        Assert.False(StoreApps.IsInfrastructure("Microsoft.Windows.Photos"));
    }

    // ---- The command line -------------------------------------------------------------

    [Fact]
    public void Backups_and_restore_parse_and_misuse_is_refused()
    {
        Assert.Equal(CliVerb.Backups, CommandLineParser.Parse(["--backups"]).Verb);

        var restore = CommandLineParser.Parse(["--restore", "latest", "--json"]);
        Assert.Equal(CliVerb.Restore, restore.Verb);
        Assert.Equal("latest", restore.RestoreQuery);
        Assert.True(restore.Json);

        Assert.NotNull(CommandLineParser.Parse(["--restore"]).Error);

        var folder = CommandLineParser.Parse(["--leftovers", "Old Tool", "--folder", @"C:\Tools\Old"]);
        Assert.Equal(@"C:\Tools\Old", folder.InstallFolder);
        Assert.NotNull(CommandLineParser.Parse(["--analyze", "x", "--folder", @"C:\Tools"]).Error);
    }

    [Fact]
    public void A_backup_is_found_by_name_prefix_or_latest_and_never_by_guess()
    {
        RemovalRecord Record(string name, int day) => new(Path.Combine(Root, name), new RemovalManifest
        {
            Format = RemovalManifest.CurrentFormat,
            Program = name,
            RemovedAt = new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero)
        });

        IReadOnlyList<RemovalRecord> records = [Record("2026-09-24 105505 Fabrikam", 24), Record("2026-09-23 101010 Contoso", 23), Record("2026-09-23 111111 Contoso", 23)];

        Assert.Equal("2026-09-24 105505 Fabrikam", RemovalHistory.Find(records, "latest")!.Name);
        Assert.Equal("2026-09-24 105505 Fabrikam", RemovalHistory.Find(records, "2026-09-24")!.Name);
        Assert.Null(RemovalHistory.Find(records, "2026-09-23"));
        Assert.Null(RemovalHistory.Find(records, "nothing"));
    }
}
