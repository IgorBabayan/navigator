using System.Windows.Input;
using Navigator.Core.Platform;

namespace Navigator.Presentation.Menus;

/// <summary>How an entry behaves when rendered.</summary>
public enum MenuEntryKind : byte
{
    Action,
    Toggle,
    Radio,
    Separator,
}

/// <summary>Kinds of location a pane can show; an entry lists the ones it belongs to.</summary>
[Flags]
public enum MenuScope
{
    None = 0,
    Folder = 1,
    Search = 2,
    Tag = 4,
    Trash = 8,
    Remote = 16,
    Home = 32,

    /// <summary>Local file-system views: a folder, search results, a tag.</summary>
    Local = Folder | Search | Tag,
    Anywhere = Local | Trash | Remote | Home,
}

/// <summary>Well-known entries the renderer or the shell merge needs to find again.</summary>
public static class MenuSlots
{
    public const string OpenWith = "open-with";
    public const string SendTo = "send-to";
    public const string Tags = "tags";
    public const string MoreOptions = "more-options";
}

/// <summary>
/// One context-menu entry, independent of any UI toolkit. A composer lists every candidate with the rules that decide
/// whether it appears (<see cref="IsWanted"/>, <see cref="Scopes"/>, <see cref="SingleOnly"/>, <see cref="ShiftOnly"/>);
/// <see cref="PaneMenuComposer.Refine"/> applies them for the current situation. Entries that only matter once a
/// sub-menu opens provide <see cref="LoadChildren"/> instead of (or in addition to) <see cref="Children"/>.
/// </summary>
public sealed class MenuEntry
{
    /// <summary>The shared "not shown" entry builders return; never rendered.</summary>
    public static readonly MenuEntry Omitted = new() { IsWanted = false };

    public MenuEntryKind Kind { get; init; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Icon catalog key (Material glyph); null for none.</summary>
    public string? Glyph { get; init; }

    /// <summary>Color of the glyph as "#RRGGBB" (tags); null for the text color.</summary>
    public string? Tint { get; init; }

    /// <summary>A bitmap from the operating system (shell menu entries); preferred over <see cref="Glyph"/>.</summary>
    public SystemIconImage? Picture { get; init; }

    public ICommand? Command { get; init; }

    public object? Parameter { get; init; }

    /// <summary>Keyboard command id (CommandRegistry) whose first gesture is shown next to the label.</summary>
    public string? GestureId { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsChecked { get; init; }

    /// <summary>Shown as an icon button in the strip on top of the menu instead of as a row.</summary>
    public bool InStrip { get; init; }

    /// <summary>Appears in the main list only while Shift is held; otherwise it moves under "Show more options".</summary>
    public bool ShiftOnly { get; init; }

    /// <summary>Only when exactly one item is selected.</summary>
    public bool SingleOnly { get; init; }

    public MenuScope Scopes { get; init; } = MenuScope.Local;

    /// <summary>The composer's own condition (settings, item types). False entries are dropped before anything else.</summary>
    public bool IsWanted { get; init; } = true;

    /// <summary>A <see cref="MenuSlots"/> name, for entries found again later.</summary>
    public string? Slot { get; init; }

    /// <summary>Fixed sub-menu entries; null for a plain entry.</summary>
    public List<MenuEntry>? Children { get; set; }

    /// <summary>Sub-menu entries produced the first time the sub-menu opens; appended after <see cref="Children"/>.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<MenuEntry>>>? LoadChildren { get; set; }

    public bool IsSeparator => Kind == MenuEntryKind.Separator;

    public bool HasSubmenu => Children is not null || LoadChildren is not null;

    public static MenuEntry Divider(MenuScope scopes = MenuScope.Anywhere) => new() { Kind = MenuEntryKind.Separator, Scopes = scopes };

    public override string ToString() => IsSeparator ? "——" : Label;
}

/// <summary>
/// Turns a command into a <see cref="MenuEntry"/>. Unless told otherwise an entry is shown exactly when its command can
/// run; <see cref="Show"/> forces the decision (true keeps a disabled entry visible, such as Paste in the strip).
/// </summary>
public readonly record struct CommandEntry(ICommand Command, string Label, string? Glyph)
{
    public bool? Show { get; init; }

    public object? Parameter { get; init; }

    public string? GestureId { get; init; }

    public bool InStrip { get; init; }

    public bool ShiftOnly { get; init; }

    public bool SingleOnly { get; init; }

    public MenuScope Scopes { get; init; } = MenuScope.Local;

    public MenuEntryKind Kind { get; init; }

    public bool IsChecked { get; init; }

    public string? Slot { get; init; }

    public MenuEntry Build()
    {
        if (Show == false)
            return MenuEntry.Omitted;

        var runnable = Command.CanExecute(Parameter);
        if (Show is null && !runnable)
            return MenuEntry.Omitted;

        return new MenuEntry
        {
            Kind = Kind,
            Label = Label,
            Glyph = Glyph,
            Command = Command,
            Parameter = Parameter,
            GestureId = GestureId,
            IsEnabled = runnable,
            IsChecked = IsChecked,
            InStrip = InStrip,
            ShiftOnly = ShiftOnly,
            SingleOnly = SingleOnly,
            Scopes = Scopes,
            Slot = Slot,
        };
    }
}
