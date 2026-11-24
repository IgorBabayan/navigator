namespace Navigator.Plugins.Remote;

/// <summary>
/// Reuses server connections between folder listings and transfers, so browsing a server signs in once.
/// <list type="bullet">
/// <item><b>Shared</b> mode (SFTP): one session per server, used by any number of callers at once
/// (the protocol multiplexes requests).</item>
/// <item><b>Exclusive</b> mode (FTP): each caller gets a session of its own (one transfer per control connection);
/// at most <c>maxSessionsPerServer</c> exist per server and further callers wait for a free one.</item>
/// </list>
/// Sessions unused for the idle timeout are closed by a timer that runs only while sessions exist. A caller that sees
/// a session break calls <see cref="Lease.Fail"/> so the session is dropped instead of being reused.
/// </summary>
public sealed class RemoteSessionPool<TSession> : IDisposable
    where TSession : class, IDisposable
{
    private readonly Func<string, CancellationToken, Task<TSession>> _connect;
    private readonly Func<TSession, bool> _isAlive;
    private readonly bool _exclusive;
    private readonly int _maxSessionsPerServer;
    private readonly long _idleMilliseconds;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Server> _servers = new(StringComparer.Ordinal);
    private readonly Timer _sweeper;
    private bool _sweeperRunning;
    private bool _disposed;

    /// <param name="connect">Opens and signs in a new session for a server key (called outside any lock).</param>
    /// <param name="isAlive">Cheap check whether a pooled session can still be used (no network round trip).</param>
    public RemoteSessionPool(Func<string, CancellationToken, Task<TSession>> connect, Func<TSession, bool> isAlive, bool exclusive, int maxSessionsPerServer, TimeSpan idleTimeout)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(isAlive);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessionsPerServer, 1);
        _connect = connect;
        _isAlive = isAlive;
        _exclusive = exclusive;
        _maxSessionsPerServer = exclusive ? maxSessionsPerServer : 1;
        _idleMilliseconds = (long)idleTimeout.TotalMilliseconds;
        _sweeper = new Timer(static state => ((RemoteSessionPool<TSession>)state!).Sweep(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>A session for <paramref name="serverKey"/> (e.g. "sftp://user@host:22"). Dispose the lease when done.</summary>
    public async Task<Lease> AcquireAsync(string serverKey, CancellationToken cancellationToken)
    {
        Server server;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_servers.TryGetValue(serverKey, out server!))
            {
                server = new Server(_maxSessionsPerServer);
                _servers.Add(serverKey, server);
            }
        }

        return _exclusive
            ? await AcquireExclusiveAsync(server, serverKey, cancellationToken).ConfigureAwait(false)
            : await AcquireSharedAsync(server, serverKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes every session of a server (e.g. after its saved settings changed).</summary>
    public void Close(string serverKey)
    {
        List<TSession> closing = [];
        lock (_gate)
        {
            if (!_servers.TryGetValue(serverKey, out var server))
                return;
            closing.AddRange(server.Idle.Select(e => e.Session));
            server.Idle.Clear();
            if (server.Shared is { } shared)
            {
                closing.Add(shared.Session);
                server.Shared = null;
            }
        }

        DisposeAll(closing);
    }

    private async Task<Lease> AcquireSharedAsync(Server server, string key, CancellationToken cancellationToken)
    {
        if (TryShare(server) is { } ready)
            return ready;

        await server.Connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryShare(server) is { } connected)
                return connected; // another caller connected while this one waited

            var session = await _connect(key, cancellationToken).ConfigureAwait(false);
            var entry = new Entry(session);
            lock (_gate)
            {
                if (_disposed)
                {
                    session.Dispose();
                    throw new ObjectDisposedException(GetType().Name);
                }

                var previous = server.Shared;
                server.Shared = entry;
                entry.Users = 1;
                StartSweeper();
                if (previous is not null)
                    DisposeQuietly(previous.Session);
            }

            return new Lease(this, server, entry);
        }
        finally
        {
            server.Connecting.Release();
        }
    }

    private Lease? TryShare(Server server)
    {
        lock (_gate)
        {
            if (server.Shared is { } shared && !shared.Failed && _isAlive(shared.Session))
            {
                shared.Users++;
                return new Lease(this, server, shared);
            }
        }

        return null;
    }

    private async Task<Lease> AcquireExclusiveAsync(Server server, string key, CancellationToken cancellationToken)
    {
        await server.Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<TSession> stale = [];
            Entry? reused = null;
            lock (_gate)
            {
                while (server.Idle.Count > 0)
                {
                    var candidate = server.Idle.Pop();
                    if (_isAlive(candidate.Session))
                    {
                        reused = candidate;
                        break;
                    }

                    stale.Add(candidate.Session);
                }
            }

            DisposeAll(stale);
            if (reused is not null)
            {
                reused.Users = 1;
                return new Lease(this, server, reused);
            }

            var session = await _connect(key, cancellationToken).ConfigureAwait(false);
            lock (_gate)
                StartSweeper();
            return new Lease(this, server, new Entry(session) { Users = 1 });
        }
        catch
        {
            server.Slots.Release();
            throw;
        }
    }

    private void Release(Server server, Entry entry)
    {
        TSession? dispose = null;
        lock (_gate)
        {
            entry.Users--;
            entry.LastUsed = Environment.TickCount64;
            if (_exclusive)
            {
                if (entry.Failed || _disposed || !_isAlive(entry.Session))
                    dispose = entry.Session;
                else
                    server.Idle.Push(entry);
            }
            else if (entry.Failed && entry.Users == 0)
            {
                if (ReferenceEquals(server.Shared, entry))
                    server.Shared = null;
                dispose = entry.Session;
            }
        }

        if (dispose is not null)
            DisposeQuietly(dispose);
        if (_exclusive && !_disposed)
            server.Slots.Release();
    }

    private void MarkFailed(Server server, Entry entry)
    {
        lock (_gate)
        {
            entry.Failed = true;
            if (ReferenceEquals(server.Shared, entry))
                server.Shared = null; // new callers connect again; current users finish (and fail) on the old one
        }
    }

    private void StartSweeper()
    {
        if (_sweeperRunning || _disposed)
            return;
        _sweeperRunning = true;
        var period = Math.Clamp(_idleMilliseconds / 2, 1_000, 60_000);
        _sweeper.Change(period, period);
    }

    private void Sweep()
    {
        List<TSession> closing = [];
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = Environment.TickCount64;
            var empty = new List<string>();
            foreach (var (key, server) in _servers)
            {
                if (server.Idle.Count > 0)
                {
                    var keep = server.Idle.Where(e => now - e.LastUsed < _idleMilliseconds).Reverse().ToList();
                    closing.AddRange(server.Idle.Where(e => now - e.LastUsed >= _idleMilliseconds).Select(e => e.Session));
                    server.Idle.Clear();
                    foreach (var entry in keep)
                        server.Idle.Push(entry);
                }

                if (server.Shared is { Users: 0 } shared && now - shared.LastUsed >= _idleMilliseconds)
                {
                    closing.Add(shared.Session);
                    server.Shared = null;
                }

                if (server.Idle.Count == 0 && server.Shared is null && server.Slots.CurrentCount == _maxSessionsPerServer)
                    empty.Add(key);
            }

            foreach (var key in empty)
                _servers.Remove(key);
            if (_servers.Count == 0)
            {
                _sweeperRunning = false;
                _sweeper.Change(Timeout.Infinite, Timeout.Infinite); // nothing left to watch: no timer wake-ups
            }
        }

        DisposeAll(closing);
    }

    private static void DisposeAll(List<TSession> sessions)
    {
        foreach (var session in sessions)
            DisposeQuietly(session);
    }

    private static void DisposeQuietly(TSession session)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            // A connection that is already broken may complain while closing; there is nothing left to release.
        }
    }

    public void Dispose()
    {
        List<TSession> closing = [];
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var server in _servers.Values)
            {
                closing.AddRange(server.Idle.Select(e => e.Session));
                if (server.Shared is { } shared)
                    closing.Add(shared.Session);
            }

            _servers.Clear();
        }

        _sweeper.Dispose();
        DisposeAll(closing);
    }

    /// <summary>Use of one session. Dispose it when the operation (or the stream built on it) is finished.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly RemoteSessionPool<TSession> _pool;
        private readonly Server _server;
        private readonly Entry _entry;
        private int _released;

        internal Lease(RemoteSessionPool<TSession> pool, Server server, Entry entry)
        {
            _pool = pool;
            _server = server;
            _entry = entry;
        }

        public TSession Session => _entry.Session;

        /// <summary>The session is broken: it is closed instead of being reused.</summary>
        public void Fail() => _pool.MarkFailed(_server, _entry);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _pool.Release(_server, _entry);
        }
    }

    internal sealed class Server(int slots)
    {
        public SemaphoreSlim Slots { get; } = new(slots, slots);

        public SemaphoreSlim Connecting { get; } = new(1, 1);

        public Stack<Entry> Idle { get; } = new();

        public Entry? Shared { get; set; }
    }

    internal sealed class Entry(TSession session)
    {
        public TSession Session { get; } = session;

        public int Users { get; set; }

        public long LastUsed { get; set; } = Environment.TickCount64;

        public bool Failed { get; set; }
    }
}
