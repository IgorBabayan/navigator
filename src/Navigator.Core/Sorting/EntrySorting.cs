using Navigator.Core.IO;

namespace Navigator.Core.Sorting;

public enum SortColumn
{
    Name,
    DateModified,
    DateCreated,
    Type,
    Size,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

public enum GroupBy
{
    None,
    Name,
    Type,
    DateModified,
    DateCreated,
    Size,
}

public sealed record SortOptions(SortColumn Column, SortDirection Direction, bool FoldersFirst)
{
    public static SortOptions Default { get; } = new(SortColumn.Name, SortDirection.Ascending, FoldersFirst: true);
}

/// <summary>Comparer implementing the column sort, folders-first ("mixed sorting" off) and name tie-breaks.</summary>
public sealed class FileEntryComparer : IComparer<FileEntry>
{
    private readonly SortOptions _options;

    public FileEntryComparer(SortOptions options) => _options = options;

    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        if (_options.FoldersFirst && x.IsDirectory != y.IsDirectory)
            return x.IsDirectory ? -1 : 1;

        var result = _options.Column switch
        {
            SortColumn.DateModified => x.LastWriteTimeUtc.CompareTo(y.LastWriteTimeUtc),
            SortColumn.DateCreated => x.CreationTimeUtc.CompareTo(y.CreationTimeUtc),
            SortColumn.Type => CompareType(x, y),
            SortColumn.Size => x.Length.CompareTo(y.Length),
            _ => 0,
        };

        if (result == 0)
            result = NaturalStringComparer.Instance.Compare(x.Name, y.Name);

        return _options.Direction == SortDirection.Descending ? -result : result;
    }

    private static int CompareType(FileEntry x, FileEntry y)
    {
        if (x.IsDirectory != y.IsDirectory)
            return x.IsDirectory ? -1 : 1;
        return string.CompareOrdinal(x.Extension, y.Extension);
    }
}

/// <summary>Static helpers to sort and to merge sorted batches incrementally.</summary>
public static class EntrySorter
{
    public static void Sort(List<FileEntry> entries, SortOptions options)
        => entries.Sort(new FileEntryComparer(options));

    /// <summary>Index at which <paramref name="entry"/> must be inserted to keep <paramref name="sorted"/> ordered.</summary>
    public static int FindInsertIndex(IReadOnlyList<FileEntry> sorted, FileEntry entry, IComparer<FileEntry> comparer)
    {
        var low = 0;
        var high = sorted.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var cmp = comparer.Compare(sorted[mid], entry);
            if (cmp <= 0)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return low;
    }
}

public readonly record struct GroupKey(int Order, string Id);

/// <summary>
/// Computes group buckets. Group ids are stable keys (e.g. "date.today") that the UI localizes.
/// </summary>
public static class EntryGrouper
{
    public static GroupKey GetGroup(FileEntry entry, GroupBy groupBy, DateTime nowLocal) => groupBy switch
    {
        GroupBy.Name => ByName(entry),
        GroupBy.Type => entry.IsDirectory ? new GroupKey(0, "type.folder") : new GroupKey(1, "type" + (entry.Extension.Length == 0 ? ".none" : entry.Extension)),
        GroupBy.DateModified => ByDate(entry.LastWriteTimeUtc.ToLocalTime(), nowLocal),
        GroupBy.DateCreated => ByDate(entry.CreationTimeUtc.ToLocalTime(), nowLocal),
        GroupBy.Size => BySize(entry),
        _ => new GroupKey(0, string.Empty),
    };

    private static GroupKey ByName(FileEntry entry)
    {
        var first = entry.Name.Length == 0 ? '#' : char.ToUpperInvariant(entry.Name[0]);
        if (char.IsAsciiDigit(first))
            return new GroupKey(0, "name.0-9");
        if (char.IsLetter(first))
            return new GroupKey(1 + first, "name." + first);
        return new GroupKey(int.MaxValue, "name.other");
    }

    internal static GroupKey ByDate(DateTime value, DateTime now)
    {
        var today = now.Date;
        var date = value.Date;
        if (date > today)
            return new GroupKey(0, "date.future");
        if (date == today)
            return new GroupKey(1, "date.today");
        if (date == today.AddDays(-1))
            return new GroupKey(2, "date.yesterday");

        var startOfWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (date >= startOfWeek)
            return new GroupKey(3, "date.earlierThisWeek");
        if (date >= startOfWeek.AddDays(-7))
            return new GroupKey(4, "date.lastWeek");

        var startOfMonth = new DateTime(today.Year, today.Month, 1, 0, 0, 0, today.Kind);
        if (date >= startOfMonth)
            return new GroupKey(5, "date.earlierThisMonth");
        if (date >= startOfMonth.AddMonths(-1))
            return new GroupKey(6, "date.lastMonth");
        if (date.Year == today.Year)
            return new GroupKey(7, "date.earlierThisYear");
        return new GroupKey(8, "date.longAgo");
    }

    private static GroupKey BySize(FileEntry entry)
    {
        if (entry.IsDirectory)
            return new GroupKey(0, "size.folder");

        return entry.Length switch
        {
            0 => new GroupKey(1, "size.empty"),
            < 16 * 1024 => new GroupKey(2, "size.tiny"),
            < 1024 * 1024 => new GroupKey(3, "size.small"),
            < 128L * 1024 * 1024 => new GroupKey(4, "size.medium"),
            < 1024L * 1024 * 1024 => new GroupKey(5, "size.large"),
            < 4L * 1024 * 1024 * 1024 => new GroupKey(6, "size.huge"),
            _ => new GroupKey(7, "size.gigantic"),
        };
    }
}

/// <summary>Folder filter (Ctrl+Shift+F in the reference app): substring or wildcard match on the name.</summary>
public sealed class NameFilter
{
    private readonly string _text;
    private readonly bool _isPattern;

    public NameFilter(string text)
    {
        _text = text.Trim();
        _isPattern = _text.AsSpan().IndexOfAny('*', '?') >= 0;
    }

    public bool IsEmpty => _text.Length == 0;

    public bool Matches(string name)
    {
        if (IsEmpty)
            return true;

        return _isPattern
            ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(_text, name, ignoreCase: true)
            : name.Contains(_text, StringComparison.OrdinalIgnoreCase);
    }
}
