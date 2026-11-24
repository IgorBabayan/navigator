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

    /// <summary>A folder served by a plugin file system ("sftp://user@host/path"); <see cref="Location.Path"/> is the URI.</summary>
    Remote,
}

/// <summary>An address a tab or pane can show. Immutable and value-equal (respecting path case rules).</summary>
public sealed record Location(LocationKind Kind, string? Path = null, string? Query = null)
{
    public static Location Home { get; } = new(LocationKind.Home);

    public static Location Trash { get; } = new(LocationKind.Trash);

    public static Location Folder(string path) => new(LocationKind.Folder, PathUtil.TrimTrailingSeparator(path));

    public static Location Search(string root, string query) => new(LocationKind.Search, PathUtil.TrimTrailingSeparator(root), query);

    public static Location Tag(string tagId) => new(LocationKind.Tag, Query: tagId);

    /// <summary>A plugin-served folder. The URI is normalized (no trailing slash except for the root).</summary>
    public static Location Remote(string uri) => new(LocationKind.Remote, RemoteUri.Normalize(uri));

    /// <summary>A local folder, or a remote one when <paramref name="path"/> is a plugin URI ("sftp://…").</summary>
    public static Location FromPath(string path) => RemoteUri.IsRemote(path) ? Remote(path) : Folder(path);

    public bool IsFolder => Kind == LocationKind.Folder;

    public bool IsRemote => Kind == LocationKind.Remote;

    public bool Equals(Location? other)
        => other is not null
           && Kind == other.Kind
           && (Kind == LocationKind.Remote ? string.Equals(Path, other.Path, StringComparison.Ordinal) : PathUtil.AreEqual(Path, other.Path))
           && string.Equals(Query, other.Query, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(Kind, Path is null ? 0
            : Kind == LocationKind.Remote ? StringComparer.Ordinal.GetHashCode(Path)
            : PathUtil.Comparer.GetHashCode(PathUtil.TrimTrailingSeparator(Path)), Query);

    /// <summary>Stable textual form used in session files: "folder:/home/me", "home:", "search:/root|query", "remote:sftp://host/dir".</summary>
    public string Serialize() => Kind switch
    {
        LocationKind.Folder => "folder:" + Path,
        // Both parts are escaped: '|' is legal in Unix paths and in search text.
        LocationKind.Search => "search:" + Uri.EscapeDataString(Path ?? string.Empty) + "|" + Uri.EscapeDataString(Query ?? string.Empty),
        LocationKind.Tag => "tag:" + Query,
        LocationKind.Remote => "remote:" + Path,
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
            "remote" when RemoteUri.IsRemote(rest) => Remote(rest),
            "search" when rest.IndexOf('|', StringComparison.Ordinal) is var bar and > 0
                => Search(Uri.UnescapeDataString(rest[..bar]), Uri.UnescapeDataString(rest[(bar + 1)..])),
            _ => null,
        };
    }
}
