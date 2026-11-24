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

    private static readonly Dictionary<string, AppCommandInfo> ById = All.ToDictionary(c => c.Id, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [","] = "OemComma",
        ["."] = "OemPeriod",
        ["+"] = "OemPlus",
        ["plus"] = "OemPlus",
        ["-"] = "OemMinus",
        ["minus"] = "OemMinus",
        ["`"] = "OemTilde",
        ["~"] = "OemTilde",
        [";"] = "OemSemicolon",
        ["'"] = "OemQuotes",
        ["/"] = "OemQuestion",
        ["\\"] = "OemBackslash",
        ["["] = "OemOpenBrackets",
        ["]"] = "OemCloseBrackets",
        ["esc"] = "Escape",
        ["del"] = "Delete",
        ["ins"] = "Insert",
        ["return"] = "Enter",
        ["pgup"] = "PageUp",
        ["pgdn"] = "PageDown",
        ["backspace"] = "Back",
        ["space"] = "Space",
    };

    private static readonly Dictionary<string, string> KeyDisplay = new(StringComparer.Ordinal)
    {
        ["OemComma"] = ",",
        ["OemPeriod"] = ".",
        ["OemPlus"] = "Plus",
        ["OemMinus"] = "-",
        ["OemTilde"] = "`",
        ["OemSemicolon"] = ";",
        ["OemQuotes"] = "'",
        ["OemQuestion"] = "/",
        ["OemBackslash"] = "\\",
        ["OemOpenBrackets"] = "[",
        ["OemCloseBrackets"] = "]",
        ["Back"] = "Backspace",
    };

    public static AppCommandInfo? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>
    /// Effective gestures for a command in canonical form (parseable by Avalonia's KeyGesture, "Primary" resolved to
    /// Ctrl or Cmd): user overrides from settings, else the defaults. Invalid stored gestures are skipped.
    /// </summary>
    public static IReadOnlyList<string> GesturesFor(string id, AppSettings settings, bool isMac)
    {
        var gestures = settings.KeyBindings.TryGetValue(id, out var custom) ? custom : Find(id)?.DefaultGestures ?? [];
        if (gestures.Count == 0)
            return [];
        var result = new List<string>(gestures.Count);
        foreach (var gesture in gestures)
        {
            if (NormalizeGesture(gesture, isMac) is { } normalized)
                result.Add(ResolvePrimary(normalized, isMac));
        }

        return result;
    }

    /// <summary>Default gestures of a command in canonical storage form (with "Primary").</summary>
    public static IReadOnlyList<string> NormalizedDefaults(string id, bool isMac)
        => Find(id) is { } command ? [.. command.DefaultGestures.Select(g => NormalizeGesture(g, isMac)).OfType<string>()] : [];

    public static string ResolvePrimary(string gesture, bool isMac)
        => gesture.Replace("Primary", isMac ? "Cmd" : "Ctrl", StringComparison.Ordinal);

    /// <summary>
    /// Converts user-typed or recorded text ("ctrl + shift + 1", "Cmd+,", "Alt+Left") to the canonical storage form
    /// ("Primary+Shift+D1"): modifiers in a fixed order, the platform's main modifier stored as "Primary" so settings
    /// stay portable between Windows/Linux and macOS, and key aliases mapped to Avalonia key names.
    /// Returns null when the text is not a valid gesture (no key, two keys, unknown modifier, bare number).
    /// </summary>
    public static string? NormalizeGesture(string? text, bool isMac)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();

        // "Ctrl++" / "Ctrl+Plus": a trailing '+' is the plus key, not a separator.
        string? trailingPlus = null;
        if (trimmed.Length > 1 && trimmed.EndsWith("++", StringComparison.Ordinal))
        {
            trailingPlus = "+";
            trimmed = trimmed[..^2];
        }
        else if (trimmed == "+")
        {
            return "OemPlus";
        }

        bool primary = false, ctrl = false, shift = false, alt = false, meta = false;
        string? key = trailingPlus;
        foreach (var raw in trimmed.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
                return null;
            switch (raw.ToLowerInvariant())
            {
                case "primary":
                    primary = true;
                    continue;
                case "ctrl" or "control":
                    ctrl |= isMac;
                    primary |= !isMac;
                    continue;
                case "cmd" or "command" or "meta" or "win" or "super":
                    primary |= isMac;
                    meta |= !isMac;
                    continue;
                case "shift":
                    shift = true;
                    continue;
                case "alt" or "option" or "opt":
                    alt = true;
                    continue;
            }

            if (key is not null)
                return null;
            key = raw;
        }

        if (key is null)
            return null;
        if (key.Length == 1 && char.IsAsciiDigit(key[0]))
            key = "D" + key;
        else if (KeyAliases.TryGetValue(key, out var alias))
            key = alias;
        else if (key.Length == 1 && char.IsAsciiLetter(key[0]))
            key = key.ToUpperInvariant();
        else if (!IsKeyName(key))
            return null;
        else
            key = char.ToUpperInvariant(key[0]) + key[1..];

        var builder = new System.Text.StringBuilder(32);
        if (primary)
            builder.Append("Primary+");
        if (ctrl)
            builder.Append("Ctrl+");
        if (shift)
            builder.Append("Shift+");
        if (alt)
            builder.Append("Alt+");
        if (meta)
            builder.Append("Meta+");
        return builder.Append(key).ToString();
    }

    /// <summary>Human-friendly form of a resolved gesture ("Ctrl+Shift+D1" → "Ctrl+Shift+1", "Ctrl+OemComma" → "Ctrl+,").</summary>
    public static string FormatForDisplay(string gesture)
    {
        var plus = gesture.LastIndexOf('+');
        var key = plus < 0 ? gesture : gesture[(plus + 1)..];
        var shown = key.Length == 2 && key[0] == 'D' && char.IsAsciiDigit(key[1]) ? key[1..]
            : KeyDisplay.TryGetValue(key, out var display) ? display : key;
        return ReferenceEquals(shown, key) ? gesture : gesture[..(plus + 1)] + shown;
    }

    /// <summary>Finds commands whose gestures collide (for warnings in the shortcut editor).</summary>
    public static IReadOnlyList<(string Gesture, IReadOnlyList<string> CommandIds)> FindConflicts(AppSettings settings, bool isMac)
        => [.. All.SelectMany(c => GesturesFor(c.Id, settings, isMac).Select(g => (Gesture: g, c.Id)))
            .GroupBy(x => x.Gesture, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<string>)[.. g.Select(x => x.Id)]))];

    // Letters/digits only, starting with a letter, no pure numbers (Enum.Parse would accept "12" as a key value).
    private static bool IsKeyName(string key)
        => key.Length > 1 && char.IsAsciiLetter(key[0]) && key.AsSpan().IndexOfAnyExcept(KeyNameChars) < 0;

    private static readonly System.Buffers.SearchValues<char> KeyNameChars
        = System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
}
