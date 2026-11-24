using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.Core.Operations;
using Navigator.Core.Platform;

namespace Navigator.Presentation.Services;

/// <summary>
/// Empty or full state of the Recycle Bin / Trash, exposed as the system icon to draw for it (shared by the sidebar and tabs).
/// Nothing runs until <see cref="SystemIconKey"/> is first read, so a hidden trash entry costs nothing. After that the state
/// is re-checked off the UI thread when the trash reports a change or a recycle/restore operation finishes; bursts
/// (one recycle produces several file events) are coalesced into a single cheap check.
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
    private bool _hasItems;
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
            return _hasItems ? FullIconKey : EmptyIconKey;
        }
    }

    /// <summary>Schedules a re-check (coalesced); safe from any thread.</summary>
    public void Invalidate()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshPending, 1) == 1)
            return;
        _ = RefreshAsync();
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

    private async Task RefreshAsync()
    {
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
