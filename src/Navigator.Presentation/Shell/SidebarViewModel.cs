using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Shell;

public sealed class SidebarItemViewModel(string name, string iconKey, Location location, string? detail = null, string? color = null, string? systemIconKey = null) : ObservableObject
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

    public string? Path => Location.Path;

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
}

/// <summary>Navigation sidebar: Home, pinned folders, drives, network, tags, trash. Shared look, per-window instance.</summary>
public sealed class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly PaneServices _services;
    private readonly PinnedFolders _pins;
    private double _width;
    private bool _isVisible;
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
        Rebuild();
    }

    public ObservableCollection<SidebarItemViewModel> TopItems { get; } = [];

    public ObservableCollection<SidebarSectionViewModel> Sections { get; } = [];

    public ObservableCollection<SidebarItemViewModel> BottomItems { get; } = [];

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
                    systemIconKey: PinnedFolders.SystemIconKeyFor(path, known)));
            }

            Sections.Add(pinned);
        }

        if (settings.ShowDrives || settings.ShowNetwork)
        {
            Sections.Add(new SidebarSectionViewModel("drives", localizer["Sidebar.Drives"], "Drive", SystemIconKey.Stock(SystemIconKind.Computer)) { IsExpanded = !collapsed.Contains("drives") });
            if (settings.ShowNetwork)
                Sections.Add(new SidebarSectionViewModel("network", localizer["Sidebar.Network"], "DriveNetwork", SystemIconKey.Stock(SystemIconKind.Network)) { IsExpanded = !collapsed.Contains("network") });
            _ = LoadVolumesAsync();
        }

        if (settings.ShowTags && _services.Settings.Current.Tags.Count > 0)
        {
            // Windows has no stock "tags" icon: Navigator's tag glyph is used.
            var tags = new SidebarSectionViewModel("tags", localizer["Sidebar.Tags"], "Tag") { IsExpanded = !collapsed.Contains("tags") };
            foreach (var tag in _services.Settings.Current.Tags)
                tags.Items.Add(new SidebarItemViewModel(tag.Name, "Tag", Location.Tag(tag.Id), color: tag.Color));
            Sections.Add(tags);
        }

        BottomItems.Clear();
        _trashItem = null;
        if (settings.ShowTrash && _services.Trash.IsSupported)
        {
            _trashItem = new SidebarItemViewModel(localizer[OperatingSystem.IsWindows() ? "Location.RecycleBin" : "Location.Trash"], "Trash", Location.Trash,
                systemIconKey: _services.TrashStatus.SystemIconKey);
            BottomItems.Add(_trashItem);
        }
    }

    private async Task LoadVolumesAsync()
    {
        var volumes = await Task.Run(_services.Volumes.GetVolumes).ConfigureAwait(true);
        var drives = Sections.FirstOrDefault(s => s.Id == "drives");
        var network = Sections.FirstOrDefault(s => s.Id == "network");
        drives?.Items.Clear();
        network?.Items.Clear();
        foreach (var volume in volumes)
        {
            var model = HomeViewModel.CreateVolumeItem(volume, _services.Localizer);
            var item = new SidebarItemViewModel(model.Name, model.IconKey, Location.Folder(volume.RootPath), model.Detail, systemIconKey: model.SystemIconKey);
            if (volume.Kind == VolumeKind.Network)
                network?.Items.Add(item);
            else if (_services.Settings.Current.Sidebar.ShowDrives)
                drives?.Items.Add(item);
        }

        if (network is not null && network.Items.Count == 0)
            Sections.Remove(network);
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
    }

    private void OnTrashStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_trashItem is not null)
            _trashItem.SystemIconKey = _services.TrashStatus.SystemIconKey;
    }

    public void Dispose()
    {
        _services.TrashStatus.PropertyChanged -= OnTrashStatusChanged;
        _pins.Changed -= OnSourcesChanged;
        _services.Volumes.VolumesChanged -= OnVolumesChanged;
        _services.Settings.Changed -= OnSettingsChanged;
    }
}
