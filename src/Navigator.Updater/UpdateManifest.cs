using System.Text.Json;
using System.Text.Json.Serialization;

namespace Navigator.Updater;

/// <summary>
/// Kinds of release assets. The updater only ever installs <see cref="Installer"/>, <see cref="Portable"/>,
/// <see cref="AppImage"/> and <see cref="AppArchive"/>; <see cref="DiskImage"/> is for manual downloads.
/// </summary>
public static class AssetKinds
{
    public const string Installer = "installer";
    public const string Portable = "portable";
    public const string AppImage = "appimage";
    public const string AppArchive = "app-archive";
    public const string DiskImage = "dmg";
}

/// <summary>
/// update-manifest.json, published with every GitHub Release by the release workflow.
/// It is the trusted description of the release: version, commit, and SHA-256 of every asset.
/// Its authenticity is established by update-manifest.json.sig (ECDSA P-256), not by the checksums.
/// </summary>
public sealed class UpdateManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "update-manifest.json";
    public const string SignatureFileName = "update-manifest.json.sig";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Product { get; set; } = "Navigator";

    public string Version { get; set; } = string.Empty;

    public string Channel { get; set; } = "stable";

    public string Commit { get; set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; set; }

    public string? ReleaseNotes { get; set; }

    public List<ManifestAsset> Assets { get; set; } = [];

    public static UpdateManifest Parse(ReadOnlySpan<byte> json)
    {
        var manifest = JsonSerializer.Deserialize(json, UpdaterJsonContext.Default.UpdateManifest)
                       ?? throw new InvalidDataException("The update manifest is empty.");
        if (manifest.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException($"Update manifest schema {manifest.SchemaVersion} is newer than this version supports.");
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Assets.Count == 0)
            throw new InvalidDataException("The update manifest is incomplete.");
        foreach (var asset in manifest.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Contains('/', StringComparison.Ordinal) || asset.Name.Contains('\\', StringComparison.Ordinal)
                || asset.Sha256.Length != 64 || asset.Size <= 0)
                throw new InvalidDataException($"Invalid asset entry '{asset.Name}' in the update manifest.");
        }

        return manifest;
    }

    public string Serialize() => JsonSerializer.Serialize(this, UpdaterJsonContext.Default.UpdateManifest);
}

public sealed class ManifestAsset
{
    public string Name { get; set; } = string.Empty;

    /// <summary>.NET runtime identifier: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64.</summary>
    public string Rid { get; set; } = string.Empty;

    /// <summary>One of <see cref="AssetKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class UpdaterJsonContext : JsonSerializerContext;
