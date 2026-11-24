using System.Threading.Channels;

namespace Navigator.Core.Monitoring;

public enum ChangeKind
{
    Created,
    Deleted,
    Changed,
}

/// <summary>
/// Coalesced changes for one folder. Names are relative to the folder. When <see cref="ResetRequired"/>
/// is true the OS dropped events (buffer overflow) and the listing must be reloaded completely.
/// </summary>
public sealed record DirectoryChangeBatch(
    string DirectoryPath,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Changed,
    bool ResetRequired,
    bool DirectoryRemoved);

/// <summary>
/// Watches a single folder (non-recursive) with the OS notification API behind <see cref="FileSystemWatcher"/>
/// (ReadDirectoryChangesW / inotify / FSEvents). Raw events are debounced and coalesced so that a burst of
/// thousands of events becomes a handful of incremental UI updates. Never polls.
/// </summary>
public sealed class DirectoryMonitor : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<RawEvent> _events = Channel.CreateBounded<RawEvent>(new BoundedChannelOptions(8192)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<DirectoryChangeBatch> _onBatch;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maxDelay;
    private readonly Task _pump;
    private int _overflowed;

    public DirectoryMonitor(string directoryPath, Action<DirectoryChangeBatch> onBatch, TimeSpan? quietPeriod = null, TimeSpan? maxDelay = null)
    {
        DirectoryPath = directoryPath;
        _onBatch = onBatch;
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(80);
        _maxDelay = maxDelay ?? TimeSpan.FromMilliseconds(400);

        _watcher = new FileSystemWatcher(directoryPath)
        {
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                           | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime,
        };

        _watcher.Created += (_, e) => Post(ChangeKind.Created, e.Name);
        _watcher.Deleted += (_, e) => Post(ChangeKind.Deleted, e.Name);
        _watcher.Changed += (_, e) => Post(ChangeKind.Changed, e.Name);
        _watcher.Renamed += (_, e) =>
        {
            Post(ChangeKind.Deleted, e.OldName);
            Post(ChangeKind.Created, e.Name);
        };
        _watcher.Error += (_, e) =>
        {
            if (e.GetException() is InternalBufferOverflowException)
                Interlocked.Exchange(ref _overflowed, 1);
            else
                _events.Writer.TryWrite(new RawEvent(ChangeKind.Deleted, null));
            _events.Writer.TryWrite(default);
        };

        _pump = Task.Run(PumpAsync);
        _watcher.EnableRaisingEvents = true;
    }

    public string DirectoryPath { get; }

    private void Post(ChangeKind kind, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        if (!_events.Writer.TryWrite(new RawEvent(kind, name)))
            Interlocked.Exchange(ref _overflowed, 1);
    }

    private async Task PumpAsync()
    {
        var reader = _events.Reader;
        var token = _stop.Token;
        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                var pending = new Dictionary<string, ChangeKind>(StringComparer.Ordinal);
                var watcherFailed = false;
                var started = Environment.TickCount64;

                while (true)
                {
                    while (reader.TryRead(out var raw))
                    {
                        if (raw.Name is null)
                        {
                            watcherFailed |= raw.Kind == ChangeKind.Deleted;
                            continue;
                        }

                        Coalesce(pending, raw.Kind, raw.Name);
                    }

                    var elapsed = Environment.TickCount64 - started;
                    if (elapsed >= _maxDelay.TotalMilliseconds)
                        break;

                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(token);
                    quiet.CancelAfter(_quietPeriod);
                    try
                    {
                        if (!await reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false))
                            break;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        break; // quiet period elapsed: flush
                    }
                }

                var overflow = Interlocked.Exchange(ref _overflowed, 0) == 1;
                var removed = watcherFailed && !Directory.Exists(DirectoryPath);
                if (pending.Count == 0 && !overflow && !watcherFailed)
                    continue;

                var created = new List<string>();
                var deleted = new List<string>();
                var changed = new List<string>();
                foreach (var (name, kind) in pending)
                {
                    (kind switch { ChangeKind.Created => created, ChangeKind.Deleted => deleted, _ => changed }).Add(name);
                }

                _onBatch(new DirectoryChangeBatch(DirectoryPath, created, deleted, changed, overflow || (watcherFailed && !removed), removed));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Folds a new event into the pending state for one name:
    /// created+deleted cancel out, deleted+created is a replacement (changed), created+changed stays created.
    /// </summary>
    internal static void Coalesce(Dictionary<string, ChangeKind> pending, ChangeKind kind, string name)
    {
        if (!pending.TryGetValue(name, out var previous))
        {
            pending[name] = kind;
            return;
        }

        switch (previous, kind)
        {
            case (ChangeKind.Created, ChangeKind.Deleted):
                pending.Remove(name);
                break;
            case (ChangeKind.Created, _):
                break;
            case (ChangeKind.Deleted, ChangeKind.Created):
                pending[name] = ChangeKind.Changed;
                break;
            case (ChangeKind.Changed, ChangeKind.Deleted):
                pending[name] = ChangeKind.Deleted;
                break;
            default:
                break;
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _events.Writer.TryComplete();
        // No blocking wait: the pump observes cancellation on its own and the callback may be queued to the UI thread.
        _stop.Cancel();
        _ = _pump.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), _stop, TaskScheduler.Default);
    }

    /// <summary>Completes when the background pump has stopped (tests).</summary>
    internal Task Stopped => _pump;

    private readonly record struct RawEvent(ChangeKind Kind, string? Name);
}
