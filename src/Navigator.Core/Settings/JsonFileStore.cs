using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Navigator.Core.Settings;

/// <summary>Small typed JSON document (session, tags) with atomic saves and corrupt-file tolerance.</summary>
public sealed class JsonFileStore<T>(string filePath, JsonTypeInfo<T> typeInfo, Func<T> createDefault)
    where T : class
{
    public string FilePath { get; } = filePath;

    public T Load()
    {
        foreach (var candidate in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;
                using var stream = File.OpenRead(candidate);
                if (JsonSerializer.Deserialize(stream, typeInfo) is { } value)
                    return value;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Fall through to the backup, then to defaults: a broken session file must never block startup.
            }
        }

        return createDefault();
    }

    public void Save(T value) => AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(value, typeInfo));
}
