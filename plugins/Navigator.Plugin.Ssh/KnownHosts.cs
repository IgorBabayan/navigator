using System.Text.Json;
using System.Text.Json.Serialization;
using Navigator.Core.Settings;

namespace Navigator.Plugin.Ssh;

public enum HostKeyStatus
{
    Trusted,
    Unknown,
    Changed,
}

/// <summary>
/// Server keys the user accepted (trust on first use), in known_hosts.json in the plugin's data folder:
/// "host:port" → "algorithm SHA256-fingerprint". Read once, written atomically. Thread-safe.
/// </summary>
internal sealed class KnownHosts(string dataDirectory)
{
    private readonly string _path = Path.Combine(dataDirectory, "known_hosts.json");
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _keys;

    public HostKeyStatus Check(string hostKey, string algorithm, string fingerprint)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(hostKey, out var known))
                return HostKeyStatus.Unknown;
            return string.Equals(known, Format(algorithm, fingerprint), StringComparison.Ordinal) ? HostKeyStatus.Trusted : HostKeyStatus.Changed;
        }
    }

    public void Trust(string hostKey, string algorithm, string fingerprint)
    {
        lock (_gate)
        {
            var keys = Load();
            keys[hostKey] = Format(algorithm, fingerprint);
            AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(keys, KnownHostsJsonContext.Default.DictionaryStringString));
        }
    }

    private static string Format(string algorithm, string fingerprint) => algorithm + " " + fingerprint;

    private Dictionary<string, string> Load()
    {
        if (_keys is not null)
            return _keys;
        try
        {
            _keys = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(_path), KnownHostsJsonContext.Default.DictionaryStringString) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable file must not silently trust anything: start empty, so every server is asked about again.
            _keys = [];
        }

        return _keys;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class KnownHostsJsonContext : JsonSerializerContext;
