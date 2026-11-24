using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Panes;
using Navigator.Tests;

namespace Navigator.UnitTests;

[TestClass]
public sealed class AddressCompletionTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    [TestMethod]
    public void Split_SeparatesFolderAndPartialName()
    {
        Assert.AreEqual(new AddressInput($"{Sep}home{Sep}", "Doc"), AddressCompletion.Split($"{Sep}home{Sep}Doc"));
        Assert.AreEqual(new AddressInput("~/", "Pi"), AddressCompletion.Split("~/Pi"));
        Assert.AreEqual(new AddressInput($"{Sep}", string.Empty), AddressCompletion.Split($"{Sep}"));
        Assert.AreEqual(new AddressInput(string.Empty, "Down"), AddressCompletion.Split("Down"));
        Assert.AreEqual(new AddressInput("a/b/", "c"), AddressCompletion.Split("  a/b/c"));
    }

    [TestMethod]
    public void ResolveParent_HandlesHomeRelativeAndBareNames()
    {
        using var temp = new TempDirectory();
        var sub = temp.CreateDirectory("sub");

        Assert.AreEqual(temp.Path, AddressCompletion.ResolveParent(new AddressInput(string.Empty, "x"), "/unused", temp.Path));
        Assert.AreEqual(sub, AddressCompletion.ResolveParent(new AddressInput($"sub{Sep}", "x"), "/unused", temp.Path));
        Assert.AreEqual(sub, AddressCompletion.ResolveParent(new AddressInput($"~{Sep}sub{Sep}", "x"), temp.Path, currentDirectory: null));
        Assert.IsNull(AddressCompletion.ResolveParent(new AddressInput($"rel{Sep}", "x"), temp.Path, currentDirectory: null));
    }

    [TestMethod]
    public void FindFolders_MatchesPrefixCaseInsensitivelyInNaturalOrder()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("Docs10");
        temp.CreateDirectory("docs2");
        temp.CreateDirectory("Music");
        temp.CreateFile("Docs-file.txt");

        CollectionAssert.AreEqual(new[] { "docs2", "Docs10" }, AddressCompletion.FindFolders(temp.Path, "DOC", includeHidden: false, maxResults: 10).ToArray());
        CollectionAssert.AreEqual(new[] { "docs2", "Docs10", "Music" }, AddressCompletion.FindFolders(temp.Path, string.Empty, includeHidden: false, maxResults: 10).ToArray());
        Assert.AreEqual(1, AddressCompletion.FindFolders(temp.Path, string.Empty, includeHidden: false, maxResults: 1).Count);
    }

    [TestMethod]
    public void FindFolders_SkipsHiddenUnlessAskedAndToleratesMissingFolders()
    {
        using var temp = new TempDirectory();
        var hidden = temp.CreateDirectory(".secret");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);

        Assert.AreEqual(0, AddressCompletion.FindFolders(temp.Path, string.Empty, includeHidden: false, maxResults: 10).Count);
        Assert.AreEqual(1, AddressCompletion.FindFolders(temp.Path, string.Empty, includeHidden: true, maxResults: 10).Count);
        Assert.AreEqual(0, AddressCompletion.FindFolders(temp.Combine("missing"), string.Empty, includeHidden: true, maxResults: 10).Count);
    }

    [TestMethod]
    public void Remember_KeepsMostRecentFirstWithoutDuplicatesAndBounded()
    {
        var history = new List<string>();
        AddressCompletion.Remember(history, "/a", capacity: 3);
        AddressCompletion.Remember(history, "/b", capacity: 3);
        AddressCompletion.Remember(history, "/c", capacity: 3);
        AddressCompletion.Remember(history, "/a", capacity: 3);
        AddressCompletion.Remember(history, "/d", capacity: 3);
        AddressCompletion.Remember(history, "  ", capacity: 3);

        CollectionAssert.AreEqual(new[] { "/d", "/a", "/c" }, history);
    }
}

[TestClass]
public sealed class AddressAliasTests
{
    private sealed class FakeKnownFolders(string root) : IKnownFolderProvider
    {
        public string? GetPath(KnownFolder folder) => folder == KnownFolder.Music ? null : Path.Combine(root, folder.ToString());
    }

    [TestMethod]
    public void Resolve_AcceptsInvariantShellAndLocalizedNames()
    {
        var localizer = new Localizer();
        var folders = new FakeKnownFolders(Path.Combine(Path.GetTempPath(), "user"));

        Assert.AreEqual(Location.Folder(folders.GetPath(KnownFolder.Downloads)!), AddressAliases.Resolve("downloads", localizer, folders));
        Assert.AreEqual(Location.Folder(folders.GetPath(KnownFolder.Desktop)!), AddressAliases.Resolve("shell:Desktop", localizer, folders));
        Assert.AreEqual(Location.Folder(folders.GetPath(KnownFolder.Documents)!), AddressAliases.Resolve("shell:Personal", localizer, folders));
        Assert.AreEqual(Location.Trash, AddressAliases.Resolve("Recycle Bin", localizer, folders));
        Assert.AreEqual(Location.Trash, AddressAliases.Resolve("shell:RecycleBinFolder", localizer, folders));
        Assert.AreEqual(Location.Home, AddressAliases.Resolve("Home", localizer, folders));
        Assert.AreEqual(Location.Folder(folders.GetPath(KnownFolder.Home)!), AddressAliases.Resolve(localizer["KnownFolder.Home"], localizer, folders));
    }

    [TestMethod]
    public void Resolve_RejectsUnknownNamesAndMissingFolders()
    {
        var localizer = new Localizer();
        var folders = new FakeKnownFolders(Path.GetTempPath());

        Assert.IsNull(AddressAliases.Resolve("not-a-place", localizer, folders));
        Assert.IsNull(AddressAliases.Resolve("Music", localizer, folders));
        Assert.IsNull(AddressAliases.Resolve("shell:", localizer, folders));
    }

    [TestMethod]
    public void All_ListsOnlyExistingLocations()
    {
        var aliases = AddressAliases.All(new Localizer(), new FakeKnownFolders(Path.GetTempPath()));

        Assert.AreEqual(Location.Home, aliases[0].Location);
        Assert.AreEqual(Location.Trash, aliases[^1].Location);
        Assert.IsFalse(aliases.Any(a => a.Name == new Localizer()["KnownFolder.Music"]));
    }
}
