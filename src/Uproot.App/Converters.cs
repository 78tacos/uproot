using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Uproot.App.ViewModels;

namespace Uproot.App;

/// <summary>Maps a label's tone onto the chip background brush from Theme.xaml.</summary>
public sealed class ToneToBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Lookup(value as LabelTone? ?? LabelTone.Neutral, "Bg");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static Brush Lookup(LabelTone tone, string suffix)
    {
        var key = $"Tone{tone}{suffix}";
        return Application.Current?.TryFindResource(key) as Brush
               ?? (Application.Current?.TryFindResource($"ToneNeutral{suffix}") as Brush)
               ?? Brushes.Gray;
    }
}

/// <summary>Maps a label's tone onto the chip foreground brush from Theme.xaml.</summary>
public sealed class ToneToForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ToneToBackgroundConverter.Lookup(value as LabelTone? ?? LabelTone.Neutral, "Fg");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses an element when the bound value is absent: null, empty/whitespace string,
/// false, zero, or an empty collection. Pass "Invert" as the parameter to reverse it.
/// </summary>
public sealed class PresenceToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            bool b => b,
            int i => i != 0,
            double d => Math.Abs(d) > double.Epsilon,
            ICollection c => c.Count > 0,
            IEnumerable e => e.Cast<object>().Any(),
            _ => true
        };

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            present = !present;

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Whether a group in the leftover list starts expanded. A forced removal of something like Acrobat
/// can list hundreds of COM registrations, and those should not bury the handful of folders.
/// </summary>
public sealed class SmallGroupConverter : IValueConverter
{
    public int Threshold { get; set; } = 60;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count <= Threshold;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
