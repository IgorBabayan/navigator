using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Plugins;
using Navigator.Plugins.Remote;
using Navigator.ReleaseTool;
using Navigator.Tests;
using Navigator.Updater;

namespace Navigator.UnitTests;

[TestClass]
public sealed class PluginTests
{
    // ── Contract version ─────────────────────────────────────

    [TestMethod]
    [DataRow("1.0", true)]
    [DataRow("1", true)]
    [DataRow("1.1", true)]
    [DataRow("1.2", true)]
    [DataRow("1.3", false)]
    [DataRow("2.0", false)]
    [DataRow("0.9", false)]
    [DataRow("", false)]
    [DataRow("x.y", false)]
    [DataRow("1.0.0", false)]
    public void PluginApi_AcceptsSameMajorAndNotNewerMinor(string version, bool expected)
        => Assert.AreEqual(expected, PluginApi.IsCompatible(version));

    [TestMethod]
    public void PluginApi_MatchesTheVersionPackagedPluginsAreBuiltAgainst()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot(), "plugins", "Directory.Build.props"));
        var packaged = props.Descendants("NavigatorPluginApiVersion").Single().Value;
        Assert.AreEqual(PluginApi.Version, packaged);
    }

    // ── Manifest ─────────────────────────────────────────────

    [TestMethod]
    public void Manifest_ParsesValidJson_WithOrWithoutByteOrderMark()
    {
        var json = ManifestJson("ssh");
        Assert.AreEqual("ssh", PluginManifest.Parse(json).Id);
        Assert.AreEqual("1.2.3", PluginManifest.Parse([0xEF, 0xBB, 0xBF, .. json]).Version);
    }

    [TestMethod]
    [DataRow("../evil")]
    [DataRow("UPPER")]
    [DataRow("a")]
    [DataRow("a..b")]
    [DataRow("with space")]
    public void Manifest_RejectsUnsafeIds(string id)
        => Assert.ThrowsExactly<InvalidDataException>(() => PluginManifest.Parse(ManifestJson(id)));

    [TestMethod]
    public void Manifest_RequiresAnEntryPointInsideThePackage()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => PluginManifest.Parse(ManifestJson("ssh", assembly: "../Navigator.Plugin.Ssh.dll")));
        Assert.ThrowsExactly<InvalidDataException>(() => PluginManifest.Parse(ManifestJson("ssh", assembly: "plugin.exe")));
        Assert.ThrowsExactly<InvalidDataException>(() => PluginManifest.Parse(ManifestJson("ssh", entryType: "")));
        Assert.ThrowsExactly<InvalidDataException>(() => PluginManifest.Parse("not json"u8));
    }

    // ── Packages ─────────────────────────────────────────────

    [TestMethod]
    public void Package_ExtractsFilesAndReturnsTheManifest()
    {
        using var temp = new TempDirectory();
        var package = CreatePackage(temp, "ssh", ("Navigator.Plugin.Ssh.dll", "dll"), ("lib/Dependency.dll", "dep"));
        var manifest = PluginPackage.Extract(package, temp.Combine("out"));
        Assert.AreEqual("ssh", manifest.Id);
        Assert.AreEqual("dep", File.ReadAllText(temp.Combine("out", "lib", "Dependency.dll")));
    }

    [TestMethod]
    [DataRow("../escape.dll")]
    [DataRow("lib/../../escape.dll")]
    [DataRow("/absolute.dll")]
    [DataRow("C:/drive.dll")]
    public void Package_RejectsPathsOutsideItsFolder(string entry)
    {
        using var temp = new TempDirectory();
        var package = CreatePackage(temp, "ssh", ("Navigator.Plugin.Ssh.dll", "dll"), (entry, "evil"));
        Assert.ThrowsExactly<InvalidDataException>(() => PluginPackage.Extract(package, temp.Combine("out", "inner")));
        Assert.IsFalse(File.Exists(temp.Combine("out", "escape.dll")));
        Assert.IsFalse(File.Exists(temp.Combine("escape.dll")));
    }

    [TestMethod]
    public void Package_RejectsSymbolicLinksAndMissingAssembly()
    {
        using var temp = new TempDirectory();
        var linked = temp.Combine("linked.navplugin");
        using (var zip = ZipFile.Open(linked, ZipArchiveMode.Create))
        {
            WriteEntry(zip, PluginManifest.FileName, Encoding.UTF8.GetString(ManifestJson("ssh")));
            WriteEntry(zip, "Navigator.Plugin.Ssh.dll", "dll");
            var link = zip.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)0xA1FF0000); // S_IFLNK | 0777
            using var stream = link.Open();
            stream.Write("/etc/passwd"u8);
        }

        Assert.ThrowsExactly<InvalidDataException>(() => PluginPackage.Extract(linked, temp.Combine("out")));
        var noAssembly = CreatePackage(temp, "ssh");
        Assert.ThrowsExactly<InvalidDataException>(() => PluginPackage.ReadManifest(noAssembly));
    }

    // ── Catalog ──────────────────────────────────────────────

    [TestMethod]
    public void Catalog_RoundTripsAndRejectsUnsafeEntries()
    {
        var entry = PluginCatalogEntry.FromManifest(Manifest("ftp"), PluginCatalog.PackageName("ftp", "1.2.3"), 10, new string('a', 64));
        var catalog = new PluginCatalog { AppVersion = "1.0.0", Plugins = [entry] };
        var parsed = PluginCatalog.Parse(catalog.Serialize());
        Assert.AreEqual("navigator-plugin-ftp-1.2.3.navplugin", parsed.Plugins.Single().Package);
        Assert.IsFalse(Encoding.UTF8.GetString(catalog.Serialize()).Contains("isCompatible", StringComparison.Ordinal));

        catalog.Plugins = [entry, entry];
        Assert.ThrowsExactly<InvalidDataException>(() => PluginCatalog.Parse(catalog.Serialize()));
        catalog.Plugins = [PluginCatalogEntry.FromManifest(Manifest("ftp"), "../ftp.navplugin", 10, new string('a', 64))];
        Assert.ThrowsExactly<InvalidDataException>(() => PluginCatalog.Parse(catalog.Serialize()));
        catalog.Plugins = [PluginCatalogEntry.FromManifest(Manifest("ftp"), "ftp.navplugin", 10, "short")];
        Assert.ThrowsExactly<InvalidDataException>(() => PluginCatalog.Parse(catalog.Serialize()));
        Assert.ThrowsExactly<InvalidDataException>(() => PluginCatalog.Parse("""{"schemaVersion":99,"plugins":[]}"""u8));
    }

    [TestMethod]
    public async Task ReleaseTool_BuildsCatalogFromPackages_AndRejectsMislabelledOnes()
    {
        using var temp = new TempDirectory();
        var good = CreatePackage(temp, "ftp", ("Navigator.Plugin.Ftp.dll", "dll"));
        File.Move(good, temp.Combine(PluginCatalog.PackageName("ftp", "1.2.3")));
        var catalog = await ReleaseLogic.BuildPluginCatalogAsync(temp.Path, "2.0.0", DateTimeOffset.UnixEpoch);
        Assert.AreEqual("ftp", catalog.Plugins.Single().Id);
        Assert.AreEqual(64, catalog.Plugins.Single().Sha256.Length);

        File.Copy(temp.Combine(PluginCatalog.PackageName("ftp", "1.2.3")), temp.Combine("navigator-plugin-ftp-9.9.9.navplugin"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReleaseLogic.BuildPluginCatalogAsync(temp.Path, "2.0.0", DateTimeOffset.UnixEpoch));
    }

    [TestMethod]
    public async Task StoreClient_RequiresAValidSignatureWhenAKeyIsCompiledIn()
    {
        using var temp = new TempDirectory();
        var (privateKey, publicKey) = ManifestSignature.CreateKeyPair();
        var bytes = new PluginCatalog { Plugins = [] }.Serialize();
        var path = temp.Combine(PluginCatalog.FileName);
        File.WriteAllBytes(path, bytes);
        using var http = new HttpClient(new FakeHttpHandler());
        PluginStoreClient Client(string? key) => new(http, PluginStoreOptions.Create(null, key, temp.Combine("dl"), name => name == PluginStoreOptions.CatalogOverrideVariable ? path : null));

        Assert.IsFalse((await Client(null).GetCatalogAsync(CancellationToken.None)).SignatureVerified);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Client(publicKey).GetCatalogAsync(CancellationToken.None)); // missing .sig

        File.WriteAllText(temp.Combine(PluginCatalog.SignatureFileName), ManifestSignature.Sign(bytes, privateKey));
        Assert.IsTrue((await Client(publicKey).GetCatalogAsync(CancellationToken.None)).SignatureVerified);

        File.WriteAllBytes(path, [.. bytes, (byte)' ']); // tampered
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Client(publicKey).GetCatalogAsync(CancellationToken.None));
    }

    [TestMethod]
    public void StoreOptions_UseTheLatestReleaseOfTheAppRepository()
    {
        var options = PluginStoreOptions.Create("owner/repo", null, "/tmp", _ => null);
        Assert.AreEqual("https://github.com/owner/repo/releases/latest/download/plugin-catalog.json", options.CatalogLocation);
        Assert.IsNull(PluginStoreOptions.Create(null, null, "/tmp", _ => null).CatalogLocation); // dev builds: no shop
        Assert.AreEqual("https://github.com/owner/repo/releases/latest/download/navigator-plugin-ssh-1.0.0.navplugin",
            PluginStoreClient.Sibling(options.CatalogLocation!, "navigator-plugin-ssh-1.0.0.navplugin"));
    }

    // ── Host ─────────────────────────────────────────────────

    [TestMethod]
    public async Task Host_BuiltInPluginIsPermanent_AndItsContributionsArePublished()
    {
        using var temp = new TempDirectory();
        using var host = new PluginHost(temp.Combine("plugins"), new FakeHostServices(), [BuiltIn("network", new ContributingPlugin())]);
        await host.StartAsync();

        Assert.AreEqual("network", host.SidebarSections.Single().Id);
        Assert.IsTrue(host.TryGetFileSystem("TEST", out _), "schemes are case-insensitive");
        var info = host.Find("network")!;
        Assert.IsTrue(info.IsPermanent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.UninstallAsync("network"));
        Assert.AreEqual(PluginStatus.Active, host.Find("network")!.Status);
    }

    [TestMethod]
    public async Task Host_PackageCannotReplaceABuiltInPlugin()
    {
        using var temp = new TempDirectory();
        using var host = new PluginHost(temp.Combine("plugins"), new FakeHostServices(), [BuiltIn("network", new ContributingPlugin())]);
        await host.StartAsync();
        var package = CreatePackage(temp, "network", ("Navigator.Plugin.Network.dll", "dll"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => host.InstallAsync(package, null, CancellationToken.None));
        Assert.IsFalse(File.Exists(temp.Combine("plugins", PluginState.FileName)));
    }

    [TestMethod]
    public async Task Host_AFailingPluginDoesNotStopTheOthers()
    {
        using var temp = new TempDirectory();
        using var host = new PluginHost(temp.Combine("plugins"), new FakeHostServices(),
            [BuiltIn("broken", new ThrowingPlugin()), BuiltIn("network", new ContributingPlugin())]);
        await host.StartAsync();
        Assert.AreEqual(PluginStatus.Failed, host.Find("broken")!.Status);
        Assert.AreEqual(PluginStatus.Active, host.Find("network")!.Status);
        Assert.AreEqual(1, host.SidebarSections.Count);
    }

    [TestMethod]
    public async Task Host_UnreadableStateLoadsNothing_AndInvalidPackagesAreReportedNotLoaded()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("plugins");
        File.WriteAllText(Path.Combine(folder, PluginState.FileName), "{ not json");
        using (var host = new PluginHost(folder, new FakeHostServices(), []))
        {
            await host.StartAsync();
            Assert.AreEqual(0, host.GetPlugins().Count);
        }

        // A package whose assembly is not a .NET assembly installs as "failed", not as a crash.
        using var second = new PluginHost(temp.Combine("plugins2"), new FakeHostServices(), []);
        await second.StartAsync();
        var info = await second.InstallAsync(CreatePackage(temp, "ftp", ("Navigator.Plugin.Ftp.dll", "not an assembly")), "ftp", CancellationToken.None);
        Assert.AreEqual(PluginStatus.Failed, info.Status);
        await second.UninstallAsync("ftp");
        Assert.IsTrue(second.Find("ftp") is null or { Status: PluginStatus.Removed });
    }

    // ── Plugin directories ───────────────────────────────────

    [TestMethod]
    public void AppDirectories_PluginsLiveInMachineLocalData()
    {
        static string? NoEnv(string _) => null;
        Assert.AreEqual(Path.Combine("/home/u", ".local", "share", "navigator", "plugins"), AppDirectories.Resolve(NoEnv, OperatingSystemKind.Linux, "/home/u").PluginDirectory);
        Assert.AreEqual(Path.Combine("/p", "plugins"), AppDirectories.Resolve(k => k == AppDirectories.OverrideVariable ? "/p" : null, OperatingSystemKind.Linux, "/h").PluginDirectory);
    }

    // ── Remote addresses ─────────────────────────────────────

    [TestMethod]
    public void RemoteUri_ParsesAndNormalizesAddresses()
    {
        var address = RemoteUri.Parse("SFTP://me%40corp@Example.COM:2222//home//me/./docs/../files/");
        Assert.AreEqual("sftp", address.Scheme);
        Assert.AreEqual("me@corp", address.UserName);
        Assert.AreEqual("example.com", address.Host);
        Assert.AreEqual(2222, address.Port);
        Assert.AreEqual("/home/me/files", address.Path);
        Assert.AreEqual("sftp://me%40corp@example.com:2222/home/me/files", address.ToString());

        var ipv6 = RemoteUri.Parse("ftp://[::1]:21/");
        Assert.AreEqual("[::1]", ipv6.Host);
        Assert.IsTrue(ipv6.IsRoot);
        Assert.IsFalse(RemoteUri.TryParse("ftp://host:99999/", out _));
        Assert.IsFalse(RemoteUri.TryParse("ftp://bad host/", out _));
    }

    [TestMethod]
    [DataRow(@"C:\Users", false)]
    [DataRow("/home/me", false)]
    [DataRow("file:///home/me", false)]
    [DataRow("sftp://host", true)]
    [DataRow("ftps://host/x", true)]
    [DataRow("1ftp://host", false)]
    public void RemoteUri_RecognizesOnlyPluginAddresses(string value, bool expected)
        => Assert.AreEqual(expected, RemoteUri.IsRemote(value));

    [TestMethod]
    public void RemoteUri_NavigatesUpAndBuildsBreadcrumbs()
    {
        Assert.AreEqual("sftp://h/a", RemoteUri.GetParent("sftp://h/a/b"));
        Assert.AreEqual("sftp://h/", RemoteUri.GetParent("sftp://h/a"));
        Assert.IsNull(RemoteUri.GetParent("sftp://h/"));
        Assert.AreEqual("sftp://h/a/b c", RemoteUri.Combine("sftp://h/a", "b c"));
        Assert.AreEqual("u@h", RemoteUri.GetName("sftp://u@h/"));
        var segments = RemoteUri.GetSegments("sftp://u@h/a/b");
        Assert.AreEqual(3, segments.Count);
        Assert.AreEqual(("b", "sftp://u@h/a/b"), segments[2]);
    }

    [TestMethod]
    public void Location_RemoteRoundTripsAndComparesExactly()
    {
        var location = Location.FromPath("sftp://u@h/Docs");
        Assert.AreEqual(LocationKind.Remote, location.Kind);
        Assert.AreEqual(location, Location.TryParse(location.Serialize()));
        Assert.AreNotEqual(location, Location.Remote("sftp://u@h/docs"), "server paths are case-sensitive");
        Assert.AreEqual(LocationKind.Folder, Location.FromPath("/home/u").Kind);
    }

    [TestMethod]
    public void NetworkAddresses_MapSharesToUncFoldersAndBack()
    {
        Assert.IsNull(Navigator.Plugin.Network.NetworkAddresses.ToSharePath(RemoteUri.Parse("network://computers/")));
        Assert.IsNull(Navigator.Plugin.Network.NetworkAddresses.ToSharePath(RemoteUri.Parse("network://computers/NAS")));
        Assert.AreEqual(@"\\NAS\Media", Navigator.Plugin.Network.NetworkAddresses.ToSharePath(RemoteUri.Parse("network://computers/NAS/Media")));
        Assert.AreEqual(@"\\NAS\Media\Films 2024", Navigator.Plugin.Network.NetworkAddresses.ToSharePath(RemoteUri.Parse("network://computers/NAS/Media/Films 2024")));
        Assert.IsNull(Navigator.Plugin.Network.NetworkAddresses.ToSharePath(RemoteUri.Parse("sftp://computers/NAS/Media")));

        Assert.AreEqual("network://computers/NAS", Navigator.Plugin.Network.NetworkAddresses.ServerOfShareRoot(@"\\NAS\Media"));
        Assert.AreEqual("network://computers/NAS", Navigator.Plugin.Network.NetworkAddresses.ServerOfShareRoot(@"\\NAS\Media\"));
        Assert.IsNull(Navigator.Plugin.Network.NetworkAddresses.ServerOfShareRoot(@"\\NAS\Media\Films"));
        Assert.IsNull(Navigator.Plugin.Network.NetworkAddresses.ServerOfShareRoot(@"\\?\C:\Temp"));
        Assert.IsNull(Navigator.Plugin.Network.NetworkAddresses.ServerOfShareRoot(@"C:\Users"));
        Assert.IsFalse(Navigator.Plugin.Network.NetworkAddresses.IsValidServer("a\\b"));
    }

    // ── Session pool ─────────────────────────────────────────

    [TestMethod]
    public async Task SessionPool_SharedModeConnectsOnceForConcurrentCallers()
    {
        var connects = 0;
        using var pool = new RemoteSessionPool<FakeSession>(async (_, token) =>
        {
            Interlocked.Increment(ref connects);
            await Task.Delay(20, token);
            return new FakeSession();
        }, s => !s.IsDisposed, exclusive: false, maxSessionsPerServer: 1, TimeSpan.FromMinutes(1));

        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => pool.AcquireAsync("sftp://h", CancellationToken.None)));
        Assert.AreEqual(1, connects);
        Assert.AreEqual(1, leases.Select(l => l.Session).Distinct().Count());

        leases[0].Fail();
        foreach (var lease in leases)
            lease.Dispose();
        Assert.IsTrue(leases[0].Session.IsDisposed, "a failed session is closed once its users are done");
        using var fresh = await pool.AcquireAsync("sftp://h", CancellationToken.None);
        Assert.AreEqual(2, connects);
    }

    [TestMethod]
    public async Task SessionPool_ExclusiveModeLimitsAndReusesSessions()
    {
        var connects = 0;
        using var pool = new RemoteSessionPool<FakeSession>((_, _) =>
        {
            Interlocked.Increment(ref connects);
            return Task.FromResult(new FakeSession());
        }, s => !s.IsDisposed, exclusive: true, maxSessionsPerServer: 2, TimeSpan.FromMinutes(1));

        var first = await pool.AcquireAsync("ftp://h", CancellationToken.None);
        var second = await pool.AcquireAsync("ftp://h", CancellationToken.None);
        Assert.AreNotSame(first.Session, second.Session);
        var third = pool.AcquireAsync("ftp://h", CancellationToken.None);
        await Task.Delay(50, CancellationToken.None);
        Assert.IsFalse(third.IsCompleted, "a third caller waits for a free session");
        var reused = first.Session;
        first.Dispose();
        using var thirdLease = await third;
        Assert.AreSame(reused, thirdLease.Session);
        Assert.AreEqual(2, connects);
        second.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────

    private static PluginManifest Manifest(string id) => new() { Id = id, Name = id.ToUpperInvariant(), Version = "1.2.3", ApiVersion = PluginApi.Version, Assembly = "Navigator.Plugin.X.dll", EntryType = "X" };

    private static byte[] ManifestJson(string id, string? assembly = null, string entryType = "Navigator.Plugin.Ssh.SshPlugin")
        => Encoding.UTF8.GetBytes($$"""
            {"id":"{{id}}","name":"Plugin","version":"1.2.3","apiVersion":"{{PluginApi.Version}}","assembly":"{{assembly ?? $"Navigator.Plugin.{char.ToUpperInvariant(id[0])}{id[1..]}.dll"}}","entryType":"{{entryType}}"}
            """);

    private static string CreatePackage(TempDirectory temp, string id, params (string Name, string Content)[] files)
    {
        var path = temp.Combine($"{id}-{Guid.NewGuid():N}.navplugin");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, PluginManifest.FileName, Encoding.UTF8.GetString(ManifestJson(id)));
        foreach (var (name, content) in files)
            WriteEntry(zip, name, content);
        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static BuiltInPlugin BuiltIn(string id, INavigatorPlugin plugin)
        => new(new PluginManifest { Id = id, Name = id, Version = "1.0.0", ApiVersion = PluginApi.Version }, () => plugin);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Navigator.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FakeSession : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class ContributingPlugin : INavigatorPlugin
    {
        public void Initialize(IPluginContext context)
        {
            context.AddSidebarSection(new Section());
            context.AddFileSystem(new NoFileSystem());
        }
    }

    private sealed class ThrowingPlugin : INavigatorPlugin
    {
        public void Initialize(IPluginContext context) => throw new InvalidOperationException("boom");
    }

    private sealed class Section : IPluginSidebarSection
    {
        public string Id => "network";

        public string Title => "Network";

        public string IconKey => "DriveNetwork";

        public string? SystemIconKey => null;

        public int Order => 100;

        public bool ShowWhenEmpty => false;

        public IReadOnlyList<PluginCommand> Commands => [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<PluginSidebarEntry>>([]);

        public Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoFileSystem : IRemoteFileSystem
    {
        public IReadOnlyList<string> Schemes => ["test"];

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteAddress folder, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteEntry?> GetEntryAsync(RemoteAddress address, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(RemoteAddress file, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadAsync(Stream source, RemoteAddress target, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateFolderAsync(RemoteAddress folder, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(RemoteAddress address, bool isDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RenameAsync(RemoteAddress address, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeHostServices : IPluginHostServices
    {
        public IPluginUi Ui => throw new NotSupportedException();

        public IVolumeProvider Volumes => throw new NotSupportedException();

        public IPlatformInfo Platform => throw new NotSupportedException();

        public string Language => "en";

        public string GetString(string key) => key;

        public VolumeDisplay DescribeVolume(VolumeInfo volume) => throw new NotSupportedException();

        public IPluginLog CreateLog(string pluginId) => new Log();

        public ISecretStore Secrets { get; } = new Navigator.Platform.Secrets.UnsupportedSecretStore();

        public bool OpenInBrowser(Uri address) => false;

        private sealed class Log : IPluginLog
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
    }
}
