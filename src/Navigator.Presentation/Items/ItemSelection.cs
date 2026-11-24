namespace Navigator.Presentation.Items;

/// <summary>
/// Selection state for one pane, independent of the visual layout (details rows, tiles, columns), so that
/// click / Ctrl+click / Shift+click / keyboard / rubber-band selection behave identically everywhere and can be unit-tested.
/// </summary>
public sealed class ItemSelection
{
    private readonly HashSet<FileItemViewModel> _selected = [];
    private IReadOnlyList<FileItemViewModel> _items = [];
    private Dictionary<FileItemViewModel, int> _indexOf = [];
    private bool _suppress;

    public event EventHandler? Changed;

    /// <summary>Index of the keyboard focus (-1 when nothing is focused).</summary>
    public int FocusIndex { get; private set; } = -1;

    public int AnchorIndex { get; private set; } = -1;

    public int Count => _selected.Count;

    public FileItemViewModel? FocusedItem => FocusIndex >= 0 && FocusIndex < _items.Count ? _items[FocusIndex] : null;

    /// <summary>Selected items in display order.</summary>
    public IReadOnlyList<FileItemViewModel> SelectedItems
        => _selected.Count == 0 ? [] : [.. _selected.OrderBy(i => _indexOf.GetValueOrDefault(i, int.MaxValue))];

    public bool IsSelected(FileItemViewModel item) => _selected.Contains(item);

    /// <summary>Points the selection at a new ordered list (after sort/filter/reload), keeping items that still exist.</summary>
    public void SetItems(IReadOnlyList<FileItemViewModel> items, IEnumerable<string>? reselectPaths = null)
    {
        var previousFocus = FocusedItem?.FullPath;
        _items = items;
        _indexOf = new Dictionary<FileItemViewModel, int>(items.Count);
        for (var i = 0; i < items.Count; i++)
            _indexOf[items[i]] = i;

        var keep = reselectPaths?.ToHashSet(Navigator.Core.IO.PathUtil.Comparer);
        _suppress = true;
        foreach (var item in _selected.ToList())
        {
            if (!_indexOf.ContainsKey(item))
                Deselect(item);
        }

        if (keep is not null)
        {
            foreach (var item in items)
            {
                if (keep.Contains(item.FullPath))
                    Select(item);
            }
        }

        _suppress = false;
        FocusIndex = previousFocus is null ? -1 : IndexOfPath(previousFocus);
        if (AnchorIndex >= items.Count)
            AnchorIndex = FocusIndex;
        RaiseChanged();
    }

    public int IndexOf(FileItemViewModel item) => _indexOf.GetValueOrDefault(item, -1);

    public int IndexOfPath(string path)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (Navigator.Core.IO.PathUtil.AreEqual(_items[i].FullPath, path))
                return i;
        }

        return -1;
    }

    /// <summary>Mouse click semantics of desktop file managers.</summary>
    public void Click(FileItemViewModel item, bool control, bool shift)
    {
        var index = IndexOf(item);
        if (index < 0)
            return;

        _suppress = true;
        if (shift && AnchorIndex >= 0)
        {
            if (!control)
                ClearInternal();
            SelectRangeInternal(AnchorIndex, index);
        }
        else if (control)
        {
            if (_selected.Contains(item))
                Deselect(item);
            else
                Select(item);
            AnchorIndex = index;
        }
        else
        {
            ClearInternal();
            Select(item);
            AnchorIndex = index;
        }

        FocusIndex = index;
        _suppress = false;
        RaiseChanged();
    }

    /// <summary>Right-click: keeps an existing multi-selection if the item is part of it, otherwise selects only the item.</summary>
    public void PrepareContextMenu(FileItemViewModel item)
    {
        if (_selected.Contains(item))
            return;
        Click(item, control: false, shift: false);
    }

    /// <summary>Arrow keys / Home / End / Page keys. <paramref name="delta"/> is relative; use int.MinValue/MaxValue for Home/End.</summary>
    public void MoveFocus(int delta, bool shift, bool control)
    {
        if (_items.Count == 0)
            return;

        var current = FocusIndex < 0 ? (delta > 0 ? -1 : _items.Count) : FocusIndex;
        var target = delta switch
        {
            int.MinValue => 0,
            int.MaxValue => _items.Count - 1,
            _ => Math.Clamp(current + delta, 0, _items.Count - 1),
        };

        _suppress = true;
        if (shift)
        {
            if (AnchorIndex < 0)
                AnchorIndex = Math.Max(0, FocusIndex);
            if (!control)
                ClearInternal();
            SelectRangeInternal(AnchorIndex, target);
        }
        else if (!control)
        {
            ClearInternal();
            Select(_items[target]);
            AnchorIndex = target;
        }

        FocusIndex = target;
        _suppress = false;
        RaiseChanged();
    }

    /// <summary>Ctrl+Space: toggles the focused item.</summary>
    public void ToggleFocused()
    {
        if (FocusedItem is not { } item)
            return;
        if (!_selected.Remove(item))
            _selected.Add(item);
        item.IsSelected = _selected.Contains(item);
        AnchorIndex = FocusIndex;
        RaiseChanged();
    }

    public void SelectAll()
    {
        _suppress = true;
        foreach (var item in _items)
            Select(item);
        _suppress = false;
        RaiseChanged();
    }

    public void Invert()
    {
        _suppress = true;
        foreach (var item in _items)
        {
            if (_selected.Contains(item))
                Deselect(item);
            else
                Select(item);
        }

        _suppress = false;
        RaiseChanged();
    }

    public void Clear()
    {
        if (_selected.Count == 0)
            return;
        ClearInternal();
        RaiseChanged();
    }

    /// <summary>Rubber-band selection: replaces (or with Ctrl, toggles against) the selection with the items in [from, to].</summary>
    public void SelectRectangle(IEnumerable<FileItemViewModel> inside, IReadOnlyCollection<FileItemViewModel>? baseline)
    {
        _suppress = true;
        ClearInternal();
        var set = inside.ToHashSet();
        if (baseline is not null)
        {
            foreach (var item in baseline)
            {
                if (!set.Contains(item))
                    Select(item);
            }

            foreach (var item in set)
            {
                if (!baseline.Contains(item))
                    Select(item);
            }
        }
        else
        {
            foreach (var item in set)
                Select(item);
        }

        _suppress = false;
        RaiseChanged();
    }

    public void SelectOnly(IEnumerable<FileItemViewModel> items)
    {
        _suppress = true;
        ClearInternal();
        FileItemViewModel? last = null;
        foreach (var item in items)
        {
            Select(item);
            last = item;
        }

        if (last is not null)
            FocusIndex = AnchorIndex = IndexOf(last);
        _suppress = false;
        RaiseChanged();
    }

    /// <summary>Type-to-select: focuses the next item whose name starts with <paramref name="prefix"/>.</summary>
    public FileItemViewModel? JumpToPrefix(string prefix)
    {
        if (_items.Count == 0 || string.IsNullOrEmpty(prefix))
            return null;

        var start = Math.Max(0, FocusIndex);
        for (var offset = prefix.Length == 1 ? 1 : 0; offset <= _items.Count; offset++)
        {
            var index = (start + offset) % _items.Count;
            if (_items[index].DisplayName.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                Click(_items[index], control: false, shift: false);
                return _items[index];
            }
        }

        return null;
    }

    private void SelectRangeInternal(int from, int to)
    {
        var (start, end) = from <= to ? (from, to) : (to, from);
        for (var i = Math.Max(0, start); i <= end && i < _items.Count; i++)
            Select(_items[i]);
    }

    private void ClearInternal()
    {
        foreach (var item in _selected)
            item.IsSelected = false;
        _selected.Clear();
    }

    private void Select(FileItemViewModel item)
    {
        if (_selected.Add(item))
            item.IsSelected = true;
    }

    private void Deselect(FileItemViewModel item)
    {
        if (_selected.Remove(item))
            item.IsSelected = false;
    }

    private void RaiseChanged()
    {
        if (!_suppress)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
