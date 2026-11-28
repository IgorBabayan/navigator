using Navigator.Core.Settings;

namespace Navigator.Presentation.Shell;

/// <summary>Where a toolbar button lives: the command bar on the left, or the view toggles on the right.</summary>
public enum ToolbarGroup
{
    Commands,
    View,
}

/// <summary>A button (or separator) the user can show, hide and move in Settings → Appearance → Toolbar.</summary>
public sealed record ToolbarItemInfo(string Id, string NameKey, string IconKey, ToolbarGroup Group, bool VisibleByDefault = true)
{
    public bool IsSeparator => Id.StartsWith(ToolbarLayout.SeparatorPrefix, StringComparison.Ordinal);
}

/// <summary>A resolved toolbar slot: the button and whether the user shows it.</summary>
public readonly record struct ToolbarSlot(ToolbarItemInfo Item, bool IsVisible);

/// <summary>
/// The customizable toolbar. Buttons that only make sense in some places (download/upload on server folders, the
/// operations ring, the update badge) are not listed: they keep appearing on their own when they apply.
/// </summary>
public static class ToolbarLayout
{
    public const string SeparatorPrefix = "separator.";

    /// <summary>Every customizable entry in its default order (within its group).</summary>
    public static IReadOnlyList<ToolbarItemInfo> Catalog { get; } =
    [
        new("new", "Command.New", "Add", ToolbarGroup.Commands),
        new("separator.edit", "Toolbar.Separator", "DragHorizontalVariant", ToolbarGroup.Commands),
        new("cut", "Command.Cut", "Cut", ToolbarGroup.Commands),
        new("copy", "Command.Copy", "Copy", ToolbarGroup.Commands),
        new("paste", "Command.Paste", "Paste", ToolbarGroup.Commands),
        new("rename", "Command.Rename", "Rename", ToolbarGroup.Commands),
        new("delete", "Command.Delete", "Delete", ToolbarGroup.Commands),
        new("separator.view", "Toolbar.Separator", "DragHorizontalVariant", ToolbarGroup.Commands),
        new("sort", "Command.Sort", "Sort", ToolbarGroup.Commands),
        new("group", "Command.Group", "Group", ToolbarGroup.Commands),
        new("layout", "Command.Layout", "LayoutDetails", ToolbarGroup.Commands),
        new("more", "Command.More", "More", ToolbarGroup.Commands),
        new("splitView", "Command.ToggleSplitView", "SplitView", ToolbarGroup.View),
        new("previewPane", "Command.TogglePreviewPane", "PreviewPane", ToolbarGroup.View),
        new("compactOverlay", "Command.CompactOverlay", "PictureInPictureBottomRight", ToolbarGroup.View),
        new("alwaysOnTop", "Command.AlwaysOnTop", "Pin", ToolbarGroup.View, VisibleByDefault: false),
        new("pluginShop", "Command.PluginShop", "Plugin", ToolbarGroup.View),
        new("settings", "Command.Settings", "Settings", ToolbarGroup.View),
    ];

    private static readonly Dictionary<string, ToolbarItemInfo> ById = Catalog.ToDictionary(i => i.Id, StringComparer.Ordinal);

    public static ToolbarItemInfo? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>
    /// The arrangement to draw for <paramref name="stored"/>: stored entries first, in their order, then catalog entries
    /// the file does not mention, each inserted after its default predecessor so a newly added button lands where it
    /// would be by default. Unknown and duplicate ids are skipped. Groups are returned in catalog order of appearance.
    /// </summary>
    public static IReadOnlyList<ToolbarSlot> Resolve(IReadOnlyList<ToolbarItemSetting>? stored)
    {
        var slots = new List<ToolbarSlot>(Catalog.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (stored is not null)
        {
            foreach (var setting in stored)
            {
                if (setting?.Id is { } id && ById.TryGetValue(id, out var item) && seen.Add(id))
                    slots.Add(new ToolbarSlot(item, setting.IsVisible));
            }
        }

        for (var i = 0; i < Catalog.Count; i++)
        {
            var missing = Catalog[i];
            if (!seen.Add(missing.Id))
                continue;
            // Every earlier catalog entry is placed by now, so the default predecessor is always found.
            var predecessor = i == 0 ? null : Catalog[i - 1];
            var at = predecessor is null ? 0 : slots.FindIndex(s => ReferenceEquals(s.Item, predecessor)) + 1;
            slots.Insert(at, new ToolbarSlot(missing, missing.VisibleByDefault));
        }

        // Keep each group together (a hand-edited file could interleave them); stable within a group.
        return [.. slots.Where(s => s.Item.Group == ToolbarGroup.Commands), .. slots.Where(s => s.Item.Group == ToolbarGroup.View)];
    }

    /// <summary>The stored form of an arrangement (every entry, so later defaults never reshuffle the user's order).</summary>
    public static List<ToolbarItemSetting> ToSettings(IEnumerable<ToolbarSlot> slots)
        => [.. slots.Select(s => new ToolbarItemSetting { Id = s.Item.Id, IsVisible = s.IsVisible })];
}
