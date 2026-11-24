using Navigator.Core.Caching;
using Navigator.Core.Collections;
using Navigator.Core.Formatting;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Core.Sorting;
using Navigator.Core.Versioning;

namespace Navigator.UnitTests;

[TestClass]
public sealed class PathUtilTests
{
    [TestMethod]
    public void ValidateName_AppliesWindowsRules()
    {
        Assert.AreEqual(NameValidationResult.Valid, PathUtil.ValidateName("report.txt", windowsRules: true));
        Assert.AreEqual(NameValidationResult.InvalidCharacters, PathUtil.ValidateName("a:b", windowsRules: true));
        Assert.AreEqual(NameValidationResult.Reserved, PathUtil.ValidateName("CON.txt", windowsRules: true));
        Assert.AreEqual(NameValidationResult.Reserved, PathUtil.ValidateName("lpt1", windowsRules: true));
        Assert.AreEqual(NameValidationResult.InvalidEnding, PathUtil.ValidateName("name.", windowsRules: true));
        Assert.AreEqual(NameValidationResult.Empty, PathUtil.ValidateName("  ", windowsRules: true));
    }

    [TestMethod]
    public void ValidateName_UnixAllowsWindowsReservedCharacters()
    {
        Assert.AreEqual(NameValidationResult.Valid, PathUtil.ValidateName("a:b?", windowsRules: false));
        Assert.AreEqual(NameValidationResult.InvalidCharacters, PathUtil.ValidateName("a/b", windowsRules: false));
        Assert.AreEqual(NameValidationResult.Reserved, PathUtil.ValidateName("..", windowsRules: false));
        Assert.AreEqual(NameValidationResult.TooLong, PathUtil.ValidateName(new string('é', 200), windowsRules: false));
    }

    [TestMethod]
    public void GetUniqueName_AddsCounterBeforeExtension()
    {
        var existing = new HashSet<string> { Path.Combine("d", "photo.jpg"), Path.Combine("d", "photo (2).jpg") };
        Assert.AreEqual("photo (3).jpg", PathUtil.GetUniqueName("d", "photo.jpg", false, existing.Contains));
        Assert.AreEqual("other.jpg", PathUtil.GetUniqueName("d", "other.jpg", false, existing.Contains));
    }

    [TestMethod]
    public void GetUniqueName_DoesNotStackCounters()
    {
        var existing = new HashSet<string> { Path.Combine("d", "a (2).txt") };
        Assert.AreEqual("a (3).txt", PathUtil.GetUniqueName("d", "a (2).txt", false, existing.Contains));
    }

    [TestMethod]
    public void GetCopyName_FoldersKeepWholeName()
    {
        Assert.AreEqual("my.folder - Copy", PathUtil.GetCopyName("d", "my.folder", true, "Copy", _ => false));
        Assert.AreEqual("notes - Copy.md", PathUtil.GetCopyName("d", "notes.md", false, "Copy", _ => false));
        Assert.AreEqual(".bashrc - Copy", PathUtil.GetCopyName("d", ".bashrc", false, "Copy", _ => false));
    }

    [TestMethod]
    public void IsSameOrDescendant_RespectsSegmentBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "home", "user");
        Assert.IsTrue(PathUtil.IsSameOrDescendant(root, root));
        Assert.IsTrue(PathUtil.IsSameOrDescendant(root, Path.Combine(root, "docs")));
        Assert.IsFalse(PathUtil.IsSameOrDescendant(root, root + "name"));
    }

    [TestMethod]
    public void GetSegments_BuildsBreadcrumbPaths()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var path = Path.Combine(root, "alpha", "beta");
        var segments = PathUtil.GetSegments(path);
        Assert.AreEqual(3, segments.Count);
        Assert.AreEqual("beta", segments[2].Name);
        Assert.IsTrue(PathUtil.AreEqual(path, segments[2].FullPath));
        Assert.IsTrue(PathUtil.AreEqual(Path.Combine(root, "alpha"), segments[1].FullPath));
    }

    [TestMethod]
    public void ExpandUserInput_ExpandsHomeAndRejectsRelative()
    {
        var home = Path.GetTempPath();
        Assert.IsTrue(PathUtil.AreEqual(PathUtil.Normalize(Path.Combine(home, "x")), PathUtil.ExpandUserInput("~/x", home)));
        Assert.IsNull(PathUtil.ExpandUserInput("relative/path", home));
        Assert.IsNull(PathUtil.ExpandUserInput("   ", home));
    }
}

[TestClass]
public sealed class SortingTests
{
    [TestMethod]
    public void NaturalComparer_OrdersNumbersByValue()
    {
        var names = new List<string> { "file10.txt", "file2.txt", "File1.txt", "file02.txt", "a", "B" };
        names.Sort(NaturalStringComparer.Instance);
        CollectionAssert.AreEqual(new[] { "a", "B", "File1.txt", "file2.txt", "file02.txt", "file10.txt" }, names);
    }

    [TestMethod]
    public void NaturalComparer_IsTotalOrderForCaseVariants()
    {
        Assert.AreNotEqual(0, NaturalStringComparer.Instance.Compare("a", "A"));
        Assert.AreEqual(0, NaturalStringComparer.Instance.Compare("same", "same"));
    }

    private static FileEntry Entry(string name, bool directory = false, long size = 0, int day = 1)
        => new(name, "/x/" + name, directory, size, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc), directory ? FileAttributes.Directory : FileAttributes.Normal);

    [TestMethod]
    public void FoldersFirst_ThenColumn()
    {
        var list = new List<FileEntry> { Entry("b.txt", size: 10), Entry("Zeta", directory: true), Entry("a.txt", size: 99) };
        EntrySorter.Sort(list, new SortOptions(SortColumn.Size, SortDirection.Descending, FoldersFirst: true));
        Assert.AreEqual("Zeta", list[0].Name);
        Assert.AreEqual("a.txt", list[1].Name);
    }

    [TestMethod]
    public void MixedSorting_InterleavesFolders()
    {
        var list = new List<FileEntry> { Entry("b"), Entry("a", directory: true), Entry("c", directory: true) };
        EntrySorter.Sort(list, new SortOptions(SortColumn.Name, SortDirection.Ascending, FoldersFirst: false));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, list.Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public void FindInsertIndex_KeepsOrder()
    {
        var comparer = new FileEntryComparer(SortOptions.Default);
        var sorted = new List<FileEntry> { Entry("a"), Entry("c"), Entry("e") };
        Assert.AreEqual(2, EntrySorter.FindInsertIndex(sorted, Entry("d"), comparer));
        Assert.AreEqual(0, EntrySorter.FindInsertIndex(sorted, Entry("0"), comparer));
    }

    [TestMethod]
    public void DateGroups_AreRelativeToNow()
    {
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Local); // Wednesday
        Assert.AreEqual("date.today", EntryGrouper.ByDate(now.AddHours(-1), now).Id);
        Assert.AreEqual("date.yesterday", EntryGrouper.ByDate(now.AddDays(-1), now).Id);
        Assert.AreEqual("date.earlierThisWeek", EntryGrouper.ByDate(now.AddDays(-2), now).Id);
        Assert.AreEqual("date.lastWeek", EntryGrouper.ByDate(now.AddDays(-7), now).Id);
        Assert.AreEqual("date.longAgo", EntryGrouper.ByDate(now.AddYears(-3), now).Id);
    }

    [TestMethod]
    public void NameFilter_SupportsWildcards()
    {
        Assert.IsTrue(new NameFilter("*.jpg").Matches("Photo.JPG"));
        Assert.IsFalse(new NameFilter("*.jpg").Matches("photo.png"));
        Assert.IsTrue(new NameFilter("port").Matches("Report.docx"));
        Assert.IsTrue(new NameFilter("").Matches("anything"));
    }
}

[TestClass]
public sealed class SemVersionTests
{
    [TestMethod]
    public void Parses_TagsAndPrereleases()
    {
        var version = SemVersion.Parse("v1.2.3-beta.4+build.5");
        Assert.AreEqual(1, version.Major);
        Assert.AreEqual(2, version.Minor);
        Assert.AreEqual(3, version.Patch);
        Assert.AreEqual("beta.4", version.Prerelease);
        Assert.AreEqual("build.5", version.Build);
    }

    [TestMethod]
    [DataRow("1.2")]
    [DataRow("1.2.3.4")]
    [DataRow("01.2.3")]
    [DataRow("1.2.3-")]
    [DataRow("1.2.3-01")]
    [DataRow("x.y.z")]
    public void Rejects_InvalidVersions(string text) => Assert.IsFalse(SemVersion.TryParse(text, out _));

    [TestMethod]
    public void Precedence_FollowsSpecification()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        for (var i = 1; i < ordered.Length; i++)
            Assert.IsTrue(SemVersion.Parse(ordered[i - 1]) < SemVersion.Parse(ordered[i]), $"{ordered[i - 1]} < {ordered[i]}");
    }

    [TestMethod]
    public void BuildMetadata_IsIgnoredForEquality() => Assert.AreEqual(SemVersion.Parse("1.0.0+a"), SemVersion.Parse("1.0.0+b"));
}

[TestClass]
public sealed class LruCacheTests
{
    private sealed class Tracked : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [TestMethod]
    public void EvictsLeastRecentlyUsed_ByCost()
    {
        using var cache = new LruCache<string, int>(capacity: 10, costOf: v => v);
        cache.Set("a", 4);
        cache.Set("b", 4);
        Assert.IsTrue(cache.TryGet("a", out _)); // a becomes most recent
        cache.Set("c", 4);                       // over capacity: b goes
        Assert.IsTrue(cache.TryGet("a", out _));
        Assert.IsFalse(cache.TryGet("b", out _));
        Assert.IsTrue(cache.CurrentCost <= 10);
    }

    [TestMethod]
    public void DisposesEvictedValues_ButNotReinsertedOnes()
    {
        using var cache = new LruCache<string, Tracked>(capacity: 1);
        var first = new Tracked();
        cache.Set("a", first);
        cache.Set("a", first);
        Assert.IsFalse(first.Disposed);
        cache.Set("b", new Tracked());
        Assert.IsTrue(first.Disposed);
    }

    [TestMethod]
    public async Task GetOrCreate_DeduplicatesConcurrentRequests()
    {
        using var cache = new LruCache<string, int>(capacity: 100);
        var calls = 0;
        var gate = new TaskCompletionSource();
        async Task<int> Factory(string key, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return 42;
        }

        var tasks = Enumerable.Range(0, 8).Select(_ => cache.GetOrCreateAsync("k", Factory)).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(results.All(r => r == 42));
    }

    [TestMethod]
    public void RemoveWhere_InvalidatesByPrefix()
    {
        using var cache = new LruCache<string, int>(capacity: 100);
        cache.Set("/a/1", 1);
        cache.Set("/a/2", 1);
        cache.Set("/b/1", 1);
        Assert.AreEqual(2, cache.RemoveWhere(k => k.StartsWith("/a/", StringComparison.Ordinal)));
        Assert.AreEqual(1, cache.Count);
    }
}

[TestClass]
public sealed class NavigationTests
{
    [TestMethod]
    public void BackForward_Work()
    {
        var history = new NavigationHistory(Location.Home);
        history.Navigate(Location.Folder("/a"));
        history.Navigate(Location.Folder("/b"));
        Assert.AreEqual(Location.Folder("/a"), history.GoBack());
        Assert.AreEqual(Location.Home, history.GoBack());
        Assert.IsTrue(history.CanGoForward);
        Assert.AreEqual(Location.Folder("/b"), history.GoForward(2));
        Assert.IsFalse(history.CanGoForward);
    }

    [TestMethod]
    public void NavigatingClearsForwardHistory()
    {
        var history = new NavigationHistory(Location.Home);
        history.Navigate(Location.Folder("/a"));
        history.GoBack();
        history.Navigate(Location.Folder("/c"));
        Assert.IsFalse(history.CanGoForward);
    }

    [TestMethod]
    public void HistoryIsBounded()
    {
        var history = new NavigationHistory(Location.Home, capacity: 5);
        for (var i = 0; i < 50; i++)
            history.Navigate(Location.Folder("/f" + i));
        Assert.AreEqual(5, history.BackEntries.Count());
    }

    [TestMethod]
    public void ReplaceLocation_FollowsRenamedFolder()
    {
        var history = new NavigationHistory(Location.Folder("/old"));
        history.Navigate(Location.Folder("/other"));
        history.ReplaceLocation(Location.Folder("/old"), Location.Folder("/new"));
        Assert.AreEqual(Location.Folder("/new"), history.GoBack());
    }

    [TestMethod]
    public void Location_SerializationRoundTrips()
    {
        foreach (var location in new[] { Location.Home, Location.Trash, Location.Folder("/x/y"), Location.Search("/x", "a|b"), Location.Tag("red") })
            Assert.AreEqual(location, Location.TryParse(location.Serialize()));
        Assert.IsNull(Location.TryParse("garbage"));
    }

    [TestMethod]
    public void BoundedStack_DropsOldest()
    {
        var stack = new BoundedStack<int>(3);
        for (var i = 1; i <= 5; i++)
            stack.Push(i);
        CollectionAssert.AreEqual(new[] { 5, 4, 3 }, stack.Items.ToArray());
        Assert.IsTrue(stack.TryPop(out var top));
        Assert.AreEqual(5, top);
    }
}

[TestClass]
public sealed class FormattingTests
{
    [TestMethod]
    public void ByteSize_UsesBinaryUnits()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Assert.AreEqual("0 bytes", ByteSize.Format(0, invariant));
        Assert.AreEqual("1 byte", ByteSize.Format(1, invariant));
        Assert.AreEqual("1023 bytes", ByteSize.Format(1023, invariant));
        Assert.AreEqual("1 KB", ByteSize.Format(1024, invariant));
        Assert.AreEqual("1.5 MB", ByteSize.Format(1572864, invariant));
        Assert.AreEqual("12.3 GB", ByteSize.Format(13_207_024_435, invariant));
    }

    [TestMethod]
    public void AppDirectories_FollowPlatformConventions()
    {
        static string? NoEnv(string _) => null;
        var linux = AppDirectories.Resolve(NoEnv, OperatingSystemKind.Linux, "/home/u");
        Assert.AreEqual(Path.Combine("/home/u", ".config", "navigator"), linux.ConfigDirectory);
        Assert.AreEqual(Path.Combine("/home/u", ".local", "state", "navigator", "logs"), linux.LogDirectory);

        var xdg = AppDirectories.Resolve(k => k == "XDG_CONFIG_HOME" ? "/cfg" : k == "XDG_CACHE_HOME" ? "relative" : null, OperatingSystemKind.Linux, "/home/u");
        Assert.AreEqual(Path.Combine("/cfg", "navigator"), xdg.ConfigDirectory);
        Assert.AreEqual(Path.Combine("/home/u", ".cache", "navigator"), xdg.CacheDirectory, "relative XDG paths must be ignored");

        var mac = AppDirectories.Resolve(NoEnv, OperatingSystemKind.MacOS, "/Users/u");
        Assert.AreEqual(Path.Combine("/Users/u", "Library", "Application Support", "Navigator"), mac.ConfigDirectory);

        var portable = AppDirectories.Resolve(k => k == AppDirectories.OverrideVariable ? "/p" : null, OperatingSystemKind.Windows, "C:/Users/u");
        Assert.AreEqual(Path.Combine("/p", "config"), portable.ConfigDirectory);
    }
}

[TestClass]
public sealed class SystemIconKeyTests
{
    [TestMethod]
    public void StockAndPathKeys_RoundTrip()
    {
        Assert.IsTrue(SystemIconKey.TryParse(SystemIconKey.Stock(SystemIconKind.DriveFixed), out var kind, out var path));
        Assert.AreEqual(SystemIconKind.DriveFixed, kind);
        Assert.IsNull(path);

        Assert.IsTrue(SystemIconKey.TryParse(SystemIconKey.Stock(SystemIconKind.RecycleBin), out kind, out _));
        Assert.AreEqual(SystemIconKind.RecycleBin, kind);

        Assert.IsTrue(SystemIconKey.TryParse(SystemIconKey.ForPath(@"C:\Users\me\Downloads"), out _, out path));
        Assert.AreEqual(@"C:\Users\me\Downloads", path);
    }

    [TestMethod]
    public void InvalidKeys_AreRejected()
    {
        Assert.IsFalse(SystemIconKey.TryParse(null, out _, out _));
        Assert.IsFalse(SystemIconKey.TryParse("path:", out _, out _));
        Assert.IsFalse(SystemIconKey.TryParse("stock:NoSuchIcon", out _, out _));
        Assert.IsFalse(SystemIconKey.TryParse("stock:999", out _, out _));
        Assert.IsFalse(SystemIconKey.TryParse("Folder", out _, out _));
    }
}
