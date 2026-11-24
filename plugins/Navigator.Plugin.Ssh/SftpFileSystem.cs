using System.Globalization;
using System.Net.Sockets;
using Navigator.Core.Navigation;
using Navigator.Plugins;
using Navigator.Plugins.Remote;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Navigator.Plugin.Ssh;

/// <summary>
/// sftp:// addresses over SSH.NET. One SSH session per server is shared by every pane and transfer (SFTP multiplexes
/// requests) and closed after a few idle minutes. Passwords and passphrases are asked once per session and kept in
/// memory only; server keys are trusted on first use and every change is reported before connecting.
/// </summary>
internal sealed class SftpFileSystem : IRemoteFileSystem, IDisposable
{
    public const string Scheme = "sftp";
    public const int DefaultPort = 22;

    /// <summary>Symbolic links in one listing whose target kind (folder or file) is looked up; the rest show as files.</summary>
    private const int MaxResolvedLinks = 64;
    private const int MaxDeleteDepth = 256;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly IPluginContext _context;
    private readonly RemoteConnectionStore _store;
    private readonly PluginStrings _strings;
    private readonly KnownHosts _knownHosts;
    private readonly CredentialCache _credentials = new();
    private readonly RemoteSessionPool<SftpClient> _pool;

    public SftpFileSystem(IPluginContext context, RemoteConnectionStore store, PluginStrings strings)
    {
        _context = context;
        _store = store;
        _strings = strings;
        _knownHosts = new KnownHosts(context.DataDirectory);
        _pool = new RemoteSessionPool<SftpClient>(ConnectAsync, static client => client.IsConnected, exclusive: false, maxSessionsPerServer: 1, IdleTimeout);
    }

    public IReadOnlyList<string> Schemes { get; } = [Scheme];

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteAddress folder, CancellationToken cancellationToken)
        => RunAsync<IReadOnlyList<RemoteEntry>>(folder, async (client, token) =>
        {
            var entries = new List<RemoteEntry>();
            var links = new List<int>();
            await foreach (var file in client.ListDirectoryAsync(folder.Path, token).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                    continue;
                if (file.IsSymbolicLink && links.Count < MaxResolvedLinks)
                    links.Add(entries.Count);
                entries.Add(new RemoteEntry(file.Name, file.IsDirectory, file.IsDirectory ? 0 : file.Length, file.LastWriteTimeUtc, file.IsSymbolicLink));
            }

            // Listings report the link itself (lstat). A link to a folder must still open as a folder.
            foreach (var index in links)
            {
                if (await IsFolderAsync(client, Child(folder.Path, entries[index].Name), token).ConfigureAwait(false))
                    entries[index] = entries[index] with { IsDirectory = true, Length = 0 };
            }

            return entries;
        }, cancellationToken);

    public Task<RemoteEntry?> GetEntryAsync(RemoteAddress address, CancellationToken cancellationToken)
        => RunAsync(address, async (client, token) =>
        {
            try
            {
                var file = await client.GetAsync(address.Path, token).ConfigureAwait(false);
                return new RemoteEntry(file.Name, file.IsDirectory, file.Length, file.LastWriteTimeUtc, file.IsSymbolicLink);
            }
            catch (SftpPathNotFoundException)
            {
                return (RemoteEntry?)null;
            }
        }, cancellationToken);

    public async Task<Stream> OpenReadAsync(RemoteAddress file, CancellationToken cancellationToken)
    {
        // The session must stay leased for as long as the stream is read, so it is released by the stream.
        var lease = await AcquireAsync(file, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await lease.Session.OpenAsync(file.Path, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            return new LeasedStream(stream, lease);
        }
        catch (Exception ex) when (Translate(ex, lease) is { } translated)
        {
            lease.Dispose();
            throw translated;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes to a hidden temporary name first and renames it into place, so an interrupted upload never leaves a
    /// truncated file under the real name. Plain SFTP rename refuses an existing target: nothing is overwritten.
    /// </summary>
    public Task UploadAsync(Stream source, RemoteAddress target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var start = source.CanSeek ? source.Position : -1L;
        var attempted = false;
        return RunAsync(target, async (client, token) =>
        {
            // A retry on a fresh session must send the whole file again, never the unread remainder.
            if (attempted)
            {
                if (start < 0)
                    throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The connection was lost during the upload.");
                source.Position = start;
            }

            attempted = true;
            var folder = Parent(target.Path);
            var name = Name(target.Path);
            var temporary = Child(folder, $".{name}.{Guid.NewGuid().ToString("N")[..8]}.navigator-partial");
            if (await client.ExistsAsync(target.Path, token).ConfigureAwait(false))
                throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, $"{target.Path} already exists.");
            try
            {
                await client.UploadFileAsync(source, temporary, canOverride: false, uploadProgress: null, token).ConfigureAwait(false);
                await client.RenameFileAsync(temporary, target.Path, token).ConfigureAwait(false);
            }
            catch
            {
                await TryDeleteFileAsync(client, temporary).ConfigureAwait(false);
                throw;
            }

            return true;
        }, cancellationToken);
    }

    public Task CreateFolderAsync(RemoteAddress folder, CancellationToken cancellationToken)
        => RunAsync(folder, async (client, token) =>
        {
            if (await client.ExistsAsync(folder.Path, token).ConfigureAwait(false))
                throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, $"{folder.Path} already exists.");
            await client.CreateDirectoryAsync(folder.Path, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    // SSH.NET resolves every path it is given with SFTP REALPATH, which follows symbolic links: DeleteFileAsync or
    // RenameFileAsync on a link would act on the link's TARGET. Delete and rename therefore act on the ISftpFile
    // taken from the parent folder's listing, whose own methods send the item's path unresolved.

    public Task DeleteAsync(RemoteAddress address, bool isDirectory, CancellationToken cancellationToken)
        => RunAsync(address, async (client, token) =>
        {
            if (address.IsRoot)
                throw new RemoteFileSystemException(RemoteErrorKind.AccessDenied, "The server's root folder cannot be deleted.");
            var item = await FindListedAsync(client, address.Path, token).ConfigureAwait(false)
                       ?? throw new RemoteFileSystemException(RemoteErrorKind.NotFound, $"{address.Path} does not exist.");
            await DeleteItemAsync(client, item, 0, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task RenameAsync(RemoteAddress address, string newName, CancellationToken cancellationToken)
        => RunAsync(address, async (client, token) =>
        {
            if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/', StringComparison.Ordinal) || newName is "." or "..")
                throw new RemoteFileSystemException(RemoteErrorKind.Other, $"'{newName}' is not a valid name.");
            var item = await FindListedAsync(client, address.Path, token).ConfigureAwait(false)
                       ?? throw new RemoteFileSystemException(RemoteErrorKind.NotFound, $"{address.Path} does not exist.");
            var target = Child(Parent(item.FullName), newName);
            if (await client.ExistsAsync(target, token).ConfigureAwait(false))
                throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, $"{target} already exists.");
            if (item.IsSymbolicLink)
                item.MoveTo(target); // renames the link itself (SSH.NET has no asynchronous form; one round trip)
            else
                await client.RenameFileAsync(item.FullName, target, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>The item as listed in its parent folder (its path not resolved through links), or null.</summary>
    private static async Task<ISftpFile?> FindListedAsync(SftpClient client, string path, CancellationToken token)
    {
        var name = Name(path);
        await foreach (var file in client.ListDirectoryAsync(Parent(path), token).ConfigureAwait(false))
        {
            if (string.Equals(file.Name, name, StringComparison.Ordinal))
                return file;
        }

        return null;
    }

    /// <summary>Deletes a listed item: links and files are removed themselves; real folders after their content.</summary>
    private static async Task DeleteItemAsync(SftpClient client, ISftpFile item, int depth, CancellationToken token)
    {
        if (item.IsDirectory && !item.IsSymbolicLink)
        {
            if (depth > MaxDeleteDepth)
                throw new RemoteFileSystemException(RemoteErrorKind.Other, $"{item.FullName} is nested too deeply to delete.");
            var children = new List<ISftpFile>();
            await foreach (var child in client.ListDirectoryAsync(item.FullName, token).ConfigureAwait(false))
            {
                if (child.Name is not "." and not "..")
                    children.Add(child);
            }

            foreach (var child in children)
                await DeleteItemAsync(client, child, depth + 1, token).ConfigureAwait(false);
        }

        await item.DeleteAsync(token).ConfigureAwait(false); // RMDIR or REMOVE on the item's own path
    }

    private static async Task<bool> IsFolderAsync(SftpClient client, string path, CancellationToken token)
    {
        // SFTP has no public "stat" in SSH.NET; opening the path as a folder follows the link on the server.
        try
        {
            var enumerator = client.ListDirectoryAsync(path, token).GetAsyncEnumerator(token);
            await using (enumerator.ConfigureAwait(false))
                await enumerator.MoveNextAsync().ConfigureAwait(false);
            return true;
        }
        catch (SshException)
        {
            return false; // a file, a dangling link or no permission: shown as a file
        }
    }

    private static async Task TryDeleteFileAsync(SftpClient client, string path)
    {
        try
        {
            if (client.IsConnected)
                await client.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or SocketException)
        {
            // Best effort: a stale ".navigator-partial" file is harmless and visibly named.
        }
    }

    // ── Sessions ─────────────────────────────────────────────

    private static string ServerKey(RemoteAddress address) => address.WithPath("/").Authority;

    private Task<RemoteSessionPool<SftpClient>.Lease> AcquireAsync(RemoteAddress address, CancellationToken cancellationToken)
        => _pool.AcquireAsync(ServerKey(address), cancellationToken);

    /// <summary>
    /// Runs one operation on the server's session. A pooled session the server has closed meanwhile (idle timeout,
    /// network change) is replaced once, transparently.
    /// </summary>
    private async Task<T> RunAsync<T>(RemoteAddress address, Func<SftpClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var lease = await AcquireAsync(address, cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(lease.Session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && IsBrokenSession(ex) && !cancellationToken.IsCancellationRequested)
            {
                lease.Fail();
            }
            catch (Exception ex) when (Translate(ex, lease) is { } translated)
            {
                throw translated;
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    private static bool IsBrokenSession(Exception ex)
        => ex is SshConnectionException or ObjectDisposedException or SocketException
           || (ex is SshException && ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps SSH.NET errors to the host's error kinds. Returns null for errors that pass through unchanged.</summary>
    private static RemoteFileSystemException? Translate(Exception ex, RemoteSessionPool<SftpClient>.Lease lease)
    {
        switch (ex)
        {
            case RemoteFileSystemException or OperationCanceledException:
                return null;
            case SftpPathNotFoundException:
                return new RemoteFileSystemException(RemoteErrorKind.NotFound, ex.Message, ex);
            case SftpPermissionDeniedException:
                return new RemoteFileSystemException(RemoteErrorKind.AccessDenied, ex.Message, ex);
            case SshAuthenticationException:
                return new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, ex.Message, ex);
            case SshConnectionException or SshOperationTimeoutException or ProxyException or SocketException or ObjectDisposedException:
                lease.Fail();
                return new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex);
            case SshException:
                return new RemoteFileSystemException(RemoteErrorKind.Other, ex.Message, ex);
            default:
                return null;
        }
    }

    private async Task<SftpClient> ConnectAsync(string serverKey, CancellationToken cancellationToken)
    {
        var server = RemoteUri.Parse(serverKey);
        var saved = _store.Find(server, DefaultPort);
        var display = server.Host;
        var user = server.UserName ?? saved?.UserName;
        if (string.IsNullOrWhiteSpace(user))
        {
            user = await _context.Ui.PromptTextAsync(_strings.Format("Ssh.UserNameTitle", display), _strings["Ssh.UserName"],
                Environment.UserName).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(user))
                throw new RemoteFileSystemException(RemoteErrorKind.Cancelled, "Sign-in was cancelled.");
        }

        var port = server.Port ?? (saved is { Port: > 0 } ? saved.Port : DefaultPort);
        var host = server.Host.Trim('[', ']'); // IPv6 literals are bracketed only inside addresses
        var hostKey = string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
        var secretKey = $"{user}@{hostKey}";

        // Secrets are collected before connecting: SSH.NET asks for them from its own thread, where waiting for a
        // dialog would block a connection thread. Keyboard-interactive prompts are answered with the same password.
        AuthenticationMethod[] methods;
        if (saved is { Authentication: RemoteAuthentication.KeyFile, KeyFile: { Length: > 0 } keyFile })
        {
            methods = [new PrivateKeyAuthenticationMethod(user, await LoadKeyAsync(keyFile, cancellationToken).ConfigureAwait(false))];
        }
        else
        {
            var password = await _credentials.GetOrPromptAsync(secretKey, _context.Ui, _strings.Format("Ssh.PasswordTitle", $"{user}@{display}"),
                _strings["Ssh.Password"], cancellationToken).ConfigureAwait(false);
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (!prompt.IsEchoed)
                        prompt.Response = password;
                }
            };
            methods = [new PasswordAuthenticationMethod(user, password), interactive];
        }

        for (var attempt = 0; ; attempt++)
        {
            var info = new ConnectionInfo(host, port, user, methods) { Timeout = TimeSpan.FromSeconds(20) };
            var client = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30), OperationTimeout = TimeSpan.FromMinutes(2) };
            (string Algorithm, string Fingerprint, HostKeyStatus Status)? offered = null;
            client.HostKeyReceived += (_, e) =>
            {
                var status = _knownHosts.Check(hostKey, e.HostKeyName, e.FingerPrintSHA256);
                e.CanTrust = status == HostKeyStatus.Trusted;
                if (!e.CanTrust)
                    offered = (e.HostKeyName, e.FingerPrintSHA256, status);
            };

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (SshConnectionException) when (offered is { } key && attempt == 0)
            {
                client.Dispose();
                if (!await ConfirmHostKeyAsync(display, key.Algorithm, key.Fingerprint, key.Status == HostKeyStatus.Changed).ConfigureAwait(false))
                    throw new RemoteFileSystemException(RemoteErrorKind.HostKeyRejected, $"The key of {display} was not trusted.");
                _knownHosts.Trust(hostKey, key.Algorithm, key.Fingerprint);
            }
            catch (SshAuthenticationException ex)
            {
                client.Dispose();
                _credentials.Forget(secretKey); // ask again next time instead of repeating a wrong password
                throw new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, ex.Message, ex);
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or InvalidOperationException)
            {
                client.Dispose();
                throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private Task<bool> ConfirmHostKeyAsync(string host, string algorithm, string fingerprint, bool changed)
        => changed
            ? _context.Ui.ConfirmAsync(_strings["Ssh.ChangedHostKeyTitle"], _strings.Format("Ssh.ChangedHostKeyMessage", host, algorithm, fingerprint), _strings["Ssh.ReplaceKey"], isDestructive: true)
            : _context.Ui.ConfirmAsync(_strings["Ssh.UnknownHostTitle"], _strings.Format("Ssh.UnknownHostMessage", host, algorithm, fingerprint), _strings["Ssh.Trust"]);

    private async Task<IPrivateKeySource> LoadKeyAsync(string keyFile, CancellationToken cancellationToken)
    {
        var path = keyFile.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), keyFile.TrimStart('~', '/', '\\')) : keyFile;
        if (!File.Exists(path))
            throw new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, _strings.Format("Ssh.KeyFileMissing", path));
        try
        {
            return new PrivateKeyFile(path);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            // Encrypted key: ask for its passphrase (remembered for the session; forgotten if it is wrong).
        }
        catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            throw new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, _strings.Format("Ssh.KeyFileUnreadable", path, ex.Message), ex);
        }

        var secretKey = "passphrase:" + path;
        var passphrase = await _credentials.GetOrPromptAsync(secretKey, _context.Ui, _strings.Format("Ssh.PassphraseTitle", Path.GetFileName(path)),
            _strings["Ssh.Passphrase"], cancellationToken).ConfigureAwait(false);
        try
        {
            return new PrivateKeyFile(path, passphrase);
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            _credentials.Forget(secretKey);
            throw new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, _strings.Format("Ssh.KeyFileUnreadable", path, ex.Message), ex);
        }
    }

    // ── Paths (server paths are POSIX) ───────────────────────

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Child(string folder, string name) => folder.EndsWith('/') ? folder + name : folder + "/" + name;

    public void Dispose()
    {
        _pool.Dispose();
        _credentials.Dispose();
    }

    /// <summary>A server file stream that returns its session to the pool when disposed.</summary>
    private sealed class LeasedStream(Stream inner, RemoteSessionPool<SftpClient>.Lease lease) : Stream
    {
        private bool _released;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_released)
            {
                _released = true;
                inner.Dispose();
                lease.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                await inner.DisposeAsync().ConfigureAwait(false);
                lease.Dispose();
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
