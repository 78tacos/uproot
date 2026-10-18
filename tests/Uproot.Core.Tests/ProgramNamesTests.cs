using Uproot.Core;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>
/// Name matching decides which folders and keys are offered as a program's leftovers, so it has
/// to recognise the program's own names and — more importantly — refuse to recognise anything else.
/// </summary>
public class ProgramNamesTests
{
    [Theory]
    [InlineData("Adobe Acrobat (64-bit)", "Adobe Acrobat")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox")]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip")]
    [InlineData("Microsoft .NET SDK 10.0.112 (x64)", "Microsoft .NET SDK")]
    [InlineData("Microsoft 365 Apps for enterprise - en-us", "Microsoft 365 Apps for enterprise")]
    [InlineData("PowerToys (Preview) x64", "PowerToys")]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "Microsoft Visual C++ v14 Redistributable")]
    [InlineData("Python 3.12.1 (64-bit)", "Python")]
    [InlineData("Git", "Git")]
    public void Display_names_lose_their_noise(string displayName, string expected)
    {
        Assert.Equal(expected, ProgramNames.CleanDisplayName(displayName));
    }

    [Theory]
    [InlineData("Adobe Systems Incorporated", "Adobe")]
    [InlineData("The Git Development Community", "Git")]
    [InlineData("Notepad++ Team", "Notepad++")]
    [InlineData("Microsoft Corporation", "Microsoft")]
    [InlineData("GitHub, Inc.", "GitHub")]
    [InlineData("Brave Software Inc", "Brave")]
    [InlineData("Luculent Systems, LLC", "Luculent")]
    [InlineData("Anthropic PBC", "Anthropic")]
    [InlineData("Software", "Software")]
    public void Publishers_lose_their_legal_form(string publisher, string expected)
    {
        Assert.Equal(expected, ProgramNames.CleanPublisher(publisher));
    }

    [Theory]
    [InlineData("Adobe Acrobat", "Adobe Acrobat", NameMatch.Exact)]
    [InlineData("AdobeAcrobat", "Adobe Acrobat", NameMatch.Exact)]
    [InlineData("adobe-acrobat", "Adobe Acrobat", NameMatch.Exact)]
    [InlineData("Acrobat Distiller", "Acrobat", NameMatch.Prefix)]
    [InlineData("Adobe Acrobat Update Task", "Adobe Acrobat", NameMatch.Prefix)]
    [InlineData("Launch Acrobat Helper", "Acrobat", NameMatch.Contains)]
    [InlineData("com.adobe.acrobat.chrome_webcapture", "Acrobat", NameMatch.Contains)]
    [InlineData("AcroCef", "Acrobat", NameMatch.None)]
    [InlineData("Notepad", "Notepad++", NameMatch.None)]
    public void Names_are_compared_by_how_closely_they_match(string candidate, string programName, NameMatch expected)
    {
        Assert.Equal(expected, ProgramNames.Compare(candidate, programName));
    }

    [Fact]
    public void A_short_name_only_ever_matches_exactly()
    {
        // "Git" as a prefix would claim GitHub's folders; as a word it would claim "Git LFS".
        Assert.Equal(NameMatch.Exact, ProgramNames.Compare("Git", "Git"));
        Assert.Equal(NameMatch.None, ProgramNames.Compare("GitHub", "Git"));
        Assert.Equal(NameMatch.None, ProgramNames.Compare("GitHub Desktop", "Git"));
        Assert.Equal(NameMatch.None, ProgramNames.Compare("Git LFS", "Git"));
    }

    [Theory]
    [InlineData("Tools")]
    [InlineData("Update")]
    [InlineData("Common Files")]
    [InlineData("Microsoft")]
    [InlineData("x64")]
    [InlineData("1.2.3")]
    [InlineData("app-1.0.9003")]
    [InlineData("12")]
    public void Generic_names_never_match_anything(string genericName)
    {
        Assert.True(ProgramNames.IsGeneric(genericName));
        Assert.Equal(NameMatch.None, ProgramNames.Compare(genericName, genericName));
    }

    [Fact]
    public void Acrobat_is_known_by_all_the_names_its_leftovers_carry()
    {
        var names = ProgramNames.ProductNames("Adobe Acrobat (64-bit)", "Adobe", @"C:\Program Files\Adobe\Acrobat DC\");

        Assert.Contains("Adobe Acrobat", names);
        Assert.Contains("Acrobat", names);        // %APPDATA%\Adobe\Acrobat
        Assert.Contains("Acrobat DC", names);     // the install folder's own name
        Assert.Equal("Adobe Acrobat", names[0]);  // strongest first
    }

    [Fact]
    public void A_generic_publisher_is_still_stripped_from_the_product_name()
    {
        // Microsoft identifies nothing, but "Visual Studio Code" on its own still identifies VS Code.
        var names = ProgramNames.ProductNames("Microsoft Visual Studio Code (User)", "Microsoft Corporation", null);

        Assert.Contains("Visual Studio Code", names);
    }

    [Fact]
    public void The_install_folder_skips_generic_leaves()
    {
        Assert.Equal("Brave-Browser", ProgramNames.InstallFolderName(@"C:\Program Files\BraveSoftware\Brave-Browser\Application"));
        Assert.Equal("Acrobat DC", ProgramNames.InstallFolderName(@"C:\Program Files\Adobe\Acrobat DC\"));
    }

    [Fact]
    public void Publisher_names_include_the_vendor_folder_and_a_distinctive_first_word()
    {
        var names = ProgramNames.PublisherNames("Brave Software Inc", "BraveSoftware");

        Assert.Contains("Brave", names);
        Assert.Contains("BraveSoftware", names);
    }

    [Fact]
    public void Generic_publishers_are_kept_as_containers()
    {
        // Microsoft\Teams is still worth looking inside; offering the Microsoft folder itself is
        // prevented elsewhere.
        Assert.Contains("Microsoft", ProgramNames.PublisherNames("Microsoft Corporation", null));
    }
}

public class UninstallCommandTests
{
    [Fact]
    public void A_quoted_path_keeps_its_arguments_verbatim()
    {
        var command = UninstallCommand.Parse("\"C:\\Program Files\\App\\setup.exe\" --uninstall --system-level", _ => false);

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\App\setup.exe", command.FileName);
        Assert.Equal("--uninstall --system-level", command.Arguments);
    }

    [Fact]
    public void An_unquoted_path_with_spaces_splits_at_the_existing_executable()
    {
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\My App\uninst.exe" };

        var command = UninstallCommand.Parse(@"C:\Program Files\My App\uninst.exe /S /D=C:\x", exists.Contains);

        Assert.Equal(@"C:\Program Files\My App\uninst.exe", command!.FileName);
        Assert.Equal(@"/S /D=C:\x", command.Arguments);
    }

    [Fact]
    public void A_missing_unquoted_uninstaller_is_still_identified()
    {
        // Needed to report "the uninstaller is missing" rather than "there is no uninstaller".
        var command = UninstallCommand.Parse(@"C:\Gone Away\Old App\unins000.exe /SILENT", _ => false);

        Assert.Equal(@"C:\Gone Away\Old App\unins000.exe", command!.FileName);
    }

    [Fact]
    public void A_bare_msiexec_resolves_to_the_system_copy()
    {
        var system = Path.Combine(Environment.SystemDirectory, "MsiExec.exe");
        var command = UninstallCommand.Parse("MsiExec.exe /I{AC76BA86-1033-FFFF-7760-BC15014EA700}",
            path => path.Equals(system, StringComparison.OrdinalIgnoreCase));

        Assert.True(command!.IsMsiExec);
        Assert.Equal(system, command.FileName, ignoreCase: true);
    }

    [Theory]
    [InlineData("/I{AC76BA86-1033-FFFF-7760-BC15014EA700}", true)]
    [InlineData("/i {AC76BA86-1033-FFFF-7760-BC15014EA700}", true)]
    [InlineData("/X{AC76BA86-1033-FFFF-7760-BC15014EA700}", false)]
    [InlineData("/x {AC76BA86-1033-FFFF-7760-BC15014EA700} /qn", false)]
    public void Maintenance_mode_is_told_apart_from_removal(string arguments, bool maintenance)
    {
        var command = new UninstallCommand(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), arguments);

        Assert.Equal(maintenance, command.IsMsiMaintenanceMode);
        Assert.Equal("{AC76BA86-1033-FFFF-7760-BC15014EA700}", command.MsiProductCode);
    }

    [Fact]
    public void Msi_removal_asks_nothing_and_never_restarts_by_itself_when_quiet()
    {
        var quiet = UninstallCommand.MsiRemove("{11111111-2222-3333-4444-555555555555}", quiet: true, "/I{11111111-2222-3333-4444-555555555555} REBOOT=ReallySuppress");

        Assert.StartsWith("/X{11111111-2222-3333-4444-555555555555}", quiet.Arguments);
        Assert.Contains("/qb", quiet.Arguments);
        Assert.Contains("/norestart", quiet.Arguments);
        Assert.Contains("REBOOT=ReallySuppress", quiet.Arguments);   // the vendor's property survives
    }

    [Fact]
    public void Switches_are_not_added_twice()
    {
        var command = new UninstallCommand(@"C:\App\uninstall.exe", "/S");

        Assert.Equal("/S", command.WithSwitches("/S").Arguments);
        Assert.Equal("/S --force", command.WithSwitches("/s", "--force").Arguments);
    }

    [Fact]
    public void Blank_commands_parse_to_nothing()
    {
        Assert.Null(UninstallCommand.Parse(null));
        Assert.Null(UninstallCommand.Parse("   "));
    }
}

public class RegistryLocationTests
{
    [Theory]
    [InlineData(@"SOFTWARE\Adobe\Acrobat", @"SOFTWARE\WOW6432Node\Adobe\Acrobat")]
    [InlineData(@"SOFTWARE\Classes\CLSID\{X}", @"SOFTWARE\Classes\WOW6432Node\CLSID\{X}")]
    [InlineData(@"SOFTWARE\Classes\AcroExch.Document", @"SOFTWARE\Classes\AcroExch.Document")]
    [InlineData(@"SOFTWARE\Policies\Adobe", @"SOFTWARE\Policies\Adobe")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\X", @"SYSTEM\CurrentControlSet\Services\X")]
    public void Thirty_two_bit_writes_land_where_WOW64_puts_them(string written, string expected)
    {
        Assert.Equal(expected, RegistryLocation.Machine(written).As32BitWrite().SubKey);
    }

    [Fact]
    public void The_current_user_hive_only_redirects_classes()
    {
        Assert.Equal(@"Software\Adobe", RegistryLocation.CurrentUser(@"Software\Adobe").As32BitWrite().SubKey);
        Assert.Equal(@"Software\Classes\WOW6432Node\CLSID\{X}", RegistryLocation.CurrentUser(@"Software\Classes\CLSID\{X}").As32BitWrite().SubKey);
    }

    [Fact]
    public void Paths_parse_in_both_spellings()
    {
        Assert.Equal(RegistryLocation.Machine(@"SOFTWARE\X"), RegistryLocation.Parse(@"HKEY_LOCAL_MACHINE\SOFTWARE\X"));
        Assert.Equal(RegistryLocation.CurrentUser(@"Software\X"), RegistryLocation.Parse(@"HKCU\Software\X\"));
        Assert.Null(RegistryLocation.Parse(@"HKCR\X"));
    }

    [Fact]
    public void Keys_compare_without_regard_to_case_and_nest_by_segment()
    {
        var parent = RegistryLocation.Machine(@"SOFTWARE\Adobe");

        Assert.Equal(parent, RegistryLocation.Machine(@"software\ADOBE"));
        Assert.True(RegistryLocation.Machine(@"SOFTWARE\Adobe\Acrobat").IsWithin(parent));
        Assert.False(RegistryLocation.Machine(@"SOFTWARE\AdobeX").IsWithin(parent));
        Assert.False(RegistryLocation.CurrentUser(@"SOFTWARE\Adobe\Acrobat").IsWithin(parent));
    }

    [Theory]
    [InlineData(@"02:\SOFTWARE\Adobe\", Microsoft.Win32.RegistryHive.LocalMachine, false, @"SOFTWARE\Adobe")]
    [InlineData(@"22:\SOFTWARE\Adobe", Microsoft.Win32.RegistryHive.LocalMachine, true, @"SOFTWARE\Adobe")]
    [InlineData(@"20:\CLSID\{X}\", Microsoft.Win32.RegistryHive.ClassesRoot, true, @"CLSID\{X}")]
    [InlineData(@"01:\Software\X", Microsoft.Win32.RegistryHive.CurrentUser, false, @"Software\X")]
    public void Msi_registry_key_paths_decode(string keyPath, Microsoft.Win32.RegistryHive hive, bool is64, string subKey)
    {
        var decoded = MsiInterop.MsiRegistryPath(keyPath);

        Assert.NotNull(decoded);
        Assert.Equal(hive, decoded.Value.Hive);
        Assert.Equal(is64, decoded.Value.Is64Bit);
        Assert.Equal(subKey, decoded.Value.SubKey);
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Windows\")]
    [InlineData(null)]
    public void Only_registry_key_paths_decode(string? keyPath)
    {
        Assert.Null(MsiInterop.MsiRegistryPath(keyPath));
    }
}

public class PathRulesTests
{
    [Fact]
    public void A_reference_must_end_at_a_path_boundary()
    {
        const string folder = @"C:\Program Files\Foo";

        Assert.True(PathRules.References(@"""C:\Program Files\Foo\foo.exe"" /background", folder));
        Assert.True(PathRules.References(@"c:\program files\foo", folder));
        Assert.False(PathRules.References(@"C:\Program Files\FooBar\x.exe", folder));
    }

    [Fact]
    public void A_sibling_whose_name_starts_the_same_is_not_a_reference()
    {
        // Uninstalling VS Code must not claim VS Code Insiders' registrations.
        const string folder = @"C:\Program Files\Microsoft VS Code";

        Assert.False(PathRules.References(@"""C:\Program Files\Microsoft VS Code Insiders\Code - Insiders.exe"" ""%1""", folder));
        Assert.False(PathRules.References(@"C:\Program Files\Microsoft VS Code Insiders\Code - Insiders.exe", folder));
        Assert.True(PathRules.References(@"""C:\Program Files\Microsoft VS Code\Code.exe"" ""%1""", folder));
        Assert.True(PathRules.References(@"""C:\Program Files\Microsoft VS Code""", folder));
    }

    [Fact]
    public void Device_prefixed_and_network_spellings_are_seen_through()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(PathRules.IsWithin(@"\\?\" + windows + @"\System32", windows));
        Assert.True(PathRules.IsWithin(@"\??\" + windows + @"\System32\drivers\x.sys", windows));
        Assert.True(PathRules.IsNetworkPath(@"\\localhost\C$\Windows"));
        Assert.True(PathRules.IsNetworkPath(@"\\?\UNC\server\share\folder"));
        Assert.False(PathRules.IsNetworkPath(@"C:\Program Files"));
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k netsvcs -p", @"System32\svchost.exe")]
    [InlineData(@"\SystemRoot\System32\drivers\acpi.sys", @"System32\drivers\acpi.sys")]
    [InlineData(@"system32\drivers\foo.sys", @"system32\drivers\foo.sys")]
    [InlineData(@"\??\C:\Windows\System32\drivers\bar.sys", @"System32\drivers\bar.sys")]
    public void Service_image_paths_resolve_in_every_form_the_service_manager_accepts(string imagePath, string relativeToWindows)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(PathRules.SamePath(Path.Combine(windows, relativeToWindows), PathRules.ServiceImage(imagePath)));
    }

    [Fact]
    public void Shared_dll_counts_mark_files_still_used_elsewhere()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [@"C:\Common\shared.dll"] = 2 };

        // While installed the program holds one of the two references; afterwards, none.
        Assert.True(SharedDlls.IsInUseByOthers(counts, @"C:\Common\shared.dll", ownUses: 1));
        Assert.True(SharedDlls.IsInUseByOthers(counts, @"c:\common\SHARED.dll", ownUses: 0));
        Assert.False(SharedDlls.IsInUseByOthers(counts, @"C:\Common\shared.dll", ownUses: 2));
        Assert.False(SharedDlls.IsInUseByOthers(counts, @"C:\Common\other.dll", ownUses: 0));
    }

    [Fact]
    public void Within_treats_trailing_separators_alike_and_respects_segments()
    {
        Assert.True(PathRules.IsWithin(@"C:\A\B\c.txt", @"C:\A\B\"));
        Assert.True(PathRules.IsWithin(@"C:\A\B", @"C:\A\B"));
        Assert.False(PathRules.IsWithin(@"C:\A\BC", @"C:\A\B"));
        Assert.False(PathRules.IsStrictlyWithin(@"C:\A\B\", @"C:\A\B"));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, uint length);

    [Fact]
    public void A_short_8dot3_spelling_is_the_same_place_as_the_long_one()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var buffer = new System.Text.StringBuilder(260);
        GetShortPathNameW(windows, buffer, 260);
        var shortForm = buffer.ToString();

        // Where 8.3 names are disabled there is no short spelling to dodge a rule with.
        if (!shortForm.Contains('~')) return;

        Assert.True(PathRules.SamePath(shortForm, windows));
        Assert.True(PathRules.IsWithin(Path.Combine(shortForm, "Contoso"), windows));
    }

    [Fact]
    public void Relative_and_blank_paths_normalize_to_nothing()
    {
        Assert.Null(PathRules.Normalize(@"relative\path"));
        Assert.Null(PathRules.Normalize(""));
        Assert.Equal(@"C:\", PathRules.Normalize(@"C:\"));
    }
}
