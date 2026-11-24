using Navigator.Core.Settings;

namespace Navigator.Presentation.Commands;

/// <summary>A user-invocable action with its default key gestures ("Primary" = Ctrl on Windows/Linux, Cmd on macOS).</summary>
public sealed record AppCommandInfo(string Id, string NameKey, IReadOnlyList<string> DefaultGestures, string Category);

/// <summary>
/// The catalog of commands shown in the command palette and in Settings → Keyboard shortcuts.
/// Gestures use Avalonia's KeyGesture syntax; "Primary" is resolved per platform.
/// </summary>
public static class CommandRegistry
{
    public static IReadOnlyList<AppCommandInfo> All { get; } =
    [
        new("tab.new", "Command.NewTab", ["Primary+T"], "Tabs"),
        new("tab.close", "Command.CloseTab", ["Primary+W", "Primary+F4"], "Tabs"),
        new("tab.reopen", "Command.ReopenClosedTab", ["Primary+Shift+T"], "Tabs"),
        new("tab.duplicate", "Command.DuplicateTab", ["Primary+K"], "Tabs"),
        new("tab.next", "Command.NextTab", ["Primary+Tab"], "Tabs"),
        new("tab.previous", "Command.PreviousTab", ["Primary+Shift+Tab"], "Tabs"),
        new("window.new", "Command.NewWindow", ["Primary+N"], "Tabs"),
        new("view.splitPane", "Command.ToggleSplitView", ["Primary+Shift+S"], "View"),
        new("view.sidebar", "Command.ToggleSidebar", ["Primary+B"], "View"),
        new("view.previewPane", "Command.TogglePreviewPane", ["Alt+P"], "View"),
        new("view.hiddenItems", "Command.ToggleHiddenItems", ["Primary+H"], "View"),
        new("view.details", "Command.LayoutDetails", ["Primary+Shift+1"], "View"),
        new("view.list", "Command.LayoutList", ["Primary+Shift+2"], "View"),
        new("view.tiles", "Command.LayoutTiles", ["Primary+Shift+3"], "View"),
        new("view.gridMedium", "Command.LayoutMediumIcons", ["Primary+Shift+4"], "View"),
        new("view.gridLarge", "Command.LayoutLargeIcons", ["Primary+Shift+5"], "View"),
        new("view.commandPalette", "Command.CommandPalette", ["Primary+Shift+P"], "View"),
        new("view.filter", "Command.FilterFolder", ["Primary+Shift+F"], "View"),
        new("nav.back", "Command.Back", ["Alt+Left"], "Navigation"),
        new("nav.forward", "Command.Forward", ["Alt+Right"], "Navigation"),
        new("nav.up", "Command.Up", ["Alt+Up"], "Navigation"),
        new("nav.refresh", "Command.Refresh", ["F5", "Primary+R"], "Navigation"),
        new("nav.address", "Command.EditAddress", ["Primary+L", "Alt+D"], "Navigation"),
        new("nav.search", "Command.Search", ["Primary+F", "F3"], "Navigation"),
        new("nav.home", "Command.GoHome", ["Alt+Home"], "Navigation"),
        new("item.open", "Command.Open", ["Enter"], "Items"),
        new("item.newFolder", "Command.NewFolder", ["Primary+Shift+N"], "Items"),
        new("item.newFile", "Command.NewFile", ["Primary+Alt+N"], "Items"),
        new("item.rename", "Command.Rename", ["F2"], "Items"),
        new("item.delete", "Command.Delete", ["Delete"], "Items"),
        new("item.deletePermanently", "Command.DeletePermanently", ["Shift+Delete"], "Items"),
        new("item.copy", "Command.Copy", ["Primary+C"], "Items"),
        new("item.cut", "Command.Cut", ["Primary+X"], "Items"),
        new("item.paste", "Command.Paste", ["Primary+V"], "Items"),
        new("item.duplicate", "Command.Duplicate", ["Primary+D"], "Items"),
        new("item.copyPath", "Command.CopyPath", ["Primary+Shift+C"], "Items"),
        new("item.selectAll", "Command.SelectAll", ["Primary+A"], "Items"),
        new("item.invertSelection", "Command.InvertSelection", ["Primary+I"], "Items"),
        new("item.clearSelection", "Command.ClearSelection", ["Primary+Shift+A"], "Items"),
        new("item.properties", "Command.Properties", ["Alt+Enter"], "Items"),
        new("item.compress", "Command.Compress", [], "Items"),
        new("item.extract", "Command.Extract", ["Primary+Shift+E"], "Items"),
        new("item.pin", "Command.PinToSidebar", [], "Items"),
        new("item.openTerminal", "Command.OpenTerminal", ["Primary+Shift+`"], "Items"),
        new("item.openInEditor", "Command.OpenInCodeEditor", [], "Items"),
        new("edit.undo", "Command.Undo", ["Primary+Z"], "Items"),
        new("edit.redo", "Command.Redo", ["Primary+Y", "Primary+Shift+Z"], "Items"),
        new("app.settings", "Command.Settings", ["Primary+OemComma"], "Application"),
        new("app.fullScreen", "Command.FullScreen", ["F11"], "Application"),
    ];

    public static AppCommandInfo? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>Effective gestures for a command: user overrides from settings, else the defaults; "Primary" resolved.</summary>
    public static IReadOnlyList<string> GesturesFor(string id, AppSettings settings, bool isMac)
    {
        var gestures = settings.KeyBindings.TryGetValue(id, out var custom) ? custom : Find(id)?.DefaultGestures ?? [];
        return [.. gestures.Select(g => ResolvePrimary(g, isMac))];
    }

    public static string ResolvePrimary(string gesture, bool isMac)
        => gesture.Replace("Primary", isMac ? "Cmd" : "Ctrl", StringComparison.Ordinal);

    /// <summary>Finds commands whose gestures collide (for warnings in the shortcut editor).</summary>
    public static IReadOnlyList<(string Gesture, IReadOnlyList<string> CommandIds)> FindConflicts(AppSettings settings, bool isMac)
        => [.. All.SelectMany(c => GesturesFor(c.Id, settings, isMac).Select(g => (Gesture: g, c.Id)))
            .GroupBy(x => x.Gesture, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<string>)[.. g.Select(x => x.Id)]))];
}
