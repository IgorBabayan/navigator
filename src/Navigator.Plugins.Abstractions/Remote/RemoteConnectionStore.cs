using System.Text.Json;
using Navigator.Core.Navigation;
using Navigator.Core.Settings;

namespace Navigator.Plugins.Remote;

/// <summary>
/// Saved servers of one plugin (connections.json in the plugin's data folder). Reads once, writes atomically.
/// Thread-safe; <see cref="Changed"/> is raised on the calling thread after every change.
/// </summary>
public sealed class RemoteConnectionStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private List<RemoteConnection>? _connections;

    public RemoteConnectionStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "connections.json");
    }

    public event EventHandler? Changed;

    public IReadOnlyList<RemoteConnection> GetAll()
    {
        lock (_gate)
            return [.. Load()];
    }

    public RemoteConnection? Find(string id)
    {
        lock (_gate)
            return Load().FirstOrDefault(c => c.Id == id);
    }

    /// <summary>The saved connection for a server address (for its key file and authentication kind), or null.</summary>
    public RemoteConnection? Find(RemoteAddress address, int defaultPort)
    {
        lock (_gate)
            return Load().FirstOrDefault(c => c.Matches(address, defaultPort));
    }

    public void AddOrUpdate(RemoteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_gate)
        {
            var list = Load();
            var index = list.FindIndex(c => c.Id == connection.Id);
            if (index >= 0)
                list[index] = connection;
            else
                list.Add(connection);
            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var list = Load();
            if (list.RemoveAll(c => c.Id == id) == 0)
                return;
            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<RemoteConnection> Load()
    {
        if (_connections is not null)
            return _connections;
        try
        {
            _connections = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(_path), RemoteJsonContext.Default.ListRemoteConnection) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the unreadable file for inspection and start empty rather than failing the sidebar.
            TryPreserveCorrupt();
            _connections = [];
        }

        _connections.RemoveAll(c => string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.Scheme));
        return _connections;
    }

    private void Save(List<RemoteConnection> list)
    {
        _connections = list;
        AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(list, RemoteJsonContext.Default.ListRemoteConnection));
    }

    private void TryPreserveCorrupt()
    {
        try
        {
            File.Move(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the empty list is used.
        }
    }
}
