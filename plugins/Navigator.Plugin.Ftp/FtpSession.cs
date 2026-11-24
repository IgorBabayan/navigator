using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Navigator.Plugins;

namespace Navigator.Plugin.Ftp;

/// <summary>A server reply: three-digit code and the text of its last line (multi-line replies keep every line).</summary>
internal readonly record struct FtpReply(int Code, string Text, IReadOnlyList<string> Lines)
{
    public bool IsPositivePreliminary => Code is >= 100 and < 200;

    public bool IsSuccess => Code is >= 200 and < 400;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Code} {Text}");
}

/// <summary>
/// Where and how to connect. <see cref="Secure"/> means explicit TLS (AUTH TLS on the normal port).
/// <see cref="LimitToTls12"/> is set for servers that refused to resume TLS 1.3 sessions on data connections.
/// </summary>
internal sealed record FtpEndpoint(string Host, int Port, bool Secure, string User, string Password, bool LimitToTls12 = false);

/// <summary>
/// One FTP control connection (RFC 959, with RFC 2228/4217 explicit TLS, RFC 2428 EPSV and RFC 3659 MLSD/MLST).
/// Not thread-safe: the pool gives each caller its own session. Data connections are always passive and always go to
/// the control connection's address (a PASV reply naming another host is ignored, which also stops "FTP bounce").
/// </summary>
internal sealed class FtpSession : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly TcpClient _control;
    private readonly IPAddress _remoteAddress;
    private readonly string _host;
    private readonly RemoteCertificateValidationCallback? _validate;
    private readonly string _tlsName;
    private readonly byte[] _buffer = new byte[8192];
    private Stream _stream;
    private int _bufferStart;
    private int _bufferEnd;
    private bool _broken;
    private bool _pendingTransferReply;
    private bool _epsv = true;
    private bool _limitToTls12;

    private FtpSession(TcpClient control, Stream stream, string host, RemoteCertificateValidationCallback? validate)
    {
        _control = control;
        _stream = stream;
        _host = host;
        _validate = validate is null ? null : (sender, certificate, chain, errors) => validate(sender, certificate, chain, AdjustNameCheck(certificate, errors));
        _remoteAddress = ((IPEndPoint)control.Client.RemoteEndPoint!).Address;
        _tlsName = TlsNameFor(host);
    }

    /// <summary>
    /// The TLS name for a host. .NET resumes TLS sessions only for host names, never for IP literals, and many servers
    /// (vsftpd's require_ssl_reuse) refuse a data connection that does not resume the control connection's session.
    /// A server addressed by IP therefore gets a reserved, never-resolvable name (RFC 6761 ".invalid"); its
    /// certificate is checked against the real IP address in <see cref="AdjustNameCheck"/>, so nothing is relaxed.
    /// </summary>
    internal static string TlsNameFor(string host)
        => IPAddress.TryParse(host, out var ip)
            ? "ip-" + ip.ToString().Replace('.', '-').Replace(':', '-').Replace('%', '-') + ".navigator.invalid"
            : host;

    private SslPolicyErrors AdjustNameCheck(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (ReferenceEquals(_tlsName, _host) || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 || certificate is null)
            return errors;
        using var copy = new X509Certificate2(certificate);
        return copy.MatchesHostname(_host) ? errors & ~SslPolicyErrors.RemoteCertificateNameMismatch : errors;
    }

    public bool IsSecure { get; private set; }

    public bool SupportsMlsd { get; private set; }

    /// <summary>False once the server rejected "LIST -a path" (some treat "-a" as part of the path).</summary>
    public bool AcceptsListOptions { get; set; } = true;

    public bool IsConnected => !_broken && _control.Connected;

    /// <summary>
    /// The server refused a data connection because it did not resume the control connection's TLS session
    /// (vsftpd "require_ssl_reuse" with single-use TLS 1.3 tickets). Reconnecting with TLS 1.2 fixes it.
    /// </summary>
    public bool TlsResumptionRejected { get; private set; }

    /// <summary>Connects, upgrades to TLS when requested, signs in and reads the server's features.</summary>
    public static async Task<FtpSession> ConnectAsync(FtpEndpoint endpoint, RemoteCertificateValidationCallback validate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var client = new TcpClient { NoDelay = true };
        FtpSession? session = null;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(CommandTimeout);
                try
                {
                    await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, $"{endpoint.Host}:{endpoint.Port} did not answer.");
                }
            }

            session = new FtpSession(client, client.GetStream(), endpoint.Host, endpoint.Secure ? validate : null) { _limitToTls12 = endpoint.LimitToTls12 };
            Expect(await session.ReadReplyAsync(cancellationToken).ConfigureAwait(false), 220, RemoteErrorKind.ConnectionFailed);

            if (endpoint.Secure)
            {
                Expect(await session.CommandAsync("AUTH TLS", cancellationToken).ConfigureAwait(false), 234, RemoteErrorKind.ConnectionFailed);
                session._stream = await session.UpgradeAsync(session._stream, cancellationToken).ConfigureAwait(false);
                session.IsSecure = true;
            }

            var user = await session.CommandAsync("USER " + Argument(endpoint.User), cancellationToken).ConfigureAwait(false);
            if (user.Code == 331)
                user = await session.CommandAsync("PASS " + Argument(endpoint.Password), cancellationToken).ConfigureAwait(false);
            if (user.Code is 530 or 532 or 430)
                throw new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, user.ToString());
            Expect(user, 230, RemoteErrorKind.AuthenticationFailed);

            if (session.IsSecure)
            {
                // Protect the data connections as well (RFC 4217).
                await session.CommandAsync("PBSZ 0", cancellationToken).ConfigureAwait(false);
                Expect(await session.CommandAsync("PROT P", cancellationToken).ConfigureAwait(false), 200, RemoteErrorKind.ConnectionFailed);
            }

            var features = await session.CommandAsync("FEAT", cancellationToken).ConfigureAwait(false);
            if (features.Code == 211)
            {
                foreach (var line in features.Lines)
                {
                    var feature = line.Trim();
                    if (feature.StartsWith("MLST", StringComparison.OrdinalIgnoreCase))
                        session.SupportsMlsd = true;
                    else if (feature.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
                        await session.CommandAsync("OPTS UTF8 ON", cancellationToken).ConfigureAwait(false);
                }
            }

            Expect(await session.CommandAsync("TYPE I", cancellationToken).ConfigureAwait(false), 200, RemoteErrorKind.ConnectionFailed);
            return session;
        }
        catch
        {
            if (session is not null)
                session.Dispose();
            else
                client.Dispose();
            throw;
        }
    }

    /// <summary>Sends one command and reads its final reply.</summary>
    public async Task<FtpReply> CommandAsync(string command, CancellationToken cancellationToken)
    {
        if (_pendingTransferReply)
        {
            // A transfer ended without its final reply being read (e.g. a download stream was just disposed).
            _pendingTransferReply = false;
            await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteLineAsync(command, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a passive data connection, sends <paramref name="command"/> (RETR, STOR, MLSD, LIST…) and returns the
    /// data stream once the server accepted it. Call <see cref="CompleteTransferAsync"/> after closing the stream.
    /// </summary>
    public async Task<Stream> OpenDataAsync(string command, CancellationToken cancellationToken)
    {
        var port = await EnterPassiveAsync(cancellationToken).ConfigureAwait(false);
        var data = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(CommandTimeout);
                await data.ConnectAsync(_remoteAddress, port, timeout.Token).ConfigureAwait(false);
            }

            var reply = await CommandAsync(command, cancellationToken).ConfigureAwait(false);
            if (reply.Code is 226 or 250)
            {
                // Some servers answer an empty listing with the final reply straight away.
                data.Dispose();
                return Stream.Null;
            }

            if (!reply.IsPositivePreliminary)
            {
                if (reply.Code == 522)
                    TlsResumptionRejected = true;
                throw ReplyError(reply);
            }

            Stream stream = data.GetStream();
            _pendingTransferReply = true; // the transfer's final reply arrives after the data
            if (IsSecure)
            {
                try
                {
                    stream = await UpgradeAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException)
                {
                    // The server refused the data connection's TLS. Its reply says why (e.g. "522 … session reuse
                    // required"); reading it now also leaves the control connection ready for the next command.
                    data.Dispose();
                    _pendingTransferReply = false;
                    var final = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
                    if (final.Code == 522)
                        TlsResumptionRejected = true;
                    throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, final.ToString(), ex);
                }
            }

            return new FtpDataStream(stream, data);
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    /// <summary>Reads the reply that ends a transfer (226/250), if one is outstanding. Throws for a failed transfer.</summary>
    public async Task CompleteTransferAsync(CancellationToken cancellationToken)
    {
        if (!_pendingTransferReply)
            return;
        _pendingTransferReply = false;
        var reply = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        if (!reply.IsSuccess)
        {
            if (reply.Code == 522)
                TlsResumptionRejected = true;
            throw ReplyError(reply);
        }
    }

    private async Task<int> EnterPassiveAsync(CancellationToken cancellationToken)
    {
        if (_epsv)
        {
            var reply = await CommandAsync("EPSV", cancellationToken).ConfigureAwait(false);
            if (reply.Code == 229 && TryParseEpsv(reply.Text, out var port))
                return port;
            _epsv = false; // not supported: PASV from now on
        }

        var passive = await CommandAsync("PASV", cancellationToken).ConfigureAwait(false);
        if (passive.Code != 227 || !TryParsePasv(passive.Text, out var pasvPort))
            throw ReplyError(passive);
        return pasvPort;
    }

    /// <summary>"229 Entering Extended Passive Mode (|||6446|)".</summary>
    internal static bool TryParseEpsv(string text, out int port)
    {
        port = 0;
        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open + 4)
            return false;
        var inner = text[(open + 1)..close];
        var delimiter = inner[0];
        var parts = inner.Split(delimiter);
        return parts.Length >= 4 && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }

    /// <summary>"227 Entering Passive Mode (h1,h2,h3,h4,p1,p2)". The address part is ignored on purpose.</summary>
    internal static bool TryParsePasv(string text, out int port)
    {
        port = 0;
        var start = text.IndexOf('(', StringComparison.Ordinal);
        var end = text.IndexOf(')', start + 1);
        var numbers = (start >= 0 && end > start ? text[(start + 1)..end] : text[Math.Min(text.Length, 4)..]).Split(',');
        if (numbers.Length < 6)
            return false;
        if (!int.TryParse(numbers[^2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var high)
            || !int.TryParse(numbers[^1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var low)
            || high is < 0 or > 255 || low is < 0 or > 255)
            return false;
        port = (high << 8) | low;
        return port > 0;
    }

    private async Task<SslStream> UpgradeAsync(Stream inner, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _tlsName,
                RemoteCertificateValidationCallback = _validate,
                // The operating system's secure defaults (TLS 1.2 and 1.3), unless this server needs session-ID reuse.
                EnabledSslProtocols = _limitToTls12 ? SslProtocols.Tls12 : SslProtocols.None,
            }, cancellationToken).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ── Control channel I/O ──────────────────────────────────

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var bytes = Utf8.GetBytes(line + "\r\n");
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _broken = true;
            throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The connection to the server was lost.", ex);
        }
    }

    private async Task<FtpReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            var first = await ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                _broken = true;
                throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The server sent an invalid reply: " + first);
            }

            var lines = new List<string>(1);
            if (first.Length > 3 && first[3] == '-')
            {
                // Multi-line reply: ends with a line starting with the same code and a space.
                var terminator = first[..3] + " ";
                lines.Add(first[4..]);
                for (var count = 0; ; count++)
                {
                    var line = await ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (line.StartsWith(terminator, StringComparison.Ordinal))
                        return new FtpReply(code, line[4..], lines);
                    if (count > 10_000)
                        throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The server's reply is too long.");
                    lines.Add(line);
                }
            }

            var text = first.Length > 4 ? first[4..] : string.Empty;
            if (code == 421)
                _broken = true; // service closing the control connection
            return new FtpReply(code, text, [text]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _broken = true;
            throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The server did not answer in time.");
        }
        catch (OperationCanceledException)
        {
            _broken = true; // a reply may still arrive: this session cannot be trusted to be in step any more
            throw;
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_bufferStart == _bufferEnd)
            {
                int read;
                try
                {
                    read = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    _broken = true;
                    throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The connection to the server was lost.", ex);
                }

                if (read == 0)
                {
                    _broken = true;
                    throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The server closed the connection.");
                }

                _bufferStart = 0;
                _bufferEnd = read;
            }

            var span = _buffer.AsSpan(_bufferStart, _bufferEnd - _bufferStart);
            var newline = span.IndexOf((byte)'\n');
            if (newline < 0)
            {
                line.Write(span);
                _bufferStart = _bufferEnd;
                if (line.Length > 64 * 1024)
                    throw new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, "The server sent an overlong line.");
                continue;
            }

            line.Write(span[..newline]);
            _bufferStart += newline + 1;
            var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
            if (bytes.Length > 0 && bytes[^1] == '\r')
                bytes = bytes[..^1];
            return Utf8.GetString(bytes);
        }
    }

    // ── Helpers ──────────────────────────────────────────────

    /// <summary>Rejects CR/LF so a name can never smuggle a second command onto the control connection.</summary>
    public static string Argument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.AsSpan().IndexOfAny('\r', '\n') >= 0
            ? throw new RemoteFileSystemException(RemoteErrorKind.Other, "Names with line breaks cannot be used on FTP servers.")
            : value;
    }

    public static void Expect(FtpReply reply, int code, RemoteErrorKind kind)
    {
        if (reply.Code != code)
            throw new RemoteFileSystemException(kind, reply.ToString());
    }

    /// <summary>Maps a failed reply to an error kind (550 is "not found or no access" on most servers).</summary>
    public static RemoteFileSystemException ReplyError(FtpReply reply) => reply.Code switch
    {
        >= 400 when IsNotFoundText(reply.Text) => new RemoteFileSystemException(RemoteErrorKind.NotFound, reply.ToString()),
        530 or 532 => new RemoteFileSystemException(RemoteErrorKind.AuthenticationFailed, reply.ToString()),
        550 when reply.Text.Contains("permission", StringComparison.OrdinalIgnoreCase) || reply.Text.Contains("denied", StringComparison.OrdinalIgnoreCase)
            => new RemoteFileSystemException(RemoteErrorKind.AccessDenied, reply.ToString()),
        550 or 450 => new RemoteFileSystemException(RemoteErrorKind.NotFound, reply.ToString()),
        553 => new RemoteFileSystemException(RemoteErrorKind.AccessDenied, reply.ToString()),
        421 or 425 or 426 or 522 => new RemoteFileSystemException(RemoteErrorKind.ConnectionFailed, reply.ToString()),
        _ => new RemoteFileSystemException(RemoteErrorKind.Other, reply.ToString()),
    };

    /// <summary>Servers disagree on codes for a missing path (550, 450, even 501); the text is consistent.</summary>
    private static bool IsNotFoundText(string text)
        => text.Contains("no such", StringComparison.OrdinalIgnoreCase)
           || text.Contains("not found", StringComparison.OrdinalIgnoreCase)
           || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
           || text.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _broken = true;
        try
        {
            // QUIT is a courtesy; never wait for it.
            if (_control.Connected)
                _stream.Write("QUIT\r\n"u8);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection is already gone.
        }

        _stream.Dispose();
        _control.Dispose();
    }

    /// <summary>A data connection. Disposing closes it; the transfer's final reply is read by the next command.</summary>
    private sealed class FtpDataStream(Stream inner, TcpClient client) : Stream
    {
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(15);

        private bool _disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try
                {
                    inner.Dispose(); // TLS close_notify, then the socket
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    // The server may already have closed its side after the last byte.
                }

                client.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Graceful close: TLS close_notify (SslStream.Dispose does not send it, and servers then treat an upload as
        /// truncated), then a half-close so the server reads every byte before the socket goes away.
        /// </summary>
        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    await inner.FlushAsync().ConfigureAwait(false);
                    if (inner is SslStream ssl)
                        await ssl.ShutdownAsync().ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);

                    // Wait for the server to close its side. Closing while its reply (e.g. its close_notify) sits
                    // unread would reset the connection, and a reset makes the server drop upload bytes it has
                    // not read yet.
                    using var drain = new CancellationTokenSource(DrainTimeout);
                    var scratch = new byte[4096];
                    while (await client.Client.ReceiveAsync(scratch, SocketFlags.None, drain.Token).ConfigureAwait(false) > 0)
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                    // The server kept the connection open: close it anyway.
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // The server may already have closed its side (a finished download, or an aborted transfer).
                }

                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    // Nothing left to release.
                }

                client.Dispose();
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>For certificate prompts: what the user is shown before trusting a certificate.</summary>
    public static string Describe(X509Certificate2 certificate, SslPolicyErrors errors)
        => string.Join(Environment.NewLine,
            certificate.Subject,
            certificate.Issuer,
            certificate.NotAfter.ToString("d", CultureInfo.CurrentCulture),
            "SHA256:" + Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)),
            errors.ToString());
}
