using Microsoft.Win32;
using Uproot.Core;
using Uproot.Core.Cli;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>
/// Installing for everyone, run against a temporary folder and keys under HKCU so it needs no
/// administrator rights. Only the rights check itself, and the real Program Files, are left out.
/// </summary>
public sealed class InstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uproot-install-tests", Guid.NewGuid().ToString("N"));
    private readonly string _registryRoot = @"Software\Uproot.Tests\" + Guid.NewGuid().ToString("N");
    private readonly InstallLayout _layout;
    private readonly string _source;

    public InstallationTests()
    {
        _layout = new InstallLayout
        {
            Folder = Path.Combine(_root, "Program Files", "Uproot"),
            ShortcutPath = Path.Combine(_root, "Start Menu", "Programs", "Uproot.lnk"),
            UninstallHive = Registry.CurrentUser,
            UninstallKeyPath = _registryRoot + @"\Uninstall\Uproot",
            EnvironmentHive = Registry.CurrentUser,
            EnvironmentKeyPath = _registryRoot + @"\Environment",
            Broadcast = false
        };

        // Any two files of one build stand in for Uproot.exe and uproot.com: the install checks
        // versions, not what the files do. This assembly is as good as any.
        _source = Path.Combine(_root, "Downloads", "Uproot");
        Directory.CreateDirectory(_source);
        var standIn = typeof(Installation).Assembly.Location;
        File.Copy(standIn, Path.Combine(_source, Installation.AppFile));
        File.Copy(standIn, Path.Combine(_source, Installation.CliFile));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private RegistryKey? UninstallKey() => _layout.UninstallHive.OpenSubKey(_layout.UninstallKeyPath);

    private void SetPath(string value, RegistryValueKind kind = RegistryValueKind.ExpandString)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_layout.EnvironmentKeyPath);
        key.SetValue("Path", value, kind);
    }

    private string? ReadPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_layout.EnvironmentKeyPath);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    [Fact]
    public void Installing_adds_the_files_an_entry_a_shortcut_and_PATH()
    {
        SetPath(@"%SystemRoot%\system32;C:\Tools");

        var outcome = Installation.Install(Installation.FromFolder(_source), _layout);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(outcome.Notes);
        Assert.True(File.Exists(_layout.AppPath));
        Assert.True(File.Exists(_layout.CliPath));
        Assert.True(File.Exists(_layout.ShortcutPath));
        Assert.Empty(Directory.EnumerateFiles(_layout.Folder, "*.new"));

        using var key = UninstallKey();
        Assert.Equal("Uproot", key!.GetValue("DisplayName"));
        Assert.Equal($"\"{_layout.AppPath}\" self-uninstall", key.GetValue("UninstallString"));
        Assert.Equal($"\"{_layout.AppPath}\" self-uninstall --quiet", key.GetValue("QuietUninstallString"));
        Assert.Equal(_layout.Folder, key.GetValue("InstallLocation"));
        Assert.Equal(_layout.AppPath + ",0", key.GetValue("DisplayIcon"));
        Assert.Equal(1, key.GetValue("NoModify"));

        Assert.Equal($@"%SystemRoot%\system32;C:\Tools;{_layout.Folder}", ReadPath());
        using var environment = Registry.CurrentUser.OpenSubKey(_layout.EnvironmentKeyPath);
        Assert.Equal(RegistryValueKind.ExpandString, environment!.GetValueKind("Path"));
    }

    [Fact]
    public void Installing_again_adds_PATH_once_and_finds_the_installed_copy()
    {
        SetPath(@"C:\Tools");

        Assert.True(Installation.Install(Installation.FromFolder(_source), _layout).Succeeded);
        Assert.True(Installation.Install(Installation.FromFolder(_source), _layout).Succeeded);

        Assert.Equal($@"C:\Tools;{_layout.Folder}", ReadPath());

        var installed = Installation.FindInstalled(_layout);
        Assert.NotNull(installed);
        Assert.Equal(_layout.AppPath, installed!.ExecutablePath);
        Assert.NotNull(installed.Build);
    }

    [Fact]
    public void Repairing_from_the_installed_folder_copies_nothing_and_puts_the_rest_back()
    {
        Assert.True(Installation.Install(Installation.FromFolder(_source), _layout).Succeeded);
        File.Delete(_layout.ShortcutPath);
        _layout.UninstallHive.DeleteSubKeyTree(_layout.UninstallKeyPath);

        var outcome = Installation.Install([], _layout);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(File.Exists(_layout.ShortcutPath));
        Assert.NotNull(Installation.FindInstalled(_layout));
    }

    [Fact]
    public void Two_different_builds_are_not_installed_and_leave_nothing_behind()
    {
        // A different assembly, so a different version: a half-copied folder.
        File.Copy(typeof(FactAttribute).Assembly.Location, Path.Combine(_source, Installation.CliFile), overwrite: true);

        var outcome = Installation.Install(Installation.FromFolder(_source), _layout);

        Assert.False(outcome.Succeeded);
        Assert.Contains("half-copied", outcome.Message);
        Assert.Empty(Directory.EnumerateFiles(_layout.Folder));
        Assert.Null(UninstallKey());
        Assert.Null(Installation.FindInstalled(_layout));
    }

    [Fact]
    public void Removing_takes_away_exactly_what_installing_added()
    {
        SetPath(@"C:\Tools;C:\Other");
        Assert.True(Installation.Install(Installation.FromFolder(_source), _layout).Succeeded);

        var outcome = Installation.Uninstall(_layout, cleanUpAfterExit: false);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(outcome.Notes);
        Assert.Null(UninstallKey());
        Assert.False(File.Exists(_layout.ShortcutPath));
        Assert.False(Directory.Exists(_layout.Folder));
        Assert.Equal(@"C:\Tools;C:\Other", ReadPath());
        Assert.Null(Installation.FindInstalled(_layout));
    }

    [Fact]
    public void Removing_leaves_files_it_did_not_put_there()
    {
        Assert.True(Installation.Install(Installation.FromFolder(_source), _layout).Succeeded);
        var stranger = Path.Combine(_layout.Folder, "notes.txt");
        File.WriteAllText(stranger, "someone else's");

        Assert.True(Installation.Uninstall(_layout, cleanUpAfterExit: false).Succeeded);

        Assert.True(File.Exists(stranger));
        Assert.False(File.Exists(_layout.AppPath));
        Assert.False(File.Exists(_layout.CliPath));
    }

    [Fact]
    public void Removing_when_nothing_is_installed_is_not_an_error()
    {
        var outcome = Installation.Uninstall(_layout, cleanUpAfterExit: false);

        Assert.True(outcome.Succeeded, outcome.Message);
    }

    [Fact]
    public void A_folder_without_both_files_is_not_installable()
    {
        File.Delete(Path.Combine(_source, Installation.CliFile));

        Assert.Contains(Installation.CliFile, Installation.ProblemWithFolder(_source));
    }

    [Fact]
    public void A_development_build_is_not_installable()
    {
        File.WriteAllText(Path.Combine(_source, "Uproot.dll"), "");

        Assert.Contains("development build", Installation.ProblemWithFolder(_source));
    }

    [Fact]
    public void A_published_folder_is_installable()
    {
        Assert.Null(Installation.ProblemWithFolder(_source));
    }

    [Fact]
    public void Exit_codes_say_how_far_it_got()
    {
        Assert.Equal(Installation.ExitCodes.Done, new InstallOutcome(true, "").ExitCode);
        Assert.Equal(Installation.ExitCodes.DoneWithNotes, new InstallOutcome(true, "") { Notes = ["no shortcut"] }.ExitCode);
        Assert.Equal(Installation.ExitCodes.Failed, new InstallOutcome(false, "").ExitCode);

        var messages = new[] { 0, 1, 2, 64, 70 }.Select(c => Installation.ExitCodes.Describe(c, install: true)).ToList();
        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    [Theory]
    [InlineData(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uproot", true)]
    [InlineData(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uproot", false)]
    [InlineData(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uproot Tools", false)]
    public void Uproot_knows_its_own_entry(RegistryHive hive, string path, bool itself)
    {
        var program = new InstalledProgram
        {
            Key = hive == RegistryHive.LocalMachine ? RegistryLocation.Machine(path) : RegistryLocation.CurrentUser(path),
            DisplayName = "Uproot",
            Scope = hive == RegistryHive.LocalMachine ? ProgramScope.Machine : ProgramScope.User
        };

        Assert.Equal(itself, Installation.IsThisProgram(program));
        Assert.Equal(itself, ProgramOwner.WhyNotHere(program)?.Contains("Uproot itself") == true);
    }

    [Fact]
    public void The_install_folder_is_never_a_leftover()
    {
        Assert.Contains(Installation.Folder, SafetyRules.ForThisMachine([]).OwnTrees);
    }

    [Theory]
    [InlineData("self-install", CliVerb.SelfInstall)]
    [InlineData("self-uninstall", CliVerb.SelfUninstall)]
    public void Installing_and_removing_are_commands(string command, CliVerb verb)
    {
        var options = CommandLineParser.Parse([command, "--quiet"]);

        Assert.Equal(verb, options.Verb);
        Assert.True(options.Quiet);
        Assert.Null(options.Error);
        Assert.NotNull(CommandLineParser.Parse([command, "--json"]).Error);
        Assert.NotNull(CommandLineParser.Parse([command, "--clean"]).Error);
    }
}
