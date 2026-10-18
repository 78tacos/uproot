using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Uproot.Core;

/// <summary>
/// One value from a .reg file. For a type .NET has no kind for, <see cref="RegistryValueKind.Unknown"/>
/// with the raw bytes in <see cref="Data"/> and the registry type number in <see cref="RawType"/>.
/// </summary>
public sealed record RegFileValue(string Name, RegistryValueKind Kind, object? Data)
{
    public int RawType { get; init; } = -1;
}

/// <summary>One key section of a .reg file.</summary>
public sealed record RegFileKey(RegistryLocation Location, bool Delete, IReadOnlyList<RegFileValue> Values);

/// <summary>
/// A .reg export as reg.exe writes it, read back for restoring.
///
/// A backup file sits in a folder that is not always trustworthy, so it is read once, into memory,
/// checked, and then written back from what was checked: reg.exe is never handed the path, which
/// it would read again, and nothing a different parser might see in the file can reach the
/// registry. Parsing is strict — the header on the first line, every key naming a hive this app
/// works with, nothing after a header's closing bracket or a string's closing quote — and anything
/// else marks the file invalid.
/// </summary>
public sealed class RegFile
{
    private const string Header = "Windows Registry Editor Version 5.00";

    /// <summary>REG_LINK: a registry symbolic link, which a genuine export of a leftover never holds.</summary>
    private const int RegLink = 6;

    private RegFile(IReadOnlyList<RegFileKey> keys, bool valid)
    {
        Keys = keys;
        IsValid = valid;
    }

    public IReadOnlyList<RegFileKey> Keys { get; }

    /// <summary>False when the header is missing, a key header names something unrecognisable, or a line could not be understood.</summary>
    public bool IsValid { get; }

    /// <summary>Reads the file's bytes once. reg.exe writes UTF-16 with a byte order mark.</summary>
    public static RegFile Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// True when every key the file would write is <paramref name="key"/> or beneath it, and it
    /// deletes nothing: what a genuine export of that key looks like, and nothing else.
    /// </summary>
    public bool OnlyTouches(RegistryLocation key) =>
        IsValid && Keys.Count > 0 && Keys.All(k => !k.Delete && k.Location.SubKey.Length > 0 && k.Location.IsWithin(key)
                                                   && k.Values.All(v => v.Data is not null));

    public RegFileValue? FindValue(RegistryLocation key, string? name) =>
        Keys.FirstOrDefault(k => k.Location.Equals(key))?.Values
            .FirstOrDefault(v => string.Equals(v.Name, name ?? string.Empty, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes back exactly what was parsed, and only if all of it lies within <paramref name="key"/>.
    /// Keys are created parent first, as the export lists them; values are set one by one.
    /// </summary>
    public void WriteWithin(RegistryLocation key)
    {
        if (!OnlyTouches(key)) throw new InvalidOperationException($"The backup holds more than {key.DisplayPath}.");

        foreach (var section in Keys)
        {
            using var root = RegistryKey.OpenBaseKey(section.Location.Hive, RegistryView.Registry64);
            using var open = root.CreateSubKey(section.Location.SubKey, writable: true)
                             ?? throw new UnauthorizedAccessException($"Could not create {section.Location.DisplayPath}.");

            foreach (var value in section.Values) Write(open, value);
        }
    }

    /// <summary>Sets one parsed value, including types .NET has no kind for.</summary>
    public static void Write(RegistryKey key, RegFileValue value)
    {
        if (value.Data is null) throw new InvalidOperationException($"No data for {value.Name}.");

        if (value.Kind != RegistryValueKind.Unknown)
        {
            key.SetValue(value.Name, value.Data, value.Kind);
            return;
        }

        var bytes = (byte[])value.Data;
        var result = RegSetValueExW(key.Handle, value.Name, 0, value.RawType, bytes, bytes.Length);
        if (result != 0) throw new System.ComponentModel.Win32Exception(result);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string? name, int reserved, int type, byte[] data, int size);

    public static RegFile Parse(string text)
    {
        var keys = new List<RegFileKey>();
        var lines = LogicalLines(text.TrimStart('﻿')).ToList();

        // The header, and nothing but blank lines before it.
        var first = lines.FindIndex(l => l.Trim().Length > 0);
        if (first < 0 || lines[first].Trim() != Header) return new RegFile(keys, false);

        var valid = true;
        RegistryLocation? current = null;
        var deleting = false;
        var values = new List<RegFileValue>();

        void Flush()
        {
            if (current is not null) keys.Add(new RegFileKey(current, deleting, values));
            values = [];
        }

        foreach (var line in lines.Skip(first + 1))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';')) continue;

            if (trimmed.StartsWith('['))
            {
                Flush();

                // The key is everything up to the closing bracket, which must end the line.
                if (!trimmed.EndsWith(']')) { valid = false; current = null; continue; }

                var inner = trimmed[1..^1];
                deleting = inner.StartsWith('-');
                if (deleting) inner = inner[1..];

                current = RegistryLocation.Parse(inner);
                if (current is null) valid = false;
                continue;
            }

            // A value outside any key is not something reg.exe writes.
            if (current is null) { valid = false; continue; }

            if (ParseValue(trimmed) is { } value) values.Add(value);
            else valid = false;
        }

        Flush();
        return new RegFile(keys, valid);
    }

    /// <summary>
    /// Lines, with reg.exe's continuations joined up. Any of CR LF, LF or a lone CR ends a line;
    /// only a hex value's data is ever wrapped, so only a line of one continues onto the next.
    /// </summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        var builder = new StringBuilder();

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = builder.Length > 0 ? raw.TrimStart() : raw;
            var trimmedEnd = line.TrimEnd();
            var isHexValue = builder.Length > 0 || IsHexValueLine(trimmedEnd);

            if (isHexValue && trimmedEnd.EndsWith(",\\", StringComparison.Ordinal))
            {
                builder.Append(trimmedEnd[..^1]);
                continue;
            }

            builder.Append(line);
            yield return builder.ToString();
            builder.Clear();
        }

        if (builder.Length > 0) yield return builder.ToString();
    }

    private static bool IsHexValueLine(string line)
    {
        var start = line.TrimStart();
        if (!(start.StartsWith('"') || start.StartsWith('@'))) return false;

        var equals = start.StartsWith('@') ? 1 : ReadQuoted(start, 0, out _) ?? -1;
        if (equals < 0) return false;

        var rest = start[equals..].TrimStart();
        return rest.StartsWith("=hex", StringComparison.OrdinalIgnoreCase);
    }

    private static RegFileValue? ParseValue(string line)
    {
        string name;
        int rest;

        if (line.StartsWith('@'))
        {
            name = string.Empty;
            rest = 1;
        }
        else if (line.StartsWith('"'))
        {
            if (ReadQuoted(line, 0, out name) is not { } end) return null;
            rest = end;
        }
        else
        {
            return null;
        }

        rest = SkipSpaces(line, rest);
        if (rest >= line.Length || line[rest] != '=') return null;
        var data = line[(rest + 1)..].Trim();

        // A deletion: never in an export, and refused by OnlyTouches.
        if (data == "-") return new RegFileValue(name, RegistryValueKind.Unknown, null);

        if (data.StartsWith('"'))
        {
            if (ReadQuoted(data, 0, out var text) is not { } end || data[end..].Trim().Length > 0) return null;
            return new RegFileValue(name, RegistryValueKind.String, text);
        }

        if (data.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            var digits = data[6..];
            return digits.Length == 8 && uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)
                ? new RegFileValue(name, RegistryValueKind.DWord, unchecked((int)number))
                : null;
        }

        if (!data.StartsWith("hex", StringComparison.OrdinalIgnoreCase)) return null;

        var colon = data.IndexOf(':');
        if (colon < 0) return null;

        var type = 3;
        var prefix = data[..colon];
        if (prefix.Length > 3)
        {
            if (!prefix.StartsWith("hex(", StringComparison.OrdinalIgnoreCase) || !prefix.EndsWith(')')) return null;
            if (!int.TryParse(prefix[4..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out type)) return null;
        }
        else if (!prefix.Equals("hex", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (type == RegLink || type < 0) return null;
        if (Bytes(data[(colon + 1)..]) is not { } bytes) return null;

        return type switch
        {
            0 => new RegFileValue(name, RegistryValueKind.None, bytes),
            2 => new RegFileValue(name, RegistryValueKind.ExpandString, Utf16(bytes).TrimEnd('\0')),
            3 => new RegFileValue(name, RegistryValueKind.Binary, bytes),
            4 when bytes.Length == 4 => new RegFileValue(name, RegistryValueKind.DWord, BitConverter.ToInt32(bytes)),
            7 => new RegFileValue(name, RegistryValueKind.MultiString, MultiString(bytes)),
            0xb when bytes.Length == 8 => new RegFileValue(name, RegistryValueKind.QWord, BitConverter.ToInt64(bytes)),

            // A type .NET has no kind for — a resource list, say — kept as its bytes and written
            // back through the registry API with its own type number.
            _ => new RegFileValue(name, RegistryValueKind.Unknown, bytes) { RawType = type }
        };
    }

    private static int SkipSpaces(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    /// <summary>Reads a quoted string with .reg escapes; returns the index after the closing quote.</summary>
    private static int? ReadQuoted(string text, int start, out string value)
    {
        var builder = new StringBuilder();

        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                builder.Append(text[++i]);
                continue;
            }

            if (c == '"')
            {
                value = builder.ToString();
                return i + 1;
            }

            builder.Append(c);
        }

        value = string.Empty;
        return null;
    }

    private static byte[]? Bytes(string hex)
    {
        var parts = hex.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return null;
        }

        return bytes;
    }

    private static string Utf16(byte[] bytes) => Encoding.Unicode.GetString(bytes, 0, bytes.Length - bytes.Length % 2);

    private static string[] MultiString(byte[] bytes)
    {
        var text = Utf16(bytes);
        var parts = text.Split('\0').ToList();

        // Terminated by a double null: drop the empty strings it leaves at the end.
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return parts.ToArray();
    }
}
