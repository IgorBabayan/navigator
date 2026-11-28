using Navigator.Core.IO;

namespace Navigator.Plugins;

/// <summary>
/// A details-view column filled by an <see cref="IFolderDecorator"/> (plugin API 1.3). The pane shows it while the folder
/// it displays is decorated by that decorator (e.g. "Git status" inside a repository). Columns cannot be sorted by.
/// </summary>
/// <param name="Id">Unique within the decorator; stored in settings as "{decorator id}.{column id}".</param>
/// <param name="Title">Localized header text; read again after a language change.</param>
/// <param name="DefaultWidth">Width until the user resizes the column.</param>
/// <param name="IsVisibleByDefault">Shown until the user hides it from the header's context menu.</param>
public sealed record PluginColumn(string Id, string Title, double DefaultWidth = 140, bool IsVisibleByDefault = true)
{
    /// <summary>Right-aligned text (numbers, sizes).</summary>
    public bool AlignRight { get; init; }
}

/// <summary>
/// A small overlay drawn on an item's icon (sync state, Git state).
/// </summary>
/// <param name="IconKey">Icon catalog key (any Material Design Icons name, e.g. "CloudOutline").</param>
/// <param name="Tint">Color as "#RRGGBB"; null draws it in the text color.</param>
public sealed record PluginBadge(string IconKey, string? Tint = null);

/// <summary>What a decorator says about one item of the folder.</summary>
public sealed record ItemDecoration
{
    public PluginBadge? Badge { get; init; }

    /// <summary>Cell texts by <see cref="PluginColumn.Id"/> (columns without an entry stay empty).</summary>
    public IReadOnlyDictionary<string, string>? Cells { get; init; }
}

/// <summary>
/// A segment of the status bar while a decorated folder is shown, such as the Git branch ("main ↑1 ↓2"). Clicking it
/// opens a menu of <see cref="Commands"/>, run by <see cref="IFolderDecorator.ExecuteAsync"/>.
/// </summary>
public sealed record PluginStatusSegment(string Text, string IconKey, string? ToolTip = null)
{
    public IReadOnlyList<PluginCommand> Commands { get; init; } = [];
}

/// <summary>One step of a folder's decoration. Steps of one run are merged in order: later ones win per item and cell.</summary>
public sealed record FolderDecoration
{
    public static readonly IReadOnlyDictionary<string, ItemDecoration> NoItems = new Dictionary<string, ItemDecoration>(0);

    /// <summary>Status bar segment; null keeps the one of an earlier step (or none).</summary>
    public PluginStatusSegment? Status { get; init; }

    /// <summary>Item decorations by item name (as in <see cref="FileEntry.Name"/>).</summary>
    public IReadOnlyDictionary<string, ItemDecoration> Items { get; init; } = NoItems;
}

/// <summary>Why a folder is being decorated.</summary>
public enum FolderDecorationReason
{
    /// <summary>The folder was opened (cached results may be reused).</summary>
    Opened,

    /// <summary>Items of the folder changed, or the decorator reported a change: cached results are stale.</summary>
    Changed,

    /// <summary>The user asked for a refresh.</summary>
    Refresh,
}

/// <summary>A local folder to decorate and a snapshot of the items it shows.</summary>
/// <param name="FolderPath">Absolute local path of the folder.</param>
/// <param name="Entries">The items listed in the folder (hidden items only when the pane shows them).</param>
/// <param name="VisibleColumns">Ids of this decorator's columns that are shown; work for hidden columns can be skipped.</param>
public sealed record FolderDecorationRequest(string FolderPath, IReadOnlyList<FileEntry> Entries, IReadOnlySet<string> VisibleColumns, FolderDecorationReason Reason);

/// <summary>Raised by a decorator whose results changed for <see cref="FolderPath"/> and everything below it (null: everywhere).</summary>
public sealed class FolderDecorationsChangedEventArgs(string? folderPath) : EventArgs
{
    public static FolderDecorationsChangedEventArgs Everywhere { get; } = new(null);

    public string? FolderPath { get; } = folderPath;

    /// <summary>True when <paramref name="path"/> is <see cref="FolderPath"/> or inside it.</summary>
    public bool Covers(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (FolderPath is null)
            return true;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = FolderPath.TrimEnd('/', '\\');
        return path.StartsWith(root, comparison) && (path.Length == root.Length || path[root.Length] is '/' or '\\');
    }
}

/// <summary>
/// Adds information to the items of local folders (plugin API 1.3): columns of the details view, badges on item icons and
/// a status bar segment. Panes call it for the folder they show once its listing is complete, again (debounced) when
/// items change, and when <see cref="Changed"/> covers the folder. Only realized work is visible, so keep it cheap:
/// <list type="bullet">
/// <item><see cref="AppliesTo"/> runs for every folder opened: answer from strings or a cache.</item>
/// <item><see cref="DecorateAsync"/> runs off the UI thread, may run concurrently for different panes, and must stop
/// promptly when cancelled (the user navigated away).</item>
/// </list>
/// </summary>
public interface IFolderDecorator
{
    /// <summary>Stable id, unique across plugins ("git", "cloudsync").</summary>
    string Id { get; }

    /// <summary>The columns this decorator can fill (titles localized; read again after a language change).</summary>
    IReadOnlyList<PluginColumn> Columns { get; }

    /// <summary>Whether <paramref name="folderPath"/> gets decorations at all. Called off the UI thread; must be fast.</summary>
    bool AppliesTo(string folderPath);

    /// <summary>The decorations of a folder, in one or more steps (e.g. Git status first, last commits later).</summary>
    IAsyncEnumerable<FolderDecoration> DecorateAsync(FolderDecorationRequest request, CancellationToken cancellationToken);

    /// <summary>Runs a command of the status segment shown for <paramref name="folderPath"/>. Called on the UI thread.</summary>
    Task ExecuteAsync(string commandId, string folderPath, CancellationToken cancellationToken);

    /// <summary>Raise (from any thread) when earlier results are stale (e.g. a commit was made).</summary>
    event EventHandler<FolderDecorationsChangedEventArgs>? Changed;
}
