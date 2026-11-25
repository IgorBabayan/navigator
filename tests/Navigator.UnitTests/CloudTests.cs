using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Plugin.GoogleDrive;
using Navigator.Plugin.OneDrive;
using Navigator.Plugins;
using Navigator.Plugins.Cloud;
using Navigator.Tests;

namespace Navigator.UnitTests;

[TestClass]
public sealed class CloudTests
{
    // ── Sidebar group ─────────────────────────────────────────

    [TestMethod]
    public async Task CloudGroup_MergesMembersRightBelowNetwork()
    {
        using var temp = new TempDirectory();
        var drive = new MemberPlugin("gdrive", 10, "a@example.com");
        var oneDrive = new MemberPlugin("onedrive", 20, "b@example.com");
        using var host = new PluginHost(temp.Path, new HostServices(), [Built("network", new NetworkLikePlugin()), Built("onedrive", oneDrive), Built("gdrive", drive), Built("ftp", new ServerLikePlugin())]);

        var sections = host.SidebarSections;
        CollectionAssert.AreEqual(new[] { "network", "cloud", "ftp" }, sections.Select(s => s.Id).ToArray());
        var cloud = sections[1];
        Assert.AreEqual("Sidebar.Cloud", cloud.Title);
        Assert.AreEqual(150, cloud.Order);
        Assert.IsTrue(cloud.ShowWhenEmpty);

        var entries = await cloud.GetEntriesAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "a@example.com", "b@example.com" }, entries.Select(e => e.Name).ToArray(), "members in their order");

        // Entry commands reach the member that produced the entry.
        await cloud.ExecuteAsync("sign-out", entries[1], CancellationToken.None);
        Assert.AreEqual(0, drive.Executed.Count);
        CollectionAssert.AreEqual(new[] { "sign-out:b@example.com" }, oneDrive.Executed);

        // The shared "add account" command is offered once; other member commands keep their owner.
        var commands = cloud.Commands;
        Assert.AreEqual(CloudAccountsSection.AddAccountCommand, commands[0].Id);
        Assert.AreEqual(1, commands.Count(c => c.Id == CloudAccountsSection.AddAccountCommand));
        var refresh = commands.Single(c => c.Title == "Refresh gdrive");
        await cloud.ExecuteAsync(refresh.Id, null, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "refresh" }, drive.Executed);
    }

    [TestMethod]
    public void CloudGroup_ListensToMembersOnlyWhileObserved()
    {
        using var temp = new TempDirectory();
        var drive = new MemberPlugin("gdrive", 10, "a@example.com");
        using var host = new PluginHost(temp.Path, new HostServices(), [Built("gdrive", drive)]);
        var cloud = host.SidebarSections.Single();

        var raised = 0;
        EventHandler handler = (sender, _) =>
        {
            Assert.AreSame(cloud, sender);
            raised++;
        };
        cloud.Changed += handler;
        Assert.AreEqual(1, drive.Section.ListenerCount);
        drive.Section.RaiseChanged();
        Assert.AreEqual(1, raised);

        cloud.Changed -= handler;
        Assert.AreEqual(0, drive.Section.ListenerCount, "no subscription survives the sidebar letting go");
    }

    [TestMethod]
    public void CloudGroup_UnknownGroupsStayOnTheirOwn()
    {
        using var temp = new TempDirectory();
        var plugin = new MemberPlugin("other", 400, "x@example.com", groupId: "no-such-group");
        using var host = new PluginHost(temp.Path, new HostServices(), [Built("other", plugin)]);
        Assert.AreEqual("other", host.SidebarSections.Single().Id);
    }

    // ── Secrets ───────────────────────────────────────────────

    [TestMethod]
    public async Task PluginSecrets_AreScopedAndKeptInMemoryWhenTheStoreRefuses()
    {
        using var temp = new TempDirectory();
        var store = new RecordingSecretStore();
        var services = new HostServices { Secrets = store };
        IPluginContext? captured = null;
        using var host = new PluginHost(temp.Path, services, [Built("gdrive", new CallbackPlugin(c => captured = c))]);
        Assert.IsNotNull(captured);

        Assert.IsTrue(await captured.Secrets.SetAsync("refresh-1", "token", CancellationToken.None));
        Assert.AreEqual("Navigator.gdrive/refresh-1", store.Writes.Single());
        Assert.AreEqual("token", await captured.Secrets.GetAsync("refresh-1", CancellationToken.None));
        Assert.AreEqual(0, store.Reads, "served from memory");

        store.Refuse = true;
        Assert.IsFalse(await captured.Secrets.SetAsync("refresh-2", "other", CancellationToken.None));
        Assert.AreEqual("other", await captured.Secrets.GetAsync("refresh-2", CancellationToken.None), "still usable for this session");

        await captured.Secrets.RemoveAsync("refresh-1", CancellationToken.None);
        Assert.IsNull(await captured.Secrets.GetAsync("refresh-1", CancellationToken.None));
    }

    [TestMethod]
    public void PluginContext_OpensOnlyHttpsAddresses()
    {
        using var temp = new TempDirectory();
        var services = new HostServices();
        IPluginContext? captured = null;
        using var host = new PluginHost(temp.Path, services, [Built("gdrive", new CallbackPlugin(c => captured = c))]);
        Assert.IsNotNull(captured);
        Assert.IsTrue(captured.OpenInBrowser(new Uri("https://accounts.example.com/auth")));
        Assert.IsFalse(captured.OpenInBrowser(new Uri("http://example.com/")));
        Assert.IsFalse(captured.OpenInBrowser(new Uri("file:///etc/passwd")));
        Assert.AreEqual(1, services.Opened.Count);
    }

    // ── OAuth ─────────────────────────────────────────────────

    [TestMethod]
    public void TokenResponse_IsParsedAndErrorsAreClassified()
    {
        var tokens = OAuthClient.ParseTokens(Encoding.UTF8.GetBytes("""{"access_token":"at","expires_in":3599,"refresh_token":"rt","token_type":"Bearer"}"""), 200, null);
        Assert.AreEqual("at", tokens.AccessToken);
        Assert.AreEqual("rt", tokens.RefreshToken);
        Assert.IsFalse(tokens.IsExpiring);

        var refreshed = OAuthClient.ParseTokens(Encoding.UTF8.GetBytes("""{"access_token":"at2","expires_in":"30"}"""), 200, "old");
        Assert.AreEqual("old", refreshed.RefreshToken, "the refresh token is kept unless the server rotates it");
        Assert.IsTrue(refreshed.IsExpiring, "a minimum lifetime of a minute counts as expiring");

        var invalid = Assert.ThrowsExactly<OAuthException>(() => OAuthClient.ParseTokens(Encoding.UTF8.GetBytes("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""), 400, "old"));
        Assert.IsTrue(invalid.IsInvalidGrant);
        Assert.ThrowsExactly<OAuthException>(() => OAuthClient.ParseTokens(Encoding.UTF8.GetBytes("<html>"), 502, null));
    }

    [TestMethod]
    public void QueryStrings_RoundTrip()
    {
        var encoded = OAuthClient.FormEncode([new("redirect_uri", "http://127.0.0.1:5000"), new("scope", "a b"), new("state", "x/y+z")]);
        var parsed = OAuthClient.ParseQuery(encoded);
        Assert.AreEqual("http://127.0.0.1:5000", parsed["redirect_uri"]);
        Assert.AreEqual("a b", parsed["scope"]);
        Assert.AreEqual("x/y+z", parsed["state"]);
    }

    [TestMethod]
    public async Task SignIn_UsesPkceAndTheLoopbackRedirect()
    {
        using var tokenServer = new TinyHttpServer(request =>
        {
            var form = OAuthClient.ParseQuery(request.Body);
            Assert.AreEqual("authorization_code", form["grant_type"]);
            Assert.AreEqual("the-code", form["code"]);
            Assert.AreEqual("client", form["client_id"]);
            Assert.IsTrue(form["code_verifier"].Length >= 43, "PKCE verifier");
            return """{"access_token":"at","refresh_token":"rt","expires_in":3600}""";
        });
        var context = new BrowserContext(async url =>
        {
            // Play the browser: the provider redirects back with the state it was given and a code.
            var query = OAuthClient.ParseQuery(url.Query);
            Assert.AreEqual("S256", query["code_challenge_method"]);
            Assert.AreEqual("offline", query["access_type"]);
            var redirect = new Uri(query["redirect_uri"]);
            Assert.AreEqual("127.0.0.1", redirect.Host);
            using var browser = new TcpClient();
            await browser.ConnectAsync(IPAddress.Loopback, redirect.Port);
            var stream = browser.GetStream();
            // An unrelated request first (favicon): answered with 404, the sign-in keeps waiting.
            await TinyHttpServer.GetAsync(redirect.Port, "/favicon.ico");
            var page = await TinyHttpServer.GetAsync(redirect.Port, "/?state=" + Uri.EscapeDataString(query["state"]) + "&code=the-code");
            StringAssert.Contains(page, "Signed in!");
        });
        var provider = new OAuthProvider(new Uri("https://accounts.example.com/auth"), tokenServer.Address, "client", null, ["scope.a"])
        {
            AuthorizeParameters = new Dictionary<string, string> { ["access_type"] = "offline" },
        };

        var tokens = await OAuthClient.SignInAsync(context, provider, null, new OAuthPages("Signed in!", "Cancelled"), CancellationToken.None);
        await context.Browser!;
        Assert.AreEqual("at", tokens.AccessToken);
        Assert.AreEqual("rt", tokens.RefreshToken);
    }

    [TestMethod]
    public async Task SignIn_ReportsADeniedConsent()
    {
        var context = new BrowserContext(async url =>
        {
            var query = OAuthClient.ParseQuery(url.Query);
            var redirect = new Uri(query["redirect_uri"]);
            await TinyHttpServer.GetAsync(redirect.Port, "/?state=" + Uri.EscapeDataString(query["state"]) + "&error=access_denied");
        });
        var provider = new OAuthProvider(new Uri("https://accounts.example.com/auth"), new Uri("http://127.0.0.1:9/token"), "client", null, ["s"]);
        var error = await Assert.ThrowsExactlyAsync<OAuthException>(() => OAuthClient.SignInAsync(context, provider, null, new OAuthPages("ok", "no"), CancellationToken.None));
        Assert.IsTrue(error.IsAccessDenied);
    }

    // ── Accounts ──────────────────────────────────────────────

    [TestMethod]
    public void AccountStore_PersistsAccountsWithoutSecrets()
    {
        using var temp = new TempDirectory();
        var store = new CloudAccountStore(temp.Path);
        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.AddOrUpdate(new CloudAccount { Id = "0a1b2c3d4e", Subject = "s1", Email = "me@example.com", DisplayName = "Me" });

        var reloaded = new CloudAccountStore(temp.Path);
        var account = reloaded.Find("0A1B2C3D4E");
        Assert.IsNotNull(account);
        Assert.AreEqual("me@example.com", account.Name);
        Assert.AreSame(account, reloaded.FindBySubject("s1"));
        Assert.IsFalse(File.ReadAllText(temp.Combine("accounts.json")).Contains("token", StringComparison.OrdinalIgnoreCase));

        store.Remove("0a1b2c3d4e");
        Assert.AreEqual(2, changes);
        Assert.AreEqual(0, new CloudAccountStore(temp.Path).GetAll().Count);
    }

    [TestMethod]
    public void AccountIds_AreValidAddressHosts()
    {
        var id = CloudAccount.NewId();
        Assert.AreEqual(10, id.Length);
        Assert.IsTrue(RemoteUri.TryParse(CloudAccountsSection.RootAddress("gdrive", id), out var address));
        Assert.AreEqual(id, address.Host);
        Assert.IsTrue(address.IsRoot);
    }

    [TestMethod]
    public void HttpErrors_MapToRemoteErrorKinds()
    {
        Assert.AreEqual(RemoteErrorKind.NotFound, CloudApi.ToException(HttpStatusCode.NotFound, string.Empty).Kind);
        Assert.AreEqual(RemoteErrorKind.AlreadyExists, CloudApi.ToException(HttpStatusCode.Conflict, """{"error":{"code":"nameAlreadyExists","message":"Name already exists"}}""").Kind);
        var denied = CloudApi.ToException(HttpStatusCode.Forbidden, """{"error":{"message":"The user does not have sufficient permissions."}}""");
        Assert.AreEqual(RemoteErrorKind.AccessDenied, denied.Kind);
        Assert.AreEqual("The user does not have sufficient permissions.", denied.Message);
    }

    // ── Google Drive ──────────────────────────────────────────

    [TestMethod]
    public void DriveNames_GiveEveryItemItsOwnPath()
    {
        var segments = DriveNames.AssignSegments([
            ("Budget", "application/vnd.google-apps.spreadsheet"),
            ("Budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ("a/b", "text/plain"),
            ("Notes", DriveNames.FolderMime),
            ("Notes", DriveNames.FolderMime),
        ]);
        CollectionAssert.AreEqual(new[] { "Budget.xlsx", "Budget (2).xlsx", "a\u2215b", "Notes", "Notes (2)" }, segments);
        Assert.AreEqual("a/b", DriveNames.ToDriveName("a\u2215b"));
        Assert.AreEqual("Plan", DriveNames.ToDriveName("Plan.xlsx", "application/vnd.google-apps.spreadsheet"), "a Google Sheet keeps no extension");
        Assert.AreEqual("Plan.xlsx", DriveNames.ToDriveName("Plan.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.AreEqual(@"'it\'s a \\ test'", DriveNames.QueryLiteral(@"it's a \ test"));
        Assert.IsTrue(DriveNames.IsContentless("application/vnd.google-apps.form"));
        Assert.IsFalse(DriveNames.IsContentless("application/vnd.google-apps.document"));
        Assert.IsFalse(DriveNames.IsContentless(DriveNames.FolderMime));
    }

    [TestMethod]
    public void DriveItems_AreParsedIncludingShortcuts()
    {
        using var document = JsonDocument.Parse("""
            [{"id":"1","name":"photo.jpg","mimeType":"image/jpeg","size":"2048","modifiedTime":"2026-03-01T10:20:30.000Z"},
             {"id":"2","name":"Shared","mimeType":"application/vnd.google-apps.shortcut","shortcutDetails":{"targetId":"9","targetMimeType":"application/vnd.google-apps.folder"}}]
            """);
        var file = GoogleDriveFileSystem.Parse(document.RootElement[0]);
        Assert.AreEqual(2048, file.Size);
        Assert.AreEqual(new DateTime(2026, 3, 1, 10, 20, 30, DateTimeKind.Utc), file.ModifiedUtc);
        Assert.AreEqual(DateTimeKind.Utc, file.ModifiedUtc.Kind);
        Assert.IsFalse(file.IsFolder);

        var shortcut = GoogleDriveFileSystem.Parse(document.RootElement[1]);
        Assert.IsTrue(shortcut.IsShortcut);
        Assert.IsTrue(shortcut.IsFolder);
        Assert.AreEqual("9", shortcut.ContentId, "listing follows the target");
        Assert.AreEqual("2", shortcut.Id, "deleting removes only the shortcut");
        Assert.IsTrue(shortcut.ToEntry().IsSymbolicLink);
    }

    // ── OneDrive ──────────────────────────────────────────────

    [TestMethod]
    public void GraphReferences_UsePathAndIdAddressing()
    {
        var file = GraphRef.Root.Child("My Docs").Child("a#b.txt");
        Assert.AreEqual("me/drive/root:/My%20Docs/a%23b.txt", file.Value);
        Assert.AreEqual("me/drive/root:/My%20Docs/a%23b.txt:/content", file.Sub("content"));
        Assert.AreEqual("me/drive/root/children", GraphRef.Root.Sub("children"));

        var shared = GraphRef.Remote("b!drive", "ITEM1");
        Assert.AreEqual("drives/b%21drive/items/ITEM1/children", shared.Sub("children"));
        Assert.AreEqual("drives/b%21drive/items/ITEM1:/x:/children", shared.Child("x").Sub("children"));
    }

    [TestMethod]
    public void OneDriveItems_AreParsedIncludingSharedFolders()
    {
        using var document = JsonDocument.Parse("""
            [{"id":"A","name":"report.pdf","size":1234,"lastModifiedDateTime":"2026-02-03T04:05:06Z","file":{}},
             {"id":"B","name":"Team","remoteItem":{"id":"R1","folder":{"childCount":3},"parentReference":{"driveId":"d2"}}},
             {"id":"C","name":"Notebook","package":{"type":"oneNote"}}]
            """);
        var self = GraphRef.Root.Child("x");
        var file = OneDriveFileSystem.Parse(document.RootElement[0], self);
        Assert.AreEqual(1234, file.Size);
        Assert.IsFalse(file.IsFolder);
        Assert.AreEqual(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc), file.ModifiedUtc);

        var shared = OneDriveFileSystem.Parse(document.RootElement[1], self);
        Assert.IsTrue(shared.IsFolder);
        Assert.IsTrue(shared.IsRemote);
        Assert.AreEqual("drives/d2/items/R1", shared.Ref.Value);
        Assert.AreEqual(self, shared.Self, "deleting removes the entry in My files, not the shared folder");

        Assert.IsTrue(OneDriveFileSystem.Parse(document.RootElement[2], self).IsFolder);
    }

    // ── Test doubles ──────────────────────────────────────────

    private static BuiltInPlugin Built(string id, INavigatorPlugin plugin)
        => new(new PluginManifest { Id = id, Name = id, Version = "1.0.0", ApiVersion = PluginApi.Version }, () => plugin);

    private sealed class HostServices : IPluginHostServices
    {
        public List<Uri> Opened { get; } = [];

        public IPluginUi Ui => throw new NotSupportedException();

        public IVolumeProvider Volumes => throw new NotSupportedException();

        public IPlatformInfo Platform => throw new NotSupportedException();

        public string Language => "en";

        public ISecretStore Secrets { get; init; } = new RecordingSecretStore();

        public string GetString(string key) => key;

        public VolumeDisplay DescribeVolume(VolumeInfo volume) => throw new NotSupportedException();

        public IPluginLog CreateLog(string pluginId) => new NullLog();

        public bool OpenInBrowser(Uri address)
        {
            Opened.Add(address);
            return true;
        }
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

    private sealed class RecordingSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public bool Refuse { get; set; }

        public List<string> Writes { get; } = [];

        public int Reads { get; private set; }

        public bool IsPersistent => true;

        public Task<string?> ReadAsync(string service, string account, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(_values.GetValueOrDefault(service + "/" + account));
        }

        public Task<bool> WriteAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
        {
            if (Refuse)
                return Task.FromResult(false);
            Writes.Add(service + "/" + account);
            _values[service + "/" + account] = secret;
            return Task.FromResult(true);
        }

        public Task DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
        {
            _values.Remove(service + "/" + account);
            return Task.CompletedTask;
        }
    }

    private sealed class CallbackPlugin(Action<IPluginContext> initialize) : INavigatorPlugin
    {
        public void Initialize(IPluginContext context) => initialize(context);
    }

    private sealed class NetworkLikePlugin : INavigatorPlugin
    {
        public void Initialize(IPluginContext context) => context.AddSidebarSection(new FakeSection("network", 100));
    }

    private sealed class ServerLikePlugin : INavigatorPlugin
    {
        public void Initialize(IPluginContext context) => context.AddSidebarSection(new FakeSection("ftp", 300));
    }

    private sealed class MemberPlugin(string id, int order, string account, string groupId = PluginSidebarGroups.Cloud) : INavigatorPlugin
    {
        public GroupMemberSection Section { get; } = new(id, order, groupId, account);

        public List<string> Executed => Section.Executed;

        public void Initialize(IPluginContext context) => context.AddSidebarSection(Section);
    }

    private class FakeSection(string id, int order) : IPluginSidebarSection
    {
        private EventHandler? _changed;

        public List<string> Executed { get; } = [];

        public int ListenerCount => _changed?.GetInvocationList().Length ?? 0;

        public string Id => id;

        public string Title => id;

        public string IconKey => "Cloud";

        public string? SystemIconKey => null;

        public int Order => order;

        public bool ShowWhenEmpty => true;

        public virtual IReadOnlyList<PluginCommand> Commands => [];

        public event EventHandler? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public void RaiseChanged() => _changed?.Invoke(this, EventArgs.Empty);

        public virtual ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<PluginSidebarEntry>>([]);

        public Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken)
        {
            Executed.Add(entry is null ? commandId : commandId + ":" + entry.Name);
            return Task.CompletedTask;
        }
    }

    private sealed class GroupMemberSection(string id, int order, string groupId, string account) : FakeSection(id, order), IPluginSidebarGroupMember
    {
        public string GroupId => groupId;

        public override IReadOnlyList<PluginCommand> Commands => [new(CloudAccountsSection.AddAccountCommand, "Add " + Id, "Add"), new("refresh", "Refresh " + Id, "Refresh")];

        public override ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<PluginSidebarEntry>>([new(Id + ":1", account, "Cloud", Location.Remote(Id + "://0123456789/"))]);
    }

    /// <summary>A plugin context that only "opens the browser" (runs a fake browser) and logs nowhere.</summary>
    private sealed class BrowserContext(Func<Uri, Task> browser) : IPluginContext
    {
        public Task? Browser { get; private set; }

        public string PluginId => "test";

        public string DataDirectory => Path.GetTempPath();

        public string Language => "en";

        public IPluginUi Ui => throw new NotSupportedException();

        public IPluginLog Log { get; } = new NullLog();

        public IVolumeProvider Volumes => throw new NotSupportedException();

        public IPlatformInfo Platform => throw new NotSupportedException();

        public IPluginSecretStore Secrets => throw new NotSupportedException();

        public bool OpenInBrowser(Uri address)
        {
            Browser = Task.Run(() => browser(address));
            return true;
        }

        public string GetHostString(string key) => key;

        public VolumeDisplay DescribeVolume(VolumeInfo volume) => throw new NotSupportedException();

        public void AddSidebarSection(IPluginSidebarSection section) => throw new NotSupportedException();

        public void AddFileSystem(IRemoteFileSystem fileSystem) => throw new NotSupportedException();
    }

    /// <summary>One-request-at-a-time HTTP/1.1 server on 127.0.0.1 for the token endpoint.</summary>
    private sealed class TinyHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public TinyHttpServer(Func<(string Path, string Body), string> respond)
        {
            _listener.Start();
            Address = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/token"));
            _ = ServeAsync(respond);
        }

        public Uri Address { get; }

        public static async Task<string> GetAsync(int port, string path)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"));
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        private async Task ServeAsync(Func<(string Path, string Body), string> respond)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) == 1)
                        head.Append((char)buffer[0]);
                    var lines = head.ToString().Split("\r\n");
                    var length = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        .Select(p => int.Parse(p[1].Trim(), CultureInfo.InvariantCulture)).FirstOrDefault();
                    var body = new byte[length];
                    var read = 0;
                    while (read < length)
                        read += await stream.ReadAsync(body.AsMemory(read));
                    var json = Encoding.UTF8.GetBytes(respond((lines[0].Split(' ')[1], Encoding.UTF8.GetString(body))));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(json);
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            _stop.Dispose();
        }
    }
}
