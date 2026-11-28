using System.Text.Json;
using System.Text.Json.Serialization;
using Navigator.Core.Settings;

namespace Navigator.Plugins;

/// <summary>
/// A plugin's own small settings (switches, a few strings) in <c>preferences.json</c> of its data folder (plugin API 1.3).
/// Read once on first use, written atomically on every change; thread safe. Not for secrets
/// (<see cref="IPluginContext.Secrets"/>) or large data.
/// </summary>
public sealed class PluginPreferences
{
    public const string FileName = "preferences.json";
    private const int MaxBytes = 256 * 1024;

    private readonly Func<string> _directory;
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _values;

    /// <param name="directory">The plugin's data folder (<see cref="IPluginContext.DataDirectory"/>); asked for on first use.</param>
    public PluginPreferences(Func<string> directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    public event EventHandler? Changed;

    public bool GetBool(string key, bool fallback)
        => GetString(key) is { } text && bool.TryParse(text, out var value) ? value : fallback;

    public void SetBool(string key, bool value) => SetString(key, value ? "true" : "false");

    public string? GetString(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
            return Load().TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>Stores <paramref name="value"/>; null removes the key.</summary>
    public void SetString(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            var values = Load();
            if (value is null ? !values.Remove(key) : values.TryGetValue(key, out var old) && old == value)
                return;
            if (value is not null)
                values[key] = value;
            AtomicFile.WriteAllBytes(Path.Combine(_directory(), FileName), JsonSerializer.SerializeToUtf8Bytes(values, PreferencesJsonContext.Default.DictionaryStringString));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, string> Load()
    {
        if (_values is not null)
            return _values;
        try
        {
            var path = Path.Combine(_directory(), FileName);
            if (File.Exists(path) && new FileInfo(path).Length <= MaxBytes)
                _values = JsonSerializer.Deserialize(File.ReadAllBytes(path), PreferencesJsonContext.Default.DictionaryStringString);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable preferences: the defaults apply and the next change rewrites the file.
        }

        return _values ??= new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class PreferencesJsonContext : JsonSerializerContext;
