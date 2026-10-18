using Microsoft.Win32;
using Uproot.Core;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>
/// The safety policy is the last word on what may be deleted, whichever scan mode found it and
/// however the list was edited afterwards. These tests pin down what it refuses.
/// </summary>
public class LeftoverSafetyTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "uproot-safety");
    private static readonly string ProgramFiles = Path.Combine(Root, "Program Files");
    private static readonly string Windows = Path.Combine(Root, "Windows");
    private static readonly string StartMenu = Path.Combine(Root, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs");

    private static LeftoverSafety Policy(params ProgramClaim[] others) => new(new SafetyRules
    {
        ProtectedTrees = [Windows, Path.Combine(Root, "ProgramData", "Microsoft", "Windows")],
        ProtectedFolders = [ProgramFiles, Path.Combine(Root, "ProgramData"), StartMenu],
        AllowedWithinTrees = [StartMenu]
    }, others);

    // ---- Files -------------------------------------------------------------

    [Fact]
    public void Nothing_inside_Windows_may_go()
    {
        var safety = Policy();

        Assert.NotNull(safety.CheckPath(Windows));
        Assert.NotNull(safety.CheckPath(Path.Combine(Windows, "System32", "msvcp140.dll")));
    }

    [Fact]
    public void Standard_folders_stay_but_what_is_inside_them_may_go()
    {
        var safety = Policy();

        Assert.NotNull(safety.CheckPath(ProgramFiles));
        Assert.Null(safety.CheckPath(Path.Combine(ProgramFiles, "Contoso")));
    }

    [Fact]
    public void Drive_roots_and_relative_paths_are_refused()
    {
        var safety = Policy();

        Assert.NotNull(safety.CheckPath(@"C:\"));
        Assert.NotNull(safety.CheckPath(@"relative\folder"));
        Assert.NotNull(safety.CheckPath(null));
    }

    [Fact]
    public void Other_spellings_of_a_protected_folder_are_refused_too()
    {
        var safety = Policy();

        Assert.NotNull(safety.CheckPath(@"\\?\" + Path.Combine(Windows, "System32")));
        Assert.NotNull(safety.CheckPath(@"\??\" + Path.Combine(Windows, "System32")));
        Assert.NotNull(safety.CheckPath(@"\\localhost\C$\Windows"));
        Assert.NotNull(safety.CheckPath(@"\\?\" + ProgramFiles));
    }

    [Fact]
    public void Tasks_and_services_that_run_another_programs_files_are_theirs()
    {
        var gadget = Path.Combine(ProgramFiles, "Contoso", "Gadget");
        var safety = Policy(new ProgramClaim("Contoso Gadget", [gadget], ["Contoso Gadget"]));

        Assert.NotNull(safety.CheckTask(@"\Contoso Widget Updater", [$"\"{Path.Combine(gadget, "update.exe")}\" /check"]));
        Assert.Null(safety.CheckTask(@"\Contoso Widget Updater", [$"\"{Path.Combine(ProgramFiles, "Contoso", "Widget", "update.exe")}\""]));
        Assert.NotNull(safety.CheckService("ContosoSvc", $"\"{Path.Combine(gadget, "svc.exe")}\""));
    }

    [Fact]
    public void A_task_is_not_windows_own_just_because_it_uses_a_windows_host()
    {
        var safety = Policy();
        var rundll = Path.Combine(Environment.SystemDirectory, "rundll32.exe");

        Assert.Null(safety.CheckTask(@"\Contoso Cleanup", [$"{rundll} \"C:\\Contoso\\clean.dll\",Run"]));
        Assert.NotNull(safety.CheckTask(@"\Odd One", [Path.Combine(Environment.SystemDirectory, "defrag.exe")]));
    }

    [Fact]
    public void A_service_whose_program_cannot_be_identified_is_refused()
    {
        Assert.NotNull(Policy().CheckService("Mystery", null));
        Assert.NotNull(Policy().CheckService(@"Bad\Name", @"C:\x.exe"));
    }

    [Fact]
    public void Start_menu_entries_are_allowed_even_inside_Windows_own_ProgramData_folder()
    {
        var safety = Policy();

        Assert.Null(safety.CheckPath(Path.Combine(StartMenu, "Contoso Widget.lnk")));
        Assert.Null(safety.CheckPath(Path.Combine(StartMenu, "Contoso")));
        Assert.NotNull(safety.CheckPath(StartMenu));
        Assert.NotNull(safety.CheckPath(Path.Combine(Root, "ProgramData", "Microsoft", "Windows", "Caches")));
    }

    [Fact]
    public void Another_programs_folder_and_anything_containing_it_are_refused()
    {
        var gadget = Path.Combine(ProgramFiles, "Contoso", "Gadget");
        var safety = Policy(new ProgramClaim("Contoso Gadget", [gadget], ["Contoso Gadget", "Gadget"]));

        Assert.Contains("Contoso Gadget", safety.CheckPath(gadget));
        Assert.Contains("Contoso Gadget", safety.CheckPath(Path.Combine(gadget, "data.db")));
        Assert.Contains("Contoso Gadget", safety.CheckPath(Path.Combine(ProgramFiles, "Contoso")));
        Assert.Null(safety.CheckPath(Path.Combine(ProgramFiles, "Contoso", "Widget")));
    }

    [Fact]
    public void A_claim_on_a_standard_folder_is_ignored_rather_than_blocking_everything()
    {
        // Some installers record Program Files itself as their location.
        var safety = Policy(new ProgramClaim("Sloppy Installer", [ProgramFiles], ["Sloppy"]));

        Assert.Null(safety.CheckPath(Path.Combine(ProgramFiles, "Contoso")));
    }

    // ---- Names -------------------------------------------------------------

    [Fact]
    public void A_name_that_matches_another_installed_program_better_is_theirs()
    {
        var safety = Policy(new ProgramClaim("Adobe Acrobat Reader", [], ["Adobe Acrobat Reader", "Acrobat Reader"]));

        // Uninstalling Acrobat found "Acrobat Reader" by prefix; Reader matches it exactly.
        Assert.True(safety.CheckName("Acrobat Reader", NameMatch.Prefix).Blocked);
    }

    [Fact]
    public void A_name_that_starts_another_installed_programs_name_is_flagged_not_blocked()
    {
        var safety = Policy(new ProgramClaim("Adobe Acrobat Reader", [], ["Adobe Acrobat Reader", "Acrobat Reader"]));

        // %APPDATA%\Adobe\Acrobat is used by both Acrobat and Reader.
        var verdict = safety.CheckName("Acrobat", NameMatch.Exact);

        Assert.False(verdict.Blocked);
        Assert.Contains("Adobe Acrobat Reader", verdict.Note);
    }

    [Fact]
    public void Two_versions_of_the_same_product_make_every_name_ambiguous()
    {
        // Removing one .NET SDK must not take the folders the others share.
        var safety = Policy(new ProgramClaim("Microsoft .NET SDK 10.0.401 (x64)", [], ["Microsoft .NET SDK", ".NET SDK"]));

        var verdict = safety.CheckName(".NET SDK", NameMatch.Exact);
        Assert.NotNull(verdict.Note);
    }

    [Fact]
    public void An_unrelated_name_is_clear()
    {
        var safety = Policy(new ProgramClaim("Contoso Gadget", [], ["Contoso Gadget", "Gadget"]));

        Assert.Equal(NameVerdict.Clear, safety.CheckName("Widget", NameMatch.Exact));
    }

    // ---- Registry ----------------------------------------------------------

    [Theory]
    [InlineData(@"SOFTWARE")]
    [InlineData(@"SOFTWARE\WOW6432Node")]
    [InlineData(@"SOFTWARE\Microsoft")]
    [InlineData(@"SOFTWARE\Classes")]
    [InlineData(@"SOFTWARE\Classes\CLSID")]
    [InlineData(@"SOFTWARE\Classes\.pdf")]
    [InlineData(@"SOFTWARE\Classes\.pdf\OpenWithProgids")]
    [InlineData(@"SOFTWARE\Classes\*\shellex\ContextMenuHandlers")]
    [InlineData(@"SOFTWARE\Classes\Directory\shell")]
    [InlineData(@"SOFTWARE\Classes\Directory\Background")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\SomeService")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData(@"SOFTWARE\Classes\Installer\Products\68AB67CA3301FFFF7706CB5110E47A00")]
    [InlineData(@"SOFTWARE\Policies\Adobe")]
    public void Shared_and_system_machine_keys_are_refused(string subKey)
    {
        Assert.NotNull(Policy().CheckRegistryKey(RegistryLocation.Machine(subKey)));
    }

    [Theory]
    [InlineData(@"SOFTWARE\Adobe\Adobe Acrobat")]
    [InlineData(@"SOFTWARE\Classes\CLSID\{CA8A9780-280D-11CF-A24D-444553540000}")]
    [InlineData(@"SOFTWARE\Classes\WOW6432Node\CLSID\{CA8A9780-280D-11CF-A24D-444553540000}")]
    [InlineData(@"SOFTWARE\Classes\AcroExch.Document.DC")]
    [InlineData(@"SOFTWARE\Classes\Directory\shell\VSCode")]
    [InlineData(@"SOFTWARE\Classes\*\shellex\ContextMenuHandlers\Adobe.Acrobat.ContextMenu")]
    [InlineData(@"SOFTWARE\Classes\.pdf\OpenWithList\Acrobat.exe")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{AC76BA86-1033-FFFF-7760-BC15014EA700}")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Acrobat.exe")]
    public void A_programs_own_keys_under_those_are_allowed(string subKey)
    {
        Assert.Null(Policy().CheckRegistryKey(RegistryLocation.Machine(subKey)));
    }

    [Fact]
    public void User_hives_are_judged_alike_however_they_are_reached()
    {
        var safety = Policy();

        Assert.NotNull(safety.CheckRegistryKey(RegistryLocation.CurrentUser("Software")));
        Assert.NotNull(safety.CheckRegistryKey(RegistryLocation.Users(@"S-1-5-21-1-2-3-1001\Software")));
        Assert.NotNull(safety.CheckRegistryKey(RegistryLocation.Users(@"Uproot_S-1-5-21-1-2-3-1001\Software\Policies\X")));
        Assert.NotNull(safety.CheckRegistryKey(RegistryLocation.Users(@"S-1-5-21-1-2-3-1001_Classes\.pdf")));

        Assert.Null(safety.CheckRegistryKey(RegistryLocation.Users(@"Uproot_S-1-5-21-1-2-3-1001\Software\Contoso")));
        Assert.Null(safety.CheckRegistryKey(RegistryLocation.Users(@"S-1-5-21-1-2-3-1001_Classes\Contoso.Document")));
    }

    [Fact]
    public void Environment_values_like_PATH_are_never_removed_whole()
    {
        Assert.NotNull(Policy().CheckRegistryValue(RegistryLocation.CurrentUser("Environment"), "Path"));
        Assert.Null(Policy().CheckRegistryValue(RegistryLocation.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Run"), "Contoso"));
    }

    [Fact]
    public void Windows_own_services_and_tasks_are_refused()
    {
        var safety = Policy();
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe");

        Assert.NotNull(safety.CheckService("Dnscache", $"{system32} -k NetworkService"));
        Assert.Null(safety.CheckService("AdobeARMservice", @"""C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\armsvc.exe"""));
        Assert.NotNull(safety.CheckTask(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.Null(safety.CheckTask(@"\Adobe Acrobat Update Task"));
    }

    [Fact]
    public void The_real_machine_protects_its_own_standard_folders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var safety = LeftoverSafety.ForThisMachine([], [profile]);

        Assert.NotNull(safety.CheckPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.NotNull(safety.CheckPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        Assert.NotNull(safety.CheckPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        Assert.NotNull(safety.CheckPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")));
        Assert.NotNull(safety.CheckPath(profile));
        Assert.NotNull(safety.CheckPath(Path.Combine(profile, "Documents")));
        Assert.Null(safety.CheckPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Contoso")));
    }
}
