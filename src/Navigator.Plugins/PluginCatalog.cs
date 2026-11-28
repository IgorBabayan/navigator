using System.Text.Json;
using System.Text.Json.Serialization;
using Navigator.Core.Versioning;

namespace Navigator.Plugins;

/// <summary>
/// plugin-catalog.json, published with every release next to the plugin packages and signed like the update
/// manifest (plugin-catalog.json.sig). It is the plugin shop's list of available plugins.
/// </summary>
public sealed class PluginCatalog
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "plugin-catalog.json";
    public const string SignatureFileName = "plugin-catalog.json.sig";
    public const string PackageExtension = ".navplugin";
    public const int MaxBytes = 512 * 1024;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Navigator version the catalog was published with.</summary>
    public string AppVersion { get; set; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; set; }

    public List<PluginCatalogEntry> Plugins { get; set; } = [];

    public static PluginCatalog Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
            throw new InvalidDataException("The plugin catalog is unexpectedly large.");
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            json = json[3..];
        PluginCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginCatalog);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The plugin catalog is not valid JSON.", ex);
        }

        if (catalog is null)
            throw new InvalidDataException("The plugin catalog is empty.");
        if (catalog.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException($"Plugin catalog schema {catalog.SchemaVersion} is newer than this version supports.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Plugins)
        {
            entry.Validate(requireEntryPoint: false);
            if (!seen.Add(entry.Id))
                throw new InvalidDataException($"Plugin '{entry.Id}' is listed twice in the catalog.");
            if (string.IsNullOrWhiteSpace(entry.Package) || entry.Package.IndexOfAny(['/', '\\', ':']) >= 0
                || !entry.Package.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase)
                || entry.Sha256.Length != 64 || entry.Size <= 0)
                throw new InvalidDataException($"Invalid package entry for plugin '{entry.Id}'.");
        }

        return catalog;
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, PluginJsonContext.Default.PluginCatalog);

    /// <summary>Package file name for a plugin version: "navigator-plugin-ssh-1.4.0.navplugin".</summary>
    public static string PackageName(string id, string version) => $"navigator-plugin-{id}-{version}{PackageExtension}";
}

/// <summary>A plugin offered by the shop: its manifest data plus the package's size and SHA-256.</summary>
public sealed class PluginCatalogEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = string.Empty;

    public string IconKey { get; set; } = "Plugin";

    public string? Homepage { get; set; }

    /// <summary>Operating systems the plugin runs on; null for every one (see <see cref="PluginManifest.Platforms"/>).</summary>
    public List<string>? Platforms { get; set; }

    /// <summary>Package file name, resolved against the catalog's location.</summary>
    public string Package { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    [JsonIgnore]
    public SemVersion ParsedVersion => SemVersion.TryParse(Version, out var version) ? version : SemVersion.Create(0, 0, 0);

    [JsonIgnore]
    public bool IsCompatible => PluginApi.IsCompatible(ApiVersion);

    /// <summary>The plugin runs on this operating system.</summary>
    [JsonIgnore]
    public bool SupportsCurrentPlatform => PluginManifest.SupportsPlatform(Platforms, PluginManifest.CurrentPlatform);

    internal void Validate(bool requireEntryPoint) => ToManifest().Validate(requireEntryPoint);

    public PluginManifest ToManifest() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Author = Author,
        Version = Version,
        ApiVersion = ApiVersion,
        IconKey = IconKey,
        Homepage = Homepage,
        Platforms = Platforms is null ? null : [.. Platforms],
    };

    public static PluginCatalogEntry FromManifest(PluginManifest manifest, string package, long size, string sha256)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new PluginCatalogEntry
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Description = manifest.Description,
            Author = manifest.Author,
            Version = manifest.Version,
            ApiVersion = manifest.ApiVersion,
            IconKey = manifest.IconKey,
            Homepage = manifest.Homepage,
            Platforms = manifest.Platforms is { Count: > 0 } platforms ? [.. platforms] : null,
            Package = package,
            Size = size,
            Sha256 = sha256,
        };
    }
}
