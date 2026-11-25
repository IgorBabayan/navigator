using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.Core.Operations;
using Navigator.Core.Platform;

namespace Navigator.Presentation.Services;

/// <summary>
/// Empty or full state of the Recycle Bin / Trash, exposed as the system icon to draw for it (shared by the sidebar and tabs).
/// Nothing runs until <see cref="SystemIconKey"/> is first read, so a hidden trash entry costs nothing. After that the state
/// is re-checked off the UI thread when the trash reports a change or a recycle/restore operation finishes; bursts
/// (one recycle produces several file events) are coalesced into a single cheap check.
/// <see cref="HasItems"/> exposes the same state for commands that only make sense on a non-empty trash ("Empty trash").
/// </summary>
public sealed class TrashStatus : ObservableObject, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly string EmptyIconKey = Core.Platform.SystemIconKey.Stock(SystemIconKind.RecycleBin);
    private static readonly string FullIconKey = Core.Platform.SystemIconKey.Stock(SystemIconKind.RecycleBinFull);

    private readonly ITrashService _trash;
    private readonly FileOperationQueue _operations;
    private readonly IUiDispatcher _dispatcher;
    private bool _started;
    private bool? _hasItems;
    private int _refreshPending;
    private volatile bool _disposed;

    public TrashStatus(ITrashService trash, FileOperationService operations, IUiDispatcher dispatcher)
    {
        _trash = trash;
        _operations = operations.Queue;
        _dispatcher = dispatcher;
    }

    /// <summary>A <see cref="Core.Platform.SystemIconKey"/>: the empty or the full bin. UI thread only.</summary>
    public string SystemIconKey
    {
        get
        {
            EnsureStarted();
            return _hasItems == true ? FullIconKey : EmptyIconKey;
        }
    }

    /// <summary>
    /// True or false once the trash was checked; null while unknown (not checked yet, or the platform cannot tell cheaply).
    /// Callers treat null as "may have items". UI thread only.
    /// </summary>
    public bool? HasItems
    {
        get
        {
            EnsureStarted();
            return _hasItems;
        }
    }

    /// <summary>
    /// Schedules a re-check (coalesced); safe from any thread. <paramref name="immediate"/> skips the debounce, for a
    /// user-visible question such as a context menu that is opening (a check already pending is not duplicated).
    /// </summary>
    public void Invalidate(bool immediate = false)
    {
        if (_disposed || !_trash.IsSupported || Interlocked.Exchange(ref _refreshPending, 1) == 1)
            return;
        _ = RefreshAsync(immediate);
    }

    private void EnsureStarted()
    {
        if (_started || _disposed || !_trash.IsSupported)
            return;
        _started = true;
        _trash.Changed += OnTrashChanged;
        _operations.OperationFinished += OnOperationFinished;
        Invalidate();
    }

    private async Task RefreshAsync(bool immediate)
    {
        // HasItemsAsync answers off the caller's thread, so an immediate check never blocks the UI thread.
        if (!immediate)
            await Task.Delay(Debounce).ConfigureAwait(false);
        Volatile.Write(ref _refreshPending, 0);

        bool? hasItems;
        try
        {
            hasItems = await _trash.HasItemsAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Error boundary: an unreadable trash keeps the icon it has.
            return;
        }

        if (hasItems is not { } value || _disposed)
            return;

        _dispatcher.Post(() =>
        {
            if (_hasItems == value || _disposed)
                return;
            _hasItems = value;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(SystemIconKey));
        });
    }

    private void OnTrashChanged(object? sender, EventArgs e) => Invalidate();

    private void OnOperationFinished(object? sender, FileOperation operation)
    {
        if (operation.Kind is FileOperationKind.Recycle or FileOperationKind.Restore)
            Invalidate();
    }

    public void Dispose()
    {
        _disposed = true;
        if (!_started)
            return;
        _trash.Changed -= OnTrashChanged;
        _operations.OperationFinished -= OnOperationFinished;
    }
}
