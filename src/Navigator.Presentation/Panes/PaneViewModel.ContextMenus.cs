using Microsoft.Extensions.Logging;
using Navigator.Core.Archives;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Presentation.Items;
using Navigator.Presentation.Menus;

namespace Navigator.Presentation.Panes;

/// <summary>Where "Extract" puts an archive's contents.</summary>
public enum ExtractTarget
{
    Smart,
    Beside,
    OwnFolder,
    Chosen,
}

/// <summary>Parameter of <see cref="PaneViewModel.OpenWithApplicationCommand"/>.</summary>
public sealed record OpenWithChoice(string Path, AppHandler Application);

/// <summary>Context menus of the pane: composition (<see cref="PaneMenuComposer"/>) plus the shell query behind them.</summary>
public sealed partial class PaneViewModel
{
    /// <summary>How long a sub-menu waits for the OS menu before it shows Navigator's own entries only.</summary>
    private static readonly TimeSpan ShellMenuPatience = TimeSpan.FromSeconds(4);

    private PaneMenuCommands? _menuCommands;

    /// <summary>Raised by "Open in other pane"; the tab shows the location in its other pane.</summary>
    public event EventHandler<Location>? OpenInOtherPaneRequested;

    private PaneMenuCommands MenuCommands => _menuCommands ??= new PaneMenuCommands
    {
        Open = OpenSelectedCommand,
        OpenInNewTab = OpenSelectedInNewTabCommand,
        OpenInNewWindow = OpenSelectedInNewWindowCommand,
        OpenInOtherPane = OpenInOtherPaneCommand,
        OpenFileLocation = OpenFileLocationCommand,
        RunAsAdministrator = RunAsAdministratorCommand,
        RunAsOtherUser = RunAsOtherUserCommand,
        Cut = CutCommand,
        Copy = CopyCommand,
        Paste = PasteCommand,
        PasteIntoFolder = PasteIntoFolderCommand,
        CopyPath = CopyPathCommand,
        CreateFolderWithSelection = CreateFolderWithSelectionCommand,
        CreateShortcut = CreateShortcutCommand,
        Rename = RenameCommand,
        Delete = DeleteCommand,
        DeletePermanently = DeletePermanentlyCommand,
        Properties = PropertiesCommand,
        Restore = RestoreCommand,
        Download = DownloadCommand,
        Upload = UploadCommand,
        TogglePin = PinSelectedCommand,
        Compress = CompressCommand,
        CompressEach = CompressEachCommand,
        ExtractFiles = ExtractFilesCommand,
        ExtractSmart = ExtractCommand,
        ExtractHere = ExtractHereCommand,
        ExtractToFolder = ExtractToFolderCommand,
        OpenInCodeEditor = OpenSelectionInCodeEditorCommand,
        OpenTerminal = OpenTerminalCommand,
        ToggleTag = ToggleTagCommand,
        Duplicate = DuplicateCommand,
        RevealInSystem = RevealInSystemCommand,
        SetLayout = SetLayoutCommand,
        SortBy = SortByCommand,
        ToggleSortDirection = ToggleSortDirectionCommand,
        SetGroupBy = SetGroupByCommand,
        Refresh = RefreshCommand,
        NewFolder = NewFolderCommand,
        NewFile = NewFileCommand,
        EmptyTrash = EmptyTrashCommand,
        SelectAll = SelectAllCommand,
    };

    private MenuScope CurrentScope => _location.Kind switch
    {
        LocationKind.Folder => MenuScope.Folder,
        LocationKind.Search => MenuScope.Search,
        LocationKind.Tag => MenuScope.Tag,
        LocationKind.Trash => MenuScope.Trash,
        LocationKind.Remote => MenuScope.Remote,
        _ => MenuScope.Home,
    };

    /// <summary>
    /// The menu for the selected items. On Windows the Explorer menu for the same items is requested right away, in
    /// parallel, so it is usually ready by the time "Show more options", "Open with" or "Send to" is opened.
    /// </summary>
    /// <param name="shiftHeld">Shift was down on the right-click: Shift-only entries stay in the main list and the shell adds its extended verbs.</param>
    /// <param name="ownerWindow">Window handle that parents dialogs shown by shell entries.</param>
    public PaneMenu CreateItemMenu(bool shiftHeld, Func<nint> ownerWindow)
    {
        ArgumentNullException.ThrowIfNull(ownerWindow);
        var selected = Selection.SelectedItems.ToArray();
        var lifetime = new CancellationTokenSource();
        var scope = CurrentScope;
        var shell = (scope & MenuScope.Local) != 0 && selected.Length > 0
            ? QueryShell([.. selected.Select(i => i.FullPath)], background: false, shiftHeld, lifetime.Token)
            : null;
        var bridge = shell is null ? null : BridgeAsync(shell, ownerWindow);

        var traits = SelectionTraits.Collect(selected, ArchiveService.IsSupportedArchive,
            scope == MenuScope.Remote ? static _ => ElevationOptions.None : _services.Launcher.GetElevationOptions);
        var first = selected.Length > 0 ? selected[0] : null;
        var input = BaseInput(scope, shiftHeld) with
        {
            Selection = traits,
            IsPinned = traits.HasFolders && selected.Where(i => i.IsDirectory).All(i => _pins.IsPinned(i.FullPath)),
            ArchiveName = ArchiveNameFor(selected),
            ExtractFolderName = selected is [var archive] ? ArchiveService.ArchiveStem(archive.Name) : null,
            Tags = TagChoicesFor(selected),
            OpenWithEntries = first is null || first.IsDirectory || scope == MenuScope.Remote ? null : token => OpenWithEntriesAsync(first.FullPath, bridge, token),
            SendToEntries = scope == MenuScope.Remote ? null : token => SendToEntriesAsync(bridge, token),
            ShellEntries = bridge is null ? null : token => ShellRemainderAsync(bridge, token),
        };

        return new PaneMenu(PaneMenuComposer.ForItems(input), lifetime, shell);
    }

    /// <summary>The menu for the empty area of the pane (the shown folder itself).</summary>
    public PaneMenu CreateBackgroundMenu(bool shiftHeld, Func<nint> ownerWindow)
    {
        ArgumentNullException.ThrowIfNull(ownerWindow);
        var lifetime = new CancellationTokenSource();
        var shell = CurrentDirectory is { } folder ? QueryShell([folder], background: true, shiftHeld, lifetime.Token) : null;
        var bridge = shell is null ? null : BridgeAsync(shell, ownerWindow);
        var input = BaseInput(CurrentScope, shiftHeld) with
        {
            IsPinned = CurrentDirectory is { } shown && _pins.IsPinned(shown),
            ShellEntries = bridge is null ? null : token => ShellRemainderAsync(bridge, token),
        };

        return new PaneMenu(PaneMenuComposer.ForBackground(input), lifetime, shell);
    }

    private PaneMenuInput BaseInput(MenuScope scope, bool shiftHeld) => new()
    {
        Text = _services.Localizer,
        Commands = MenuCommands,
        Settings = _services.Settings.Current.General,
        Scope = scope,
        ShiftHeld = shiftHeld,
        CanCreate = CanCreateItems,
        View = new ViewState(Layout, SortColumn, IsSortDescending, GroupBy),
    };

    private Task<IShellMenu?>? QueryShell(IReadOnlyList<string> paths, bool background, bool shiftHeld, CancellationToken cancellationToken)
    {
        if (!_services.ShellMenus.IsSupported || paths.Any(RemoteUri.IsRemote))
            return null;
        var request = new ShellMenuRequest(background, shiftHeld, ShellMenuBridge.NavigatorVerbs);
        return _services.ShellMenus.OpenAsync(paths, request, cancellationToken);
    }

    private async Task<ShellMenuBridge?> BridgeAsync(Task<IShellMenu?> shell, Func<nint> ownerWindow)
    {
        try
        {
            var menu = await shell.WaitAsync(ShellMenuPatience).ConfigureAwait(false);
            return menu is null ? null : new ShellMenuBridge(menu, ownerWindow, _logger);
        }
        catch (TimeoutException slow)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(slow, "The shell context menu took longer than {Seconds} s; showing Navigator's entries only", ShellMenuPatience.TotalSeconds);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<MenuEntry>> ShellRemainderAsync(Task<ShellMenuBridge?> bridge, CancellationToken cancellationToken)
    {
        var shell = await bridge.WaitAsync(cancellationToken).ConfigureAwait(false);
        return shell?.Remainder() ?? [];
    }

    /// <summary>The shell's own "Open with" list where it has one (Windows), otherwise registered applications plus a chooser.</summary>
    private async Task<IReadOnlyList<MenuEntry>> OpenWithEntriesAsync(string path, Task<ShellMenuBridge?>? bridge, CancellationToken cancellationToken)
    {
        if (bridge is not null && await bridge.WaitAsync(cancellationToken).ConfigureAwait(false) is { } shell
            && shell.FindSubmenu(ShellMenuBridge.OpenWithVerb) is { } shellList)
        {
            var fromShell = await shell.ChildrenOfAsync(shellList, cancellationToken).ConfigureAwait(false);
            if (fromShell.Count > 0)
                return fromShell;
        }

        var handlers = await _services.Launcher.GetHandlersAsync(path, cancellationToken).ConfigureAwait(false);
        var entries = new List<MenuEntry>(handlers.Count + 2);
        foreach (var handler in handlers)
        {
            entries.Add(new MenuEntry
            {
                Label = handler.IsDefault ? $"{handler.DisplayName} ({_services.Localizer["OpenWith.Default"]})" : handler.DisplayName,
                Command = OpenWithApplicationCommand,
                Parameter = new OpenWithChoice(path, handler),
                Scopes = MenuScope.Anywhere,
            });
        }

        if (entries.Count > 0)
            entries.Add(MenuEntry.Divider());
        entries.Add(new MenuEntry
        {
            Label = _services.Localizer["OpenWith.Choose"],
            Glyph = "Search",
            Command = ChooseApplicationCommand,
            Parameter = path,
            Scopes = MenuScope.Anywhere,
        });
        return entries;
    }

    /// <summary>The shell's "Send to" list where it has one (Windows), otherwise known folders and removable drives.</summary>
    private async Task<IReadOnlyList<MenuEntry>> SendToEntriesAsync(Task<ShellMenuBridge?>? bridge, CancellationToken cancellationToken)
    {
        if (bridge is not null && await bridge.WaitAsync(cancellationToken).ConfigureAwait(false) is { } shell
            && shell.FindSubmenu(ShellMenuBridge.SendToVerb) is { } shellList)
        {
            var fromShell = await shell.ChildrenOfAsync(shellList, cancellationToken).ConfigureAwait(false);
            if (fromShell.Count > 0)
                return fromShell;
        }

        var entries = new List<MenuEntry>();
        foreach (var target in GetSendToTargets())
            entries.Add(new MenuEntry { Label = target.Label, Glyph = target.IconKey, Command = SendToCommand, Parameter = target.Path, Scopes = MenuScope.Anywhere });
        if (entries.Count == 0)
            entries.Add(new MenuEntry { Label = _services.Localizer["SendTo.NoDestinations"], IsEnabled = false, Scopes = MenuScope.Anywhere });
        return entries;
    }

    private List<TagChoice> TagChoicesFor(FileItemViewModel[] selected)
    {
        var defined = _services.Settings.Current.Tags;
        if (defined.Count == 0 || selected.Length == 0)
            return [];

        var choices = new List<TagChoice>(defined.Count);
        foreach (var tag in defined)
        {
            var onAll = selected.All(item => item.Tags.Any(chip => chip.Id == tag.Id));
            choices.Add(new TagChoice(tag.Id, tag.Name, onAll));
        }

        return choices;
    }

    private static string ArchiveNameFor(FileItemViewModel[] selected)
    {
        if (selected.Length == 0)
            return "Archive.zip";
        var stem = selected is [var only]
            ? PathUtil.SplitName(only.Name, only.IsDirectory).Stem
            : Path.GetFileName(Path.GetDirectoryName(selected[0].FullPath));
        return (string.IsNullOrEmpty(stem) ? "Archive" : stem) + ".zip";
    }

    private void OpenSelectionInOtherPane()
    {
        if (Selection.SelectedItems is [{ IsDirectory: true } folder])
            OpenInOtherPaneRequested?.Invoke(this, Location.FromPath(folder.FullPath));
    }

    private void OpenSelectionLocation()
    {
        if (Selection.SelectedItems is not [var item] || Path.GetDirectoryName(item.FullPath) is not { } parent)
            return;
        Navigate(Location.FromPath(parent));
        SelectWhenAvailable(item.FullPath);
    }

    private async Task LaunchWithAsync(OpenWithChoice? choice)
    {
        if (choice is null)
            return;
        if (await _services.Launcher.OpenWithAsync(choice.Path, choice.Application).ConfigureAwait(true))
        {
            _services.Recent.Add(choice.Path);
            return;
        }

        await _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.OpenFailedTitle"],
            _services.Localizer.Format("Error.OpenFailed", Path.GetFileName(choice.Path))).ConfigureAwait(true);
    }

    private async Task ChooseApplicationAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || await _services.Launcher.ShowOpenWithDialogAsync(path).ConfigureAwait(true))
            return;
        var program = await _services.Dialogs.PickFileAsync(_services.Localizer["OpenWith.Choose"]).ConfigureAwait(true);
        if (program is not null)
            await LaunchWithAsync(new OpenWithChoice(path, new AppHandler(program, Path.GetFileName(program), null, false))).ConfigureAwait(true);
    }
}
