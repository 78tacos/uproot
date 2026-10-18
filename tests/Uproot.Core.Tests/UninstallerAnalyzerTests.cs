using System.Text;
using Uproot.Core.Cli;
using Uproot.Core;
using Xunit;

namespace Uproot.Core.Tests;

/// <summary>Recognising the installer behind an uninstaller, and reading what it recorded.</summary>
public sealed class UninstallerAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uproot-tests", Guid.NewGuid().ToString("N"));

    public UninstallerAnalyzerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static InstalledProgram Program(string keyName, string? uninstall = null, bool msi = false,
        params (string Name, string Value)[] values) => new()
    {
        Key = RegistryLocation.Machine($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}"),
        DisplayName = "Contoso Widget",
        UninstallString = uninstall,
        IsWindowsInstaller = msi,
        Scope = ProgramScope.Machine,
        Values = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase)
    };

    [Fact]
    public void Installer_frameworks_are_recognised_from_the_entry()
    {
        Assert.Equal(InstallerTechnology.WindowsInstaller,
            UninstallerAnalyzer.QuickDetect(Program("{11111111-2222-3333-4444-555555555555}", "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", msi: true)));

        Assert.Equal(InstallerTechnology.InnoSetup,
            UninstallerAnalyzer.QuickDetect(Program("Contoso_is1", @"""C:\Contoso\unins000.exe""")));

        Assert.Equal(InstallerTechnology.WixBurn,
            UninstallerAnalyzer.QuickDetect(Program("{bundle}", @"""C:\ProgramData\Package Cache\{x}\setup.exe"" /uninstall", values: ("BundleUpgradeCode", "{y}"))));

        Assert.Equal(InstallerTechnology.Squirrel,
            UninstallerAnalyzer.QuickDetect(Program("Contoso", @"""C:\Users\a\AppData\Local\Contoso\Update.exe"" --uninstall")));

        Assert.Equal(InstallerTechnology.ChromiumSetup,
            UninstallerAnalyzer.QuickDetect(Program("Contoso", @"""C:\Program Files\Contoso\Application\1.2.3\Installer\setup.exe"" --uninstall --system-level")));

        Assert.Equal(InstallerTechnology.Unknown,
            UninstallerAnalyzer.QuickDetect(Program("Contoso", @"""C:\Contoso\remove.exe""")));
    }

    [Fact]
    public void An_msi_product_code_comes_from_the_key_or_the_command()
    {
        Assert.Equal("{11111111-2222-3333-4444-555555555555}",
            Program("{11111111-2222-3333-4444-555555555555}", msi: true).ProductCode);

        Assert.Equal("{AAAAAAAA-2222-3333-4444-555555555555}",
            Program("Contoso", "MsiExec.exe /x{aaaaaaaa-2222-3333-4444-555555555555}").ProductCode);

        Assert.Null(Program("Contoso", @"C:\Contoso\uninstall.exe").ProductCode);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Mozilla Firefox\uninstall\helper.exe", @"C:\Program Files\Mozilla Firefox")]
    [InlineData(@"C:\Program Files\Brave\Brave-Browser\Application\1.2.3\Installer\setup.exe", @"C:\Program Files\Brave\Brave-Browser")]
    [InlineData(@"C:\Users\a\AppData\Local\Discord\app-1.0.9003\Discord.exe", @"C:\Users\a\AppData\Local\Discord")]
    [InlineData(@"C:\Program Files\Notepad++\uninstall.exe", @"C:\Program Files\Notepad++")]
    public void The_program_folder_is_found_above_uninstaller_subfolders(string executable, string expected)
    {
        Assert.Equal(expected, UninstallerAnalyzer.ProgramFolderFromExecutable(executable), ignoreCase: true);
    }

    [Theory]
    [InlineData("Nullsoft.NSIS.exehead", InstallerTechnology.Nsis)]
    [InlineData("Inno Setup Setup Data", InstallerTechnology.InnoSetup)]
    [InlineData(".wixburn", InstallerTechnology.WixBurn)]
    [InlineData("nothing recognisable", InstallerTechnology.Unknown)]
    public void Uninstaller_binaries_are_recognised_by_their_embedded_signatures(string marker, InstallerTechnology expected)
    {
        var path = Path.Combine(_root, "uninstall.exe");

        // Far enough in that it lands in the second read, with the overlap carrying it across.
        var bytes = new byte[(1024 * 1024) - 4];
        Random.Shared.NextBytes(bytes);
        for (var i = 0; i < bytes.Length; i++) if (bytes[i] is (byte)'N' or (byte)'I' or (byte)'.') bytes[i] = 0;

        File.WriteAllBytes(path, [.. bytes, .. Encoding.ASCII.GetBytes(marker), .. new byte[64]]);

        Assert.Equal(expected, UninstallerAnalyzer.DetectFromBinary(path));
    }

    // ---- Inno Setup log ----------------------------------------------------

    private static byte[] InnoLog(string id, string appId, string appName, params string[] strings)
    {
        var header = new byte[448];
        Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
        Encoding.ASCII.GetBytes(appId).CopyTo(header, 64);
        Encoding.ASCII.GetBytes(appName).CopyTo(header, 192);

        var body = new List<byte>();
        foreach (var value in strings)
        {
            body.AddRange(BitConverter.GetBytes(value.Length * 2));
            body.AddRange(Encoding.Unicode.GetBytes(value));
            body.AddRange(new byte[] { 0xFE, 0xFF, 0x01, 0x00 });
        }

        return [.. header, .. body];
    }

    [Fact]
    public void An_inno_log_yields_its_app_and_the_paths_and_keys_it_recorded()
    {
        var bytes = InnoLog("Inno Setup Uninstall Log (b) 64-bit", "{771FD6B0-FA20-440A-A002-3B3BAC16DC50}", "Visual Studio Code",
            @"C:\Users\a\AppData\Local\Programs\Microsoft VS Code\Code.exe",
            @"C:\Users\a\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Visual Studio Code\Visual Studio Code.lnk",
            @"C:\Users\a\AppData\Local\Programs\Microsoft VS Code\new_*",
            @"Software\Classes\VSCode.ascx",
            @"Software\Classes\directory\shell\VSCode\command");

        var log = InnoSetupLog.Parse(bytes);

        Assert.NotNull(log);
        Assert.Equal("{771FD6B0-FA20-440A-A002-3B3BAC16DC50}", log.AppId);
        Assert.Equal("Visual Studio Code", log.AppName);
        Assert.True(log.Is64Bit);
        Assert.Contains(@"C:\Users\a\AppData\Local\Programs\Microsoft VS Code\Code.exe", log.Files);
        Assert.Contains(log.Files, f => f.EndsWith("Visual Studio Code.lnk", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Files, f => f.Contains('*'));   // a delete pattern, not an installed file
        Assert.Contains(@"Software\Classes\VSCode.ascx", log.RegistryKeys);
        Assert.Contains(@"Software\Classes\directory\shell\VSCode\command", log.RegistryKeys);
    }

    [Fact]
    public void Something_that_is_not_an_inno_log_parses_to_nothing()
    {
        Assert.Null(InnoSetupLog.Parse(new byte[1024]));
        Assert.Null(InnoSetupLog.Parse(InnoLog("Nullsoft Install System", "x", "y")));
    }

    // ---- Shortcuts ---------------------------------------------------------

    /// <summary>A minimal .lnk: header, then LinkInfo with a local base path.</summary>
    internal static byte[] Shortcut(string target)
    {
        var header = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(header, 0);
        BitConverter.GetBytes(0x2).CopyTo(header, 0x14);   // HasLinkInfo

        var path = Encoding.Latin1.GetBytes(target + "\0");
        var volume = new byte[0x11];
        BitConverter.GetBytes(0x11).CopyTo(volume, 0);

        const int infoHeader = 0x1C;
        var volumeOffset = infoHeader;
        var pathOffset = volumeOffset + volume.Length;
        var suffixOffset = pathOffset + path.Length;
        var infoSize = suffixOffset + 1;

        var info = new byte[infoSize];
        BitConverter.GetBytes(infoSize).CopyTo(info, 0);
        BitConverter.GetBytes(infoHeader).CopyTo(info, 4);
        BitConverter.GetBytes(1).CopyTo(info, 8);          // VolumeIDAndLocalBasePath
        BitConverter.GetBytes(volumeOffset).CopyTo(info, 0x0C);
        BitConverter.GetBytes(pathOffset).CopyTo(info, 0x10);
        BitConverter.GetBytes(suffixOffset).CopyTo(info, 0x18);
        volume.CopyTo(info, volumeOffset);
        path.CopyTo(info, pathOffset);

        return [.. header, .. info, 0, 0, 0, 0];
    }

    [Fact]
    public void A_shortcuts_target_is_read_without_windows_script_host()
    {
        Assert.Equal(@"C:\Program Files\Contoso\Widget.exe", ShortcutFile.Parse(Shortcut(@"C:\Program Files\Contoso\Widget.exe")));
    }

    [Fact]
    public void Something_that_is_not_a_shortcut_has_no_target()
    {
        Assert.Null(ShortcutFile.Parse(Encoding.ASCII.GetBytes("not a shortcut at all, just text padding it out beyond the header length")));
        Assert.Null(ShortcutFile.Parse([]));
    }
}
