using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Navigator.App.Controls;
using Navigator.App.Services;
using Navigator.Core.Settings;
using Navigator.Presentation.Commands;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Services;
using Navigator.Presentation.Shell;

namespace Navigator.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly PaneServices _services;
    private readonly ThemeService _theme;
    private readonly List<(KeyGesture Gesture, string CommandId)> _gestures = [];
    private bool _closeConfirmed;
    private TabViewModel? _draggedTab;
    private Point _dragStart;
    private PaneViewModel? _mouseNavigationPane;
    private string? _addressBeforeBrowsing;
    private int _crumbMenuVersion;
    private MenuFlyout? _crumbMenu;
    private Button? _lastClosedCrumbMenuOwner;
    private long _lastCrumbMenuClosedAt;
    private WindowState _stateBeforeMinimize = WindowState.Normal;

    public MainWindow()
        : this(ActivatorUtilities.CreateInstance<ShellViewModel>(App.Services), App.Services.GetRequiredService<PaneServices>())
    {
    }

    public MainWindow(ShellViewModel shell, PaneServices services)
    {
        Shell = shell;
        _services = services;
        _theme = App.Services.GetRequiredService<ThemeService>();
        DataContext = shell;
        InitializeComponent();

        ConfigureCaptionInsets();
        ApplyBackdrop();
        RebuildKeyGestures();
        ApplyToolbarLabels();
        UpdateSidebarColumn();

        _theme.BackdropChanged += OnBackdropChanged;
        _services.Settings.Changed += OnSettingsChanged;
        shell.Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SidebarViewModel.IsVisible) or nameof(SidebarViewModel.Width))
                UpdateSidebarColumn();
        };
        shell.CloseRequested += (_, _) =>
        {
            _closeConfirmed = true;
            Close();
        };
        shell.ViewCommandRequested += (_, id) => OnViewCommand(id);
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.IsCommandPaletteOpen) && shell.IsCommandPaletteOpen)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PaletteBox.Focus());
        };

        // The address box can also be opened by the view model (clicking the Home/Trash/Tag crumb): focus it whenever it appears.
        AddressBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && AddressBox.IsVisible)
                FocusAddressBox();
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // Mouse side buttons (XButton1 = back, XButton2 = forward) navigate like a browser, wherever the pointer is.
        AddHandler(PointerPressedEvent, OnMouseNavigationPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnMouseNavigationReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => Shell.ActivePane?.CloseAddressDropDown();
        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        TitleBar.DoubleTapped += (_, _) => ToggleMaximize();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _theme.BackdropChanged -= OnBackdropChanged;
            _services.Settings.Changed -= OnSettingsChanged;
        };
    }

    public ShellViewModel Shell { get; }

    public WindowSession CaptureSession()
        => Shell.ToSession(Position.X, Position.Y, Bounds.Width, Bounds.Height, WindowState == WindowState.Maximized);

    /// <summary>Global shortcut / tray: un-minimizes to the state the window had before (e.g. maximized) and focuses it.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _stateBeforeMinimize;
        if (!IsVisible)
            Show();
        Activate();

        // Activate() alone is refused by Windows' foreground lock when another app is in front (global shortcut).
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
            Navigator.Platform.Hotkeys.WindowsForeground.BringToFront(handle.Handle);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() == WindowState.Minimized)
            _stateBeforeMinimize = change.GetOldValue<WindowState>();
    }

    // ── Window chrome ─────────────────────────────────────────

    /// <summary>Leaves room for the native caption buttons: left on macOS (traffic lights), right elsewhere.</summary>
    private void ConfigureCaptionInsets()
    {
        if (OperatingSystem.IsMacOS())
        {
            LeftCaptionInset.Width = 78;
            RightCaptionInset.Width = 8;
        }
        else
        {
            LeftCaptionInset.Width = 8;
            RightCaptionInset.Width = 140;
        }
    }

    private void OnBackdropChanged(object? sender, EventArgs e) => ApplyBackdrop();

    private void ApplyBackdrop()
    {
        var appearance = _theme.Appearance;
        TransparencyLevelHint = appearance.Backdrop switch
        {
            BackdropMaterial.Mica => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None],
            BackdropMaterial.Acrylic => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None],
            _ => [WindowTransparencyLevel.None],
        };

        // With no real backdrop the tint must be opaque, otherwise the desktop would show through.
        var native = ActualTransparencyLevel != WindowTransparencyLevel.None && appearance.Backdrop != BackdropMaterial.None;
        BackdropTint.Opacity = native ? 1.0 - (appearance.Transparency * 0.6) : 1.0;

        if (appearance.BackgroundImagePath is { } image && File.Exists(image))
        {
            try
            {
                using var stream = File.OpenRead(image);
                BackgroundImage.Source = Bitmap.DecodeToWidth(stream, 1920);
                BackgroundImage.Opacity = appearance.BackgroundImageOpacity;
                BackgroundImage.IsVisible = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
            {
                BackgroundImage.IsVisible = false;
            }
        }
        else
        {
            BackgroundImage.IsVisible = false;
            BackgroundImage.Source = null;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount > 1)
            return;
        BeginMoveDrag(e);
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ── Tabs ──────────────────────────────────────────────────

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: TabViewModel tab })
            return;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            Shell.CloseTab(tab);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            Shell.SelectedTab = tab;
            _draggedTab = tab;
            _dragStart = point.Position;
            e.Handled = true; // the title bar must not start a window drag from a tab
        }
    }

    /// <summary>Drag-to-reorder: dropping a tab over another tab moves it to that position.</summary>
    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragged = _draggedTab;
        _draggedTab = null;
        if (dragged is null)
            return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 8)
            return;

        foreach (var container in TabStrip.GetRealizedContainers())
        {
            var origin = container.TranslatePoint(new Point(0, 0), this);
            if (origin is { } p && position.X >= p.X && position.X <= p.X + container.Bounds.Width && container.DataContext is TabViewModel target)
            {
                Shell.MoveTab(dragged, Shell.Tabs.IndexOf(target));
                break;
            }
        }
    }

    // ── Omnibar and search ────────────────────────────────────

    /// <summary>A left click on the omnibar's empty area switches it to the editable address box.</summary>
    private void OnOmnibarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            EnterAddressEdit();
    }

    private void OnEditAddressClick(object? sender, RoutedEventArgs e) => EnterAddressEdit();

    /// <summary>Every location can be replaced by typing a path, including Home, Trash, tags and search results.</summary>
    private void EnterAddressEdit()
    {
        if (Shell.ActivePane is not { } pane)
            return;
        pane.IsEditingAddress = true;
        FocusAddressBox();
    }

    private void FocusAddressBox()
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!AddressBox.IsVisible)
                return;
            AddressBox.Focus();
            AddressBox.SelectAll();
        });

    /// <summary>
    /// Address box keys: Enter navigates, Escape closes the drop-down and then leaves edit mode, Up/Down browse the
    /// drop-down (the highlighted entry is written into the box), Tab completes the highlighted or first folder and
    /// continues into it, F4 / Alt+Down toggle the typed-address history.
    /// </summary>
    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell.ActivePane is not { } pane)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                pane.NavigateToAddressCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                if (pane.IsAddressDropDownOpen)
                {
                    RestoreTypedAddress(pane);
                    pane.CloseAddressDropDown();
                }
                else
                {
                    pane.IsEditingAddress = false;
                }

                e.Handled = true;
                break;
            case Key.F4:
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt:
                pane.ToggleAddressHistory();
                e.Handled = true;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                if (pane.IsAddressDropDownOpen)
                    MoveSuggestion(pane, +1);
                else
                    pane.ShowAddressHistory();
                e.Handled = true;
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.None && pane.IsAddressDropDownOpen:
                MoveSuggestion(pane, -1);
                e.Handled = true;
                break;
            case Key.Tab when e.KeyModifiers == KeyModifiers.None && pane.IsAddressDropDownOpen && pane.AddressSuggestions.Count > 0:
                CompleteSuggestion(pane);
                e.Handled = true;
                break;
        }
    }

    private void MoveSuggestion(PaneViewModel pane, int delta)
    {
        var count = pane.AddressSuggestions.Count;
        if (count == 0)
            return;

        var index = SuggestionList.SelectedIndex;
        if (index < 0)
            _addressBeforeBrowsing = pane.AddressText;
        index = index < 0 && delta < 0 ? count - 1 : index + delta;

        if (index < 0 || index >= count)
        {
            // Moving past either end returns to the text that was typed, like a browser's address bar.
            SuggestionList.SelectedIndex = -1;
            RestoreTypedAddress(pane);
            return;
        }

        SuggestionList.SelectedIndex = index;
        SuggestionList.ScrollIntoView(index);
        SetAddressText(pane, pane.AddressSuggestions[index].Text, silently: true);
    }

    private void CompleteSuggestion(PaneViewModel pane)
    {
        var suggestion = SuggestionList.SelectedItem as AddressSuggestion ?? pane.AddressSuggestions[0];
        var text = suggestion.Kind == AddressSuggestionKind.Folder ? suggestion.Text + Path.DirectorySeparatorChar : suggestion.Text;
        _addressBeforeBrowsing = null;
        SetAddressText(pane, text, silently: false);
    }

    private void RestoreTypedAddress(PaneViewModel pane)
    {
        if (_addressBeforeBrowsing is { } typed)
            SetAddressText(pane, typed, silently: true);
        _addressBeforeBrowsing = null;
    }

    private void SetAddressText(PaneViewModel pane, string text, bool silently)
    {
        if (silently)
            pane.SetAddressTextSilently(text);
        else
            pane.AddressText = text;
        AddressBox.CaretIndex = text.Length;
        AddressBox.SelectionStart = text.Length;
        AddressBox.SelectionEnd = text.Length;
    }

    /// <summary>
    /// Leaving the address box ends edit mode, except when the pointer is on the omnibar's own drop-down or history
    /// arrow (clicking those may move focus for a moment): then the box gets its focus back.
    /// </summary>
    private void OnAddressLostFocus(object? sender, RoutedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Shell.ActivePane is not { IsEditingAddress: true } pane || AddressBox.IsFocused)
                return;
            if (SuggestionCard.IsPointerOver || HistoryButton.IsPointerOver)
            {
                AddressBox.Focus();
                return;
            }

            _addressBeforeBrowsing = null;
            pane.IsEditingAddress = false;
        });

    private void OnSuggestionTapped(object? sender, TappedEventArgs e)
    {
        if (Shell.ActivePane is not { } pane || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not AddressSuggestion suggestion)
            return;
        _addressBeforeBrowsing = null;
        pane.AcceptAddressSuggestion(suggestion);
        e.Handled = true;
    }

    private void OnHistoryButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Shell.ActivePane is not { } pane)
            return;
        var wasEditing = pane.IsEditingAddress;
        pane.ToggleAddressHistory();
        if (wasEditing)
            AddressBox.Focus(); // entering edit mode focuses the box through its IsVisible change
    }

    /// <summary>Middle-click on a breadcrumb segment opens it in a new tab.</summary>
    private void OnCrumbPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle || sender is not Control { DataContext: BreadcrumbViewModel { HasPath: true } crumb })
            return;
        Shell.ActivePane?.OpenInNewTab(Navigator.Core.Navigation.Location.Folder(crumb.Path));
        e.Handled = true;
    }

    /// <summary>Keeps the current folder in view: a long path scrolls left and the leading button turns into «.</summary>
    private void OnCrumbScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.X != 0 || e.ViewportDelta.X != 0)
            CrumbScroller.Offset = new Vector(Math.Max(0, CrumbScroller.Extent.Width - CrumbScroller.Viewport.Width), 0);
        var overflow = CrumbScroller.Offset.X > 0.5;
        RootChevronIcon.IsVisible = !overflow;
        OverflowGlyph.IsVisible = overflow;
    }

    /// <summary>The chevron after a segment lists its sub-folders; the one on the current path is shown bold.</summary>
    private async void OnCrumbChevronClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BreadcrumbViewModel crumb } button || Shell.ActivePane is not { } pane)
            return;
        await ShowLocationMenuAsync(button, pane, [], () => pane.GetBreadcrumbChildrenAsync(crumb)).ConfigureAwait(true);
    }

    /// <summary>The leading button lists the segments scrolled out of view (nearest first), then the root locations.</summary>
    private async void OnRootChevronClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Shell.ActivePane is not { } pane)
            return;

        var hidden = new List<BreadcrumbChild>();
        foreach (var container in CrumbItems.GetRealizedContainers())
        {
            if (container.Bounds.X < CrumbScroller.Offset.X - 0.5 && container.DataContext is BreadcrumbViewModel { HasPath: true } crumb)
                hidden.Add(new BreadcrumbChild(crumb.Name, Navigator.Core.Navigation.Location.Folder(crumb.Path), "Folder", IsOnPath: false));
        }

        hidden.Reverse();
        await ShowLocationMenuAsync(button, pane, hidden, () => pane.GetRootLocationsAsync()).ConfigureAwait(true);
    }

    /// <summary>
    /// Builds a location menu and opens it under <paramref name="button"/>. The menu is opened explicitly rather than
    /// through <c>Button.Flyout</c>: a button opens its flyout before raising Click, and an empty MenuFlyout does not
    /// open at all. Fast listings are shown complete; slow ones (network folders) open with a "Loading…" row that is
    /// replaced when the listing arrives. Clicking the same chevron while its menu is open closes it.
    /// </summary>
    private async Task ShowLocationMenuAsync(Button button, PaneViewModel pane, List<BreadcrumbChild> leading, Func<Task<IReadOnlyList<BreadcrumbChild>>> load)
    {
        // The press that dismissed this button's own menu must not reopen it (toggle behaviour).
        if (ReferenceEquals(_lastClosedCrumbMenuOwner, button) && Environment.TickCount64 - _lastCrumbMenuClosedAt < 300)
        {
            _lastClosedCrumbMenuOwner = null;
            return;
        }

        _crumbMenu?.Hide();
        var version = ++_crumbMenuVersion;
        var localizer = _services.Localizer;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.FlyoutPresenterClasses.Add("crumbs");
        foreach (var child in leading)
            menu.Items.Add(CreateLocationItem(menu, pane, child));
        if (leading.Count > 0)
            menu.Items.Add(new Separator());

        var loadTask = LoadLocationsAsync(load);
        await Task.WhenAny(loadTask, Task.Delay(150)).ConfigureAwait(true);
        if (version != _crumbMenuVersion)
            return; // another chevron was clicked meanwhile

        MenuItem? loading = null;
        if (loadTask.IsCompleted)
        {
            AddLocationItems(menu, pane, await loadTask.ConfigureAwait(true));
        }
        else
        {
            loading = new MenuItem { Header = localizer["Omnibar.Loading"], IsEnabled = false };
            menu.Items.Add(loading);
        }

        var icon = button.FindDescendantOfType<IconView>();
        var restoreKind = icon?.Kind;
        if (icon is not null && restoreKind == "ChevronRight")
            icon.Kind = "ChevronDown";

        menu.Closed += (_, _) =>
        {
            if (icon is not null && restoreKind is not null)
                icon.Kind = restoreKind;
            _lastClosedCrumbMenuOwner = button;
            _lastCrumbMenuClosedAt = Environment.TickCount64;
            if (ReferenceEquals(_crumbMenu, menu))
                _crumbMenu = null;
        };

        _crumbMenu = menu;
        menu.ShowAt(button);

        if (loading is null)
            return;
        var children = await loadTask.ConfigureAwait(true);
        if (version != _crumbMenuVersion || !ReferenceEquals(_crumbMenu, menu))
            return;
        menu.Items.Remove(loading);
        AddLocationItems(menu, pane, children);
    }

    private static async Task<IReadOnlyList<BreadcrumbChild>> LoadLocationsAsync(Func<Task<IReadOnlyList<BreadcrumbChild>>> load)
    {
        try
        {
            return await load().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return [];
        }
    }

    private void AddLocationItems(MenuFlyout menu, PaneViewModel pane, IReadOnlyList<BreadcrumbChild> children)
    {
        if (children.Count == 0)
            menu.Items.Add(new MenuItem { Header = _services.Localizer["Omnibar.NoSubfolders"], IsEnabled = false });
        foreach (var child in children)
            menu.Items.Add(CreateLocationItem(menu, pane, child));
    }

    private static MenuItem CreateLocationItem(MenuFlyout menu, PaneViewModel pane, BreadcrumbChild child)
    {
        var item = new MenuItem
        {
            Header = child.Name,
            Icon = new IconView { Kind = child.IconKey, Width = 16, Height = 16 },
            FontWeight = child.IsOnPath ? FontWeight.SemiBold : FontWeight.Normal,
        };
        item.Click += (_, _) => pane.Navigate(child.Location);
        item.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Middle)
                return;
            pane.OpenInNewTab(child.Location);
            menu.Hide();
            e.Handled = true;
        };
        return item;
    }

    // ── Mouse back / forward buttons ──────────────────────────

    /// <summary>
    /// Side buttons act on release, like a browser, on the pane under the pointer (which becomes the active pane in
    /// split view). The press is consumed so lists and buttons never treat it as a click.
    /// </summary>
    private void OnMouseNavigationPressed(object? sender, PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed))
            return;

        var paneView = (e.Source as Visual)?.FindAncestorOfType<PaneView>(includeSelf: true);
        if (paneView?.Pane is { } pane && paneView.FindAncestorOfType<TabContentView>()?.DataContext is TabViewModel tab)
            tab.ActivePane = pane;
        _mouseNavigationPane = paneView?.Pane ?? Shell.ActivePane;
        e.Handled = true;
    }

    private void OnMouseNavigationReleased(object? sender, PointerReleasedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Released or PointerUpdateKind.XButton2Released))
            return;

        var pane = _mouseNavigationPane;
        _mouseNavigationPane = null;
        e.Handled = true;
        if (pane is null)
            return;
        if (kind == PointerUpdateKind.XButton1Released)
            pane.GoBack();
        else
            pane.GoForward();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Shell.ActivePane is { } pane)
        {
            pane.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    // ── Command palette ───────────────────────────────────────

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Shell.IsCommandPaletteOpen = false;
                e.Handled = true;
                break;
            case Key.Down:
                PaletteList.SelectedIndex = Math.Min(PaletteList.SelectedIndex + 1, Shell.PaletteEntries.Count - 1);
                e.Handled = true;
                break;
            case Key.Up:
                PaletteList.SelectedIndex = Math.Max(PaletteList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                var entry = PaletteList.SelectedItem as PaletteEntry ?? Shell.PaletteEntries.FirstOrDefault();
                Shell.RunPaletteCommand.Execute(entry);
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteDoubleTapped(object? sender, TappedEventArgs e) => Shell.RunPaletteCommand.Execute(PaletteList.SelectedItem as PaletteEntry);

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e) => Shell.IsCommandPaletteOpen = false;

    // ── Sidebar column ────────────────────────────────────────

    private void UpdateSidebarColumn()
    {
        var visible = Shell.Sidebar.IsVisible;
        SidebarHost.IsVisible = visible;
        SidebarSplitter.IsVisible = visible;
        ContentGrid.ColumnDefinitions[0].Width = visible ? new GridLength(Shell.Sidebar.Width) : new GridLength(0);
        ContentGrid.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
    }

    private void OnSidebarResized(object? sender, Avalonia.Input.VectorEventArgs e)
        => Shell.Sidebar.Width = ContentGrid.ColumnDefinitions[0].ActualWidth;

    // ── Keyboard ──────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, string section)
    {
        if (section is "keyBindings" or "*")
            Avalonia.Threading.Dispatcher.UIThread.Post(RebuildKeyGestures);
        if (section is "appearance" or "*")
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyToolbarLabels);
    }

    /// <summary>Settings → Appearance → "Show labels on toolbar buttons": icon-only buttons keep their tooltips.</summary>
    private void ApplyToolbarLabels() => CommandBar.Classes.Set("noLabels", !_services.Settings.Current.Appearance.ShowToolbarLabels);

    private void RebuildKeyGestures()
    {
        _gestures.Clear();
        var isMac = OperatingSystem.IsMacOS();
        foreach (var command in CommandRegistry.All)
        {
            if (command.IsGlobal)
                continue; // registered with the OS by GlobalHotkeyController
            foreach (var text in CommandRegistry.GesturesFor(command.Id, _services.Settings.Current, isMac))
            {
                try
                {
                    _gestures.Add((KeyGesture.Parse(text.Replace("Cmd", "Meta", StringComparison.Ordinal)), command.Id));
                }
                catch (ArgumentException)
                {
                    // An invalid user-defined gesture is ignored (Settings shows it as text for correction).
                }
            }
        }
    }

    /// <summary>
    /// Global shortcuts. While a text box has focus, editing keys (copy, paste, delete, select all…) belong to the
    /// text box; only commands that cannot collide with text editing are handled.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Browser keys (multimedia keyboards, and mice whose drivers map side buttons to them) work everywhere.
        if (e.Key is Key.BrowserBack or Key.BrowserForward && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.BrowserBack)
                Shell.ActivePane?.GoBack();
            else
                Shell.ActivePane?.GoForward();
            e.Handled = true;
            return;
        }

        var inText = FocusManager?.GetFocusedElement() is TextBox;
        if (!inText && e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None && Shell.ActivePane is { } historyPane)
        {
            historyPane.ShowAddressHistory();
            FocusAddressBox();
            e.Handled = true;
            return;
        }

        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == primary && e.Key is >= Key.D1 and <= Key.D9)
        {
            Shell.SelectTabByNumber(e.Key - Key.D0);
            e.Handled = true;
            return;
        }

        foreach (var (gesture, id) in _gestures)
        {
            if (!gesture.Matches(e))
                continue;
            if (inText && (id.StartsWith("item.", StringComparison.Ordinal) || id.StartsWith("edit.", StringComparison.Ordinal) || id is "nav.back" or "nav.forward" or "nav.up"))
                return;
            Shell.Execute(id);
            e.Handled = true;
            return;
        }

        if (!inText && e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None)
        {
            Shell.ActivePane?.GoBackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewCommand(string id)
    {
        switch (id)
        {
            case "nav.address":
                EnterAddressEdit();
                break;
            case "nav.search":
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case "app.fullScreen":
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                break;
            case "view.filter":
                if (this.FindDescendantOfType<PaneView>() is { } pane)
                    pane.FocusFilter();
                break;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || Shell.Tabs.Count < 2 || !_services.Settings.Current.General.ConfirmCloseWithMultipleTabs)
            return;

        e.Cancel = true;
        var localizer = _services.Localizer;
        var answer = await _services.Dialogs.ConfirmAsync(new ConfirmRequest(
            localizer["Dialog.CloseWindowTitle"], localizer.Format("Dialog.CloseWindowMessage", Shell.Tabs.Count), localizer["Dialog.CloseWindow"],
            CheckBoxText: localizer["Dialog.DontAskAgain"])).ConfigureAwait(true);
        if (answer.Result != DialogResult.Primary)
            return;
        if (answer.IsChecked)
            _services.Settings.Update("general", s => s.General.ConfirmCloseWithMultipleTabs = false);
        _closeConfirmed = true;
        Close();
    }
}
