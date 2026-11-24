using System.Text;

namespace Navigator.Core.IO;

/// <summary>
/// Platform-aware path helpers. All comparison rules live here so that case sensitivity and
/// Unicode normalization are handled identically everywhere in the application.
/// </summary>
public static class PathUtil
{
    private static readonly string[] WindowsReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Default comparison for paths on the current OS. Windows (NTFS) and macOS (APFS default)
    /// are case-insensitive; Linux file systems are case-sensitive.
    /// </summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer Comparer { get; } =
        Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Returns the full path without a trailing separator (roots keep theirs: "C:\", "/").</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        return TrimTrailingSeparator(full);
    }

    public static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1)
            return path;

        var root = Path.GetPathRoot(path);
        if (root is not null && root.Length == path.Length)
            return path;

        var end = path.Length;
        while (end > 1 && IsSeparator(path[end - 1]))
            end--;

        return end == path.Length ? path : path[..end];
    }

    public static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    /// <summary>Compares two normalized paths using the platform rules (NFC-normalized on macOS).</summary>
    public static bool AreEqual(string? a, string? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;

        a = TrimTrailingSeparator(a);
        b = TrimTrailingSeparator(b);
        if (string.Equals(a, b, Comparison))
            return true;

        // macOS stores names in NFD on HFS+ and preserves the given form on APFS:
        // equal-looking names may differ only in normalization form.
        return OperatingSystem.IsMacOS()
               && string.Equals(a.Normalize(NormalizationForm.FormC), b.Normalize(NormalizationForm.FormC), Comparison);
    }

    /// <summary>True when <paramref name="candidate"/> equals <paramref name="parent"/> or lies below it.</summary>
    public static bool IsSameOrDescendant(string parent, string candidate)
    {
        parent = TrimTrailingSeparator(parent);
        candidate = TrimTrailingSeparator(candidate);

        if (AreEqual(parent, candidate))
            return true;

        if (!candidate.StartsWith(parent, Comparison))
            return false;

        // "/home/user" must not match "/home/username"
        return IsSeparator(parent[^1]) || (candidate.Length > parent.Length && IsSeparator(candidate[parent.Length]));
    }

    /// <summary>Splits a path into breadcrumb segments. Each segment carries the full path it navigates to.</summary>
    public static IReadOnlyList<PathSegment> GetSegments(string path)
    {
        path = TrimTrailingSeparator(path);
        var root = Path.GetPathRoot(path);
        var segments = new List<PathSegment>(8);
        int start;

        if (string.IsNullOrEmpty(root))
        {
            start = 0;
        }
        else
        {
            var rootPath = root;
            var rootName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (rootName.Length == 0)
                rootName = root;
            segments.Add(new PathSegment(rootName, rootPath));
            start = root.Length;
        }

        var current = new StringBuilder(root ?? string.Empty);
        var span = path.AsSpan(start);
        while (!span.IsEmpty)
        {
            var next = span.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = next < 0 ? span : span[..next];
            if (!name.IsEmpty)
            {
                if (current.Length > 0 && !IsSeparator(current[^1]))
                    current.Append(Path.DirectorySeparatorChar);
                current.Append(name);
                segments.Add(new PathSegment(name.ToString(), current.ToString()));
            }

            if (next < 0)
                break;
            span = span[(next + 1)..];
        }

        return segments;
    }

    /// <summary>Validates a single file or folder name for the current platform.</summary>
    public static NameValidationResult ValidateName(string? name) => ValidateName(name, IsWindows);

    internal static NameValidationResult ValidateName(string? name, bool windowsRules)
    {
        if (string.IsNullOrWhiteSpace(name))
            return NameValidationResult.Empty;

        if (name is "." or "..")
            return NameValidationResult.Reserved;

        if (Encoding.UTF8.GetByteCount(name) > 255)
            return NameValidationResult.TooLong;

        foreach (var c in name)
        {
            if (c == '/' || c == '\0')
                return NameValidationResult.InvalidCharacters;

            if (windowsRules && (c < 32 || c is '<' or '>' or ':' or '"' or '\\' or '|' or '?' or '*'))
                return NameValidationResult.InvalidCharacters;
        }

        if (windowsRules)
        {
            if (name.EndsWith('.') || name.EndsWith(' '))
                return NameValidationResult.InvalidEnding;

            var stem = name;
            var dot = stem.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
                stem = stem[..dot];
            stem = stem.TrimEnd();
            foreach (var reserved in WindowsReservedNames)
            {
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                    return NameValidationResult.Reserved;
            }
        }

        return NameValidationResult.Valid;
    }

    /// <summary>
    /// Produces "name (2).ext", "name (3).ext", ... until <paramref name="exists"/> returns false.
    /// Folders and dot-files keep their full name as the stem.
    /// </summary>
    public static string GetUniqueName(string directory, string name, bool isDirectory, Func<string, bool> exists)
    {
        if (!exists(Path.Combine(directory, name)))
            return name;

        var (stem, extension) = SplitName(name, isDirectory);
        var baseStem = StripCounter(stem);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{baseStem} ({i}){extension}";
            if (!exists(Path.Combine(directory, candidate)))
                return candidate;
        }

        return $"{baseStem} ({Guid.NewGuid():N}){extension}";
    }

    /// <summary>Name for "Duplicate" / copying into the same folder: "report - Copy.txt", "report - Copy (2).txt".</summary>
    public static string GetCopyName(string directory, string name, bool isDirectory, string copySuffix, Func<string, bool> exists)
    {
        var (stem, extension) = SplitName(name, isDirectory);
        var first = $"{stem} - {copySuffix}{extension}";
        return GetUniqueName(directory, first, isDirectory, exists);
    }

    public static (string Stem, string Extension) SplitName(string name, bool isDirectory)
    {
        if (isDirectory)
            return (name, string.Empty);

        var dot = name.LastIndexOf('.');
        return dot <= 0 ? (name, string.Empty) : (name[..dot], name[dot..]);
    }

    private static string StripCounter(string stem)
    {
        // "photo (3)" -> "photo" so that repeated "keep both" does not produce "photo (3) (2)"
        if (!stem.EndsWith(')'))
            return stem;

        var open = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0)
            return stem;

        var digits = stem.AsSpan(open + 2, stem.Length - open - 3);
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
                return stem;
        }

        return digits.IsEmpty ? stem : stem[..open];
    }

    /// <summary>
    /// Expands user-typed address bar input: "~", "~/x", %VAR% on Windows and $VAR on Unix.
    /// Returns null if the input is not a plausible local path.
    /// </summary>
    public static string? ExpandUserInput(string input, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var text = input.Trim().Trim('"');
        if (text == "~")
            return homeDirectory;
        if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
            text = Path.Combine(homeDirectory, text[2..]);

        text = Environment.ExpandEnvironmentVariables(text);
        if (!IsWindows && text.Contains('$', StringComparison.Ordinal))
            text = ExpandUnixVariables(text);

        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile)
            text = uri.LocalPath;

        if (!Path.IsPathFullyQualified(text))
            return null;

        try
        {
            return Normalize(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string ExpandUnixVariables(string text)
    {
        var result = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '$')
            {
                result.Append(text[i]);
                continue;
            }

            var braced = i + 1 < text.Length && text[i + 1] == '{';
            var start = braced ? i + 2 : i + 1;
            var end = start;
            while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
                end++;

            if (end == start || (braced && (end >= text.Length || text[end] != '}')))
            {
                result.Append(text[i]);
                continue;
            }

            var value = Environment.GetEnvironmentVariable(text[start..end]);
            result.Append(value ?? text[i..(braced ? end + 1 : end)]);
            i = braced ? end : end - 1;
        }

        return result.ToString();
    }
}

public readonly record struct PathSegment(string Name, string FullPath);

public enum NameValidationResult
{
    Valid,
    Empty,
    InvalidCharacters,
    InvalidEnding,
    Reserved,
    TooLong,
}
