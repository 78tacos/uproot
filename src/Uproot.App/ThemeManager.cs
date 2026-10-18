using System.Windows;
using Microsoft.Win32;

namespace Uproot.App;

/// <summary>
/// Follows the Windows app theme, light or dark, and switches the palette when it changes.
///
/// The palette dictionary always sits at index 0 of the application's merged dictionaries and
/// the style dictionary after it, so switching theme is a single replacement. Styles reference
/// colours through DynamicResource, which is what makes an open window repaint rather than keep
/// the palette it loaded with.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool _listeningForSystemChanges;

    /// <summary>Raised after the palette changes, so views can re-evaluate colour converters.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>The palette in use.</summary>
    public static bool IsDark { get; private set; } = true;

    public static void FollowSystem()
    {
        if (!_listeningForSystemChanges)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listeningForSystemChanges = true;
        }

        // Applied even when it matches the default, so startup always ends with a known-good
        // dictionary in slot 0.
        SetPalette(IsSystemUsingDarkTheme());
    }

    private static void SetPalette(bool dark)
    {
        if (Application.Current is not { } app) return;

        var source = new Uri(
            dark ? "Themes/Dark.xaml" : "Themes/Light.xaml",
            UriKind.Relative);

        var palette = new ResourceDictionary { Source = source };
        var merged = app.Resources.MergedDictionaries;

        if (merged.Count == 0) merged.Add(palette);
        else merged[0] = palette;

        IsDark = dark;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Reads the Windows app theme. Windows exposes this as AppsUseLightTheme, which is
    /// separate from SystemUsesLightTheme (the taskbar and Start menu) — an app should follow
    /// the former. A missing value means light, which is the Windows default.
    /// </summary>
    public static bool IsSystemUsingDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;

        var dark = IsSystemUsingDarkTheme();
        if (dark == IsDark) return;

        // The broadcast arrives on a system thread; resource changes must happen on the UI one.
        Application.Current?.Dispatcher.Invoke(() => SetPalette(dark));
    }

    public static void Detach()
    {
        if (!_listeningForSystemChanges) return;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listeningForSystemChanges = false;
    }
}
