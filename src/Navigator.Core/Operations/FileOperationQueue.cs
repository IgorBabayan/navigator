namespace Navigator.Core.Operations;

/// <summary>
/// Runs file operations in the background with a configurable concurrency limit.
/// Operations never run on the UI thread; finished operations are kept in a bounded history.
/// </summary>
public sealed class FileOperationQueue : IAsyncDisposable
{
    public const int HistoryCapacity = 64;

    private readonly Lock _gate = new();
    private readonly Queue<(FileOperation Operation, Func<OperationContext, Task> Body)> _pending = new();
    private readonly List<FileOperation> _active = [];
    private readonly LinkedList<FileOperation> _history = new();
    private readonly List<Task> _running = [];
    private readonly FileOperationOptions _options;
    private int _maxConcurrency;
    private bool _disposed;

    public FileOperationQueue(int maxConcurrency = 2, FileOperationOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _maxConcurrency = maxConcurrency;
        _options = options ?? new FileOperationOptions();
    }

    public FileOperationOptions Options => _options;

    /// <summary>Raised (on a worker thread) when an operation is queued.</summary>
    public event EventHandler<FileOperation>? OperationQueued;

    /// <summary>Raised (on a worker thread) when an operation reaches a final state.</summary>
    public event EventHandler<FileOperation>? OperationFinished;

    public int MaxConcurrency
    {
        get
        {
            lock (_gate)
                return _maxConcurrency;
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_gate)
                _maxConcurrency = value;
            Pump();
        }
    }

    /// <summary>True while anything is queued or running. The updater refuses to restart while this is true.</summary>
    public bool HasActiveOperations
    {
        get
        {
            lock (_gate)
                return _pending.Count > 0 || _active.Count > 0;
        }
    }

    public IReadOnlyList<FileOperation> ActiveOperations
    {
        get
        {
            lock (_gate)
                return [.. _active, .. _pending.Select(p => p.Operation)];
        }
    }

    public IReadOnlyList<FileOperation> History
    {
        get
        {
            lock (_gate)
                return [.. _history];
        }
    }

    public FileOperation Enqueue(
        FileOperationKind kind,
        IReadOnlyList<string> sources,
        string? destination,
        IFileOperationInteraction interaction,
        Func<OperationContext, Task> body)
    {
        var operation = new FileOperation(kind, sources, destination, interaction);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Enqueue((operation, body));
        }

        OperationQueued?.Invoke(this, operation);
        Pump();
        return operation;
    }

    public void CancelAll()
    {
        foreach (var operation in ActiveOperations)
            operation.Cancel();
    }

    public void ClearHistory()
    {
        lock (_gate)
            _history.Clear();
    }

    private void Pump()
    {
        lock (_gate)
        {
            while (!_disposed && _active.Count < _maxConcurrency && _pending.Count > 0)
            {
                var (operation, body) = _pending.Dequeue();
                _active.Add(operation);
                var task = Task.Run(() => RunAsync(operation, body));
                _running.Add(task);
            }

            _running.RemoveAll(t => t.IsCompleted);
        }
    }

    private async Task RunAsync(FileOperation operation, Func<OperationContext, Task> body)
    {
        var context = new OperationContext(operation, _options);
        FileOperationState final;
        Exception? failure = null;
        try
        {
            operation.CancellationToken.ThrowIfCancellationRequested();

            operation.SetState(FileOperationState.Preparing);
            await body(context).ConfigureAwait(false);
            final = operation.Errors.Count > 0 ? FileOperationState.CompletedWithErrors : FileOperationState.Completed;
        }
        catch (OperationCanceledException)
        {
            final = FileOperationState.Cancelled;
        }
        catch (Exception ex)
        {
            // Error boundary: an unexpected failure ends this operation only, never the process.
            final = FileOperationState.Failed;
            failure = ex;
        }

        lock (_gate)
        {
            _active.Remove(operation);
            _history.AddFirst(operation);
            while (_history.Count > HistoryCapacity)
                _history.RemoveLast();
        }

        operation.Finish(final, failure);
        OperationFinished?.Invoke(this, operation);
        Pump();
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_pending.TryDequeue(out var pending))
                pending.Operation.Finish(FileOperationState.Cancelled);
            foreach (var operation in _active)
                operation.Cancel();
            running = [.. _running];
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }
}
