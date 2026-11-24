using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Navigator.Core.Versioning;

/// <summary>
/// Semantic version (https://semver.org 2.0.0) with correct precedence rules:
/// 1.0.0-alpha &lt; 1.0.0-alpha.1 &lt; 1.0.0-beta.2 &lt; 1.0.0-beta.11 &lt; 1.0.0-rc.1 &lt; 1.0.0.
/// Build metadata is kept but ignored for precedence. A leading "v" is accepted ("v1.2.3" Git tags).
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private SemVersion(int major, int minor, int patch, string prerelease, string build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Dot-separated prerelease identifiers without the leading "-" ("" for releases).</summary>
    public string Prerelease { get; }

    public string Build { get; }

    public bool IsPrerelease => Prerelease.Length > 0;

    public static SemVersion Create(int major, int minor, int patch, string? prerelease = null, string? build = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        return new SemVersion(major, minor, patch, prerelease ?? string.Empty, build ?? string.Empty);
    }

    public static SemVersion Parse(string text)
        => TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a semantic version.");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
            span = span[1..];

        var build = string.Empty;
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            build = span[(plus + 1)..].ToString();
            span = span[..plus];
            if (!AreValidIdentifiers(build, numericLeadingZerosAllowed: true))
                return false;
        }

        var prerelease = string.Empty;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..].ToString();
            span = span[..dash];
            if (!AreValidIdentifiers(prerelease, numericLeadingZerosAllowed: false))
                return false;
        }

        Span<Range> parts = stackalloc Range[4];
        if (span.Split(parts, '.') != 3)
            return false;

        if (!TryParseNumber(span[parts[0]], out var major)
            || !TryParseNumber(span[parts[1]], out var minor)
            || !TryParseNumber(span[parts[2]], out var patch))
            return false;

        version = new SemVersion(major, minor, patch, prerelease, build);
        return true;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.IsEmpty || (text.Length > 1 && text[0] == '0'))
            return false;
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool AreValidIdentifiers(string text, bool numericLeadingZerosAllowed)
    {
        if (text.Length == 0)
            return false;

        foreach (var identifier in text.Split('.'))
        {
            if (identifier.Length == 0)
                return false;

            var numeric = true;
            foreach (var c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    return false;
                numeric &= char.IsAsciiDigit(c);
            }

            if (numeric && !numericLeadingZerosAllowed && identifier.Length > 1 && identifier[0] == '0')
                return false;
        }

        return true;
    }

    public SemVersion WithoutMetadata() => Build.Length == 0 ? this : new SemVersion(Major, Minor, Patch, Prerelease, string.Empty);

    public SemVersion NextPatch() => new(Major, Minor, Patch + 1, string.Empty, string.Empty);

    public SemVersion NextMinor() => new(Major, Minor + 1, 0, string.Empty, string.Empty);

    public SemVersion NextMajor() => new(Major + 1, 0, 0, string.Empty, string.Empty);

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
            return 1;

        var cmp = Major.CompareTo(other.Major);
        if (cmp == 0)
            cmp = Minor.CompareTo(other.Minor);
        if (cmp == 0)
            cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0)
            return cmp;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return Math.Sign(right.Length.CompareTo(left.Length)); // a release outranks any prerelease

        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            int cmp;
            if (aNumeric && bNumeric)
                cmp = an.CompareTo(bn);
            else if (aNumeric)
                cmp = -1;
            else if (bNumeric)
                cmp = 1;
            else
                cmp = string.CompareOrdinal(a[i], b[i]);

            if (cmp != 0)
                return cmp < 0 ? -1 : 1;
        }

        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString()
    {
        var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (Prerelease.Length > 0)
            core += "-" + Prerelease;
        if (Build.Length > 0)
            core += "+" + Build;
        return core;
    }

    public static bool operator ==(SemVersion? left, SemVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemVersion? left, SemVersion? right) => !(left == right);

    public static bool operator <(SemVersion? left, SemVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(SemVersion? left, SemVersion? right) => left is not null && left.CompareTo(right) > 0;

    public static bool operator <=(SemVersion? left, SemVersion? right) => !(left > right);

    public static bool operator >=(SemVersion? left, SemVersion? right) => !(left < right);
}
