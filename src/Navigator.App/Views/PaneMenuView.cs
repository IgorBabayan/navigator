using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Microsoft.Extensions.Logging;
using Navigator.App.Controls;
using Navigator.Presentation.Commands;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Menus;
using Navigator.Presentation.Services;

namespace Navigator.App.Views;

/// <summary>
/// Draws a <see cref="PaneMenu"/> as an Avalonia context menu: the strip entries become a row of icon buttons on top,
/// the rest become rows. Sub-menus that are produced on demand (applications, "Send to", the shell's own entries) fill
/// the first time they open. Closing the menu releases the composed menu and every bitmap it created.
/// </summary>
internal sealed class PaneMenuView(PaneServices services, ILogger logger)
{
    private const double GlyphSize = 16;

    private Localizer Text => services.Localizer;

    public ContextMenu Render(PaneMenu model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var menu = new ContextMenu();
        var pictures = new List<Bitmap>();

        var stripEntries = model.Entries.Where(e => e.InStrip).ToList();
        var rows = model.Entries.Where(e => !e.InStrip).ToList();
        if (stripEntries.Count > 0)
        {
            menu.Items.Add(Strip(menu, stripEntries));
            if (rows.Count > 0)
                menu.Items.Add(new Separator());
        }

        Control? moreOptions = null;
        foreach (var entry in rows)
        {
            var row = Row(entry, pictures, model.Lifetime);
            if (entry.Slot == MenuSlots.MoreOptions)
                moreOptions = row;
            menu.Items.Add(row);
        }

        if (model.LateEntries is { } late)
            AddLateEntries(menu, late, moreOptions, pictures, model.Lifetime);

        menu.Closed += (_, _) =>
        {
            model.Dispose();
            foreach (var picture in pictures)
                picture.Dispose();
        };
        return menu;
    }

    /// <summary>
    /// Plugin entries (Windows 11 app commands, Git…) join the open menu above "Show more options" as soon as they arrive;
    /// entries that are already there (cached) are placed before the menu is first drawn.
    /// </summary>
    private async void AddLateEntries(ContextMenu menu, Task<IReadOnlyList<MenuEntry>> late, Control? anchor, List<Bitmap> pictures, CancellationToken lifetime)
    {
        IReadOnlyList<MenuEntry> entries;
        try
        {
            // Already finished (cached results): the continuation runs right away, before the menu is first drawn.
            entries = await late.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Plugin menu entries were not added");
            return;
        }

        if (lifetime.IsCancellationRequested || entries.Count == 0)
            return;

        var index = anchor is null ? menu.Items.Count : menu.Items.IndexOf(anchor);
        if (index < 0)
            index = menu.Items.Count;
        // The separator in front of "Show more options" stays where it is; the plugin entries get their own above it.
        if (index > 0 && menu.Items[index - 1] is Separator)
            index--;
        var rows = new List<Control>(entries.Count + 1);
        if (index > 0 && menu.Items[index - 1] is not Separator)
            rows.Add(new Separator());
        foreach (var entry in entries)
            rows.Add(Row(entry, pictures, lifetime));
        if (index < menu.Items.Count && menu.Items[index] is not Separator)
            rows.Add(new Separator());
        foreach (var row in rows)
            menu.Items.Insert(index++, row);
    }

    /// <summary>A menu dropped down from a toolbar button (the command bar's "New").</summary>
    public void OpenBelow(PaneMenu model, Control anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var menu = Render(model);
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.PlacementTarget = anchor;
        menu.Open(anchor);
    }

    // ── Icon strip ──

    private MenuItem Strip(ContextMenu menu, List<MenuEntry> entries)
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var action in entries)
        {
            var tool = new Button
            {
                // The strip's commands (Cut, Copy, Paste, Rename, Delete, Properties) use the OS's own pictures.
                Content = new IconView { Kind = action.Glyph, OsGlyph = action.Glyph, Width = GlyphSize, Height = GlyphSize },
                Command = action.Command,
                CommandParameter = action.Parameter,
            };
            if (!action.IsEnabled)
                tool.IsEnabled = false;
            tool.Classes.Add("subtle");
            tool.Classes.Add("icon");
            Avalonia.Automation.AutomationProperties.SetName(tool, action.Label);
            ToolTip.SetTip(tool, GestureText(action.GestureId) is { } keys ? $"{action.Label} ({keys})" : action.Label);
            tool.Click += (_, _) => menu.Close();
            tools.Children.Add(tool);
            if (action.GestureId == "item.paste" && action.IsEnabled)
                GreyOutWithoutFiles(tool);
        }

        // The row itself is inert; only its buttons react (styles: MenuItem.quickactions in Controls.axaml).
        var host = new MenuItem { Header = tools, StaysOpenOnClick = true, Focusable = false };
        host.Classes.Add("quickactions");
        return host;
    }

    /// <summary>Paste turns grey once the clipboard turns out to hold no files; read in the background so the menu opens at once.</summary>
    private async void GreyOutWithoutFiles(Button paste)
    {
        try
        {
            if (!await services.Clipboard.HasFilesAsync().ConfigureAwait(true))
                paste.IsEnabled = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or TimeoutException)
        {
            // Unreadable clipboard: Paste stays enabled and reports the problem itself if used.
            logger.LogDebug(ex, "Clipboard probe for the context menu failed");
        }
    }

    // ── Rows ──

    private Control Row(MenuEntry entry, List<Bitmap> pictures, CancellationToken lifetime)
    {
        if (entry.IsSeparator)
            return new Separator();

        var row = new MenuItem
        {
            Header = entry.Label,
            Command = entry.Command,
            CommandParameter = entry.Parameter,
            Icon = IconFor(entry, pictures),
        };
        if (!entry.IsEnabled)
            row.IsEnabled = false;
        if (entry.Kind is MenuEntryKind.Toggle or MenuEntryKind.Radio)
        {
            row.ToggleType = entry.Kind == MenuEntryKind.Radio ? MenuItemToggleType.Radio : MenuItemToggleType.CheckBox;
            row.IsChecked = entry.IsChecked;
        }

        if (GestureText(entry.GestureId) is { } keys)
        {
            try
            {
                row.InputGesture = KeyGesture.Parse(keys.Replace("Cmd", "Meta", StringComparison.Ordinal));
            }
            catch (ArgumentException unparsable)
            {
                // An unparsable custom gesture: the row just shows no shortcut.
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(unparsable, "Gesture {Gesture} of {Entry} could not be parsed", keys, entry.Label);
            }
        }

        if (entry.Children is { } fixedChildren)
        {
            foreach (var child in fixedChildren)
                row.Items.Add(Row(child, pictures, lifetime));
        }

        if (entry.LoadChildren is { } load)
            FillOnFirstOpen(row, load, pictures, lifetime);
        return row;
    }

    private void FillOnFirstOpen(MenuItem row, Func<CancellationToken, Task<IReadOnlyList<MenuEntry>>> load, List<Bitmap> pictures, CancellationToken lifetime)
    {
        // A trailing "Loading…" row is also what makes Avalonia treat the row as a sub-menu at all.
        var pending = new MenuItem { Header = Text["Menu.Loading"], IsEnabled = false };
        row.Items.Add(pending);
        var started = false;
        row.SubmenuOpened += async (_, e) =>
        {
            // The event bubbles up from nested sub-menus; only this row's own opening counts, and only once.
            if (started || !ReferenceEquals(e.Source, row))
                return;
            started = true;
            IReadOnlyList<MenuEntry> loaded;
            try
            {
                loaded = await load(lifetime).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return; // the menu closed first
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    logger.LogWarning(ex, "Filling the {Menu} sub-menu failed", row.Header);
                loaded = [];
            }

            if (lifetime.IsCancellationRequested)
                return;
            row.Items.Remove(pending);
            if (loaded.Count > 0 && row.Items.Count > 0)
                row.Items.Add(new Separator());
            foreach (var entry in loaded)
                row.Items.Add(Row(entry, pictures, lifetime));
            if (row.Items.Count == 0)
                row.Items.Add(new MenuItem { Header = Text["Menu.Empty"], IsEnabled = false });
        };
    }

    private static Control? IconFor(MenuEntry entry, List<Bitmap> pictures)
    {
        if (entry.Picture is { } picture)
        {
            var bitmap = SystemIcons.ToBitmap(picture, 32);
            pictures.Add(bitmap);
            return new Image { Source = bitmap, Width = GlyphSize, Height = GlyphSize };
        }

        if (entry.Glyph is null && entry.SystemIcon is null)
            return null;
        var glyph = new IconView { Kind = entry.Glyph, SystemIcon = entry.SystemIcon, Width = GlyphSize, Height = GlyphSize };
        if (entry.Tint is { } tint && Color.TryParse(tint, out var color))
            glyph.Foreground = new ImmutableSolidColorBrush(color); // tags show in their own color
        return glyph;
    }

    private string? GestureText(string? commandId)
    {
        if (commandId is null)
            return null;
        var gestures = CommandRegistry.GesturesFor(commandId, services.Settings.Current, OperatingSystem.IsMacOS());
        return gestures.Count > 0 ? gestures[0] : null;
    }
}
