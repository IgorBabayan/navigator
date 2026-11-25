using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Navigator.App.Views.Rows;
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
    private const double AutoScrollMargin = 28;
    private const double AutoScrollStep = 24;
    private readonly PaneServices _services = App.Services.GetRequiredService<PaneServices>();
    private readonly IVolumeLocator _volumes = App.Services.GetRequiredService<IVolumeLocator>();
    private readonly DropEvaluator _drop;
    private Border? _dropHighlight;
    private PointerPressedEventArgs? _pressEvent;
    private PaneMenuView? _menus;
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
        _drop = new DropEvaluator(_volumes);
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

        // Drop target: files and folders from other panes, other tabs, other windows and other applications.
        DragDrop.SetAllowDrop(Scroller, true);
        Scroller.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        Scroller.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Scroller.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        Scroller.AddHandler(DragDrop.DropEvent, OnDrop);
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

    /// <summary>Draws the pane's context menus; created on the first right-click.</summary>
    private PaneMenuView Menus
        => _menus ??= new PaneMenuView(_services, App.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PaneMenuView>>());

    /// <summary>Native handle of the window, so dialogs of Explorer menu entries are owned by it (0 where there is none).</summary>
    private nint OwnerWindow() => TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0;

    private void ShowItemMenu(bool shiftHeld) => Menus.Render(_pane!.CreateItemMenu(shiftHeld, OwnerWindow)).Open(this);

    private void ShowBackgroundMenu(bool shiftHeld) => Menus.Render(_pane!.CreateBackgroundMenu(shiftHeld, OwnerWindow)).Open(this);

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

    private static FileItemViewModel? ItemFromSource(object? source) => ItemBorderFromSource(source)?.DataContext as FileItemViewModel;

    /// <summary>The row/tile surface of the item under <paramref name="source"/>, or null on the empty area.</summary>
    private static Border? ItemBorderFromSource(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border { DataContext: FileItemViewModel } border && border.Classes.Contains("item"))
                return border;
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
            ShowItemMenu(shift);
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
        _pressEvent = e;
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
            ShowBackgroundMenu(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
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
                StartDrag();
        }
    }

    /// <summary>
    /// Starts an OS drag of the selection: it can be dropped on a folder in any pane of any tab or window, on a tab, on
    /// the sidebar, or in another application. Trash items are not dragged (they are restored with "Restore").
    /// </summary>
    private async void StartDrag()
    {
        var pane = _pane;
        var trigger = _pressEvent;
        if (pane is null || trigger is null || pane.IsTrash || pane.Selection.Count == 0)
        {
            _pressedItem = null;
            _pressEvent = null;
            return;
        }

        _dragging = true;
        try
        {
            await ItemDragDrop.StartAsync(trigger, this, [.. pane.Selection.SelectedItems]).ConfigureAwait(true);
        }
        finally
        {
            _dragging = false;
            _pressedItem = null;
            _pressEvent = null;
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

        // A drag in progress ends in its own drop handling (the OS owns the pointer until then).
        if (_dragging)
            return;

        if (_pressedItem is { } pressed && _pane is not null && e.InitialPressMouseButton == MouseButton.Left
            && !e.KeyModifiers.HasFlag(PrimaryModifier) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _pane.Selection.Count > 1)
        {
            // Click (no drag) on an item of a multi-selection selects just that item.
            _pane.Selection.Click(pressed, control: false, shift: false);
        }

        _pressedItem = null;
        _pressEvent = null;
    }

    // ── Drop target ────────────────────────────────────────────

    /// <summary>
    /// Where a drop at the pointer lands: a folder item under it (highlighted), otherwise the pane's own folder.
    /// Files under the pointer are not targets; the drop goes to the folder they are in.
    /// </summary>
    private (string? Destination, Border? Folder) DropTargetAt(DragEventArgs e)
    {
        if (ItemBorderFromSource(e.Source) is { DataContext: FileItemViewModel { IsDirectory: true } folder } border)
            return (folder.FullPath, border);
        return (_pane?.DropDestination, null);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var (destination, folder) = DropTargetAt(e);
        e.DragEffects = _pane is null ? DragDropEffects.None : _drop.Evaluate(e, destination);
        SetDropHighlight(e.DragEffects == DragDropEffects.None ? null : folder);
        AutoScroll(e);
        e.Handled = true;
    }

    /// <summary>
    /// Leave is raised for every child the pointer crosses (followed at once by Enter on the next one), so it only
    /// clears the highlight; the cached decision is keyed by destination and stays valid.
    /// </summary>
    private void OnDragLeave(object? sender, DragEventArgs e) => SetDropHighlight(null);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var (destination, _) = DropTargetAt(e);
        SetDropHighlight(null);
        _drop.Reset();
        e.DragEffects = ItemDragDrop.Drop(e, destination, _pane, _volumes);
        e.Handled = true;
    }

    /// <summary>One highlighted folder at a time; only the class changes, so no relayout happens while dragging.</summary>
    private void SetDropHighlight(Border? folder)
    {
        if (ReferenceEquals(folder, _dropHighlight))
            return;
        _dropHighlight?.Classes.Remove("droptarget");
        _dropHighlight = folder;
        folder?.Classes.Add("droptarget");
    }

    /// <summary>Dragging near the top or bottom edge scrolls the list, so any folder can be reached.</summary>
    private void AutoScroll(DragEventArgs e)
    {
        var y = e.GetPosition(Scroller).Y;
        var height = Scroller.Viewport.Height;
        if (height <= AutoScrollMargin * 2)
            return;
        var delta = y < AutoScrollMargin ? -AutoScrollStep : y > height - AutoScrollMargin ? AutoScrollStep : 0;
        if (delta == 0)
            return;
        var max = Math.Max(0, Scroller.Extent.Height - height);
        var target = Math.Clamp(Scroller.Offset.Y + delta, 0, max);
        if (target != Scroller.Offset.Y)
            Scroller.Offset = new Vector(Scroller.Offset.X, target);
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

    private static KeyModifiers PrimaryModifier => ItemDragDrop.PrimaryModifier;

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
            if (_pane.Selection.Count > 0)
                ShowItemMenu(shiftHeld: false);
            else
                ShowBackgroundMenu(shiftHeld: false);
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

    /// <summary>The OS has no "Open with" chooser (Linux/macOS): list the registered applications.</summary>
    private async void OnOpenWithRequested(object? sender, FileItemViewModel item)
    {
        var handlers = await _services.Launcher.GetHandlersAsync(item.FullPath).ConfigureAwait(true);
        var menu = new ContextMenu();
        foreach (var handler in handlers)
        {
            var captured = handler;
            var entry = new MenuItem
            {
                Header = handler.IsDefault ? $"{handler.DisplayName} ({Text["OpenWith.Default"]})" : handler.DisplayName,
                Icon = new Controls.IconView { Kind = "ApplicationOutline", Width = 16, Height = 16 },
            };
            entry.Click += async (_, _) => await _services.Launcher.OpenWithAsync(item.FullPath, captured).ConfigureAwait(true);
            menu.Items.Add(entry);
        }

        if (handlers.Count == 0)
            menu.Items.Add(new MenuItem { Header = Text["OpenWith.NoApplications"], IsEnabled = false });

        menu.Items.Add(new Separator());
        var choose = new MenuItem { Header = Text["OpenWith.Choose"], Icon = new Controls.IconView { Kind = "Search", Width = 16, Height = 16 } };
        choose.Click += async (_, _) => await (_pane?.ChooseApplicationCommand.ExecuteAsync(item.FullPath) ?? Task.CompletedTask).ConfigureAwait(true);
        menu.Items.Add(choose);
        menu.Open(this);
    }
}
