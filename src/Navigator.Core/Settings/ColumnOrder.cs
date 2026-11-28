namespace Navigator.Core.Settings;

/// <summary>
/// Order of the details-view columns. The stored <see cref="FilesAndFoldersSettings.Columns"/> list is the order the
/// user arranged; this keeps it usable whatever a hand-edited or older file contains: the name column leads (it carries
/// the icon and the rename editor), every known column appears exactly once, unknown ids are dropped, and columns
/// added in later versions join at the end.
/// </summary>
public static class ColumnOrder
{
    public const string NameColumn = "name";

    /// <summary>Every details column, in the default order.</summary>
    public static IReadOnlyList<string> Known { get; } = ["name", "modified", "type", "size", "created", "tags"];

    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? stored)
    {
        var order = new List<string>(Known.Count) { NameColumn };
        if (stored is not null)
        {
            foreach (var id in stored)
            {
                if (id is not null && Known.Contains(id, StringComparer.Ordinal) && !order.Contains(id, StringComparer.Ordinal))
                    order.Add(id);
            }
        }

        foreach (var id in Known)
        {
            if (!order.Contains(id, StringComparer.Ordinal))
                order.Add(id);
        }

        return order;
    }

    /// <summary>
    /// Moves <paramref name="id"/> to <paramref name="index"/> (clamped behind the name column). Returns the new order,
    /// or the same instance when nothing moves.
    /// </summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> order, string id, int index)
    {
        ArgumentNullException.ThrowIfNull(order);
        var from = IndexOf(order, id);
        if (from <= 0)
            return order;
        var target = Math.Clamp(index, 1, order.Count - 1);
        if (target == from)
            return order;
        var moved = new List<string>(order);
        moved.RemoveAt(from);
        moved.Insert(target, id);
        return moved;
    }

    private static int IndexOf(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], id, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
