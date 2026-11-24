using System.Globalization;
using System.Text.RegularExpressions;
using Navigator.Plugins;

namespace Navigator.Plugin.Ftp;

/// <summary>
/// Parses directory listings: machine-readable MLSD/MLST facts (RFC 3659) and, for older servers, the two LIST
/// formats seen in practice (Unix "ls -l" and Windows/IIS). Unrecognized lines are skipped, never guessed.
/// </summary>
internal static partial class FtpListParser
{
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>"type=file;size=1024;modify=20240131120000; name". Returns null for "." / ".." (cdir/pdir) and junk.</summary>
    public static RemoteEntry? ParseMlsd(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0 || space == line.Length - 1)
            return null;
        var name = line[(space + 1)..];
        string? type = null;
        long size = 0;
        var modified = DateTime.MinValue;
        foreach (var fact in line[..space].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = fact.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue;
            var key = fact[..equals];
            var value = fact[(equals + 1)..];
            if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                type = value.ToLowerInvariant();
            else if (key.Equals("size", StringComparison.OrdinalIgnoreCase) || key.Equals("sizd", StringComparison.OrdinalIgnoreCase))
                _ = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out size);
            else if (key.Equals("modify", StringComparison.OrdinalIgnoreCase))
                modified = ParseTimeVal(value);
        }

        if (type is null or "cdir" or "pdir" || name is "." or "..")
            return null;
        var isLink = type.StartsWith("os.unix=slink", StringComparison.Ordinal) || type.StartsWith("os.unix=symlink", StringComparison.Ordinal);
        var isDirectory = type == "dir";
        return new RemoteEntry(name, isDirectory, isDirectory ? 0 : Math.Max(0, size), modified, isLink);
    }

    /// <summary>LIST lines that carry no entry: "total 12", "." and ".." (also when listed with "-a").</summary>
    public static bool IsIgnorable(string line)
    {
        if (line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            return true;
        var match = UnixPattern().Match(line);
        return match.Success && match.Groups["name"].Value is "." or "..";
    }

    /// <summary>One LIST line in Unix or Windows format, or null.</summary>
    public static RemoteEntry? ParseList(string line, DateTime nowUtc)
    {
        if (line.Length == 0 || line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            return null;
        return ParseUnix(line, nowUtc) ?? ParseWindows(line);
    }

    // drwxr-xr-x   2 user  group      4096 Jan 31 12:00 name with spaces
    // -rw-r--r--   1 user  group  12345678 Jan 31  2023 file.txt
    // lrwxrwxrwx   1 user  group         7 Jan 31 12:00 link -> target
    [GeneratedRegex(@"^(?<type>[-dlbcps])[rwxsStTl-]{9}[+.@]?\s+\d+\s+(?:\S+\s+){1,2}?(?<size>\d+)\s+(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<timeOrYear>\d{1,2}:\d{2}|\d{4})\s(?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPattern();

    // 01-31-24  12:00PM       <DIR>          folder
    // 2024-01-31  12:00            1234 file.txt
    [GeneratedRegex(@"^(?<date>\d{2,4}-\d{2}-\d{2,4})\s+(?<time>\d{1,2}:\d{2}(?:[AaPp][Mm])?)\s+(?<size><DIR>|\d+)\s+(?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPattern();

    private static RemoteEntry? ParseUnix(string line, DateTime nowUtc)
    {
        var match = UnixPattern().Match(line);
        if (!match.Success)
            return null;
        var kind = match.Groups["type"].Value[0];
        var name = match.Groups["name"].Value;
        var isLink = kind == 'l';
        if (isLink && name.IndexOf(" -> ", StringComparison.Ordinal) is var arrow and > 0)
            name = name[..arrow];
        if (name is "." or ".." || name.Length == 0)
            return null;

        var month = Array.IndexOf(Months, match.Groups["month"].Value.ToLowerInvariant()) + 1;
        _ = int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day);
        var timeOrYear = match.Groups["timeOrYear"].Value;
        var modified = DateTime.MinValue;
        if (month > 0 && day is >= 1 and <= 31)
        {
            if (timeOrYear.Contains(':', StringComparison.Ordinal))
            {
                // "Jan 31 12:00": within the last six months; the year is implied (and the zone is the server's).
                var parts = timeOrYear.Split(':');
                var year = nowUtc.Year;
                if (TryDate(year, month, day, int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), out var candidate))
                    modified = candidate > nowUtc.AddDays(2) && TryDate(year - 1, month, day, candidate.Hour, candidate.Minute, out var previous) ? previous : candidate;
            }
            else if (int.TryParse(timeOrYear, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            {
                _ = TryDate(year, month, day, 0, 0, out modified);
            }
        }

        var isDirectory = kind == 'd';
        _ = long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size);
        return new RemoteEntry(name, isDirectory, isDirectory ? 0 : size, modified, isLink);
    }

    private static RemoteEntry? ParseWindows(string line)
    {
        var match = WindowsPattern().Match(line);
        if (!match.Success)
            return null;
        var name = match.Groups["name"].Value;
        if (name is "." or "..")
            return null;
        var isDirectory = match.Groups["size"].Value == "<DIR>";
        long size = 0;
        if (!isDirectory)
            _ = long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out size);
        var text = match.Groups["date"].Value + " " + match.Groups["time"].Value;
        string[] formats = ["MM-dd-yy hh:mmtt", "MM-dd-yy h:mmtt", "MM-dd-yyyy hh:mmtt", "MM-dd-yyyy h:mmtt", "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "MM-dd-yy HH:mm", "dd-MM-yyyy HH:mm"];
        var modified = DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTime.MinValue;
        return new RemoteEntry(name, isDirectory, size, modified);
    }

    /// <summary>"YYYYMMDDHHMMSS[.sss]" in UTC (RFC 3659 time-val).</summary>
    public static DateTime ParseTimeVal(string value)
    {
        var text = value.Length > 14 ? value[..14] : value;
        return DateTime.TryParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result)
            ? result
            : DateTime.MinValue;
    }

    private static bool TryDate(int year, int month, int day, int hour, int minute, out DateTime value)
    {
        value = DateTime.MinValue;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;
        value = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
        return true;
    }
}
