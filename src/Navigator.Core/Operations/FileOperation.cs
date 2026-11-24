namespace Navigator.Core.Operations;

/// <summary>
/// Handle for one queued or running operation. Thread-safe. Progress is read by polling
/// <see cref="GetProgress"/> (the UI polls ~10x per second only while operations are active),
/// state transitions are pushed through <see cref="StateChanged"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The token source is disposed when the operation finishes (Finish); handles outlive the run only as read-only history.")]
public sealed class FileOperation
{
    private const int SpeedWindowMilliseconds = 3000;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<FileOperationError> _errors = [];
    private readonly List<TransferredItem> _transferred = [];
    private readonly List<Platform.TrashItem> _trashed = [];
    private readonly Queue<(long Tick, long Bytes)> _samples = new();
    private long _totalBytes;
    private long _processedBytes;
    private int _totalItems;
    private int _processedItems;
    private string? _currentItem;
    private FileOperationState _state = FileOperationState.Queued;

    internal FileOperation(FileOperationKind kind, IReadOnlyList<string> sources, string? destination, IFileOperationInteraction interaction)
    {
        Kind = kind;
        Sources = sources;
        Destination = destination;
        Interaction = interaction;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public FileOperationKind Kind { get; }

    public IReadOnlyList<string> Sources { get; }

    public string? Destination { get; }

    public DateTimeOffset QueuedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    internal IFileOperationInteraction Interaction { get; }

    internal PauseGate PauseGate { get; } = new();

    internal CancellationToken CancellationToken => _cancellation.Token;

    public FileOperationState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public bool IsFinished => State is FileOperationState.Completed or FileOperationState.CompletedWithErrors
        or FileOperationState.Cancelled or FileOperationState.Failed;

    public bool CanPause => Kind is FileOperationKind.Copy or FileOperationKind.Move or FileOperationKind.CreateArchive or FileOperationKind.ExtractArchive;

    /// <summary>Completes when the operation reaches a final state. Never faults.</summary>
    public Task Completion => _completion.Task;

    public Exception? Failure { get; private set; }

    public IReadOnlyList<FileOperationError> Errors
    {
        get
        {
            lock (_gate)
                return [.. _errors];
        }
    }

    /// <summary>Items that were created/moved, in order. Used for undo, tag updates and selection after paste.</summary>
    public IReadOnlyList<TransferredItem> TransferredItems
    {
        get
        {
            lock (_gate)
                return [.. _transferred];
        }
    }

    /// <summary>Trash entries created by a Recycle operation (used to undo it).</summary>
    public IReadOnlyList<Platform.TrashItem> TrashedItems
    {
        get
        {
            lock (_gate)
                return [.. _trashed];
        }
    }

    public event EventHandler? StateChanged;

    public void Cancel()
    {
        PauseGate.Resume();
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Pause()
    {
        if (!CanPause)
            return;

        lock (_gate)
        {
            if (_state != FileOperationState.Running)
                return;
            _state = FileOperationState.Paused;
        }

        PauseGate.Pause();
        RaiseStateChanged();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != FileOperationState.Paused)
                return;
            _state = FileOperationState.Running;
        }

        PauseGate.Resume();
        RaiseStateChanged();
    }

    public ProgressSnapshot GetProgress()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var processed = Interlocked.Read(ref _processedBytes);
            _samples.Enqueue((now, processed));
            while (_samples.Count > 2 && now - _samples.Peek().Tick > SpeedWindowMilliseconds)
                _samples.Dequeue();

            double speed = 0;
            var (firstTick, firstBytes) = _samples.Peek();
            if (now - firstTick >= 250)
                speed = (processed - firstBytes) * 1000.0 / (now - firstTick);

            var total = Interlocked.Read(ref _totalBytes);
            TimeSpan? remaining = speed > 1 && total > processed && _state == FileOperationState.Running
                ? TimeSpan.FromSeconds((total - processed) / speed)
                : null;

            return new ProgressSnapshot(total, processed, _totalItems, _processedItems, _currentItem, speed, remaining);
        }
    }

    internal void AddTotals(long bytes, int items)
    {
        Interlocked.Add(ref _totalBytes, bytes);
        Interlocked.Add(ref _totalItems, items);
    }

    internal void AddProcessedBytes(long bytes) => Interlocked.Add(ref _processedBytes, bytes);

    internal void CompleteItem() => Interlocked.Increment(ref _processedItems);

    internal void SetCurrentItem(string? path)
    {
        lock (_gate)
            _currentItem = path;
    }

    internal void AddError(FileOperationError error)
    {
        lock (_gate)
            _errors.Add(error);
    }

    internal void AddTransferred(TransferredItem item)
    {
        lock (_gate)
            _transferred.Add(item);
    }

    internal void AddTrashed(Platform.TrashItem item)
    {
        lock (_gate)
            _trashed.Add(item);
    }

    internal void SetState(FileOperationState state)
    {
        lock (_gate)
        {
            if (_state == state)
                return;
            _state = state;
            if (state is FileOperationState.Preparing or FileOperationState.Running && StartedAt is null)
                StartedAt = DateTimeOffset.Now;
        }

        RaiseStateChanged();
    }

    internal void Finish(FileOperationState state, Exception? failure = null)
    {
        lock (_gate)
        {
            _state = state;
            _currentItem = null;
            FinishedAt = DateTimeOffset.Now;
            Failure = failure;
        }

        _cancellation.Dispose();
        RaiseStateChanged();
        _completion.TrySetResult();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
