using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Navigator.Core.Platform;

/// <summary>
/// "Bring Navigator to the front" between processes of the same user: a Unix domain socket (also on Windows 10+) in the
/// user's own cache folder. The running app listens (<see cref="Listen"/>); the global shortcut agent and
/// <c>Navigator --activate</c> signal it (<see cref="TrySignal"/>) and start Navigator only when nobody answers.
/// An idle listener is one pending accept: no thread, no timer, no polling.
/// </summary>
public static class ActivationChannel
{
    private const byte Activate = (byte)'A';
    private const byte Ping = (byte)'P';
    private const byte Acknowledge = (byte)'K';
    private const int MaxSocketPath = 100; // sun_path is 104 bytes on macOS, 108 on Linux

    /// <summary>The socket path for a cache folder (a short temp path when the folder's path is too long for a socket).</summary>
    public static string SocketPath(AppDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var path = Path.Combine(directories.CacheDirectory, "activate.sock");
        if (Encoding.UTF8.GetByteCount(path) <= MaxSocketPath)
            return path;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directories.CacheDirectory)).AsSpan(0, 6));
        return Path.Combine(Path.GetTempPath(), "navigator-" + hash + ".sock");
    }

    /// <summary>Asks a running Navigator to show a window. False when none answered.</summary>
    public static bool TrySignal(string socketPath, TimeSpan timeout) => Exchange(socketPath, Activate, timeout);

    /// <summary>
    /// Starts listening, unless another Navigator already does (then null: that instance keeps receiving activations).
    /// <paramref name="activated"/> runs on a thread-pool thread.
    /// </summary>
    public static IDisposable? Listen(string socketPath, Action activated)
    {
        ArgumentNullException.ThrowIfNull(activated);
        if (Exchange(socketPath, Ping, TimeSpan.FromMilliseconds(500)))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        try
        {
            File.Delete(socketPath); // left over from a process that ended without cleaning up
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            socket.Listen(4);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }

        var listener = new Listener(socket, socketPath, activated);
        listener.Start();
        return listener;
    }

    private static bool Exchange(string socketPath, byte command, TimeSpan timeout)
    {
        if (!File.Exists(socketPath))
            return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            // A local socket connects (or is refused) at once; nothing to wait for.
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            socket.Send([command]);
            Span<byte> answer = stackalloc byte[1];
            return socket.Receive(answer) == 1 && answer[0] == Acknowledge;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return false;
        }
    }

    private sealed class Listener(Socket socket, string path, Action activated) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        public void Start() => _ = AcceptLoopAsync();

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await socket.AcceptAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(Socket client)
        {
            using (client)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var buffer = new byte[1];
                    if (await client.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false) != 1)
                        return;
                    if (buffer[0] == Activate)
                        activated();
                    await client.SendAsync(new[] { Acknowledge }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // A client that went away early simply gets no answer.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            socket.Dispose();
            _stop.Dispose();
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed by the next listener.
            }
        }
    }
}
