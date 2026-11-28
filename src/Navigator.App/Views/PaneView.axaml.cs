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
using Navigator.Core.Navigation;
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
    private DragDropEffects _overlayEffect;
    private FileItemViewModel? _overlayFolder;
    private int _dragEventSerial;
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
    private FileItemViewModel? _renaming;
    private bool _renameEnding;
    private long _columnsNavigatedAt;

    /// <summary>A second click this soon after a column opened belongs to the click that opened it, not to the new column.</summary>
    private const long ColumnDoubleClickGuardMilliseconds = 500;

    public PaneView()
    {
        InitializeComponent();
        _drop = new DropEvaluator(_volumes);
        ItemsHost.ItemTemplate = new RowTemplates(this);
        Columns.Load(_services.Settings.Current.FilesAndFolders.Columns, _services.Settings.Current.FilesAndFolders.ShowFileTagsColumn);
        Columns.PropertyChanged += (_, _) => SaveColumns();

        foreach (var header in Headers)
        {
            header.SortRequested += (_, column) => _pane?.SortByCommand.Execute(column);
            header.Resized += (sender, delta) => ResizeColumn((ColumnHeader)sender!, delta);
            header.ReorderDropped += (sender, x) => DropHeader((ColumnHeader)sender!, x);
            if (header != NameHeader)
                header.ContextMenu = CreateHeaderMenu(header);
        }

        DataContextChanged += (_, _) => Attach(DataContext as PaneViewModel);
        Scroller.SizeChanged += (_, e) => _pane?.SetViewportWidth(e.NewSize.Width);

        // Column layout: columns left of the current folder take at most two thirds of the pane.
        SizeChanged += (_, e) => TrailScroller.MaxWidth = Math.Max(240, e.NewSize.Width * 0.66);
        TrailHost.Tapped += OnTrailTapped;
        TrailHost.DoubleTapped += OnTrailDoubleTapped;

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

        // In-place rename: Enter/Escape/Tab are taken before the text box sees them; leaving the box commits.
        RenameBox.AddHandler(KeyDownEvent, OnRenameKeyDown, RoutingStrategies.Tunnel);
        RenameBox.LostFocus += (_, _) => _ = CommitRenameAsync(move: 0, fromFocusLoss: true);
        RenameBox.TextChanged += (_, _) => ShowRenameValidation(explicitly: false);
        Scroller.ScrollChanged += (_, _) => FollowRenamedItem();
        Scroller.SizeChanged += (_, _) => FollowRenamedItem();
        Columns.PropertyChanged += (_, _) => FollowRenamedItem();
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
        if (_pane is null && DataContext is PaneViewModel pane)
            Attach(pane);
    }

    /// <summary>
    /// A view leaving the tree lets go of its pane: a tab moved to another window keeps its panes alive there, and
    /// they must not keep raising events into this (closed) window's view.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _services.Settings.Changed -= OnSettingsChanged;
        Attach(null);
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
            _pane.InlineRenameRequested -= OnInlineRenameRequested;
            _pane.TrailColumns.CollectionChanged -= OnTrailChanged;
        }

        CancelRename();
        _pane = pane;
        if (pane is null)
            return;
        pane.TrailColumns.CollectionChanged += OnTrailChanged;
        ApplyColumnsClass();

        pane.PropertyChanged += OnPaneChanged;
        pane.ScrollIntoViewRequested += OnScrollIntoView;
        pane.OpenWithRequested += OnOpenWithRequested;
        pane.InlineRenameRequested += OnInlineRenameRequested;
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
                CancelRename();
                FilterBar.IsVisible = false;
                Scroller.Offset = default;
                break;
            case nameof(PaneViewModel.Layout):
                _ = CommitRenameAsync(move: 0, fromFocusLoss: true);
                ApplyColumnsClass();
                break;
        }
    }

    private ColumnHeader[] Headers => [NameHeader, ModifiedHeader, TypeHeader, SizeHeader, CreatedHeader, TagsHeader];

    private void UpdateSortGlyphs()
    {
        if (_pane is null)
            return;
        foreach (var header in Headers)
            header.ShowSort(_pane.Sort);
    }

    // ── Column order ─────────────────────────────────────────

    /// <summary>
    /// A header dragged sideways was released at <paramref name="x"/> (header-row coordinates): the column moves in
    /// front of the first shown column whose middle lies right of the pointer. Hidden columns keep their slots.
    /// </summary>
    private void DropHeader(ColumnHeader dragged, double x)
    {
        var others = Columns.Order.Where(id => id != dragged.ColumnId).ToList();
        var target = others.Count;
        for (var i = 0; i < others.Count; i++)
        {
            if (HeaderFor(others[i]) is { IsVisible: true } header && x < header.Bounds.X + (header.Bounds.Width / 2))
            {
                target = i;
                break;
            }
        }

        Columns.Move(dragged.ColumnId, target);
    }

    /// <summary>Header context menu: move the column one shown column to the left or right (keyboard-free alternative to dragging).</summary>
    private ContextMenu CreateHeaderMenu(ColumnHeader header)
    {
        var left = new MenuItem { Icon = new Controls.IconView { Kind = "ChevronLeft", Width = 16, Height = 16 } };
        var right = new MenuItem { Icon = new Controls.IconView { Kind = "ChevronRight", Width = 16, Height = 16 } };
        var reset = new MenuItem { Icon = new Controls.IconView { Kind = "Undo", Width = 16, Height = 16 } };
        left.Click += (_, _) => StepColumn(header.ColumnId, -1);
        right.Click += (_, _) => StepColumn(header.ColumnId, +1);
        reset.Click += (_, _) =>
        {
            foreach (var (id, index) in Navigator.Core.Settings.ColumnOrder.Known.Select((id, index) => (id, index)))
                Columns.Move(id, index);
        };
        var menu = new ContextMenu { Items = { left, right, new Separator(), reset } };
        menu.Opening += (_, _) =>
        {
            // Texts are read when the menu opens, so a language switch shows up without rebuilding the header.
            left.Header = Text["Column.MoveLeft"];
            right.Header = Text["Column.MoveRight"];
            reset.Header = Text["Column.ResetOrder"];
            var visible = Columns.Order.Where(id => HeaderFor(id) is { IsVisible: true }).ToList();
            var at = visible.IndexOf(header.ColumnId);
            left.IsEnabled = at > 1; // the name column stays first
            right.IsEnabled = at >= 0 && at < visible.Count - 1;
        };
        return menu;
    }

    private void StepColumn(string id, int direction)
    {
        var visible = Columns.Order.Where(c => HeaderFor(c) is { IsVisible: true }).ToList();
        var at = visible.IndexOf(id);
        var neighbour = at + direction;
        if (at < 0 || neighbour < 1 || neighbour >= visible.Count)
            return;
        var target = Columns.Order.ToList().IndexOf(visible[neighbour]);
        Columns.Move(id, target);
    }

    private ColumnHeader? HeaderFor(string id) => Headers.FirstOrDefault(h => h.ColumnId == id);

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

        // Column layout: a folder opens on release (so it can still be dragged); the second click of a double-click
        // that opened a column lands in the new column and must not open what happens to be under the pointer there.
        if (_pane.IsColumnsLayout && e.ClickCount == 2 && Environment.TickCount64 - _columnsNavigatedAt < ColumnDoubleClickGuardMilliseconds)
        {
            _pane.Selection.Click(item, control, shift);
            e.Handled = true;
            return;
        }

        var openOnSingleClick = _services.Settings.Current.FilesAndFolders.OpenItemsWithSingleClick && !control && !shift && !_pane.IsColumnsLayout;
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

        if (_pressedItem is { IsDirectory: true } folder && _pane is { IsColumnsLayout: true } columns && e.InitialPressMouseButton == MouseButton.Left
            && !e.KeyModifiers.HasFlag(PrimaryModifier) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Column layout: clicking a folder opens it as the next column.
            _pressedItem = null;
            _pressEvent = null;
            _columnsNavigatedAt = Environment.TickCount64;
            columns.OpenInColumns(folder);
            return;
        }

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
        _dragEventSerial++;
        var (destination, folder) = DropTargetAt(e);
        e.DragEffects = _pane is null ? DragDropEffects.None : _drop.Evaluate(e, destination);
        var droppable = e.DragEffects != DragDropEffects.None;
        SetDropHighlight(droppable ? folder : null);
        if (droppable)
            ShowDropOverlay(e.DragEffects, destination, folder?.DataContext as FileItemViewModel);
        else
            HideDropOverlay();
        AutoScroll(e);
        e.Handled = true;
    }

    /// <summary>
    /// Leave is raised for every child the pointer crosses, followed at once by Enter on the next one. Clearing is
    /// therefore deferred to the next dispatcher turn and skipped when another drag event arrived meanwhile, so the
    /// overlay neither flickers nor rebuilds its text while the pointer moves across rows.
    /// </summary>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        var serial = ++_dragEventSerial;
        Dispatcher.UIThread.Post(() =>
        {
            if (serial != _dragEventSerial)
                return;
            SetDropHighlight(null);
            HideDropOverlay();
        });
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dragEventSerial++;
        var (destination, _) = DropTargetAt(e);
        SetDropHighlight(null);
        HideDropOverlay();
        _drop.Reset();
        e.DragEffects = ItemDragDrop.Drop(e, destination, _pane, _volumes);
        e.Handled = true;
    }

    /// <summary>
    /// Shows the drop overlay (like a chat window's "drop files here"): a dashed frame around the list when the drop
    /// goes into this folder, and a card naming the action and the target. Texts are rebuilt only when the action or
    /// the target folder changes, not on every drag-over event.
    /// </summary>
    private void ShowDropOverlay(DragDropEffects effect, string? destination, FileItemViewModel? folder)
    {
        var shown = DropOverlay.Classes.Contains("shown");
        if (shown && effect == _overlayEffect && ReferenceEquals(folder, _overlayFolder))
            return;
        _overlayEffect = effect;
        _overlayFolder = folder;

        var upload = RemoteUri.IsRemote(destination);
        var name = folder?.DisplayName ?? _pane?.Title ?? string.Empty;
        var (key, icon) = upload ? ("Drop.UploadTo", "Upload")
            : effect == DragDropEffects.Move ? ("Drop.MoveTo", "FolderMove")
            : ("Drop.CopyTo", "Copy");
        DropIcon.Kind = icon;
        DropTitle.Text = Text.Format(key, name);
        DropHint.IsVisible = !upload;
        if (!upload)
            DropHint.Text = Text.Format("Drop.Hint", OperatingSystem.IsMacOS() ? "⌘" : "Ctrl");
        DropOverlay.Classes.Set("folder", folder is not null);
        if (!shown)
            DropOverlay.Classes.Add("shown");
    }

    private void HideDropOverlay()
    {
        DropOverlay.Classes.Remove("shown");
        _overlayEffect = DragDropEffects.None;
        _overlayFolder = null;
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

        // Column layout: Right opens the focused folder as the next column, Left returns to the previous one.
        if (_pane.IsColumnsLayout && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Right or Key.Left)
        {
            if (e.Key == Key.Left)
                _pane.ColumnsGoBack();
            else if (_pane.Selection.FocusedItem is { IsDirectory: true } focusedFolder)
                _pane.OpenInColumns(focusedFolder);
            e.Handled = true;
            return;
        }

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

    // ── Column layout ────────────────────────────────────────────

    /// <summary>Styles key off this class: folder chevrons in the current column (PaneView.axaml).</summary>
    private void ApplyColumnsClass() => Classes.Set("columns", _pane?.IsColumnsLayout == true);

    /// <summary>The entry and its column under a pointer event in the trail, if any.</summary>
    private static (MillerColumnViewModel? Column, ColumnEntryViewModel? Entry) TrailEntryFrom(object? source)
    {
        ColumnEntryViewModel? entry = null;
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not StyledElement { DataContext: { } context })
                continue;
            if (entry is null && context is ColumnEntryViewModel found)
                entry = found;
            else if (context is MillerColumnViewModel column)
                return (column, entry);
        }

        return (null, entry);
    }

    private void OnTrailTapped(object? sender, TappedEventArgs e)
    {
        if (_pane is null || TrailEntryFrom(e.Source) is not ({ } column, { } entry))
            return;
        Focus();
        if (entry.Item.IsDirectory)
            _columnsNavigatedAt = Environment.TickCount64;
        _pane.ActivateTrailEntry(column, entry);
        e.Handled = true;
    }

    private void OnTrailDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_pane is not null && TrailEntryFrom(e.Source) is (_, { Item.IsDirectory: false } entry))
        {
            _ = _pane.OpenTrailEntryAsync(entry);
            e.Handled = true;
        }
    }

    /// <summary>
    /// After the trail changes, the newest column is scrolled into view and every column shows its highlighted entry
    /// (a column filled in the background is scrolled once its listing arrives).
    /// </summary>
    private void OnTrailChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is { } added)
        {
            foreach (MillerColumnViewModel column in added)
            {
                column.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(MillerColumnViewModel.OpenIndex))
                        Dispatcher.UIThread.Post(ScrollTrail, DispatcherPriority.Background);
                };
            }
        }

        Dispatcher.UIThread.Post(ScrollTrail, DispatcherPriority.Background);
    }

    private void ScrollTrail()
    {
        if (_pane is not { HasTrailColumns: true })
            return;
        TrailScroller.Offset = new Vector(Math.Max(0, TrailScroller.Extent.Width - TrailScroller.Viewport.Width), 0);
        foreach (var container in TrailHost.GetRealizedContainers())
        {
            if (container.DataContext is MillerColumnViewModel { OpenIndex: >= 0 } column
                && container.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault() is { } list)
                list.ScrollIntoView(column.OpenIndex);
        }
    }

    // ── In-place rename ──────────────────────────────────────────

    /// <summary>
    /// F2 on one item: the pane's single editor is laid over the item's name (scrolled into view first), with the stem
    /// selected. Enter commits, Escape cancels, Tab / Shift+Tab commit and move to the next / previous item, and
    /// clicking elsewhere commits. Rows themselves never host an editor, so the list stays as cheap as before.
    /// </summary>
    private void OnInlineRenameRequested(object? sender, InlineRenameEventArgs request)
    {
        if (_pane is not { IsBrowsingList: true } || !IsEffectivelyVisible)
            return; // not shown: the view model falls back to the dialog

        request.Handled = true;
        if (_renaming is not null)
            CancelRename();

        _renaming = request.Item;
        RenameBox.Text = request.Text;
        RenameBox.Classes.Remove("invalid");
        RenameError.IsVisible = false;
        OnScrollIntoView(this, request.Item);

        // The row may only exist after the scroll has been laid out.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_renaming, request.Item))
                return;
            if (!PlaceRenameBox())
            {
                CancelRename();
                return;
            }

            RenameBox.IsVisible = true;
            RenameBox.Focus();
            RenameBox.SelectionStart = 0;
            RenameBox.SelectionEnd = Math.Clamp(request.SelectionLength, 0, request.Text.Length);
        }, DispatcherPriority.Background);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _ = CommitRenameAsync(move: 0, fromFocusLoss: false);
                e.Handled = true;
                break;
            case Key.Escape:
                CancelRename();
                Focus();
                e.Handled = true;
                break;
            case Key.Tab:
                _ = CommitRenameAsync(move: e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1, fromFocusLoss: false);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Ends the edit with the typed name. An unusable name keeps the editor open with the reason shown, except when
    /// focus left the box: then the edit is dropped, like Explorer does.
    /// </summary>
    private async Task CommitRenameAsync(int move, bool fromFocusLoss)
    {
        if (_renameEnding || _renaming is not { } item || _pane is not { } pane || !RenameBox.IsVisible)
            return;

        var text = RenameBox.Text ?? string.Empty;
        if (pane.ValidateInlineName(item, text) is not null)
        {
            if (fromFocusLoss)
                CancelRename();
            else
                ShowRenameValidation(explicitly: true);
            return;
        }

        var next = move == 0 ? null : pane.NeighbourOf(item, move);
        var keepFocus = !fromFocusLoss;
        _renameEnding = true;
        try
        {
            HideRenameBox();
            if (keepFocus)
                Focus();
            await pane.CommitInlineRenameAsync(item, text, selectRenamed: next is null).ConfigureAwait(true);
        }
        finally
        {
            _renameEnding = false;
        }

        if (next is not null && ReferenceEquals(_pane, pane))
            pane.BeginRename(next);
    }

    private void CancelRename()
    {
        if (_renaming is null)
            return;
        _renameEnding = true;
        try
        {
            HideRenameBox();
        }
        finally
        {
            _renameEnding = false;
        }
    }

    private void HideRenameBox()
    {
        _renaming = null;
        RenameError.IsVisible = false;
        var hadFocus = RenameBox.IsFocused;
        RenameBox.IsVisible = false;
        if (hadFocus)
            Focus();
    }

    /// <summary>While typing, only the border turns red; the reason is shown once Enter or Tab is pressed.</summary>
    private void ShowRenameValidation(bool explicitly)
    {
        if (_renaming is not { } item || _pane is null || !RenameBox.IsVisible)
            return;
        var error = _pane.ValidateInlineName(item, RenameBox.Text ?? string.Empty);
        RenameBox.Classes.Set("invalid", error is not null);
        if (error is null)
        {
            RenameError.IsVisible = false;
            return;
        }

        if (!explicitly && !RenameError.IsVisible)
            return;
        RenameErrorText.Text = error;
        Canvas.SetLeft(RenameError, Canvas.GetLeft(RenameBox));
        Canvas.SetTop(RenameError, Canvas.GetTop(RenameBox) + RenameBox.Bounds.Height + 4);
        RenameError.IsVisible = true;
    }

    /// <summary>Keeps the editor on its item while the list scrolls or resizes; commits once the item leaves the view.</summary>
    private void FollowRenamedItem()
    {
        if (_renaming is null || !RenameBox.IsVisible || _renameEnding)
            return;
        if (!PlaceRenameBox())
            _ = CommitRenameAsync(move: 0, fromFocusLoss: true);
        else if (RenameError.IsVisible)
            ShowRenameValidation(explicitly: true);
    }

    /// <summary>Lays the editor over the name of the realized row/tile of the item; false when it is not on screen.</summary>
    private bool PlaceRenameBox()
    {
        if (_renaming is not { } item || _pane is not { } pane || FindItemSurface(item.FullPath) is not { } surface)
            return false;

        var name = surface.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("itemName"));
        var origin = (name ?? (Visual)surface).TranslatePoint(default, RenameCanvas);
        var rowOrigin = surface.TranslatePoint(default, RenameCanvas);
        if (origin is not { } at || rowOrigin is not { } row)
            return false;

        var viewport = RenameCanvas.Bounds;
        if (row.Y + surface.Bounds.Height <= 0 || row.Y >= viewport.Height)
            return false;

        double left, top, width;
        if (pane.IsTileLayout)
        {
            left = row.X + 2;
            width = Math.Max(80, surface.Bounds.Width - 4);
            top = at.Y - 3;
            RenameBox.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            RenameBox.TextAlignment = Avalonia.Media.TextAlignment.Center;
            RenameBox.Height = double.NaN;
        }
        else
        {
            left = at.X - 7; // text box padding + border, so the text does not jump
            width = Math.Max(160, (name?.Bounds.Width ?? surface.Bounds.Width) + 14);
            var height = Math.Max(24, surface.Bounds.Height - 4);
            top = row.Y + ((surface.Bounds.Height - height) / 2);
            RenameBox.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
            RenameBox.TextAlignment = Avalonia.Media.TextAlignment.Left;
            RenameBox.Height = height;
        }

        width = Math.Min(width, Math.Max(80, viewport.Width - left - 8));
        RenameBox.Width = width;
        Canvas.SetLeft(RenameBox, Math.Max(0, left));
        Canvas.SetTop(RenameBox, top);
        return true;
    }

    /// <summary>The realized row/tile surface showing the item at <paramref name="path"/> (items are matched by path: a
    /// reload may replace the view model while the editor is open).</summary>
    private Border? FindItemSurface(string path)
    {
        foreach (var container in ItemsHost.GetRealizedContainers())
        {
            foreach (var border in container.GetSelfAndVisualDescendants().OfType<Border>())
            {
                if (border.DataContext is FileItemViewModel model && border.Classes.Contains("item") && PathUtil.AreEqual(model.FullPath, path))
                    return border;
            }
        }

        return null;
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
