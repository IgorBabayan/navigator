using Navigator.Core.Archives;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Presentation.Items;
using Navigator.Presentation.Localization;

namespace Navigator.UnitTests;

[TestClass]
public sealed class ItemMenuPlannerTests
{
    private static readonly ItemDisplayContext Display = new(new Localizer(), showExtensions: true);

    private static readonly ItemMenuOptions Writable = new(ShowOpenInNewWindow: true, ShowCopyPath: true, ShowOpenTerminal: true, CanModify: true, CanCreateInCurrentFolder: true);

    private static readonly ItemMenuOptions ReadOnly = new(ShowOpenInNewWindow: false, ShowCopyPath: true, ShowOpenTerminal: false, CanModify: false, CanCreateInCurrentFolder: false);

    private static FileItemViewModel File(string name) => new(new FileEntry(name, "/data/" + name, false, 10, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal), Display);

    private static FileItemViewModel Folder(string name) => new(new FileEntry(name, "/data/" + name, true, 0, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Directory), Display);

    /// <summary>Behaves like the Windows launcher: programs can be elevated, nothing else.</summary>
    private static ElevationOptions WindowsLike(string path)
        => Path.GetExtension(path) is ".exe" or ".msi" ? ElevationOptions.Administrator | ElevationOptions.OtherUser : ElevationOptions.None;

    private static SelectionTraits Traits(params FileItemViewModel[] items) => SelectionTraits.Collect(items, ArchiveService.IsSupportedArchive, WindowsLike);

    private static List<ItemMenuAction> TopLevel(ItemMenuLayout layout) => [.. layout.Entries.Select(e => e.Action)];

    private static ItemMenuNode Find(ItemMenuLayout layout, ItemMenuAction action) => layout.Entries.Single(e => e.Action == action);

    [TestMethod]
    public void Program_OffersElevation_InsteadOfOpenWith()
    {
        var actions = TopLevel(ItemMenuPlanner.Plan(Traits(File("setup.exe")), Writable));

        CollectionAssert.Contains(actions, ItemMenuAction.RunAsAdministrator);
        CollectionAssert.Contains(actions, ItemMenuAction.RunAsOtherUser);
        CollectionAssert.DoesNotContain(actions, ItemMenuAction.OpenWith);
        Assert.IsTrue(actions.IndexOf(ItemMenuAction.Open) < actions.IndexOf(ItemMenuAction.RunAsAdministrator));
    }

    [TestMethod]
    public void Document_OffersOpenWith_AndNoElevation()
    {
        var actions = TopLevel(ItemMenuPlanner.Plan(Traits(File("report.pdf")), Writable));

        CollectionAssert.Contains(actions, ItemMenuAction.OpenWith);
        CollectionAssert.DoesNotContain(actions, ItemMenuAction.RunAsAdministrator);
        CollectionAssert.DoesNotContain(actions, ItemMenuAction.OpenInNewTab);
        CollectionAssert.DoesNotContain(actions, ItemMenuAction.ExtractMenu);
    }

    [TestMethod]
    public void Folder_OpensInTabs_AcceptsPaste_AndCanBePinned()
    {
        var actions = TopLevel(ItemMenuPlanner.Plan(Traits(Folder("Projects")), Writable));

        CollectionAssert.Contains(actions, ItemMenuAction.OpenInNewTab);
        CollectionAssert.Contains(actions, ItemMenuAction.OpenInNewWindow);
        CollectionAssert.Contains(actions, ItemMenuAction.PasteIntoFolder);
        CollectionAssert.Contains(actions, ItemMenuAction.PinToSidebar);
        CollectionAssert.DoesNotContain(actions, ItemMenuAction.OpenWith);
    }

    [TestMethod]
    public void Archive_ExtractsHereOrIntoItsOwnFolder()
    {
        var single = Find(ItemMenuPlanner.Plan(Traits(File("photos.zip")), Writable), ItemMenuAction.ExtractMenu);
        CollectionAssert.AreEqual(new[] { ItemMenuAction.ExtractHere, ItemMenuAction.ExtractToFolder }, single.Children!.Select(c => c.Action).ToArray());

        // An archive mixed with other files only gets the smart "extract here" for the archive part.
        var mixed = Find(ItemMenuPlanner.Plan(Traits(File("photos.zip"), File("notes.txt")), Writable), ItemMenuAction.ExtractMenu);
        CollectionAssert.AreEqual(new[] { ItemMenuAction.ExtractHere }, mixed.Children!.Select(c => c.Action).ToArray());
    }

    [TestMethod]
    public void SeveralItems_CanBeCompressedTogetherOrEach()
    {
        var one = Find(ItemMenuPlanner.Plan(Traits(File("a.txt")), Writable), ItemMenuAction.CompressMenu);
        var many = Find(ItemMenuPlanner.Plan(Traits(File("a.txt"), Folder("b")), Writable), ItemMenuAction.CompressMenu);

        Assert.AreEqual(1, one.Children!.Count);
        CollectionAssert.AreEqual(new[] { ItemMenuAction.CompressTogether, ItemMenuAction.CompressEach }, many.Children!.Select(c => c.Action).ToArray());
    }

    [TestMethod]
    public void ReadOnlyLocation_HidesEverythingThatWrites()
    {
        var layout = ItemMenuPlanner.Plan(Traits(File("a.txt"), Folder("b")), ReadOnly);
        var everything = layout.Entries.SelectMany(e => (e.Children ?? []).Prepend(e)).Select(e => e.Action).ToList();

        CollectionAssert.AreEqual(new[] { ItemMenuAction.Copy, ItemMenuAction.Properties }, layout.QuickActions.ToArray());
        foreach (var writing in new[] { ItemMenuAction.CompressMenu, ItemMenuAction.CreateShortcut, ItemMenuAction.CreateFolderWithSelection, ItemMenuAction.PasteIntoFolder, ItemMenuAction.Duplicate, ItemMenuAction.DeletePermanently })
            CollectionAssert.DoesNotContain(everything, writing);
        CollectionAssert.Contains(everything, ItemMenuAction.CopyPath);
    }

    [TestMethod]
    public void Separators_AreNeverLeadingTrailingOrDoubled()
    {
        FileItemViewModel[][] selections =
        [
            [File("setup.exe")], [File("a.txt")], [Folder("x")], [File("p.zip")], [File("a.txt"), Folder("x"), File("p.tar.gz")],
        ];

        foreach (var options in new[] { Writable, ReadOnly, default })
        {
            foreach (var selection in selections)
            {
                var entries = ItemMenuPlanner.Plan(Traits(selection), options).Entries;
                Assert.IsFalse(entries[0].IsSeparator);
                Assert.IsFalse(entries[^1].IsSeparator);
                for (var i = 1; i < entries.Count; i++)
                    Assert.IsFalse(entries[i].IsSeparator && entries[i - 1].IsSeparator);
            }
        }
    }

    [TestMethod]
    public void EmptySelection_PlansNothing()
    {
        var layout = ItemMenuPlanner.Plan(Traits(), Writable);
        Assert.AreEqual(0, layout.Entries.Count);
        Assert.AreEqual(0, layout.QuickActions.Count);
    }

    [TestMethod]
    public void Traits_CountKinds_AndAskElevationOnlyForOneFile()
    {
        var asked = 0;
        ElevationOptions Counting(string path)
        {
            asked++;
            return WindowsLike(path);
        }

        var many = SelectionTraits.Collect([File("a.png"), File("b.png"), Folder("c"), File("d.zip")], ArchiveService.IsSupportedArchive, Counting);
        Assert.AreEqual(0, asked);
        Assert.AreEqual(4, many.Count);
        Assert.AreEqual(1, many.FolderCount);
        Assert.AreEqual(1, many.ArchiveCount);
        Assert.IsNull(many.SharedCategory);

        var pictures = SelectionTraits.Collect([File("a.png"), File("b.jpg")], ArchiveService.IsSupportedArchive, Counting);
        Assert.AreEqual(ItemCategory.Image, pictures.SharedCategory);

        var program = SelectionTraits.Collect([File("tool.exe")], ArchiveService.IsSupportedArchive, Counting);
        Assert.AreEqual(1, asked);
        Assert.IsTrue(program.IsElevatableProgram);

        var source = SelectionTraits.Collect([File("main.cs")], ArchiveService.IsSupportedArchive, Counting);
        Assert.IsTrue(source.IsEditableText);
        Assert.IsFalse(source.IsElevatableProgram);
    }

    [TestMethod]
    public void ArchiveStem_DropsArchiveSuffixesOnly()
    {
        Assert.AreEqual("photos", ArchiveService.ArchiveStem("/x/photos.tar.gz"));
        Assert.AreEqual("photos", ArchiveService.ArchiveStem("photos.zip"));
        Assert.AreEqual("notes.txt", ArchiveService.ArchiveStem("notes.txt"));
        Assert.AreEqual(".zip", ArchiveService.ArchiveStem(".zip"));
    }

    [TestMethod]
    public void ChooseFolderDestination_NeverReusesAnExistingName()
    {
        var root = Directory.CreateTempSubdirectory("nav-extract-").FullName;
        try
        {
            Assert.AreEqual(Path.Combine(root, "photos"), ArchiveService.ChooseFolderDestination(Path.Combine(root, "photos.zip"), root));
            Directory.CreateDirectory(Path.Combine(root, "photos"));
            Assert.AreNotEqual(Path.Combine(root, "photos"), ArchiveService.ChooseFolderDestination(Path.Combine(root, "photos.zip"), root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
