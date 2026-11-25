using System.Windows.Input;
using Navigator.Core.Settings;
using Navigator.Core.Sorting;
using Navigator.Presentation.Localization;

namespace Navigator.Presentation.Menus;

/// <summary>The commands a pane exposes to its context menus.</summary>
public sealed record PaneMenuCommands
{
    public required ICommand Open { get; init; }
    public required ICommand OpenInNewTab { get; init; }
    public required ICommand OpenInNewWindow { get; init; }
    public required ICommand OpenInOtherPane { get; init; }
    public required ICommand OpenFileLocation { get; init; }
    public required ICommand RunAsAdministrator { get; init; }
    public required ICommand RunAsOtherUser { get; init; }
    public required ICommand Cut { get; init; }
    public required ICommand Copy { get; init; }
    public required ICommand Paste { get; init; }
    public required ICommand PasteIntoFolder { get; init; }
    public required ICommand CopyPath { get; init; }
    public required ICommand CreateFolderWithSelection { get; init; }
    public required ICommand CreateShortcut { get; init; }
    public required ICommand Rename { get; init; }
    public required ICommand Delete { get; init; }
    public required ICommand DeletePermanently { get; init; }
    public required ICommand Properties { get; init; }
    public required ICommand Restore { get; init; }
    public required ICommand Download { get; init; }
    public required ICommand Upload { get; init; }
    public required ICommand TogglePin { get; init; }
    public required ICommand Compress { get; init; }
    public required ICommand CompressEach { get; init; }
    public required ICommand ExtractFiles { get; init; }
    public required ICommand ExtractSmart { get; init; }
    public required ICommand ExtractHere { get; init; }
    public required ICommand ExtractToFolder { get; init; }
    public required ICommand OpenInCodeEditor { get; init; }
    public required ICommand OpenTerminal { get; init; }
    public required ICommand ToggleTag { get; init; }
    public required ICommand RemoveTag { get; init; }
    public required ICommand Duplicate { get; init; }
    public required ICommand RevealInSystem { get; init; }
    public required ICommand SetLayout { get; init; }
    public required ICommand SortBy { get; init; }
    public required ICommand ToggleSortDirection { get; init; }
    public required ICommand SetGroupBy { get; init; }
    public required ICommand Refresh { get; init; }
    public required ICommand NewFolder { get; init; }
    public required ICommand NewFile { get; init; }
    public required ICommand EmptyTrash { get; init; }
    public required ICommand SelectAll { get; init; }
}

/// <summary>
/// A tag as the "Edit tags" sub-menu shows it: checked when every selected item carries it, present when at least one
/// does (then "Clear tag(s)" offers to take it off).
/// </summary>
public readonly record struct TagChoice(string Id, string Name, string Color, bool IsChecked, bool IsPresent);

/// <summary>The current view settings the background menu marks with radio checks.</summary>
public readonly record struct ViewState(FolderLayout Layout, SortColumn SortColumn, bool SortDescending, GroupBy GroupBy);

/// <summary>Everything that decides a pane menu: where, what is selected, settings, and the pane's commands.</summary>
public sealed record PaneMenuInput
{
    public required Localizer Text { get; init; }
    public required PaneMenuCommands Commands { get; init; }
    public required GeneralSettings Settings { get; init; }
    public MenuScope Scope { get; init; } = MenuScope.Folder;
    public SelectionTraits Selection { get; init; }
    public bool ShiftHeld { get; init; }

    /// <summary>Every selected folder is pinned (for items) / the shown folder is pinned (background).</summary>
    public bool IsPinned { get; init; }

    /// <summary>The folder shown is a writable file-system folder (new items, paste).</summary>
    public bool CanCreate { get; init; }

    /// <summary>Header of "Compress to …" ("photos.zip").</summary>
    public string ArchiveName { get; init; } = "Archive.zip";

    /// <summary>Header part of "Extract to …" for a single archive ("photos"); null for several.</summary>
    public string? ExtractFolderName { get; init; }

    public IReadOnlyList<TagChoice> Tags { get; init; } = [];

    public ViewState View { get; init; }

    /// <summary>Applications for "Open with" (shell or registered handlers); asked when the sub-menu opens.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<MenuEntry>>>? OpenWithEntries { get; init; }

    /// <summary>"Send to" destinations; asked when the sub-menu opens.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<MenuEntry>>>? SendToEntries { get; init; }

    /// <summary>The operating system's own entries for "Show more options"; null where there are none.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<MenuEntry>>>? ShellEntries { get; init; }
}

/// <summary>
/// Composes the pane context menus the way the reference app structures them: one declarative list of every entry a
/// selection could get, each carrying its own visibility rules, then <see cref="Refine"/> drops what does not apply here,
/// moves Shift-only entries under "Show more options" and tidies separators. The strip entries (<see cref="MenuEntry.InStrip"/>)
/// are part of the same list; the renderer lifts them into the icon row.
/// </summary>
public static class PaneMenuComposer
{
    private const MenuScope LocalOrRemote = MenuScope.Local | MenuScope.Remote;

    /// <summary>Menu for a right-click on selected items.</summary>
    public static List<MenuEntry> ForItems(PaneMenuInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Text;
        var run = input.Commands;
        var set = input.Settings;
        var picked = input.Selection;

        var entries = new List<MenuEntry>
        {
            new CommandEntry(run.Restore, text["Command.Restore"], "Restore") { Scopes = MenuScope.Trash }.Build(),
            new CommandEntry(run.Open, text["Command.Open"], "Forward") { Scopes = LocalOrRemote }.Build(),
            OpenWith(input),
            new CommandEntry(run.OpenFileLocation, text["Command.OpenFileLocation"], "Folder") { Scopes = MenuScope.Search | MenuScope.Tag, SingleOnly = true }.Build(),
            new CommandEntry(run.OpenInNewTab, text["Command.OpenInNewTab"], "Add")
            {
                Show = set.ShowOpenInNewTabInContextMenu && picked.HasFolders ? null : false,
                Scopes = LocalOrRemote,
            }.Build(),
            new CommandEntry(run.OpenInNewWindow, text["Command.OpenInNewWindow"], "Window")
            {
                Show = set.ShowOpenInNewWindowInContextMenu && picked.HasFolders ? null : false,
            }.Build(),
            new CommandEntry(run.OpenInOtherPane, text["Command.OpenInOtherPane"], "SplitView")
            {
                Show = set.ShowOpenInOtherPaneInContextMenu && picked.OnlyFolders ? null : false,
                SingleOnly = true,
            }.Build(),
            new CommandEntry(run.RunAsAdministrator, text["Command.RunAsAdministrator"], "Shield")
            {
                Show = picked.IsElevatableProgram && picked.Elevation.HasFlag(Core.Platform.ElevationOptions.Administrator) ? null : false,
            }.Build(),
            new CommandEntry(run.RunAsOtherUser, text["Command.RunAsOtherUser"], "UserSwitch")
            {
                Show = picked.IsElevatableProgram && picked.Elevation.HasFlag(Core.Platform.ElevationOptions.OtherUser) ? null : false,
            }.Build(),
            MenuEntry.Divider(),

            // Strip: always present (disabled when not applicable) so the row keeps its shape.
            new CommandEntry(run.Cut, text["Command.Cut"], "Cut") { InStrip = true, Show = true, GestureId = "item.cut" }.Build(),
            new CommandEntry(run.Copy, text["Command.Copy"], "Copy") { InStrip = true, Show = true, GestureId = "item.copy" }.Build(),
            picked.IsSingle && picked.OnlyFolders
                ? new CommandEntry(run.PasteIntoFolder, text["Command.PasteInto"], "Paste") { InStrip = true, Show = true, GestureId = "item.paste" }.Build()
                : new CommandEntry(run.Paste, text["Command.Paste"], "Paste") { InStrip = true, Show = true, GestureId = "item.paste" }.Build(),
            new CommandEntry(run.Rename, text[picked.Count > 1 ? "Command.BulkRename" : "Command.Rename"], "Rename")
            {
                InStrip = true, Show = true, GestureId = "item.rename", Scopes = LocalOrRemote,
            }.Build(),
            new CommandEntry(run.Delete, text["Command.Delete"], "Delete")
            {
                InStrip = true, Show = true, GestureId = "item.delete", Scopes = LocalOrRemote | MenuScope.Trash,
            }.Build(),
            new CommandEntry(run.Properties, text["Command.Properties"], "Properties")
            {
                InStrip = true, Show = true, GestureId = "item.properties", Scopes = MenuScope.Local | MenuScope.Trash,
            }.Build(),

            new CommandEntry(run.Download, text["Remote.Download"], "Download") { Scopes = MenuScope.Remote }.Build(),
            new CommandEntry(run.CopyPath, text["Command.CopyPath"], "Copy")
            {
                Show = set.ShowCopyPathInContextMenu ? null : false, GestureId = "item.copyPath", Scopes = LocalOrRemote,
            }.Build(),
            new CommandEntry(run.CreateFolderWithSelection, text["Command.CreateFolderWithSelection"], "FolderAdd")
            {
                Show = set.ShowCreateFolderWithSelectionInContextMenu ? null : false,
            }.Build(),
            new CommandEntry(run.CreateShortcut, text["Command.CreateShortcut"], "Shortcut")
            {
                Show = set.ShowCreateShortcutInContextMenu && !picked.IsSingleShortcut ? null : false,
                SingleOnly = true,
            }.Build(),
            new CommandEntry(run.TogglePin, text[input.IsPinned ? "Command.UnpinFromSidebar" : "Command.PinToSidebar"], "Pin")
            {
                Show = set.ShowPinToSidebarInContextMenu && picked.OnlyFolders ? null : false,
            }.Build(),
            CompressMenu(input),
            ExtractMenu(input),
            new MenuEntry
            {
                Label = text["Command.SendTo"],
                Glyph = "SendTo",
                Slot = MenuSlots.SendTo,
                IsWanted = set.ShowSendToInContextMenu && input.SendToEntries is not null,
                LoadChildren = input.SendToEntries,
            },
            new CommandEntry(run.OpenInCodeEditor, text["Command.OpenInCodeEditor"], "CodeEditor")
            {
                Show = picked.IsEditableText || (picked.IsSingle && picked.OnlyFolders) ? null : false,
                SingleOnly = true,
            }.Build(),
            MenuEntry.Divider(),
            new CommandEntry(run.OpenTerminal, text["Command.OpenTerminal"], "Terminal")
            {
                Show = set.ShowOpenTerminalInContextMenu && picked.OnlyFolders ? null : false,
            }.Build(),
            MenuEntry.Divider(),
            EditTags(input),

            // Kept for "Show more options" unless Shift is held.
            new CommandEntry(run.Duplicate, text["Command.Duplicate"], "Multiple") { ShiftOnly = true, GestureId = "item.duplicate" }.Build(),
            new CommandEntry(run.RevealInSystem, text["Command.RevealInSystem"], "OpenWith") { ShiftOnly = true }.Build(),
            new CommandEntry(run.DeletePermanently, text["Command.DeletePermanently"], "Delete") { ShiftOnly = true }.Build(),
            MenuEntry.Divider(),
            MoreOptions(input, MenuScope.Local),
        };

        return Refine(entries, input.Scope, picked.Count, input.ShiftHeld);
    }

    /// <summary>Menu for a right-click on the empty area of a pane.</summary>
    public static List<MenuEntry> ForBackground(PaneMenuInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Text;
        var run = input.Commands;
        var view = input.View;
        const MenuScope Browsable = MenuScope.Local | MenuScope.Trash | MenuScope.Remote;

        var layouts = new[] { FolderLayout.Details, FolderLayout.List, FolderLayout.Tiles, FolderLayout.MediumIcons, FolderLayout.LargeIcons };
        var layoutChoices = new List<MenuEntry>(layouts.Length);
        foreach (var layout in layouts)
            layoutChoices.Add(Choice(run.SetLayout, text["Layout." + layout], layout, view.Layout == layout, LayoutGlyph(layout)));

        var sortChoices = new List<MenuEntry>();
        foreach (var column in Enum.GetValues<SortColumn>())
            sortChoices.Add(Choice(run.SortBy, text["Sort." + column], column, view.SortColumn == column, PropertyGlyph(column.ToString())));
        sortChoices.Add(MenuEntry.Divider());
        sortChoices.Add(Direction(run.ToggleSortDirection, text["Sort.Ascending"], chosen: !view.SortDescending, "SortAscending"));
        sortChoices.Add(Direction(run.ToggleSortDirection, text["Sort.Descending"], chosen: view.SortDescending, "SortDescending"));

        var groupChoices = new List<MenuEntry>();
        foreach (var grouping in Enum.GetValues<GroupBy>())
            groupChoices.Add(Choice(run.SetGroupBy, text["GroupBy." + grouping], grouping, view.GroupBy == grouping, PropertyGlyph(grouping.ToString())));

        var entries = new List<MenuEntry>
        {
            new() { Label = text["Command.Layout"], Glyph = "LayoutGrid", Scopes = Browsable, Children = layoutChoices },
            new() { Label = text["Command.Sort"], Glyph = "Sort", Scopes = Browsable, Children = sortChoices },
            new() { Label = text["Command.Group"], Glyph = "Group", Scopes = Browsable, Children = groupChoices },
            new CommandEntry(run.Refresh, text["Command.Refresh"], "Refresh") { Scopes = Browsable, GestureId = "nav.refresh" }.Build(),
            MenuEntry.Divider(),
            new MenuEntry
            {
                Label = text["Command.New"],
                Glyph = "Add",
                Scopes = MenuScope.Folder,
                IsWanted = input.CanCreate,
                Children =
                [
                    new CommandEntry(run.NewFolder, text["Command.NewFolder"], "FolderAdd") { Show = true, GestureId = "item.newFolder" }.Build(),
                    new CommandEntry(run.NewFile, text["Command.NewFile"], "File") { Show = true, GestureId = "item.newFile" }.Build(),
                ],
            },
            new CommandEntry(run.NewFolder, text["Command.NewFolder"], "FolderAdd") { Scopes = MenuScope.Remote }.Build(),
            new CommandEntry(run.Upload, text["Remote.Upload"], "Upload") { Scopes = MenuScope.Remote }.Build(),
            new CommandEntry(run.EmptyTrash, text["Command.EmptyTrash"], "Delete") { Scopes = MenuScope.Trash }.Build(),
            new CommandEntry(run.Paste, text["Command.Paste"], "Paste") { InStrip = true, Show = input.CanCreate, GestureId = "item.paste", Scopes = MenuScope.Folder }.Build(),
            new CommandEntry(run.Properties, text["Command.Properties"], "Properties") { InStrip = true, Show = true, GestureId = "item.properties", Scopes = MenuScope.Folder }.Build(),
            new CommandEntry(run.OpenTerminal, text["Command.OpenTerminal"], "Terminal")
            {
                Show = input.Settings.ShowOpenTerminalInContextMenu ? null : false, Scopes = MenuScope.Folder,
            }.Build(),
            new CommandEntry(run.OpenInCodeEditor, text["Command.OpenInCodeEditor"], "CodeEditor") { Scopes = MenuScope.Folder }.Build(),
            new CommandEntry(run.TogglePin, text[input.IsPinned ? "Command.UnpinFromSidebar" : "Command.PinToSidebar"], "Pin")
            {
                Show = input.Settings.ShowPinToSidebarInContextMenu ? null : false, Scopes = MenuScope.Folder,
            }.Build(),
            new CommandEntry(run.SelectAll, text["Command.SelectAll"], "Check") { Scopes = Browsable, GestureId = "item.selectAll" }.Build(),
            MenuEntry.Divider(),
            MoreOptions(input, MenuScope.Folder),
        };

        return Refine(entries, input.Scope, selectionCount: 0, input.ShiftHeld);
    }

    /// <summary>
    /// Applies the rules every entry carries: drops unwanted entries, entries of other locations and single-item entries
    /// for several items; without Shift, moves <see cref="MenuEntry.ShiftOnly"/> entries to the top of "Show more
    /// options"; removes sub-menus left without content and leading, trailing or doubled separators (also inside sub-menus).
    /// </summary>
    public static List<MenuEntry> Refine(List<MenuEntry> entries, MenuScope scope, int selectionCount, bool shiftHeld)
    {
        ArgumentNullException.ThrowIfNull(entries);
        bool Applies(MenuEntry entry)
            => entry.IsWanted && (entry.Scopes & scope) != 0 && (!entry.SingleOnly || selectionCount == 1);

        var kept = new List<MenuEntry>(entries.Count);
        foreach (var candidate in entries)
        {
            if (!Applies(candidate))
                continue;
            if (candidate.Children is { } children)
                candidate.Children = children.FindAll(Applies);
            kept.Add(candidate);
        }

        var overflow = kept.Find(e => e.Slot == MenuSlots.MoreOptions);
        if (!shiftHeld && overflow is not null)
        {
            var moved = kept.FindAll(e => e.ShiftOnly);
            if (moved.Count > 0)
            {
                kept.RemoveAll(e => e.ShiftOnly);
                overflow.Children ??= [];
                if (overflow.Children.Count > 0)
                    moved.Add(MenuEntry.Divider());
                overflow.Children.InsertRange(0, moved);
            }
        }

        foreach (var entry in kept)
        {
            if (entry.Children is { } children)
                Tidy(children);
        }

        // A sub-menu with nothing fixed and nothing to load disappears (no shell entries, no built-ins left).
        kept.RemoveAll(e => e.Children is { Count: 0 } && e.LoadChildren is null);
        Tidy(kept);
        return kept;
    }

    /// <summary>No leading, trailing or consecutive separators.</summary>
    public static void Tidy(List<MenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var write = 0;
        for (var read = 0; read < entries.Count; read++)
        {
            var entry = entries[read];
            if (entry.IsSeparator && (write == 0 || entries[write - 1].IsSeparator))
                continue;
            entries[write++] = entry;
        }

        while (write > 0 && entries[write - 1].IsSeparator)
            write--;
        entries.RemoveRange(write, entries.Count - write);
    }

    private static MenuEntry OpenWith(PaneMenuInput input)
    {
        var picked = input.Selection;
        return new MenuEntry
        {
            Label = input.Text["Command.OpenWithMenu"],
            Glyph = "OpenWith",
            Slot = MenuSlots.OpenWith,
            SingleOnly = true,
            IsWanted = picked.IsSingle && picked.OnlyFiles && !picked.IsElevatableProgram && !picked.IsSingleShortcut && input.OpenWithEntries is not null,
            LoadChildren = input.OpenWithEntries,
        };
    }

    private static MenuEntry CompressMenu(PaneMenuInput input)
    {
        var text = input.Text;
        var run = input.Commands;
        return new MenuEntry
        {
            Label = text["Command.CompressMenu"],
            Glyph = "Compress",
            IsWanted = input.Settings.ShowCompressionInContextMenu && run.Compress.CanExecute(null),
            Children =
            [
                new CommandEntry(run.Compress, text.Format("Command.CompressTo", input.ArchiveName), "Compress") { GestureId = "item.compress" }.Build(),
                new CommandEntry(run.CompressEach, text["Command.CompressEach"], "Multiple").Build(),
            ],
        };
    }

    private static MenuEntry ExtractMenu(PaneMenuInput input)
    {
        var text = input.Text;
        var run = input.Commands;
        var picked = input.Selection;
        var toFolder = input.ExtractFolderName is { } name ? text.Format("Command.ExtractToFolder", name) : text["Command.ExtractEachToFolder"];
        return new MenuEntry
        {
            Label = text["Command.ExtractMenu"],
            Glyph = "Extract",
            IsWanted = input.Settings.ShowCompressionInContextMenu && picked.ArchiveCount > 0,
            Children =
            [
                new CommandEntry(run.ExtractFiles, text["Command.ExtractFiles"], "Extract").Build(),
                new CommandEntry(run.ExtractSmart, text["Command.ExtractHereSmart"], "Extract") { GestureId = "item.extract" }.Build(),
                new CommandEntry(run.ExtractHere, text["Command.ExtractHere"], "Extract").Build(),
                new CommandEntry(run.ExtractToFolder, toFolder, "FolderAdd") { Show = picked.OnlyArchives ? null : false }.Build(),
            ],
        };
    }

    private static MenuEntry EditTags(PaneMenuInput input)
    {
        var choices = new List<MenuEntry>(Math.Max(1, input.Tags.Count));
        foreach (var tag in input.Tags)
        {
            choices.Add(new MenuEntry
            {
                Kind = MenuEntryKind.Toggle,
                Label = tag.Name,
                Glyph = "Tag",
                Tint = tag.Color,
                Command = input.Commands.ToggleTag,
                Parameter = tag.Id,
                IsChecked = tag.IsChecked,
            });
        }

        if (choices.Count == 0)
            choices.Add(new MenuEntry { Label = input.Text["Tags.NoneDefined"], IsEnabled = false });
        choices.Add(MenuEntry.Divider());
        choices.Add(ClearTags(input));

        return new MenuEntry
        {
            Label = input.Text["Command.EditTags"],
            Glyph = "Tag",
            Slot = MenuSlots.Tags,
            IsWanted = input.Settings.ShowEditTagsInContextMenu,
            Children = choices,
        };
    }

    /// <summary>
    /// "Clear tag(s)": disabled when no selected item carries a tag; one entry removing the tag when exactly one tag is
    /// present; otherwise a sub-menu with one entry per present tag, each removing that tag from the selection.
    /// </summary>
    private static MenuEntry ClearTags(PaneMenuInput input)
    {
        var present = new List<TagChoice>();
        foreach (var tag in input.Tags)
        {
            if (tag.IsPresent)
                present.Add(tag);
        }

        var text = input.Text;
        var remove = input.Commands.RemoveTag;
        switch (present.Count)
        {
            case 0:
                return new MenuEntry { Label = text["Command.ClearTag"], Glyph = "TagRemove", IsEnabled = false };
            case 1:
                return new MenuEntry
                {
                    Label = text.Format("Command.ClearTagNamed", present[0].Name),
                    Glyph = "TagRemove",
                    Command = remove,
                    Parameter = present[0].Id,
                };
            default:
                var each = new List<MenuEntry>(present.Count);
                foreach (var tag in present)
                    each.Add(new MenuEntry { Label = tag.Name, Glyph = "Tag", Tint = tag.Color, Command = remove, Parameter = tag.Id });
                return new MenuEntry { Label = text["Command.ClearTags"], Glyph = "TagRemove", Children = each };
        }
    }

    /// <summary>"Show more options": Shift-only entries land here, the shell's own entries load into it when it opens.</summary>
    private static MenuEntry MoreOptions(PaneMenuInput input, MenuScope scopes) => new()
    {
        Label = input.Text["Command.ShowMoreOptions"],
        Glyph = "More",
        Slot = MenuSlots.MoreOptions,
        Scopes = scopes,
        Children = [],
        LoadChildren = input.ShellEntries,
    };

    private static MenuEntry Choice(ICommand command, string label, object value, bool chosen, string glyph) => new()
    {
        Kind = MenuEntryKind.Radio,
        Label = label,
        Glyph = glyph,
        Command = command,
        Parameter = value,
        IsChecked = chosen,
        Scopes = MenuScope.Anywhere,
    };

    /// <summary>Sort direction pair over a toggle command: the checked side does nothing, the other one toggles.</summary>
    private static MenuEntry Direction(ICommand toggle, string label, bool chosen, string glyph) => new()
    {
        Kind = MenuEntryKind.Radio,
        Label = label,
        Glyph = glyph,
        Command = chosen ? null : toggle,
        IsChecked = chosen,
        Scopes = MenuScope.Anywhere,
    };

    private static string LayoutGlyph(FolderLayout layout) => layout switch
    {
        FolderLayout.Details => "LayoutDetails",
        FolderLayout.List => "LayoutList",
        FolderLayout.Tiles => "LayoutTiles",
        FolderLayout.LargeIcons => "ViewGridOutline",
        _ => "LayoutGrid",
    };

    /// <summary>The same icons for sorting and grouping by a property (the enums share member names).</summary>
    private static string PropertyGlyph(string property) => property switch
    {
        "Name" => "SortAlphabeticalAscending",
        "DateModified" => "CalendarEdit",
        "DateCreated" => "CalendarPlus",
        "Type" => "Shape",
        "Size" => "Weight",
        "None" => "CloseCircleOutline",
        _ => "Sort",
    };
}
