using Navigator.Core.Formatting;
using Navigator.Core.IO;
using Navigator.Core.Monitoring;
using Navigator.Core.Navigation;
using Navigator.Core.Scheduling;
using Navigator.Core.Settings;
using Navigator.Core.Sorting;
using Navigator.Presentation.Items;

namespace Navigator.Presentation.Panes;

public sealed partial class PaneViewModel
{
    private const int MaxRememberedFolderViews = 500;

    public FolderLayout Layout
    {
        get => _layout;
        set
        {
            if (SetProperty(ref _layout, value))
            {
                OnColumnsLayoutChanged();
                OnPropertyChanged(nameof(IsDetailsLayout));
                OnPropertyChanged(nameof(IsListLayout));
                OnPropertyChanged(nameof(IsTileLayout));
                OnPropertyChanged(nameof(TileWidth));
                OnPropertyChanged(nameof(TileIconSize));
                UpdateTileColumns();
                RebuildRows();
                SaveViewPreferences();
            }
        }
    }

    public bool IsDetailsLayout => _layout == FolderLayout.Details;

    public bool IsListLayout => _layout is FolderLayout.List or FolderLayout.Columns;

    public bool IsTileLayout => !IsDetailsLayout && !IsListLayout;

    public SortOptions Sort
    {
        get => _sort;
        set
        {
            if (SetProperty(ref _sort, value))
            {
                OnPropertyChanged(nameof(SortColumn));
                OnPropertyChanged(nameof(IsSortDescending));
                _ = ResortAsync();
                ResortTrailColumns();
                SaveViewPreferences();
            }
        }
    }

    public SortColumn SortColumn => _sort.Column;

    public bool IsSortDescending => _sort.Direction == SortDirection.Descending;

    public GroupBy GroupBy
    {
        get => _groupBy;
        set
        {
            if (SetProperty(ref _groupBy, value))
            {
                RebuildRows();
                SaveViewPreferences();
            }
        }
    }

    /// <summary>Width of one tile (icon layouts), including its margin.</summary>
    public double TileWidth => _layout switch
    {
        FolderLayout.SmallIcons => 96,
        FolderLayout.MediumIcons => 120,
        FolderLayout.LargeIcons => 168,
        FolderLayout.ExtraLargeIcons => 272,
        FolderLayout.Tiles => 260,
        _ => 120,
    };

    public double TileIconSize => _layout switch
    {
        FolderLayout.SmallIcons => 32,
        FolderLayout.MediumIcons => 64,
        FolderLayout.LargeIcons => 112,
        FolderLayout.ExtraLargeIcons => 224,
        FolderLayout.Tiles => 48,
        _ => 64,
    };

    /// <summary>Pixel size thumbnails are decoded at for the current layout (never the full image).</summary>
    public int ThumbnailPixelSize => (int)Math.Ceiling((IsTileLayout ? TileIconSize : 20) * 2);

    public int TileColumns => _tileColumns;

    /// <summary>Reported by the view whenever the list's viewport width changes.</summary>
    public void SetViewportWidth(double width)
    {
        if (Math.Abs(width - _viewportWidth) < 1)
            return;
        _viewportWidth = width;
        if (UpdateTileColumns() && IsTileLayout)
            RebuildRows();
    }

    private bool UpdateTileColumns()
    {
        var columns = Math.Max(1, (int)Math.Floor((_viewportWidth - 8) / TileWidth));
        if (columns == _tileColumns)
            return false;
        _tileColumns = columns;
        OnPropertyChanged(nameof(TileColumns));
        return true;
    }

    private void SortBy(SortColumn column)
        => Sort = _sort.Column == column
            ? _sort with { Direction = _sort.Direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending }
            : _sort with { Column = column, Direction = column is SortColumn.DateModified or SortColumn.DateCreated or SortColumn.Size ? SortDirection.Descending : SortDirection.Ascending };

    private async Task ResortAsync()
    {
        if (_entries.Count == 0)
            return;
        var token = _load?.Token ?? CancellationToken.None;
        try
        {
            await PublishAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Maps sorted entries to item view models, reusing existing ones (keeps selection, thumbnails).</summary>
    private void ApplySorted(List<FileEntry> sorted)
    {
        var models = new List<FileItemViewModel>(sorted.Count);
        foreach (var entry in sorted)
        {
            if (!_itemsByPath.TryGetValue(entry.FullPath, out var model) || !ReferenceEquals(model.Entry, entry))
            {
                var trash = model?.TrashItem;
                model = new FileItemViewModel(entry, _displayContext, trash);
                _itemsByPath[entry.FullPath] = model;
            }

            models.Add(model);
        }

        _sortedAll = models;
        ApplyView();
    }

    private List<FileItemViewModel> _sortedAll = [];

    /// <summary>Applies the name filter and tags to the sorted list, then rebuilds rows.</summary>
    private void ApplyView()
    {
        var filter = new NameFilter(_filterText);
        _ordered = filter.IsEmpty ? _sortedAll : [.. _sortedAll.Where(i => filter.Matches(i.Name))];

        if (_services.Settings.Current.FilesAndFolders.ShowFileTagsColumn)
            ApplyTags(_ordered);

        var reselect = _reselectAfterLoad ?? Selection.SelectedItems.Select(i => i.FullPath).ToList();
        if (!IsLoading)
            _reselectAfterLoad = null;
        Selection.SetItems(_ordered, reselect);
        RebuildRows();
        UpdateStatus();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Items));
        RaiseItemCommandState();
        FlushPendingSelection();
    }

    private void RebuildRows()
    {
        var rows = new List<object>(_ordered.Count + 16);
        if (_groupBy == GroupBy.None)
        {
            AppendRows(rows, _ordered);
        }
        else
        {
            var now = DateTime.Now;
            var groups = _ordered
                .GroupBy(i => EntryGrouper.GetGroup(i.Entry, _groupBy, now))
                .OrderBy(g => g.Key.Order)
                .ThenBy(g => g.Key.Id, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var items = group.ToList();
                rows.Add(new GroupHeaderViewModel(GroupTitle(group.Key), items.Count));
                AppendRows(rows, items);
            }
        }

        Rows.ReplaceAll(rows);
    }

    private void AppendRows(List<object> rows, List<FileItemViewModel> items)
    {
        if (!IsTileLayout)
        {
            rows.AddRange(items);
            return;
        }

        var columns = Math.Max(1, _tileColumns);
        for (var i = 0; i < items.Count; i += columns)
            rows.Add(new TileRowViewModel([.. items.Skip(i).Take(columns)]));
    }

    private string GroupTitle(GroupKey key)
    {
        if (key.Id.StartsWith("type.", StringComparison.Ordinal) && key.Id is not ("type.folder" or "type.none"))
            return _services.Localizer.Format("Item.ExtensionFileType", key.Id[6..].ToUpperInvariant());
        if (key.Id.StartsWith("name.", StringComparison.Ordinal) && key.Id is not ("name.0-9" or "name.other"))
            return key.Id[5..];
        return _services.Localizer["Group." + key.Id];
    }

    private void ApplyTags(IEnumerable<FileItemViewModel> items)
    {
        var definitions = _services.Settings.Current.Tags.ToDictionary(t => t.Id);
        foreach (var item in items)
        {
            var ids = _services.Tags.GetTags(item.FullPath);
            item.Tags = ids.Count == 0 ? [] : [.. ids.Where(definitions.ContainsKey).Select(id => new TagChip(id, definitions[id].Name, definitions[id].Color))];
        }
    }

    private void OnSelectionChanged()
    {
        UpdateStatus();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseItemCommandState();
    }

    public event EventHandler? SelectionChanged;

    private void UpdateStatus()
    {
        var localizer = _services.Localizer;
        StatusText = IsHome ? string.Empty : localizer.Format(_ordered.Count == 1 ? "Status.OneItem" : "Status.Items", _ordered.Count);

        var selected = Selection.SelectedItems;
        if (selected.Count == 0)
        {
            SelectionText = string.Empty;
            return;
        }

        var files = selected.Where(i => !i.IsDirectory).ToList();
        var size = files.Sum(i => Math.Max(0, i.Entry.Length));
        SelectionText = files.Count > 0
            ? localizer.Format("Status.SelectedWithSize", selected.Count, ByteSize.Format(size, localizer.Culture))
            : localizer.Format("Status.Selected", selected.Count);
    }

    /// <summary>Incremental update from the file system monitor (UI thread).</summary>
    internal void ApplyChanges(DirectoryChangeBatch batch)
    {
        if (_disposed || !IsFolder || !PathUtil.AreEqual(batch.DirectoryPath, _location.Path))
            return;

        if (batch.DirectoryRemoved)
        {
            var parent = Path.GetDirectoryName(batch.DirectoryPath);
            while (parent is not null && !Directory.Exists(parent))
                parent = Path.GetDirectoryName(parent);
            _services.Notifications.Show(new Services.InAppNotification(_services.Localizer["Notify.FolderRemovedTitle"], batch.DirectoryPath, Services.NotificationSeverity.Warning));
            Navigate(parent is null ? Location.Home : Location.Folder(parent));
            return;
        }

        if (batch.ResetRequired)
        {
            Refresh();
            return;
        }

        var filter = CurrentFilter();
        var removed = new List<string>();
        var added = new List<FileEntry>();
        foreach (var name in batch.Deleted.Concat(batch.Created).Concat(batch.Changed).Distinct(StringComparer.Ordinal))
        {
            var path = Path.Combine(batch.DirectoryPath, name);
            if (_entries.ContainsKey(path))
                removed.Add(path);
            if (TryReadEntry(path, filter) is { } entry)
                added.Add(entry);
        }

        if (removed.Count == 0 && added.Count == 0)
            return;

        if (!TryApplyIncrementally(removed, added))
        {
            foreach (var path in removed)
                RemoveEntry(path);
            foreach (var entry in added)
                _entries[entry.FullPath] = entry;
            _ = ResortAsync();
        }
    }

    /// <summary>
    /// Small change bursts in an ungrouped list/details view are applied as individual inserts/removals at their
    /// sorted positions (binary search) instead of re-sorting and resetting the whole list.
    /// </summary>
    private bool TryApplyIncrementally(List<string> removed, List<FileEntry> added)
    {
        if (_groupBy != GroupBy.None || IsTileLayout || _filterText.Length > 0 || removed.Count + added.Count > 64 || !ReferenceEquals(_ordered, _sortedAll))
            return false;

        var comparer = new ItemComparer(new FileEntryComparer(_sort));
        var keep = Selection.SelectedItems.Select(i => i.FullPath).ToList();
        foreach (var path in removed)
        {
            if (!_itemsByPath.TryGetValue(path, out var model))
                continue;
            var index = _ordered.BinarySearch(model, comparer);
            if (index < 0 || !ReferenceEquals(_ordered[index], model))
                index = _ordered.IndexOf(model);
            if (index >= 0)
            {
                _ordered.RemoveAt(index);
                Rows.RemoveAt(index);
            }

            RemoveEntry(path);
        }

        foreach (var entry in added)
        {
            _entries[entry.FullPath] = entry;
            var model = new FileItemViewModel(entry, _displayContext);
            _itemsByPath[entry.FullPath] = model;
            var index = _ordered.BinarySearch(model, comparer);
            if (index < 0)
                index = ~index;
            _ordered.Insert(index, model);
            Rows.Insert(index, model);
        }

        if (_services.Settings.Current.FilesAndFolders.ShowFileTagsColumn)
            ApplyTags(added.Select(e => _itemsByPath[e.FullPath]));
        Selection.SetItems(_ordered, keep);
        UpdateStatus();
        OnPropertyChanged(nameof(IsEmpty));
        FlushPendingSelection();
        return true;
    }

    private sealed class ItemComparer(FileEntryComparer inner) : IComparer<FileItemViewModel>
    {
        public int Compare(FileItemViewModel? x, FileItemViewModel? y) => inner.Compare(x?.Entry, y?.Entry);
    }

    private bool RemoveEntry(string path)
    {
        if (!_entries.Remove(path))
            return false;
        if (_itemsByPath.Remove(path, out var model))
            model.CancelThumbnail();
        return true;
    }

    private static FileEntry? TryReadEntry(string path, EnumerationFilter filter)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null)
                return null;
            var entry = FileEntry.FromInfo(info);
            if (entry.Name.Contains(".navigator-partial", StringComparison.Ordinal))
                return null; // our own in-flight copy
            if (entry.IsDotFile && !filter.IncludeDotFiles && !filter.IncludeHidden)
                return null;
            if (entry.IsSystem && !filter.IncludeSystem)
                return null;
            if (entry.IsHidden && !entry.IsDotFile && !filter.IncludeHidden)
                return null;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ScheduleFolderSizes(CancellationToken token)
    {
        if (!_services.Settings.Current.FilesAndFolders.CalculateFolderSizes)
            return;

        foreach (var folder in _ordered.Where(i => i.IsDirectory).Take(1000).ToList())
        {
            _services.Scheduler.Schedule(WorkPriority.Background, ct =>
            {
                var statistics = FolderStatisticsCalculator.Calculate(folder.FullPath, cancellationToken: ct);
                _services.Dispatcher.Post(() => folder.SetFolderSize(statistics.TotalBytes));
                return ValueTask.CompletedTask;
            }, token);
        }
    }

    private void LoadViewPreferences()
    {
        // Stepping through the column layout keeps it (and its sort): the per-folder views apply to folders opened anew.
        if (ContinuesColumns())
            return;

        var files = _services.Settings.Current.FilesAndFolders;
        if (files.RememberViewPerFolder && _location.Path is { } path && _location.IsFolder
            && files.FolderViews.TryGetValue(path, out var preference))
        {
            _layout = preference.Layout;
            _sort = new SortOptions(preference.SortColumn, preference.SortDirection, files.ListFoldersFirst);
            _groupBy = preference.GroupBy;
        }
        else
        {
            _layout = files.DefaultLayout;
            _sort = new SortOptions(files.DefaultSortColumn, files.DefaultSortDirection, files.ListFoldersFirst);
            _groupBy = IsTrash ? GroupBy.None : files.DefaultGroupBy;
        }

        UpdateTileColumns();
        OnPropertyChanged(nameof(Layout));
        OnPropertyChanged(nameof(IsDetailsLayout));
        OnPropertyChanged(nameof(IsListLayout));
        OnPropertyChanged(nameof(IsTileLayout));
        OnPropertyChanged(nameof(IsColumnsLayout));
        OnPropertyChanged(nameof(TileWidth));
        OnPropertyChanged(nameof(TileIconSize));
        OnPropertyChanged(nameof(Sort));
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(IsSortDescending));
        OnPropertyChanged(nameof(GroupBy));
    }

    private void SaveViewPreferences()
    {
        var files = _services.Settings.Current.FilesAndFolders;
        if (!files.RememberViewPerFolder || !_location.IsFolder || _location.Path is not { } path)
            return;

        _services.Settings.Update("folderViews", s =>
        {
            var views = s.FilesAndFolders.FolderViews;
            views[path] = new FolderViewPreference
            {
                Layout = _layout,
                SortColumn = _sort.Column,
                SortDirection = _sort.Direction,
                GroupBy = _groupBy,
                LastUsed = DateTimeOffset.Now,
            };

            // Bounded: forget the least recently used folders.
            if (views.Count > MaxRememberedFolderViews)
            {
                foreach (var old in views.OrderBy(kv => kv.Value.LastUsed).Take(views.Count - MaxRememberedFolderViews).Select(kv => kv.Key).ToList())
                    views.Remove(old);
            }
        });
    }
}
