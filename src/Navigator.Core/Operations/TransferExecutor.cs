using System.IO.Enumeration;
using Navigator.Core.Hashing;
using Navigator.Core.IO;

namespace Navigator.Core.Operations;

/// <summary>
/// Copy and move engine.
/// Safety rules (see docs/ARCHITECTURE.md "File operation safety"):
/// <list type="bullet">
/// <item>Existing destinations are never overwritten without an explicit Replace decision.</item>
/// <item>Same-volume moves are atomic renames; cross-volume moves copy, verify, and only then delete the source.</item>
/// <item>Files are copied through a temporary file and committed with an atomic rename.</item>
/// <item>Symbolic links are recreated as links and never followed, so link loops cannot recurse.</item>
/// <item>A folder is only removed after a move if every child was moved successfully.</item>
/// </list>
/// </summary>
internal sealed class TransferExecutor(IVolumeLocator volumes)
{
    private static readonly EnumerationOptions MeasureOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public async Task RunAsync(IReadOnlyList<TransferPair> pairs, bool move, OperationContext context)
    {
        var plan = new List<(string Source, ItemInfo Info, string DestinationDirectory, string Name)>(pairs.Count);

        foreach (var pair in pairs)
        {
            var source = PathUtil.TrimTrailingSeparator(pair.Source);
            var destinationDirectory = PathUtil.TrimTrailingSeparator(pair.DestinationDirectory);
            ItemInfo info = default;

            var valid = await context.RunWithRecoveryAsync(source, () =>
            {
                info = ItemInfo.Probe(source);
                if (!info.Exists)
                    throw new FileNotFoundException($"'{source}' no longer exists.", source);
                if (!Directory.Exists(destinationDirectory))
                    throw new DirectoryNotFoundException($"Destination folder '{destinationDirectory}' does not exist.");
                if (info.IsDirectory && !info.IsLink && PathUtil.IsSameOrDescendant(source, destinationDirectory))
                {
                    throw new OperationStepException(
                        FileOperationErrorKind.DestinationInsideSource,
                        $"The destination folder is inside the source folder '{source}'.");
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            if (!valid)
                continue;

            var sameFolder = PathUtil.AreEqual(Path.GetDirectoryName(source), destinationDirectory);
            if (move && sameFolder && pair.TargetName is null)
                continue; // moving an item onto itself is a no-op, not a conflict

            var name = pair.TargetName ?? Path.GetFileName(source);
            if (!move && sameFolder && pair.TargetName is null)
                name = PathUtil.GetCopyName(destinationDirectory, name, info.IsDirectory, context.Options.CopyNameSuffix, ItemInfo.AnyExists);

            var (bytes, items) = Measure(source, info);
            context.AddTotals(bytes, items);
            plan.Add((source, info, destinationDirectory, name));
        }

        context.MarkRunning();
        foreach (var (source, info, destinationDirectory, name) in plan)
            await TransferAsync(source, info, destinationDirectory, name, move, isRoot: true, context).ConfigureAwait(false);

        context.SetCurrentItem(null);
    }

    private async ValueTask<bool> TransferAsync(string source, ItemInfo info, string destinationDirectory, string name, bool move, bool isRoot, OperationContext context)
    {
        context.SetCurrentItem(source);
        await context.CheckpointAsync().ConfigureAwait(false);

        var target = Path.Combine(destinationDirectory, name);
        var replace = false;
        var merge = false;
        var targetInfo = ItemInfo.Probe(target);

        if (targetInfo.Exists)
        {
            if (PathUtil.AreEqual(source, target))
            {
                Skip(source, info, context);
                return false;
            }

            var resolution = await context.ResolveConflictAsync(new ConflictInfo(
                source, target, info.IsDirectory, targetInfo.IsDirectory,
                info.Length, targetInfo.Length, info.LastWriteUtc, targetInfo.LastWriteUtc)).ConfigureAwait(false);

            switch (resolution)
            {
                case ConflictResolution.Skip:
                    Skip(source, info, context);
                    return false;
                case ConflictResolution.KeepBoth:
                    name = PathUtil.GetUniqueName(destinationDirectory, name, info.IsDirectory, ItemInfo.AnyExists);
                    target = Path.Combine(destinationDirectory, name);
                    break;
                default:
                    merge = info.IsDirectory && !info.IsLink && targetInfo.IsDirectory && !targetInfo.IsLink;
                    replace = !merge;
                    break;
            }
        }

        bool succeeded;
        if (info.IsLink)
            succeeded = await TransferLinkAsync(source, info, target, destinationDirectory, replace, move, context).ConfigureAwait(false);
        else if (info.IsDirectory)
            succeeded = await TransferDirectoryAsync(source, info, target, destinationDirectory, merge, move, context).ConfigureAwait(false);
        else
            succeeded = await TransferFileAsync(source, info, target, destinationDirectory, replace, move, context).ConfigureAwait(false);

        if (succeeded && isRoot)
            context.RecordTransferred(new TransferredItem(source, target, info.IsDirectory, move));

        return succeeded;
    }

    private async ValueTask<bool> TransferFileAsync(string source, ItemInfo info, string target, string destinationDirectory, bool replace, bool move, OperationContext context)
    {
        if (move && volumes.AreOnSameVolume(source, destinationDirectory))
        {
            var renamed = await context.RunWithRecoveryAsync(source, () =>
            {
                if (replace)
                    SafeFileCopier.PrepareForReplace(target);
                File.Move(source, target, replace);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            context.ReportBytes(info.Length);
            context.CompleteItem();
            return renamed;
        }

        var copied = await context.RunWithRecoveryAsync(source, () =>
            SafeFileCopier.CopyAsync(source, target, replace, flushToDisk: move && context.Options.FlushBeforeDeletingSource, context)).ConfigureAwait(false);

        if (!copied)
        {
            context.ReportBytes(info.Length);
            context.CompleteItem();
            return false;
        }

        var deleted = true;
        if (move)
        {
            var verified = await context.RunWithRecoveryAsync(target, () => VerifyCopyAsync(source, target, context)).ConfigureAwait(false);
            deleted = verified && await context.RunWithRecoveryAsync(source, () =>
            {
                DeleteFile(source);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }

        context.CompleteItem();
        return deleted;
    }

    private static async ValueTask VerifyCopyAsync(string source, string target, OperationContext context)
    {
        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        if (!targetInfo.Exists || targetInfo.Length != sourceInfo.Length)
            throw new OperationStepException(FileOperationErrorKind.VerificationFailed, $"'{target}' does not match '{source}'. The source was kept.");

        if (context.Options.VerifyMovedFileContents)
        {
            var expected = await FileHasher.ComputeAsync(source, HashKind.Sha256, cancellationToken: context.CancellationToken).ConfigureAwait(false);
            var actual = await FileHasher.ComputeAsync(target, HashKind.Sha256, cancellationToken: context.CancellationToken).ConfigureAwait(false);
            if (!FileHasher.DigestsEqual(expected, actual))
                throw new OperationStepException(FileOperationErrorKind.VerificationFailed, $"Content of '{target}' differs from '{source}'. The source was kept.");
        }
    }

    private async ValueTask<bool> TransferDirectoryAsync(string source, ItemInfo info, string target, string destinationDirectory, bool merge, bool move, OperationContext context)
    {
        if (move && !merge && volumes.AreOnSameVolume(source, destinationDirectory))
        {
            var (bytes, items) = Measure(source, info);
            var renamed = await context.RunWithRecoveryAsync(source, () =>
            {
                Directory.Move(source, target);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            if (renamed)
            {
                context.ReportBytes(bytes);
                for (var i = 0; i < items; i++)
                    context.CompleteItem();
                return true;
            }

            // Rename failed and was skipped (or Directory.Move refused, e.g. bind mounts): nothing moved.
            context.ReportBytes(bytes);
            return false;
        }

        if (!merge)
        {
            var created = await context.RunWithRecoveryAsync(target, () =>
            {
                Directory.CreateDirectory(target);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            if (!created)
            {
                Skip(source, info, context);
                return false;
            }
        }

        context.CompleteItem();

        List<FileEntry> children = [];
        var listed = await context.RunWithRecoveryAsync(source, () =>
        {
            children = DirectoryEnumerator.List(source, EnumerationFilter.All);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        var allSucceeded = listed;
        foreach (var child in children)
        {
            var childInfo = ItemInfo.Probe(child.FullPath);
            if (!childInfo.Exists)
                continue;

            var ok = await TransferAsync(child.FullPath, childInfo, target, child.Name, move, isRoot: false, context).ConfigureAwait(false);
            allSucceeded &= ok;
        }

        TryCopyDirectoryMetadata(source, target);

        if (move && allSucceeded)
        {
            allSucceeded = await context.RunWithRecoveryAsync(source, () =>
            {
                Directory.Delete(source, recursive: false);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }

        return allSucceeded;
    }

    private async ValueTask<bool> TransferLinkAsync(string source, ItemInfo info, string target, string destinationDirectory, bool replace, bool move, OperationContext context)
    {
        var ok = await context.RunWithRecoveryAsync(source, () =>
        {
            if (replace)
                DeleteExistingForReplace(target);

            if (move && volumes.AreOnSameVolume(source, destinationDirectory))
            {
                if (info.IsDirectory)
                    Directory.Move(source, target);
                else
                    File.Move(source, target);
                return ValueTask.CompletedTask;
            }

            var linkTarget = info.LinkTarget ?? throw new IOException($"Cannot read the link target of '{source}'.");
            if (info.IsDirectory)
                Directory.CreateSymbolicLink(target, linkTarget);
            else
                File.CreateSymbolicLink(target, linkTarget);

            if (move)
                DeleteLink(source, info.IsDirectory);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        context.CompleteItem();
        return ok;
    }

    private static void DeleteExistingForReplace(string target)
    {
        var existing = ItemInfo.Probe(target);
        if (!existing.Exists)
            return;
        if (existing.IsDirectory && !existing.IsLink)
            throw new OperationStepException(FileOperationErrorKind.AlreadyExists, $"'{target}' is a folder and cannot be replaced by a link.");
        if (existing.IsLink)
            DeleteLink(target, existing.IsDirectory);
        else
            DeleteFile(target);
    }

    internal static void DeleteLink(string path, bool isDirectoryLink)
    {
        if (isDirectoryLink)
            Directory.Delete(path, recursive: false); // removes the link itself, never the target's content
        else
            File.Delete(path);
    }

    internal static void DeleteFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    }

    private static void TryCopyDirectoryMetadata(string source, string target)
    {
        try
        {
            var info = new DirectoryInfo(source);
            if (!info.Exists)
                return;
            Directory.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, info.UnixFileMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Folder timestamps/permissions are cosmetic for the transfer; content safety is unaffected.
        }
    }

    private static void Skip(string source, ItemInfo info, OperationContext context)
    {
        var (bytes, items) = Measure(source, info);
        context.ReportBytes(bytes);
        for (var i = 0; i < items; i++)
            context.CompleteItem();
        context.NoteSkipped();
    }

    /// <summary>Total bytes and item count below a source, without following links.</summary>
    internal static (long Bytes, int Items) Measure(string path, ItemInfo info)
    {
        if (!info.IsDirectory || info.IsLink)
            return (info.IsLink ? 0 : Math.Max(0, info.Length), 1);

        long bytes = 0;
        var items = 1;
        try
        {
            var enumerable = new FileSystemEnumerable<long>(
                path,
                static (ref FileSystemEntry e) => e.IsDirectory || (e.Attributes & FileAttributes.ReparsePoint) != 0 ? 0 : e.Length,
                MeasureOptions)
            {
                ShouldRecursePredicate = static (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
            };

            foreach (var length in enumerable)
            {
                bytes += length;
                items++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Measurement only drives the progress bar; the transfer itself reports real errors.
        }

        return (bytes, items);
    }
}

/// <summary>What is at a path, without following a symbolic link at that path.</summary>
internal readonly record struct ItemInfo(bool Exists, bool IsDirectory, bool IsLink, long Length, DateTime LastWriteUtc, string? LinkTarget)
{
    public static ItemInfo Probe(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists)
            return new ItemInfo(true, false, file.LinkTarget is not null, file.Length, file.LastWriteTimeUtc, file.LinkTarget);

        var directory = new DirectoryInfo(path);
        if (directory.Exists)
            return new ItemInfo(true, true, directory.LinkTarget is not null, -1, directory.LastWriteTimeUtc, directory.LinkTarget);

        // A dangling link: the target is gone, but the link itself exists and must be handled.
        try
        {
            var target = file.LinkTarget;
            if (target is not null)
                return new ItemInfo(true, false, true, 0, DateTime.MinValue, target);
        }
        catch (IOException)
        {
        }

        return default;
    }

    public static bool AnyExists(string path) => Probe(path).Exists;
}
