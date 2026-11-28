using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Settings;
using Navigator.Core.Sorting;
using Navigator.Presentation.Items;

namespace Navigator.Presentation.Panes;

/// <summary>
/// Column (Miller) layout. The current folder stays the pane's own list (selection, menus, rename, drag and drop all
/// work as in the other layouts) and is drawn rightmost; every folder between the column anchor and it gets a column
/// to its left with the way onward highlighted. Opening a folder navigates (so history, breadcrumb and Back work), and
/// the trail is updated incrementally: going one level deeper reuses the listing just left instead of reading it again.
/// </summary>
public sealed partial class PaneViewModel
{
    private string? _columnAnchor;

    /// <summary>Columns left of the current folder, outermost first.</summary>
    public ObservableCollection<MillerColumnViewModel> TrailColumns { get; } = [];

    public bool IsColumnsLayout => _layout == FolderLayout.Columns;

    public bool HasTrailColumns => TrailColumns.Count > 0;

    /// <summary>Clicking a folder of the current column: it opens as the next column (always in place, never in a new tab).</summary>
    public void OpenInColumns(FileItemViewModel? folder)
    {
        if (folder is { IsDirectory: true } && IsColumnsLayout)
            NavigateInPlace(Location.Folder(folder.FullPath));
    }

    /// <summary>
    /// An entry of a column left of the current one was clicked: a folder continues the trail from there; a file
    /// makes its folder the current column with the file selected.
    /// </summary>
    public void ActivateTrailEntry(MillerColumnViewModel? column, ColumnEntryViewModel? entry)
    {
        if (column is null || entry is null)
            return;
        if (entry.Item.IsDirectory)
        {
            if (!PathUtil.AreEqual(entry.Item.FullPath, _location.Path))
                NavigateInPlace(Location.Folder(entry.Item.FullPath));
            return;
        }

        NavigateInPlace(Location.Folder(column.Path));
        _reselectAfterLoad = [entry.Item.FullPath];
    }

    /// <summary>Double-click on a file of a column left of the current one opens it like in the list.</summary>
    public Task OpenTrailEntryAsync(ColumnEntryViewModel? entry)
        => entry is { Item.IsDirectory: false } ? OpenItemAsync(entry.Item) : Task.CompletedTask;

    /// <summary>Left arrow in the column layout: back to the parent column, with the folder just left selected.</summary>
    public void ColumnsGoBack()
    {
        if (!IsFolder || ParentPath is not { } parent || _location.Path is not { } child || !Directory.Exists(parent))
            return;
        NavigateInPlace(Location.Folder(parent));
        _reselectAfterLoad = [child];
    }

    /// <summary>Navigation that never turns into "open in new tab" (column steps are part of one browsing gesture).</summary>
    private void NavigateInPlace(Location location)
    {
        if (!History.Navigate(location))
        {
            Refresh();
            return;
        }

        ApplyLocation();
    }

    /// <summary>
    /// Called when the layout changes: entering columns anchors them at the current folder; leaving drops the trail.
    /// </summary>
    private void OnColumnsLayoutChanged()
    {
        ClearTrailColumns();
        _columnAnchor = IsColumnsLayout && IsFolder ? _location.Path : null;
        OnPropertyChanged(nameof(IsColumnsLayout));
    }

    /// <summary>True when the column layout simply continues at the new location (its per-folder preference is not applied).</summary>
    private bool ContinuesColumns()
        => IsColumnsLayout && IsFolder && _location.Path is { } path && ColumnTrail.Continues(_columnAnchor, path);

    /// <summary>
    /// Brings the trail in step with the location just applied. Columns that are still on the way are kept (only their
    /// highlight moves), the rest are dropped, and new ones are filled from the folder just left when that is the one
    /// needed, otherwise from the directory cache or a background listing.
    /// </summary>
    private void UpdateTrailColumns(string? previousFolder, List<FileItemViewModel> previousItems)
    {
        if (!IsColumnsLayout || !IsFolder || _location.Path is not { } current)
        {
            ClearTrailColumns();
            if (!IsColumnsLayout)
                _columnAnchor = null;
            return;
        }

        _columnAnchor = ColumnTrail.NextAnchor(_columnAnchor, current);
        var wanted = ColumnTrail.Build(_columnAnchor, current);
        if (wanted.Count > 0 && !PathUtil.AreEqual(wanted[0].Folder, _columnAnchor))
            _columnAnchor = wanted[0].Folder; // the trail was capped: the anchor follows its first column

        var keep = 0;
        while (keep < wanted.Count && keep < TrailColumns.Count && PathUtil.AreEqual(TrailColumns[keep].Path, wanted[keep].Folder))
            keep++;
        for (var i = TrailColumns.Count - 1; i >= keep; i--)
        {
            TrailColumns[i].Dispose();
            TrailColumns.RemoveAt(i);
        }

        for (var i = keep; i < wanted.Count; i++)
        {
            var folder = wanted[i].Folder;
            var column = new MillerColumnViewModel(folder, FolderDisplayName(folder));
            TrailColumns.Add(column);
            if (previousItems.Count > 0 && PathUtil.AreEqual(folder, previousFolder))
                column.SetItems(previousItems);
            else
                _ = LoadTrailColumnAsync(column);
        }

        for (var i = 0; i < wanted.Count; i++)
            TrailColumns[i].SetOpenChild(wanted[i].OpenChild);
        OnPropertyChanged(nameof(HasTrailColumns));
    }

    private void ClearTrailColumns()
    {
        if (TrailColumns.Count == 0)
            return;
        foreach (var column in TrailColumns)
            column.Dispose();
        TrailColumns.Clear();
        OnPropertyChanged(nameof(HasTrailColumns));
    }

    /// <summary>Lists one column's folder off the UI thread with the pane's filter and sort; the cache is tried first.</summary>
    private async Task LoadTrailColumnAsync(MillerColumnViewModel column)
    {
        var token = column.Token;
        var filter = CurrentFilter();
        var sort = _sort;
        var context = _displayContext;
        try
        {
            var cached = _services.DirectoryCache.TryGet(column.Path, filter);
            List<FileEntry> entries = cached is not null ? [.. cached] : await Task.Run(() => ListFolderAsync(column.Path, filter, token), token).ConfigureAwait(true);
            if (cached is null)
                _services.DirectoryCache.Store(column.Path, filter, entries);

            var models = await Task.Run(() =>
            {
                EntrySorter.Sort(entries, sort);
                var list = new List<FileItemViewModel>(entries.Count);
                foreach (var entry in entries)
                    list.Add(new FileItemViewModel(entry, context));
                return list;
            }, token).ConfigureAwait(true);

            if (!token.IsCancellationRequested)
                column.SetItems(models);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The column left the trail before its listing arrived.
        }
        catch (Exception ex) when (ex is DirectoryEnumerationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Listing a column folder failed");
            if (!token.IsCancellationRequested)
                column.SetError(ex is DirectoryEnumerationException { Kind: EnumerationErrorKind.AccessDenied } ? _services.Localizer["Error.AccessDenied"] : _services.Localizer["Error.FolderNotFound"]);
        }
    }

    private static async Task<List<FileEntry>> ListFolderAsync(string path, EnumerationFilter filter, CancellationToken token)
    {
        var entries = new List<FileEntry>();
        await foreach (var batch in DirectoryEnumerator.EnumerateBatchesAsync(path, filter, token).ConfigureAwait(false))
            entries.AddRange(batch);
        return entries;
    }

    /// <summary>A new sort applies to the trail too; columns are re-sorted off the UI thread and swapped in at once.</summary>
    private void ResortTrailColumns()
    {
        if (TrailColumns.Count == 0)
            return;
        var comparer = new FileEntryComparer(_sort);
        foreach (var column in TrailColumns)
        {
            if (!column.IsLoading && !column.HasError)
                _ = ResortColumnAsync(column, comparer);
        }
    }

    private static async Task ResortColumnAsync(MillerColumnViewModel column, FileEntryComparer comparer)
    {
        var items = column.Items;
        var token = column.Token;
        try
        {
            var sorted = await Task.Run(() =>
            {
                var list = new List<FileItemViewModel>(items);
                list.Sort((a, b) => comparer.Compare(a.Entry, b.Entry));
                return list;
            }, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
                column.SetItems(sorted);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The column was dropped while sorting.
        }
    }
}
