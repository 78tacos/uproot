using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Uproot.Core;

namespace Uproot.App;

/// <summary>Opens Registry Editor at a given key, so a leftover can be looked at before it is removed.</summary>
internal static class RegistryEditor
{
    private const string AppletKey = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    /// <summary>
    /// Registry Editor opens wherever it was last closed, which it remembers in LastKey, so
    /// setting that first is the documented-by-convention way to point it somewhere. It only
    /// takes effect if Registry Editor is not already open.
    /// </summary>
    public static void OpenAt(RegistryLocation key)
    {
        var hive = key.Hive switch
        {
            RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
            RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
            RegistryHive.Users => "HKEY_USERS",
            _ => "HKEY_CLASSES_ROOT"
        };

        try
        {
            using (var applet = Registry.CurrentUser.CreateSubKey(AppletKey))
                applet.SetValue("LastKey", $@"Computer\{hive}\{key.SubKey}");

            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true, ArgumentList = { "-m" } });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Declining the administrator prompt lands here; nothing to report.
        }
    }
}
