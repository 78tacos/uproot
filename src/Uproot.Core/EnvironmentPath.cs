using System.Runtime.InteropServices;

namespace Uproot.Core;

/// <summary>
/// Reading and editing a PATH value one entry at a time. PATH belongs to everyone, so an entry is
/// taken out — or put back — without touching any other, their order, or the value's type.
/// </summary>
public static class EnvironmentPath
{
    /// <summary>The entries in a PATH value, trimmed, empty ones left out.</summary>
    public static IReadOnlyList<string> Split(string? value) =>
        (value ?? string.Empty)
        .Split(';')
        .Select(e => e.Trim())
        .Where(e => e.Length > 0)
        .ToList();

    /// <summary>
    /// True when two entries are the same text, give or take quotes, surrounding spaces, a trailing
    /// backslash and case. Never by expanding variables: "%JAVA_HOME%\bin" is a different entry from
    /// the folder it happens to mean today, kept on purpose by someone who unticked it — and in
    /// another account's PATH it would be expanded in the wrong account's environment anyway.
    /// </summary>
    public static bool SameEntry(string a, string b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string entry) => entry.Trim().Trim('"').Trim().TrimEnd('\\');

    /// <summary>
    /// The value with every occurrence of <paramref name="entry"/> taken out and everything else —
    /// other entries, their order, even stray empty ones — exactly as it was.
    /// </summary>
    public static string Without(string value, string entry, out int removed)
    {
        var parts = value.Split(';').ToList();
        removed = parts.RemoveAll(p => p.Trim().Length > 0 && SameEntry(p, entry));
        return string.Join(';', parts);
    }

    /// <summary>The value with <paramref name="entry"/> added at the end, unless it is already there.</summary>
    public static string With(string? value, string entry)
    {
        var current = value ?? string.Empty;
        if (Split(current).Any(e => SameEntry(e, entry))) return current;

        return current.Length == 0 || current.EndsWith(';') ? current + entry : current + ";" + entry;
    }

    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

    /// <summary>
    /// Tells running programs the environment changed, as the System Properties dialog does, so a
    /// console opened afterwards sees the new PATH without signing out.
    /// </summary>
    public static void Broadcast()
    {
        try
        {
            SendMessageTimeoutW(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment", SmtoAbortIfHung, 3000, out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
