namespace Navigator.Core.Collections;

/// <summary>
/// LIFO stack that silently drops its oldest item when full. Used for recently closed tabs,
/// undo history and search history so that none of them grow without limit.
/// </summary>
public sealed class BoundedStack<T>
{
    private readonly LinkedList<T> _items = new();

    public BoundedStack(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _items.Count;

    public void Push(T item)
    {
        _items.AddLast(item);
        if (_items.Count > Capacity)
            _items.RemoveFirst();
    }

    public bool TryPop(out T item)
    {
        if (_items.Last is null)
        {
            item = default!;
            return false;
        }

        item = _items.Last.Value;
        _items.RemoveLast();
        return true;
    }

    public bool TryPeek(out T item)
    {
        if (_items.Last is null)
        {
            item = default!;
            return false;
        }

        item = _items.Last.Value;
        return true;
    }

    public void Clear() => _items.Clear();

    /// <summary>Newest first.</summary>
    public IEnumerable<T> Items
    {
        get
        {
            for (var node = _items.Last; node is not null; node = node.Previous)
                yield return node.Value;
        }
    }

    public void RemoveWhere(Func<T, bool> predicate)
    {
        var node = _items.First;
        while (node is not null)
        {
            var next = node.Next;
            if (predicate(node.Value))
                _items.Remove(node);
            node = next;
        }
    }
}
