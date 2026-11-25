using Microsoft.Extensions.Logging.Abstractions;
using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Presentation.Panes;
using Navigator.Presentation.Settings;
using Navigator.Presentation.Shell;
using Navigator.Tests;

namespace Navigator.UnitTests;

[TestClass]
public sealed class DropPlannerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "navigator-drop");
    private static readonly string Source = Path.Combine(Root, "source");
    private static readonly string Target = Path.Combine(Root, "target");

    private static string In(string folder, string name) => Path.Combine(folder, name);

    [TestMethod]
    public void SameVolumeMovesAndOtherVolumeCopies()
    {
        string[] items = [In(Source, "a.txt"), In(Source, "b")];
        Assert.AreEqual(DropAction.Move, DropPlanner.Plan(items, Target, false, false, new FakeVolumeLocator(sameVolume: true)));
        Assert.AreEqual(DropAction.Copy, DropPlanner.Plan(items, Target, false, false, new FakeVolumeLocator(sameVolume: false)));
    }

    [TestMethod]
    public void ModifiersOverrideTheDefault()
    {
        string[] items = [In(Source, "a.txt")];
        Assert.AreEqual(DropAction.Copy, DropPlanner.Plan(items, Target, controlPressed: true, false, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.Move, DropPlanner.Plan(items, Target, false, shiftPressed: true, new FakeVolumeLocator(false)));
    }

    [TestMethod]
    public void FolderIsNeverDroppedIntoItselfOrItsSubfolder()
    {
        var folder = In(Source, "folder");
        Assert.AreEqual(DropAction.None, DropPlanner.Plan([folder], folder, false, false, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.None, DropPlanner.Plan([folder], In(folder, "child"), true, false, new FakeVolumeLocator(true)));
        // A sibling whose name starts the same is not a subfolder.
        Assert.AreEqual(DropAction.Move, DropPlanner.Plan([folder], folder + "2", false, false, new FakeVolumeLocator(true)));
    }

    [TestMethod]
    public void DropOnTheFolderTheItemsAreInDoesNothingUnlessCopyIsForced()
    {
        string[] items = [In(Source, "a.txt"), In(Source, "b.txt")];
        Assert.AreEqual(DropAction.None, DropPlanner.Plan(items, Source, false, false, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.None, DropPlanner.Plan(items, Source + Path.DirectorySeparatorChar, false, true, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.Copy, DropPlanner.Plan(items, Source, true, false, new FakeVolumeLocator(true)));
    }

    [TestMethod]
    public void ServerItemsAreRefusedAndLocalItemsAreUploaded()
    {
        const string server = "sftp://host/home/user";
        Assert.AreEqual(DropAction.None, DropPlanner.Plan([server + "/a.txt"], Target, false, false, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.None, DropPlanner.Plan([server + "/a.txt"], server + "/b", false, false, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.Copy, DropPlanner.Plan([In(Source, "a.txt")], server, false, true, new FakeVolumeLocator(true)));
    }

    [TestMethod]
    public void UnknownSourcesCopyUnlessShiftIsHeld()
    {
        Assert.AreEqual(DropAction.Copy, DropPlanner.Plan(null, Target, false, false, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.Move, DropPlanner.Plan(null, Target, false, true, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.Copy, DropPlanner.Plan(null, Target, true, true, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.Copy, DropPlanner.Plan(null, "sftp://host/x", false, true, new FakeVolumeLocator(true)));
    }

    [TestMethod]
    public void NothingToDropOrNowhereToDropIt()
    {
        Assert.AreEqual(DropAction.None, DropPlanner.Plan([], Target, false, false, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.None, DropPlanner.Plan([In(Source, "a.txt")], null, false, false, new FakeVolumeLocator(true)));
        Assert.AreEqual(DropAction.None, DropPlanner.Plan(null, string.Empty, false, false, new FakeVolumeLocator(true)));
    }
}

[TestClass]
public sealed class PinnedFoldersDropTests
{
    private sealed class NoKnownFolders : IKnownFolderProvider
    {
        public string? GetPath(KnownFolder folder) => null;
    }

    private static (PinnedFolders Pins, SettingsService Settings) Create(TempDirectory temp, params string[] initial)
    {
        var settings = new SettingsService(new SettingsStore(temp.Combine("settings.json")), NullLogger<SettingsService>.Instance);
        settings.Update("sidebar", s => s.Sidebar.PinnedFolders = [.. initial]);
        return (new PinnedFolders(settings, new NoKnownFolders()), settings);
    }

    [TestMethod]
    public void PinsNewFoldersAtTheDropPosition()
    {
        using var temp = new TempDirectory();
        var (pins, settings) = Create(temp, temp.Combine("a"), temp.Combine("b"));
        using (settings)
        {
            pins.PinAt([temp.Combine("x"), temp.Combine("y")], 1);
            CollectionAssert.AreEqual(new[] { temp.Combine("a"), temp.Combine("x"), temp.Combine("y"), temp.Combine("b") }, pins.Paths.ToArray());
        }
    }

    [TestMethod]
    public void AlreadyPinnedFoldersMoveInsteadOfDuplicating()
    {
        using var temp = new TempDirectory();
        var (pins, settings) = Create(temp, temp.Combine("a"), temp.Combine("b"), temp.Combine("c"));
        using (settings)
        {
            // Dropping "a" (index 0) after "b" (insert index 2) lands it between "b" and "c".
            pins.PinAt([temp.Combine("a") + Path.DirectorySeparatorChar], 2);
            CollectionAssert.AreEqual(new[] { temp.Combine("b"), temp.Combine("a"), temp.Combine("c") }, pins.Paths.ToArray());

            pins.PinAt([temp.Combine("c"), temp.Combine("c"), temp.Combine("d")], 0);
            CollectionAssert.AreEqual(new[] { temp.Combine("c"), temp.Combine("d"), temp.Combine("b"), temp.Combine("a") }, pins.Paths.ToArray());
        }
    }

    [TestMethod]
    public void IndexBeyondTheListAppends()
    {
        using var temp = new TempDirectory();
        var (pins, settings) = Create(temp, temp.Combine("a"));
        using (settings)
        {
            pins.PinAt([temp.Combine("z")], 99);
            CollectionAssert.AreEqual(new[] { temp.Combine("a"), temp.Combine("z") }, pins.Paths.ToArray());
        }
    }
}
