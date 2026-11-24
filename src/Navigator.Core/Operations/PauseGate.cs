namespace Navigator.Core.Operations;

/// <summary>Cooperative pause: workers await <see cref="WaitIfPausedAsync"/> between chunks.</summary>
public sealed class PauseGate
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _resumed;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _resumed is not null;
        }
    }

    public void Pause()
    {
        lock (_gate)
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? toRelease;
        lock (_gate)
        {
            toRelease = _resumed;
            _resumed = null;
        }

        toRelease?.TrySetResult();
    }

    public ValueTask WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_gate)
            wait = _resumed?.Task;

        return wait is null ? ValueTask.CompletedTask : new ValueTask(wait.WaitAsync(cancellationToken));
    }
}
