using Navigator.Core.IO;
using Navigator.Core.Settings;

namespace Navigator.Core.Tags;

/// <summary>
/// File and folder tags. Assignments are kept in Navigator's own data file (portable across file systems,
/// no extended attributes or alternate data streams needed) and follow items when Navigator renames or moves them.
/// </summary>
public sealed class TagStore
{
    private readonly Lock _gate = new();
    private readonly JsonFileStore<TagAssignments>? _store;
    private readonly Dictionary<string, HashSet<string>> _items;
    private bool _dirty;

    public TagStore(JsonFileStore<TagAssignments>? store)
    {
        _store = store;
        var loaded = store?.Load() ?? new TagAssignments();
        _items = new Dictionary<string, HashSet<string>>(PathUtil.Comparer);
        foreach (var (path, ids) in loaded.Items)
        {
            if (ids.Count > 0)
                _items[PathUtil.TrimTrailingSeparator(path)] = [.. ids];
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyCollection<string> GetTags(string path)
    {
        lock (_gate)
            return _items.TryGetValue(PathUtil.TrimTrailingSeparator(path), out var ids) ? [.. ids] : [];
    }

    public bool HasTag(string path, string tagId)
    {
        lock (_gate)
            return _items.TryGetValue(PathUtil.TrimTrailingSeparator(path), out var ids) && ids.Contains(tagId);
    }

    public void SetTags(string path, IEnumerable<string> tagIds)
    {
        var key = PathUtil.TrimTrailingSeparator(path);
        lock (_gate)
        {
            var set = tagIds.ToHashSet(StringComparer.Ordinal);
            if (set.Count == 0)
                _items.Remove(key);
            else
                _items[key] = set;
            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleTag(IEnumerable<string> paths, string tagId)
    {
        lock (_gate)
        {
            var keys = paths.Select(PathUtil.TrimTrailingSeparator).ToList();
            var allHave = keys.All(k => _items.TryGetValue(k, out var s) && s.Contains(tagId));
            foreach (var key in keys)
            {
                if (allHave)
                {
                    if (_items.TryGetValue(key, out var set) && set.Remove(tagId) && set.Count == 0)
                        _items.Remove(key);
                }
                else
                {
                    if (!_items.TryGetValue(key, out var set))
                        _items[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(tagId);
                }
            }

            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes <paramref name="tagId"/> off every given path that carries it (others are left alone).</summary>
    public void RemoveTag(IEnumerable<string> paths, string tagId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var changed = false;
        lock (_gate)
        {
            foreach (var key in paths.Select(PathUtil.TrimTrailingSeparator))
            {
                if (!_items.TryGetValue(key, out var assigned) || !assigned.Remove(tagId))
                    continue;
                if (assigned.Count == 0)
                    _items.Remove(key);
                changed = true;
            }

            _dirty |= changed;
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Paths carrying <paramref name="tagId"/> that still exist.</summary>
    public IReadOnlyList<string> FindByTag(string tagId)
    {
        lock (_gate)
            return [.. _items.Where(kv => kv.Value.Contains(tagId)).Select(kv => kv.Key).Where(p => File.Exists(p) || Directory.Exists(p))];
    }

    /// <summary>Keeps assignments attached after a rename or move (including items below a moved folder).</summary>
    public void OnItemsRelocated(IReadOnlyList<(string OldPath, string NewPath)> moves)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var (oldPath, newPath) in moves)
            {
                var oldRoot = PathUtil.TrimTrailingSeparator(oldPath);
                var newRoot = PathUtil.TrimTrailingSeparator(newPath);
                foreach (var key in _items.Keys.Where(k => PathUtil.IsSameOrDescendant(oldRoot, k)).ToList())
                {
                    var relocated = newRoot + key[oldRoot.Length..];
                    _items[relocated] = _items[key];
                    _items.Remove(key);
                    changed = true;
                }
            }

            _dirty |= changed;
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveTagEverywhere(string tagId)
    {
        lock (_gate)
        {
            foreach (var key in _items.Keys.ToList())
            {
                if (_items[key].Remove(tagId) && _items[key].Count == 0)
                    _items.Remove(key);
            }

            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveIfDirty()
    {
        TagAssignments snapshot;
        lock (_gate)
        {
            if (!_dirty || _store is null)
                return;
            snapshot = new TagAssignments
            {
                Items = _items.ToDictionary(kv => kv.Key, kv => kv.Value.Order(StringComparer.Ordinal).ToList(), PathUtil.Comparer),
            };
            _dirty = false;
        }

        _store.Save(snapshot);
    }
}
