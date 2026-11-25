using Navigator.Core.Platform;

namespace Navigator.Presentation.Items;

/// <summary>
/// Every entry the item context menu can contain. The planner decides <i>which</i> entries a selection gets and in what
/// order; the view decides how each one looks and which command it runs. Values are grouped by menu region.
/// </summary>
public enum ItemMenuAction : byte
{
    Separator,

    // Quick-action strip (icon row at the top of the menu)
    Cut,
    Copy,
    Paste,
    Rename,
    Delete,
    Properties,

    // Launch
    Open,
    OpenInNewTab,
    OpenInNewWindow,
    OpenWith,
    RunAsAdministrator,
    RunAsOtherUser,

    // Archives
    ExtractMenu,
    ExtractHere,
    ExtractToFolder,
    CompressMenu,
    CompressTogether,
    CompressEach,

    // Organize
    PasteIntoFolder,
    CopyPath,
    CreateFolderWithSelection,
    CreateShortcut,
    SendTo,
    Tags,
    PinToSidebar,

    // Overflow ("More options")
    MoreMenu,
    Duplicate,
    OpenInTerminal,
    OpenInCodeEditor,
    RevealInSystem,
    DeletePermanently,
}

/// <summary>One menu entry; <see cref="Children"/> is set for entries that open a fixed sub-menu.</summary>
public sealed record ItemMenuNode(ItemMenuAction Action, IReadOnlyList<ItemMenuNode>? Children = null)
{
    public static readonly ItemMenuNode Divider = new(ItemMenuAction.Separator);

    public bool IsSeparator => Action == ItemMenuAction.Separator;
}

/// <summary>A planned item menu: the icon strip plus the vertical entries below it.</summary>
public sealed record ItemMenuLayout(IReadOnlyList<ItemMenuAction> QuickActions, IReadOnlyList<ItemMenuNode> Entries);

/// <summary>User switches from Settings → General → Context menu, plus what the current location allows.</summary>
public readonly record struct ItemMenuOptions(
    bool ShowOpenInNewWindow,
    bool ShowCopyPath,
    bool ShowOpenTerminal,
    bool CanModify,
    bool CanCreateInCurrentFolder);

/// <summary>
/// What the menu needs to know about a selection, gathered in one pass so planning never re-walks the items or touches
/// the disk. A selection of one runnable program also carries what elevation the OS offers for it.
/// </summary>
public readonly record struct SelectionTraits(
    int Count,
    int FolderCount,
    int ArchiveCount,
    ItemCategory? SharedCategory,
    ElevationOptions Elevation)
{
    public bool IsEmpty => Count == 0;

    public bool IsSingle => Count == 1;

    public bool HasFolders => FolderCount > 0;

    public bool OnlyFolders => Count > 0 && FolderCount == Count;

    public bool OnlyFiles => Count > 0 && FolderCount == 0;

    public bool OnlyArchives => Count > 0 && ArchiveCount == Count;

    /// <summary>A single program the OS can start under other credentials (".exe", ".msi"… on Windows).</summary>
    public bool IsElevatableProgram => IsSingle && Elevation != ElevationOptions.None;

    /// <summary>A single text or source file: offered "Open in code editor" next to the folder case.</summary>
    public bool IsEditableText => IsSingle && SharedCategory is ItemCategory.Text or ItemCategory.Code;

    /// <param name="items">The selection.</param>
    /// <param name="isArchive">Archive test (extension based; must not read the file).</param>
    /// <param name="elevation">Elevation the OS offers for one path; asked only for a single file.</param>
    public static SelectionTraits Collect(IReadOnlyList<FileItemViewModel> items, Func<string, bool> isArchive, Func<string, ElevationOptions> elevation)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isArchive);
        ArgumentNullException.ThrowIfNull(elevation);

        int folders = 0, archives = 0;
        ItemCategory? shared = null;
        var mixed = false;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.IsDirectory)
                folders++;
            else if (isArchive(item.FullPath))
                archives++;

            if (mixed)
                continue;
            if (shared is null)
                shared = item.Category;
            else if (shared != item.Category)
            {
                shared = null;
                mixed = true;
            }
        }

        var runAs = items.Count == 1 && folders == 0 ? elevation(items[0].FullPath) : ElevationOptions.None;
        return new SelectionTraits(items.Count, folders, archives, shared, runAs);
    }
}

/// <summary>
/// Builds the item context menu for a selection. The layout follows the reference app's behaviour: a row of quick
/// actions, then launch entries that depend on the item type (folders open in tabs, programs can run elevated, documents
/// pick an app, archives extract), then organizing entries, sub-menus, and an overflow for rarely used commands.
/// Pure and allocation-light: it is evaluated on every right-click.
/// </summary>
public static class ItemMenuPlanner
{
    private static readonly ItemMenuAction[] StripWithClipboard =
        [ItemMenuAction.Cut, ItemMenuAction.Copy, ItemMenuAction.Paste, ItemMenuAction.Rename, ItemMenuAction.Delete, ItemMenuAction.Properties];

    private static readonly ItemMenuAction[] StripReadOnly = [ItemMenuAction.Copy, ItemMenuAction.Properties];

    public static ItemMenuLayout Plan(in SelectionTraits selection, in ItemMenuOptions options)
    {
        if (selection.IsEmpty)
            return new ItemMenuLayout([], []);

        var entries = new List<ItemMenuNode>(24);
        AddLaunchSection(entries, selection, options);
        AddOrganizeSection(entries, selection, options);
        AddTransferSection(entries, selection, options);
        AddMarkSection(entries, selection);
        AddOverflow(entries, selection, options);
        TrimSeparators(entries);
        return new ItemMenuLayout(options.CanModify ? StripWithClipboard : StripReadOnly, entries);
    }

    private static void AddLaunchSection(List<ItemMenuNode> entries, in SelectionTraits selection, in ItemMenuOptions options)
    {
        entries.Add(new ItemMenuNode(ItemMenuAction.Open));
        if (selection.HasFolders)
        {
            entries.Add(new ItemMenuNode(ItemMenuAction.OpenInNewTab));
            if (options.ShowOpenInNewWindow)
                entries.Add(new ItemMenuNode(ItemMenuAction.OpenInNewWindow));
        }

        if (selection.IsElevatableProgram)
        {
            if (selection.Elevation.HasFlag(ElevationOptions.Administrator))
                entries.Add(new ItemMenuNode(ItemMenuAction.RunAsAdministrator));
            if (selection.Elevation.HasFlag(ElevationOptions.OtherUser))
                entries.Add(new ItemMenuNode(ItemMenuAction.RunAsOtherUser));
        }
        else if (selection.IsSingle && selection.OnlyFiles)
        {
            // Programs launch themselves; every other single file chooses its application.
            entries.Add(new ItemMenuNode(ItemMenuAction.OpenWith));
        }

        if (selection.ArchiveCount > 0)
        {
            var extractChildren = new List<ItemMenuNode>(2) { new(ItemMenuAction.ExtractHere) };
            if (selection.OnlyArchives)
                extractChildren.Add(new ItemMenuNode(ItemMenuAction.ExtractToFolder));
            entries.Add(new ItemMenuNode(ItemMenuAction.ExtractMenu, extractChildren));
        }

        entries.Add(ItemMenuNode.Divider);
    }

    private static void AddOrganizeSection(List<ItemMenuNode> entries, in SelectionTraits selection, in ItemMenuOptions options)
    {
        if (options.CanModify && selection.IsSingle && selection.OnlyFolders)
            entries.Add(new ItemMenuNode(ItemMenuAction.PasteIntoFolder));
        if (options.ShowCopyPath)
            entries.Add(new ItemMenuNode(ItemMenuAction.CopyPath));
        if (options.CanModify && options.CanCreateInCurrentFolder)
            entries.Add(new ItemMenuNode(ItemMenuAction.CreateFolderWithSelection));
        if (options.CanModify && selection.IsSingle)
            entries.Add(new ItemMenuNode(ItemMenuAction.CreateShortcut));
        entries.Add(ItemMenuNode.Divider);
    }

    private static void AddTransferSection(List<ItemMenuNode> entries, in SelectionTraits selection, in ItemMenuOptions options)
    {
        if (options.CanModify)
        {
            var compressChildren = selection.Count > 1
                ? new ItemMenuNode[] { new(ItemMenuAction.CompressTogether), new(ItemMenuAction.CompressEach) }
                : [new(ItemMenuAction.CompressTogether)];
            entries.Add(new ItemMenuNode(ItemMenuAction.CompressMenu, compressChildren));
        }

        // Children are discovered when the sub-menu opens (known folders, removable drives).
        entries.Add(new ItemMenuNode(ItemMenuAction.SendTo));
        entries.Add(ItemMenuNode.Divider);
    }

    private static void AddMarkSection(List<ItemMenuNode> entries, in SelectionTraits selection)
    {
        entries.Add(new ItemMenuNode(ItemMenuAction.Tags));
        if (selection.HasFolders)
            entries.Add(new ItemMenuNode(ItemMenuAction.PinToSidebar));
        entries.Add(ItemMenuNode.Divider);
    }

    private static void AddOverflow(List<ItemMenuNode> entries, in SelectionTraits selection, in ItemMenuOptions options)
    {
        var more = new List<ItemMenuNode>(5);
        if (options.CanModify)
            more.Add(new ItemMenuNode(ItemMenuAction.Duplicate));
        if (options.ShowOpenTerminal && selection.HasFolders)
            more.Add(new ItemMenuNode(ItemMenuAction.OpenInTerminal));
        if (selection.IsEditableText || (selection.IsSingle && selection.OnlyFolders))
            more.Add(new ItemMenuNode(ItemMenuAction.OpenInCodeEditor));
        more.Add(new ItemMenuNode(ItemMenuAction.RevealInSystem));
        if (options.CanModify)
        {
            more.Add(ItemMenuNode.Divider);
            more.Add(new ItemMenuNode(ItemMenuAction.DeletePermanently));
        }

        entries.Add(new ItemMenuNode(ItemMenuAction.MoreMenu, more));
    }

    /// <summary>No leading, trailing or doubled separators, whatever sections ended up empty.</summary>
    private static void TrimSeparators(List<ItemMenuNode> entries)
    {
        var write = 0;
        for (var read = 0; read < entries.Count; read++)
        {
            var node = entries[read];
            if (node.IsSeparator && (write == 0 || entries[write - 1].IsSeparator))
                continue;
            entries[write++] = node;
        }

        while (write > 0 && entries[write - 1].IsSeparator)
            write--;
        entries.RemoveRange(write, entries.Count - write);
    }
}
