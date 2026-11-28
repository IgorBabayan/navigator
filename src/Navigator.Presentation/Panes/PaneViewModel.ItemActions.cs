using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.Fonts;
using Navigator.Core.Imaging;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Presentation.Menus;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Panes;

/// <summary>
/// Type-specific item actions: rotating pictures, setting one as desktop or lock screen picture, installing fonts,
/// mounting disk images and sharing files with other applications.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>How long "Mount" waits for the new drive to appear when the system does not report where it went.</summary>
    private static readonly TimeSpan MountPatience = TimeSpan.FromSeconds(10);

    public IAsyncRelayCommand RotateLeftCommand { get; private set; } = null!;

    public IAsyncRelayCommand RotateRightCommand { get; private set; } = null!;

    public IAsyncRelayCommand SetAsWallpaperCommand { get; private set; } = null!;

    public IAsyncRelayCommand SetAsLockScreenCommand { get; private set; } = null!;

    public IAsyncRelayCommand InstallFontCommand { get; private set; } = null!;

    public IAsyncRelayCommand MountImageCommand { get; private set; } = null!;

    /// <summary>Shares the selection with the <see cref="ShareTarget"/> given as parameter (macOS, Linux).</summary>
    public IAsyncRelayCommand<ShareTarget> ShareWithCommand { get; private set; } = null!;

    private ItemActionServices Actions => _services.ItemActions;

    /// <summary>Local files only (no folders), outside the trash.</summary>
    private bool HasOnlyLocalFiles => !IsRemote && !IsTrash && HasSelection && Selection.SelectedItems.All(i => !i.IsDirectory);

    private void InitializeItemActionCommands()
    {
        RotateLeftCommand = new AsyncRelayCommand(() => RotateSelectionAsync(clockwise: false), CanRotateSelection);
        RotateRightCommand = new AsyncRelayCommand(() => RotateSelectionAsync(clockwise: true), CanRotateSelection);
        SetAsWallpaperCommand = new AsyncRelayCommand(() => SetDesktopPictureAsync(lockScreen: false),
            () => Actions.Backgrounds.CanSetWallpaper && IsSingleDesktopPicture());
        SetAsLockScreenCommand = new AsyncRelayCommand(() => SetDesktopPictureAsync(lockScreen: true),
            () => Actions.Backgrounds.CanSetLockScreen && IsSingleDesktopPicture());
        InstallFontCommand = new AsyncRelayCommand(InstallSelectedFontsAsync,
            () => Actions.Fonts.IsSupported && HasOnlyLocalFiles && Selection.SelectedItems.All(i => FontFileInfo.IsInstallable(i.FullPath)));
        MountImageCommand = new AsyncRelayCommand(MountSelectionAsync,
            () => HasSingleLocalFile && Actions.Drives.CanMount(Selection.SelectedItems[0].FullPath));
        ShareWithCommand = new AsyncRelayCommand<ShareTarget>(ShareSelectionWithAsync, _ => HasOnlyLocalFiles);
    }

    private void RaiseItemActionCommandState()
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        SetAsWallpaperCommand.NotifyCanExecuteChanged();
        SetAsLockScreenCommand.NotifyCanExecuteChanged();
        InstallFontCommand.NotifyCanExecuteChanged();
        MountImageCommand.NotifyCanExecuteChanged();
        ShareWithCommand.NotifyCanExecuteChanged();
    }

    private bool CanRotateSelection() => CanModifySelection && HasOnlyLocalFiles && Selection.SelectedItems.All(i => ImageRotation.CanRotate(i.FullPath));

    /// <summary>Picture formats every supported desktop accepts as wallpaper.</summary>
    private bool IsSingleDesktopPicture()
        => HasSingleLocalFile && Selection.SelectedItems[0].Entry.Extension is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png" or ".bmp" or ".gif"
            or ".webp" or ".tif" or ".tiff" or ".heic" or ".heif" or ".avif";

    private async Task RotateSelectionAsync(bool clockwise)
    {
        if (!CanRotateSelection())
            return;
        var paths = Selection.SelectedItems.Select(i => i.FullPath).ToArray();

        // JPEGs change two header bytes; PNGs are re-encoded, which can take a moment for large pictures.
        var failure = await Task.Run(() =>
        {
            Exception? first = null;
            foreach (var path in paths)
            {
                try
                {
                    ImageRotation.Rotate(path, clockwise);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    first ??= ex;
                }
            }

            return first;
        }).ConfigureAwait(true);

        if (failure is not null)
            await ReportErrorAsync(failure).ConfigureAwait(true);
        SelectWhenAvailable(paths);
    }

    private async Task SetDesktopPictureAsync(bool lockScreen)
    {
        if (!IsSingleDesktopPicture())
            return;
        var path = Selection.SelectedItems[0].FullPath;
        var backgrounds = Actions.Backgrounds;
        var done = lockScreen
            ? await backgrounds.SetLockScreenAsync(path).ConfigureAwait(true)
            : await backgrounds.SetWallpaperAsync(path).ConfigureAwait(true);

        var text = _services.Localizer;
        _services.Notifications.Show(done
            ? new InAppNotification(text[lockScreen ? "Notify.LockScreenSet" : "Notify.WallpaperSet"], Path.GetFileName(path), NotificationSeverity.Success)
            : new InAppNotification(text[lockScreen ? "Error.LockScreenFailed" : "Error.WallpaperFailed"], Path.GetFileName(path), NotificationSeverity.Error));
    }

    private async Task InstallSelectedFontsAsync()
    {
        var fonts = Selection.SelectedItems.Where(i => !i.IsDirectory && FontFileInfo.IsInstallable(i.FullPath)).Select(i => i.FullPath).ToList();
        if (fonts.Count == 0)
            return;

        int installed = 0, existing = 0;
        var failed = new List<string>();
        foreach (var font in fonts)
        {
            switch (await Actions.Fonts.InstallAsync(font).ConfigureAwait(true))
            {
                case FontInstallResult.Installed:
                    installed++;
                    break;
                case FontInstallResult.AlreadyInstalled:
                    existing++;
                    break;
                default:
                    failed.Add(Path.GetFileName(font));
                    break;
            }
        }

        var text = _services.Localizer;
        if (failed.Count > 0)
        {
            _services.Notifications.Show(new InAppNotification(text["Error.FontInstallFailed"], string.Join(", ", failed), NotificationSeverity.Error));
            return;
        }

        _services.Notifications.Show(installed > 0
            ? new InAppNotification(text["Notify.FontsInstalled"], text.Format("Notify.FontsInstalledCount", installed), NotificationSeverity.Success)
            : new InAppNotification(text["Notify.FontsAlreadyInstalled"], text.Format("Notify.FontsInstalledCount", existing), NotificationSeverity.Information));
    }

    /// <summary>Mounts the selected image and opens the new drive (reported by the system or found among the volumes).</summary>
    private async Task MountSelectionAsync()
    {
        if (Selection.SelectedItems is not [{ IsDirectory: false } image] || IsRemote)
            return;

        var volumes = _services.Volumes;
        var before = await Task.Run(() => volumes.GetVolumes().Select(v => v.RootPath).ToHashSet(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(true);
        using var stopWaiting = new CancellationTokenSource();
        var appeared = WaitForNewVolumeAsync(before, stopWaiting.Token);
        var result = await Actions.Drives.MountAsync(image.FullPath).ConfigureAwait(true);
        if (result.MountPath is not null || !result.Succeeded)
            await stopWaiting.CancelAsync().ConfigureAwait(true);
        if (!result.Succeeded)
        {
            var text = _services.Localizer;
            await _services.Dialogs.ShowMessageAsync(text["Error.MountFailedTitle"],
                text.Format("Error.MountFailed", image.Name) + (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : Environment.NewLine + result.Message.Trim())).ConfigureAwait(true);
            return;
        }

        var root = result.MountPath ?? await appeared.ConfigureAwait(true);
        if (root is not null && Directory.Exists(root))
            Navigate(Location.Folder(root));
    }

    /// <summary>The root of the first volume that was not there before, or null after <see cref="MountPatience"/>.</summary>
    private async Task<string?> WaitForNewVolumeAsync(HashSet<string> before, CancellationToken cancellationToken)
    {
        var volumes = _services.Volumes;
        var changed = new SemaphoreSlim(0);
        void OnChanged(object? sender, EventArgs e) => changed.Release();
        volumes.VolumesChanged += OnChanged;
        try
        {
            var deadline = DateTime.UtcNow + MountPatience;
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                // Either the change notification or a slow poll, whichever comes first.
                try
                {
                    await changed.WaitAsync(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                var now = volumes.GetVolumes();
                foreach (var volume in now)
                {
                    if (!before.Contains(volume.RootPath))
                        return volume.RootPath;
                }
            }

            return null;
        }
        finally
        {
            volumes.VolumesChanged -= OnChanged; // the semaphore has no wait handle: nothing to dispose
        }
    }

    /// <summary>
    /// The item menu's "Share": on Windows the shell's own share verb (Explorer's share sheet) from the menu queried for
    /// this right-click; elsewhere a sub-menu of the system's share targets.
    /// </summary>
    private MenuEntry ShareEntry(Task<ShellMenuBridge?>? bridge)
    {
        var text = _services.Localizer;
        if (!HasOnlyLocalFiles)
            return MenuEntry.Omitted;

        if (OperatingSystem.IsWindows())
        {
            if (bridge is null)
                return MenuEntry.Omitted;
            return new MenuEntry
            {
                Label = text["Command.Share"],
                Glyph = "Share",
                Command = new AsyncRelayCommand(() => ShareThroughShellAsync(bridge)),
            };
        }

        if (!Actions.Share.IsSupported)
            return MenuEntry.Omitted;
        var paths = Selection.SelectedItems.Select(i => i.FullPath).ToArray();
        return new MenuEntry
        {
            Label = text["Command.Share"],
            Glyph = "Share",
            LoadChildren = token => ShareTargetEntriesAsync(paths, token),
        };
    }

    private async Task ShareThroughShellAsync(Task<ShellMenuBridge?> bridge)
    {
        var shell = await bridge.ConfigureAwait(true);
        if (shell is not null && await shell.InvokeVerbAsync(ShellMenuBridge.ShareVerb).ConfigureAwait(true))
            return;
        await _services.Dialogs.ShowMessageAsync(_services.Localizer["Command.Share"], _services.Localizer["Error.ShareUnavailable"]).ConfigureAwait(true);
    }

    /// <summary>Asked when the sub-menu opens; on macOS the service list must be read on the UI thread, so no await precedes it.</summary>
    private async Task<IReadOnlyList<MenuEntry>> ShareTargetEntriesAsync(string[] paths, CancellationToken cancellationToken)
    {
        IReadOnlyList<ShareTarget> targets;
        try
        {
            targets = await Actions.Share.GetTargetsAsync(paths, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or EntryPointNotFoundException or DllNotFoundException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(ex, "Share targets could not be listed");
            targets = [];
        }

        var text = _services.Localizer;
        var entries = new List<MenuEntry>(Math.Max(1, targets.Count));
        foreach (var target in targets)
        {
            entries.Add(new MenuEntry
            {
                Label = target.Id == "mail" ? text["Share.Email"] : target.Title,
                Glyph = target.IconKey,
                Command = ShareWithCommand,
                Parameter = target,
                Scopes = MenuScope.Anywhere,
            });
        }

        if (entries.Count == 0)
            entries.Add(new MenuEntry { Label = text["Share.NoTargets"], IsEnabled = false, Scopes = MenuScope.Anywhere });
        return entries;
    }

    private async Task ShareSelectionWithAsync(ShareTarget? target)
    {
        if (target is null || !HasOnlyLocalFiles)
            return;
        var paths = Selection.SelectedItems.Select(i => i.FullPath).ToArray();
        if (!await Actions.Share.ShareAsync(paths, target).ConfigureAwait(true))
            await _services.Dialogs.ShowMessageAsync(_services.Localizer["Command.Share"], _services.Localizer["Error.ShareUnavailable"]).ConfigureAwait(true);
    }
}
