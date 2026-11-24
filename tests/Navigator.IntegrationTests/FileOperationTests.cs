using Navigator.Core.IO;
using Navigator.Core.Operations;
using Navigator.Tests;

namespace Navigator.IntegrationTests;

[TestClass]
public sealed class FileOperationTests
{
    private static (FileOperationService Service, FileOperationQueue Queue) Create(TempDirectory temp, bool sameVolume = true, FileOperationOptions? options = null)
    {
        var queue = new FileOperationQueue(2, options);
        return (new FileOperationService(queue, new FolderTrash(temp.Combine(".trash")), new FakeVolumeLocator(sameVolume)), queue);
    }

    private static readonly IFileOperationInteraction KeepBoth = new PolicyInteraction(ConflictResolution.KeepBoth, ErrorResolution.Skip);
    private static readonly IFileOperationInteraction Replace = new PolicyInteraction(ConflictResolution.Replace, ErrorResolution.Skip);
    private static readonly IFileOperationInteraction Skip = new PolicyInteraction(ConflictResolution.Skip, ErrorResolution.Skip);

    [TestMethod]
    public async Task Copy_FileAndFolderTree_PreservesContentAndTimes()
    {
        using var temp = new TempDirectory();
        var stamp = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var file = temp.CreateFile("src/a.txt", "alpha", stamp);
        temp.CreateFile("src/tree/b/c.txt", "gamma");
        var destination = temp.CreateDirectory("dst");
        var (service, _) = Create(temp);

        var operation = service.Copy([file, temp.Combine("src", "tree")], destination, Skip);
        await operation.Completion;

        Assert.AreEqual(FileOperationState.Completed, operation.State);
        Assert.AreEqual("alpha", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.AreEqual("gamma", File.ReadAllText(Path.Combine(destination, "tree", "b", "c.txt")));
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(Path.Combine(destination, "a.txt")));
        Assert.IsTrue(File.Exists(file), "copy keeps the source");
        Assert.AreEqual(1.0, operation.GetProgress().Fraction, 0.0001);
        Assert.AreEqual(2, operation.TransferredItems.Count);
    }

    [TestMethod]
    public async Task Copy_Conflicts_KeepBothReplaceSkip()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("src/n.txt", "new");
        var destination = temp.CreateDirectory("dst");
        var existing = temp.CreateFile("dst/n.txt", "old");
        var (service, _) = Create(temp);

        await service.Copy([source], destination, Skip).Completion;
        Assert.AreEqual("old", File.ReadAllText(existing), "never overwrite without a Replace decision");

        await service.Copy([source], destination, KeepBoth).Completion;
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(destination, "n (2).txt")));
        Assert.AreEqual("old", File.ReadAllText(existing));

        await service.Copy([source], destination, Replace).Completion;
        Assert.AreEqual("new", File.ReadAllText(existing));
        Assert.AreEqual(0, Directory.GetFiles(destination, "*navigator-partial*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task Copy_FolderOntoFolder_Merges()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/data/one.txt", "1");
        temp.CreateFile("dst/data/two.txt", "2");
        var (service, _) = Create(temp);
        await service.Copy([temp.Combine("src", "data")], temp.Combine("dst"), Replace).Completion;
        Assert.IsTrue(File.Exists(temp.Combine("dst", "data", "one.txt")));
        Assert.IsTrue(File.Exists(temp.Combine("dst", "data", "two.txt")));
    }

    [TestMethod]
    public async Task Copy_IntoSameFolder_CreatesCopyName()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("report.md", "x");
        var (service, _) = Create(temp);
        await service.Duplicate([file], Skip).Completion;
        await service.Duplicate([file], Skip).Completion;
        Assert.IsTrue(File.Exists(temp.Combine("report - Copy.md")));
        Assert.IsTrue(File.Exists(temp.Combine("report - Copy (2).md")));
    }

    [TestMethod]
    public async Task Move_SameVolume_Renames()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("src/f");
        temp.CreateFile("src/f/x.txt");
        var destination = temp.CreateDirectory("dst");
        var (service, _) = Create(temp, sameVolume: true);
        var operation = service.Move([folder], destination, Skip);
        await operation.Completion;
        Assert.IsFalse(Directory.Exists(folder));
        Assert.IsTrue(File.Exists(Path.Combine(destination, "f", "x.txt")));
    }

    [TestMethod]
    public async Task Move_CrossVolume_CopiesVerifiesThenDeletesSource()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/f/x.txt", "content");
        temp.CreateFile("src/f/sub/y.txt", "more");
        var destination = temp.CreateDirectory("dst");
        var (service, _) = Create(temp, sameVolume: false, new FileOperationOptions { VerifyMovedFileContents = true });

        var operation = service.Move([temp.Combine("src", "f")], destination, Skip);
        await operation.Completion;

        Assert.AreEqual(FileOperationState.Completed, operation.State);
        Assert.IsFalse(Directory.Exists(temp.Combine("src", "f")));
        Assert.AreEqual("more", File.ReadAllText(Path.Combine(destination, "f", "sub", "y.txt")));
    }

    [TestMethod]
    public async Task Move_SkippedChild_KeepsSourceFolder()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/f/keep.txt", "source");
        temp.CreateFile("src/f/go.txt", "go");
        temp.CreateFile("dst/f/keep.txt", "existing");
        var (service, _) = Create(temp, sameVolume: false);

        await service.Move([temp.Combine("src", "f")], temp.Combine("dst"), new MergeButSkipFiles()).Completion;

        Assert.IsTrue(File.Exists(temp.Combine("src", "f", "keep.txt")), "the skipped item stays in the source");
        Assert.IsFalse(File.Exists(temp.Combine("src", "f", "go.txt")));
        Assert.AreEqual("existing", File.ReadAllText(temp.Combine("dst", "f", "keep.txt")));
    }

    private sealed class MergeButSkipFiles : IFileOperationInteraction
    {
        public ValueTask<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ConflictChoice(conflict.IsFolderMerge ? ConflictResolution.Replace : ConflictResolution.Skip));

        public ValueTask<ErrorChoice> ResolveErrorAsync(string path, FileOperationErrorKind kind, string message, bool canRetry, CancellationToken cancellationToken)
            => ValueTask.FromResult(new ErrorChoice(ErrorResolution.Skip));
    }

    [TestMethod]
    public async Task Copy_IntoItself_IsRefused()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("a");
        var inner = temp.CreateDirectory("a/b");
        var (service, _) = Create(temp);
        var operation = service.Copy([folder], inner, Skip);
        await operation.Completion;
        Assert.AreEqual(FileOperationState.CompletedWithErrors, operation.State);
        Assert.AreEqual(FileOperationErrorKind.DestinationInsideSource, operation.Errors[0].Kind);
        Assert.IsFalse(Directory.Exists(Path.Combine(inner, "a")));
    }

    [TestMethod]
    public async Task Cancel_LeavesNoPartialFiles()
    {
        using var temp = new TempDirectory();
        var big = temp.Combine("big.bin");
        await using (var stream = File.Create(big))
            stream.SetLength(64L * 1024 * 1024);
        var destination = temp.CreateDirectory("dst");
        var (service, _) = Create(temp, options: new FileOperationOptions { CopyBufferSize = 64 * 1024 });

        var operation = service.Copy([big], destination, Skip);
        while (operation.GetProgress().ProcessedBytes == 0 && !operation.IsFinished)
            await Task.Delay(1);
        operation.Cancel();
        await operation.Completion;

        Assert.AreEqual(FileOperationState.Cancelled, operation.State);
        Assert.AreEqual(0, Directory.GetFiles(destination).Length, "no partial or truncated destination file");
    }

    [TestMethod]
    public async Task PauseAndResume()
    {
        using var temp = new TempDirectory();
        var big = temp.Combine("big.bin");
        await using (var stream = File.Create(big))
            stream.SetLength(32L * 1024 * 1024);
        var (service, _) = Create(temp, options: new FileOperationOptions { CopyBufferSize = 64 * 1024 });
        var operation = service.Copy([big], temp.CreateDirectory("dst"), Skip);
        while (operation.State != FileOperationState.Running && !operation.IsFinished)
            await Task.Delay(1);
        operation.Pause();
        var paused = operation.GetProgress().ProcessedBytes;
        await Task.Delay(150);
        Assert.IsTrue(operation.IsFinished || operation.GetProgress().ProcessedBytes - paused <= 64 * 1024, "at most one chunk after pausing");
        operation.Resume();
        await operation.Completion;
        Assert.AreEqual(FileOperationState.Completed, operation.State);
    }

    [TestMethod]
    public async Task DeletePermanently_RemovesTree_AndNeverFollowsLinks()
    {
        using var temp = new TempDirectory();
        var outside = temp.CreateFile("outside/keep.txt", "precious");
        temp.CreateFile("victim/a/b.txt");
        var link = temp.Combine("victim", "link");
        try
        {
            Directory.CreateSymbolicLink(link, temp.Combine("outside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links are not permitted for this user.");
        }

        var (service, _) = Create(temp);
        var operation = service.Delete([temp.Combine("victim")], permanently: true, Skip);
        await operation.Completion;
        Assert.IsFalse(Directory.Exists(temp.Combine("victim")));
        Assert.IsTrue(File.Exists(outside), "deleting a link must not delete its target's content");
    }

    [TestMethod]
    public async Task Copy_RecreatesSymbolicLinks()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/target.txt", "t");
        var link = temp.Combine("src", "loop");
        try
        {
            Directory.CreateSymbolicLink(link, temp.Combine("src")); // a link loop
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links are not permitted for this user.");
        }

        var (service, _) = Create(temp);
        var operation = service.Copy([temp.Combine("src")], temp.CreateDirectory("dst"), Skip);
        await operation.Completion;
        Assert.AreEqual(FileOperationState.Completed, operation.State);
        Assert.IsNotNull(new DirectoryInfo(temp.Combine("dst", "src", "loop")).LinkTarget, "copied as a link, not followed");
    }

    [TestMethod]
    public async Task Recycle_AndUndoRestores()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("work/doc.txt", "keep me");
        var (service, _) = Create(temp);
        var operation = service.Delete([file], permanently: false, Skip);
        await operation.Completion;
        await Task.Delay(50); // undo record is written by a continuation
        Assert.IsFalse(File.Exists(file));
        Assert.AreEqual(UndoOutcome.Done, await service.UndoAsync(Skip));
        Assert.AreEqual("keep me", File.ReadAllText(file));
    }

    [TestMethod]
    public async Task Rename_UndoRedo()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("before.txt");
        var (service, _) = Create(temp);
        var renamed = service.Rename(file, "after.txt");
        Assert.IsTrue(File.Exists(renamed));
        Assert.AreEqual(UndoOutcome.Done, await service.UndoAsync(Skip));
        Assert.IsTrue(File.Exists(file));
        Assert.AreEqual(UndoOutcome.Done, await service.RedoAsync(Skip));
        Assert.IsTrue(File.Exists(renamed));
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public async Task Move_Undo_MovesBack()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("a/x.txt");
        var (service, _) = Create(temp);
        await service.Move([file], temp.CreateDirectory("b"), Skip).Completion;
        await Task.Delay(50);
        Assert.AreEqual(UndoOutcome.Done, await service.UndoAsync(Skip));
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public void Rename_RefusesExistingAndInvalidNames()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("a.txt");
        temp.CreateFile("b.txt");
        var (service, _) = Create(temp);
        var exists = Assert.ThrowsExactly<OperationStepException>(() => service.Rename(file, "b.txt"));
        Assert.AreEqual(FileOperationErrorKind.AlreadyExists, exists.Kind);
        var invalid = Assert.ThrowsExactly<OperationStepException>(() => service.Rename(file, "x/y"));
        Assert.AreEqual(FileOperationErrorKind.InvalidName, invalid.Kind);
    }

    [TestMethod]
    public void Rename_CaseOnlyChange()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("readme.md", "content");
        var (service, _) = Create(temp);
        var renamed = service.Rename(file, "README.md");
        Assert.AreEqual("README.md", Path.GetFileName(renamed));
        Assert.AreEqual("README.md", Path.GetFileName(Directory.GetFiles(temp.Path).Single()));
        Assert.AreEqual("content", File.ReadAllText(renamed));
    }

    [TestMethod]
    public void Create_NeverTruncatesExistingFiles()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("note.txt", "important");
        var (service, _) = Create(temp);
        Assert.ThrowsExactly<OperationStepException>(() => service.CreateFile(temp.Path, "note.txt"));
        Assert.AreEqual("important", File.ReadAllText(temp.Combine("note.txt")));
        Assert.IsTrue(Directory.Exists(service.CreateFolder(temp.Path, "New folder")));
    }

    [TestMethod]
    public async Task Queue_RespectsConcurrencyLimit()
    {
        using var temp = new TempDirectory();
        var queue = new FileOperationQueue(maxConcurrency: 1);
        var running = 0;
        var maximum = 0;
        var operations = Enumerable.Range(0, 4).Select(_ => queue.Enqueue(FileOperationKind.Copy, [], null, Skip, async context =>
        {
            var now = Interlocked.Increment(ref running);
            maximum = Math.Max(maximum, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
        })).ToList();

        Assert.IsTrue(queue.HasActiveOperations);
        await Task.WhenAll(operations.Select(o => o.Completion));
        Assert.AreEqual(1, maximum);
        Assert.IsFalse(queue.HasActiveOperations);
        Assert.AreEqual(4, queue.History.Count);
        await queue.DisposeAsync();
    }

    [TestMethod]
    public async Task UnexpectedExceptions_FailOnlyTheOperation()
    {
        var queue = new FileOperationQueue();
        var operation = queue.Enqueue(FileOperationKind.Copy, [], null, Skip, _ => throw new InvalidOperationException("boom"));
        await operation.Completion;
        Assert.AreEqual(FileOperationState.Failed, operation.State);
        Assert.AreEqual("boom", operation.Failure?.Message);
        await queue.DisposeAsync();
    }
}
