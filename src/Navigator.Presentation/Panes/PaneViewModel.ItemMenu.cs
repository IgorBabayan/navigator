using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Archives;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Core.Navigation;
using Navigator.Presentation.Items;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Panes;

/// <summary>A destination offered under "Send to": a well-known folder or a removable drive.</summary>
public sealed record SendToTarget(string Label, string Path, string IconKey);

/// <summary>Commands that exist for the type-aware item context menu (see <see cref="ItemMenuPlanner"/>).</summary>
public sealed partial class PaneViewModel
{
    private static readonly KnownFolder[] SendToFolders = [KnownFolder.Desktop, KnownFolder.Documents, KnownFolder.Downloads];

    public IAsyncRelayCommand RunAsAdministratorCommand { get; private set; } = null!;

    public IAsyncRelayCommand RunAsOtherUserCommand { get; private set; } = null!;

    public IAsyncRelayCommand ExtractToFolderCommand { get; private set; } = null!;

    public IAsyncRelayCommand CompressEachCommand { get; private set; } = null!;

    public IAsyncRelayCommand PasteIntoFolderCommand { get; private set; } = null!;

    public IAsyncRelayCommand CreateFolderWithSelectionCommand { get; private set; } = null!;

    public IAsyncRelayCommand CreateShortcutCommand { get; private set; } = null!;

    /// <summary>Copies the selection into the folder given as parameter.</summary>
    public IAsyncRelayCommand<string> SendToCommand { get; private set; } = null!;

    public IAsyncRelayCommand OpenSelectionInCodeEditorCommand { get; private set; } = null!;

    /// <summary>Shows the selected folder in the other pane of the tab (opening split view when needed).</summary>
    public IRelayCommand OpenInOtherPaneCommand { get; private set; } = null!;

    /// <summary>Search results and tag views: goes to the folder that contains the item and selects it.</summary>
    public IRelayCommand OpenFileLocationCommand { get; private set; } = null!;

    /// <summary>Extracts into a folder the user picks.</summary>
    public IAsyncRelayCommand ExtractFilesCommand { get; private set; } = null!;

    /// <summary>Extracts right next to the archive, whatever it contains.</summary>
    public IAsyncRelayCommand ExtractHereCommand { get; private set; } = null!;

    /// <summary>Opens a file with the application given as <see cref="OpenWithChoice"/>.</summary>
    public IAsyncRelayCommand<OpenWithChoice> OpenWithApplicationCommand { get; private set; } = null!;

    /// <summary>The OS "Open with" chooser where there is one, otherwise a file picker for the program.</summary>
    public IAsyncRelayCommand<string> ChooseApplicationCommand { get; private set; } = null!;

    private bool HasSingleLocalFile => !IsRemote && !IsTrash && Selection.SelectedItems is [{ IsDirectory: false }];

    private void InitializeItemMenuCommands()
    {
        RunAsAdministratorCommand = new AsyncRelayCommand(() => RunSelectionElevatedAsync(ElevationOptions.Administrator), () => HasSingleLocalFile);
        RunAsOtherUserCommand = new AsyncRelayCommand(() => RunSelectionElevatedAsync(ElevationOptions.OtherUser), () => HasSingleLocalFile);
        ExtractToFolderCommand = new AsyncRelayCommand(() => ExtractArchivesAsync(ExtractTarget.OwnFolder), () => ExtractCommand.CanExecute(null));
        ExtractFilesCommand = new AsyncRelayCommand(() => ExtractArchivesAsync(ExtractTarget.Chosen), () => ExtractCommand.CanExecute(null));
        ExtractHereCommand = new AsyncRelayCommand(() => ExtractArchivesAsync(ExtractTarget.Beside), () => ExtractCommand.CanExecute(null));
        OpenInOtherPaneCommand = new RelayCommand(OpenSelectionInOtherPane, () => !IsRemote && !IsTrash && Selection.SelectedItems is [{ IsDirectory: true }]);
        OpenFileLocationCommand = new RelayCommand(OpenSelectionLocation, () => (IsSearch || _location.Kind == LocationKind.Tag) && Selection.Count == 1);
        OpenWithApplicationCommand = new AsyncRelayCommand<OpenWithChoice>(LaunchWithAsync);
        ChooseApplicationCommand = new AsyncRelayCommand<string>(ChooseApplicationAsync);
        CompressEachCommand = new AsyncRelayCommand(CompressEachAsync, () => CanModifySelection && Selection.Count > 1);
        PasteIntoFolderCommand = new AsyncRelayCommand(PasteIntoSelectedFolderAsync, () => CanModifySelection && Selection.SelectedItems is [{ IsDirectory: true }]);
        CreateFolderWithSelectionCommand = new AsyncRelayCommand(CreateFolderWithSelectionAsync, () => CanModifySelection && CanCreateItems);
        CreateShortcutCommand = new AsyncRelayCommand(MakeShortcutForSelectionAsync, () => CanModifySelection && Selection.Count == 1);
        SendToCommand = new AsyncRelayCommand<string>(SendSelectionToAsync, _ => HasSelection && !IsTrash && !IsRemote);
        OpenSelectionInCodeEditorCommand = new AsyncRelayCommand(
            () => Selection.SelectedItems is [var only] ? LaunchCodeEditorAsync(only.FullPath) : Task.CompletedTask,
            () => !IsRemote && Selection.Count == 1);
    }

    private void RaiseItemMenuCommandState()
    {
        RunAsAdministratorCommand.NotifyCanExecuteChanged();
        RunAsOtherUserCommand.NotifyCanExecuteChanged();
        ExtractToFolderCommand.NotifyCanExecuteChanged();
        CompressEachCommand.NotifyCanExecuteChanged();
        PasteIntoFolderCommand.NotifyCanExecuteChanged();
        CreateFolderWithSelectionCommand.NotifyCanExecuteChanged();
        CreateShortcutCommand.NotifyCanExecuteChanged();
        SendToCommand.NotifyCanExecuteChanged();
        OpenSelectionInCodeEditorCommand.NotifyCanExecuteChanged();
        OpenInOtherPaneCommand.NotifyCanExecuteChanged();
        OpenFileLocationCommand.NotifyCanExecuteChanged();
        ExtractFilesCommand.NotifyCanExecuteChanged();
        ExtractHereCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// "Send to" destinations, resolved when the sub-menu opens rather than on every right-click: the known folders that
    /// exist plus ready removable drives, never the folder being shown.
    /// </summary>
    public IReadOnlyList<SendToTarget> GetSendToTargets()
    {
        var targets = new List<SendToTarget>(SendToFolders.Length + 2);
        foreach (var folder in SendToFolders)
        {
            if (_services.KnownFolders.GetPath(folder) is { } path && !PathUtil.AreEqual(path, CurrentDirectory))
                targets.Add(new SendToTarget(_services.Localizer["KnownFolder." + folder], path, folder.ToString()));
        }

        foreach (var volume in _services.Volumes.GetVolumes())
        {
            if (volume is { Kind: VolumeKind.Removable, IsReady: true } && !PathUtil.AreEqual(volume.RootPath, CurrentDirectory))
                targets.Add(new SendToTarget(string.IsNullOrWhiteSpace(volume.Label) ? volume.RootPath : volume.Label, volume.RootPath, "DriveRemovable"));
        }

        return targets;
    }

    private async Task RunSelectionElevatedAsync(ElevationOptions mode)
    {
        if (!HasSingleLocalFile)
            return;
        var program = Selection.SelectedItems[0].FullPath;

        // A declined UAC / credential prompt reports false as well; that is the user's choice, not an error to show.
        if (await _services.Launcher.RunElevatedAsync(program, mode).ConfigureAwait(true))
            _services.Recent.Add(program);
    }

    private async Task PasteIntoSelectedFolderAsync()
    {
        if (Selection.SelectedItems is not [{ IsDirectory: true } folder])
            return;

        await PasteIntoAsync(folder.FullPath).ConfigureAwait(true);
    }

    /// <summary>Asks for a name, creates the folder next to the selection and moves the selection into it.</summary>
    private async Task CreateFolderWithSelectionAsync()
    {
        if (CurrentDirectory is not { } directory || !CanModifySelection)
            return;

        var selected = Selection.SelectedItems;
        var sources = new List<string>(selected.Count);
        foreach (var item in selected)
            sources.Add(item.FullPath);

        var localizer = _services.Localizer;
        var proposal = selected is [var single] ? PathUtil.SplitName(single.Name, single.IsDirectory).Stem : localizer["Item.NewFolderName"];
        if (string.IsNullOrWhiteSpace(proposal))
            proposal = localizer["Item.NewFolderName"];
        var suggested = PathUtil.GetUniqueName(directory, proposal, isDirectory: true, p => File.Exists(p) || Directory.Exists(p));
        var folderName = await _services.Dialogs.PromptTextAsync(new TextInputRequest(
            localizer["Command.CreateFolderWithSelection"], localizer["Dialog.NameLabel"], suggested, localizer["Dialog.Create"],
            ValidateNewName(directory))).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folderName))
            return;

        string folder;
        try
        {
            folder = _services.Operations.CreateFolder(directory, folderName.Trim());
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
        {
            await ReportErrorAsync(failure).ConfigureAwait(true);
            return;
        }

        await TransferIntoAsync(sources, folder, move: true).ConfigureAwait(true);
        SelectWhenAvailable(folder);
    }

    private async Task MakeShortcutForSelectionAsync()
    {
        if (IsRemote || Selection.SelectedItems is not [var item])
            return;

        var directory = Path.GetDirectoryName(item.FullPath);
        if (directory is null)
            return;

        var extension = _services.Launcher.ShortcutExtension;
        var label = _services.Localizer.Format("Item.ShortcutName", item.Name);

        // Without an extension (symbolic links) the whole label is the stem, so "(2)" never lands inside "a.txt".
        var name = PathUtil.GetUniqueName(directory, label + extension, isDirectory: extension.Length == 0,
            p => File.Exists(p) || Directory.Exists(p) || Path.Exists(p));
        var shortcut = Path.Combine(directory, name);
        if (!await _services.Launcher.CreateShortcutAsync(item.FullPath, shortcut).ConfigureAwait(true))
        {
            await _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.OperationTitle"],
                _services.Localizer.Format("Error.ShortcutFailed", item.Name)).ConfigureAwait(true);
            return;
        }

        if (PathUtil.AreEqual(directory, CurrentDirectory))
            SelectWhenAvailable(shortcut);
    }

    private Task SendSelectionToAsync(string? destination)
    {
        if (string.IsNullOrEmpty(destination) || !HasSelection || IsRemote || IsTrash)
            return Task.CompletedTask;

        var selected = Selection.SelectedItems;
        var sources = new List<string>(selected.Count);
        foreach (var item in selected)
            sources.Add(item.FullPath);
        return TransferIntoAsync(sources, destination, move: false);
    }

    /// <summary>One ZIP per selected item, each next to its source, compressed one after another in the queue.</summary>
    private async Task CompressEachAsync()
    {
        foreach (var item in Selection.SelectedItems.ToList())
        {
            var directory = Path.GetDirectoryName(item.FullPath);
            if (directory is not null)
                await CompressToZipAsync([item.FullPath], directory, PathUtil.SplitName(item.Name, item.IsDirectory).Stem).ConfigureAwait(true);
        }
    }
}
