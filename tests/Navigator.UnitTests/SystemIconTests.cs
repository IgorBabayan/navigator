using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Core.Tags;
using Navigator.Platform.Icons;
using Navigator.Presentation.Items;
using Navigator.Presentation.Localization;

namespace Navigator.UnitTests;

[TestClass]
public sealed class SystemIconTests
{
    private sealed class FakeIcons(bool supported = true) : ISystemIconProvider
    {
        public bool IsSupported => supported;

        public Task<SystemIconImage?> GetIconAsync(string key, int pixelSize, CancellationToken cancellationToken = default)
            => Task.FromResult<SystemIconImage?>(null);

        public bool HasOwnIcon(string extension) => extension == ".exe";
    }

    private static FileEntry Entry(string name, bool folder = false, bool remote = false)
        => new(name, "/data/" + name, folder, 1, DateTime.UtcNow, DateTime.UtcNow, folder ? FileAttributes.Directory : FileAttributes.Normal, remote);

    [TestMethod]
    public void FileTypeKeys_AreNormalized_AndParseBack()
    {
        Assert.AreEqual("type:.pdf", SystemIconKey.ForFileType(".PDF"));
        Assert.AreEqual("type:.pdf", SystemIconKey.ForFileType("pdf"));
        Assert.AreEqual("type:", SystemIconKey.ForFileType(""));
        Assert.IsTrue(SystemIconKey.TryParseFileType("type:.pdf", out var extension));
        Assert.AreEqual(".pdf", extension);
        Assert.IsFalse(SystemIconKey.TryParseFileType(SystemIconKey.Stock(SystemIconKind.Folder), out _));
    }

    [TestMethod]
    public void Rows_ShareTypeIcons_ButProgramsGetTheirOwn()
    {
        var context = new ItemDisplayContext(new Localizer(), showExtensions: true, new FakeIcons());

        Assert.AreEqual(SystemIconKey.Stock(SystemIconKind.Folder), context.SystemIconFor(Entry("Projects", folder: true)));
        Assert.AreEqual("type:.pdf", context.SystemIconFor(Entry("a.pdf")));
        Assert.AreSame(context.SystemIconFor(Entry("a.pdf")), context.SystemIconFor(Entry("b.PDF")));
        Assert.AreEqual(SystemIconKey.ForPath("/data/setup.exe"), context.SystemIconFor(Entry("setup.exe")));
        Assert.AreEqual("type:.exe", context.SystemIconFor(Entry("setup.exe", remote: true))); // never read a remote file
    }

    [TestMethod]
    public void Rows_KeepNavigatorIcons_WhenOffOrUnsupported()
    {
        Assert.IsNull(new ItemDisplayContext(new Localizer(), true).SystemIconFor(Entry("a.pdf")));
        Assert.IsNull(new ItemDisplayContext(new Localizer(), true, new FakeIcons(supported: false)).SystemIconFor(Entry("a.pdf")));
    }

    [TestMethod]
    public void TagStore_RemoveTag_TouchesOnlyThatTag()
    {
        var store = new TagStore(null);
        store.SetTags("/x/a", ["work", "home"]);
        store.SetTags("/x/b", ["work"]);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.RemoveTag(["/x/a", "/x/b", "/x/c"], "work");
        CollectionAssert.AreEqual(new[] { "home" }, store.GetTags("/x/a").ToArray());
        Assert.AreEqual(0, store.GetTags("/x/b").Count);
        Assert.AreEqual(1, raised);

        store.RemoveTag(["/x/b"], "work");
        Assert.AreEqual(1, raised); // nothing to remove, no change notification
    }

    [TestMethod]
    public void IconTheme_FollowsInheritance_AndPrefersFittingSizes()
    {
        var root = Directory.CreateTempSubdirectory("nav-icons-").FullName;
        try
        {
            void Theme(string name, string index, params string[] files)
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                System.IO.File.WriteAllText(Path.Combine(root, name, "index.theme"), index);
                foreach (var file in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, name, file))!);
                    System.IO.File.WriteAllText(Path.Combine(root, name, file), "x");
                }
            }

            Theme("Child", """
                [Icon Theme]
                Name=Child
                Inherits=Parent
                Directories=mimes/16,mimes/48,apps/48

                [mimes/16]
                Size=16
                Context=MimeTypes
                Type=Fixed

                [mimes/48]
                Size=48
                Context=MimeTypes
                Type=Fixed

                [apps/48]
                Size=48
                Context=Applications
                Type=Fixed
                """, "mimes/16/application-pdf.png", "mimes/48/application-pdf.png", "apps/48/text-x-generic.png");
            Theme("Parent", """
                [Icon Theme]
                Name=Parent
                Directories=scalable/places

                [scalable/places]
                Size=64
                MinSize=8
                MaxSize=512
                Context=Places
                Type=Scalable
                """, "scalable/places/folder.svg", "scalable/places/folder.png");

            var theme = FreedesktopIconTheme.Load("Child", [root]);

            var pdf = theme.Find(["application-pdf"], 48);
            Assert.AreEqual(Path.Combine(root, "Child", "mimes", "48", "application-pdf.png"), pdf[0]);
            Assert.AreEqual(Path.Combine(root, "Child", "mimes", "16", "application-pdf.png"), theme.Find(["application-pdf"], 16)[0]);

            // Inherited, scalable: only the vector image counts in a scalable directory.
            CollectionAssert.AreEqual(new[] { Path.Combine(root, "Parent", "scalable", "places", "folder.svg") }, theme.Find(["folder"], 32));

            // Applications are not a context a file manager looks in.
            Assert.AreEqual(0, theme.Find(["text-x-generic"], 48).Count);

            // An earlier name wins over a later one.
            Assert.AreEqual(pdf[0], theme.Find(["application-pdf", "folder"], 48)[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void IconThemeName_ReadsIniKeysOfTheRightSection()
    {
        var file = Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(file, "[General]\nTheme=wrong\n[Icons]\nTheme = Papirus-Dark\n");
            Assert.AreEqual("Papirus-Dark", IconThemeName.ReadKey(file, "Icons", "Theme"));
            Assert.IsNull(IconThemeName.ReadKey(file, "Icons", "Missing"));
            Assert.IsNull(IconThemeName.ReadKey(file + ".none", "Icons", "Theme"));
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }
}
