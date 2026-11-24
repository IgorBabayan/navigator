using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Plugins;
using Navigator.Presentation.Items;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Panes;

/// <summary>
/// Folders served by plugins ("sftp://…", "ftp://…"). Listing, navigation, open (download to a cache, then the default
/// app), new folder, rename, delete, download to a local folder and upload. Local-only work (monitoring, thumbnails,
/// previews, folder sizes, clipboard, tags, pins, shell actions) is switched off for these locations.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>Downloads, uploads and changes started from this pane; cancelled when the pane closes.</summary>
    private readonly CancellationTokenSource _remoteWork = new();

    public bool IsRemote => _location.Kind == LocationKind.Remote;

    public IAsyncRelayCommand DownloadCommand { get; private set; } = null!;

    public IAsyncRelayCommand UploadCommand { get; private set; } = null!;

    private void InitializeRemoteCommands()
    {
        DownloadCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => IsRemote && HasSelection);
        UploadCommand = new AsyncRelayCommand(UploadAsync, () => IsRemote);
    }

    private void RaiseRemoteCommandState()
    {
        DownloadCommand.NotifyCanExecuteChanged();
        UploadCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadRemoteAsync(string folder, CancellationToken token)
    {
        _displayContext = CreateDisplayContext();
        var files = _services.Settings.Current.FilesAndFolders;
        var entries = await Task.Run(() => _services.Remote.ListAsync(folder, files.ShowHiddenItems || files.ShowDotFiles, token), token).ConfigureAwait(true);
        foreach (var entry in entries)
            _entries[entry.FullPath] = entry;
        await PublishAsync(token).ConfigureAwait(true);
    }

    private string RemoteErrorText(RemoteFileSystemException ex)
    {
        var localizer = _services.Localizer;
        return ex.Kind switch
        {
            RemoteErrorKind.Cancelled => localizer["Remote.SignInCancelled"],
            RemoteErrorKind.AuthenticationFailed => localizer["Remote.AuthenticationFailed"] + Environment.NewLine + ex.Message,
            RemoteErrorKind.HostKeyRejected => localizer["Remote.HostKeyRejected"] + Environment.NewLine + ex.Message,
            RemoteErrorKind.ConnectionFailed => localizer["Remote.ConnectionFailed"] + Environment.NewLine + ex.Message,
            RemoteErrorKind.NotFound => localizer["Error.FolderNotFound"],
            RemoteErrorKind.AccessDenied => localizer["Error.AccessDenied"],
            _ => ex.Message,
        };
    }

    private async Task OpenRemoteFileAsync(FileItemViewModel item)
    {
        try
        {
            var local = await _services.Remote.DownloadToCacheAsync(item.FullPath, _remoteWork.Token).ConfigureAwait(true);
            if (!await _services.Launcher.OpenAsync(local).ConfigureAwait(true))
                await _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.OpenFailedTitle"], _services.Localizer.Format("Error.OpenFailed", item.Name)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportRemoteErrorAsync(ex).ConfigureAwait(true);
        }
    }

    private async Task CreateRemoteFolderAsync()
    {
        if (_location.Path is not { } folder)
            return;
        var localizer = _services.Localizer;
        var existing = _ordered.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var suggested = PathUtil.GetUniqueName(folder, localizer["Item.NewFolderName"], isDirectory: true, p => existing.Contains(Path.GetFileName(p)));
        var name = await _services.Dialogs.PromptTextAsync(new TextInputRequest(localizer["Command.NewFolder"], localizer["Dialog.NameLabel"], suggested, localizer["Dialog.Create"],
            ValidateRemoteName(existing))).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
            return;

        await RunRemoteAsync(token => _services.Remote.CreateFolderAsync(folder, name.Trim(), token)).ConfigureAwait(true);
    }

    private async Task RenameRemoteAsync(FileItemViewModel item)
    {
        var localizer = _services.Localizer;
        var existing = _ordered.Select(i => i.Name).Where(n => n != item.Name).ToHashSet(StringComparer.Ordinal);
        var stemLength = item.IsDirectory ? -1 : PathUtil.SplitName(item.Name, false).Stem.Length;
        var name = await _services.Dialogs.PromptTextAsync(new TextInputRequest(localizer["Command.Rename"], localizer["Dialog.NameLabel"], item.Name, localizer["Command.Rename"],
            ValidateRemoteName(existing), stemLength)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == item.Name)
            return;

        await RunRemoteAsync(token => _services.Remote.RenameAsync(item.FullPath, name.Trim(), token)).ConfigureAwait(true);
    }

    /// <summary>Servers have no trash: deleting is permanent and always confirmed.</summary>
    private async Task DeleteRemoteAsync(IReadOnlyList<FileItemViewModel> selected)
    {
        var localizer = _services.Localizer;
        var message = selected.Count == 1
            ? localizer.Format("Dialog.DeleteOnePermanentlyMessage", selected[0].Name)
            : localizer.Format("Dialog.DeleteManyPermanentlyMessage", selected.Count);
        var confirm = await _services.Dialogs.ConfirmAsync(new ConfirmRequest(localizer["Dialog.DeletePermanentlyTitle"], message, localizer["Command.Delete"], IsDestructive: true)).ConfigureAwait(true);
        if (confirm.Result != DialogResult.Primary)
            return;

        var items = selected.Select(i => (i.FullPath, i.IsDirectory)).ToList();
        await RunRemoteAsync(async token =>
        {
            foreach (var (path, isDirectory) in items)
                await _services.Remote.DeleteAsync(path, isDirectory, token).ConfigureAwait(false);
        }).ConfigureAwait(true);
    }

    private async Task DownloadSelectedAsync()
    {
        var localizer = _services.Localizer;
        var items = Selection.SelectedItems.Select(i => i.Entry).ToList();
        if (items.Count == 0)
            return;
        var target = await _services.Dialogs.PickFolderAsync(localizer["Remote.DownloadTo"]).ConfigureAwait(true);
        if (target is null)
            return;

        _services.Notifications.Show(new InAppNotification(localizer["Remote.Downloading"], localizer.Format("Remote.ItemsCount", items.Count), NotificationSeverity.Information));
        try
        {
            var count = await Task.Run(() => _services.Remote.DownloadAsync(items, target, _remoteWork.Token)).ConfigureAwait(true);
            _services.Notifications.Show(new InAppNotification(localizer["Remote.DownloadDone"], localizer.Format("Remote.FilesCount", count), NotificationSeverity.Success,
                localizer["Remote.ShowFolder"], () => _services.Windows.OpenNewWindow(Location.Folder(target))));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportRemoteErrorAsync(ex).ConfigureAwait(true);
        }
    }

    private async Task UploadAsync()
    {
        if (_location.Path is not { } folder)
            return;
        var localizer = _services.Localizer;
        var file = await _services.Dialogs.PickFileAsync(localizer["Remote.UploadTitle"]).ConfigureAwait(true);
        if (file is null)
            return;
        await UploadPathsAsync([file], folder).ConfigureAwait(true);
    }

    /// <summary>Uploads local files and folders into the shown remote folder (also used for drops onto a remote pane).</summary>
    public async Task UploadPathsAsync(IReadOnlyList<string> paths, string? remoteFolder = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if ((remoteFolder ?? (IsRemote ? _location.Path : null)) is not { } folder || paths.Count == 0)
            return;
        var localizer = _services.Localizer;
        _services.Notifications.Show(new InAppNotification(localizer["Remote.Uploading"], localizer.Format("Remote.ItemsCount", paths.Count), NotificationSeverity.Information));
        await RunRemoteAsync(async token =>
        {
            var count = await Task.Run(() => _services.Remote.UploadAsync(paths, folder, token), token).ConfigureAwait(false);
            _services.Dispatcher.Post(() => _services.Notifications.Show(new InAppNotification(localizer["Remote.UploadDone"], localizer.Format("Remote.FilesCount", count), NotificationSeverity.Success)));
        }).ConfigureAwait(true);
    }

    /// <summary>Runs a changing remote action, reports failures, then lists the folder again (servers send no change events).</summary>
    private async Task RunRemoteAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(_remoteWork.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportRemoteErrorAsync(ex).ConfigureAwait(true);
        }

        if (!_disposed && IsRemote)
            Refresh();
    }

    private Task ReportRemoteErrorAsync(Exception ex)
    {
        if (ex is RemoteFileSystemException { Kind: RemoteErrorKind.Cancelled })
            return Task.CompletedTask;
        _logger.LogWarning(ex, "Remote operation failed");
        var message = ex is RemoteFileSystemException remote ? RemoteErrorText(remote)
            : ex is NoRemoteFileSystemException missing ? _services.Localizer.Format("Remote.NoPlugin", missing.Scheme)
            : ex.Message;
        return _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.OperationTitle"], message);
    }

    private Func<string, string?> ValidateRemoteName(HashSet<string> existing)
        => text =>
        {
            var name = text.Trim();
            var result = PathUtil.ValidateName(name, windowsRules: false);
            if (result != NameValidationResult.Valid)
                return _services.Localizer["Validation." + result];
            return existing.Contains(name) ? _services.Localizer["Validation.AlreadyExists"] : null;
        };
}
