namespace Navigator.Plugins;

/// <summary>A right-click in a pane on local items (or on the empty area of a local folder).</summary>
/// <param name="Paths">Full local paths of the selected items; empty for the background menu.</param>
/// <param name="FolderPath">The folder the pane shows; null for search results and tag views.</param>
/// <param name="IsBackground">The menu of the folder itself (right-click on empty space).</param>
/// <param name="ShiftHeld">Shift was down: rarely used entries may be included.</param>
/// <param name="OwnerWindow">Native handle of the window (HWND on Windows) that parents dialogs; 0 when unknown.</param>
public sealed record PluginMenuRequest(IReadOnlyList<string> Paths, string? FolderPath, bool IsBackground, bool ShiftHeld, nint OwnerWindow)
{
    /// <summary>The items the menu is about: the selection, or the shown folder for the background menu.</summary>
    public IReadOnlyList<string> Targets => IsBackground && FolderPath is { } folder ? [folder] : Paths;
}

/// <summary>
/// One context-menu entry contributed by a plugin. An entry runs <see cref="Invoke"/>, or opens a sub-menu of
/// <see cref="Children"/> followed by the entries <see cref="LoadChildren"/> produces when the sub-menu first opens.
/// </summary>
public sealed record PluginMenuItem(string Title)
{
    public static PluginMenuItem Separator { get; } = new(string.Empty) { IsSeparator = true };

    /// <summary>Icon catalog key (Material Design Icons name); null for none.</summary>
    public string? IconKey { get; init; }

    /// <summary>
    /// An icon the operating system draws (a <c>Navigator.Core.Platform.SystemIconKey</c>, e.g. the icon of an application);
    /// preferred over <see cref="IconKey"/> where the OS can draw it.
    /// </summary>
    public string? SystemIconKey { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>Drawn with a check mark.</summary>
    public bool IsChecked { get; init; }

    public bool IsSeparator { get; init; }

    /// <summary>Runs the entry. Called on the UI thread after the menu closed; do slow work off it.</summary>
    public Func<CancellationToken, Task>? Invoke { get; init; }

    public IReadOnlyList<PluginMenuItem>? Children { get; init; }

    /// <summary>Sub-menu entries produced when the sub-menu first opens (off the UI thread).</summary>
    public Func<CancellationToken, Task<IReadOnlyList<PluginMenuItem>>>? LoadChildren { get; init; }
}

/// <summary>
/// Adds entries to the pane context menus of local items (plugin API 1.3). The menu opens at once with Navigator's own
/// entries; a provider's entries are added above "Show more options" as soon as they are ready (usually before the
/// pointer gets there). Entries that are not ready after a few seconds are left out.
/// </summary>
public interface IItemMenuProvider
{
    /// <summary>Stable id, unique across plugins.</summary>
    string Id { get; }

    /// <summary>
    /// The entries for <paramref name="request"/>. Called off the UI thread whenever a menu opens.
    /// <paramref name="menuLifetime"/> is cancelled when the menu closes: release what the entries hold on to then
    /// (entries may still be invoked right after the menu closed, so keep them usable for a few seconds).
    /// </summary>
    Task<IReadOnlyList<PluginMenuItem>> GetItemsAsync(PluginMenuRequest request, CancellationToken menuLifetime);
}
