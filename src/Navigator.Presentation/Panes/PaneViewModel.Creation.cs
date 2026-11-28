using System.IO.Compression;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Archives;
using Navigator.Core.IO;
using Navigator.Core.Operations;
using Navigator.Core.Platform;
using Navigator.Presentation.Menus;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Panes;

/// <summary>
/// The "New" menu (folder, file, shortcut and the document types the system registers) and the Compress menu's
/// "Create archive" / one-click archive formats.
/// </summary>
public sealed partial class PaneViewModel
{
    private static int _creationWarmed;

    /// <summary>"Create archive": name, format and compression level asked in a dialog.</summary>
    public IAsyncRelayCommand CreateArchiveCommand { get; private set; } = null!;

    /// <summary>"Create name.7z" through the installed 7-Zip / libarchive program.</summary>
    public IAsyncRelayCommand CompressToSevenZipCommand { get; private set; } = null!;

    /// <summary>New → Shortcut: a link to an item the user names, created in the shown folder.</summary>
    public IAsyncRelayCommand NewShortcutCommand { get; private set; } = null!;

    /// <summary>New → a document type (<see cref="NewItemTemplate"/> as parameter).</summary>
    public IAsyncRelayCommand<NewItemTemplate> NewFromTemplateCommand { get; private set; } = null!;

    private void InitializeCreationCommands()
    {
        CreateArchiveCommand = new AsyncRelayCommand(CreateArchiveWithOptionsAsync, () => CanModifySelection);
        CompressToSevenZipCommand = new AsyncRelayCommand(
            () => CompressSelectionAsync(ArchiveFormat.SevenZip, CompressionLevel.Optimal), () => CanModifySelection && SevenZipTool.IsAvailable);
        NewShortcutCommand = new AsyncRelayCommand(CreateShortcutHereAsync, () => CanCreateItems);
        NewFromTemplateCommand = new AsyncRelayCommand<NewItemTemplate>(CreateFromTemplateAsync, _ => CanCreateItems);

        // The first pane starts the two slow lookups in the background, so no menu ever waits for them.
        if (Interlocked.Exchange(ref _creationWarmed, 1) == 0)
        {
            SevenZipTool.Warm();
            ArchiveReaderTool.Warm();
            _ = _services.NewItems.GetAsync();
        }
    }

    private void RaiseCreationCommandState()
    {
        CreateArchiveCommand.NotifyCanExecuteChanged();
        CompressToSevenZipCommand.NotifyCanExecuteChanged();
        NewShortcutCommand.NotifyCanExecuteChanged();
        NewFromTemplateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The toolbar's New menu: Folder, File, Shortcut, then every document type, in one list like Explorer's. The types
    /// come from a cache filled at startup, so this normally completes at once.
    /// </summary>
    public async Task<PaneMenu> CreateNewMenuAsync()
    {
        var lifetime = new CancellationTokenSource();
        var entries = PaneMenuComposer.NewChoices(BaseInput(CurrentScope, shiftHeld: false));
        var documents = await NewItemEntriesAsync(lifetime.Token).ConfigureAwait(true);
        if (documents.Count > 0)
        {
            entries.Add(MenuEntry.Divider());
            entries.AddRange(documents);
        }

        return new PaneMenu(entries, lifetime, shell: null);
    }

    private async Task<IReadOnlyList<MenuEntry>> NewItemEntriesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<NewItemTemplate> templates;
        try
        {
            templates = await _services.NewItems.GetAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var entries = new List<MenuEntry>(templates.Count);
        foreach (var template in templates)
        {
            entries.Add(new MenuEntry
            {
                Label = template.DisplayName,
                Glyph = "File",
                SystemIcon = SystemIconKey.ForFileType(template.Extension),
                Command = NewFromTemplateCommand,
                Parameter = template,
                Scopes = MenuScope.Anywhere,
            });
        }

        return entries;
    }

    private async Task CreateFromTemplateAsync(NewItemTemplate? template)
    {
        if (template is null || CurrentDirectory is not { } directory)
            return;

        var localizer = _services.Localizer;
        var suggested = PathUtil.GetUniqueName(directory, template.SuggestedName(localizer["Item.NewPrefix"]), isDirectory: false, ItemExists);
        if (CanNameInPlace)
        {
            try
            {
                RenameWhenListed(_services.Operations.CreateFileFromTemplate(directory, suggested, template), suggested);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await ReportErrorAsync(ex).ConfigureAwait(true);
            }

            return;
        }

        var name = await _services.Dialogs.PromptTextAsync(new TextInputRequest(
            template.DisplayName, localizer["Dialog.NameLabel"], suggested, localizer["Dialog.Create"],
            ValidateNewName(directory), PathUtil.SplitName(suggested, false).Stem.Length)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            var created = _services.Operations.CreateFileFromTemplate(directory, name.Trim(), template);
            _reselectAfterLoad = [created];
            SelectWhenAvailable(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportErrorAsync(ex).ConfigureAwait(true);
        }
    }

    /// <summary>Asks for the item to link to (and the link's name), then creates the shortcut in the shown folder.</summary>
    private async Task CreateShortcutHereAsync()
    {
        if (CurrentDirectory is not { } directory)
            return;

        var localizer = _services.Localizer;
        var form = new FormRequest(localizer["Shortcut.CreateTitle"], localizer["Dialog.Create"],
        [
            new FormField("target", localizer["Shortcut.Target"], FormFieldKind.FilePath, string.Empty, true, [], null,
                value => ItemExists(value.Trim().Trim('"')) ? null : localizer["Shortcut.TargetMissing"]),
            new FormField("name", localizer["Shortcut.Name"], FormFieldKind.Text, string.Empty, false, [], null,
                value => value.Trim().Length == 0 ? null : NameProblem(value)),
        ]);
        if (await _services.Dialogs.ShowFormAsync(form).ConfigureAwait(true) is not { } answers)
            return;

        var target = answers["target"].Trim().Trim('"');
        var extension = _services.Launcher.ShortcutExtension;
        var label = answers["name"].Trim();
        if (label.Length == 0)
            label = Path.GetFileName(PathUtil.TrimTrailingSeparator(target)) is { Length: > 0 } leaf ? leaf : target;
        if (extension.Length > 0 && !label.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            label += extension;

        var name = PathUtil.GetUniqueName(directory, label, isDirectory: extension.Length == 0, ItemExists);
        var link = Path.Combine(directory, name);
        if (await _services.Launcher.CreateShortcutAsync(target, link).ConfigureAwait(true))
            SelectWhenAvailable(link);
        else
            await _services.Dialogs.ShowMessageAsync(localizer["Error.OperationTitle"], localizer.Format("Shortcut.Failed", name)).ConfigureAwait(true);
    }

    /// <summary>"Create archive": the reference app's dialog reduced to what Navigator can write (no passwords or volumes).</summary>
    private async Task CreateArchiveWithOptionsAsync()
    {
        var selected = Selection.SelectedItems;
        if (selected.Count == 0)
            return;

        var localizer = _services.Localizer;
        var formats = ArchiveCreation.Creatable();
        var formatChoices = new List<FormChoice>(formats.Count);
        foreach (var format in formats)
            formatChoices.Add(new FormChoice(format.ToString(), localizer["Archive.Format" + format]));
        FormChoice[] levels =
        [
            new(nameof(CompressionLevel.Optimal), localizer["Archive.LevelOptimal"]),
            new(nameof(CompressionLevel.Fastest), localizer["Archive.LevelFastest"]),
            new(nameof(CompressionLevel.SmallestSize), localizer["Archive.LevelSmallest"]),
            new(nameof(CompressionLevel.NoCompression), localizer["Archive.LevelNone"]),
        ];

        var form = new FormRequest(localizer["Archive.CreateTitle"], localizer["Dialog.Create"],
        [
            new FormField("name", localizer["Archive.Name"], FormFieldKind.Text, ArchiveBaseName(), true, [], null,
                NameProblem),
            new FormField("format", localizer["Archive.Format"], FormFieldKind.Choice, nameof(ArchiveFormat.Zip), true, formatChoices, null, null),
            new FormField("level", localizer["Archive.Level"], FormFieldKind.Choice, nameof(CompressionLevel.Optimal), true, levels, null, null),
        ]);
        if (await _services.Dialogs.ShowFormAsync(form).ConfigureAwait(true) is not { } answers)
            return;
        if (!Enum.TryParse<ArchiveFormat>(answers["format"], out var chosen) || !Enum.TryParse<CompressionLevel>(answers["level"], out var level))
            return;

        await CompressSelectionAsync(chosen, level, answers["name"].Trim()).ConfigureAwait(true);
    }

    /// <summary>Name an archive of the selection gets (before its suffix).</summary>
    private string ArchiveBaseName() => ArchiveBaseNameOf(Selection.SelectedItems);

    /// <summary>
    /// As the reference app names it: the whole item name for one item ("setup.exe" → "setup.exe.zip", so archives of
    /// "report.pdf" and "report.docx" never collide), the containing folder's name for several.
    /// </summary>
    private static string ArchiveBaseNameOf(IReadOnlyList<Items.FileItemViewModel> selected)
    {
        if (selected.Count == 0)
            return "Archive";
        var name = selected.Count == 1 ? selected[0].Name : Path.GetFileName(Path.GetDirectoryName(selected[0].FullPath));
        return string.IsNullOrEmpty(name) ? "Archive" : name;
    }

    private Task CompressSelectionAsync(ArchiveFormat format, CompressionLevel level) => CompressSelectionAsync(format, level, ArchiveBaseName());

    /// <summary>Queues "<paramref name="baseName"/>" plus the format's suffix (made unique) next to the selection; selects it when done.</summary>
    private async Task CompressSelectionAsync(ArchiveFormat format, CompressionLevel level, string baseName)
    {
        var selected = Selection.SelectedItems;
        if (selected.Count == 0)
            return;

        var sources = selected.Select(i => i.FullPath).ToList();
        var directory = Path.GetDirectoryName(sources[0])!;
        var name = PathUtil.GetUniqueName(directory, baseName + ArchiveCreation.Suffix(format), isDirectory: false, ItemExists);
        var archivePath = Path.Combine(directory, name);
        var operation = _services.Operations.Queue.Enqueue(FileOperationKind.CreateArchive, sources, archivePath, _services.CreateInteraction(),
            context => ArchiveCreation.CreateAsync(sources, archivePath, format, level, context));
        await operation.Completion.ConfigureAwait(true);
        ReportFinished(operation);
        if (operation.State == FileOperationState.Completed)
            SelectWhenAvailable([archivePath]);
    }

    /// <summary>Validation message for a typed file name, or null when it is valid.</summary>
    private string? NameProblem(string value)
    {
        var verdict = PathUtil.ValidateName(value.Trim());
        return verdict == NameValidationResult.Valid ? null : _services.Localizer["Validation." + verdict];
    }

    private static bool ItemExists(string path) => File.Exists(path) || Directory.Exists(path);
}
