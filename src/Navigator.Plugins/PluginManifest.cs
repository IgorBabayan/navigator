using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Navigator.Core.Versioning;

namespace Navigator.Plugins;

/// <summary>
/// plugin.json at the root of every plugin package: identity, version, contract version and entry point.
/// Built-in plugins describe themselves with the same type (in code).
/// </summary>
public sealed partial class PluginManifest
{
    public const string FileName = "plugin.json";
    public const int MaxBytes = 64 * 1024;

    /// <summary>Lower-case letters, digits, '-' and '.', 2–64 characters ("ssh", "ftp", "acme.s3").</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>Plugin contract version the plugin was built against ("1.0", see <see cref="PluginApi"/>).</summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>Main assembly file name inside the package ("Navigator.Plugin.Ssh.dll").</summary>
    public string Assembly { get; set; } = string.Empty;

    /// <summary>Full name of the <see cref="INavigatorPlugin"/> implementation.</summary>
    public string EntryType { get; set; } = string.Empty;

    public string IconKey { get; set; } = "Plugin";

    public string? Homepage { get; set; }

    /// <summary>
    /// Operating systems the plugin runs on ("windows", "linux", "macos"); null or empty for every one (plugin API 1.3).
    /// The host refuses to install or load a plugin elsewhere, and the shop does not offer it.
    /// </summary>
    public List<string>? Platforms { get; set; }

    [JsonIgnore]
    public bool SupportsCurrentPlatform => SupportsPlatform(Platforms, CurrentPlatform);

    /// <summary>"windows", "linux" or "macos" (the values of <see cref="Platforms"/>).</summary>
    public static string CurrentPlatform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public static bool SupportsPlatform(IReadOnlyCollection<string>? platforms, string platform)
        => platforms is null || platforms.Count == 0 || platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public SemVersion ParsedVersion => SemVersion.TryParse(Version, out var version) ? version : SemVersion.Create(0, 0, 0);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id) && !id.Contains("..", StringComparison.Ordinal);

    public static PluginManifest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
            throw new InvalidDataException("plugin.json is unexpectedly large.");
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            json = json[3..]; // editors on Windows often save a byte order mark
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("plugin.json is not valid JSON.", ex);
        }

        manifest ??= new PluginManifest();
        manifest.Validate(requireEntryPoint: true);
        return manifest;
    }

    /// <summary>Throws <see cref="InvalidDataException"/> when a required field is missing or malformed.</summary>
    public void Validate(bool requireEntryPoint)
    {
        if (!IsValidId(Id))
            throw new InvalidDataException($"Invalid plugin id '{Id}'.");
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException($"Plugin '{Id}' has no name.");
        if (!SemVersion.TryParse(Version, out _))
            throw new InvalidDataException($"Plugin '{Id}' has an invalid version '{Version}'.");
        if (string.IsNullOrWhiteSpace(ApiVersion))
            throw new InvalidDataException($"Plugin '{Id}' does not declare apiVersion.");
        if (Platforms is { } platforms && platforms.Exists(p => p is not ("windows" or "linux" or "macos")))
            throw new InvalidDataException($"Plugin '{Id}' declares an unknown platform.");
        if (!requireEntryPoint)
            return;
        if (string.IsNullOrWhiteSpace(Assembly) || Assembly.IndexOfAny(['/', '\\', ':']) >= 0 || !Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin '{Id}' has an invalid assembly name '{Assembly}'.");
        if (string.IsNullOrWhiteSpace(EntryType))
            throw new InvalidDataException($"Plugin '{Id}' does not declare entryType.");
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, PluginJsonContext.Default.PluginManifest);
}

/// <summary>Installed plugins (installed.json in the plugin folder): the active version of each.</summary>
public sealed class PluginState
{
    public const string FileName = "installed.json";

    public int SchemaVersion { get; set; } = 1;

    public List<InstalledPlugin> Plugins { get; set; } = [];
}

public sealed class InstalledPlugin
{
    public string Id { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginState))]
[JsonSerializable(typeof(PluginCatalog))]
internal sealed partial class PluginJsonContext : JsonSerializerContext;
