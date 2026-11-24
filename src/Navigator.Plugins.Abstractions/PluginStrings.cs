using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Navigator.Plugins;

/// <summary>
/// A plugin's own localized strings: embedded JSON dictionaries (one per language, flat "key": "text") with the
/// logical name "Strings.{language}.json". Missing keys fall back to English, then to the key itself.
/// Each language is parsed once, on first use.
/// </summary>
public sealed class PluginStrings(Assembly assembly, Func<string> language)
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _languages = new(StringComparer.Ordinal);

    public string this[string key]
    {
        get
        {
            var current = Load(language());
            if (current.TryGetValue(key, out var text))
                return text;
            return Load("en").TryGetValue(key, out var fallback) ? fallback : key;
        }
    }

    public string Format(string key, params object?[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, this[key], arguments);

    private IReadOnlyDictionary<string, string> Load(string code)
        => _languages.GetOrAdd(code, static (name, owner) =>
        {
            using var stream = owner.GetManifestResourceStream($"Strings.{name}.json");
            if (stream is null)
                return new Dictionary<string, string>();
            using var document = JsonDocument.Parse(stream);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    map[property.Name] = property.Value.GetString()!;
            }

            return map;
        }, assembly);
}
