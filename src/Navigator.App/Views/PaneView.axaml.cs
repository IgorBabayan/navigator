using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Navigator.App.Views.Rows;
using Navigator.Core.Archives;
using Navigator.Core.IO;
using Navigator.Presentation.Items;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Services;
using Navigator.Presentation.Shell;

namespace Navigator.App.Views;

/// <summary>
/// File list interaction. All selection logic lives in <see cref="ItemSelection"/>; this view only translates
/// pointer/keyboard input into calls on it, draws the rubber band, and keeps the focused item scrolled into view.
/// </summary>
public sealed partial class PaneView : UserControl
{
    private const double DragThreshold = 6;
    private readonly PaneServices _services = App.Services.GetRequiredService<PaneServices>();
    private PaneViewModel? _pane;
    private Point? _bandStart;
    private IReadOnlyCollection<FileItemViewModel>? _bandBaseline;
    private FileItemViewModel? _pressedItem;
    private Point _pressPoint;
    private bool _dragging;
    private string _typeAhead = string.Empty;
    private long _typeAheadAt;

    public PaneView()
    {
        InitializeComponent();
        ItemsHost.ItemTemplate = new RowTemplates(this);
        Columns.Load(_services.Settings.Current.FilesAndFolders.Columns, _services.Settings.Current.FilesAndFolders.ShowFileTagsColumn);
        Columns.PropertyChanged += (_, _) => SaveColumns();

        foreach (var header in new[] { NameHeader, ModifiedHeader, TypeHeader, SizeHeader, CreatedHeader, TagsHeader })
        {
            header.SortRequested += (_, column) => _pane?.SortByCommand.Execute(column);
            header.Resized += (sender, delta) => ResizeColumn((ColumnHeader)sender!, delta);
        }

        DataContextChanged += (_, _) => Attach(DataContext as PaneViewModel);
        Scroller.SizeChanged += (_, e) => _pane?.SetViewportWidth(e.NewSize.Width);

        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        ItemsHost.PointerPressed += OnItemsPointerPressed;
        Scroller.PointerPressed += OnBackgroundPointerPressed;
        Scroller.PointerMoved += OnPointerMoved;
        Scroller.PointerReleased += OnPointerReleased;
        Scroller.DoubleTapped += OnBackgroundDoubleTapped;
        KeyDown += OnKeyDown;
        TextInput += OnTextInput;
    }

    public PaneViewModel? Pane => _pane;

    public ColumnLayout Columns { get; } = new();

    public bool ShowHeader
    {
        get => PaneHeader.IsVisible;
        set => PaneHeader.IsVisible = value;
    }

    private Localizer Text => _services.Localizer;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _services.Settings.Changed += OnSettingsChanged;
        ApplyCheckBoxes();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _services.Settings.Changed -= OnSettingsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSettingsChanged(object? sender, string section)
    {
        if (section is "filesAndFolders" or "*")
            Dispatcher.UIThread.Post(ApplyCheckBoxes);
    }

    /// <summary>Settings → Files and folders → "Show check boxes" (styles in PaneView.axaml key off this class).</summary>
    private void ApplyCheckBoxes() => Classes.Set("checkboxes", _services.Settings.Current.FilesAndFolders.ShowCheckBoxes);

    /// <summary>True when the press landed on an item's check box.</summary>
    private static bool IsOnCheckBox(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border border)
            {
                if (border.Classes.Contains("rowCheck"))
                    return true;
                if (border.Classes.Contains("item"))
                    return false;
            }
        }

        return false;
    }

    private void Attach(PaneViewModel? pane)
    {
        if (_pane is not null)
        {
            _pane.PropertyChanged -= OnPaneChanged;
            _pane.ScrollIntoViewRequested -= OnScrollIntoView;
            _pane.OpenWithRequested -= OnOpenWithRequested;
        }

        _pane = pane;
        if (pane is null)
            return;

        pane.PropertyChanged += OnPaneChanged;
        pane.ScrollIntoViewRequested += OnScrollIntoView;
        pane.OpenWithRequested += OnOpenWithRequested;
        pane.SetViewportWidth(Scroller.Bounds.Width > 0 ? Scroller.Bounds.Width : 800);
        UpdateSortGlyphs();
        UpdateActiveIndicator();
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.Sort):
            case nameof(PaneViewModel.SortColumn):
            case nameof(PaneViewModel.IsSortDescending):
                UpdateSortGlyphs();
                break;
            case nameof(PaneViewModel.IsActive):
                UpdateActiveIndicator();
                break;
            case nameof(PaneViewModel.Location):
                FilterBar.IsVisible = false;
                Scroller.Offset = default;
                break;
        }
    }

    private void UpdateSortGlyphs()
    {
        if (_pane is null)
            return;
        foreach (var header in new[] { NameHeader, ModifiedHeader, TypeHeader, SizeHeader, CreatedHeader, TagsHeader })
            header.ShowSort(_pane.Sort);
    }

    private void UpdateActiveIndicator() => PaneHeader.Classes.Set("active", _pane?.IsActive == true);

    public void FocusFilter()
    {
        FilterBar.IsVisible = true;
        Dispatcher.UIThread.Post(() => FilterBox.Focus());
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            OnCloseFilter(sender, e);
    }

    private void OnCloseFilter(object? sender, RoutedEventArgs e)
    {
        if (_pane is not null)
            _pane.FilterText = string.Empty;
        FilterBar.IsVisible = false;
        Focus();
    }

    private void ResizeColumn(ColumnHeader header, double delta)
    {
        if (header == NameHeader)
            Columns.NameWidth += delta;
        else if (header == ModifiedHeader)
            Columns.ModifiedWidth += delta;
        else if (header == TypeHeader)
            Columns.TypeWidth += delta;
        else if (header == SizeHeader)
            Columns.SizeWidth += delta;
        else if (header == CreatedHeader)
            Columns.CreatedWidth += delta;
        else if (header == TagsHeader)
            Columns.TagsWidth += delta;
    }

    private void SaveColumns() => _services.Settings.Update("columns", s => s.FilesAndFolders.Columns = Columns.ToSettings());

    // ── Pointer: activation, selection, rubber band, drag & drop ──

    /// <summary>Any click inside a pane makes it the active pane of its tab (split view).</summary>
    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (_pane is not null && this.FindAncestorOfType<TabContentView>()?.DataContext is TabViewModel tab)
            tab.ActivePane = _pane;
    }

    private static FileItemViewModel? ItemFromSource(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border { DataContext: FileItemViewModel item } border && border.Classes.Contains("item"))
                return item;
            if (visual is PaneView)
                return null;
        }

        return null;
    }

    private void OnItemsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_pane is null)
            return;
        var item = ItemFromSource(e.Source);
        if (item is null)
            return;

        Focus();
        var point = e.GetCurrentPoint(this);
        var control = e.KeyModifiers.HasFlag(PrimaryModifier);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (point.Properties.IsRightButtonPressed)
        {
            _pane.Selection.PrepareContextMenu(item);
            BuildItemMenu().Open(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        // The check box toggles the item in or out of the selection and keeps the rest; Shift adds a range.
        if (IsOnCheckBox(e.Source))
        {
            _pane.Selection.Click(item, control: true, shift: shift);
            e.Handled = true;
            return;
        }

        var openOnSingleClick = _services.Settings.Current.FilesAndFolders.OpenItemsWithSingleClick && !control && !shift;
        if (e.ClickCount == 2 || openOnSingleClick && item.IsDirectory)
        {
            _ = _pane.OpenItemAsync(item);
            e.Handled = true;
            return;
        }

        // Pressing on an already selected item keeps the multi-selection so it can be dragged.
        if (!_pane.Selection.IsSelected(item) || control || shift)
            _pane.Selection.Click(item, control, shift);
        _pressedItem = item;
        _pressPoint = point.Position;
        e.Handled = true;
    }

    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _pane is null)
            return;
        Focus();
        var point = e.GetCurrentPoint(SelectionCanvas);
        if (point.Properties.IsRightButtonPressed)
        {
            _pane.Selection.Clear();
            BuildBackgroundMenu().Open(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        var control = e.KeyModifiers.HasFlag(PrimaryModifier);
        _bandBaseline = control ? [.. _pane.Selection.SelectedItems] : null;
        if (!control)
            _pane.Selection.Clear();
        _bandStart = point.Position;
        e.Pointer.Capture(Scroller);
    }

    private void OnBackgroundDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemFromSource(e.Source) is null && _services.Settings.Current.FilesAndFolders.DoubleClickEmptyAreaGoesUp)
            _pane?.GoUpCommand.Execute(null);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pane is null)
            return;

        if (_bandStart is { } start)
        {
            var current = e.GetPosition(SelectionCanvas);
            var rect = new Rect(start, current).Normalize();
            SelectionRectangle.IsVisible = true;
            Canvas.SetLeft(SelectionRectangle, rect.X);
            Canvas.SetTop(SelectionRectangle, rect.Y);
            SelectionRectangle.Width = rect.Width;
            SelectionRectangle.Height = rect.Height;
            _pane.Selection.SelectRectangle(ItemsIntersecting(rect), _bandBaseline);
            return;
        }

        if (_pressedItem is not null && !_dragging && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var position = e.GetPosition(this);
            if (Math.Abs(position.X - _pressPoint.X) > DragThreshold || Math.Abs(position.Y - _pressPoint.Y) > DragThreshold)
            {
                _dragging = true;
                Cursor = new Cursor(StandardCursorType.DragMove);
            }
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_bandStart is not null)
        {
            _bandStart = null;
            _bandBaseline = null;
            SelectionRectangle.IsVisible = false;
            e.Pointer.Capture(null);
        }

        if (_dragging && _pane is not null)
        {
            // In-window drag and drop: dropping the selection on a folder moves (same volume) or copies it.
            var target = ItemFromSource(this.InputHitTest(e.GetPosition(this)));
            var sources = _pane.Selection.SelectedItems.Select(i => i.FullPath).ToList();
            if (target is { IsDirectory: true } folder && !_pane.Selection.IsSelected(folder) && sources.Count > 0)
            {
                var move = PaneViewModel.ShouldMoveOnDrop(sources, folder.FullPath,
                    e.KeyModifiers.HasFlag(PrimaryModifier), e.KeyModifiers.HasFlag(KeyModifiers.Shift), App.Services.GetRequiredService<IVolumeLocator>());
                _ = _pane.TransferIntoAsync(sources, folder.FullPath, move);
            }
        }
        else if (_pressedItem is { } pressed && _pane is not null && e.InitialPressMouseButton == MouseButton.Left
                 && !e.KeyModifiers.HasFlag(PrimaryModifier) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _pane.Selection.Count > 1)
        {
            // Click (no drag) on an item of a multi-selection selects just that item.
            _pane.Selection.Click(pressed, control: false, shift: false);
        }

        _dragging = false;
        _pressedItem = null;
        Cursor = Cursor.Default;
    }

    /// <summary>Items whose visuals intersect the band (realized containers only: the visible area).</summary>
    private List<FileItemViewModel> ItemsIntersecting(Rect band)
    {
        var result = new List<FileItemViewModel>();
        foreach (var container in ItemsHost.GetRealizedContainers())
        {
            foreach (var border in container.GetSelfAndVisualDescendants().OfType<Border>())
            {
                if (border.DataContext is not FileItemViewModel item || !border.Classes.Contains("item"))
                    continue;
                var origin = border.TranslatePoint(default, SelectionCanvas);
                if (origin is { } p && new Rect(p, border.Bounds.Size).Intersects(band))
                    result.Add(item);
            }
        }

        return result;
    }

    // ── Keyboard ───────────────────────────────────────────────

    private static KeyModifiers PrimaryModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_pane is null || e.Source is TextBox)
            return;

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var control = e.KeyModifiers.HasFlag(PrimaryModifier);
        var columns = _pane.IsTileLayout ? Math.Max(1, _pane.TileColumns) : 1;
        var page = Math.Max(1, (int)(Scroller.Viewport.Height / 32)) * columns;
        int? delta = e.Key switch
        {
            Key.Down => columns,
            Key.Up => -columns,
            Key.Right when _pane.IsTileLayout => 1,
            Key.Left when _pane.IsTileLayout => -1,
            Key.PageDown => page,
            Key.PageUp => -page,
            Key.Home => int.MinValue,
            Key.End => int.MaxValue,
            _ => null,
        };

        if (delta is { } step)
        {
            _pane.Selection.MoveFocus(step, shift, control);
            if (_pane.Selection.FocusedItem is { } focused)
                OnScrollIntoView(this, focused);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && control)
        {
            _pane.Selection.ToggleFocused();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _pane.Selection.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && shift))
        {
            (_pane.Selection.Count > 0 ? BuildItemMenu() : BuildBackgroundMenu()).Open(this);
            e.Handled = true;
        }
    }

    /// <summary>Type-to-select: typed characters within one second build a prefix.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_pane is null || string.IsNullOrEmpty(e.Text) || e.Source is TextBox)
            return;
        var now = Environment.TickCount64;
        _typeAhead = now - _typeAheadAt > 1000 ? e.Text : _typeAhead + e.Text;
        _typeAheadAt = now;
        if (_pane.Selection.JumpToPrefix(_typeAhead) is { } item)
            OnScrollIntoView(this, item);
        e.Handled = true;
    }

    private void OnScrollIntoView(object? sender, FileItemViewModel item)
    {
        if (_pane is null)
            return;
        var rows = _pane.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], item) || rows[i] is TileRowViewModel row && row.Items.Contains(item))
            {
                ItemsHost.ScrollIntoView(i);
                return;
            }
        }
    }

    // ── Context menus (built on demand; cheap and always in sync with the selection) ──

    private ContextMenu BuildItemMenu()
    {
        var pane = _pane!;
        var selected = pane.Selection.SelectedItems;
        var general = _services.Settings.Current.General;
        var menu = new ContextMenu();

        if (pane.IsTrash)
        {
            menu.Items.Add(Item("Command.Restore", "Restore", pane.RestoreCommand));
            menu.Items.Add(Item("Command.DeletePermanently", "Delete", pane.DeleteCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Command.Properties", "Properties", pane.PropertiesCommand));
            return menu;
        }

        if (pane.IsRemote)
            return BuildRemoteItemMenu(pane, selected);

        menu.Items.Add(Item("Command.Open", "Forward", pane.OpenSelectedCommand));
        if (selected.Any(i => i.IsDirectory))
        {
            menu.Items.Add(Item("Command.OpenInNewTab", "Add", pane.OpenSelectedInNewTabCommand));
            if (general.ShowOpenInNewWindowInContextMenu)
                menu.Items.Add(Item("Command.OpenInNewWindow", "Window", pane.OpenSelectedInNewWindowCommand));
        }
        else if (selected.Count == 1)
        {
            menu.Items.Add(Item("Command.OpenWith", "OpenWith", pane.OpenWithCommand));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Command.Cut", "Cut", pane.CutCommand, "item.cut"));
        menu.Items.Add(Item("Command.Copy", "Copy", pane.CopyCommand, "item.copy"));
        if (selected.Count == 1 && selected[0].IsDirectory)
            menu.Items.Add(Item("Command.PasteInto", "Paste", pane.PasteCommand));
        menu.Items.Add(Item("Command.Duplicate", "Copy", pane.DuplicateCommand, "item.duplicate"));
        menu.Items.Add(Item(selected.Count > 1 ? "Command.BulkRename" : "Command.Rename", "Rename", pane.RenameCommand, "item.rename"));
        if (general.ShowCopyPathInContextMenu)
            menu.Items.Add(Item("Command.CopyPath", "Copy", pane.CopyPathCommand, "item.copyPath"));
        menu.Items.Add(new Separator());

        var archive = new MenuItem { Header = Text["Command.Archive"] };
        archive.Items.Add(Item("Command.Compress", "Archive", pane.CompressCommand));
        if (selected.Any(i => ArchiveService.IsSupportedArchive(i.FullPath)))
            archive.Items.Add(Item("Command.Extract", "Extract", pane.ExtractCommand, "item.extract"));
        menu.Items.Add(archive);

        var tags = new MenuItem { Header = Text["Command.Tags"] };
        foreach (var tag in _services.Settings.Current.Tags)
        {
            tags.Items.Add(new MenuItem
            {
                Header = tag.Name,
                Command = pane.ToggleTagCommand,
                CommandParameter = tag.Id,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = selected.All(i => i.Tags.Any(t => t.Id == tag.Id)),
            });
        }

        menu.Items.Add(tags);
        if (selected.Any(i => i.IsDirectory))
        {
            menu.Items.Add(Item("Command.PinToSidebar", "Pin", pane.PinSelectedCommand));
            if (general.ShowOpenTerminalInContextMenu)
                menu.Items.Add(Item("Command.OpenTerminal", "Terminal", pane.OpenTerminalCommand));
        }

        menu.Items.Add(Item("Command.RevealInSystem", "OpenWith", pane.RevealInSystemCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Command.Delete", "Delete", pane.DeleteCommand, "item.delete"));
        menu.Items.Add(Item("Command.Properties", "Properties", pane.PropertiesCommand, "item.properties"));
        return menu;
    }

    /// <summary>Items in a server folder (plugin file system): only what a server supports, plus Download.</summary>
    private ContextMenu BuildRemoteItemMenu(PaneViewModel pane, IReadOnlyList<FileItemViewModel> selected)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Command.Open", "Forward", pane.OpenSelectedCommand));
        if (selected.Any(i => i.IsDirectory))
            menu.Items.Add(Item("Command.OpenInNewTab", "Add", pane.OpenSelectedInNewTabCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remote.Download", "Download", pane.DownloadCommand));
        menu.Items.Add(Item("Command.Rename", "Rename", pane.RenameCommand, "item.rename"));
        menu.Items.Add(Item("Command.CopyPath", "Copy", pane.CopyPathCommand, "item.copyPath"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Command.Delete", "Delete", pane.DeleteCommand, "item.delete"));
        return menu;
    }

    private ContextMenu BuildBackgroundMenu()
    {
        var pane = _pane!;
        var menu = new ContextMenu();
        if (pane.IsTrash)
        {
            menu.Items.Add(Item("Command.EmptyTrash", "Delete", pane.EmptyTrashCommand));
            menu.Items.Add(Item("Command.Refresh", "Refresh", pane.RefreshCommand));
            return menu;
        }

        var layout = new MenuItem { Header = Text["Command.Layout"] };
        foreach (var value in new[] { Core.Settings.FolderLayout.Details, Core.Settings.FolderLayout.List, Core.Settings.FolderLayout.Tiles, Core.Settings.FolderLayout.MediumIcons, Core.Settings.FolderLayout.LargeIcons })
            layout.Items.Add(new MenuItem { Header = Text["Layout." + value], Command = pane.SetLayoutCommand, CommandParameter = value, ToggleType = MenuItemToggleType.Radio, IsChecked = pane.Layout == value });
        menu.Items.Add(layout);

        var sort = new MenuItem { Header = Text["Command.Sort"] };
        foreach (var value in Enum.GetValues<Core.Sorting.SortColumn>())
            sort.Items.Add(new MenuItem { Header = Text["Sort." + value], Command = pane.SortByCommand, CommandParameter = value, ToggleType = MenuItemToggleType.Radio, IsChecked = pane.SortColumn == value });
        menu.Items.Add(sort);

        var group = new MenuItem { Header = Text["Command.Group"] };
        foreach (var value in Enum.GetValues<Core.Sorting.GroupBy>())
            group.Items.Add(new MenuItem { Header = Text["GroupBy." + value], Command = pane.SetGroupByCommand, CommandParameter = value, ToggleType = MenuItemToggleType.Radio, IsChecked = pane.GroupBy == value });
        menu.Items.Add(group);
        menu.Items.Add(Item("Command.Refresh", "Refresh", pane.RefreshCommand, "nav.refresh"));
        menu.Items.Add(new Separator());

        if (pane.IsRemote)
        {
            menu.Items.Add(Item("Command.NewFolder", "Folder", pane.NewFolderCommand, "item.newFolder"));
            menu.Items.Add(Item("Remote.Upload", "Upload", pane.UploadCommand));
            menu.Items.Add(new Separator());
        }

        if (pane.CanCreateItems)
        {
            var create = new MenuItem { Header = Text["Command.New"] };
            create.Items.Add(Item("Command.NewFolder", "Folder", pane.NewFolderCommand, "item.newFolder"));
            create.Items.Add(Item("Command.NewFile", "File", pane.NewFileCommand, "item.newFile"));
            menu.Items.Add(create);
            menu.Items.Add(Item("Command.Paste", "Paste", pane.PasteCommand, "item.paste"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Command.PinToSidebar", "Pin", pane.PinSelectedCommand));
            menu.Items.Add(Item("Command.OpenTerminal", "Terminal", pane.OpenTerminalCommand));
            menu.Items.Add(Item("Command.OpenInCodeEditor", "Terminal", pane.OpenInCodeEditorCommand));
        }

        menu.Items.Add(Item("Command.SelectAll", "Check", pane.SelectAllCommand, "item.selectAll"));
        menu.Items.Add(Item("Command.Properties", "Properties", pane.PropertiesCommand, "item.properties"));
        return menu;
    }

    private MenuItem Item(string textKey, string icon, System.Windows.Input.ICommand command, string? commandId = null)
    {
        var item = new MenuItem
        {
            Header = Text[textKey],
            Command = command,
            Icon = new Controls.IconView { Kind = icon, Width = 16, Height = 16 },
        };

        if (commandId is not null)
        {
            var gestures = Presentation.Commands.CommandRegistry.GesturesFor(commandId, _services.Settings.Current, OperatingSystem.IsMacOS());
            if (gestures.Count > 0)
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(gestures[0].Replace("Cmd", "Meta", StringComparison.Ordinal));
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return item;
    }

    /// <summary>The OS has no "Open with" chooser (Linux/macOS): list the registered applications.</summary>
    private async void OnOpenWithRequested(object? sender, FileItemViewModel item)
    {
        var handlers = await _services.Launcher.GetHandlersAsync(item.FullPath).ConfigureAwait(true);
        var menu = new ContextMenu();
        foreach (var handler in handlers)
        {
            var captured = handler;
            var entry = new MenuItem { Header = handler.IsDefault ? $"{handler.DisplayName} ({Text["OpenWith.Default"]})" : handler.DisplayName };
            entry.Click += async (_, _) => await _services.Launcher.OpenWithAsync(item.FullPath, captured).ConfigureAwait(true);
            menu.Items.Add(entry);
        }

        if (handlers.Count == 0)
            menu.Items.Add(new MenuItem { Header = Text["OpenWith.NoApplications"], IsEnabled = false });

        menu.Items.Add(new Separator());
        var choose = new MenuItem { Header = Text["OpenWith.Choose"] };
        choose.Click += async (_, _) =>
        {
            var application = await _services.Dialogs.PickFileAsync(Text["OpenWith.Choose"]).ConfigureAwait(true);
            if (application is not null)
                await _services.Launcher.OpenWithAsync(item.FullPath, new Core.Platform.AppHandler(application, Path.GetFileName(application), null, false)).ConfigureAwait(true);
        };
        menu.Items.Add(choose);
        menu.Open(this);
    }
}
