using System.Threading.Channels;

namespace Navigator.Core.Scheduling;

/// <summary>Lower value = runs first.</summary>
public enum WorkPriority
{
    /// <summary>Data for items currently on screen (icons, thumbnails of visible rows).</summary>
    Visible = 0,

    /// <summary>Data the user will probably need soon (preview of selection, folder size in details pane).</summary>
    Normal = 1,

    /// <summary>Opportunistic work (cache maintenance, prefetch, off-screen thumbnails).</summary>
    Background = 2,
}

/// <summary>
/// Small fixed pool of workers that always drains higher priorities first. Keeps thumbnail generation,
/// metadata extraction and cache maintenance from competing with each other or flooding the thread pool.
/// User-initiated file operations do not use this pool (they have <see cref="Operations.FileOperationQueue"/>).
/// </summary>
public sealed class BackgroundWorkScheduler : IAsyncDisposable
{
    private readonly Channel<WorkItem>[] _queues;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;

    public BackgroundWorkScheduler(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        _queues = [.. Enumerable.Range(0, 3).Select(_ => Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = false }))];
        _workers = [.. Enumerable.Range(0, workerCount).Select(_ => Task.Run(WorkerLoopAsync))];
    }

    public event EventHandler<Exception>? WorkFailed;

    /// <summary>Queues work. Items whose token is already cancelled when dequeued are dropped without running.</summary>
    public void Schedule(WorkPriority priority, Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default)
    {
        if (_shutdown.IsCancellationRequested)
            return;
        _queues[(int)priority].Writer.TryWrite(new WorkItem(work, cancellationToken));
        _signal.Release();
    }

    public Task<T> ScheduleAsync<T>(WorkPriority priority, Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Schedule(priority, async token =>
        {
            try
            {
                completion.TrySetResult(await work(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, cancellationToken);

        cancellationToken.Register(static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(), completion);
        return completion.Task;
    }

    private async Task WorkerLoopAsync()
    {
        var shutdown = _shutdown.Token;
        try
        {
            while (true)
            {
                await _signal.WaitAsync(shutdown).ConfigureAwait(false);
                if (!TryDequeue(out var item))
                    continue;
                if (item.CancellationToken.IsCancellationRequested)
                    continue;

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, item.CancellationToken);
                    await item.Work(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    WorkFailed?.Invoke(this, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool TryDequeue(out WorkItem item)
    {
        foreach (var queue in _queues)
        {
            if (queue.Reader.TryRead(out item))
                return true;
        }

        item = default;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _shutdown.Dispose();
        _signal.Dispose();
    }

    private readonly record struct WorkItem(Func<CancellationToken, ValueTask> Work, CancellationToken CancellationToken);
}
