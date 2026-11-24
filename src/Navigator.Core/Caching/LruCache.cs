namespace Navigator.Core.Caching;

/// <summary>
/// Thread-safe, cost-bounded least-recently-used cache with request de-duplication:
/// concurrent <see cref="GetOrCreateAsync"/> calls for the same key share one factory task.
/// Evicted values that implement <see cref="IDisposable"/> are disposed unless an eviction callback is given.
/// </summary>
public sealed class LruCache<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();
    private readonly Dictionary<TKey, Task<TValue>> _inFlight;
    private readonly Func<TValue, long> _costOf;
    private readonly Action<TValue>? _onEvicted;
    private long _currentCost;
    private long _hits;
    private long _misses;
    private bool _disposed;

    public LruCache(long capacity, Func<TValue, long>? costOf = null, IEqualityComparer<TKey>? comparer = null, Action<TValue>? onEvicted = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _costOf = costOf ?? (static _ => 1);
        _onEvicted = onEvicted;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
        _inFlight = new Dictionary<TKey, Task<TValue>>(comparer);
    }

    public long Capacity { get; private set; }

    public long CurrentCost
    {
        get
        {
            lock (_gate)
                return _currentCost;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _map.Count;
        }
    }

    public CacheStatistics Statistics => new(Interlocked.Read(ref _hits), Interlocked.Read(ref _misses));

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }

        Interlocked.Increment(ref _misses);
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        List<TValue>? evicted = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cost = Math.Max(0, _costOf(value));
            if (_map.Remove(key, out var existing))
            {
                _order.Remove(existing);
                _currentCost -= existing.Value.Cost;
                if (!EqualityComparer<TValue>.Default.Equals(existing.Value.Value, value))
                    (evicted ??= []).Add(existing.Value.Value);
            }

            if (cost > Capacity)
            {
                // Larger than the whole cache: do not keep it, but do not leak it either.
                (evicted ??= []).Add(value);
            }
            else
            {
                var node = _order.AddFirst(new Entry(key, value, cost));
                _map[key] = node;
                _currentCost += cost;
                TrimLocked(ref evicted);
            }
        }

        Release(evicted);
    }

    /// <summary>
    /// Returns the cached value or runs <paramref name="factory"/> once for all concurrent callers.
    /// The caller's token only cancels its own wait, never the shared factory of other callers.
    /// </summary>
    public async Task<TValue> GetOrCreateAsync(TKey key, Func<TKey, CancellationToken, Task<TValue>> factory, CancellationToken cancellationToken = default)
    {
        if (TryGet(key, out var cached))
            return cached;

        Task<TValue> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_map.TryGetValue(key, out var node))
                return node.Value.Value;

            if (!_inFlight.TryGetValue(key, out task!))
            {
                task = RunFactoryAsync(key, factory);
                _inFlight[key] = task;
            }
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TValue> RunFactoryAsync(TKey key, Func<TKey, CancellationToken, Task<TValue>> factory)
    {
        try
        {
            await Task.Yield();
            var value = await factory(key, CancellationToken.None).ConfigureAwait(false);
            Set(key, value);
            return value;
        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(key);
        }
    }

    public bool Remove(TKey key)
    {
        TValue removed;
        lock (_gate)
        {
            if (!_map.Remove(key, out var node))
                return false;

            _order.Remove(node);
            _currentCost -= node.Value.Cost;
            removed = node.Value.Value;
        }

        Release([removed]);
        return true;
    }

    /// <summary>Invalidates every key matching <paramref name="predicate"/> (e.g. all entries below a changed folder).</summary>
    public int RemoveWhere(Func<TKey, bool> predicate)
    {
        List<TValue>? removed = null;
        lock (_gate)
        {
            var node = _order.First;
            while (node is not null)
            {
                var next = node.Next;
                if (predicate(node.Value.Key))
                {
                    _order.Remove(node);
                    _map.Remove(node.Value.Key);
                    _currentCost -= node.Value.Cost;
                    (removed ??= []).Add(node.Value.Value);
                }

                node = next;
            }
        }

        Release(removed);
        return removed?.Count ?? 0;
    }

    public void Resize(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        List<TValue>? evicted = null;
        lock (_gate)
        {
            Capacity = capacity;
            TrimLocked(ref evicted);
        }

        Release(evicted);
    }

    public void Clear()
    {
        List<TValue> all;
        lock (_gate)
        {
            all = [.. _order.Select(e => e.Value)];
            _order.Clear();
            _map.Clear();
            _currentCost = 0;
        }

        Release(all);
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Clear();
    }

    private void TrimLocked(ref List<TValue>? evicted)
    {
        while (_currentCost > Capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
            _currentCost -= last.Value.Cost;
            (evicted ??= []).Add(last.Value.Value);
        }
    }

    private void Release(List<TValue>? values)
    {
        if (values is null)
            return;

        foreach (var value in values)
        {
            if (_onEvicted is not null)
                _onEvicted(value);
            else if (value is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private readonly record struct Entry(TKey Key, TValue Value, long Cost);
}

public readonly record struct CacheStatistics(long Hits, long Misses)
{
    public double HitRatio => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}
