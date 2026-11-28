using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Formatting;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Presentation.Items;
using Navigator.Presentation.Services;
using Navigator.Presentation.Shell;

namespace Navigator.Presentation.Panes;

public sealed class QuickAccessCardViewModel(string name, string path, string iconKey, string? systemIconKey = null)
{
    public string Name { get; } = name;

    public string Path { get; } = path;

    public string IconKey { get; } = iconKey;

    /// <summary>OS-drawn folder icon (<see cref="SystemIconKey"/>); <see cref="IconKey"/> is the fallback.</summary>
    public string? SystemIconKey { get; } = systemIconKey;
}

public sealed class VolumeItemViewModel(VolumeInfo volume, string name, string detail)
{
    public VolumeInfo Volume { get; } = volume;

    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public double UsedPercent => Volume.UsedFraction * 100;

    public bool IsAlmostFull => Volume.UsedFraction > 0.9;

    /// <summary>Removable media, optical discs and mounted images offer "Eject" in the drive's context menu.</summary>
    public bool CanEject { get; init; }

    /// <summary>Mapped network drives / mounted shares offer "Disconnect".</summary>
    public bool CanDisconnect { get; init; }

    public bool HasDriveActions => CanEject || CanDisconnect;

    public string IconKey => Volume.Kind switch
    {
        VolumeKind.Removable => "DriveRemovable",
        VolumeKind.Network => "DriveNetwork",
        VolumeKind.Optical => "DriveOptical",
        _ => "Drive",
    };

    /// <summary>Stock OS drive icon by kind: no disk is touched to draw it (a sleeping or disconnected drive never stalls).</summary>
    public string SystemIconKey => Core.Platform.SystemIconKey.Stock(Volume.Kind switch
    {
        VolumeKind.Removable => SystemIconKind.DriveRemovable,
        VolumeKind.Network => SystemIconKind.DriveNetwork,
        VolumeKind.Optical => SystemIconKind.DriveOptical,
        VolumeKind.Ram => SystemIconKind.DriveRam,
        _ => SystemIconKind.DriveFixed,
    });
}

public sealed class RecentFileViewModel(string path, ItemCategory category)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path);

    public string Folder { get; } = System.IO.Path.GetDirectoryName(path) ?? string.Empty;

    public string IconKey { get; } = category.ToString();
}

/// <summary>The Home dashboard: quick access, drives, tags and recent files.</summary>
public sealed class HomeViewModel : ObservableObject
{
    private readonly PaneServices _services;
    private readonly PinnedFolders _pins;
    private readonly Action<Location, bool> _open;

    public HomeViewModel(PaneServices services, PinnedFolders pins, Action<Location, bool> open)
    {
        _services = services;
        _pins = pins;
        _open = open;
        OpenCommand = new RelayCommand<string>(path => { if (path is not null) _open(Location.Folder(path), false); });
        OpenInNewTabCommand = new RelayCommand<string>(path => { if (path is not null) _open(Location.Folder(path), true); });
        OpenRecentCommand = new AsyncRelayCommand<RecentFileViewModel>(OpenRecentAsync);
        OpenTagCommand = new RelayCommand<TagChip>(tag => { if (tag is not null) _open(Location.Tag(tag.Id), false); });
        UnpinCommand = new RelayCommand<string>(path => { if (path is not null) _pins.Unpin(path); });
        RemoveRecentCommand = new RelayCommand<RecentFileViewModel>(item => { if (item is not null) _services.Recent.Remove(item.Path); });
        ClearRecentCommand = new RelayCommand(_services.Recent.Clear);
        _drives = new DriveActions(services);
        EjectCommand = new AsyncRelayCommand<VolumeItemViewModel>(async item =>
        {
            if (item is not null && await _drives.EjectAsync(item.Volume).ConfigureAwait(true))
                Drives.Remove(item);
        });
        DisconnectCommand = new AsyncRelayCommand<VolumeItemViewModel>(async item =>
        {
            if (item is not null && await _drives.DisconnectAsync(item.Volume).ConfigureAwait(true))
                Drives.Remove(item);
        });
        ConnectNetworkDriveCommand = new AsyncRelayCommand(_drives.ConnectNetworkDriveAsync, () => _drives.CanConnectNetworkDrive);
    }

    private readonly DriveActions _drives;

    public IAsyncRelayCommand<VolumeItemViewModel> EjectCommand { get; }

    public IAsyncRelayCommand<VolumeItemViewModel> DisconnectCommand { get; }

    /// <summary>"Map network drive" (Windows) / "Connect to server"; offered in a drive's context menu.</summary>
    public IAsyncRelayCommand ConnectNetworkDriveCommand { get; }

    public bool CanConnectNetworkDrive => _drives.CanConnectNetworkDrive;

    public string ConnectNetworkDriveTitle => _drives.ConnectTitle;

    public ObservableCollection<QuickAccessCardViewModel> QuickAccess { get; } = [];

    public ObservableCollection<VolumeItemViewModel> Drives { get; } = [];

    public ObservableCollection<TagChip> Tags { get; } = [];

    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];

    public bool ShowQuickAccess => _services.Settings.Current.General.ShowHomeQuickAccess;

    public bool ShowDrives => _services.Settings.Current.General.ShowHomeDrives;

    public bool ShowTags => _services.Settings.Current.General.ShowHomeTags && Tags.Count > 0;

    public bool ShowRecentFiles => _services.Settings.Current.General.ShowHomeRecentFiles;

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public IRelayCommand<string> OpenCommand { get; }

    public IRelayCommand<string> OpenInNewTabCommand { get; }

    public IAsyncRelayCommand<RecentFileViewModel> OpenRecentCommand { get; }

    public IRelayCommand<TagChip> OpenTagCommand { get; }

    public IRelayCommand<string> UnpinCommand { get; }

    public IRelayCommand<RecentFileViewModel> RemoveRecentCommand { get; }

    public IRelayCommand ClearRecentCommand { get; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var localizer = _services.Localizer;
        QuickAccess.Clear();
        foreach (var path in _pins.Paths)
        {
            var known = _pins.KnownFolderFor(path);
            var name = known is { } folder && folder != KnownFolder.Home ? localizer["KnownFolder." + folder] : System.IO.Path.GetFileName(path);
            QuickAccess.Add(new QuickAccessCardViewModel(string.IsNullOrEmpty(name) ? path : name, path, known?.ToString() ?? "Folder", PinnedFolders.SystemIconKeyFor(path, known)));
        }

        Tags.Clear();
        foreach (var tag in _services.Settings.Current.Tags)
            Tags.Add(new TagChip(tag.Id, tag.Name, tag.Color));

        RecentFiles.Clear();
        foreach (var path in _services.Recent.Items.Take(12))
            RecentFiles.Add(new RecentFileViewModel(path, ItemCategories.FromExtension(System.IO.Path.GetExtension(path).ToLowerInvariant())));

        // Drive queries can block on disconnected network drives: never on the UI thread.
        var volumes = await Task.Run(_services.Volumes.GetVolumes, cancellationToken).ConfigureAwait(true);
        Drives.Clear();
        foreach (var volume in volumes)
            Drives.Add(CreateVolumeItem(volume, localizer, _drives));

        OnPropertyChanged(string.Empty);
    }

    internal static VolumeItemViewModel CreateVolumeItem(VolumeInfo volume, Localization.Localizer localizer, DriveActions? drives = null)
    {
        var name = OperatingSystem.IsWindows()
            ? WindowsVolumeName(volume, localizer)
            : string.IsNullOrEmpty(volume.Label) ? (volume.RootPath is "/" ? localizer["Drive.FileSystem"] : volume.RootPath) : volume.Label;
        var detail = volume.IsReady && volume.TotalBytes > 0
            ? localizer.Format("Drive.FreeOf", ByteSize.Format(volume.FreeBytes, localizer.Culture), ByteSize.Format(volume.TotalBytes, localizer.Culture))
            : localizer["Drive.NotReady"];
        return new VolumeItemViewModel(volume, name, detail)
        {
            CanEject = drives?.CanEject(volume) == true,
            CanDisconnect = drives?.CanDisconnect(volume) == true,
        };
    }

    /// <summary>File Explorer style: "Games (D:)", or the drive type for unlabeled drives — "Local Disk (C:)".</summary>
    internal static string WindowsVolumeName(VolumeInfo volume, Localization.Localizer localizer)
    {
        var letter = volume.RootPath.TrimEnd('\\', '/');
        var label = string.IsNullOrWhiteSpace(volume.Label)
            ? localizer[volume.Kind switch
            {
                VolumeKind.Removable => "Drive.Removable",
                VolumeKind.Network => "Drive.Network",
                VolumeKind.Optical => "Drive.Optical",
                VolumeKind.Ram => "Drive.Ram",
                _ => "Drive.LocalDisk",
            }]
            : volume.Label;
        return letter.Length == 0 ? label : $"{label} ({letter})";
    }

    private async Task OpenRecentAsync(RecentFileViewModel? item)
    {
        if (item is null)
            return;
        if (!File.Exists(item.Path))
        {
            _services.Recent.Remove(item.Path);
            RecentFiles.Remove(item);
            return;
        }

        await _services.Launcher.OpenAsync(item.Path).ConfigureAwait(true);
    }
}
