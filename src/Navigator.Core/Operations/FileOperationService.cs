using Navigator.Core.IO;
using Navigator.Core.Platform;

namespace Navigator.Core.Operations;

public enum UndoOutcome
{
    Nothing,
    Done,
    PartiallyDone,
    NotPossible,
}

/// <summary>
/// Entry point for every file system change made by the UI: queued background operations
/// (copy, move, delete, recycle) and immediate ones (rename, create), plus undo/redo.
/// </summary>
public sealed class FileOperationService
{
    private readonly FileOperationQueue _queue;
    private readonly ITrashService _trash;
    private readonly TransferExecutor _transfer;

    public FileOperationService(FileOperationQueue queue, ITrashService trash, IVolumeLocator volumes)
    {
        _queue = queue;
        _trash = trash;
        _transfer = new TransferExecutor(volumes);
    }

    public FileOperationQueue Queue => _queue;

    public UndoManager Undo { get; } = new();

    public bool CanRecycle => _trash.IsSupported;

    /// <summary>Raised after items were renamed or moved (old path, new path). Used to keep tags and pins in sync.</summary>
    public event EventHandler<IReadOnlyList<(string OldPath, string NewPath)>>? ItemsRelocated;

    public FileOperation Copy(IReadOnlyList<string> sources, string destinationDirectory, IFileOperationInteraction interaction)
        => Transfer([.. sources.Select(s => new TransferPair(s, destinationDirectory))], move: false, interaction, recordUndo: true);

    public FileOperation Move(IReadOnlyList<string> sources, string destinationDirectory, IFileOperationInteraction interaction)
        => Transfer([.. sources.Select(s => new TransferPair(s, destinationDirectory))], move: true, interaction, recordUndo: true);

    /// <summary>Copies items next to themselves ("name - Copy").</summary>
    public FileOperation Duplicate(IReadOnlyList<string> sources, IFileOperationInteraction interaction)
        => Transfer([.. sources.Select(s => new TransferPair(s, Path.GetDirectoryName(PathUtil.TrimTrailingSeparator(s))!))], move: false, interaction, recordUndo: true);

    public FileOperation Transfer(IReadOnlyList<TransferPair> pairs, bool move, IFileOperationInteraction interaction, bool recordUndo)
    {
        var destination = pairs.Select(p => p.DestinationDirectory).Distinct(PathUtil.Comparer).Count() == 1 ? pairs[0].DestinationDirectory : null;
        var operation = _queue.Enqueue(
            move ? FileOperationKind.Move : FileOperationKind.Copy,
            [.. pairs.Select(p => p.Source)],
            destination,
            interaction,
            context => _transfer.RunAsync(pairs, move, context));

        _ = operation.Completion.ContinueWith(
            _ => OnTransferFinished(operation, move, recordUndo),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    public FileOperation Delete(IReadOnlyList<string> paths, bool permanently, IFileOperationInteraction interaction)
    {
        if (!permanently && _trash.IsSupported)
        {
            var recycle = _queue.Enqueue(FileOperationKind.Recycle, paths, null, interaction,
                context => DeleteExecutor.RecycleAsync(paths, _trash, context));
            _ = recycle.Completion.ContinueWith(
                _ =>
                {
                    if (recycle.TrashedItems.Count > 0)
                        Undo.Record(new RecycleRecord(recycle.TrashedItems));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return recycle;
        }

        return _queue.Enqueue(FileOperationKind.Delete, paths, null, interaction,
            context => DeleteExecutor.DeletePermanentlyAsync(paths, context));
    }

    /// <summary>Creates a folder. Throws <see cref="OperationStepException"/> for invalid or existing names.</summary>
    public string CreateFolder(string parentDirectory, string name) => Create(parentDirectory, name, isDirectory: true, recordUndo: true);

    /// <summary>Creates an empty file. Never truncates an existing file.</summary>
    public string CreateFile(string parentDirectory, string name) => Create(parentDirectory, name, isDirectory: false, recordUndo: true);

    private string Create(string parentDirectory, string name, bool isDirectory, bool recordUndo)
    {
        ValidateName(name);
        var path = Path.Combine(parentDirectory, name);
        if (ItemInfo.AnyExists(path))
            throw new OperationStepException(FileOperationErrorKind.AlreadyExists, $"'{name}' already exists.");

        if (isDirectory)
            Directory.CreateDirectory(path);
        else
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();

        if (recordUndo)
            Undo.Record(new CreateRecord(path, isDirectory));
        return path;
    }

    /// <summary>Renames a file or folder in place. Returns the new full path.</summary>
    public string Rename(string path, string newName) => Rename(path, newName, recordUndo: true);

    private string Rename(string path, string newName, bool recordUndo)
    {
        path = PathUtil.TrimTrailingSeparator(path);
        ValidateName(newName);
        var parent = Path.GetDirectoryName(path) ?? throw new OperationStepException(FileOperationErrorKind.NotSupported, "A root cannot be renamed.");
        var target = Path.Combine(parent, newName);
        if (string.Equals(path, target, StringComparison.Ordinal))
            return path;

        var info = ItemInfo.Probe(path);
        if (!info.Exists)
            throw new FileNotFoundException($"'{path}' no longer exists.", path);

        var caseOnly = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
        if (ItemInfo.AnyExists(target) && !caseOnly)
            throw new OperationStepException(FileOperationErrorKind.AlreadyExists, $"'{newName}' already exists.");

        if (caseOnly)
        {
            // Case-insensitive volumes report the target as existing (it is the same item): go through a temporary name.
            var temporary = Path.Combine(parent, $".{Guid.NewGuid():N}.rename");
            MoveItem(path, temporary, info.IsDirectory);
            try
            {
                MoveItem(temporary, target, info.IsDirectory);
            }
            catch
            {
                MoveItem(temporary, path, info.IsDirectory);
                throw;
            }
        }
        else
        {
            MoveItem(path, target, info.IsDirectory);
        }

        if (recordUndo)
            Undo.Record(new RenameRecord(path, target));
        ItemsRelocated?.Invoke(this, [(path, target)]);
        return target;
    }

    private static void MoveItem(string from, string to, bool isDirectory)
    {
        if (isDirectory)
            Directory.Move(from, to);
        else
            File.Move(from, to, overwrite: false);
    }

    private static void ValidateName(string name)
    {
        var result = PathUtil.ValidateName(name);
        if (result != NameValidationResult.Valid)
            throw new OperationStepException(FileOperationErrorKind.InvalidName, $"'{name}' is not a valid name ({result}).");
    }

    public async Task<UndoOutcome> UndoAsync(IFileOperationInteraction interaction)
    {
        if (!Undo.TryTakeUndo(out var record))
            return UndoOutcome.Nothing;

        var (outcome, inverse) = await RevertAsync(record, interaction).ConfigureAwait(false);
        if (inverse is not null)
            Undo.PushRedo(inverse);
        return outcome;
    }

    public async Task<UndoOutcome> RedoAsync(IFileOperationInteraction interaction)
    {
        if (!Undo.TryTakeRedo(out var record))
            return UndoOutcome.Nothing;

        var (outcome, inverse) = await RevertAsync(record, interaction).ConfigureAwait(false);
        if (inverse is not null)
            Undo.PushUndoKeepingRedo(inverse);
        return outcome;
    }

    /// <summary>Applies the inverse of <paramref name="record"/> and returns the record that would undo that again.</summary>
    private async Task<(UndoOutcome Outcome, UndoRecord? Inverse)> RevertAsync(UndoRecord record, IFileOperationInteraction interaction)
    {
        switch (record)
        {
            case RenameRecord rename:
            {
                if (!ItemInfo.AnyExists(rename.RenamedPath))
                    return (UndoOutcome.NotPossible, null);
                var restored = Rename(rename.RenamedPath, Path.GetFileName(rename.OriginalPath), recordUndo: false);
                return (UndoOutcome.Done, new RenameRecord(rename.RenamedPath, restored));
            }

            case TransferRecord { WasMove: true } move:
            {
                var pairs = move.Items
                    .Where(i => ItemInfo.AnyExists(i.DestinationPath))
                    .Select(i => new TransferPair(i.DestinationPath, Path.GetDirectoryName(i.SourcePath)!, Path.GetFileName(i.SourcePath)))
                    .ToList();
                if (pairs.Count == 0)
                    return (UndoOutcome.NotPossible, null);

                var operation = Transfer(pairs, move: true, interaction, recordUndo: false);
                await operation.Completion.ConfigureAwait(false);
                var back = operation.TransferredItems;
                var outcome = back.Count == move.Items.Count ? UndoOutcome.Done : back.Count > 0 ? UndoOutcome.PartiallyDone : UndoOutcome.NotPossible;
                return (outcome, back.Count > 0 ? new TransferRecord(back, WasMove: true) : null);
            }

            case TransferRecord copy:
            {
                if (!_trash.IsSupported)
                    return (UndoOutcome.NotPossible, null);
                var created = copy.Items.Select(i => i.DestinationPath).Where(ItemInfo.AnyExists).ToList();
                if (created.Count == 0)
                    return (UndoOutcome.NotPossible, null);
                var operation = Delete(created, permanently: false, interaction);
                await operation.Completion.ConfigureAwait(false);
                return (operation.State == FileOperationState.Completed ? UndoOutcome.Done : UndoOutcome.PartiallyDone,
                    operation.TrashedItems.Count > 0 ? new RecycleRecord(operation.TrashedItems) : null);
            }

            case CreateRecord create:
            {
                if (!_trash.IsSupported || !ItemInfo.AnyExists(create.Path))
                    return (UndoOutcome.NotPossible, null);
                var operation = Delete([create.Path], permanently: false, interaction);
                await operation.Completion.ConfigureAwait(false);
                return operation.TrashedItems.Count > 0
                    ? (UndoOutcome.Done, new RecycleRecord(operation.TrashedItems))
                    : (UndoOutcome.NotPossible, null);
            }

            case RecycleRecord recycle:
            {
                var restored = new List<string>();
                foreach (var item in recycle.Items)
                {
                    try
                    {
                        await _trash.RestoreAsync(item).ConfigureAwait(false);
                        restored.Add(item.OriginalPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Restore target occupied or trash entry gone: skip that item, keep the rest.
                    }
                }

                var outcome = restored.Count == recycle.Items.Count ? UndoOutcome.Done : restored.Count > 0 ? UndoOutcome.PartiallyDone : UndoOutcome.NotPossible;
                return (outcome, restored.Count > 0
                    ? new TransferRecord([.. restored.Select(p => new TransferredItem(p, p, Directory.Exists(p), WasMoved: false))], WasMove: false)
                    : null);
            }

            default:
                return (UndoOutcome.NotPossible, null);
        }
    }

    private void OnTransferFinished(FileOperation operation, bool move, bool recordUndo)
    {
        var items = operation.TransferredItems;
        if (items.Count == 0)
            return;

        if (recordUndo)
            Undo.Record(new TransferRecord(items, move));
        if (move)
            ItemsRelocated?.Invoke(this, [.. items.Select(i => (i.SourcePath, i.DestinationPath))]);
    }
}
