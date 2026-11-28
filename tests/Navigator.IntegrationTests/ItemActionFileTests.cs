using Navigator.Core.Archives;
using Navigator.Core.Operations;
using Navigator.Tests;

namespace Navigator.IntegrationTests;

[TestClass]
public sealed class ArchiveReaderToolTests
{
    /// <summary>Builds a 7z with whichever program the machine has (the same ones Navigator uses); null when there is none.</summary>
    private static async Task<string?> MakeSevenZipAsync(TempDirectory temp)
    {
        if (!ArchiveReaderTool.CanRead(ArchiveFormat.SevenZip) || !SevenZipTool.IsAvailable)
            return null;
        temp.CreateFile("pack/a.txt", "alpha");
        temp.CreateFile("pack/sub/b.txt", "beta");
        temp.CreateFile("loose.txt", "gamma");
        var archive = temp.Combine("t.7z");
        await using var queue = new FileOperationQueue();
        var operation = queue.Enqueue(FileOperationKind.CreateArchive, [], archive, PolicyInteraction.SkipConflictsAndErrors,
            c => ArchiveCreation.CreateAsync([temp.Combine("pack"), temp.Combine("loose.txt")], archive, ArchiveFormat.SevenZip, System.IO.Compression.CompressionLevel.Fastest, c));
        await operation.Completion;
        return File.Exists(archive) ? archive : null;
    }

    [TestMethod]
    public async Task SevenZip_ListsAndExtracts_WithConflictHandling()
    {
        using var temp = new TempDirectory();
        if (await MakeSevenZipAsync(temp) is not { } archive)
        {
            Assert.Inconclusive("No 7-Zip or libarchive program on this machine.");
            return;
        }

        Assert.IsTrue(ArchiveService.IsSupportedArchive(archive));
        Assert.AreEqual(temp.Combine("t"), ArchiveService.ChooseSmartDestination(archive, temp.Path), "two top-level items get their own folder");

        var target = temp.CreateDirectory("out");
        await using (var queue = new FileOperationQueue())
        {
            var first = queue.Enqueue(FileOperationKind.ExtractArchive, [archive], target, PolicyInteraction.SkipConflictsAndErrors, c => ArchiveService.ExtractAsync(archive, target, c));
            await first.Completion;
            Assert.AreEqual(FileOperationState.Completed, first.State, first.Failure?.Message);
        }

        Assert.AreEqual("beta", File.ReadAllText(Path.Combine(target, "pack", "sub", "b.txt")));
        Assert.AreEqual("gamma", File.ReadAllText(Path.Combine(target, "loose.txt")));

        // Again with "keep both": nothing is overwritten, copies get a suffix, no staging folder is left behind.
        File.WriteAllText(Path.Combine(target, "loose.txt"), "mine");
        await using (var queue = new FileOperationQueue())
        {
            var second = queue.Enqueue(FileOperationKind.ExtractArchive, [archive], target, new PolicyInteraction(ConflictResolution.KeepBoth, ErrorResolution.Skip),
                c => ArchiveService.ExtractAsync(archive, target, c));
            await second.Completion;
        }

        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(target, "loose.txt")));
        Assert.AreEqual("gamma", File.ReadAllText(Path.Combine(target, "loose (2).txt")));
        Assert.IsFalse(Directory.EnumerateDirectories(target, ".navigator-extract-*").Any());
    }

    [TestMethod]
    public void Rar_IsOfferedOnlyWithAReader()
    {
        Assert.AreEqual(ArchiveReaderTool.CanRead(ArchiveFormat.Rar), ArchiveService.IsSupportedArchive("/x/photos.rar"));
        Assert.IsTrue(ArchiveService.IsSupportedArchive("/x/photos.zip"));
        Assert.IsFalse(ArchiveService.IsSupportedArchive("/x/photos.cab"));
    }
}
