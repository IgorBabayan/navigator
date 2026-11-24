using Navigator.Core.IO;
using Navigator.Core.Platform;

namespace Navigator.Core.Operations;

/// <summary>Permanent deletion and Recycle Bin / Trash moves.</summary>
internal static class DeleteExecutor
{
    public static async Task DeletePermanentlyAsync(IReadOnlyList<string> paths, OperationContext context)
    {
        var roots = new List<(string Path, ItemInfo Info)>(paths.Count);
        foreach (var raw in paths)
        {
            var path = PathUtil.TrimTrailingSeparator(raw);
            var info = ItemInfo.Probe(path);
            if (!info.Exists)
                continue;
            var (_, items) = TransferExecutor.Measure(path, info);
            context.AddTotals(0, items);
            roots.Add((path, info));
        }

        context.MarkRunning();
        foreach (var (path, info) in roots)
            await DeleteTreeAsync(path, info, context).ConfigureAwait(false);

        context.SetCurrentItem(null);
    }

    private static async ValueTask<bool> DeleteTreeAsync(string path, ItemInfo info, OperationContext context)
    {
        context.SetCurrentItem(path);
        await context.CheckpointAsync().ConfigureAwait(false);

        if (info.IsDirectory && !info.IsLink)
        {
            List<FileEntry> children = [];
            var listed = await context.RunWithRecoveryAsync(path, () =>
            {
                children = DirectoryEnumerator.List(path, EnumerationFilter.All);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            var allDeleted = listed;
            foreach (var child in children)
            {
                var childInfo = ItemInfo.Probe(child.FullPath);
                if (childInfo.Exists)
                    allDeleted &= await DeleteTreeAsync(child.FullPath, childInfo, context).ConfigureAwait(false);
            }

            // Never force-delete a folder whose children could not all be removed.
            var removed = allDeleted && await context.RunWithRecoveryAsync(path, () =>
            {
                Directory.Delete(path, recursive: false);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            context.CompleteItem();
            return removed;
        }

        var deleted = await context.RunWithRecoveryAsync(path, () =>
        {
            if (info.IsLink)
                TransferExecutor.DeleteLink(path, info.IsDirectory);
            else
                TransferExecutor.DeleteFile(path);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        context.CompleteItem();
        return deleted;
    }

    public static async Task RecycleAsync(IReadOnlyList<string> paths, ITrashService trash, OperationContext context)
    {
        context.AddTotals(0, paths.Count);
        context.MarkRunning();
        foreach (var raw in paths)
        {
            var path = PathUtil.TrimTrailingSeparator(raw);
            context.SetCurrentItem(path);
            await context.CheckpointAsync().ConfigureAwait(false);
            var info = ItemInfo.Probe(path);
            if (!info.Exists)
            {
                context.CompleteItem();
                continue;
            }

            TrashItem? trashed = null;
            var ok = await context.RunWithRecoveryAsync(path, async () =>
                trashed = await trash.MoveToTrashAsync(path, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            if (ok && trashed is not null)
                context.RecordTrashed(trashed);
            if (ok)
                context.RecordTransferred(new TransferredItem(path, trashed?.StoragePath ?? string.Empty, info.IsDirectory, WasMoved: true));
            context.CompleteItem();
        }

        context.SetCurrentItem(null);
    }
}
