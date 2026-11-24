using System.IO.Compression;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.Archives;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Operations;
using Navigator.Presentation.Items;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Panes;

public sealed partial class PaneViewModel
{
    public IAsyncRelayCommand<FileItemViewModel> OpenItemCommand { get; private set; } = null!;

    public IAsyncRelayCommand OpenSelectedCommand { get; private set; } = null!;

    public IRelayCommand OpenSelectedInNewTabCommand { get; private set; } = null!;

    public IRelayCommand OpenSelectedInNewWindowCommand { get; private set; } = null!;

    public IAsyncRelayCommand NewFolderCommand { get; private set; } = null!;

    public IAsyncRelayCommand NewFileCommand { get; private set; } = null!;

    public IAsyncRelayCommand RenameCommand { get; private set; } = null!;

    public IAsyncRelayCommand DeleteCommand { get; private set; } = null!;

    public IAsyncRelayCommand DeletePermanentlyCommand { get; private set; } = null!;

    public IAsyncRelayCommand CopyCommand { get; private set; } = null!;

    public IAsyncRelayCommand CutCommand { get; private set; } = null!;

    public IAsyncRelayCommand PasteCommand { get; private set; } = null!;

    public IRelayCommand DuplicateCommand { get; private set; } = null!;

    public IAsyncRelayCommand CopyPathCommand { get; private set; } = null!;

    public IRelayCommand SelectAllCommand { get; private set; } = null!;

    public IRelayCommand InvertSelectionCommand { get; private set; } = null!;

    public IRelayCommand ClearSelectionCommand { get; private set; } = null!;

    public IRelayCommand PropertiesCommand { get; private set; } = null!;

    public IAsyncRelayCommand OpenTerminalCommand { get; private set; } = null!;

    public IAsyncRelayCommand OpenWithCommand { get; private set; } = null!;

    public IAsyncRelayCommand RevealInSystemCommand { get; private set; } = null!;

    public IRelayCommand PinSelectedCommand { get; private set; } = null!;

    public IAsyncRelayCommand CompressCommand { get; private set; } = null!;

    public IAsyncRelayCommand ExtractCommand { get; private set; } = null!;

    public IAsyncRelayCommand RestoreCommand { get; private set; } = null!;

    public IAsyncRelayCommand EmptyTrashCommand { get; private set; } = null!;

    public IRelayCommand<string> ToggleTagCommand { get; private set; } = null!;

    public IAsyncRelayCommand OpenInCodeEditorCommand { get; private set; } = null!;

    private bool HasSelection => Selection.Count > 0;

    private bool CanModifySelection => HasSelection && (IsFolder || IsSearch || _location.Kind == LocationKind.Tag);

    private void InitializeItemCommands()
    {
        OpenItemCommand = new AsyncRelayCommand<FileItemViewModel>(OpenItemAsync);
        OpenSelectedCommand = new AsyncRelayCommand(OpenSelectedAsync, () => HasSelection);
        OpenSelectedInNewTabCommand = new RelayCommand(OpenSelectedInNewTab, () => Selection.SelectedItems.Any(i => i.IsDirectory));
        OpenSelectedInNewWindowCommand = new RelayCommand(() =>
        {
            foreach (var folder in Selection.SelectedItems.Where(i => i.IsDirectory))
                _services.Windows.OpenNewWindow(Location.FromPath(folder.FullPath));
        }, () => Selection.SelectedItems.Any(i => i.IsDirectory));
        NewFolderCommand = new AsyncRelayCommand(() => CreateItemAsync(isDirectory: true), () => CanCreateItems || IsRemote);
        NewFileCommand = new AsyncRelayCommand(() => CreateItemAsync(isDirectory: false), () => CanCreateItems);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => CanModifySelection || (IsRemote && Selection.Count == 1));
        DeleteCommand = new AsyncRelayCommand(() => DeleteAsync(permanently: false), () => HasSelection && !IsHome);
        DeletePermanentlyCommand = new AsyncRelayCommand(() => DeleteAsync(permanently: true), () => HasSelection && !IsHome);
        CopyCommand = new AsyncRelayCommand(() => PutOnClipboardAsync(cut: false), () => HasSelection && !IsTrash && !IsRemote);
        CutCommand = new AsyncRelayCommand(() => PutOnClipboardAsync(cut: true), () => CanModifySelection);
        PasteCommand = new AsyncRelayCommand(PasteAsync, () => CanCreateItems);
        DuplicateCommand = new RelayCommand(Duplicate, () => CanModifySelection);
        CopyPathCommand = new AsyncRelayCommand(CopyPathAsync);
        SelectAllCommand = new RelayCommand(Selection.SelectAll);
        InvertSelectionCommand = new RelayCommand(Selection.Invert);
        ClearSelectionCommand = new RelayCommand(Selection.Clear);
        PropertiesCommand = new RelayCommand(ShowProperties);
        OpenTerminalCommand = new AsyncRelayCommand(OpenTerminalAsync, () => !IsRemote && (CurrentDirectory is not null || Selection.SelectedItems.Any(i => i.IsDirectory)));
        OpenWithCommand = new AsyncRelayCommand(OpenWithAsync, () => !IsRemote && Selection.SelectedItems.Count == 1 && !Selection.SelectedItems[0].IsDirectory);
        RevealInSystemCommand = new AsyncRelayCommand(RevealInSystemAsync);
        PinSelectedCommand = new RelayCommand(PinSelected);
        CompressCommand = new AsyncRelayCommand(CompressAsync, () => CanModifySelection);
        ExtractCommand = new AsyncRelayCommand(ExtractAsync, () => !IsRemote && Selection.SelectedItems.Any(i => ArchiveService.IsSupportedArchive(i.FullPath)));
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => IsTrash && HasSelection);
        EmptyTrashCommand = new AsyncRelayCommand(EmptyTrashAsync, () => IsTrash && _ordered.Count > 0);
        ToggleTagCommand = new RelayCommand<string>(ToggleTag);
        OpenInCodeEditorCommand = new AsyncRelayCommand(OpenInCodeEditorAsync, () => CurrentDirectory is not null);
        InitializeRemoteCommands();
    }

    private void RaiseItemCommandState()
    {
        OpenSelectedCommand.NotifyCanExecuteChanged();
        OpenSelectedInNewTabCommand.NotifyCanExecuteChanged();
        OpenSelectedInNewWindowCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewFileCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DeletePermanentlyCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        OpenTerminalCommand.NotifyCanExecuteChanged();
        OpenWithCommand.NotifyCanExecuteChanged();
        CompressCommand.NotifyCanExecuteChanged();
        ExtractCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        EmptyTrashCommand.NotifyCanExecuteChanged();
        OpenInCodeEditorCommand.NotifyCanExecuteChanged();
        RaiseRemoteCommandState();
    }

    /// <summary>Double-click / Enter on an item: folders navigate, archives and files open with the default app.</summary>
    public async Task OpenItemAsync(FileItemViewModel? item)
    {
        if (item is null || IsTrash)
            return;

        if (item.IsDirectory)
        {
            Navigate(Location.FromPath(item.FullPath));
            return;
        }

        if (item.Entry.IsRemote)
        {
            await OpenRemoteFileAsync(item).ConfigureAwait(true);
            return;
        }

        if (!await _services.Launcher.OpenAsync(item.FullPath).ConfigureAwait(true))
        {
            await _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.OpenFailedTitle"], _services.Localizer.Format("Error.OpenFailed", item.Name)).ConfigureAwait(true);
            return;
        }

        _services.Recent.Add(item.FullPath);
    }

    private async Task OpenSelectedAsync()
    {
        var selected = Selection.SelectedItems;
        var folders = selected.Where(i => i.IsDirectory).ToList();
        if (folders.Count == 1 && selected.Count == 1)
        {
            await OpenItemAsync(folders[0]).ConfigureAwait(true);
            return;
        }

        foreach (var folder in folders)
            OpenInNewTabRequested?.Invoke(this, Location.FromPath(folder.FullPath));
        foreach (var file in selected.Where(i => !i.IsDirectory).Take(16))
            await OpenItemAsync(file).ConfigureAwait(true);
    }

    private void OpenSelectedInNewTab()
    {
        foreach (var folder in Selection.SelectedItems.Where(i => i.IsDirectory))
            OpenInNewTabRequested?.Invoke(this, Location.FromPath(folder.FullPath));
    }

    private async Task CreateItemAsync(bool isDirectory)
    {
        if (IsRemote)
        {
            if (isDirectory)
                await CreateRemoteFolderAsync().ConfigureAwait(true);
            return;
        }

        if (CurrentDirectory is not { } directory)
            return;

        var localizer = _services.Localizer;
        var suggested = PathUtil.GetUniqueName(directory, localizer[isDirectory ? "Item.NewFolderName" : "Item.NewFileName"], isDirectory, p => File.Exists(p) || Directory.Exists(p));
        var name = await _services.Dialogs.PromptTextAsync(new TextInputRequest(
            localizer[isDirectory ? "Command.NewFolder" : "Command.NewFile"], localizer["Dialog.NameLabel"], suggested, localizer["Dialog.Create"],
            ValidateNewName(directory), isDirectory ? -1 : PathUtil.SplitName(suggested, false).Stem.Length)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            var created = isDirectory ? _services.Operations.CreateFolder(directory, name.Trim()) : _services.Operations.CreateFile(directory, name.Trim());
            _reselectAfterLoad = [created];
            SelectWhenAvailable(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportErrorAsync(ex).ConfigureAwait(true);
        }
    }

    private Func<string, string?> ValidateNewName(string directory)
        => text =>
        {
            var name = text.Trim();
            var result = PathUtil.ValidateName(name);
            if (result != NameValidationResult.Valid)
                return _services.Localizer["Validation." + result];
            return File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name))
                ? _services.Localizer["Validation.AlreadyExists"]
                : null;
        };

    private async Task RenameAsync()
    {
        var selected = Selection.SelectedItems;
        if (selected.Count == 0)
            return;

        if (IsRemote)
        {
            await RenameRemoteAsync(selected[0]).ConfigureAwait(true);
            return;
        }

        if (selected.Count > 1)
        {
            await BulkRenameAsync(selected).ConfigureAwait(true);
            return;
        }

        var item = selected[0];
        var directory = Path.GetDirectoryName(item.FullPath)!;
        var stemLength = item.IsDirectory ? -1 : PathUtil.SplitName(item.Name, false).Stem.Length;
        var newName = await _services.Dialogs.PromptTextAsync(new TextInputRequest(
            _services.Localizer["Command.Rename"], _services.Localizer["Dialog.NameLabel"], item.Name, _services.Localizer["Command.Rename"],
            text =>
            {
                var trimmed = text.Trim();
                var result = PathUtil.ValidateName(trimmed);
                if (result != NameValidationResult.Valid)
                    return _services.Localizer["Validation." + result];
                if (string.Equals(trimmed, item.Name, StringComparison.OrdinalIgnoreCase))
                    return null;
                return File.Exists(Path.Combine(directory, trimmed)) || Directory.Exists(Path.Combine(directory, trimmed))
                    ? _services.Localizer["Validation.AlreadyExists"]
                    : null;
            }, stemLength)).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name)
            return;

        try
        {
            var renamed = _services.Operations.Rename(item.FullPath, newName.Trim());
            SelectWhenAvailable(renamed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportErrorAsync(ex).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Bulk rename: a pattern with "#" for a running number ("Holiday #" → "Holiday 1.jpg", "Holiday 2.jpg").
    /// Extensions are kept. Every target name is validated before anything is renamed.
    /// </summary>
    private async Task BulkRenameAsync(IReadOnlyList<FileItemViewModel> items)
    {
        var pattern = await _services.Dialogs.PromptTextAsync(new TextInputRequest(
            _services.Localizer["Command.BulkRename"], _services.Localizer["Dialog.BulkRenameLabel"], _services.Localizer["Dialog.BulkRenameDefault"],
            _services.Localizer["Command.Rename"],
            text => text.Contains('#', StringComparison.Ordinal) ? null : _services.Localizer["Validation.BulkRenamePattern"])).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var plan = BulkRenamePlanner.Plan(items.Select(i => (i.FullPath, i.IsDirectory)).ToList(), pattern.Trim());
        if (plan.Error is { } error)
        {
            await _services.Dialogs.ShowMessageAsync(_services.Localizer["Command.BulkRename"], _services.Localizer["Validation." + error]).ConfigureAwait(true);
            return;
        }

        foreach (var (path, newName) in plan.Renames)
        {
            try
            {
                _services.Operations.Rename(path, newName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ReportErrorAsync(ex).ConfigureAwait(true);
                return;
            }
        }
    }

    private async Task DeleteAsync(bool permanently)
    {
        var selected = Selection.SelectedItems;
        if (selected.Count == 0)
            return;

        if (IsRemote)
        {
            await DeleteRemoteAsync(selected).ConfigureAwait(true);
            return;
        }

        var localizer = _services.Localizer;
        if (IsTrash)
        {
            var confirm = await _services.Dialogs.ConfirmAsync(new ConfirmRequest(localizer["Dialog.DeletePermanentlyTitle"],
                localizer.Format("Dialog.DeletePermanentlyMessage", selected.Count), localizer["Command.Delete"], IsDestructive: true)).ConfigureAwait(true);
            if (confirm.Result != DialogResult.Primary)
                return;
            foreach (var item in selected.Where(i => i.TrashItem is not null))
            {
                try
                {
                    await _services.Trash.DeletePermanentlyAsync(item.TrashItem!).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await ReportErrorAsync(ex).ConfigureAwait(true);
                }
            }

            Refresh();
            return;
        }

        var paths = selected.Select(i => i.FullPath).ToList();
        permanently |= !_services.Trash.IsSupported;
        if (permanently || _services.Settings.Current.FilesAndFolders.ConfirmDelete)
        {
            var message = selected.Count == 1
                ? localizer.Format(permanently ? "Dialog.DeleteOnePermanentlyMessage" : "Dialog.DeleteOneMessage", selected[0].Name)
                : localizer.Format(permanently ? "Dialog.DeleteManyPermanentlyMessage" : "Dialog.DeleteManyMessage", selected.Count);
            var confirm = await _services.Dialogs.ConfirmAsync(new ConfirmRequest(
                localizer[permanently ? "Dialog.DeletePermanentlyTitle" : "Dialog.DeleteTitle"], message, localizer["Command.Delete"],
                IsDestructive: permanently)).ConfigureAwait(true);
            if (confirm.Result != DialogResult.Primary)
                return;
        }

        _services.Operations.Delete(paths, permanently, _services.CreateInteraction());
    }

    private async Task PutOnClipboardAsync(bool cut)
    {
        var selected = Selection.SelectedItems;
        if (selected.Count == 0)
            return;
        await _services.Clipboard.SetFilesAsync([.. selected.Select(i => i.FullPath)], cut).ConfigureAwait(true);
        foreach (var item in _ordered)
            item.IsCut = false;
        if (cut)
        {
            foreach (var item in selected)
                item.IsCut = true;
        }
    }

    private async Task PasteAsync()
    {
        if (CurrentDirectory is not { } destination)
            return;

        var clipboard = await _services.Clipboard.GetFilesAsync().ConfigureAwait(true);
        if (clipboard is not { } content || content.Paths.Count == 0)
            return;

        await TransferIntoAsync(content.Paths, destination, move: content.IsCut).ConfigureAwait(true);
        if (content.IsCut)
            await _services.Clipboard.ClearCutStateAsync().ConfigureAwait(true);
    }

    /// <summary>Copy/move into a folder (paste and drag-and-drop). Selects the new items when they land in this pane.</summary>
    public Task TransferIntoAsync(IReadOnlyList<string> sources, string destination, bool move)
    {
        ArgumentNullException.ThrowIfNull(sources);
        // The local engine must never see a plugin address: on Unix "sftp://host/x" is a valid *relative* local path.
        // Local items dropped on a server folder are uploaded; server items are only copied out by Download.
        if (RemoteUri.IsRemote(destination))
            return sources.Any(RemoteUri.IsRemote) ? Task.CompletedTask : UploadPathsAsync(sources, destination);
        if (sources.Any(RemoteUri.IsRemote))
            return Task.CompletedTask;

        var operation = move
            ? _services.Operations.Move(sources, destination, _services.CreateInteraction())
            : _services.Operations.Copy(sources, destination, _services.CreateInteraction());

        _ = operation.Completion.ContinueWith(_ =>
            _services.Dispatcher.Post(() =>
            {
                if (PathUtil.AreEqual(CurrentDirectory, destination))
                    SelectWhenAvailable([.. operation.TransferredItems.Select(t => t.DestinationPath)]);
                ReportFinished(operation);
            }), TaskScheduler.Default);
        return Task.CompletedTask;
    }

    private void Duplicate()
    {
        var sources = Selection.SelectedItems.Select(i => i.FullPath).ToList();
        if (sources.Count == 0)
            return;
        var operation = _services.Operations.Duplicate(sources, _services.CreateInteraction());
        _ = operation.Completion.ContinueWith(_ => _services.Dispatcher.Post(() =>
        {
            SelectWhenAvailable([.. operation.TransferredItems.Select(t => t.DestinationPath)]);
            ReportFinished(operation);
        }), TaskScheduler.Default);
    }

    private async Task CopyPathAsync()
    {
        var selected = Selection.SelectedItems;
        var text = selected.Count > 0
            ? string.Join(Environment.NewLine, selected.Select(i => i.FullPath))
            : _location.Path ?? string.Empty;
        if (text.Length > 0)
            await _services.Clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    private void ShowProperties()
    {
        if (IsRemote)
            return;
        var paths = Selection.SelectedItems.Select(i => i.FullPath).ToList();
        if (paths.Count == 0 && CurrentDirectory is { } current)
            paths.Add(current);
        if (paths.Count > 0)
            _services.Dialogs.ShowProperties(paths);
    }

    private async Task OpenTerminalAsync()
    {
        if (IsRemote)
            return;
        var target = Selection.SelectedItems.FirstOrDefault(i => i.IsDirectory)?.FullPath ?? CurrentDirectory;
        if (target is not null && !await _services.Launcher.OpenTerminalAsync(target).ConfigureAwait(true))
            await _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.TerminalTitle"], _services.Localizer["Error.TerminalNotFound"]).ConfigureAwait(true);
    }

    private async Task OpenWithAsync()
    {
        if (Selection.SelectedItems is not [var item])
            return;
        if (await _services.Launcher.ShowOpenWithDialogAsync(item.FullPath).ConfigureAwait(true))
            return;
        OpenWithRequested?.Invoke(this, item);
    }

    /// <summary>Raised when the OS has no chooser of its own; the view shows Navigator's handler list.</summary>
    public event EventHandler<FileItemViewModel>? OpenWithRequested;

    private async Task RevealInSystemAsync()
    {
        if (IsRemote)
            return;
        var selected = Selection.SelectedItems;
        var target = selected.Count > 0 ? selected[0].FullPath : CurrentDirectory;
        if (target is not null)
            await _services.Launcher.RevealInSystemFileManagerAsync(target).ConfigureAwait(true);
    }

    private async Task OpenInCodeEditorAsync()
    {
        var command = _services.Settings.Current.General.CodeEditorCommand;
        var target = Selection.SelectedItems.FirstOrDefault(i => i.IsDirectory)?.FullPath ?? CurrentDirectory;
        if (target is null)
            return;
        var editor = string.IsNullOrWhiteSpace(command) ? "code" : command.Trim();
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(editor) { ArgumentList = { target }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.EditorTitle"], _services.Localizer.Format("Error.EditorNotFound", editor)).ConfigureAwait(true);
        }
    }

    private void PinSelected()
    {
        if (IsRemote)
            return;
        var folders = Selection.SelectedItems.Where(i => i.IsDirectory).Select(i => i.FullPath).ToList();
        if (folders.Count == 0 && CurrentDirectory is { } current)
            folders.Add(current);
        foreach (var folder in folders)
        {
            if (_pins.IsPinned(folder))
                _pins.Unpin(folder);
            else
                _pins.Pin(folder);
        }
    }

    private async Task CompressAsync()
    {
        var selected = Selection.SelectedItems;
        if (selected.Count == 0)
            return;
        var directory = Path.GetDirectoryName(selected[0].FullPath)!;
        var baseName = selected.Count == 1 ? PathUtil.SplitName(selected[0].Name, selected[0].IsDirectory).Stem : Path.GetFileName(directory);
        var name = PathUtil.GetUniqueName(directory, (string.IsNullOrEmpty(baseName) ? "Archive" : baseName) + ".zip", false, p => File.Exists(p) || Directory.Exists(p));
        var sources = selected.Select(i => i.FullPath).ToList();
        var zipPath = Path.Combine(directory, name);
        var operation = _services.Operations.Queue.Enqueue(FileOperationKind.CreateArchive, sources, zipPath, _services.CreateInteraction(),
            context => ArchiveService.CreateZipAsync(sources, zipPath, CompressionLevel.Optimal, context));
        await operation.Completion.ConfigureAwait(true);
        ReportFinished(operation);
        if (operation.State == FileOperationState.Completed)
            SelectWhenAvailable([zipPath]);
    }

    private async Task ExtractAsync()
    {
        foreach (var archive in Selection.SelectedItems.Where(i => ArchiveService.IsSupportedArchive(i.FullPath)).ToList())
        {
            var parent = Path.GetDirectoryName(archive.FullPath)!;
            string destination;
            try
            {
                destination = await Task.Run(() => ArchiveService.ChooseSmartDestination(archive.FullPath, parent)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await ReportErrorAsync(ex).ConfigureAwait(true);
                continue;
            }

            var operation = _services.Operations.Queue.Enqueue(FileOperationKind.ExtractArchive, [archive.FullPath], destination, _services.CreateInteraction(),
                context => ArchiveService.ExtractAsync(archive.FullPath, destination, context));
            await operation.Completion.ConfigureAwait(true);
            ReportFinished(operation);
        }
    }

    private async Task RestoreAsync()
    {
        foreach (var item in Selection.SelectedItems.Where(i => i.TrashItem is not null).ToList())
        {
            try
            {
                await _services.Trash.RestoreAsync(item.TrashItem!).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ReportErrorAsync(ex).ConfigureAwait(true);
            }
        }

        Refresh();
    }

    private async Task EmptyTrashAsync()
    {
        var localizer = _services.Localizer;
        var confirm = await _services.Dialogs.ConfirmAsync(new ConfirmRequest(localizer["Dialog.EmptyTrashTitle"], localizer["Dialog.EmptyTrashMessage"],
            localizer["Command.EmptyTrash"], IsDestructive: true)).ConfigureAwait(true);
        if (confirm.Result != DialogResult.Primary)
            return;
        try
        {
            await _services.Trash.EmptyAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportErrorAsync(ex).ConfigureAwait(true);
        }

        Refresh();
    }

    private void ToggleTag(string? tagId)
    {
        if (tagId is null || IsRemote)
            return;
        var paths = Selection.SelectedItems.Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
            return;
        _services.Tags.ToggleTag(paths, tagId);
        ApplyTags(Selection.SelectedItems);
    }

    /// <summary>Selects items as soon as the monitor/refresh has added them to the list.</summary>
    private void SelectWhenAvailable(params string[] paths)
    {
        var models = paths.Select(p => _itemsByPath.GetValueOrDefault(p)).Where(m => m is not null).Select(m => m!).ToList();
        if (models.Count == paths.Length)
        {
            Selection.SelectOnly(models);
            ScrollIntoViewRequested?.Invoke(this, models[^1]);
            return;
        }

        _reselectAfterLoad = [.. paths];
        _pendingSelection = paths;
    }

    private string[]? _pendingSelection;

    /// <summary>Asks the view to bring an item into view (after paste, create, rename, type-to-select).</summary>
    public event EventHandler<FileItemViewModel>? ScrollIntoViewRequested;

    internal void FlushPendingSelection()
    {
        if (_pendingSelection is not { } pending)
            return;
        var models = pending.Select(p => _itemsByPath.GetValueOrDefault(p)).Where(m => m is not null).Select(m => m!).ToList();
        if (models.Count == 0)
            return;
        _pendingSelection = null;
        Selection.SelectOnly(models);
        ScrollIntoViewRequested?.Invoke(this, models[^1]);
    }

    private void ReportFinished(FileOperation operation)
    {
        if (operation.State == FileOperationState.Failed && operation.Failure is { } failure)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(failure, "File operation {Kind} failed", operation.Kind);
            _services.Notifications.Show(new InAppNotification(_services.Localizer["Notify.OperationFailed"], failure.Message, NotificationSeverity.Error));
        }
        else if (operation.State == FileOperationState.CompletedWithErrors)
        {
            _services.Notifications.Show(new InAppNotification(_services.Localizer["Notify.OperationPartial"],
                _services.Localizer.Format("Notify.SkippedItems", operation.Errors.Count), NotificationSeverity.Warning));
        }
    }

    private Task ReportErrorAsync(Exception exception)
    {
        _logger.LogWarning(exception, "Item command failed");
        var kind = exception is OperationStepException step ? step.Kind : IoErrorClassifier.Classify(exception);
        return _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.OperationTitle"], _services.Localizer["ErrorKind." + kind] + Environment.NewLine + exception.Message);
    }

    /// <summary>Drag-and-drop default: move within the same volume, copy across volumes (Ctrl forces copy, Shift forces move).</summary>
    public static bool ShouldMoveOnDrop(IReadOnlyList<string> sources, string destination, bool controlPressed, bool shiftPressed, IVolumeLocator volumes)
    {
        if (controlPressed)
            return false;
        if (shiftPressed)
            return true;
        return sources.All(s => volumes.AreOnSameVolume(s, destination));
    }
}

/// <summary>Validates a bulk rename completely before anything is touched.</summary>
public static class BulkRenamePlanner
{
    public sealed record Result(IReadOnlyList<(string Path, string NewName)> Renames, string? Error);

    public static Result Plan(IReadOnlyList<(string Path, bool IsDirectory)> items, string pattern)
    {
        var renames = new List<(string, string)>();
        var targets = new HashSet<string>(PathUtil.Comparer);
        var sources = items.Select(i => i.Path).ToHashSet(PathUtil.Comparer);
        var width = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        for (var i = 0; i < items.Count; i++)
        {
            var (path, isDirectory) = items[i];
            var extension = isDirectory ? string.Empty : PathUtil.SplitName(Path.GetFileName(path), false).Extension;
            var number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0');
            var name = pattern.Replace("#", number, StringComparison.Ordinal) + extension;
            var validation = PathUtil.ValidateName(name);
            if (validation != NameValidationResult.Valid)
                return new Result([], validation.ToString());

            var target = Path.Combine(Path.GetDirectoryName(path)!, name);
            if (!targets.Add(target))
                return new Result([], "Duplicate");
            if ((File.Exists(target) || Directory.Exists(target)) && !sources.Contains(target))
                return new Result([], "AlreadyExists");
            renames.Add((path, name));
        }

        // Renames that collide with another item of the same batch must go through that item's new name first;
        // ordering by "target not currently occupied" avoids transient collisions.
        var ordered = new List<(string Path, string NewName)>();
        var pending = new List<(string Path, string NewName)>(renames);
        var occupied = new HashSet<string>(sources, PathUtil.Comparer);
        while (pending.Count > 0)
        {
            var next = pending.FindIndex(r => !occupied.Contains(Path.Combine(Path.GetDirectoryName(r.Path)!, r.NewName)) || PathUtil.AreEqual(r.Path, Path.Combine(Path.GetDirectoryName(r.Path)!, r.NewName)));
            if (next < 0)
                return new Result([], "Cycle");
            var rename = pending[next];
            pending.RemoveAt(next);
            occupied.Remove(rename.Path);
            occupied.Add(Path.Combine(Path.GetDirectoryName(rename.Path)!, rename.NewName));
            if (!PathUtil.AreEqual(rename.Path, Path.Combine(Path.GetDirectoryName(rename.Path)!, rename.NewName)))
                ordered.Add(rename);
        }

        return new Result(ordered, null);
    }
}
