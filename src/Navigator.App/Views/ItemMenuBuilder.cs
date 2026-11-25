using System.Collections.Frozen;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Microsoft.Extensions.Logging;
using Navigator.App.Controls;
using Navigator.Core.Archives;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Presentation.Commands;
using Navigator.Presentation.Items;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Services;

namespace Navigator.App.Views;

/// <summary>
/// Turns an <see cref="ItemMenuLayout"/> into an Avalonia context menu: an icon strip on top, then the planned entries.
/// Sub-menus whose content costs anything to find (applications for "Open with", "Send to" destinations, tags) are
/// filled the first time they open, so a right-click only pays for what the user actually looks at.
/// </summary>
internal sealed class ItemMenuBuilder(PaneServices services, ILogger logger)
{
    private const double GlyphSize = 16;

    /// <summary>Static look of each entry: text key, glyph and keyboard command id (for the gesture hint).</summary>
    private static readonly FrozenDictionary<ItemMenuAction, (string TextKey, string Glyph, string? GestureId)> Looks =
        new Dictionary<ItemMenuAction, (string, string, string?)>
        {
            [ItemMenuAction.Cut] = ("Command.Cut", "Cut", "item.cut"),
            [ItemMenuAction.Copy] = ("Command.Copy", "Copy", "item.copy"),
            [ItemMenuAction.Paste] = ("Command.Paste", "Paste", "item.paste"),
            [ItemMenuAction.Rename] = ("Command.Rename", "Rename", "item.rename"),
            [ItemMenuAction.Delete] = ("Command.Delete", "Delete", "item.delete"),
            [ItemMenuAction.Properties] = ("Command.Properties", "Properties", "item.properties"),
            [ItemMenuAction.Open] = ("Command.Open", "Forward", null),
            [ItemMenuAction.OpenInNewTab] = ("Command.OpenInNewTab", "Add", null),
            [ItemMenuAction.OpenInNewWindow] = ("Command.OpenInNewWindow", "Window", null),
            [ItemMenuAction.OpenWith] = ("Command.OpenWithMenu", "OpenWith", null),
            [ItemMenuAction.RunAsAdministrator] = ("Command.RunAsAdministrator", "Shield", null),
            [ItemMenuAction.RunAsOtherUser] = ("Command.RunAsOtherUser", "UserSwitch", null),
            [ItemMenuAction.ExtractMenu] = ("Command.ExtractMenu", "Extract", null),
            [ItemMenuAction.ExtractHere] = ("Command.ExtractHere", "Extract", "item.extract"),
            [ItemMenuAction.ExtractToFolder] = ("Command.ExtractToFolder", "FolderAdd", null),
            [ItemMenuAction.CompressMenu] = ("Command.CompressMenu", "Archive", null),
            [ItemMenuAction.CompressTogether] = ("Command.CompressTo", "Archive", "item.compress"),
            [ItemMenuAction.CompressEach] = ("Command.CompressEach", "Multiple", null),
            [ItemMenuAction.PasteIntoFolder] = ("Command.PasteInto", "Paste", null),
            [ItemMenuAction.CopyPath] = ("Command.CopyPath", "Copy", "item.copyPath"),
            [ItemMenuAction.CreateFolderWithSelection] = ("Command.CreateFolderWithSelection", "FolderAdd", null),
            [ItemMenuAction.CreateShortcut] = ("Command.CreateShortcut", "Shortcut", null),
            [ItemMenuAction.SendTo] = ("Command.SendTo", "SendTo", null),
            [ItemMenuAction.Tags] = ("Command.Tags", "Tag", null),
            [ItemMenuAction.PinToSidebar] = ("Command.PinToSidebar", "Pin", null),
            [ItemMenuAction.MoreMenu] = ("Command.More", "More", null),
            [ItemMenuAction.Duplicate] = ("Command.Duplicate", "Multiple", "item.duplicate"),
            [ItemMenuAction.OpenInTerminal] = ("Command.OpenTerminal", "Terminal", null),
            [ItemMenuAction.OpenInCodeEditor] = ("Command.OpenInCodeEditor", "Code", null),
            [ItemMenuAction.RevealInSystem] = ("Command.RevealInSystem", "OpenWith", null),
            [ItemMenuAction.DeletePermanently] = ("Command.DeletePermanently", "Delete", null),
        }.ToFrozenDictionary();

    private Localizer Text => services.Localizer;

    public ContextMenu Build(PaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        var traits = pane.DescribeSelection();
        var layout = ItemMenuPlanner.Plan(traits, pane.GetItemMenuOptions());
        var menu = new ContextMenu();

        if (layout.QuickActions.Count > 0)
        {
            menu.Items.Add(BuildStrip(menu, pane, layout.QuickActions));
            menu.Items.Add(new Separator());
        }

        foreach (var node in layout.Entries)
            menu.Items.Add(Realize(pane, node));
        return menu;
    }

    // ── Quick-action strip ──

    private MenuItem BuildStrip(ContextMenu menu, PaneViewModel pane, IReadOnlyList<ItemMenuAction> actions)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        var multiple = pane.Selection.Count > 1;
        Button? paste = null;
        foreach (var action in actions)
        {
            var look = Looks[action];
            var textKey = action == ItemMenuAction.Rename && multiple ? "Command.BulkRename" : look.TextKey;
            var button = new Button
            {
                Content = new IconView { Kind = look.Glyph, Width = GlyphSize, Height = GlyphSize },
                Command = CommandFor(pane, action),
            };
            button.Classes.Add("subtle");
            button.Classes.Add("icon");
            AutomationName(button, Text[textKey]);
            ToolTip.SetTip(button, GestureText(look.GestureId) is { } gesture ? $"{Text[textKey]} ({gesture})" : Text[textKey]);
            button.Click += (_, _) => menu.Close();
            row.Children.Add(button);
            if (action == ItemMenuAction.Paste)
                paste = button;
        }

        if (paste is not null)
            DisablePasteWhenClipboardEmpty(paste);

        // The host row never reacts itself: only its buttons do (hover and press styles in Controls.axaml).
        var host = new MenuItem { Header = row, StaysOpenOnClick = true, Focusable = false };
        host.Classes.Add("quickactions");
        return host;
    }

    /// <summary>Greys Paste out once the clipboard turns out to hold no files (read asynchronously; the menu opens at once).</summary>
    private async void DisablePasteWhenClipboardEmpty(Button paste)
    {
        try
        {
            var content = await services.Clipboard.GetFilesAsync().ConfigureAwait(true);
            if (content is not { } files || files.Paths.Count == 0)
                paste.IsEnabled = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or TimeoutException)
        {
            // An unreadable clipboard leaves Paste enabled; the paste itself reports the problem.
            logger.LogDebug(ex, "Clipboard probe for the context menu failed");
        }
    }

    // ── Entries ──

    private Control Realize(PaneViewModel pane, ItemMenuNode node)
    {
        switch (node.Action)
        {
            case ItemMenuAction.Separator:
                return new Separator();
            case ItemMenuAction.OpenWith:
                return Deferred(node.Action, item => FillOpenWithAsync(item, pane));
            case ItemMenuAction.SendTo:
                return Deferred(node.Action, item => FillSendTo(item, pane));
            case ItemMenuAction.Tags:
                return Deferred(node.Action, item => FillTags(item, pane));
        }

        var entry = Entry(node.Action, HeaderFor(pane, node.Action), CommandFor(pane, node.Action));
        if (node.Children is { Count: > 0 } children)
        {
            foreach (var nested in children)
                entry.Items.Add(Realize(pane, nested));
        }

        return entry;
    }

    private MenuItem Entry(ItemMenuAction action, string header, ICommand? command, object? parameter = null)
    {
        var look = Looks[action];
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            CommandParameter = parameter,
            Icon = new IconView { Kind = look.Glyph, Width = GlyphSize, Height = GlyphSize },
        };

        if (GestureText(look.GestureId) is { } gesture)
        {
            try
            {
                item.InputGesture = KeyGesture.Parse(gesture.Replace("Cmd", "Meta", StringComparison.Ordinal));
            }
            catch (ArgumentException ex)
            {
                logger.LogDebug(ex, "Unparsable gesture {Gesture} for {Action}", gesture, action);
            }
        }

        return item;
    }

    /// <summary>Headers that name the item they act on ("Extract to “photos”", "Compress to “photos.zip”").</summary>
    private string HeaderFor(PaneViewModel pane, ItemMenuAction action)
    {
        var selected = pane.Selection.SelectedItems;
        switch (action)
        {
            case ItemMenuAction.Rename when selected.Count > 1:
                return Text["Command.BulkRename"];
            case ItemMenuAction.ExtractToFolder:
                return selected is [var archive]
                    ? Text.Format("Command.ExtractToFolder", ArchiveService.ArchiveStem(archive.Name))
                    : Text["Command.ExtractEachToFolder"];
            case ItemMenuAction.CompressTogether:
                var stem = selected is [var only]
                    ? PathUtil.SplitName(only.Name, only.IsDirectory).Stem
                    : Path.GetFileName(Path.GetDirectoryName(selected[0].FullPath));
                return Text.Format("Command.CompressTo", (string.IsNullOrEmpty(stem) ? "Archive" : stem) + ".zip");
            default:
                return Text[Looks[action].TextKey];
        }
    }

    private static ICommand? CommandFor(PaneViewModel pane, ItemMenuAction action) => action switch
    {
        ItemMenuAction.Cut => pane.CutCommand,
        ItemMenuAction.Copy => pane.CopyCommand,
        ItemMenuAction.Paste => pane.PasteCommand,
        ItemMenuAction.Rename => pane.RenameCommand,
        ItemMenuAction.Delete => pane.DeleteCommand,
        ItemMenuAction.Properties => pane.PropertiesCommand,
        ItemMenuAction.Open => pane.OpenSelectedCommand,
        ItemMenuAction.OpenInNewTab => pane.OpenSelectedInNewTabCommand,
        ItemMenuAction.OpenInNewWindow => pane.OpenSelectedInNewWindowCommand,
        ItemMenuAction.RunAsAdministrator => pane.RunAsAdministratorCommand,
        ItemMenuAction.RunAsOtherUser => pane.RunAsOtherUserCommand,
        ItemMenuAction.ExtractHere => pane.ExtractCommand,
        ItemMenuAction.ExtractToFolder => pane.ExtractToFolderCommand,
        ItemMenuAction.CompressTogether => pane.CompressCommand,
        ItemMenuAction.CompressEach => pane.CompressEachCommand,
        ItemMenuAction.PasteIntoFolder => pane.PasteIntoFolderCommand,
        ItemMenuAction.CopyPath => pane.CopyPathCommand,
        ItemMenuAction.CreateFolderWithSelection => pane.CreateFolderWithSelectionCommand,
        ItemMenuAction.CreateShortcut => pane.CreateShortcutCommand,
        ItemMenuAction.PinToSidebar => pane.PinSelectedCommand,
        ItemMenuAction.Duplicate => pane.DuplicateCommand,
        ItemMenuAction.OpenInTerminal => pane.OpenTerminalCommand,
        ItemMenuAction.OpenInCodeEditor => pane.OpenSelectionInCodeEditorCommand,
        ItemMenuAction.RevealInSystem => pane.RevealInSystemCommand,
        ItemMenuAction.DeletePermanently => pane.DeletePermanentlyCommand,
        _ => null,
    };

    // ── Sub-menus filled on first open ──

    /// <summary>A sub-menu whose items are produced by <paramref name="fill"/> the first time it opens.</summary>
    private MenuItem Deferred(ItemMenuAction action, Func<MenuItem, Task> fill)
    {
        var host = Entry(action, Text[Looks[action].TextKey], command: null);

        // A placeholder child is what makes Avalonia draw the chevron and open a sub-menu at all.
        host.Items.Add(new MenuItem { Header = Text["Menu.Loading"], IsEnabled = false });
        var filled = false;
        host.SubmenuOpened += async (_, e) =>
        {
            // SubmenuOpened bubbles: ignore nested sub-menus, and fill only once.
            if (filled || !ReferenceEquals(e.Source, host))
                return;
            filled = true;
            try
            {
                await fill(host).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Filling the {Action} sub-menu failed", action);
                host.Items.Clear();
                host.Items.Add(new MenuItem { Header = Text["Error.OperationTitle"], IsEnabled = false });
            }
        };
        return host;
    }

    private Task FillSendTo(MenuItem host, PaneViewModel pane)
    {
        host.Items.Clear();
        var targets = pane.GetSendToTargets();
        foreach (var target in targets)
        {
            host.Items.Add(new MenuItem
            {
                Header = target.Label,
                Command = pane.SendToCommand,
                CommandParameter = target.Path,
                Icon = new IconView { Kind = target.IconKey, Width = GlyphSize, Height = GlyphSize },
            });
        }

        if (targets.Count == 0)
            host.Items.Add(new MenuItem { Header = Text["SendTo.NoDestinations"], IsEnabled = false });
        return Task.CompletedTask;
    }

    private Task FillTags(MenuItem host, PaneViewModel pane)
    {
        host.Items.Clear();
        var selected = pane.Selection.SelectedItems;
        var tags = services.Settings.Current.Tags;
        foreach (var tag in tags)
        {
            var tagId = tag.Id;
            host.Items.Add(new MenuItem
            {
                Header = tag.Name,
                Command = pane.ToggleTagCommand,
                CommandParameter = tagId,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = AllCarry(selected, tagId),
            });
        }

        if (tags.Count == 0)
            host.Items.Add(new MenuItem { Header = Text["Tags.NoneDefined"], IsEnabled = false });
        return Task.CompletedTask;
    }

    private static bool AllCarry(IReadOnlyList<FileItemViewModel> items, string tagId)
    {
        foreach (var item in items)
        {
            var found = false;
            foreach (var chip in item.Tags)
            {
                if (chip.Id == tagId)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return items.Count > 0;
    }

    /// <summary>Registered applications for the file (where the OS can list them), then "Choose another application…".</summary>
    private async Task FillOpenWithAsync(MenuItem host, PaneViewModel pane)
    {
        if (pane.Selection.SelectedItems is not [var file])
            return;

        var path = file.FullPath;
        var handlers = await services.Launcher.GetHandlersAsync(path).ConfigureAwait(true);
        host.Items.Clear();
        foreach (var handler in handlers)
        {
            var chosen = handler;
            var entry = new MenuItem { Header = handler.IsDefault ? $"{handler.DisplayName} ({Text["OpenWith.Default"]})" : handler.DisplayName };
            entry.Click += async (_, _) => await LaunchWithAsync(path, chosen).ConfigureAwait(true);
            host.Items.Add(entry);
        }

        if (handlers.Count > 0)
            host.Items.Add(new Separator());
        var choose = new MenuItem { Header = Text["OpenWith.Choose"] };
        choose.Click += async (_, _) => await ChooseApplicationAsync(path).ConfigureAwait(true);
        host.Items.Add(choose);
    }

    /// <summary>The OS chooser where there is one (Windows), otherwise a file picker for the program.</summary>
    public async Task ChooseApplicationAsync(string path)
    {
        if (await services.Launcher.ShowOpenWithDialogAsync(path).ConfigureAwait(true))
            return;
        var application = await services.Dialogs.PickFileAsync(Text["OpenWith.Choose"]).ConfigureAwait(true);
        if (application is not null)
            await LaunchWithAsync(path, new AppHandler(application, Path.GetFileName(application), null, false)).ConfigureAwait(true);
    }

    private async Task LaunchWithAsync(string path, AppHandler handler)
    {
        if (await services.Launcher.OpenWithAsync(path, handler).ConfigureAwait(true))
            services.Recent.Add(path);
        else
            await services.Dialogs.ShowMessageAsync(Text["Error.OpenFailedTitle"], Text.Format("Error.OpenFailed", Path.GetFileName(path))).ConfigureAwait(true);
    }

    // ── Helpers ──

    private string? GestureText(string? commandId)
    {
        if (commandId is null)
            return null;
        var gestures = CommandRegistry.GesturesFor(commandId, services.Settings.Current, OperatingSystem.IsMacOS());
        return gestures.Count > 0 ? gestures[0] : null;
    }

    private static void AutomationName(Control control, string name)
        => Avalonia.Automation.AutomationProperties.SetName(control, name);
}
