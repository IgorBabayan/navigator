using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Navigator.Core.Archives;
using Navigator.Core.Hashing;
using Navigator.Core.IO;
using Navigator.Core.Monitoring;
using Navigator.Core.Operations;
using Navigator.Core.Search;
using Navigator.Core.Settings;
using Navigator.Platform.Trash;
using Navigator.Tests;

namespace Navigator.IntegrationTests;

[TestClass]
public sealed class EnumerationTests
{
    [TestMethod]
    public async Task Batches_ContainEveryEntry_FirstBatchIsSmall()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 5000; i++)
            temp.CreateFile($"f{i}.txt", string.Empty);
        var batches = new List<IReadOnlyList<FileEntry>>();
        await foreach (var batch in DirectoryEnumerator.EnumerateBatchesAsync(temp.Path, EnumerationFilter.All))
            batches.Add(batch);
        Assert.AreEqual(5000, batches.Sum(b => b.Count));
        Assert.IsTrue(batches[0].Count <= DirectoryEnumerator.FirstBatchSize);
    }

    [TestMethod]
    public void HiddenFilter_HidesDotFilesUnlessAsked()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(".secret");
        temp.CreateFile("visible");
        Assert.AreEqual(1, DirectoryEnumerator.List(temp.Path, EnumerationFilter.Default).Count);
        Assert.AreEqual(2, DirectoryEnumerator.List(temp.Path, EnumerationFilter.All).Count);
    }

    [TestMethod]
    public async Task MissingFolder_ReportsNotFound()
    {
        var error = await Assert.ThrowsExactlyAsync<DirectoryEnumerationException>(async () =>
        {
            await foreach (var _ in DirectoryEnumerator.EnumerateBatchesAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), EnumerationFilter.All))
            {
            }
        });
        Assert.AreEqual(EnumerationErrorKind.NotFound, error.Kind);
    }

    [TestMethod]
    public async Task Cancellation_StopsEnumeration()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 3000; i++)
            temp.CreateFile($"f{i}", string.Empty);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in DirectoryEnumerator.EnumerateBatchesAsync(temp.Path, EnumerationFilter.All, cancel.Token))
                cancel.Cancel();
        });
    }

    [TestMethod]
    public void FolderStatistics_DoNotFollowLinks()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("d/a.bin", new string('x', 100));
        temp.CreateFile("d/sub/b.bin", new string('y', 50));
        try
        {
            Directory.CreateSymbolicLink(temp.Combine("d", "loop"), temp.Combine("d"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var statistics = FolderStatisticsCalculator.Calculate(temp.Combine("d"));
        Assert.AreEqual(150, statistics.TotalBytes);
        Assert.AreEqual(2, statistics.FileCount);
    }
}

[TestClass]
public sealed class MonitorTests
{
    [TestMethod]
    public async Task ReportsCoalescedChanges()
    {
        using var temp = new TempDirectory();
        var batches = new List<DirectoryChangeBatch>();
        var signal = new SemaphoreSlim(0);
        using var monitor = new DirectoryMonitor(temp.Path, b =>
        {
            lock (batches)
                batches.Add(b);
            signal.Release();
        }, TimeSpan.FromMilliseconds(50));

        temp.CreateFile("new.txt");
        var deleted = temp.CreateFile("temp.txt");
        File.Delete(deleted);
        Assert.IsTrue(await signal.WaitAsync(TimeSpan.FromSeconds(5)), "a batch arrives");
        await Task.Delay(300);

        List<string> created;
        lock (batches)
            created = [.. batches.SelectMany(b => b.Created)];
        CollectionAssert.Contains(created, "new.txt");
    }

    [TestMethod]
    public void Coalescing_Rules()
    {
        var pending = new Dictionary<string, ChangeKind>();
        DirectoryMonitor.Coalesce(pending, ChangeKind.Created, "a");
        DirectoryMonitor.Coalesce(pending, ChangeKind.Changed, "a");
        Assert.AreEqual(ChangeKind.Created, pending["a"]);
        DirectoryMonitor.Coalesce(pending, ChangeKind.Deleted, "a");
        Assert.IsFalse(pending.ContainsKey("a"));
        DirectoryMonitor.Coalesce(pending, ChangeKind.Deleted, "b");
        DirectoryMonitor.Coalesce(pending, ChangeKind.Created, "b");
        Assert.AreEqual(ChangeKind.Changed, pending["b"]);
    }
}

[TestClass]
public sealed class SearchTests
{
    private static async Task<List<string>> Run(SearchQuery query, CancellationToken token = default)
    {
        var names = new List<string>();
        await foreach (var batch in FileSearchEngine.SearchAsync(query, token))
            names.AddRange(batch.Select(e => e.Name));
        return names;
    }

    [TestMethod]
    public async Task FindsByNamePatternExtensionAndContent()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("docs/Report-2026.docx");
        temp.CreateFile("docs/deep/report-old.txt", "contains the NEEDLE text");
        temp.CreateFile("img/photo.jpg");
        temp.CreateFile(".hidden/report.txt");

        CollectionAssert.AreEquivalent(new[] { "Report-2026.docx", "report-old.txt" }, await Run(new SearchQuery { Root = temp.Path, Text = "report" }));
        CollectionAssert.AreEquivalent(new[] { "photo.jpg" }, await Run(new SearchQuery { Root = temp.Path, Text = "*.JPG" }));
        CollectionAssert.AreEquivalent(new[] { "report-old.txt" }, await Run(new SearchQuery { Root = temp.Path, Extensions = new HashSet<string>([".txt"], StringComparer.OrdinalIgnoreCase) }));
        CollectionAssert.AreEquivalent(new[] { "report-old.txt" }, await Run(new SearchQuery { Root = temp.Path, ContentText = "needle" }));
        Assert.AreEqual(0, (await Run(new SearchQuery { Root = temp.Path, Text = "report", Recursive = false })).Count);
        Assert.AreEqual(3, (await Run(new SearchQuery { Root = temp.Path, Text = "report", IncludeHidden = true })).Count);
    }

    [TestMethod]
    public async Task ContentSearch_FindsMatchAcrossBufferBoundary()
    {
        using var temp = new TempDirectory();
        var text = new string('a', (64 * 1024) - 3) + "boundary" + new string('b', 100);
        temp.CreateFile("big.txt", text);
        CollectionAssert.AreEquivalent(new[] { "big.txt" }, await Run(new SearchQuery { Root = temp.Path, ContentText = "boundary" }));
    }

    [TestMethod]
    public async Task RespectsMaxResultsAndCancellation()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 300; i++)
            temp.CreateFile($"match{i}.txt", string.Empty);
        Assert.AreEqual(50, (await Run(new SearchQuery { Root = temp.Path, Text = "match", MaxResults = 50 })).Count);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(new SearchQuery { Root = temp.Path }, cancel.Token));
    }
}

[TestClass]
public sealed class ArchiveTests
{
    [TestMethod]
    public async Task Zip_CreateAndExtract_RoundTrips()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/a.txt", "alpha");
        temp.CreateFile("src/sub/b.txt", "beta");
        var zip = temp.Combine("out.zip");

        await OperationContextHarness.Run(c => ArchiveService.CreateZipAsync([temp.Combine("src")], zip, CompressionLevel.Fastest, c));
        Assert.IsTrue(File.Exists(zip));

        var destination = ArchiveService.ChooseSmartDestination(zip, temp.Path);
        Assert.IsTrue(PathUtil.AreEqual(temp.Path, destination), "single top-level folder extracts in place");
        var extractTo = temp.CreateDirectory("extracted");
        await OperationContextHarness.Run(c => ArchiveService.ExtractAsync(zip, extractTo, c));
        Assert.AreEqual("beta", File.ReadAllText(Path.Combine(extractTo, "src", "sub", "b.txt")));
    }

    [TestMethod]
    public async Task Zip_PathTraversalIsBlocked()
    {
        using var temp = new TempDirectory();
        var zip = temp.Combine("evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using (var stream = archive.CreateEntry("../../escaped.txt").Open())
                await stream.WriteAsync(Encoding.UTF8.GetBytes("pwned"));
            await using (var ok = archive.CreateEntry("fine.txt").Open())
                await ok.WriteAsync(Encoding.UTF8.GetBytes("ok"));
        }

        var destination = temp.CreateDirectory("x/y");
        var operation = await OperationContextHarness.Run(c => ArchiveService.ExtractAsync(zip, destination, c));
        Assert.IsFalse(File.Exists(temp.Combine("escaped.txt")));
        Assert.IsFalse(File.Exists(temp.Combine("x", "escaped.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(destination, "fine.txt")));
        Assert.AreEqual(FileOperationErrorKind.InvalidName, operation.Errors.Single().Kind);
    }

    [TestMethod]
    public void SmartDestination_MultipleRootsGetAFolder()
    {
        using var temp = new TempDirectory();
        var zip = temp.Combine("pack.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("a.txt");
            archive.CreateEntry("b.txt");
        }

        Assert.AreEqual(temp.Combine("pack"), ArchiveService.ChooseSmartDestination(zip, temp.Path));
    }

    /// <summary>Runs an operation body through a real queue so contexts, progress and errors behave as in the app.</summary>
    private static class OperationContextHarness
    {
        public static async Task<FileOperation> Run(Func<OperationContext, Task> body)
        {
            await using var queue = new FileOperationQueue();
            var operation = queue.Enqueue(FileOperationKind.ExtractArchive, [], null, PolicyInteraction.SkipConflictsAndErrors, body);
            await operation.Completion;
            if (operation.Failure is { } failure)
                throw new AssertFailedException(failure.ToString());
            return operation;
        }
    }
}

[TestClass]
public sealed class StorageTests
{
    [TestMethod]
    public async Task Hashes_MatchKnownVectors()
    {
        var data = new MemoryStream("abc"u8.ToArray());
        Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await FileHasher.ComputeAsync(data, HashKind.Sha256));
        data.Position = 0;
        Assert.AreEqual("900150983cd24fb0d6963f7d28e17f72", await FileHasher.ComputeAsync(data, HashKind.Md5));
        Assert.IsTrue(FileHasher.DigestsEqual("ABCD", "abcd"));
        Assert.IsFalse(FileHasher.DigestsEqual("abcd", "abce"));
    }

    [TestMethod]
    public void AtomicFile_KeepsBackupAndLeavesNoTemporaries()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("s.json");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");
        Assert.AreEqual("two", File.ReadAllText(path));
        Assert.AreEqual("one", File.ReadAllText(path + ".bak"));
        Assert.AreEqual(2, Directory.GetFiles(temp.Path).Length);
    }

    [TestMethod]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("freebsd")]
    public async Task FreedesktopTrash_TrashListRestoreDelete()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            Assert.Inconclusive("Freedesktop trash is Linux/BSD-only.");
            return;
        }

        using var temp = new TempDirectory();
        var trashRoot = temp.Combine("home", ".local", "share", "Trash");
        var trash = new FreedesktopTrash(trashRoot, 1000, new FakeVolumeLocator(sameVolume: true), () => []);
        var file = temp.CreateFile("work/My Report.txt", "hello");
        var duplicate = temp.CreateFile("other/My Report.txt", "second");
        Assert.IsFalse(await trash.HasItemsAsync(), "a trash that does not exist yet is empty");

        var first = await trash.MoveToTrashAsync(file);
        var second = await trash.MoveToTrashAsync(duplicate);
        Assert.IsFalse(File.Exists(file));
        Assert.IsTrue(File.Exists(Path.Combine(trashRoot, "info", "My Report.txt.trashinfo")));
        Assert.AreNotEqual(first!.StoragePath, second!.StoragePath, "name collisions inside the trash get unique names");

        Assert.IsTrue(await trash.HasItemsAsync());

        var items = await trash.ListAsync();
        Assert.AreEqual(2, items.Count);
        Assert.IsTrue(items.Any(i => PathUtil.AreEqual(i.OriginalPath, file)));

        await trash.RestoreAsync(items.First(i => PathUtil.AreEqual(i.OriginalPath, file)));
        Assert.AreEqual("hello", File.ReadAllText(file));

        temp.CreateFile("other/My Report.txt", "occupied");
        await Assert.ThrowsExactlyAsync<IOException>(() => trash.RestoreAsync(second));

        await trash.EmptyAsync();
        Assert.AreEqual(0, (await trash.ListAsync()).Count);
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(trashRoot, "files")).Length);
        Assert.IsFalse(await trash.HasItemsAsync(), "an emptied trash disables \"Empty trash\"");
    }
}
