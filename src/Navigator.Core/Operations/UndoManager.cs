using Navigator.Core.Collections;
using Navigator.Core.Platform;

namespace Navigator.Core.Operations;

/// <summary>Something that can be reverted. Records only describe effects; <see cref="FileOperationService"/> executes them.</summary>
public abstract record UndoRecord;

public sealed record RenameRecord(string OriginalPath, string RenamedPath) : UndoRecord;

/// <summary>A multi-rename: undone (and redone) as one step, in reverse order of how it was applied.</summary>
public sealed record BatchRenameRecord(IReadOnlyList<RenameRecord> Items) : UndoRecord;

public sealed record TransferRecord(IReadOnlyList<TransferredItem> Items, bool WasMove) : UndoRecord;

public sealed record CreateRecord(string Path, bool IsDirectory) : UndoRecord;

public sealed record RecycleRecord(IReadOnlyList<TrashItem> Items) : UndoRecord;

/// <summary>Bounded undo/redo stacks. Recording a new action clears the redo stack.</summary>
public sealed class UndoManager
{
    public const int Capacity = 64;

    private readonly Lock _gate = new();
    private readonly BoundedStack<UndoRecord> _undo = new(Capacity);
    private readonly BoundedStack<UndoRecord> _redo = new(Capacity);

    public event EventHandler? Changed;

    public bool CanUndo
    {
        get
        {
            lock (_gate)
                return _undo.Count > 0;
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_gate)
                return _redo.Count > 0;
        }
    }

    public void Record(UndoRecord record)
    {
        lock (_gate)
        {
            _undo.Push(record);
            _redo.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Swaps the newest undo step for <paramref name="replace"/>(step) when <paramref name="match"/> accepts it; the redo
    /// stack is left alone (nothing was undone). False, with nothing changed, when the newest step is another one.
    /// </summary>
    public bool TryReplaceLatest(Func<UndoRecord, bool> match, Func<UndoRecord, UndoRecord> replace)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(replace);
        lock (_gate)
        {
            if (!_undo.TryPeek(out var latest) || !match(latest))
                return false;
            _undo.TryPop(out _);
            _undo.Push(replace(latest));
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal bool TryTakeUndo(out UndoRecord record)
    {
        bool taken;
        lock (_gate)
            taken = _undo.TryPop(out record);
        if (taken)
            Changed?.Invoke(this, EventArgs.Empty);
        return taken;
    }

    internal bool TryTakeRedo(out UndoRecord record)
    {
        bool taken;
        lock (_gate)
            taken = _redo.TryPop(out record);
        if (taken)
            Changed?.Invoke(this, EventArgs.Empty);
        return taken;
    }

    internal void PushRedo(UndoRecord record)
    {
        lock (_gate)
            _redo.Push(record);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void PushUndoKeepingRedo(UndoRecord record)
    {
        lock (_gate)
            _undo.Push(record);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _undo.Clear();
            _redo.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
