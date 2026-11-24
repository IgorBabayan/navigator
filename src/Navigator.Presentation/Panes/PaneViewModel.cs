using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.IO;
using Navigator.Core.Monitoring;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Core.Search;
using Navigator.Core.Settings;
using Navigator.Core.Sorting;
using Navigator.Presentation.Collections;
using Navigator.Presentation.Items;
using Navigator.Presentation.Services;
using Navigator.Presentation.Shell;

namespace Navigator.Presentation.Panes;

public sealed class BreadcrumbViewModel(string name, string path, bool isLast)
{
    public string Name { get; } = name;

    public string Path { get; } = path;

    public bool IsLast { get; } = isLast;
}

/// <summary>
/// One file browsing pane (a tab has one, or two in split view). Owns navigation history, the incremental
/// listing, sorting/grouping/filtering, selection, file system monitoring and all item commands.
/// </summary>
public sealed partial class PaneViewModel : ObservableObject, IDisposable
{
    private const int PublishIntervalMilliseconds = 250;

    private readonly PaneServices _services;
    private readonly PinnedFolders _pins;
    private readonly ILogger _logger;
    private readonly Dictionary<string, FileEntry> _entries = new(PathUtil.Comparer);
    private readonly Dictionary<string, FileItemViewModel> _itemsByPath = new(PathUtil.Comparer);
    private List<FileItemViewModel> _ordered = [];
    private CancellationTokenSource? _load;
    private DirectoryMonitor? _monitor;
    private ItemDisplayContext _displayContext;
    private Location _location;
    private bool _isLoading;
    private string? _errorText;
    private string _addressText = string.Empty;
    private bool _isEditingAddress;
    private string _filterText = string.Empty;
    private FolderLayout _layout;
    private SortOptions _sort;
    private GroupBy _groupBy;
    private double _viewportWidth = 800;
    private int _tileColumns = 1;
    private string _statusText = string.Empty;
    private string _selectionText = string.Empty;
    private string _searchText = string.Empty;
    private bool _isActive;
    private long _lastLoadMilliseconds;
    private bool _disposed;

    public PaneViewModel(PaneServices services, PinnedFolders pins, Location initial)
    {
        _services = services;
        _pins = pins;
        _logger = services.LoggerFactory.CreateLogger<PaneViewModel>();
        _location = initial;
        History = new NavigationHistory(initial);
        _displayContext = CreateDisplayContext();
        var files = services.Settings.Current.FilesAndFolders;
        _layout = files.DefaultLayout;
        _sort = new SortOptions(files.DefaultSortColumn, files.DefaultSortDirection, files.ListFoldersFirst);
        _groupBy = files.DefaultGroupBy;

        Selection.Changed += (_, _) => OnSelectionChanged();
        Home = new HomeViewModel(services, pins, (location, newTab) =>
        {
            if (newTab)
                OpenInNewTabRequested?.Invoke(this, location);
            else
                Navigate(location);
        });

        GoBackCommand = new RelayCommand(() => GoBack(), () => History.CanGoBack);
        GoForwardCommand = new RelayCommand(() => GoForward(), () => History.CanGoForward);
        GoUpCommand = new RelayCommand(GoUp, () => ParentPath is not null);
        RefreshCommand = new RelayCommand(Refresh);
        NavigateToAddressCommand = new AsyncRelayCommand(NavigateToAddressAsync);
        NavigateToBreadcrumbCommand = new RelayCommand<BreadcrumbViewModel>(NavigateToBreadcrumb);
        SetLayoutCommand = new RelayCommand<FolderLayout>(l => Layout = l);
        SortByCommand = new RelayCommand<SortColumn>(SortBy);
        SetGroupByCommand = new RelayCommand<GroupBy>(g => GroupBy = g);
        ToggleSortDirectionCommand = new RelayCommand(() => Sort = Sort with { Direction = Sort.Direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending });
        ToggleFoldersFirstCommand = new RelayCommand(() => Sort = Sort with { FoldersFirst = !Sort.FoldersFirst });
        SearchCommand = new RelayCommand(StartSearch, () => SearchRoot is not null);
        InitializeItemCommands();
        History.Changed += (_, _) => RaiseNavigationState();
    }

    public NavigationHistory History { get; }

    public ItemSelection Selection { get; } = new();

    public HomeViewModel Home { get; }

    /// <summary>Rows bound to the virtualized list: FileItemViewModel, GroupHeaderViewModel or TileRowViewModel.</summary>
    public RangeObservableCollection<object> Rows { get; } = [];

    public ObservableCollection<BreadcrumbViewModel> Breadcrumbs { get; } = [];

    /// <summary>Items in display order (what selection and keyboard navigation operate on).</summary>
    public IReadOnlyList<FileItemViewModel> Items => _ordered;

    public Location Location
    {
        get => _location;
        private set
        {
            if (SetProperty(ref _location, value))
            {
                OnPropertyChanged(nameof(IsHome));
                OnPropertyChanged(nameof(IsTrash));
                OnPropertyChanged(nameof(IsSearch));
                OnPropertyChanged(nameof(IsFolder));
                OnPropertyChanged(nameof(IsBrowsingList));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(CurrentDirectory));
                OnPropertyChanged(nameof(CanCreateItems));
            }
        }
    }

    public bool IsHome => _location.Kind == LocationKind.Home;

    public bool IsTrash => _location.Kind == LocationKind.Trash;

    public bool IsSearch => _location.Kind == LocationKind.Search;

    public bool IsFolder => _location.Kind == LocationKind.Folder;

    public bool IsBrowsingList => !IsHome;

    /// <summary>Folder in which new items are created and pasted (null for Home/Trash/Tag views).</summary>
    public string? CurrentDirectory => IsFolder ? _location.Path : null;

    public bool CanCreateItems => IsFolder;

    public string? ParentPath => IsFolder && _location.Path is { } path ? Path.GetDirectoryName(path) : IsSearch ? _location.Path : null;

    public string Title => _location.Kind switch
    {
        LocationKind.Home => _services.Localizer["Location.Home"],
        LocationKind.Trash => _services.Localizer[OperatingSystem.IsWindows() ? "Location.RecycleBin" : "Location.Trash"],
        LocationKind.Search => _services.Localizer.Format("Location.SearchResults", _location.Query),
        LocationKind.Tag => _services.Settings.Current.Tags.FirstOrDefault(t => t.Id == _location.Query)?.Name ?? _services.Localizer["Location.Tag"],
        _ => FolderDisplayName(_location.Path!),
    };

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _errorText is not null;

    public bool IsEmpty => !IsLoading && !HasError && !IsHome && _ordered.Count == 0;

    public string EmptyText => IsSearch ? _services.Localizer["Pane.NoSearchResults"] : _filterText.Length > 0 ? _services.Localizer["Pane.NoFilterMatches"] : _services.Localizer["Pane.EmptyFolder"];

    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public bool IsEditingAddress
    {
        get => _isEditingAddress;
        set
        {
            if (SetProperty(ref _isEditingAddress, value) && value)
                AddressText = _location.Path ?? string.Empty;
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                ApplyView();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    /// <summary>Last directory listing time in milliseconds (performance overlay).</summary>
    public long LastLoadMilliseconds
    {
        get => _lastLoadMilliseconds;
        private set => SetProperty(ref _lastLoadMilliseconds, value);
    }

    public event EventHandler<Location>? OpenInNewTabRequested;

    public event EventHandler? Navigated;

    public IRelayCommand GoBackCommand { get; }

    public IRelayCommand GoForwardCommand { get; }

    public IRelayCommand GoUpCommand { get; }

    public IRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand NavigateToAddressCommand { get; }

    public IRelayCommand<BreadcrumbViewModel> NavigateToBreadcrumbCommand { get; }

    public IRelayCommand<FolderLayout> SetLayoutCommand { get; }

    public IRelayCommand<SortColumn> SortByCommand { get; }

    public IRelayCommand<GroupBy> SetGroupByCommand { get; }

    public IRelayCommand ToggleSortDirectionCommand { get; }

    public IRelayCommand ToggleFoldersFirstCommand { get; }

    public IRelayCommand SearchCommand { get; }

    private string? SearchRoot => _location.Kind switch
    {
        LocationKind.Folder => _location.Path,
        LocationKind.Search => _location.Path,
        LocationKind.Home => _services.Platform.HomeDirectory,
        _ => null,
    };

    /// <summary>Starts (or restarts) the pane after creation or session restore.</summary>
    public void Start() => ApplyLocation();

    public void Navigate(Location location)
    {
        if (location.IsFolder && _services.Settings.Current.General.OpenFoldersInNewTab && !IsHome && !location.Equals(_location))
        {
            OpenInNewTabRequested?.Invoke(this, location);
            return;
        }

        if (!History.Navigate(location))
        {
            Refresh();
            return;
        }

        ApplyLocation();
    }

    public bool GoBack(int steps = 1)
    {
        if (History.GoBack(steps) is null)
            return false;
        ApplyLocation();
        return true;
    }

    public bool GoForward(int steps = 1)
    {
        if (History.GoForward(steps) is null)
            return false;
        ApplyLocation();
        return true;
    }

    public void GoUp()
    {
        if (ParentPath is { } parent)
        {
            var child = _location.Path;
            Navigate(Location.Folder(parent));
            if (child is not null)
                _reselectAfterLoad = [child];
        }
    }

    public void Refresh()
    {
        var keep = Selection.SelectedItems.Select(i => i.FullPath).ToList();
        _reselectAfterLoad = keep;
        ApplyLocation();
    }

    private List<string>? _reselectAfterLoad;

    private void ApplyLocation()
    {
        CancelLoad();
        StopMonitoring();
        Location = History.Current;
        IsEditingAddress = false;
        ErrorText = null;
        FilterText = string.Empty;
        _entries.Clear();
        _itemsByPath.Clear();
        _ordered = [];
        Selection.SetItems([]);
        Rows.ReplaceAll([]);
        UpdateBreadcrumbs();
        LoadViewPreferences();
        RaiseNavigationState();
        Navigated?.Invoke(this, EventArgs.Empty);

        var load = new CancellationTokenSource();
        _load = load;
        _ = RunLoadAsync(load.Token);
    }

    private async Task RunLoadAsync(CancellationToken token)
    {
        IsLoading = true;
        OnPropertyChanged(nameof(IsEmpty));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            switch (_location.Kind)
            {
                case LocationKind.Home:
                    await Home.LoadAsync(token).ConfigureAwait(true);
                    break;
                case LocationKind.Folder:
                    await LoadFolderAsync(_location.Path!, token).ConfigureAwait(true);
                    break;
                case LocationKind.Trash:
                    await LoadTrashAsync(token).ConfigureAwait(true);
                    break;
                case LocationKind.Search:
                    await RunSearchAsync(token).ConfigureAwait(true);
                    break;
                case LocationKind.Tag:
                    await LoadTagAsync(_location.Query!, token).ConfigureAwait(true);
                    break;
            }

            LastLoadMilliseconds = stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (DirectoryEnumerationException ex)
        {
            ErrorText = ex.Kind switch
            {
                EnumerationErrorKind.AccessDenied => _services.Localizer["Error.AccessDenied"],
                EnumerationErrorKind.NotFound => _services.Localizer["Error.FolderNotFound"],
                EnumerationErrorKind.DeviceNotReady => _services.Localizer["Error.DeviceNotReady"],
                EnumerationErrorKind.NotADirectory => _services.Localizer["Error.NotAFolder"],
                _ => ex.InnerException?.Message ?? ex.Message,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Loading a location failed");
            ErrorText = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    private async Task LoadFolderAsync(string path, CancellationToken token)
    {
        _displayContext = CreateDisplayContext();
        var filter = CurrentFilter();
        var lastPublish = 0L;
        var first = true;

        await foreach (var batch in DirectoryEnumerator.EnumerateBatchesAsync(path, filter, token).ConfigureAwait(true))
        {
            foreach (var entry in batch)
                _entries[entry.FullPath] = entry;
            var now = Environment.TickCount64;
            if (first || now - lastPublish >= PublishIntervalMilliseconds)
            {
                await PublishAsync(token).ConfigureAwait(true);
                first = false;
                lastPublish = Environment.TickCount64;
            }
        }

        await PublishAsync(token).ConfigureAwait(true);
        StartMonitoring(path);
        ScheduleFolderSizes(token);
    }

    private async Task LoadTrashAsync(CancellationToken token)
    {
        _displayContext = CreateDisplayContext();
        var items = await _services.Trash.ListAsync(token).ConfigureAwait(true);
        var models = new List<FileItemViewModel>(items.Count);
        foreach (var item in items)
        {
            var entry = new FileEntry(item.Name, item.StoragePath, item.IsDirectory, Math.Max(0, item.Length),
                item.DeletedAt?.UtcDateTime ?? DateTime.MinValue, item.DeletedAt?.UtcDateTime ?? DateTime.MinValue, FileAttributes.Normal);
            _entries[entry.FullPath] = entry;
            models.Add(new FileItemViewModel(entry, _displayContext, item));
        }

        foreach (var model in models)
            _itemsByPath[model.FullPath] = model;
        ApplyView();
    }

    private async Task LoadTagAsync(string tagId, CancellationToken token)
    {
        _displayContext = CreateDisplayContext();
        var entries = await Task.Run(() => _services.Tags.FindByTag(tagId)
            .Select(p => Directory.Exists(p) ? FileEntry.FromInfo(new DirectoryInfo(p)) : File.Exists(p) ? FileEntry.FromInfo(new FileInfo(p)) : null)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList(), token).ConfigureAwait(true);
        foreach (var entry in entries)
            _entries[entry.FullPath] = entry;
        await PublishAsync(token).ConfigureAwait(true);
    }

    private async Task RunSearchAsync(CancellationToken token)
    {
        _displayContext = CreateDisplayContext();
        var settings = _services.Settings.Current;
        var text = _location.Query ?? string.Empty;
        var content = settings.Search.SearchFileContents && text.Length >= 3 && !text.Contains('*', StringComparison.Ordinal) ? text : null;
        var query = new SearchQuery
        {
            Root = _location.Path!,
            Text = content is null ? text : string.Empty,
            ContentText = content,
            Recursive = settings.Search.IncludeSubfolders,
            IncludeHidden = settings.FilesAndFolders.ShowHiddenItems,
            MaxResults = settings.Search.MaxResults,
        };

        var lastPublish = 0L;
        await foreach (var batch in FileSearchEngine.SearchAsync(query, token).ConfigureAwait(true))
        {
            foreach (var entry in batch)
                _entries[entry.FullPath] = entry;
            if (Environment.TickCount64 - lastPublish >= PublishIntervalMilliseconds)
            {
                await PublishAsync(token).ConfigureAwait(true);
                lastPublish = Environment.TickCount64;
            }
        }

        await PublishAsync(token).ConfigureAwait(true);
    }

    /// <summary>Sorts a snapshot off the UI thread, then swaps the rows in with one notification.</summary>
    private async Task PublishAsync(CancellationToken token)
    {
        var snapshot = _entries.Values.ToArray();
        var sort = _sort;
        var sorted = await Task.Run(() =>
        {
            var list = new List<FileEntry>(snapshot);
            EntrySorter.Sort(list, sort);
            return list;
        }, token).ConfigureAwait(true);

        token.ThrowIfCancellationRequested();
        ApplySorted(sorted);
    }

    private void StartSearch()
    {
        var text = SearchText.Trim();
        if (text.Length == 0 || SearchRoot is not { } root)
            return;

        var history = _services.Settings.Current.Search;
        if (history.KeepHistory)
        {
            _services.Settings.Update("search", s =>
            {
                s.Search.History.RemoveAll(h => string.Equals(h, text, StringComparison.OrdinalIgnoreCase));
                s.Search.History.Insert(0, text);
            });
        }

        Navigate(Location.Search(root, text));
    }

    private async Task NavigateToAddressAsync()
    {
        var input = AddressText.Trim();
        if (input.Length == 0)
        {
            IsEditingAddress = false;
            return;
        }

        var resolved = PathUtil.ExpandUserInput(input, _services.Platform.HomeDirectory);
        if (resolved is null && !Path.IsPathRooted(input) && CurrentDirectory is { } current)
            resolved = PathUtil.ExpandUserInput(Path.Combine(current, input), _services.Platform.HomeDirectory);

        if (resolved is not null && Directory.Exists(resolved))
        {
            Navigate(Location.Folder(resolved));
            return;
        }

        if (resolved is not null && File.Exists(resolved))
        {
            await _services.Launcher.OpenAsync(resolved).ConfigureAwait(true);
            IsEditingAddress = false;
            return;
        }

        await _services.Dialogs.ShowMessageAsync(_services.Localizer["Error.PathNotFoundTitle"], _services.Localizer.Format("Error.PathNotFound", input)).ConfigureAwait(true);
    }

    /// <summary>
    /// A crumb with a path navigates there. Home, Trash and Tag views have a single path-less crumb: clicking it
    /// switches the omnibar to the editable address box instead of navigating to an empty path.
    /// </summary>
    private void NavigateToBreadcrumb(BreadcrumbViewModel? crumb)
    {
        if (crumb is null)
            return;
        if (string.IsNullOrEmpty(crumb.Path))
            IsEditingAddress = true;
        else
            Navigate(Location.Folder(crumb.Path));
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (_location.Kind is LocationKind.Folder or LocationKind.Search && _location.Path is { } path)
        {
            var segments = PathUtil.GetSegments(path);
            for (var i = 0; i < segments.Count; i++)
            {
                var name = i == 0 && segments[i].Name == "/" ? _services.Localizer["Drive.FileSystem"] : FolderDisplayName(segments[i].FullPath, segments[i].Name);
                Breadcrumbs.Add(new BreadcrumbViewModel(name, segments[i].FullPath, i == segments.Count - 1 && !IsSearch));
            }

            if (IsSearch)
                Breadcrumbs.Add(new BreadcrumbViewModel(Title, path, isLast: true));
        }
        else
        {
            Breadcrumbs.Add(new BreadcrumbViewModel(Title, string.Empty, isLast: true));
        }
    }

    private string FolderDisplayName(string path, string? fallback = null)
    {
        if (_pins.KnownFolderFor(path) is { } known)
            return _services.Localizer["KnownFolder." + known];
        var name = Path.GetFileName(PathUtil.TrimTrailingSeparator(path));
        return string.IsNullOrEmpty(name) ? fallback ?? path : name;
    }

    private EnumerationFilter CurrentFilter()
    {
        var files = _services.Settings.Current.FilesAndFolders;
        return new EnumerationFilter(files.ShowHiddenItems, files.ShowProtectedSystemItems, files.ShowDotFiles || files.ShowHiddenItems);
    }

    private ItemDisplayContext CreateDisplayContext()
        => new(_services.Localizer, _services.Settings.Current.FilesAndFolders.ShowFileExtensions);

    private void RaiseNavigationState()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ParentPath));
    }

    private void CancelLoad()
    {
        var load = _load;
        _load = null;
        if (load is null)
            return;
        load.Cancel();
        load.Dispose();
    }

    private void StartMonitoring(string path)
    {
        StopMonitoring();
        try
        {
            _monitor = new DirectoryMonitor(path, batch => _services.Dispatcher.Post(() => ApplyChanges(batch)));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // e.g. inotify watch limit reached: the folder still works, it just will not auto-refresh.
            _logger.LogInformation(ex, "Change monitoring unavailable for a folder");
        }
    }

    private void StopMonitoring()
    {
        _monitor?.Dispose();
        _monitor = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelLoad();
        StopMonitoring();
        foreach (var item in _ordered)
            item.CancelThumbnail();
    }
}
