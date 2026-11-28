using CommunityToolkit.Mvvm.Input;
using Navigator.Core.IO;
using Navigator.Presentation.Items;

namespace Navigator.Presentation.Panes;

/// <summary>
/// Asks the view to edit an item's name in place (F2). <see cref="Text"/> is what the edit box starts with (the
/// shown name: without the extension when extensions are hidden); <see cref="SelectionLength"/> covers the stem.
/// The view sets <see cref="Handled"/>; when nothing handles it the rename falls back to the dialog.
/// </summary>
public sealed class InlineRenameEventArgs(FileItemViewModel item, string text, int selectionLength) : EventArgs
{
    public FileItemViewModel Item { get; } = item;

    public string Text { get; } = text;

    public int SelectionLength { get; } = selectionLength;

    public bool Handled { get; set; }
}

/// <summary>Renaming: in place for one item, the multi-rename dialog for several (or Ctrl+R).</summary>
public sealed partial class PaneViewModel
{
    /// <summary>Raised by Rename for a single item; see <see cref="InlineRenameEventArgs"/>.</summary>
    public event EventHandler<InlineRenameEventArgs>? InlineRenameRequested;

    /// <summary>Ctrl+R: the multi-rename dialog for the selection (one item or many).</summary>
    public IAsyncRelayCommand MultiRenameCommand { get; private set; } = null!;

    private void InitializeRenameCommands()
        => MultiRenameCommand = new AsyncRelayCommand(MultiRenameAsync, () => CanModifySelection && !IsRemote && !IsTrash);

    private void RaiseRenameCommandState() => MultiRenameCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// A just-created item (New folder / New file / New → document) waiting for the listing to show it, so its name can
    /// be edited in place. Path for local folders; remote listings are matched by name (Path is empty).
    /// </summary>
    private (string Path, string Name)? _renameWhenListed;

    /// <summary>The item whose in-place edit names it right after creation: committing rewrites the creation's undo step.</summary>
    private string? _freshlyCreated;

    /// <summary>Whether new items are named in the list (a view edits names in place) instead of in a dialog first.</summary>
    private bool CanNameInPlace => InlineRenameRequested is not null && IsBrowsingList;

    /// <summary>Selects the item at <paramref name="path"/> once the folder monitor lists it, then opens its name for editing.</summary>
    private void RenameWhenListed(string path, string name)
    {
        _renameWhenListed = (path, name);
        SelectWhenAvailable(path);
        StartPendingRename();
    }

    /// <summary>
    /// Runs after every list update: a lookup in the path index (or, remotely, one pass over the listing) and nothing
    /// at all while no new item is waiting, so ordinary updates pay no cost.
    /// </summary>
    private void StartPendingRename()
    {
        if (_renameWhenListed is not { } pending)
            return;

        FileItemViewModel? item;
        if (IsRemote)
        {
            item = null;
            foreach (var candidate in _sortedAll)
            {
                if (string.Equals(candidate.Name, pending.Name, StringComparison.Ordinal))
                {
                    item = candidate;
                    break;
                }
            }
        }
        else
        {
            item = _itemsByPath.GetValueOrDefault(pending.Path);
        }

        if (item is null)
            return;

        _renameWhenListed = null;
        var shown = Selection.IndexOf(item) >= 0; // hidden by the name filter: no row to edit on
        if (shown && IsRemote)
        {
            Selection.SelectOnly([item]);
            ScrollIntoViewRequested?.Invoke(this, item);
        }

        if (shown && TryRenameInPlace(item))
        {
            if (!IsRemote)
                _freshlyCreated = item.FullPath;
            return;
        }

        _ = RenameWithDialogAsync(item);
    }

    /// <summary>Starts editing <paramref name="item"/>'s name in the list; false when no view could (dialog instead).</summary>
    private bool TryRenameInPlace(FileItemViewModel item)
    {
        _freshlyCreated = null;
        var handler = InlineRenameRequested;
        if (handler is null)
            return false;

        var text = item.DisplayName;
        var selection = item.IsDirectory || HiddenExtensionOf(item).Length > 0 ? text.Length : PathUtil.SplitName(text, false).Stem.Length;
        var request = new InlineRenameEventArgs(item, text, selection);
        handler(this, request);
        return request.Handled;
    }

    /// <summary>The extension the list hides ("Show file extensions" off): kept as it is when the name is edited.</summary>
    private static string HiddenExtensionOf(FileItemViewModel item)
    {
        var shown = item.DisplayName;
        return !item.IsDirectory && item.Name.Length > shown.Length && item.Name.StartsWith(shown, StringComparison.Ordinal)
            ? item.Name[shown.Length..]
            : string.Empty;
    }

    private static string FullNameFromEdit(FileItemViewModel item, string text) => text.Trim() + HiddenExtensionOf(item);

    /// <summary>Validation message for the in-place editor; null when the text can be committed.</summary>
    public string? ValidateInlineName(FileItemViewModel item, string text)
    {
        ArgumentNullException.ThrowIfNull(item);
        return string.IsNullOrWhiteSpace(text) ? _services.Localizer["Validation.Empty"] : ValidateFullName(item, FullNameFromEdit(item, text));
    }

    /// <summary>Whether <paramref name="item"/> can be renamed to <paramref name="name"/> (the complete new name).</summary>
    private string? ValidateFullName(FileItemViewModel item, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return _services.Localizer["Validation.Empty"];
        if (string.Equals(name, item.Name, StringComparison.Ordinal))
            return null;

        if (IsRemote)
        {
            var existing = _ordered.Select(i => i.Name).Where(n => n != item.Name).ToHashSet(StringComparer.Ordinal);
            return ValidateRemoteName(existing)(name);
        }

        var result = PathUtil.ValidateName(name);
        if (result != NameValidationResult.Valid)
            return _services.Localizer["Validation." + result];
        if (string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase))
            return null; // case-only change
        var target = Path.Combine(Path.GetDirectoryName(item.FullPath)!, name);
        return File.Exists(target) || Directory.Exists(target) ? _services.Localizer["Validation.AlreadyExists"] : null;
    }

    /// <summary>
    /// Ends an in-place edit with <paramref name="text"/>. Returns false (keep editing) only when the text is not a
    /// usable name; unchanged text, success and I/O failures (reported) all end the edit.
    /// </summary>
    /// <param name="selectRenamed">Select the item under its new name (false when the edit moves on to another item).</param>
    public async Task<bool> CommitInlineRenameAsync(FileItemViewModel item, string text, bool selectRenamed = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ValidateInlineName(item, text) is not null)
            return false;

        var name = FullNameFromEdit(item, text);
        if (string.Equals(name, item.Name, StringComparison.Ordinal))
            return true;

        if (IsRemote)
        {
            await RunRemoteAsync(token => _services.Remote.RenameAsync(item.FullPath, name, token)).ConfigureAwait(true);
            return true;
        }

        var fresh = string.Equals(_freshlyCreated, item.FullPath, StringComparison.Ordinal);
        _freshlyCreated = null;
        try
        {
            var renamed = fresh ? _services.Operations.RenameCreated(item.FullPath, name) : _services.Operations.Rename(item.FullPath, name);
            if (selectRenamed)
                SelectWhenAvailable(renamed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportErrorAsync(ex).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>The item <paramref name="delta"/> places after (or before) <paramref name="item"/> in display order.</summary>
    public FileItemViewModel? NeighbourOf(FileItemViewModel item, int delta)
    {
        ArgumentNullException.ThrowIfNull(item);
        var index = Selection.IndexOf(item);
        if (index < 0)
            index = Selection.IndexOfPath(item.FullPath);
        if (index < 0)
            return null;
        var next = index + delta;
        return next >= 0 && next < _ordered.Count ? _ordered[next] : null;
    }

    /// <summary>Selects <paramref name="item"/> alone and starts renaming it in place (Tab / Shift+Tab in the editor).</summary>
    public void BeginRename(FileItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Selection.SelectOnly([item]);
        _ = TryRenameInPlace(item);
    }

    /// <summary>
    /// The multi-rename dialog. The batch is checked as a whole first (names, clashes, order), then renamed on a worker
    /// thread so a large selection never freezes the window, and recorded as one undo step.
    /// </summary>
    private async Task MultiRenameAsync()
    {
        if (IsRemote || IsTrash || !CanModifySelection)
            return;

        var selected = Selection.SelectedItems;
        var items = new List<MultiRenameItem>(selected.Count);
        foreach (var item in selected)
            items.Add(new MultiRenameItem(item.FullPath, item.IsDirectory, item.Entry.LastWriteTimeUtc));

        var model = new MultiRenameViewModel(items, _services.Localizer);
        if (!await _services.Dialogs.ShowMultiRenameAsync(model).ConfigureAwait(true))
            return;

        var renames = model.GetRenames();
        if (renames.Count == 0)
            return;

        var plan = BulkRenamePlanner.Order(renames);
        if (plan.Error is { } error)
        {
            await _services.Dialogs.ShowMessageAsync(_services.Localizer["Command.MultiRename"], _services.Localizer["Validation." + error]).ConfigureAwait(true);
            return;
        }

        var operations = _services.Operations;
        var outcome = await Task.Run(() => operations.RenameMany(plan.Renames)).ConfigureAwait(true);
        operations.RecordRenames(outcome.Done);
        if (outcome.Done.Count > 0)
            SelectWhenAvailable([.. outcome.Done.Select(r => r.RenamedPath)]);
        if (outcome.Failure is { } failure)
            await ReportErrorAsync(failure).ConfigureAwait(true);
    }
}
