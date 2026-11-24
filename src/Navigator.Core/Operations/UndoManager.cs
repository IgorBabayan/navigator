using Navigator.Core.Collections;
using Navigator.Core.Platform;

namespace Navigator.Core.Operations;

/// <summary>Something that can be reverted. Records only describe effects; <see cref="FileOperationService"/> executes them.</summary>
public abstract record UndoRecord;

public sealed record RenameRecord(string OriginalPath, string RenamedPath) : UndoRecord;

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
