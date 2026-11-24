using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Navigation;
using Navigator.Plugins;

namespace Navigator.Plugin.Network;

/// <summary>
/// The sidebar's "Network" entry on Windows, like File Explorer's Network folder:
/// <c>network://computers/</c> lists the computers Windows has discovered on the local network (Network Discovery),
/// <c>network://computers/SERVER</c> lists that computer's shared folders (administrative shares such as C$ are hidden),
/// and a share opens as the ordinary folder <c>\\SERVER\share</c>, so every file action works on it as usual.
/// A computer that is not discovered can be typed in the address bar (<c>network://computers/192.168.1.20</c>).
/// Read-only: computers and shares cannot be created, renamed or deleted here.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsNetworkFileSystem(IPluginContext context) : IRemoteFileSystem, IRemoteLocationMapper
{
    public const string Scheme = NetworkAddresses.Scheme;
    public const string Host = NetworkAddresses.Host;
    public static readonly string RootAddress = NetworkAddresses.Root;

    public IReadOnlyList<string> Schemes { get; } = [Scheme];

    // ── Mapping (no I/O) ─────────────────────────────────────

    public string? GetLocalPath(RemoteAddress address) => NetworkAddresses.ToSharePath(address);

    public string? GetParentAddress(string localPath) => NetworkAddresses.ServerOfShareRoot(localPath);

    public string? GetDisplayName(RemoteAddress address)
        => address is not null && address.IsRoot && IsThisHost(address) ? context.GetHostString("Sidebar.Network") : null;

    // ── Listing ──────────────────────────────────────────────

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteAddress folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var segments = Segments(folder);
        if (!IsThisHost(folder) || segments.Length > 1)
            throw new RemoteFileSystemException(RemoteErrorKind.NotFound, $"{folder} is not a network location.");

        // Both calls block on the network (seconds when a computer is offline): never on the UI thread, and the wait
        // is abandoned when the user navigates away.
        if (segments.Length == 0)
            return await Task.Run(ListComputers, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

        var server = segments[0];
        if (!IsValidServer(server))
            throw new RemoteFileSystemException(RemoteErrorKind.NotFound, $"'{server}' is not a computer name.");
        var result = await Task.Run(() => ListShares(server), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result.Error is ErrorAccessDenied or ErrorLogonFailure or ErrorInvalidPassword or ErrorNotAuthenticated or ErrorBadUserName)
        {
            // The computer wants credentials: ask, connect to its IPC$ (Windows keeps the session), and try again.
            if (await SignInAsync(server, cancellationToken).ConfigureAwait(false))
                result = await Task.Run(() => ListShares(server), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result.Error switch
        {
            0 => result.Shares,
            ErrorAccessDenied or ErrorLogonFailure or ErrorInvalidPassword or ErrorNotAuthenticated or ErrorBadUserName
                => throw new RemoteFileSystemException(RemoteErrorKind.AccessDenied, new Win32Exception(result.Error).Message),
            ErrorBadNetPath or ErrorBadNetName or ErrorNetworkUnreachable or ErrorHostUnreachable
                => throw new RemoteFileSystemException(RemoteErrorKind.NotFound, new Win32Exception(result.Error).Message),
            _ => throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, new Win32Exception(result.Error).Message),
        };
    }

    public Task<RemoteEntry?> GetEntryAsync(RemoteAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var segments = Segments(address);
        return Task.FromResult<RemoteEntry?>(IsThisHost(address) && segments.Length <= 1
            ? new RemoteEntry(segments.Length == 0 ? Host : segments[0], true, 0, DateTime.MinValue)
            : null);
    }

    public Task<Stream> OpenReadAsync(RemoteAddress file, CancellationToken cancellationToken) => throw ReadOnly();

    public Task UploadAsync(Stream source, RemoteAddress target, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateFolderAsync(RemoteAddress folder, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(RemoteAddress address, bool isDirectory, CancellationToken cancellationToken) => throw ReadOnly();

    public Task RenameAsync(RemoteAddress address, string newName, CancellationToken cancellationToken) => throw ReadOnly();

    private RemoteFileSystemException ReadOnly() => new(RemoteErrorKind.AccessDenied, context.GetHostString("Network.ReadOnly"));

    // ── Windows networking ───────────────────────────────────

    private const int ResourceContext = 0x5;
    private const int ResourceTypeAny = 0x0;
    private const int ResourceDisplayTypeServer = 0x2;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorMoreData = 234;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetPath = 53;
    private const int ErrorBadNetName = 67;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorNetworkUnreachable = 1231;
    private const int ErrorHostUnreachable = 1232;
    private const int ErrorNotAuthenticated = 1244;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorBadUserName = 2202;
    private const uint ShareTypeMask = 0xFF;
    private const uint ShareTypeDisk = 0;
    private const uint ShareSpecial = 0x80000000;
    private const int MaxEntries = 10_000;

    /// <summary>Computers in the Network folder (what Network Discovery found). Empty when discovery is off.</summary>
    private static IReadOnlyList<RemoteEntry> ListComputers()
    {
        var computers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (WNetOpenEnum(ResourceContext, ResourceTypeAny, 0, 0, out var handle) != 0)
            return [];
        var buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            while (computers.Count < MaxEntries)
            {
                var count = -1;
                var size = 64 * 1024;
                var status = WNetEnumResource(handle, ref count, buffer, ref size);
                if (status is ErrorNoMoreItems || (status != 0 && status != ErrorMoreData))
                    break;
                var itemSize = Marshal.SizeOf<NetResource>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<NetResource>(buffer + (i * itemSize));
                    if (item.DisplayType != ResourceDisplayTypeServer || Marshal.PtrToStringUni(item.RemoteName) is not { Length: > 2 } name)
                        continue;
                    var server = name.TrimStart('\\');
                    if (IsValidServer(server))
                        computers.Add(server);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _ = WNetCloseEnum(handle);
        }

        return [.. computers.Select(c => new RemoteEntry(c, true, 0, DateTime.MinValue))];
    }

    /// <summary>The disk shares of a computer, without administrative/special ones (C$, ADMIN$, IPC$).</summary>
    private static (IReadOnlyList<RemoteEntry> Shares, int Error) ListShares(string server)
    {
        var resume = 0;
        var status = NetShareEnum(@"\\" + server, 1, out var buffer, -1, out var read, out _, ref resume);
        try
        {
            if (status != 0 && status != ErrorMoreData)
                return ([], status);
            var shares = new List<RemoteEntry>(read);
            var itemSize = Marshal.SizeOf<ShareInfo1>();
            for (var i = 0; i < read && i < MaxEntries; i++)
            {
                var share = Marshal.PtrToStructure<ShareInfo1>(buffer + (i * itemSize));
                if ((share.Type & ShareTypeMask) != ShareTypeDisk || (share.Type & ShareSpecial) != 0)
                    continue;
                if (Marshal.PtrToStringUni(share.NetName) is { Length: > 0 } name && name.IndexOfAny(['\\', '/']) < 0)
                    shares.Add(new RemoteEntry(name, true, 0, DateTime.MinValue));
            }

            shares.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            return (shares, 0);
        }
        finally
        {
            if (buffer != 0)
                _ = NetApiBufferFree(buffer);
        }
    }

    /// <summary>Asks for credentials and opens a session to the computer (IPC$). Returns false when cancelled or refused.</summary>
    private async Task<bool> SignInAsync(string server, CancellationToken cancellationToken)
    {
        var values = await context.Ui.ShowFormAsync(new PluginForm(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, context.GetHostString("Network.SignInTitle"), server),
            context.GetHostString("Network.SignIn"),
            [
                new PluginFormField("user", context.GetHostString("Plugin.Remote.User"), IsRequired: true) { Placeholder = @"DOMAIN\user" },
                new PluginFormField("password", context.GetHostString("Network.Password"), PluginFieldKind.Password),
            ])).ConfigureAwait(false);
        if (values is null)
            return false;
        var user = values.GetValueOrDefault("user", string.Empty).Trim();
        var password = values.GetValueOrDefault("password", string.Empty);
        var status = await Task.Run(() => Connect(@"\\" + server + @"\IPC$", user, password), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (status == 0)
            return true;
        context.Log.Warning($"Signing in to {server} failed: {new Win32Exception(status).Message}");
        await context.Ui.ShowMessageAsync(context.GetHostString("Remote.AuthenticationFailed"), new Win32Exception(status).Message).ConfigureAwait(false);
        return false;
    }

    private static int Connect(string remoteName, string user, string password)
    {
        var name = Marshal.StringToHGlobalUni(remoteName);
        try
        {
            var resource = new NetResource { Type = ResourceTypeAny, RemoteName = name };
            return WNetAddConnection2(ref resource, password, user, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    // ── Helpers ──────────────────────────────────────────────

    private static bool IsThisHost(RemoteAddress address) => NetworkAddresses.IsThisHost(address);

    private static string[] Segments(RemoteAddress address) => NetworkAddresses.Segments(address);

    private static bool IsValidServer(string name) => NetworkAddresses.IsValidServer(name);

    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public nint LocalName;
        public nint RemoteName;
        public nint Comment;
        public nint Provider;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShareInfo1
    {
        public nint NetName;
        public uint Type;
        public nint Remark;
    }

    [LibraryImport("mpr.dll", EntryPoint = "WNetOpenEnumW")]
    private static partial int WNetOpenEnum(int scope, int type, int usage, nint resource, out nint handle);

    [LibraryImport("mpr.dll", EntryPoint = "WNetEnumResourceW")]
    private static partial int WNetEnumResource(nint handle, ref int count, nint buffer, ref int bufferSize);

    [LibraryImport("mpr.dll")]
    private static partial int WNetCloseEnum(nint handle);

    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WNetAddConnection2(ref NetResource resource, string? password, string? userName, int flags);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareEnum(string serverName, int level, out nint buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buffer);
}
