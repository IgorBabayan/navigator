using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Plugin.CloudSync;
using Navigator.Plugin.DefaultFileManager;
using Navigator.Plugin.Git;
using Navigator.Plugin.ModernMenu;
using Navigator.Plugin.WindowsSearch;
using Navigator.Plugin.Wsl;
using Navigator.Plugins;
using Navigator.Presentation.Items;
using Navigator.Presentation.Plugins;
using Navigator.Presentation.Settings;
using Navigator.Tests;

namespace Navigator.UnitTests;

/// <summary>
/// Plugin API 1.3 (folder decorators, menu and search providers, settings pages, platforms, helpers) and the plugins
/// built on it: Git, cloud sync folders, WSL, the Windows Search index, Windows 11 app commands, default file manager.
/// </summary>
[TestClass]
public sealed class IntegrationPluginTests
{
    // ── Host: contributions of API 1.3 ───────────────────────

    [TestMethod]
    public void Host_PublishesDecoratorsMenusSearchProvidersAndSettingsPages()
    {
        using var temp = new TempDirectory();
        var plugin = new ContributingPlugin();
        using var host = new PluginHost(temp.Combine("plugins"), new HostServices(), [Built("acme", plugin)]);

        Assert.AreSame(plugin.Decorator, host.FolderDecorators.Single());
        Assert.AreSame(plugin.Menu, host.MenuProviders.Single());
        Assert.AreSame(plugin.Search, host.SearchProviders.Single());
        Assert.AreSame(plugin.Page, host.GetSettingsPage("acme"));
        Assert.IsNull(host.GetSettingsPage("other"));
    }

    [TestMethod]
    public void Host_PublishesALateRegistrationOnce_AndKeepsDecoratorIdsUnique()
    {
        using var temp = new TempDirectory();
        IPluginContext? captured = null;
        using var host = new PluginHost(temp.Combine("plugins"), new HostServices(), [Built("acme", new CallbackPlugin(c => captured = c))]);
        var changes = 0;
        host.Changed += (_, _) => changes++;

        captured!.AddFolderDecorator(new FakeDecorator("same"));
        captured.AddFolderDecorator(new FakeDecorator("same"));

        Assert.AreEqual(1, host.FolderDecorators.Count);
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void Manifest_Platforms_AreValidatedAndCopiedIntoTheCatalog()
    {
        var json = """{"id":"wsl","name":"WSL","version":"1.0.0","apiVersion":"1.3","assembly":"A.dll","entryType":"A.B","platforms":["windows"]}"""u8;
        var manifest = PluginManifest.Parse(json);
        CollectionAssert.AreEqual(new[] { "windows" }, manifest.Platforms);
        Assert.AreEqual(OperatingSystem.IsWindows(), manifest.SupportsCurrentPlatform);

        var entry = PluginCatalogEntry.FromManifest(manifest, "navigator-plugin-wsl-1.0.0.navplugin", 10, new string('a', 64));
        CollectionAssert.AreEqual(new[] { "windows" }, entry.Platforms);
        Assert.AreEqual(OperatingSystem.IsWindows(), entry.SupportsCurrentPlatform);
        CollectionAssert.AreEqual(new[] { "windows" }, entry.ToManifest().Platforms);

        Assert.ThrowsExactly<InvalidDataException>(() => PluginManifest.Parse("""{"id":"x1","name":"X","version":"1.0.0","apiVersion":"1.3","assembly":"A.dll","entryType":"A.B","platforms":["amiga"]}"""u8));
        Assert.IsTrue(PluginManifest.SupportsPlatform(null, "linux"));
        Assert.IsTrue(PluginManifest.SupportsPlatform([], "macos"));
        Assert.IsFalse(PluginManifest.SupportsPlatform(["windows", "linux"], "macos"));
    }

    [TestMethod]
    public void PackagedPlugins_DeclareTheirPlatforms()
    {
        if (RepositoryRoot() is not { } root)
        {
            Assert.Inconclusive("Repository root not found from the test output folder.");
            return;
        }

        string Platforms(string project) => System.Xml.Linq.XDocument.Load(Path.Combine(root, "plugins", project, project + ".csproj"))
            .Descendants("NavigatorPluginPlatforms").SingleOrDefault()?.Value ?? string.Empty;

        Assert.AreEqual("windows", Platforms("Navigator.Plugin.Wsl"));
        Assert.AreEqual("windows", Platforms("Navigator.Plugin.WindowsSearch"));
        Assert.AreEqual("windows", Platforms("Navigator.Plugin.ModernMenu"));
        Assert.AreEqual("windows;linux", Platforms("Navigator.Plugin.DefaultFileManager"));
        Assert.AreEqual(string.Empty, Platforms("Navigator.Plugin.Git"));
        Assert.AreEqual(string.Empty, Platforms("Navigator.Plugin.CloudSync"));
    }

    [TestMethod]
    [DataRow("/home/me/repo", "/home/me/repo", true)]
    [DataRow("/home/me/repo", "/home/me/repo/src", true)]
    [DataRow("/home/me/repo/", "/home/me/repo/src", true)]
    [DataRow("/home/me/repo", "/home/me/repository", false)]
    [DataRow("/home/me/repo", "/home/me", false)]
    public void DecorationsChanged_CoversTheFolderAndItsSubfolders(string changed, string shown, bool expected)
        => Assert.AreEqual(expected, new FolderDecorationsChangedEventArgs(changed).Covers(shown));

    [TestMethod]
    public void DecorationsChanged_Everywhere_CoversAnyFolder()
        => Assert.IsTrue(FolderDecorationsChangedEventArgs.Everywhere.Covers("/anything"));

    // ── Helpers: preferences, processes ──────────────────────

    [TestMethod]
    public void Preferences_RoundTripThroughTheDataFolder()
    {
        using var temp = new TempDirectory();
        var preferences = new PluginPreferences(() => temp.Path);
        var changes = 0;
        preferences.Changed += (_, _) => changes++;

        Assert.IsTrue(preferences.GetBool("badges", true));
        preferences.SetBool("badges", false);
        preferences.SetString("name", "value");
        preferences.SetString("name", "value"); // unchanged: no write, no event

        var reloaded = new PluginPreferences(() => temp.Path);
        Assert.IsFalse(reloaded.GetBool("badges", true));
        Assert.AreEqual("value", reloaded.GetString("name"));
        reloaded.SetString("name", null);
        Assert.IsNull(new PluginPreferences(() => temp.Path).GetString("name"));
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void Preferences_UnreadableFile_FallsBackToDefaults()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine(PluginPreferences.FileName), "{ not json");
        Assert.IsTrue(new PluginPreferences(() => temp.Path).GetBool("badges", true));
    }

    [TestMethod]
    public async Task Process_RunsWithoutShellQuoting_AndCapturesBothStreams()
    {
        if (OperatingSystem.IsWindows() || PluginProcess.FindExecutable("sh") is not { } sh)
        {
            Assert.Inconclusive("Needs a POSIX shell.");
            return;
        }

        var result = await PluginProcess.RunAsync(new PluginProcessRequest(sh, ["-c", "printf '%s' \"$1\"; printf err >&2; exit 3", "sh", "a b 'c'"]), CancellationToken.None);
        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual("a b 'c'", result.Output);
        Assert.AreEqual("err", result.Error);
    }

    [TestMethod]
    public async Task Process_StreamsRecords_AndStopsTheCommandWhenTheReaderStops()
    {
        if (OperatingSystem.IsWindows() || PluginProcess.FindExecutable("sh") is not { } sh)
        {
            Assert.Inconclusive("Needs a POSIX shell.");
            return;
        }

        var records = new List<string>();
        await foreach (var record in PluginProcess.ReadRecordsAsync(new PluginProcessRequest(sh, ["-c", "printf 'one\\0two\\0three'"]), '\0', CancellationToken.None))
            records.Add(record);
        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, records);

        // An endless producer: reading two records and stopping must end it (otherwise this test hangs).
        var first = new List<string>();
        await foreach (var record in PluginProcess.ReadRecordsAsync(new PluginProcessRequest(sh, ["-c", "while true; do echo line; done"]) { Timeout = TimeSpan.FromSeconds(20) }, '\n', CancellationToken.None))
        {
            first.Add(record);
            if (first.Count == 2)
                break;
        }

        CollectionAssert.AreEqual(new[] { "line", "line" }, first);
    }

    [TestMethod]
    public async Task Process_TimeoutKillsTheCommand_AndFailuresAreReported()
    {
        if (OperatingSystem.IsWindows() || PluginProcess.FindExecutable("sh") is not { } sh)
        {
            Assert.Inconclusive("Needs a POSIX shell.");
            return;
        }

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => PluginProcess.RunAsync(new PluginProcessRequest(sh, ["-c", "sleep 30"]) { Timeout = TimeSpan.FromMilliseconds(200) }, CancellationToken.None));
        var failure = await Assert.ThrowsExactlyAsync<PluginProcessException>(async () =>
        {
            await foreach (var _ in PluginProcess.ReadRecordsAsync(new PluginProcessRequest(sh, ["-c", "echo nope >&2; exit 2"]), '\n', CancellationToken.None))
            {
            }
        });
        Assert.AreEqual(2, failure.ExitCode);
        Assert.AreEqual("nope", failure.Message);
        await Assert.ThrowsExactlyAsync<PluginProcessException>(() => PluginProcess.RunAsync(new PluginProcessRequest("/no/such/program", []), CancellationToken.None));
    }

    // ── Presentation: decorations and plugin columns ─────────

    [TestMethod]
    public void ItemDecoration_EqualByContent()
    {
        var a = new PluginItemDecoration(new PluginBadge("AlphaMBox", "#D29922"), new Dictionary<string, string> { ["git.status"] = "Modified" });
        var b = new PluginItemDecoration(new PluginBadge("AlphaMBox", "#D29922"), new Dictionary<string, string> { ["git.status"] = "Modified" });
        var c = new PluginItemDecoration(new PluginBadge("AlphaMBox", "#D29922"), new Dictionary<string, string> { ["git.status"] = "Staged" });
        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, c);
        Assert.AreEqual("Modified", a.CellText("git.status"));
        Assert.AreEqual(string.Empty, a.CellText("git.author"));
    }

    [TestMethod]
    public void ColumnStrip_ShowsTheDecoratorsColumns_AndRemembersWidthAndVisibility()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsService(new SettingsStore(temp.Combine("settings.json")), NullLogger<SettingsService>.Instance);
        var decorator = new FakeDecorator("git", [new PluginColumn("status", "Git status", 110), new PluginColumn("author", "Author", 140, IsVisibleByDefault: false)]);
        var strip = new PluginColumnStrip(settings);

        strip.Show([decorator]);
        Assert.AreEqual(2, strip.Available.Count);
        CollectionAssert.AreEqual(new[] { "git.status" }, strip.Visible.Select(c => c.Key).ToList());
        CollectionAssert.AreEqual(new[] { "status" }, strip.VisibleColumnsOf(decorator).ToList());

        var revision = strip.Revision;
        strip.Resize(strip.Visible[0], 30);
        strip.SetVisible(strip.Available[1], true);
        Assert.AreEqual(140, strip.Visible[0].Width);
        Assert.IsTrue(strip.Revision > revision);
        CollectionAssert.AreEqual(new[] { "git.status", "git.author" }, strip.Visible.Select(c => c.Key).ToList());

        var again = new PluginColumnStrip(settings);
        again.Show([decorator]);
        Assert.AreEqual(140, again.Visible[0].Width);
        Assert.AreEqual(2, again.Visible.Count);

        again.Show([]);
        Assert.AreEqual(0, again.Visible.Count);
        Assert.IsFalse(again.HasVisible);
    }

    [TestMethod]
    public void Settings_PluginColumnsAreBounded()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 100; i++)
            settings.FilesAndFolders.PluginColumns.Add(new ColumnSetting { Id = "p.c" + i, Width = i == 0 ? double.NaN : 5000 });
        settings.FilesAndFolders.PluginColumns.Add(new ColumnSetting { Id = string.Empty });
        SettingsStore.Sanitize(settings);
        Assert.AreEqual(FilesAndFoldersSettings.MaxPluginColumns, settings.FilesAndFolders.PluginColumns.Count);
        Assert.AreEqual(140, settings.FilesAndFolders.PluginColumns[0].Width);
        Assert.AreEqual(1200, settings.FilesAndFolders.PluginColumns[1].Width);
    }

    // ── Git ──────────────────────────────────────────────────

    private const string Porcelain =
        "# branch.oid 1234567890abcdef\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +2 -1\0" +
        "1 .M N... 100644 100644 100644 aaa bbb src/app/Program.cs\0" +
        "1 M. N... 100644 100644 100644 aaa bbb src/app/Staged File.cs\0" +
        "1 A. N... 000000 100644 100644 000 bbb docs/new.md\0" +
        "2 R. N... 100644 100644 100644 aaa bbb R100 src/renamed.cs\0src/old.cs\0" +
        "u UU N... 100644 100644 100644 100644 a b c src/conflict.cs\0" +
        "? notes.txt\0" +
        "? build/\0" +
        "! bin/\0";

    [TestMethod]
    public void GitStatus_ParsesBranchAndPaths()
    {
        var status = GitRepoStatus.Parse(Porcelain);
        Assert.AreEqual("main", status.Branch.Branch);
        Assert.AreEqual("origin/main", status.Branch.Upstream);
        Assert.AreEqual(2, status.Branch.Ahead);
        Assert.AreEqual(1, status.Branch.Behind);
        Assert.AreEqual("main", status.Branch.Label);

        Assert.AreEqual(GitFileState.Modified, status.Paths["src/app/Program.cs"]);
        Assert.AreEqual(GitFileState.Staged, status.Paths["src/app/Staged File.cs"]);
        Assert.AreEqual(GitFileState.Added, status.Paths["docs/new.md"]);
        Assert.AreEqual(GitFileState.Renamed, status.Paths["src/renamed.cs"]);
        Assert.IsFalse(status.Paths.ContainsKey("src/old.cs"));
        Assert.AreEqual(GitFileState.Conflicted, status.Paths["src/conflict.cs"]);
        Assert.AreEqual(GitFileState.Untracked, status.Paths["build/"]);
        Assert.AreEqual(GitFileState.Ignored, status.Paths["bin/"]);
        Assert.AreEqual(7, status.ChangedCount);
    }

    [TestMethod]
    public void GitStatus_DetachedAndFreshRepositories()
    {
        var detached = GitRepoStatus.Parse("# branch.oid abcdef1234567\0# branch.head (detached)\0").Branch;
        Assert.IsTrue(detached.IsDetached);
        Assert.AreEqual("abcdef1", detached.Label);
        Assert.IsFalse(detached.HasUpstream);

        var fresh = GitRepoStatus.Parse("# branch.oid (initial)\0# branch.head main\0? a.txt\0").Branch;
        Assert.IsNull(fresh.Oid);
        Assert.AreEqual("main", fresh.Label);
    }

    [TestMethod]
    public void GitStatus_ChildrenOfAFolder_AggregateTheirContents()
    {
        var status = GitRepoStatus.Parse(Porcelain);

        var root = status.ChildrenOf(string.Empty, out var whole);
        Assert.AreEqual(GitFileState.None, whole);
        Assert.AreEqual(new GitChildState(GitFileState.Conflicted, true), root["src"]);
        Assert.AreEqual(new GitChildState(GitFileState.Modified, true), root["docs"]); // an added file inside makes the folder "changed"
        Assert.AreEqual(new GitChildState(GitFileState.Untracked, false), root["notes.txt"]);
        Assert.AreEqual(new GitChildState(GitFileState.Untracked, false), root["build"]);
        Assert.AreEqual(new GitChildState(GitFileState.Ignored, false), root["bin"]);

        var app = status.ChildrenOf("src/app", out _);
        Assert.AreEqual(GitFileState.Modified, app["Program.cs"].State);
        Assert.AreEqual(GitFileState.Staged, app["Staged File.cs"].State);
        Assert.AreEqual(2, app.Count);

        Assert.AreEqual(0, status.ChildrenOf("build/output", out var insideUntracked).Count);
        Assert.AreEqual(GitFileState.Untracked, insideUntracked);
    }

    [TestMethod]
    public void GitCommitLine_IsParsed()
    {
        var commit = GitClient.ParseCommit("\u001eabc123\u001fAda Lovelace\u001f1700000000\u001fFix the parser");
        Assert.IsNotNull(commit);
        Assert.AreEqual("abc123", commit.Hash);
        Assert.AreEqual("Ada Lovelace", commit.Author);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1700000000), commit.Date);
        Assert.AreEqual("Fix the parser", commit.Subject);
        Assert.IsNull(GitClient.ParseCommit("\u001ebroken"));
    }

    [TestMethod]
    public void GitRepositoryLocator_FindsTheWorkTree_AndFollowsGitFiles()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("repo/.git");
        temp.CreateDirectory("repo/src/deep");
        temp.CreateDirectory("worktree");
        File.WriteAllText(temp.Combine("worktree", ".git"), "gitdir: ../repo/.git\n");
        temp.CreateDirectory("plain");
        var locator = new GitRepositoryLocator();

        Assert.AreEqual(temp.Combine("repo"), locator.Find(temp.Combine("repo", "src", "deep"))?.Root);
        Assert.AreEqual(temp.Combine("repo", ".git"), locator.Find(temp.Combine("repo"))?.GitDirectory);
        Assert.AreEqual(Path.GetFullPath(temp.Combine("repo", ".git")), locator.Find(temp.Combine("worktree"))?.GitDirectory);
        Assert.IsNull(locator.Find(temp.Combine("plain")));
        Assert.IsNull(locator.Find(temp.Combine("repo", ".git")));
        Assert.IsNull(locator.Find(@"\\server\share\repo"));
    }

    [TestMethod]
    public async Task GitDecorator_AgainstARealRepository()
    {
        if (PluginProcess.FindExecutable("git") is null)
        {
            Assert.Inconclusive("git is not installed.");
            return;
        }

        GitPlugin.Text = new PluginStrings(typeof(GitPlugin).Assembly, () => "en");
        using var temp = new TempDirectory();
        var repo = temp.CreateDirectory("repo");
        await Git(repo, "init", "-q", "-b", "main");
        temp.CreateFile("repo/tracked.txt", "one");
        temp.CreateFile("repo/src/code.cs", "class A {}");
        await Git(repo, "add", ".");
        await Git(repo, "-c", "user.name=Ada", "-c", "user.email=ada@example.com", "commit", "-q", "-m", "First commit");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "two");
        temp.CreateFile("repo/new.txt", "new");

        using var decorator = new GitDecorator(new GitClient(), new GitRepositoryLocator(), new NullContext(temp.Combine("data")), new PluginPreferences(() => temp.Combine("data")));
        Assert.IsTrue(decorator.AppliesTo(repo));
        var entries = new[] { Entry(repo, "tracked.txt", false), Entry(repo, "src", true), Entry(repo, "new.txt", false) };
        var steps = new List<FolderDecoration>();
        await foreach (var step in decorator.DecorateAsync(new FolderDecorationRequest(repo, entries, new HashSet<string> { "status", "commit", "author" }, FolderDecorationReason.Opened), CancellationToken.None))
            steps.Add(step);

        Assert.AreEqual(2, steps.Count);
        Assert.AreEqual("main", steps[0].Status?.Text);
        CollectionAssert.AreEqual(new[] { "pull", "push", "fetch" }, steps[0].Status!.Commands.Select(c => c.Id).ToList());
        Assert.AreEqual("Modified", steps[0].Items["tracked.txt"].Cells!["status"]);
        Assert.AreEqual("AlphaMBox", steps[0].Items["tracked.txt"].Badge?.IconKey);
        Assert.AreEqual("Untracked", steps[0].Items["new.txt"].Cells!["status"]);
        Assert.IsFalse(steps[0].Items.ContainsKey("src")); // clean

        Assert.AreEqual("First commit", steps[1].Items["tracked.txt"].Cells!["commit"]);
        Assert.AreEqual("Ada", steps[1].Items["src"].Cells!["author"]);
        Assert.IsFalse(steps[1].Items.ContainsKey("new.txt"));
    }

    private static async Task Git(string repository, params string[] arguments)
    {
        var result = await PluginProcess.RunAsync(new PluginProcessRequest(PluginProcess.FindExecutable("git")!, ["-C", repository, .. arguments]), CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode, result.Error);
    }

    private static FileEntry Entry(string folder, string name, bool isDirectory)
        => new(name, Path.Combine(folder, name), isDirectory, 1, DateTime.UtcNow, DateTime.UtcNow, isDirectory ? FileAttributes.Directory : FileAttributes.Normal);

    // ── Cloud sync folders ───────────────────────────────────

    [TestMethod]
    [DataRow(0x20, "Local")]                // archive only: a downloaded file
    [DataRow(0x400000 | 0x20, "OnlineOnly")] // RECALL_ON_DATA_ACCESS: a placeholder file
    [DataRow(0x40000 | 0x10, "OnlineOnly")]  // RECALL_ON_OPEN: a folder not populated yet
    [DataRow(0x1000, "OnlineOnly")]          // OFFLINE
    [DataRow(0x80000 | 0x20, "AlwaysAvailable")]
    [DataRow(0x80000 | 0x400000, "Downloading")]
    [DataRow(0x100000 | 0x20, "Local")]      // UNPINNED but still local
    public void CloudFilesAttributes_MapToSyncStates(int attributes, string expected)
        => Assert.AreEqual(expected, SyncStates.FromWindowsAttributes(attributes).ToString());

    [TestMethod]
    [DataRow("OneDrive-Personal", "OneDrive", "OneDrive - Personal")]
    [DataRow("OneDrive-Contoso", "OneDrive", "OneDrive - Contoso")]
    [DataRow("Dropbox", "Dropbox", "Dropbox")]
    [DataRow("GoogleDrive-me@example.com", "GoogleDrive", "Google Drive - me@example.com")]
    [DataRow("Box-Box", "Box", "Box")]
    [DataRow("Acme-Sync", "Other", "Acme - Sync")]
    public void CloudStorageFolders_AreNamedLikeFinderNamesThem(string folder, string provider, string name)
    {
        var (actualProvider, actualName) = SyncRootLocator.DescribeCloudStorageFolder(folder);
        Assert.AreEqual(provider, actualProvider.ToString());
        Assert.AreEqual(name, actualName);
    }

    [TestMethod]
    public void SyncRoots_FromDropboxOneDriveAndNextcloudConfigurations()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("Covers the Linux clients.");
            return;
        }

        using var temp = new TempDirectory();
        var dropbox = temp.CreateDirectory("home/Dropbox");
        var work = temp.CreateDirectory("home/Work Dropbox");
        temp.CreateDirectory("home/OneDrive Sync");
        var nextcloud = temp.CreateDirectory("home/Nextcloud");
        temp.CreateFile("home/.dropbox/info.json", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>> { ["personal"] = new() { ["path"] = dropbox }, ["business"] = new() { ["path"] = work } }));
        temp.CreateFile("home/.config/onedrive/config", "# comment\nsync_dir = \"~/OneDrive Sync\"\n");
        temp.CreateFile("home/.config/Nextcloud/nextcloud.cfg", $"[Accounts]\n0\\Folders\\1\\localPath={nextcloud}/\n0\\Folders\\2\\localPath=/does/not/exist\n");

        var home = temp.Combine("home");
        var roots = SyncRootLocator.Discover(new SyncRootSources(home, null, null, _ => null));
        CollectionAssert.AreEquivalent(new[] { dropbox, work, temp.Combine("home", "OneDrive Sync"), nextcloud }, roots.Select(r => r.Path).ToList());
        Assert.AreEqual("Dropbox (Business)", roots.Single(r => r.Path == work).DisplayName);

        var locator = new SyncRootLocator(new SyncRootSources(home, null, null, _ => null));
        Assert.AreEqual(dropbox, locator.FindRoot(Path.Combine(dropbox, "Photos", "2024"))?.Path);
        Assert.IsNull(locator.FindRoot(dropbox + "2"));
        Assert.IsNull(locator.FindRoot(home));
    }

    [TestMethod]
    public async Task CloudSyncDecorator_ShowsStatusAndBadges()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("Linux: every item of a sync folder is a local copy.");
            return;
        }

        using var temp = new TempDirectory();
        var dropbox = temp.CreateDirectory("home/Dropbox");
        temp.CreateFile("home/.dropbox/info.json", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>> { ["personal"] = new() { ["path"] = dropbox } }));
        var locator = new SyncRootLocator(new SyncRootSources(temp.Combine("home"), null, null, _ => null));
        var preferences = new PluginPreferences(() => temp.Combine("data"));
        var decorator = new CloudSyncDecorator(locator, preferences, new PluginStrings(typeof(CloudSyncPlugin).Assembly, () => "en"));

        Assert.IsTrue(decorator.AppliesTo(dropbox));
        Assert.IsFalse(decorator.AppliesTo(temp.Path));
        var request = new FolderDecorationRequest(dropbox, [Entry(dropbox, "a.txt", false)], new HashSet<string> { "status" }, FolderDecorationReason.Opened);
        var steps = new List<FolderDecoration>();
        await foreach (var step in decorator.DecorateAsync(request, CancellationToken.None))
            steps.Add(step);
        Assert.AreEqual("Available on this device", steps.Single().Items["a.txt"].Cells!["status"]);
        Assert.AreEqual("CheckCircleOutline", steps.Single().Items["a.txt"].Badge?.IconKey);

        preferences.SetBool(CloudSyncPlugin.BadgesPreference, false);
        await foreach (var step in decorator.DecorateAsync(request, CancellationToken.None))
            Assert.IsNull(step.Items["a.txt"].Badge);
    }

    // ── WSL ──────────────────────────────────────────────────

    [TestMethod]
    public void Wsl_DistributionPathsIconsAndOrder()
    {
        var ubuntu = new WslDistribution("Ubuntu-24.04", 2, IsDefault: false, Flavor: "ubuntu");
        Assert.AreEqual(@"\\wsl.localhost\Ubuntu-24.04\", ubuntu.RootPath(22631));
        Assert.AreEqual(@"\\wsl$\Ubuntu-24.04\", ubuntu.RootPath(19045));
        Assert.AreEqual("Ubuntu", ubuntu.IconKey);
        Assert.AreEqual("Debian", new WslDistribution("kali-linux", 2, false, null).IconKey);
        Assert.AreEqual("Linux", new WslDistribution("openSUSE-Tumbleweed", 2, false, null).IconKey);

        var sorted = WslRegistry.Sort([ubuntu, new WslDistribution("Alpine", 2, false, null), new WslDistribution("Debian", 1, true, null)]);
        CollectionAssert.AreEqual(new[] { "Debian", "Alpine", "Ubuntu-24.04" }, sorted.Select(d => d.Name).ToList());
    }

    // ── Windows Search ───────────────────────────────────────

    [TestMethod]
    public void WindowsSearchSql_NameSearchInAFolderTree()
    {
        var sql = SearchSql.Build(new PluginSearchRequest(@"C:\Users\me\O'Brien", "report", null, Recursive: true, IncludeHidden: false, MaxResults: 500), 501);
        Assert.AreEqual("SELECT TOP 501 " + SearchSql.Columns + " FROM SystemIndex WHERE SCOPE='file:C:/Users/me/O''Brien/' AND System.FileName LIKE '%report%'", sql);
    }

    [TestMethod]
    public void WindowsSearchSql_PatternsContentsAndOneFolder()
    {
        var sql = SearchSql.Build(new PluginSearchRequest(@"D:\Docs", string.Empty, "budget \"2025\"", Recursive: false, IncludeHidden: true, MaxResults: 10), 10);
        Assert.AreEqual("SELECT TOP 10 " + SearchSql.Columns + " FROM SystemIndex WHERE DIRECTORY='file:D:/Docs/' AND CONTAINS(*, '\"budget 2025*\"')", sql);
        Assert.AreEqual("%.txt", SearchSql.LikePattern("*.txt"));
        Assert.AreEqual("a_[_]b%", SearchSql.LikePattern("a?_b*"));
        Assert.AreEqual("%50[%] off%", SearchSql.LikePattern("50% off"));
    }

    [TestMethod]
    [DataRow(@"C:\Users", true)]
    [DataRow("D:/", true)]
    [DataRow(@"\\server\share", false)]
    [DataRow("/home/me", false)]
    [DataRow("C:", false)]
    public void WindowsSearch_OnlySearchesLocalDrives(string root, bool expected)
        => Assert.AreEqual(expected, WindowsSearchProviderRules.IsLocalFolder(root));

    // ── Windows 11 app commands ──────────────────────────────

    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
                 xmlns:desktop5="http://schemas.microsoft.com/appx/manifest/desktop/windows10/5"
                 xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10">
          <Applications>
            <Application Id="App">
              <Extensions>
                <desktop4:Extension Category="windows.fileExplorerContextMenus">
                  <desktop4:FileExplorerContextMenus>
                    <desktop5:ItemType Type="*">
                      <desktop5:Verb Id="EditWithApp" Clsid="11111111-2222-3333-4444-555555555555" />
                    </desktop5:ItemType>
                    <desktop5:ItemType Type=".txt">
                      <desktop5:Verb Id="Summarize" Clsid="{66666666-2222-3333-4444-555555555555}" />
                    </desktop5:ItemType>
                    <desktop5:ItemType Type="Directory">
                      <desktop5:Verb Id="OpenFolder" Clsid="77777777-2222-3333-4444-555555555555" />
                    </desktop5:ItemType>
                    <desktop5:ItemType Type="Directory\Background">
                      <desktop5:Verb Id="OpenHere" Clsid="88888888-2222-3333-4444-555555555555" />
                    </desktop5:ItemType>
                  </desktop4:FileExplorerContextMenus>
                </desktop4:Extension>
                <com:Extension Category="windows.comServer">
                  <com:ComServer><com:SurrogateServer DisplayName="X"><com:Class Id="11111111-2222-3333-4444-555555555555" Path="x.dll" ThreadingModel="STA" /></com:SurrogateServer></com:ComServer>
                </com:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    [TestMethod]
    public void AppCommands_AreReadFromTheManifest()
    {
        var verbs = PackagedVerbManifest.Parse("Acme.App_1.0_x64__abc", Manifest);
        Assert.AreEqual(4, verbs.Count);
        Assert.AreEqual(new PackagedVerb("Acme.App_1.0_x64__abc", "*", "EditWithApp", new Guid("11111111-2222-3333-4444-555555555555")), verbs[0]);
        Assert.AreEqual(".txt", verbs[1].ItemType);
        Assert.AreEqual(@"Directory\Background", verbs[3].ItemType);
    }

    [TestMethod]
    public void AppCommands_MatchTheSelection()
    {
        var verbs = PackagedVerbManifest.Parse("p", Manifest);
        string Ids(IReadOnlyList<(string, bool)> targets, bool background = false)
            => string.Join(",", PackagedVerbManifest.Matching(verbs, targets, background).Select(v => v.VerbId));

        Assert.AreEqual("EditWithApp,Summarize", Ids([(@"C:\a\notes.TXT", false)]));
        Assert.AreEqual("EditWithApp", Ids([(@"C:\a\notes.txt", false), (@"C:\a\photo.jpg", false)]));
        Assert.AreEqual("OpenFolder", Ids([(@"C:\a\folder", true)]));
        Assert.AreEqual(string.Empty, Ids([(@"C:\a\folder", true), (@"C:\a\file.txt", false)]));
        Assert.AreEqual("OpenHere", Ids([(@"C:\a", true)], background: true));
        Assert.AreEqual(string.Empty, Ids([]));
    }

    // ── Default file manager ─────────────────────────────────

    [TestMethod]
    public void DefaultFileManager_LinuxDesktopEntryHandlesFolders()
    {
        var entry = LinuxDesktopEntry.For("/opt/Navigator App/Navigator");
        StringAssert.Contains(entry, "Exec=\"/opt/Navigator App/Navigator\" %U");
        StringAssert.Contains(entry, "MimeType=inode/directory;");
        StringAssert.Contains(entry, "NoDisplay=true");
        Assert.IsTrue(entry.StartsWith("[Desktop Entry]\n", StringComparison.Ordinal));
    }

    // ── Fakes ────────────────────────────────────────────────

    private static string? RepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, typeof(IntegrationPluginTests).Assembly.Location })
        {
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(start) ?? start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Navigator.slnx")))
                    return directory.FullName;
            }
        }

        return null;
    }

    private static BuiltInPlugin Built(string id, INavigatorPlugin plugin)
        => new(new PluginManifest { Id = id, Name = id, Version = "1.0.0", ApiVersion = PluginApi.Version }, () => plugin);

    private sealed class ContributingPlugin : INavigatorPlugin
    {
        public FakeDecorator Decorator { get; } = new("acme");

        public FakeMenu Menu { get; } = new();

        public FakeSearch Search { get; } = new();

        public FakePage Page { get; } = new();

        public void Initialize(IPluginContext context)
        {
            context.AddFolderDecorator(Decorator);
            context.AddMenuProvider(Menu);
            context.AddSearchProvider(Search);
            context.SetSettingsPage(Page);
        }
    }

    private sealed class CallbackPlugin(Action<IPluginContext> initialize) : INavigatorPlugin
    {
        public void Initialize(IPluginContext context) => initialize(context);
    }

    private sealed class FakeDecorator(string id, IReadOnlyList<PluginColumn>? columns = null) : IFolderDecorator
    {
        public string Id => id;

        public IReadOnlyList<PluginColumn> Columns => columns ?? [];

        public event EventHandler<FolderDecorationsChangedEventArgs>? Changed;

        public bool AppliesTo(string folderPath) => true;

        public async IAsyncEnumerable<FolderDecoration> DecorateAsync(FolderDecorationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            Changed?.Invoke(this, FolderDecorationsChangedEventArgs.Everywhere);
            yield return new FolderDecoration();
        }

        public Task ExecuteAsync(string commandId, string folderPath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeMenu : IItemMenuProvider
    {
        public string Id => "menu";

        public Task<IReadOnlyList<PluginMenuItem>> GetItemsAsync(PluginMenuRequest request, CancellationToken menuLifetime)
            => Task.FromResult<IReadOnlyList<PluginMenuItem>>([new PluginMenuItem("Do it")]);
    }

    private sealed class FakeSearch : ISearchProvider
    {
        public string Id => "search";

        public ValueTask<bool> CanSearchAsync(PluginSearchRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> SearchAsync(PluginSearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class FakePage : IPluginSettingsPage
    {
        public event EventHandler? Changed;

        public IReadOnlyList<PluginSetting> GetSettings() => [new PluginSetting("x", PluginSettingKind.Info, "X")];

        public Task ApplyAsync(string settingId, bool? value, CancellationToken cancellationToken)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class NullContext(string data) : IPluginContext
    {
        public string PluginId => "git";

        public string DataDirectory => Directory.CreateDirectory(data).FullName;

        public string Language => "en";

        public IPluginUi Ui => throw new NotSupportedException();

        public IPluginLog Log { get; } = new NullLog();

        public IVolumeProvider Volumes => throw new NotSupportedException();

        public IPlatformInfo Platform => throw new NotSupportedException();

        public IPluginSecretStore Secrets => throw new NotSupportedException();

        public bool OpenInBrowser(Uri address) => false;

        public string GetHostString(string key) => key;

        public VolumeDisplay DescribeVolume(VolumeInfo volume) => throw new NotSupportedException();

        public void AddSidebarSection(IPluginSidebarSection section) => throw new NotSupportedException();

        public void AddFileSystem(IRemoteFileSystem fileSystem) => throw new NotSupportedException();

        public void AddFolderDecorator(IFolderDecorator decorator) => throw new NotSupportedException();

        public void AddMenuProvider(IItemMenuProvider provider) => throw new NotSupportedException();

        public void AddSearchProvider(ISearchProvider provider) => throw new NotSupportedException();

        public void SetSettingsPage(IPluginSettingsPage page) => throw new NotSupportedException();
    }

    private sealed class NullLog : IPluginLog
    {
        public void Info(string message)
        {
        }

        public void Warning(string message, Exception? exception = null)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }

    private sealed class HostServices : IPluginHostServices
    {
        public IPluginUi Ui => throw new NotSupportedException();

        public IVolumeProvider Volumes => throw new NotSupportedException();

        public IPlatformInfo Platform => throw new NotSupportedException();

        public string Language => "en";

        public string GetString(string key) => key;

        public VolumeDisplay DescribeVolume(VolumeInfo volume) => throw new NotSupportedException();

        public IPluginLog CreateLog(string pluginId) => new NullLog();

        public ISecretStore Secrets { get; } = new Navigator.Platform.Secrets.UnsupportedSecretStore();

        public bool OpenInBrowser(Uri address) => false;
    }
}
