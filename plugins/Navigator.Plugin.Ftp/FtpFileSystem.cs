using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Navigator.Core.Navigation;
using Navigator.Core.Settings;
using Navigator.Plugins;
using Navigator.Plugins.Remote;

namespace Navigator.Plugin.Ftp;

/// <summary>
/// ftp:// and ftps:// (explicit TLS) addresses. FTP allows one transfer per control connection, so the pool hands each
/// operation a session of its own (up to <see cref="MaxSessionsPerServer"/> per server, reused while idle).
/// Uploads go to a temporary name and are renamed into place after checking the name is free.
/// </summary>
internal sealed class FtpFileSystem : IRemoteFileSystem, IDisposable
{
    public const string PlainScheme = "ftp";
    public const string SecureScheme = "ftps";
    public const int DefaultPort = 21;
    private const int MaxSessionsPerServer = 3;
    private const int MaxDeleteDepth = 256;
    private const int MaxListingBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly IPluginContext _context;
    private readonly RemoteConnectionStore _store;
    private readonly PluginStrings _strings;
    private readonly CredentialCache _credentials = new();
    private readonly TrustedCertificates _certificates;
    private readonly RemoteSessionPool<FtpSession> _pool;

    /// <summary>Servers that need TLS 1.2 for their data connections (see <see cref="FtpSession.TlsResumptionRejected"/>).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _tls12Servers = new(StringComparer.Ordinal);

    public FtpFileSystem(IPluginContext context, RemoteConnectionStore store, PluginStrings strings)
    {
        _context = context;
        _store = store;
        _strings = strings;
        _certificates = new TrustedCertificates(context.DataDirectory);
        _pool = new RemoteSessionPool<FtpSession>(ConnectAsync, static session => session.IsConnected, exclusive: true, MaxSessionsPerServer, IdleTimeout);
    }

    public IReadOnlyList<string> Schemes { get; } = [PlainScheme, SecureScheme];

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteAddress folder, CancellationToken cancellationToken)
        => RunAsync(folder, async (session, token) =>
        {
            var entries = await ListCoreAsync(session, folder.Path, token).ConfigureAwait(false);
            if (entries.Count == 0 && !session.SupportsMlsd)
            {
                // Some servers (vsftpd) answer LIST of a missing folder with an empty listing: confirm the folder exists.
                var reply = await session.CommandAsync("CWD " + FtpSession.Argument(folder.Path), token).ConfigureAwait(false);
                if (!reply.IsSuccess)
                    throw reply.Code is 550 or 450 or 501 ? new RemoteFileSystemException(RemoteErrorKind.NotFound, reply.ToString()) : FtpSession.ReplyError(reply);
            }

            return entries;
        }, cancellationToken);

    public Task<RemoteEntry?> GetEntryAsync(RemoteAddress address, CancellationToken cancellationToken)
        => RunAsync(address, async (session, token) =>
        {
            if (address.IsRoot)
                return new RemoteEntry("/", true, 0, DateTime.MinValue);
            if (session.SupportsMlsd)
            {
                var reply = await session.CommandAsync("MLST " + FtpSession.Argument(address.Path), token).ConfigureAwait(false);
                if (reply.Code == 250)
                {
                    foreach (var line in reply.Lines)
                    {
                        if (FtpListParser.ParseMlsd(line.TrimStart()) is { } entry)
                            return entry with { Name = Name(address.Path) };
                    }
                }

                if (reply.Code is 550 or 450)
                    return null;
            }

            // Older servers: look for the name in the parent folder's listing.
            var name = Name(address.Path);
            try
            {
                var siblings = await ListCoreAsync(session, Parent(address.Path), token).ConfigureAwait(false);
                return siblings.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            }
            catch (RemoteFileSystemException ex) when (ex.Kind == RemoteErrorKind.NotFound)
            {
                return null;
            }
        }, cancellationToken);

    public async Task<Stream> OpenReadAsync(RemoteAddress file, CancellationToken cancellationToken)
    {
        // The session stays leased while the caller reads; the stream returns it to the pool.
        var lease = await _pool.AcquireAsync(ServerKey(file), cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await lease.Session.OpenDataAsync("RETR " + FtpSession.Argument(file.Path), cancellationToken).ConfigureAwait(false);
            return new LeasedDownload(data, lease);
        }
        catch (Exception ex)
        {
            if (ex is RemoteFileSystemException { Kind: RemoteErrorKind.ConnectionFailed } or IOException or SocketException)
                lease.Fail();
            lease.Dispose();
            throw Translate(ex) ?? throw new RemoteFileSystemException(RemoteErrorKind.Other, ex.Message, ex);
        }
    }

    public Task UploadAsync(Stream source, RemoteAddress target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var start = source.CanSeek ? source.Position : -1L;
        var attempted = false;
        string? orphan = null; // a temporary file the previous attempt could not delete (its connection died)
        return RunAsync(target, async (session, token) =>
        {
            if (orphan is not null && await TryDeleteAsync(session, orphan).ConfigureAwait(false))
                orphan = null;

            // A retry on a fresh session must send the whole file again, never the unread remainder.
            if (attempted)
            {
                if (start < 0)
                    throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The connection was lost during the upload.");
                source.Position = start;
            }

            attempted = true;
            if (await ExistsAsync(session, target.Path, token).ConfigureAwait(false))
                throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, $"{target.Path} already exists.");

            var temporary = Child(Parent(target.Path), $".{Name(target.Path)}.{Guid.NewGuid().ToString("N")[..8]}.navigator-partial");
            try
            {
                var data = await session.OpenDataAsync("STOR " + FtpSession.Argument(temporary), token).ConfigureAwait(false);
                await using (data.ConfigureAwait(false))
                    await source.CopyToAsync(data, 256 * 1024, token).ConfigureAwait(false);
                await session.CompleteTransferAsync(token).ConfigureAwait(false);

                // RNTO replaces an existing file on most servers, so the name is checked again right before.
                if (await ExistsAsync(session, target.Path, token).ConfigureAwait(false))
                    throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, $"{target.Path} already exists.");
                await RenameCoreAsync(session, temporary, target.Path, token).ConfigureAwait(false);
            }
            catch
            {
                if (!await TryDeleteAsync(session, temporary).ConfigureAwait(false))
                    orphan = temporary;
                throw;
            }

            return true;
        }, cancellationToken);
    }

    public Task CreateFolderAsync(RemoteAddress folder, CancellationToken cancellationToken)
        => RunAsync(folder, async (session, token) =>
        {
            if (await ExistsAsync(session, folder.Path, token).ConfigureAwait(false))
                throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, $"{folder.Path} already exists.");
            var reply = await session.CommandAsync("MKD " + FtpSession.Argument(folder.Path), token).ConfigureAwait(false);
            return reply.Code == 257 ? true : throw FtpSession.ReplyError(reply);
        }, cancellationToken);

    public Task DeleteAsync(RemoteAddress address, bool isDirectory, CancellationToken cancellationToken)
        => RunAsync(address, async (session, token) =>
        {
            if (address.IsRoot)
                throw new RemoteFileSystemException(RemoteErrorKind.AccessDenied, "The server's root folder cannot be deleted.");

            // The kind comes from the parent's LIST, which marks links: a link (even to a folder) is removed itself
            // and its target is never touched. The caller's isDirectory may come from MLSD, which can hide links.
            var name = Name(address.Path);
            var item = (await ListUnixAsync(session, Parent(address.Path), strict: false, token).ConfigureAwait(false))
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (item is null && isDirectory)
                throw new RemoteFileSystemException(RemoteErrorKind.Other, $"{address.Path} could not be found in its folder's listing; it was not deleted.");
            if (item is { IsDirectory: true, IsSymbolicLink: false })
                await DeleteFolderAsync(session, address.Path, 0, token).ConfigureAwait(false);
            else
                await DeleteFileAsync(session, address.Path, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task RenameAsync(RemoteAddress address, string newName, CancellationToken cancellationToken)
        => RunAsync(address, async (session, token) =>
        {
            if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/', StringComparison.Ordinal) || newName is "." or "..")
                throw new RemoteFileSystemException(RemoteErrorKind.Other, $"'{newName}' is not a valid name.");
            var target = Child(Parent(address.Path), newName);
            if (await ExistsAsync(session, target, token).ConfigureAwait(false))
                throw new RemoteFileSystemException(RemoteErrorKind.AlreadyExists, $"{target} already exists.");
            await RenameCoreAsync(session, address.Path, target, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    // ── Operations on one session ────────────────────────────

    /// <summary>Folder contents for browsing: MLSD when the server has it (exact sizes and UTC times), else LIST.</summary>
    private static Task<IReadOnlyList<RemoteEntry>> ListCoreAsync(FtpSession session, string path, CancellationToken token)
        => session.SupportsMlsd ? ListMlsdAsync(session, path, token) : ListUnixAsync(session, path, strict: false, token);

    private static async Task<IReadOnlyList<RemoteEntry>> ListMlsdAsync(FtpSession session, string path, CancellationToken token)
    {
        var text = await ReadDataAsync(session, "MLSD " + FtpSession.Argument(path), token).ConfigureAwait(false);
        var entries = new List<RemoteEntry>();
        foreach (var line in Lines(text))
        {
            if (FtpListParser.ParseMlsd(line) is { } entry && !entry.Name.Contains('/', StringComparison.Ordinal))
                entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// LIST ("ls -l" style). Unlike MLSD it reliably marks symbolic links ("l…"): some servers report a link to a
    /// folder as a plain folder in MLSD. With <paramref name="strict"/>, a line that cannot be understood fails the
    /// listing instead of being skipped, so a recursive delete never acts on a guess.
    /// </summary>
    private static async Task<IReadOnlyList<RemoteEntry>> ListUnixAsync(FtpSession session, string path, bool strict, CancellationToken token)
    {
        string text;
        if (session.AcceptsListOptions)
        {
            try
            {
                text = await ReadDataAsync(session, "LIST -a " + FtpSession.Argument(path), token).ConfigureAwait(false);
            }
            catch (RemoteFileSystemException ex) when (ex.Kind is RemoteErrorKind.Other or RemoteErrorKind.NotFound or RemoteErrorKind.AccessDenied)
            {
                session.AcceptsListOptions = false;
                text = await ReadDataAsync(session, "LIST " + FtpSession.Argument(path), token).ConfigureAwait(false);
            }
        }
        else
        {
            text = await ReadDataAsync(session, "LIST " + FtpSession.Argument(path), token).ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;
        var entries = new List<RemoteEntry>();
        foreach (var line in Lines(text))
        {
            var entry = FtpListParser.ParseList(line, now);
            if (entry is null)
            {
                if (strict && !FtpListParser.IsIgnorable(line))
                    throw new RemoteFileSystemException(RemoteErrorKind.Other, $"The server's listing of {path} could not be read safely; nothing in it was deleted.");
                continue;
            }

            if (!entry.Name.Contains('/', StringComparison.Ordinal))
                entries.Add(entry);
        }

        return entries;
    }

    private static IEnumerable<string> Lines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0)
                yield return line;
        }
    }

    private static async Task<string> ReadDataAsync(FtpSession session, string command, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var data = await session.OpenDataAsync(command, token).ConfigureAwait(false);
        await using (data.ConfigureAwait(false))
        {
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await data.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxListingBytes)
                    throw new RemoteFileSystemException(RemoteErrorKind.Other, "The folder listing is too large.");
                buffer.Write(chunk, 0, read);
            }
        }

        await session.CompleteTransferAsync(token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task<bool> ExistsAsync(FtpSession session, string path, CancellationToken token)
    {
        if (session.SupportsMlsd)
        {
            var reply = await session.CommandAsync("MLST " + FtpSession.Argument(path), token).ConfigureAwait(false);
            if (reply.Code == 250)
                return true;
            if (reply.Code is 550 or 450 or 501)
                return false;
        }

        var name = Name(path);
        var siblings = await ListCoreAsync(session, Parent(path), token).ConfigureAwait(false);
        return siblings.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));
    }

    private static async Task RenameCoreAsync(FtpSession session, string from, string to, CancellationToken token)
    {
        var start = await session.CommandAsync("RNFR " + FtpSession.Argument(from), token).ConfigureAwait(false);
        if (start.Code != 350)
            throw FtpSession.ReplyError(start);
        var done = await session.CommandAsync("RNTO " + FtpSession.Argument(to), token).ConfigureAwait(false);
        if (done.Code != 250)
            throw FtpSession.ReplyError(done);
    }

    private static async Task DeleteFileAsync(FtpSession session, string path, CancellationToken token)
    {
        var reply = await session.CommandAsync("DELE " + FtpSession.Argument(path), token).ConfigureAwait(false);
        if (reply.Code != 250)
            throw FtpSession.ReplyError(reply);
    }

    private static async Task DeleteFolderAsync(FtpSession session, string path, int depth, CancellationToken token)
    {
        if (depth > MaxDeleteDepth)
            throw new RemoteFileSystemException(RemoteErrorKind.Other, $"{path} is nested too deeply to delete.");
        foreach (var child in await ListUnixAsync(session, path, strict: true, token).ConfigureAwait(false))
        {
            var childPath = Child(path, child.Name);
            if (child.IsDirectory && !child.IsSymbolicLink)
                await DeleteFolderAsync(session, childPath, depth + 1, token).ConfigureAwait(false);
            else
                await DeleteFileAsync(session, childPath, token).ConfigureAwait(false);
        }

        var reply = await session.CommandAsync("RMD " + FtpSession.Argument(path), token).ConfigureAwait(false);
        if (reply.Code != 250)
            throw FtpSession.ReplyError(reply);
    }

    /// <summary>Best effort; false when the session could not be used. A stale ".navigator-partial" file is harmless.</summary>
    private static async Task<bool> TryDeleteAsync(FtpSession session, string path)
    {
        try
        {
            if (!session.IsConnected)
                return false;
            await session.CommandAsync("DELE " + FtpSession.Argument(path), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is RemoteFileSystemException or IOException or SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    // ── Sessions ─────────────────────────────────────────────

    private static string ServerKey(RemoteAddress address) => address.WithPath("/").Authority;

    private async Task<T> RunAsync<T>(RemoteAddress address, Func<FtpSession, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var key = ServerKey(address);
            var lease = await _pool.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
            var wasIdle = attempt == 0;
            try
            {
                var result = await operation(lease.Session, cancellationToken).ConfigureAwait(false);
                await lease.Session.CompleteTransferAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception) when (lease.Session.TlsResumptionRejected && _tls12Servers.TryAdd(key, true) && !cancellationToken.IsCancellationRequested)
            {
                // First time for this server: reconnect every session with TLS 1.2 and try again.
                lease.Fail();
                _pool.Close(key);
                attempt = -1;
            }
            catch (Exception ex) when (wasIdle && IsBroken(ex, lease.Session) && !cancellationToken.IsCancellationRequested)
            {
                lease.Fail(); // the server closed an idle connection: retry once on a new one
            }
            catch (Exception ex)
            {
                if (IsBroken(ex, lease.Session) || ex is OperationCanceledException)
                    lease.Fail();
                throw Translate(ex) ?? throw new RemoteFileSystemException(RemoteErrorKind.Other, ex.Message, ex);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    private static bool IsBroken(Exception ex, FtpSession session)
        => !session.IsConnected || ex is IOException { InnerException: SocketException } or SocketException
           || ex is RemoteFileSystemException { Kind: RemoteErrorKind.ConnectionFailed };

    private static Exception? Translate(Exception ex) => ex switch
    {
        RemoteFileSystemException or OperationCanceledException => ex,
        AuthenticationException => new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex),
        IOException or SocketException or ObjectDisposedException => new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex),
        _ => null,
    };

    private async Task<FtpSession> ConnectAsync(string serverKey, CancellationToken cancellationToken)
    {
        var server = RemoteUri.Parse(serverKey);
        var secure = server.Scheme == SecureScheme;
        var saved = _store.Find(server, DefaultPort);
        var host = server.Host.Trim('[', ']');
        var port = server.Port ?? (saved is { Port: > 0 } ? saved.Port : DefaultPort);
        var anonymous = saved?.Authentication == RemoteAuthentication.Anonymous
                        || (saved is null && server.UserName is null)
                        || string.Equals(server.UserName, "anonymous", StringComparison.OrdinalIgnoreCase);
        var user = anonymous ? "anonymous" : server.UserName ?? saved?.UserName;
        if (string.IsNullOrWhiteSpace(user))
        {
            user = await _context.Ui.PromptTextAsync(_strings.Format("Ftp.UserNameTitle", host), _strings["Ftp.UserName"]).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(user))
                throw new RemoteFileSystemException(RemoteErrorKind.Cancelled, "Sign-in was cancelled.");
        }

        var secretKey = $"{server.Scheme}://{user}@{host}:{port}";
        var password = anonymous
            ? "anonymous@"
            : await _credentials.GetOrPromptAsync(secretKey, _context.Ui, _strings.Format("Ftp.PasswordTitle", $"{user}@{host}"), _strings["Ftp.Password"], cancellationToken).ConfigureAwait(false);
        var endpoint = new FtpEndpoint(host, port, secure, user, password, _tls12Servers.ContainsKey(serverKey));
        var certificateKey = $"{host}:{port}";

        for (var attempt = 0; ; attempt++)
        {
            // The certificate check runs inside the TLS handshake, where no dialog can be awaited: an untrusted
            // certificate is remembered, the handshake fails, the user is asked, and the connection is made again.
            (string Thumbprint, string Description)? offered = null;
            bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
            {
                if (errors == SslPolicyErrors.None)
                    return true;
                if (certificate is null)
                    return false;
                using var copy = new X509Certificate2(certificate);
                var thumbprint = Convert.ToHexString(copy.GetCertHash(HashAlgorithmName.SHA256));
                if (_certificates.IsTrusted(certificateKey, thumbprint))
                    return true;
                offered = (thumbprint, FtpSession.Describe(copy, errors));
                return false;
            }

            try
            {
                return await FtpSession.ConnectAsync(endpoint, Validate, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthenticationException) when (offered is { } certificate && attempt == 0)
            {
                var trust = await _context.Ui.ConfirmAsync(_strings["Ftp.UntrustedTitle"], _strings.Format("Ftp.UntrustedMessage", host, certificate.Description),
                    _strings["Ftp.Trust"], isDestructive: true).ConfigureAwait(false);
                if (!trust)
                    throw new RemoteFileSystemException(RemoteErrorKind.HostKeyRejected, $"The certificate of {host} was not trusted.");
                _certificates.Trust(certificateKey, certificate.Thumbprint);
            }
            catch (RemoteFileSystemException ex) when (ex.Kind == RemoteErrorKind.AuthenticationFailed)
            {
                _credentials.Forget(secretKey);
                throw;
            }
            catch (AuthenticationException ex)
            {
                throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, ex.Message, ex);
            }
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

    /// <summary>A download: reading streams the file; disposing ends the transfer and returns the session.</summary>
    private sealed class LeasedDownload(Stream data, RemoteSessionPool<FtpSession>.Lease lease) : Stream
    {
        private bool _complete;
        private bool _released;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Track(data.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Track(data.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Track(await data.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Track(await data.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Track(int read)
        {
            if (read == 0)
                _complete = true;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_released)
            {
                _released = true;
                data.Dispose();
                if (!_complete)
                    lease.Fail(); // an aborted transfer leaves the control connection in an unknown state
                lease.Dispose(); // the next command on this session reads the transfer's final reply first
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_released)
            {
                await base.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _released = true;
            await data.DisposeAsync().ConfigureAwait(false);
            if (_complete)
            {
                try
                {
                    await lease.Session.CompleteTransferAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is RemoteFileSystemException or IOException or SocketException)
                {
                    lease.Fail();
                }
            }
            else
            {
                lease.Fail();
            }

            lease.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// TLS certificates the user chose to trust although the system did not (self-signed, expired, wrong name):
/// "host:port" → SHA-256 of the certificate, in trusted_certificates.json in the plugin's data folder.
/// </summary>
internal sealed class TrustedCertificates(string dataDirectory)
{
    private readonly string _path = Path.Combine(dataDirectory, "trusted_certificates.json");
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _trusted;

    public bool IsTrusted(string server, string thumbprint)
    {
        lock (_gate)
            return Load().TryGetValue(server, out var known) && string.Equals(known, thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    public void Trust(string server, string thumbprint)
    {
        lock (_gate)
        {
            var trusted = Load();
            trusted[server] = thumbprint;
            AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(trusted, FtpJsonContext.Default.DictionaryStringString));
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_trusted is not null)
            return _trusted;
        try
        {
            _trusted = File.Exists(_path) ? JsonSerializer.Deserialize(File.ReadAllBytes(_path), FtpJsonContext.Default.DictionaryStringString) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _trusted = []; // never trust anything because of an unreadable file
        }

        return _trusted;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class FtpJsonContext : JsonSerializerContext;
