namespace Navigator.Core.Navigation;

/// <summary>
/// Back/forward history for one tab or pane. Bounded so that long sessions cannot grow memory without limit.
/// Not thread-safe: owned by a single view model on the UI thread.
/// </summary>
public sealed class NavigationHistory
{
    public const int DefaultCapacity = 128;

    private readonly int _capacity;
    private readonly LinkedList<Location> _back = new();
    private readonly Stack<Location> _forward = new();

    public NavigationHistory(Location initial, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        Current = initial;
    }

    public Location Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Most recent first.</summary>
    public IEnumerable<Location> BackEntries
    {
        get
        {
            for (var node = _back.Last; node is not null; node = node.Previous)
                yield return node.Value;
        }
    }

    /// <summary>Nearest first.</summary>
    public IEnumerable<Location> ForwardEntries => _forward;

    public event EventHandler? Changed;

    /// <summary>Navigates to a new location. Re-navigating to the current location is a no-op.</summary>
    public bool Navigate(Location location)
    {
        if (location.Equals(Current))
            return false;

        PushBack(Current);
        _forward.Clear();
        Current = location;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public Location? GoBack(int steps = 1)
    {
        if (steps < 1 || _back.Count < steps)
            return null;

        for (var i = 0; i < steps; i++)
        {
            _forward.Push(Current);
            Current = _back.Last!.Value;
            _back.RemoveLast();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    public Location? GoForward(int steps = 1)
    {
        if (steps < 1 || _forward.Count < steps)
            return null;

        for (var i = 0; i < steps; i++)
        {
            PushBack(Current);
            Current = _forward.Pop();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    /// <summary>
    /// Replaces the current entry and any history entries for <paramref name="oldLocation"/>,
    /// e.g. after the folder being shown was renamed or moved.
    /// </summary>
    public void ReplaceLocation(Location oldLocation, Location newLocation)
    {
        if (Current.Equals(oldLocation))
            Current = newLocation;

        for (var node = _back.First; node is not null; node = node.Next)
        {
            if (node.Value.Equals(oldLocation))
                node.Value = newLocation;
        }

        if (_forward.Count > 0 && _forward.Any(l => l.Equals(oldLocation)))
        {
            var items = _forward.Select(l => l.Equals(oldLocation) ? newLocation : l).Reverse().ToArray();
            _forward.Clear();
            foreach (var item in items)
                _forward.Push(item);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies the state (used when duplicating a tab).</summary>
    public NavigationHistory Clone()
    {
        var clone = new NavigationHistory(Current, _capacity);
        foreach (var item in _back)
            clone._back.AddLast(item);
        foreach (var item in _forward.Reverse())
            clone._forward.Push(item);
        return clone;
    }

    private void PushBack(Location location)
    {
        _back.AddLast(location);
        while (_back.Count > _capacity)
            _back.RemoveFirst();
    }
}
