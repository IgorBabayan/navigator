using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Plugins;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Shell;

/// <summary>A plugin action on a sidebar section header or in an entry's context menu.</summary>
public sealed class SidebarCommandViewModel(string title, string iconKey, bool isDestructive, IAsyncRelayCommand command)
{
    public string Title { get; } = title;

    public string IconKey { get; } = iconKey;

    public bool IsDestructive { get; } = isDestructive;

    public IAsyncRelayCommand Command { get; } = command;
}

public sealed class SidebarItemViewModel(string name, string iconKey, Location location, string? detail = null, string? color = null, string? systemIconKey = null, bool isPinned = false) : ObservableObject
{
    private bool _isSelected;

    public string Name { get; } = name;

    /// <summary>Navigator's own icon; also the fallback while (or where) the OS icon is unavailable.</summary>
    public string IconKey { get; } = iconKey;

    private string? _systemIconKey = systemIconKey;

    /// <summary>OS-drawn icon (<see cref="SystemIconKey"/>), or null to always use <see cref="IconKey"/>. Changes for the trash (empty/full).</summary>
    public string? SystemIconKey
    {
        get => _systemIconKey;
        set => SetProperty(ref _systemIconKey, value);
    }

    public Location Location { get; } = location;

    public string? Detail { get; } = detail;

    /// <summary>Path, plus the detail line (e.g. free space of a drive) when there is one.</summary>
    public string? ToolTip { get; } = detail is null ? location.Path : location.Path is null ? detail : location.Path + Environment.NewLine + detail;

    /// <summary>Tag color ("#RRGGBB") for tag entries.</summary>
    public string? Color { get; } = color;

    public bool IsTag => Color is not null;

    /// <summary>A pinned folder (quick access): offers move up/down and unpin.</summary>
    public bool IsPinned { get; } = isPinned;

    public string? Path => Location.Path;

    /// <summary>Context menu actions contributed by a plugin (e.g. "Edit…", "Remove" for a saved server).</summary>
    public IReadOnlyList<SidebarCommandViewModel> Commands { get; init; } = [];

    public bool HasCommands => Commands.Count > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class SidebarSectionViewModel(string id, string title, string iconKey, string? systemIconKey = null) : ObservableObject
{
    private bool _isExpanded = true;

    public string Id { get; } = id;

    public string Title { get; } = title;

    /// <summary>Navigator's own section icon; the fallback for <see cref="SystemIconKey"/>.</summary>
    public string IconKey { get; } = iconKey;

    /// <summary>OS-drawn section icon where the OS has a matching one (e.g. "This PC" for drives).</summary>
    public string? SystemIconKey { get; } = systemIconKey;

    public ObservableCollection<SidebarItemViewModel> Items { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsReorderable => Id == "pinned";

    /// <summary>Header buttons contributed by a plugin (e.g. "Add connection").</summary>
    public IReadOnlyList<SidebarCommandViewModel> Commands { get; init; } = [];

    public bool HasCommands => Commands.Count > 0;

    /// <summary>The plugin section this view model shows, or null for Navigator's own sections.</summary>
    internal IPluginSidebarSection? Source { get; init; }
}

/// <summary>
/// Navigation sidebar: Home, pinned folders, drives, plugin sections (Network, SSH, FTP…), tags, trash.
/// Shared look, per-window instance. Plugin entries are fetched off the UI thread and each section refreshes on its own.
/// </summary>
public sealed class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly PaneServices _services;
    private readonly PinnedFolders _pins;
    private readonly List<IPluginSidebarSection> _subscribedSections = [];
    private readonly HashSet<IPluginSidebarSection> _pendingSectionReloads = [];
    private double _width;
    private bool _isVisible;
    private int _generation;
    private bool _disposed;
    private SidebarItemViewModel? _trashItem;

    public SidebarViewModel(PaneServices services, PinnedFolders pins)
    {
        _services = services;
        _pins = pins;
        _width = services.Settings.Current.Sidebar.Width;
        _isVisible = services.Settings.Current.Sidebar.IsVisible;
        NavigateCommand = new RelayCommand<SidebarItemViewModel>(item => { if (item is not null) NavigateRequested?.Invoke(this, (item.Location, false)); });
        OpenInNewTabCommand = new RelayCommand<SidebarItemViewModel>(item => { if (item is not null) NavigateRequested?.Invoke(this, (item.Location, true)); });
        OpenInNewWindowCommand = new RelayCommand<SidebarItemViewModel>(item => { if (item is not null) _services.Windows.OpenNewWindow(item.Location); });
        UnpinCommand = new RelayCommand<SidebarItemViewModel>(item => { if (item?.Path is { } path) _pins.Unpin(path); });
        MoveUpCommand = new RelayCommand<SidebarItemViewModel>(item => Move(item, -1));
        MoveDownCommand = new RelayCommand<SidebarItemViewModel>(item => Move(item, +1));
        ToggleSectionCommand = new RelayCommand<SidebarSectionViewModel>(ToggleSection);

        _pins.Changed += OnSourcesChanged;
        _services.Volumes.VolumesChanged += OnVolumesChanged;
        _services.Settings.Changed += OnSettingsChanged;
        _services.TrashStatus.PropertyChanged += OnTrashStatusChanged;
        _services.Plugins.Changed += OnPluginsChanged;
        Rebuild();
    }

    public ObservableCollection<SidebarItemViewModel> TopItems { get; } = [];

    public ObservableCollection<SidebarSectionViewModel> Sections { get; } = [];

    /// <summary>Items below the sections: only the trash, and only while the quick access section is hidden.</summary>
    public ObservableCollection<SidebarItemViewModel> BottomItems { get; } = [];

    public bool HasBottomItems => BottomItems.Count > 0;

    public double Width
    {
        get => _width;
        set
        {
            var clamped = Math.Clamp(value, 160, 480);
            if (SetProperty(ref _width, clamped))
                _services.Settings.Update("sidebarWidth", s => s.Sidebar.Width = clamped);
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
                _services.Settings.Update("sidebarVisible", s => s.Sidebar.IsVisible = value);
        }
    }

    public event EventHandler<(Location Location, bool NewTab)>? NavigateRequested;

    public IRelayCommand<SidebarItemViewModel> NavigateCommand { get; }

    public IRelayCommand<SidebarItemViewModel> OpenInNewTabCommand { get; }

    public IRelayCommand<SidebarItemViewModel> OpenInNewWindowCommand { get; }

    public IRelayCommand<SidebarItemViewModel> UnpinCommand { get; }

    public IRelayCommand<SidebarItemViewModel> MoveUpCommand { get; }

    public IRelayCommand<SidebarItemViewModel> MoveDownCommand { get; }

    public IRelayCommand<SidebarSectionViewModel> ToggleSectionCommand { get; }

    /// <summary>Drag-and-drop reordering of pinned folders.</summary>
    public void MovePinned(SidebarItemViewModel item, int newIndex)
    {
        if (item.Path is { } path)
            _pins.Move(path, newIndex);
    }

    /// <summary>Highlights the entry matching the active pane's location.</summary>
    public void SyncSelection(Location location)
    {
        foreach (var item in AllItems())
            item.IsSelected = item.Location.Equals(location);
    }

    private IEnumerable<SidebarItemViewModel> AllItems() => TopItems.Concat(Sections.SelectMany(s => s.Items)).Concat(BottomItems);

    private void Rebuild()
    {
        var localizer = _services.Localizer;
        var settings = _services.Settings.Current.Sidebar;
        var collapsed = settings.CollapsedSections.ToHashSet(StringComparer.Ordinal);

        TopItems.Clear();
        TopItems.Add(new SidebarItemViewModel(localizer["Location.Home"], "Home", Location.Home));

        _trashItem = settings.ShowTrash && _services.Trash.IsSupported
            ? new SidebarItemViewModel(localizer[OperatingSystem.IsWindows() ? "Location.RecycleBin" : "Location.Trash"], "Trash", Location.Trash,
                systemIconKey: _services.TrashStatus.SystemIconKey)
            : null;

        Sections.Clear();
        if (settings.ShowQuickAccess)
        {
            // Windows has no stock "pinned" icon: Navigator's pin glyph is used.
            var pinned = new SidebarSectionViewModel("pinned", localizer["Sidebar.Pinned"], "Pin") { IsExpanded = !collapsed.Contains("pinned") };
            foreach (var path in _pins.Paths)
            {
                var known = _pins.KnownFolderFor(path);
                var name = known is { } folder && folder != KnownFolder.Home ? localizer["KnownFolder." + folder] : System.IO.Path.GetFileName(PathUtil.TrimTrailingSeparator(path));
                pinned.Items.Add(new SidebarItemViewModel(string.IsNullOrEmpty(name) ? path : name, known?.ToString() ?? "Folder", Location.Folder(path),
                    systemIconKey: PinnedFolders.SystemIconKeyFor(path, known), isPinned: true));
            }

            // The trash lives in quick access, after the pinned folders (it is not reorderable or unpinnable).
            if (_trashItem is not null)
                pinned.Items.Add(_trashItem);

            Sections.Add(pinned);
        }

        if (settings.ShowDrives)
        {
            Sections.Add(new SidebarSectionViewModel("drives", localizer["Sidebar.Drives"], "Drive", SystemIconKey.Stock(SystemIconKind.Computer)) { IsExpanded = !collapsed.Contains("drives") });
            _ = LoadVolumesAsync();
        }

        RebuildPluginSections(collapsed);

        if (settings.ShowTags && _services.Settings.Current.Tags.Count > 0)
        {
            // Windows has no stock "tags" icon: Navigator's tag glyph is used.
            var tags = new SidebarSectionViewModel("tags", localizer["Sidebar.Tags"], "Tag") { IsExpanded = !collapsed.Contains("tags") };
            foreach (var tag in _services.Settings.Current.Tags)
                tags.Items.Add(new SidebarItemViewModel(tag.Name, "Tag", Location.Tag(tag.Id), color: tag.Color));
            Sections.Add(tags);
        }

        BottomItems.Clear();
        if (_trashItem is not null && !settings.ShowQuickAccess)
            BottomItems.Add(_trashItem);
        OnPropertyChanged(nameof(HasBottomItems));
    }

    /// <summary>Local drives. Network volumes belong to the Network plugin's section.</summary>
    private async Task LoadVolumesAsync()
    {
        var volumes = await Task.Run(_services.Volumes.GetVolumes).ConfigureAwait(true);
        if (_disposed || Sections.FirstOrDefault(s => s.Id == "drives") is not { } drives)
            return;
        drives.Items.Clear();
        foreach (var volume in volumes)
        {
            if (volume.Kind == VolumeKind.Network)
                continue;
            var model = HomeViewModel.CreateVolumeItem(volume, _services.Localizer);
            drives.Items.Add(new SidebarItemViewModel(model.Name, model.IconKey, Location.Folder(volume.RootPath), model.Detail, systemIconKey: model.SystemIconKey));
        }
    }

    // ── Plugin sections ───────────────────────────────────────

    private void RebuildPluginSections(HashSet<string> collapsed)
    {
        _generation++;
        foreach (var source in _subscribedSections)
            source.Changed -= OnPluginSectionChanged;
        _subscribedSections.Clear();
        _pendingSectionReloads.Clear();

        var settings = _services.Settings.Current.Sidebar;
        foreach (var source in _services.Plugins.SidebarSections)
        {
            // The Network section keeps its own switch in Settings → Sidebar.
            if (source.Id == "network" && !settings.ShowNetwork)
                continue;

            source.Changed += OnPluginSectionChanged;
            _subscribedSections.Add(source);
            var section = CreatePluginSection(source, collapsed.Contains(source.Id));
            if (source.ShowWhenEmpty)
                InsertPluginSection(section);
            _ = LoadPluginSectionAsync(section, _generation);
        }
    }

    private SidebarSectionViewModel CreatePluginSection(IPluginSidebarSection source, bool isCollapsed)
    {
        string title;
        IReadOnlyList<PluginCommand> commands;
        try
        {
            title = source.Title;
            commands = source.Commands;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            title = source.Id;
            commands = [];
            LogPluginFailure(ex, source);
        }

        return new SidebarSectionViewModel(source.Id, title, source.IconKey, source.SystemIconKey)
        {
            IsExpanded = !isCollapsed,
            Source = source,
            Commands = [.. commands.Select(c => CreateCommand(source, c, null))],
        };
    }

    private async Task LoadPluginSectionAsync(SidebarSectionViewModel section, int generation)
    {
        var source = section.Source!;
        IReadOnlyList<PluginSidebarEntry> entries;
        try
        {
            entries = await Task.Run(() => source.GetEntriesAsync(CancellationToken.None).AsTask()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogPluginFailure(ex, source);
            entries = [];
        }

        if (_disposed || generation != _generation)
            return;

        section.Items.Clear();
        foreach (var entry in entries)
        {
            section.Items.Add(new SidebarItemViewModel(entry.Name, entry.IconKey, entry.Location, entry.Detail, systemIconKey: entry.SystemIconKey)
            {
                Commands = [.. entry.Commands.Select(c => CreateCommand(source, c, entry))],
            });
        }

        if (entries.Count == 0 && !source.ShowWhenEmpty)
            Sections.Remove(section);
        else if (!Sections.Contains(section))
            InsertPluginSection(section);
    }

    /// <summary>Plugin sections sit after Drives and before Tags, ordered by <see cref="IPluginSidebarSection.Order"/>.</summary>
    private void InsertPluginSection(SidebarSectionViewModel section)
    {
        var order = section.Source!.Order;
        var index = Sections.Count;
        for (var i = 0; i < Sections.Count; i++)
        {
            var existing = Sections[i];
            if (existing.Id == "tags" || (existing.Source is { } other && other.Order > order))
            {
                index = i;
                break;
            }
        }

        Sections.Insert(index, section);
    }

    private SidebarCommandViewModel CreateCommand(IPluginSidebarSection source, PluginCommand command, PluginSidebarEntry? entry)
        => new(command.Title, command.IconKey, command.IsDestructive, new AsyncRelayCommand(async () =>
        {
            try
            {
                await source.ExecuteAsync(command.Id, entry, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogPluginFailure(ex, source);
                await _services.Dialogs.ShowMessageAsync(_services.Localizer["Plugin.CommandFailed"], ex.Message).ConfigureAwait(true);
            }
        }));

    /// <summary>A section's entries changed: reload just that section (coalesced per section).</summary>
    private void OnPluginSectionChanged(object? sender, EventArgs e)
    {
        if (sender is not IPluginSidebarSection source)
            return;
        _services.Dispatcher.Post(() =>
        {
            if (_disposed || !_pendingSectionReloads.Add(source))
                return;
            _services.Dispatcher.Post(() =>
            {
                _pendingSectionReloads.Remove(source);
                if (_disposed || !_subscribedSections.Contains(source))
                    return;
                var collapsed = _services.Settings.Current.Sidebar.CollapsedSections.Contains(source.Id);
                var current = Sections.FirstOrDefault(s => ReferenceEquals(s.Source, source));
                var section = CreatePluginSection(source, collapsed);
                if (current is not null)
                    Sections[Sections.IndexOf(current)] = section;
                else if (source.ShowWhenEmpty)
                    InsertPluginSection(section);
                _ = LoadPluginSectionAsync(section, _generation);
            });
        });
    }

    private void OnPluginsChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        if (!_disposed)
            Rebuild();
    });

    private void LogPluginFailure(Exception ex, IPluginSidebarSection source)
    {
        _services.LoggerFactory.CreateLogger<SidebarViewModel>().LogWarning(ex, "Plugin sidebar section {Section} failed", source.Id);
    }

    private void Move(SidebarItemViewModel? item, int delta)
    {
        if (item?.Path is not { } path)
            return;
        var index = _pins.Paths.ToList().FindIndex(p => PathUtil.AreEqual(p, path));
        if (index >= 0)
            _pins.Move(path, index + delta);
    }

    private void ToggleSection(SidebarSectionViewModel? section)
    {
        if (section is null)
            return;
        section.IsExpanded = !section.IsExpanded;
        _services.Settings.Update("sidebarSections", s =>
        {
            s.Sidebar.CollapsedSections.Remove(section.Id);
            if (!section.IsExpanded)
                s.Sidebar.CollapsedSections.Add(section.Id);
        });
    }

    private void OnSourcesChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(Rebuild);

    private void OnVolumesChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() => _ = LoadVolumesAsync());

    private void OnSettingsChanged(object? sender, string section)
    {
        if (section is "*" or "sidebarOptions" or "tags" or "language")
            _services.Dispatcher.Post(Rebuild);
        if (section is "*" or "sidebarVisible")
            _services.Dispatcher.Post(SyncVisibilityFromSettings);
    }

    /// <summary>Visibility can also change from Settings (or reset/import); follow it without writing it back.</summary>
    private void SyncVisibilityFromSettings()
    {
        var visible = _services.Settings.Current.Sidebar.IsVisible;
        if (_isVisible == visible)
            return;
        _isVisible = visible;
        OnPropertyChanged(nameof(IsVisible));
    }

    private void OnTrashStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_trashItem is not null)
            _trashItem.SystemIconKey = _services.TrashStatus.SystemIconKey;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var source in _subscribedSections)
            source.Changed -= OnPluginSectionChanged;
        _subscribedSections.Clear();
        _services.Plugins.Changed -= OnPluginsChanged;
        _services.TrashStatus.PropertyChanged -= OnTrashStatusChanged;
        _pins.Changed -= OnSourcesChanged;
        _services.Volumes.VolumesChanged -= OnVolumesChanged;
        _services.Settings.Changed -= OnSettingsChanged;
    }
}
