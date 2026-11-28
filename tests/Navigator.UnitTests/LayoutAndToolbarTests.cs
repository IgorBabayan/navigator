using Navigator.Core.Navigation;
using Navigator.Core.Settings;
using Navigator.Presentation.Localization;
using Navigator.Presentation.SettingsPages;
using Navigator.Presentation.Shell;

namespace Navigator.UnitTests;

[TestClass]
public sealed class ColumnTrailTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "navigator-trail");

    private static string Under(params string[] names) => Path.Combine([Root, .. names]);

    [TestMethod]
    public void Build_ListsEveryFolderFromTheAnchorToTheParentOfTheCurrentOne()
    {
        var trail = ColumnTrail.Build(Root, Under("a", "b", "c"));

        Assert.AreEqual(3, trail.Count);
        Assert.AreEqual(new TrailColumn(Root, Under("a")), trail[0]);
        Assert.AreEqual(new TrailColumn(Under("a"), Under("a", "b")), trail[1]);
        Assert.AreEqual(new TrailColumn(Under("a", "b"), Under("a", "b", "c")), trail[2]);
    }

    [TestMethod]
    public void Build_IsEmptyAtTheAnchorAndOutsideIt()
    {
        Assert.AreEqual(0, ColumnTrail.Build(Root, Root).Count);
        Assert.AreEqual(0, ColumnTrail.Build(Under("a"), Root).Count);
        Assert.AreEqual(0, ColumnTrail.Build(Under("a"), Under("ab", "c")).Count, "a sibling sharing a name prefix is not inside");
    }

    [TestMethod]
    public void Build_KeepsOnlyTheDeepestColumnsOfALongTrail()
    {
        var deep = Under([.. Enumerable.Range(0, ColumnTrail.MaxColumns + 6).Select(i => "d" + i)]);
        var trail = ColumnTrail.Build(Root, deep);

        Assert.AreEqual(ColumnTrail.MaxColumns, trail.Count);
        Assert.AreEqual(deep, trail[^1].OpenChild);
        Assert.AreEqual(Path.GetDirectoryName(deep), trail[^1].Folder);
    }

    [TestMethod]
    public void NextAnchor_StaysInsideMovesUpAndRestartsElsewhere()
    {
        Assert.AreEqual(Under("a"), ColumnTrail.NextAnchor(Under("a"), Under("a", "b")));
        Assert.AreEqual(Root, ColumnTrail.NextAnchor(Under("a"), Root));
        Assert.AreEqual(Under("x"), ColumnTrail.NextAnchor(Under("a"), Under("x")));
        Assert.AreEqual(Under("x"), ColumnTrail.NextAnchor(null, Under("x")));
    }

    [TestMethod]
    public void Continues_CoversDescendantsAndAncestorsOnly()
    {
        Assert.IsTrue(ColumnTrail.Continues(Under("a"), Under("a", "b")));
        Assert.IsTrue(ColumnTrail.Continues(Under("a"), Root));
        Assert.IsFalse(ColumnTrail.Continues(Under("a"), Under("x")));
        Assert.IsFalse(ColumnTrail.Continues(null, Under("a")));
    }
}

[TestClass]
public sealed class ColumnOrderTests
{
    [TestMethod]
    public void Normalize_PutsNameFirstDropsUnknownAndAppendsMissing()
    {
        var order = ColumnOrder.Normalize(["size", "bogus", "name", "size", "tags", null]);

        CollectionAssert.AreEqual(new[] { "name", "size", "tags", "modified", "type", "created" }, order.ToArray());
    }

    [TestMethod]
    public void Normalize_OfNothingIsTheDefaultOrder()
        => CollectionAssert.AreEqual(ColumnOrder.Known.ToArray(), ColumnOrder.Normalize(null).ToArray());

    [TestMethod]
    public void Move_KeepsTheNameColumnFirst()
    {
        var order = ColumnOrder.Known;

        CollectionAssert.AreEqual(new[] { "name", "size", "modified", "type", "created", "tags" }, ColumnOrder.Move(order, "size", 0).ToArray());
        CollectionAssert.AreEqual(new[] { "name", "type", "size", "created", "tags", "modified" }, ColumnOrder.Move(order, "modified", 99).ToArray());
        Assert.AreSame(order, ColumnOrder.Move(order, "name", 3), "the name column does not move");
        Assert.AreSame(order, ColumnOrder.Move(order, "unknown", 2));
    }

    [TestMethod]
    public void DefaultColumnSettings_FollowTheKnownOrder()
        => CollectionAssert.AreEqual(ColumnOrder.Known.ToArray(), new FilesAndFoldersSettings().Columns.Select(c => c.Id).ToArray());
}

[TestClass]
public sealed class ToolbarLayoutTests
{
    [TestMethod]
    public void Resolve_OfNothingIsTheCatalogWithItsDefaults()
    {
        var slots = ToolbarLayout.Resolve([]);

        CollectionAssert.AreEqual(ToolbarLayout.Catalog.Select(i => i.Id).ToArray(), slots.Select(s => s.Item.Id).ToArray());
        Assert.IsFalse(slots.Single(s => s.Item.Id == "alwaysOnTop").IsVisible);
        Assert.IsTrue(slots.Single(s => s.Item.Id == "compactOverlay").IsVisible);
    }

    [TestMethod]
    public void Resolve_KeepsTheStoredOrderAndPlacesNewButtonsAfterTheirDefaultNeighbour()
    {
        // A file written before "compactOverlay" and "alwaysOnTop" existed, with the view buttons rearranged.
        var stored = new List<ToolbarItemSetting>
        {
            new() { Id = "settings" },
            new() { Id = "copy", IsVisible = false },
            new() { Id = "unknown" },
            new() { Id = "previewPane" },
            new() { Id = "copy" },
        };

        var slots = ToolbarLayout.Resolve(stored);
        var view = slots.Where(s => s.Item.Group == ToolbarGroup.View).Select(s => s.Item.Id).ToList();
        var commands = slots.Where(s => s.Item.Group == ToolbarGroup.Commands).ToList();

        Assert.AreEqual(ToolbarLayout.Catalog.Count, slots.Count, "every button exactly once");
        Assert.IsFalse(commands.Single(s => s.Item.Id == "copy").IsVisible, "the first entry for an id wins");
        Assert.AreEqual(commands.FindIndex(s => s.Item.Id == "copy") + 1, commands.FindIndex(s => s.Item.Id == "paste"));
        Assert.IsTrue(view.IndexOf("settings") < view.IndexOf("previewPane"), "stored order wins");
        Assert.AreEqual(view.IndexOf("previewPane") + 1, view.IndexOf("compactOverlay"), "a new button follows its default predecessor");
    }

    [TestMethod]
    public void ToSettings_RoundTrips()
    {
        var slots = ToolbarLayout.Resolve([new() { Id = "more", IsVisible = false }]);
        var again = ToolbarLayout.Resolve(ToolbarLayout.ToSettings(slots));

        CollectionAssert.AreEqual(slots.ToArray(), again.ToArray());
    }

    [TestMethod]
    public void EveryToolbarEntryHasAName()
    {
        var localizer = new Localizer();
        foreach (var item in ToolbarLayout.Catalog)
            Assert.IsTrue(localizer.HasKey(item.NameKey), item.NameKey);
    }

    [TestMethod]
    public void Sanitize_DropsEmptyToolbarEntries()
    {
        var settings = new AppSettings();
        settings.Appearance.ToolbarItems = [new() { Id = " " }, new() { Id = "copy" }];
        SettingsStore.Sanitize(settings);

        Assert.AreEqual(1, settings.Appearance.ToolbarItems.Count);
        Assert.AreEqual("copy", settings.Appearance.ToolbarItems[0].Id);
    }
}

[TestClass]
public sealed class SettingsSearchTests
{
    private static readonly Localizer Text = new();

    [TestMethod]
    public void EverySearchableSettingHasItsStrings()
    {
        foreach (var entry in SettingsSearch.Entries)
        {
            Assert.IsTrue(Text.HasKey(entry.TitleKey), entry.TitleKey);
            if (entry.DescriptionKey is { } description)
                Assert.IsTrue(Text.HasKey(description), description);
            if (entry.PageTitleKey is { } section)
                Assert.IsTrue(Text.HasKey(section), section);
            Assert.AreNotEqual(entry.PageId, SettingsSearch.PageTitle(entry.PageId, Text), entry.PageId);
        }
    }

    [TestMethod]
    public void Find_MatchesWordsOfTheNameFirst()
    {
        var results = SettingsSearch.Find("hidden", Text, 5);

        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual("Settings.ShowHiddenItems", results[0].TitleKey);
        Assert.AreEqual("files", results[0].PageId);
    }

    [TestMethod]
    public void Find_RequiresEveryWord()
    {
        Assert.IsTrue(SettingsSearch.Find("toolbar", Text, 10).Any(r => r.TitleKey == "Settings.Toolbar"));
        Assert.AreEqual(0, SettingsSearch.Find("toolbar zzzz", Text, 10).Count);
    }

    [TestMethod]
    public void Find_IgnoresTooShortQueriesAndHonoursTheLimit()
    {
        Assert.AreEqual(0, SettingsSearch.Find("a", Text, 5).Count);
        Assert.AreEqual(0, SettingsSearch.Find("   ", Text, 5).Count);
        Assert.IsTrue(SettingsSearch.Find("show", Text, 2).Count <= 2);
    }
}
