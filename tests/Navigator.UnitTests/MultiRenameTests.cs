using Navigator.Core.Renaming;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Panes;
using Navigator.Tests;

namespace Navigator.UnitTests;

[TestClass]
public sealed class MultiRenameTemplateTests
{
    private static readonly RenameSource Photo = new("Holiday Photo.JPG", false, Path.Combine("home", "Trips", "Rome"), new DateTime(2025, 7, 4, 13, 5, 9));

    private static string Apply(MultiRenameOptions options, int index = 0) => MultiRenameTemplate.Compile(options).Apply(Photo, index);

    [TestMethod]
    [DataRow("[N]", "Holiday Photo.JPG")]
    [DataRow("[N1-3]", "Hol.JPG")]
    [DataRow("[N2,4]", "olid.JPG")]
    [DataRow("[N3]", "l.JPG")]
    [DataRow("[N9-]", "Photo.JPG")]
    [DataRow("[N-5-]", "Photo.JPG")]
    [DataRow("[N2--3]", "oliday Pho.JPG")]
    [DataRow("[P]_[G]", "Rome_Trips.JPG")]
    [DataRow("[Y][M][D]-[t]", "20250704-13.05.09.JPG")]
    [DataRow("[F]hello world-x[n] ABC", "Hello World-X ABC.JPG")]
    [DataRow("[[][N][]]", "[Holiday Photo].JPG")]
    public void Masks_FollowTotalCommanderSyntax(string mask, string expected)
        => Assert.AreEqual(expected, Apply(new MultiRenameOptions { NameMask = mask }));

    [TestMethod]
    public void Counter_UsesDialogSettingsOrInlineOverrides()
    {
        Assert.AreEqual("x005.JPG", Apply(new MultiRenameOptions { NameMask = "x[C]", CounterStart = 1, CounterStep = 1, CounterDigits = 3 }, index: 4));
        Assert.AreEqual("25.JPG", Apply(new MultiRenameOptions { NameMask = "[C10+5:2]" }, index: 3));
    }

    [TestMethod]
    public void ExtensionMask_CaseAndSearchReplace()
    {
        Assert.AreEqual("HOLIDAY PHOTOjpg", Apply(new MultiRenameOptions { NameMask = "[U][N][L][E]", ExtensionMask = string.Empty }));
        Assert.AreEqual("Holiday pic.png", Apply(new MultiRenameOptions { Search = "photo|jpg", Replace = "pic|png" }));
        Assert.AreEqual("Photo_Holiday.JPG", Apply(new MultiRenameOptions { Search = @"(\w+) (\w+)", Replace = "$2_$1", UseRegex = true }));
        Assert.AreEqual("Holiday Photo.JPG", Apply(new MultiRenameOptions { Search = "photo", Replace = "x", MatchCase = true }));
        Assert.AreEqual("holiday photo.jpg", Apply(new MultiRenameOptions { Case = RenameCase.Lower }));
        Assert.AreEqual("Holiday Photo.Jpg", Apply(new MultiRenameOptions { Case = RenameCase.EachWordUpper }));
    }

    [TestMethod]
    public void Folders_HaveNoExtension()
    {
        var folder = new RenameSource("release.v2", true, null, DateTime.Now);
        Assert.AreEqual("release.v2-old", MultiRenameTemplate.Compile(new MultiRenameOptions { NameMask = "[N]-old" }).Apply(folder, 0));
    }

    [TestMethod]
    [DataRow("[X]", "UnknownPlaceholder")]
    [DataRow("[N", "Unclosed")]
    [DataRow("[N0]", "InvalidRange")]
    [DataRow("[N1-x]", "InvalidRange")]
    [DataRow("[C+]", "InvalidCounter")]
    public void InvalidMasks_AreReported(string mask, string key)
    {
        var error = Assert.ThrowsExactly<MultiRenameTemplateException>(() => MultiRenameTemplate.Compile(new MultiRenameOptions { NameMask = mask }));
        Assert.AreEqual(key, error.Key);
    }

    [TestMethod]
    public void InvalidRegex_IsReported()
        => Assert.AreEqual("InvalidRegex", Assert.ThrowsExactly<MultiRenameTemplateException>(
            () => MultiRenameTemplate.Compile(new MultiRenameOptions { Search = "(", UseRegex = true })).Key);
}

[TestClass]
public sealed class MultiRenameViewModelTests
{
    private static readonly Localizer Text = new();

    private static MultiRenameViewModel Model(params string[] paths)
    {
        var model = new MultiRenameViewModel([.. paths.Select(p => new MultiRenameItem(p, false, DateTime.UtcNow))], Text);
        model.Reset(); // the dialog remembers settings between uses; tests start from the defaults
        return model;
    }

    [TestMethod]
    public void Preview_NumbersItemsAndBlocksClashes()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateFile("a.jpg");
        var b = temp.CreateFile("b.jpg");
        temp.CreateFile("outside.jpg");
        var model = Model(a, b);
        Assert.IsFalse(model.CanRename, "nothing changes yet");

        model.NameMask = "Trip [C]";
        CollectionAssert.AreEqual(new[] { "Trip 1.jpg", "Trip 2.jpg" }, model.Rows.Select(r => r.NewName).ToArray());
        Assert.IsTrue(model.CanRename);
        Assert.AreEqual(2, model.GetRenames().Count);

        model.NameMask = "same";
        Assert.IsTrue(model.Rows.All(r => r.HasError));
        Assert.IsFalse(model.CanRename);

        model.NameMask = "outside";
        Assert.IsFalse(model.CanRename);

        model.NameMask = "[N";
        Assert.IsTrue(model.HasTemplateError);
        Assert.IsFalse(model.CanRename);

        model.NameMask = "[N]-x";
        model.CounterDigits = "abc";
        Assert.IsTrue(model.HasTemplateError);
        model.CounterDigits = "2";
        Assert.IsFalse(model.HasTemplateError);
        Assert.IsTrue(model.CanRename);
    }

    [TestMethod]
    public void Planner_OrdersChainsAndRejectsSwaps()
    {
        var a = Path.Combine("d", "a.txt");
        var b = Path.Combine("d", "b.txt");
        var c = Path.Combine("d", "c.txt");
        var chain = BulkRenamePlanner.Order([(a, "b.txt"), (b, "c.txt")]);
        Assert.IsNull(chain.Error);
        CollectionAssert.AreEqual(new[] { b, a }, chain.Renames.Select(r => r.Path).ToArray());

        Assert.AreEqual("Cycle", BulkRenamePlanner.Order([(a, "b.txt"), (b, "a.txt")]).Error);
        Assert.AreEqual(1, BulkRenamePlanner.Order([(c, "c.txt"), (a, "x.txt")]).Renames.Count, "unchanged names are dropped");
    }
}
