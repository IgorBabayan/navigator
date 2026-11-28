using System.Text;

namespace Navigator.Core.Customization;

/// <summary>
/// Minimal in-place editing of INI-style text (Windows desktop.ini, KDE .directory): one key is read, set or removed
/// while every other line — comments, unknown sections, ordering — is left exactly as it was.
/// </summary>
public static class IniText
{
    public static string? GetValue(string text, string section, string key)
    {
        ArgumentNullException.ThrowIfNull(text);
        var inSection = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (IsSection(trimmed, out var name))
            {
                inSection = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection && TryKey(trimmed, out var lineKey, out var value) && string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    /// <summary>Sets (or, with a null value, removes) a key; the section is created at the end when missing.</summary>
    public static string SetValue(string text, string section, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(text);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) || text.Length == 0 ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var sectionStart = -1;
        var sectionEnd = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsSection(lines[i].Trim(), out var name))
                continue;
            if (sectionStart >= 0)
            {
                sectionEnd = i;
                break;
            }

            if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                sectionStart = i;
        }

        if (sectionStart < 0)
        {
            if (value is null)
                return text;
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add(string.Empty);
            lines.Add("[" + section + "]");
            lines.Add(key + "=" + value);
            return Join(lines, newLine);
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (!TryKey(lines[i].Trim(), out var lineKey, out _) || !string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (value is null)
                lines.RemoveAt(i);
            else
                lines[i] = key + "=" + value;
            return Join(lines, newLine);
        }

        if (value is null)
            return text;

        // After the section's last key (before trailing blank lines).
        var insert = sectionEnd;
        while (insert > sectionStart + 1 && lines[insert - 1].Trim().Length == 0)
            insert--;
        lines.Insert(insert, key + "=" + value);
        return Join(lines, newLine);
    }

    /// <summary>True when the section has no keys left (or does not exist).</summary>
    public static bool IsSectionEmpty(string text, string section)
    {
        ArgumentNullException.ThrowIfNull(text);
        var inSection = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (IsSection(trimmed, out var name))
                inSection = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
            else if (inSection && TryKey(trimmed, out _, out _))
                return false;
        }

        return true;
    }

    /// <summary>True when no line other than section headers, blanks and comments is left.</summary>
    public static bool HasNoKeys(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split('\n').Select(l => l.Trim()).All(l => l.Length == 0 || l[0] is ';' or '#' || IsSection(l, out _));
    }

    private static bool IsSection(string line, out string name)
    {
        if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
        {
            name = line[1..^1].Trim();
            return true;
        }

        name = string.Empty;
        return false;
    }

    private static bool TryKey(string line, out string key, out string value)
    {
        var equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || line[0] is ';' or '#')
        {
            key = value = string.Empty;
            return false;
        }

        key = line[..equals].Trim();
        value = line[(equals + 1)..].Trim();
        return true;
    }

    private static string Join(List<string> lines, string newLine)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
            builder.Append(line).Append(newLine);
        return builder.ToString();
    }
}
