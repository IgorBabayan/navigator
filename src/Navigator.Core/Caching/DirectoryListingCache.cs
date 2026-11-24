using Navigator.Core.IO;

namespace Navigator.Core.Caching;

/// <summary>
/// Listings of recently visited folders, so going back/forward or reopening a folder shows its items at once while
/// a fresh enumeration runs in the background ("stale while revalidate"; see PaneViewModel.LoadFolderAsync).
/// <para>
/// Bounded twice: by the number of folders (Performance.DirectoryCacheEntries, 0 = off) and by the total number of
/// entries across folders (<see cref="MaxTotalEntries"/>), so a few huge folders cannot hold on to lots of memory.
/// Folders above <see cref="MaxEntriesPerFolder"/> are never cached. Entries are immutable and shared, so a cached
/// listing costs only the array that holds it. Thread-safe.
/// </para>
/// </summary>
public sealed class DirectoryListingCache
{
    public const int MaxEntriesPerFolder = 20_000;
    public const int MaxTotalEntries = 100_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Listing>> _map;
    private readonly LinkedList<Listing> _order = new();
    private int _maxFolders;
    private int _totalEntries;

    public DirectoryListingCache(int maxFolders, IEqualityComparer<string>? pathComparer = null)
    {
        _maxFolders = Math.Max(0, maxFolders);
        _map = new Dictionary<string, LinkedListNode<Listing>>(pathComparer ?? PathUtil.Comparer);
    }

    public int MaxFolders
    {
        get
        {
            lock (_gate)
                return _maxFolders;
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

    public int TotalEntries
    {
        get
        {
            lock (_gate)
                return _totalEntries;
        }
    }

    public void Resize(int maxFolders)
    {
        lock (_gate)
        {
            _maxFolders = Math.Max(0, maxFolders);
            EvictOverLimit();
        }
    }

    /// <summary>The cached listing of <paramref name="path"/> made with the same filter, or null.</summary>
    public FileEntry[]? TryGet(string path, EnumerationFilter filter)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(path, out var node))
                return null;
            if (node.Value.Filter != filter)
                return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Entries;
        }
    }

    /// <summary>Remembers a complete listing (replacing an older one). Too large folders are dropped instead.</summary>
    public void Store(string path, EnumerationFilter filter, IReadOnlyCollection<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaxEntriesPerFolder)
        {
            Invalidate(path);
            return;
        }

        var listing = new Listing(path, filter, [.. entries]);
        lock (_gate)
        {
            RemoveLocked(path);
            if (_maxFolders == 0)
                return;
            _map[path] = _order.AddFirst(listing);
            _totalEntries += listing.Entries.Length;
            EvictOverLimit();
        }
    }

    public void Invalidate(string path)
    {
        lock (_gate)
            RemoveLocked(path);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
            _totalEntries = 0;
        }
    }

    private void RemoveLocked(string path)
    {
        if (!_map.Remove(path, out var node))
            return;
        _order.Remove(node);
        _totalEntries -= node.Value.Entries.Length;
    }

    private void EvictOverLimit()
    {
        while (_order.Last is { } oldest && (_map.Count > _maxFolders || _totalEntries > MaxTotalEntries))
            RemoveLocked(oldest.Value.Path);
    }

    private sealed record Listing(string Path, EnumerationFilter Filter, FileEntry[] Entries);
}
