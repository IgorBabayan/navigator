using Navigator.Core.IO;

namespace Navigator.Core.Navigation;

public enum LocationKind
{
    /// <summary>A folder on a local, removable or network file system.</summary>
    Folder,

    /// <summary>The Home dashboard (quick access, drives, recent files, tags).</summary>
    Home,

    /// <summary>Recycle Bin / Trash.</summary>
    Trash,

    /// <summary>Search results; <see cref="Location.Path"/> is the search root, <see cref="Location.Query"/> the text.</summary>
    Search,

    /// <summary>All items with a tag; <see cref="Location.Query"/> is the tag id.</summary>
    Tag,
}

/// <summary>An address a tab or pane can show. Immutable and value-equal (respecting path case rules).</summary>
public sealed record Location(LocationKind Kind, string? Path = null, string? Query = null)
{
    public static Location Home { get; } = new(LocationKind.Home);

    public static Location Trash { get; } = new(LocationKind.Trash);

    public static Location Folder(string path) => new(LocationKind.Folder, PathUtil.TrimTrailingSeparator(path));

    public static Location Search(string root, string query) => new(LocationKind.Search, PathUtil.TrimTrailingSeparator(root), query);

    public static Location Tag(string tagId) => new(LocationKind.Tag, Query: tagId);

    public bool IsFolder => Kind == LocationKind.Folder;

    public bool Equals(Location? other)
        => other is not null
           && Kind == other.Kind
           && PathUtil.AreEqual(Path, other.Path)
           && string.Equals(Query, other.Query, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(Kind, Path is null ? 0 : PathUtil.Comparer.GetHashCode(PathUtil.TrimTrailingSeparator(Path)), Query);

    /// <summary>Stable textual form used in session files: "folder:/home/me", "home:", "search:/root|query".</summary>
    public string Serialize() => Kind switch
    {
        LocationKind.Folder => "folder:" + Path,
        // Both parts are escaped: '|' is legal in Unix paths and in search text.
        LocationKind.Search => "search:" + Uri.EscapeDataString(Path ?? string.Empty) + "|" + Uri.EscapeDataString(Query ?? string.Empty),
        LocationKind.Tag => "tag:" + Query,
        LocationKind.Trash => "trash:",
        _ => "home:",
    };

    public static Location? TryParse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
            return null;

        var scheme = value[..colon];
        var rest = value[(colon + 1)..];
        return scheme switch
        {
            "home" => Home,
            "trash" => Trash,
            "folder" when rest.Length > 0 => Folder(rest),
            "tag" when rest.Length > 0 => Tag(rest),
            "search" when rest.IndexOf('|', StringComparison.Ordinal) is var bar and > 0
                => Search(Uri.UnescapeDataString(rest[..bar]), Uri.UnescapeDataString(rest[(bar + 1)..])),
            _ => null,
        };
    }
}
