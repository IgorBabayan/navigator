using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Collections;
using Navigator.Core.Navigation;
using Navigator.Core.Operations;
using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Core.Sorting;
using Navigator.Presentation.Commands;
using Navigator.Presentation.Details;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Services;
using Navigator.Presentation.Updates;

namespace Navigator.Presentation.Shell;

/// <summary>One application window: tabs, sidebar, preview pane, operations and notifications.</summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    public const int RecentlyClosedCapacity = 25;

    private readonly PaneServices _services;
    private readonly PinnedFolders _pins;
    private readonly BoundedStack<TabSession> _recentlyClosed = new(RecentlyClosedCapacity);
    private TabViewModel? _selectedTab;
    private bool _isPreviewVisible;
    private PreviewViewModel? _quickLook;
    private bool _isQuickLookOpen;
    private QuickLookProvider? _externalQuickLook;
    private PaneViewModel? _selectionSource;
    private bool _isCommandPaletteOpen;
    private bool _isAlwaysOnTop;
    private bool _isCompactOverlay;
    private string _paletteFilter = string.Empty;
    private bool _disposed;

    public ShellViewModel(PaneServices services, PinnedFolders pins, NotificationCenter notifications, UpdateViewModel update)
    {
        _services = services;
        _pins = pins;
        Notifications = notifications;
        Update = update;
        Sidebar = new SidebarViewModel(services, pins);
        Sidebar.NavigateRequested += (_, request) =>
        {
            if (request.NewTab)
                AddTab(request.Location);
            else
                ActivePane?.Navigate(request.Location);
        };
        Operations = new OperationsViewModel(services);
        Preview = new PreviewViewModel(services);
        _isPreviewVisible = false;

        NewTabCommand = new RelayCommand(() => AddTab(Location.Home));
        CloseTabCommand = new RelayCommand<TabViewModel>(tab => CloseTab(tab ?? SelectedTab));
        CloseOtherTabsCommand = new RelayCommand<TabViewModel>(CloseOtherTabs);
        CloseTabsToRightCommand = new RelayCommand<TabViewModel>(CloseTabsToRight);
        ReopenClosedTabCommand = new RelayCommand(ReopenClosedTab, () => _recentlyClosed.Count > 0);
        DuplicateTabCommand = new RelayCommand<TabViewModel>(tab => DuplicateTab(tab ?? SelectedTab));
        TogglePinTabCommand = new RelayCommand<TabViewModel>(TogglePin);
        NextTabCommand = new RelayCommand(() => CycleTab(+1));
        PreviousTabCommand = new RelayCommand(() => CycleTab(-1));
        ToggleSplitViewCommand = new RelayCommand(() => SelectedTab?.ToggleSplit());
        ToggleSidebarCommand = new RelayCommand(() => Sidebar.IsVisible = !Sidebar.IsVisible);
        TogglePreviewCommand = new RelayCommand(() => IsPreviewVisible = !IsPreviewVisible);
        QuickLookCommand = new AsyncRelayCommand(ToggleQuickLookAsync);
        ToggleHiddenItemsCommand = new RelayCommand(ToggleHiddenItems);
        OpenSettingsCommand = new RelayCommand(() => _services.Windows.OpenSettings());
        OpenPluginShopCommand = new RelayCommand(() => _services.Windows.OpenSettings("plugins"));
        NewWindowCommand = new RelayCommand(() => _services.Windows.OpenNewWindow());
        MoveTabToNewWindowCommand = new RelayCommand<TabViewModel>(
            tab =>
            {
                if ((tab ?? SelectedTab) is { } moving)
                    _services.Windows.MoveTabToWindow(this, moving);
            },
            _ => Tabs.Count > 1);
        ToggleAlwaysOnTopCommand = new RelayCommand(() => IsAlwaysOnTop = !IsAlwaysOnTop);
        ToggleCompactOverlayCommand = new RelayCommand(() => IsCompactOverlay = !IsCompactOverlay);
        CustomizeToolbarCommand = new RelayCommand(() => _services.Windows.OpenSettings("appearance", "Settings.Toolbar"));
        GoHomeCommand = new RelayCommand(() => ActivePane?.Navigate(Location.Home));
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _services.Operations.Undo.CanUndo);
        RedoCommand = new AsyncRelayCommand(RedoAsync, () => _services.Operations.Undo.CanRedo);
        ToggleCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = !IsCommandPaletteOpen);
        RunPaletteCommand = new RelayCommand<PaletteEntry>(entry =>
        {
            IsCommandPaletteOpen = false;
            if (entry is not null)
                Execute(entry.Id);
        });

        Tabs.CollectionChanged += (_, _) => MoveTabToNewWindowCommand.NotifyCanExecuteChanged();
        _services.Operations.Undo.Changed += OnUndoChanged;
        _services.Operations.ItemsRelocated += OnItemsRelocated;
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public SidebarViewModel Sidebar { get; }

    public OperationsViewModel Operations { get; }

    public PreviewViewModel Preview { get; }

    public NotificationCenter Notifications { get; }

    public UpdateViewModel Update { get; }

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            var previous = _selectedTab;
            if (!SetProperty(ref _selectedTab, value))
                return;
            if (previous is not null)
            {
                previous.ActivePaneChanged -= OnActivePaneChanged;
                previous.IsSelected = false;
            }

            if (value is not null)
            {
                value.ActivePaneChanged += OnActivePaneChanged;
                value.IsSelected = true;
            }

            OnActivePaneChanged(this, EventArgs.Empty);
        }
    }

    public PaneViewModel? ActivePane => _selectedTab?.ActivePane;

    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set
        {
            if (SetProperty(ref _isPreviewVisible, value))
                Preview.IsEnabled = value;
        }
    }

    /// <summary>The window stays above other windows (also while in compact overlay).</summary>
    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        set => SetProperty(ref _isAlwaysOnTop, value);
    }

    /// <summary>
    /// Compact overlay: a small window that floats above other applications, for keeping a folder at hand while
    /// working elsewhere. The window shrinks and hides the sidebar; leaving restores its previous size and state.
    /// </summary>
    public bool IsCompactOverlay
    {
        get => _isCompactOverlay;
        set => SetProperty(ref _isCompactOverlay, value);
    }

    public string WindowTitle => ActivePane is { } pane ? $"{pane.Title} — Navigator" : "Navigator";

    public bool IsCommandPaletteOpen
    {
        get => _isCommandPaletteOpen;
        set
        {
            if (SetProperty(ref _isCommandPaletteOpen, value))
            {
                PaletteFilter = string.Empty;
                RefreshPalette();
            }
        }
    }

    public string PaletteFilter
    {
        get => _paletteFilter;
        set
        {
            if (SetProperty(ref _paletteFilter, value))
                RefreshPalette();
        }
    }

    public ObservableCollection<PaletteEntry> PaletteEntries { get; } = [];

    public IRelayCommand NewTabCommand { get; }

    public IRelayCommand<TabViewModel> CloseTabCommand { get; }

    public IRelayCommand<TabViewModel> CloseOtherTabsCommand { get; }

    public IRelayCommand<TabViewModel> CloseTabsToRightCommand { get; }

    public IRelayCommand ReopenClosedTabCommand { get; }

    public IRelayCommand<TabViewModel> DuplicateTabCommand { get; }

    public IRelayCommand<TabViewModel> TogglePinTabCommand { get; }

    public IRelayCommand NextTabCommand { get; }

    public IRelayCommand PreviousTabCommand { get; }

    public IRelayCommand ToggleSplitViewCommand { get; }

    public IRelayCommand ToggleSidebarCommand { get; }

    public IRelayCommand TogglePreviewCommand { get; }

    /// <summary>Space bar: previews the selection in the chosen previewer (Settings → Files and folders → Quick Look).</summary>
    public IAsyncRelayCommand QuickLookCommand { get; }

    /// <summary>The built-in Quick Look window's content; created the first time the window opens.</summary>
    public PreviewViewModel QuickLook => _quickLook ??= CreateQuickLook();

    /// <summary>The built-in Quick Look window is shown (the window follows this flag; closing it clears it).</summary>
    public bool IsQuickLookOpen
    {
        get => _isQuickLookOpen;
        set
        {
            if (!SetProperty(ref _isQuickLookOpen, value))
                return;
            if (value && ActivePane is { } pane)
                QuickLook.Attach(pane);
            if (_quickLook is not null)
                _quickLook.IsEnabled = value;
        }
    }

    public IRelayCommand ToggleHiddenItemsCommand { get; }

    public IRelayCommand OpenSettingsCommand { get; }

    /// <summary>Settings → Plugins (install, update and remove plugins).</summary>
    public IRelayCommand OpenPluginShopCommand { get; }

    public IRelayCommand NewWindowCommand { get; }

    public IRelayCommand GoHomeCommand { get; }

    /// <summary>Tab context menu "Move to new window" (dragging a tab out of the strip does the same).</summary>
    public IRelayCommand<TabViewModel> MoveTabToNewWindowCommand { get; }

    public IRelayCommand ToggleAlwaysOnTopCommand { get; }

    public IRelayCommand ToggleCompactOverlayCommand { get; }

    /// <summary>Opens Settings → Appearance at the toolbar section (toolbar context menu).</summary>
    public IRelayCommand CustomizeToolbarCommand { get; }

    public IAsyncRelayCommand UndoCommand { get; }

    public IAsyncRelayCommand RedoCommand { get; }

    public IRelayCommand ToggleCommandPaletteCommand { get; }

    public IRelayCommand<PaletteEntry> RunPaletteCommand { get; }

    /// <summary>Raised when the last tab closes: the window should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised for view-level commands (focus address bar, search box, filter box, full screen).</summary>
    public event EventHandler<string>? ViewCommandRequested;

    public TabViewModel AddTab(Location location, Location? secondary = null, bool select = true, bool pinned = false, int? index = null)
    {
        var tab = new TabViewModel(_services, _pins, location, secondary) { IsPinned = pinned };
        tab.OpenInNewTabRequested += OnTabOpenInNewTab;
        var insertAt = index ?? (pinned ? Tabs.Count(t => t.IsPinned) : Tabs.Count);
        Tabs.Insert(Math.Clamp(insertAt, 0, Tabs.Count), tab);
        tab.Start();
        if (select || SelectedTab is null)
            SelectedTab = tab;
        return tab;
    }

    /// <summary>"Open in new tab" from a pane: the new tab goes right after the tab that asked.</summary>
    private void OnTabOpenInNewTab(object? sender, Location target)
    {
        if (sender is TabViewModel origin && Tabs.Contains(origin))
            AddTab(target, select: false, index: Tabs.IndexOf(origin) + 1);
    }

    /// <summary>
    /// Takes a live tab out of this window without disposing it (it is moving to another window). The neighbour is
    /// selected as on close; the window closes when it was the last tab. Not recorded as a closed tab.
    /// </summary>
    public bool DetachTab(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return false;

        tab.OpenInNewTabRequested -= OnTabOpenInNewTab;
        tab.Primary.Navigated -= OnPaneNavigated;
        if (tab.Secondary is { } secondary)
            secondary.Navigated -= OnPaneNavigated;
        Tabs.RemoveAt(index);
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        if (Tabs.Count == 0)
            CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Receives a tab detached from another window, at <paramref name="index"/> within its pinned/unpinned group.</summary>
    public void AdoptTab(TabViewModel tab, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (Tabs.Contains(tab))
            return;
        tab.OpenInNewTabRequested += OnTabOpenInNewTab;
        var pinnedCount = Tabs.Count(t => t.IsPinned);
        var at = tab.IsPinned
            ? Math.Clamp(index ?? pinnedCount, 0, pinnedCount)
            : Math.Clamp(index ?? Tabs.Count, pinnedCount, Tabs.Count);
        tab.IsSelected = false;
        Tabs.Insert(at, tab);
        SelectedTab = tab;
    }

    public void CloseTab(TabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
            return;

        _recentlyClosed.Push(tab.ToSession());
        ReopenClosedTabCommand.NotifyCanExecuteChanged();
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        tab.Dispose();

        if (Tabs.Count == 0)
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseOtherTabs(TabViewModel? keep)
    {
        keep ??= SelectedTab;
        foreach (var tab in Tabs.Where(t => t != keep && !t.IsPinned).ToList())
            CloseTab(tab);
    }

    private void CloseTabsToRight(TabViewModel? from)
    {
        from ??= SelectedTab;
        if (from is null)
            return;
        var index = Tabs.IndexOf(from);
        foreach (var tab in Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList())
            CloseTab(tab);
    }

    private void ReopenClosedTab()
    {
        if (!_recentlyClosed.TryPop(out var session))
            return;
        ReopenClosedTabCommand.NotifyCanExecuteChanged();
        AddTab(Location.TryParse(session.Location) ?? Location.Home, Location.TryParse(session.SecondaryLocation), pinned: session.IsPinned);
    }

    private void DuplicateTab(TabViewModel? tab)
    {
        if (tab is null)
            return;
        AddTab(tab.Primary.Location, tab.Secondary?.Location, index: Tabs.IndexOf(tab) + 1);
    }

    private void TogglePin(TabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null)
            return;
        tab.IsPinned = !tab.IsPinned;
        // Pinned tabs stay together on the left.
        Tabs.Remove(tab);
        Tabs.Insert(tab.IsPinned ? Tabs.Count(t => t.IsPinned) : Tabs.Count(t => t.IsPinned), tab);
        SelectedTab = tab;
    }

    /// <summary>Drag-reorder within the tab strip; pinned tabs cannot move behind unpinned ones and vice versa.</summary>
    public void MoveTab(TabViewModel tab, int newIndex)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0)
            return;
        var pinnedCount = Tabs.Count(t => t.IsPinned);
        var target = tab.IsPinned ? Math.Clamp(newIndex, 0, pinnedCount - 1) : Math.Clamp(newIndex, pinnedCount, Tabs.Count - 1);
        if (target != from)
            Tabs.Move(from, target);
    }

    public void SelectTabByNumber(int number)
    {
        if (Tabs.Count == 0)
            return;
        SelectedTab = number >= 9 ? Tabs[^1] : Tabs[Math.Clamp(number - 1, 0, Tabs.Count - 1)];
    }

    private void CycleTab(int delta)
    {
        if (Tabs.Count < 2 || SelectedTab is null)
            return;
        var index = (Tabs.IndexOf(SelectedTab) + delta + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }

    private void ToggleHiddenItems()
    {
        _services.Settings.Update("filesAndFolders", s => s.FilesAndFolders.ShowHiddenItems = !s.FilesAndFolders.ShowHiddenItems);
        foreach (var tab in Tabs)
        {
            tab.Primary.Refresh();
            tab.Secondary?.Refresh();
        }
    }

    private async Task UndoAsync()
    {
        var outcome = await _services.Operations.UndoAsync(_services.CreateInteraction()).ConfigureAwait(true);
        if (outcome == UndoOutcome.NotPossible)
            _services.Notifications.Show(new InAppNotification(_services.Localizer["Notify.UndoNotPossible"], string.Empty, NotificationSeverity.Warning));
    }

    private async Task RedoAsync()
    {
        var outcome = await _services.Operations.RedoAsync(_services.CreateInteraction()).ConfigureAwait(true);
        if (outcome == UndoOutcome.NotPossible)
            _services.Notifications.Show(new InAppNotification(_services.Localizer["Notify.RedoNotPossible"], string.Empty, NotificationSeverity.Warning));
    }

    /// <summary>Executes a registry command by id (keyboard shortcuts, command palette).</summary>
    public void Execute(string id)
    {
        var pane = ActivePane;
        switch (id)
        {
            case "tab.new": NewTabCommand.Execute(null); break;
            case "tab.close": CloseTab(SelectedTab); break;
            case "tab.reopen": ReopenClosedTab(); break;
            case "tab.duplicate": DuplicateTab(SelectedTab); break;
            case "tab.next": CycleTab(+1); break;
            case "tab.previous": CycleTab(-1); break;
            case "window.new": NewWindowCommand.Execute(null); break;
            case "tab.moveToNewWindow": MoveTabToNewWindowCommand.Execute(null); break;
            case "view.alwaysOnTop": ToggleAlwaysOnTopCommand.Execute(null); break;
            case "view.compactOverlay": ToggleCompactOverlayCommand.Execute(null); break;
            case "view.columns": pane?.SetLayoutCommand.Execute(FolderLayout.Columns); break;
            case "view.splitPane": ToggleSplitViewCommand.Execute(null); break;
            case "view.sidebar": ToggleSidebarCommand.Execute(null); break;
            case "view.previewPane": TogglePreviewCommand.Execute(null); break;
            case "item.quickLook": QuickLookCommand.Execute(null); break;
            case "view.hiddenItems": ToggleHiddenItems(); break;
            case "view.details": pane?.SetLayoutCommand.Execute(FolderLayout.Details); break;
            case "view.list": pane?.SetLayoutCommand.Execute(FolderLayout.List); break;
            case "view.tiles": pane?.SetLayoutCommand.Execute(FolderLayout.Tiles); break;
            case "view.gridMedium": pane?.SetLayoutCommand.Execute(FolderLayout.MediumIcons); break;
            case "view.gridLarge": pane?.SetLayoutCommand.Execute(FolderLayout.LargeIcons); break;
            case "view.commandPalette": ToggleCommandPaletteCommand.Execute(null); break;
            case "nav.back": pane?.GoBackCommand.Execute(null); break;
            case "nav.forward": pane?.GoForwardCommand.Execute(null); break;
            case "nav.up": pane?.GoUpCommand.Execute(null); break;
            case "nav.refresh": pane?.RefreshCommand.Execute(null); break;
            case "nav.home": GoHomeCommand.Execute(null); break;
            case "item.open": pane?.OpenSelectedCommand.Execute(null); break;
            case "item.newFolder": pane?.NewFolderCommand.Execute(null); break;
            case "item.newFile": pane?.NewFileCommand.Execute(null); break;
            case "item.rename": pane?.RenameCommand.Execute(null); break;
            case "item.multiRename": pane?.MultiRenameCommand.Execute(null); break;
            case "item.delete": pane?.DeleteCommand.Execute(null); break;
            case "item.deletePermanently": pane?.DeletePermanentlyCommand.Execute(null); break;
            case "item.copy": pane?.CopyCommand.Execute(null); break;
            case "item.cut": pane?.CutCommand.Execute(null); break;
            case "item.paste": pane?.PasteCommand.Execute(null); break;
            case "item.duplicate": pane?.DuplicateCommand.Execute(null); break;
            case "item.copyPath": pane?.CopyPathCommand.Execute(null); break;
            case "item.selectAll": pane?.SelectAllCommand.Execute(null); break;
            case "item.invertSelection": pane?.InvertSelectionCommand.Execute(null); break;
            case "item.clearSelection": pane?.ClearSelectionCommand.Execute(null); break;
            case "item.properties": pane?.PropertiesCommand.Execute(null); break;
            case "item.compress": pane?.CompressCommand.Execute(null); break;
            case "item.extract": pane?.ExtractCommand.Execute(null); break;
            case "item.extractFiles": pane?.ExtractFilesCommand.Execute(null); break;
            case "item.pin": pane?.PinSelectedCommand.Execute(null); break;
            case "item.openTerminal": pane?.OpenTerminalCommand.Execute(null); break;
            case "item.openInEditor": pane?.OpenInCodeEditorCommand.Execute(null); break;
            case "edit.undo": UndoCommand.Execute(null); break;
            case "edit.redo": RedoCommand.Execute(null); break;
            case "app.settings": OpenSettingsCommand.Execute(null); break;
            case "app.pluginShop": OpenPluginShopCommand.Execute(null); break;
            default:
                ViewCommandRequested?.Invoke(this, id);
                break;
        }
    }

    private void RefreshPalette()
    {
        PaletteEntries.Clear();
        if (!_isCommandPaletteOpen)
            return;
        var isMac = OperatingSystem.IsMacOS();
        foreach (var command in CommandRegistry.All)
        {
            if (command.IsGlobal)
                continue;
            var name = _services.Localizer[command.NameKey];
            if (_paletteFilter.Length > 0 && !name.Contains(_paletteFilter, StringComparison.CurrentCultureIgnoreCase))
                continue;
            var gestures = CommandRegistry.GesturesFor(command.Id, _services.Settings.Current, isMac);
            var gesture = gestures.Count > 0 ? CommandRegistry.FormatForDisplay(gestures[0]) : string.Empty;
            PaletteEntries.Add(new PaletteEntry(command.Id, name, gesture));
        }
    }

    public WindowSession ToSession(double x, double y, double width, double height, bool maximized) => new()
    {
        X = x,
        Y = y,
        Width = width,
        Height = height,
        IsMaximized = maximized,
        SelectedTabIndex = SelectedTab is null ? 0 : Tabs.IndexOf(SelectedTab),
        Tabs = [.. Tabs.Select(t => t.ToSession())],
    };

    public void RestoreTabs(IEnumerable<TabSession> tabs, int selectedIndex)
    {
        foreach (var tab in tabs)
            AddTab(Location.TryParse(tab.Location) ?? Location.Home, Location.TryParse(tab.SecondaryLocation), select: false, pinned: tab.IsPinned);
        if (Tabs.Count > 0)
            SelectedTab = Tabs[Math.Clamp(selectedIndex, 0, Tabs.Count - 1)];
    }

    public IReadOnlyList<TabSession> RecentlyClosed => [.. _recentlyClosed.Items];

    public void RestoreRecentlyClosed(IEnumerable<TabSession> sessions)
    {
        foreach (var session in sessions.Reverse())
            _recentlyClosed.Push(session);
        ReopenClosedTabCommand.NotifyCanExecuteChanged();
    }

    private void OnActivePaneChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ActivePane));
        OnPropertyChanged(nameof(WindowTitle));
        if (ActivePane is { } pane)
        {
            Sidebar.SyncSelection(pane.Location);
            Preview.Attach(pane);
            if (_isQuickLookOpen)
                QuickLook.Attach(pane);
            if (!ReferenceEquals(_selectionSource, pane))
            {
                if (_selectionSource is not null)
                    _selectionSource.SelectionChanged -= OnActiveSelectionChanged;
                _selectionSource = pane;
                pane.SelectionChanged += OnActiveSelectionChanged;
            }
            pane.Navigated -= OnPaneNavigated;
            pane.Navigated += OnPaneNavigated;
        }
    }

    private PreviewViewModel CreateQuickLook()
        => new(_services, PreviewViewModel.QuickLookImageSize, TimeSpan.FromMilliseconds(40));

    /// <summary>
    /// Opens or closes the preview of the focused item. External previewers (QuickLook, Seer, PowerToys Peek, macOS
    /// Quick Look, Sushi) get the file when chosen — or, with "Automatic", when one is running; Navigator's own window is
    /// used otherwise and whenever the external one cannot be reached.
    /// </summary>
    public async Task ToggleQuickLookAsync()
    {
        if (_isQuickLookOpen)
        {
            IsQuickLookOpen = false;
            return;
        }

        if (ActivePane is not { } pane || ((pane.Selection.SelectedItems is { Count: > 0 } selected ? selected[0] : null) ?? pane.Selection.FocusedItem) is not { } item)
            return;

        var setting = _services.Settings.Current.FilesAndFolders.QuickLook;
        var external = setting switch
        {
            QuickLookProvider.BuiltIn => null,
            QuickLookProvider.Automatic => _services.QuickLook.ResolveAutomatic(),
            _ => setting,
        };

        if (external is { } provider && !item.Entry.IsRemote)
        {
            if (await _services.QuickLook.ToggleAsync(provider, item.FullPath).ConfigureAwait(true))
            {
                _externalQuickLook = provider;
                return;
            }

            _externalQuickLook = null;
        }

        IsQuickLookOpen = true;
    }

    /// <summary>Selection moved while an external previewer shows a file: it follows, like it does in Explorer/Finder.</summary>
    private void OnActiveSelectionChanged(object? sender, EventArgs e)
    {
        if (_externalQuickLook is not { } provider || sender is not PaneViewModel pane || pane.Selection.SelectedItems is not [{ Entry.IsRemote: false } item])
            return;
        _ = _services.QuickLook.SwitchAsync(provider, item.FullPath);
    }

    private void OnPaneNavigated(object? sender, EventArgs e)
    {
        if (sender != ActivePane)
            return;
        OnPropertyChanged(nameof(WindowTitle));
        Sidebar.SyncSelection(ActivePane!.Location);
    }

    private void OnUndoChanged(object? sender, EventArgs e)
        => _services.Dispatcher.Post(() =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        });

    private void OnItemsRelocated(object? sender, IReadOnlyList<(string OldPath, string NewPath)> moves)
    {
        _services.Tags.OnItemsRelocated(moves);
        _services.Dispatcher.Post(() =>
        {
            foreach (var (oldPath, newPath) in moves)
            {
                if (_pins.IsPinned(oldPath))
                {
                    _pins.Unpin(oldPath);
                    _pins.Pin(newPath);
                }

                foreach (var pane in Tabs.SelectMany(t => new[] { t.Primary, t.Secondary }).Where(p => p is not null))
                {
                    var current = pane!.Location;
                    if (current.IsFolder && Navigator.Core.IO.PathUtil.IsSameOrDescendant(oldPath, current.Path!))
                    {
                        // The folder being shown was renamed or moved: follow it instead of falling back to the parent.
                        pane.History.ReplaceLocation(current, Location.Folder(newPath + current.Path![oldPath.Length..]));
                        pane.Refresh();
                    }
                }
            }
        });
    }

    /// <summary>Idempotent: safe to call from window close and again from application shutdown.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _services.Operations.Undo.Changed -= OnUndoChanged;
        _services.Operations.ItemsRelocated -= OnItemsRelocated;
        foreach (var tab in Tabs)
            tab.Dispose();
        Tabs.Clear();
        Sidebar.Dispose();
        Operations.Dispose();
        Preview.Dispose();
        _quickLook?.Dispose();
        if (_selectionSource is not null)
            _selectionSource.SelectionChanged -= OnActiveSelectionChanged;
        _selectionSource = null;
    }
}

public sealed record PaletteEntry(string Id, string Name, string Gesture);
