using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Navigator.Presentation.Localization;

/// <summary>
/// User-facing strings. Each language is a flat JSON dictionary embedded in this assembly
/// (Localization/Strings/&lt;code&gt;.json). Adding a language = adding one JSON file; no code changes.
/// Missing keys fall back to English, then to the key itself (visible, never a crash).
/// Bindings use the indexer ("[Key]"); switching language raises a change for every key at once.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public const string DefaultLanguage = "en";
    private const string ResourcePrefix = "Navigator.Presentation.Localization.Strings.";

    private readonly Dictionary<string, string> _fallback;
    private Dictionary<string, string> _current;

    public Localizer()
    {
        _fallback = Load(DefaultLanguage) ?? [];
        _current = _fallback;
        Language = DefaultLanguage;
    }

    /// <summary>Process-wide instance used by XAML markup.</summary>
    public static Localizer Current { get; set; } = new();

    public static IReadOnlyList<string> AvailableLanguages { get; } = DiscoverLanguages();

    public string Language { get; private set; }

    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key]
        => _current.TryGetValue(key, out var value) || _fallback.TryGetValue(key, out value) ? value : key;

    public string Format(string key, params object?[] arguments) => string.Format(Culture, this[key], arguments);

    /// <summary>"system" picks the OS UI language when available, otherwise English.</summary>
    public void SetLanguage(string? code)
    {
        var requested = string.IsNullOrEmpty(code) || code == "system" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : code;
        var language = AvailableLanguages.Contains(requested) ? requested : DefaultLanguage;
        _current = language == DefaultLanguage ? _fallback : Load(language) ?? _fallback;
        Language = language;
        try
        {
            Culture = CultureInfo.GetCultureInfo(language == DefaultLanguage && requested != DefaultLanguage ? CultureInfo.CurrentCulture.Name : language);
        }
        catch (CultureNotFoundException)
        {
            Culture = CultureInfo.InvariantCulture;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    public bool HasKey(string key) => _fallback.ContainsKey(key);

    internal static IReadOnlyCollection<string> Keys(string language) => (Load(language) ?? []).Keys;

    private static Dictionary<string, string>? Load(string language)
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream(ResourcePrefix + language + ".json");
        if (stream is null)
            return null;
        return JsonSerializer.Deserialize(stream, LocalizationJsonContext.Default.DictionaryStringString);
    }

    private static List<string> DiscoverLanguages()
        => [.. typeof(Localizer).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^5])
            .Order(StringComparer.Ordinal)];
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocalizationJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
