using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
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

        foreach (var entry in rows)
            menu.Items.Add(Row(entry, model.Lifetime, pictures));

        menu.Closed += (_, _) =>
        {
            model.Dispose();
            foreach (var picture in pictures)
                picture.Dispose();
        };
        return menu;
    }

    // ── Icon strip ──

    private MenuItem Strip(ContextMenu menu, List<MenuEntry> entries)
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var action in entries)
        {
            var tool = new Button
            {
                Content = new IconView { Kind = action.Glyph, Width = GlyphSize, Height = GlyphSize },
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
            if (await services.Clipboard.GetFilesAsync().ConfigureAwait(true) is not { } content || content.Paths.Count == 0)
                paste.IsEnabled = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or TimeoutException)
        {
            // Unreadable clipboard: Paste stays enabled and reports the problem itself if used.
            logger.LogDebug(ex, "Clipboard probe for the context menu failed");
        }
    }

    // ── Rows ──

    private Control Row(MenuEntry entry, CancellationToken lifetime, List<Bitmap> pictures)
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
                row.Items.Add(Row(child, lifetime, pictures));
        }

        if (entry.LoadChildren is { } load)
            FillOnFirstOpen(row, load, lifetime, pictures);
        return row;
    }

    private void FillOnFirstOpen(MenuItem row, Func<CancellationToken, Task<IReadOnlyList<MenuEntry>>> load, CancellationToken lifetime, List<Bitmap> pictures)
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
                row.Items.Add(Row(entry, lifetime, pictures));
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

        return entry.Glyph is null ? null : new IconView { Kind = entry.Glyph, Width = GlyphSize, Height = GlyphSize };
    }

    private string? GestureText(string? commandId)
    {
        if (commandId is null)
            return null;
        var gestures = CommandRegistry.GesturesFor(commandId, services.Settings.Current, OperatingSystem.IsMacOS());
        return gestures.Count > 0 ? gestures[0] : null;
    }
}
