using Navigator.Core.IO;

namespace Navigator.Core.Navigation;

/// <summary>One column left of the current folder in the column (Miller) layout: a folder and the child shown open.</summary>
/// <param name="Folder">Folder whose children the column lists.</param>
/// <param name="OpenChild">Child of <paramref name="Folder"/> that leads to the current folder (drawn highlighted).</param>
public readonly record struct TrailColumn(string Folder, string OpenChild);

/// <summary>
/// Geometry of the column layout. The pane keeps an anchor folder (where the user switched to columns, or the
/// highest folder reached since); the current folder is always the anchor or one of its descendants, and every
/// folder between them gets a column of its own. Pure path arithmetic: nothing here touches the disk.
/// </summary>
public static class ColumnTrail
{
    /// <summary>Columns kept to the left of the current folder; deeper trails move the anchor down.</summary>
    public const int MaxColumns = 24;

    /// <summary>
    /// The anchor to use after navigating to <paramref name="target"/>: kept while the target lies inside it, moved up
    /// when the target is one of its ancestors, and restarted at the target when the two are unrelated.
    /// </summary>
    public static string NextAnchor(string? anchor, string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // Inside the anchor: unchanged. An ancestor of it, or an unrelated folder, becomes the new anchor.
        return anchor is { Length: > 0 } && PathUtil.IsSameOrDescendant(anchor, target) ? anchor : target;
    }

    /// <summary>True when the column layout continues from <paramref name="anchor"/> to <paramref name="target"/>.</summary>
    public static bool Continues(string? anchor, string target)
        => anchor is { Length: > 0 } && (PathUtil.IsSameOrDescendant(anchor, target) || PathUtil.IsSameOrDescendant(target, anchor));

    /// <summary>
    /// Columns from <paramref name="anchor"/> down to the parent of <paramref name="current"/>, outermost first.
    /// Empty when the current folder is the anchor or is not below it.
    /// </summary>
    public static IReadOnlyList<TrailColumn> Build(string anchor, string current)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(current);
        if (PathUtil.AreEqual(anchor, current) || !PathUtil.IsSameOrDescendant(anchor, current))
            return [];

        // Walk up from the current folder: each step yields the parent column and the child it opens.
        var reversed = new List<TrailColumn>(8);
        var child = PathUtil.TrimTrailingSeparator(current);
        while (Path.GetDirectoryName(child) is { Length: > 0 } parent)
        {
            reversed.Add(new TrailColumn(parent, child));
            if (PathUtil.AreEqual(parent, anchor))
                break;
            child = parent;
        }

        reversed.Reverse();
        return reversed.Count > MaxColumns ? reversed.GetRange(reversed.Count - MaxColumns, MaxColumns) : reversed;
    }
}
