using Navigator.Core.Platform;
using Navigator.Presentation.Items;

namespace Navigator.Presentation.Menus;

/// <summary>
/// What a context menu needs to know about the selection, gathered in one pass so composing a menu never walks the
/// items twice or touches the disk. A single selected program also carries the elevation the OS offers for it.
/// </summary>
public readonly record struct SelectionTraits(
    int Count,
    int FolderCount,
    int ArchiveCount,
    int ShortcutCount,
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

    /// <summary>A single shortcut file (".lnk", ".desktop", ".url"…): it opens its target, so it gets no "Open with".</summary>
    public bool IsSingleShortcut => IsSingle && ShortcutCount == 1;

    /// <summary>A single text or source file: offered "Open in code editor" like a folder.</summary>
    public bool IsEditableText => IsSingle && SharedCategory is ItemCategory.Text or ItemCategory.Code;

    /// <param name="items">The selection.</param>
    /// <param name="isArchive">Archive test (extension based; must not read the file).</param>
    /// <param name="elevation">Elevation the OS offers for one path; asked only for a single file.</param>
    public static SelectionTraits Collect(IReadOnlyList<FileItemViewModel> items, Func<string, bool> isArchive, Func<string, ElevationOptions> elevation)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isArchive);
        ArgumentNullException.ThrowIfNull(elevation);

        int folders = 0, archives = 0, shortcuts = 0;
        ItemCategory? common = null;
        var varied = false;
        foreach (var picked in items)
        {
            if (picked.IsDirectory)
                folders++;
            else if (isArchive(picked.FullPath))
                archives++;
            if (picked.Category == ItemCategory.Link)
                shortcuts++;

            if (varied)
                continue;
            if (common is null)
            {
                common = picked.Category;
            }
            else if (common != picked.Category)
            {
                common = null;
                varied = true;
            }
        }

        var runAs = items.Count == 1 && folders == 0 ? elevation(items[0].FullPath) : ElevationOptions.None;
        return new SelectionTraits(items.Count, folders, archives, shortcuts, common, runAs);
    }
}
