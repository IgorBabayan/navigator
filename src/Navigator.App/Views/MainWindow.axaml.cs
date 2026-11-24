using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Navigator.App.Services;
using Navigator.Core.Settings;
using Navigator.Presentation.Commands;
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

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
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

    private void OnOmnibarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Handled)
            EnterAddressEdit();
    }

    private void EnterAddressEdit()
    {
        if (Shell.ActivePane is not { IsFolder: true } pane)
            return;
        pane.IsEditingAddress = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        });
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell.ActivePane is not { } pane)
            return;
        if (e.Key == Key.Enter)
        {
            pane.NavigateToAddressCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            pane.IsEditingAddress = false;
            e.Handled = true;
        }
    }

    private void OnAddressLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Shell.ActivePane is { } pane)
            pane.IsEditingAddress = false;
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
    }

    private void RebuildKeyGestures()
    {
        _gestures.Clear();
        var isMac = OperatingSystem.IsMacOS();
        foreach (var command in CommandRegistry.All)
        {
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
        var inText = FocusManager?.GetFocusedElement() is TextBox;
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
