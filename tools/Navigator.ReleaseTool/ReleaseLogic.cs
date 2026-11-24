using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Navigator.Core.Hashing;
using Navigator.Core.Versioning;
using Navigator.Updater;

namespace Navigator.ReleaseTool;

public enum VersionBump
{
    Patch,
    Minor,
    Major,
}

/// <summary>Pure release logic, unit-tested in Navigator.UnitTests (ReleaseToolTests).</summary>
public static partial class ReleaseLogic
{
    /// <summary>
    /// Next unique release version. The series comes from version.json (major.minor); the patch is one above the
    /// highest existing tag in that series. The result is always greater than every existing tag, so a stale
    /// version.json can never produce a duplicate or a downgrade.
    /// </summary>
    public static SemVersion ComputeNextVersion(int seriesMajor, int seriesMinor, IEnumerable<string> existingTags, VersionBump bump)
    {
        var versions = existingTags
            .Select(t => SemVersion.TryParse(t, out var v) ? v : null)
            .Where(v => v is not null && !v.IsPrerelease)
            .Select(v => v!)
            .ToList();

        var highest = versions.Count == 0 ? null : versions.Max();
        SemVersion candidate = bump switch
        {
            VersionBump.Major => SemVersion.Create((highest?.Major ?? seriesMajor - 1) + 1, 0, 0),
            VersionBump.Minor => NextMinor(seriesMajor, seriesMinor, versions),
            _ => NextPatch(seriesMajor, seriesMinor, versions),
        };

        if (highest is not null && candidate <= highest)
            candidate = highest.NextPatch();
        return candidate;
    }

    private static SemVersion NextPatch(int major, int minor, List<SemVersion> versions)
    {
        var inSeries = versions.Where(v => v.Major == major && v.Minor == minor).ToList();
        return inSeries.Count == 0 ? SemVersion.Create(major, minor, 0) : inSeries.Max()!.NextPatch();
    }

    private static SemVersion NextMinor(int major, int minor, List<SemVersion> versions)
    {
        var inMajor = versions.Where(v => v.Major == major).ToList();
        var highestMinor = inMajor.Count == 0 ? minor - 1 : Math.Max(minor - 1, inMajor.Max(v => v.Minor));
        return SemVersion.Create(major, highestMinor + 1, 0);
    }

    public static (int Major, int Minor) ReadSeries(string versionJson)
    {
        var node = JsonNode.Parse(versionJson) ?? throw new InvalidDataException("version.json is empty.");
        return ((int)node["major"]!, (int)node["minor"]!);
    }

    /// <summary>Maps release labels on the merged pull request to a bump.</summary>
    public static VersionBump BumpFromLabels(IEnumerable<string> labels)
    {
        var set = labels.Select(l => l.Trim().ToLowerInvariant()).ToHashSet();
        return set.Contains("release:major") ? VersionBump.Major : set.Contains("release:minor") ? VersionBump.Minor : VersionBump.Patch;
    }

    [GeneratedRegex(@"^Navigator-(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)-(?<rid>(?:win|linux|osx)-(?:x64|arm64))(?<suffix>-setup\.exe|-portable\.zip|\.AppImage|\.tar\.gz|\.dmg|\.zip)$")]
    private static partial Regex AssetNamePattern();

    /// <summary>Classifies a release file by its name. Returns null for files that are not installable packages.</summary>
    public static (string Rid, string Kind)? ClassifyAsset(string fileName, string version)
    {
        var match = AssetNamePattern().Match(fileName);
        if (!match.Success || match.Groups["version"].Value != version)
            return null;

        var rid = match.Groups["rid"].Value;
        var kind = match.Groups["suffix"].Value switch
        {
            "-setup.exe" => AssetKinds.Installer,
            "-portable.zip" => AssetKinds.Portable,
            ".AppImage" => AssetKinds.AppImage,
            ".tar.gz" => AssetKinds.Portable,
            ".dmg" => AssetKinds.DiskImage,
            ".zip" when rid.StartsWith("osx-", StringComparison.Ordinal) => AssetKinds.AppArchive,
            _ => null,
        };

        return kind is null ? null : (rid, kind);
    }

    public static async Task<UpdateManifest> BuildManifestAsync(string version, string commit, string channel, string assetsDirectory, string? notes, DateTimeOffset publishedAt)
    {
        var manifest = new UpdateManifest
        {
            Version = version,
            Commit = commit,
            Channel = channel,
            PublishedAt = publishedAt,
            ReleaseNotes = notes,
        };

        foreach (var file in Directory.GetFiles(assetsDirectory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (ClassifyAsset(name, version) is not { } classification)
                continue;
            manifest.Assets.Add(new ManifestAsset
            {
                Name = name,
                Rid = classification.Rid,
                Kind = classification.Kind,
                Size = new FileInfo(file).Length,
                Sha256 = await FileHasher.ComputeAsync(file, HashKind.Sha256).ConfigureAwait(false),
            });
        }

        if (manifest.Assets.Count == 0)
            throw new InvalidDataException($"No release packages for version {version} were found in '{assetsDirectory}'.");
        return manifest;
    }

    /// <summary>
    /// The plugin shop catalog for the plugin packages (.navplugin) in <paramref name="packagesDirectory"/>: each
    /// package's plugin.json plus its size and SHA-256. Package names must follow PluginCatalog.PackageName and each
    /// plugin may appear once, so a stale or mislabelled package fails the release instead of reaching users.
    /// </summary>
    public static async Task<Plugins.PluginCatalog> BuildPluginCatalogAsync(string packagesDirectory, string appVersion, DateTimeOffset generatedAt)
    {
        var catalog = new Plugins.PluginCatalog { AppVersion = appVersion, GeneratedAt = generatedAt };
        foreach (var file in Directory.GetFiles(packagesDirectory, "*" + Plugins.PluginCatalog.PackageExtension).Order(StringComparer.Ordinal))
        {
            var manifest = Plugins.PluginPackage.ReadManifest(file);
            var name = Path.GetFileName(file);
            if (name != Plugins.PluginCatalog.PackageName(manifest.Id, manifest.Version))
                throw new InvalidDataException($"{name} contains plugin {manifest.Id} {manifest.Version}; expected {Plugins.PluginCatalog.PackageName(manifest.Id, manifest.Version)}.");
            if (!Plugins.PluginApi.IsCompatible(manifest.ApiVersion))
                throw new InvalidDataException($"{name} targets plugin API {manifest.ApiVersion}, but this release provides {Plugins.PluginApi.Version}.");
            if (catalog.Plugins.Any(p => p.Id == manifest.Id))
                throw new InvalidDataException($"Plugin {manifest.Id} is packaged more than once.");
            var sha256 = await FileHasher.ComputeAsync(file, HashKind.Sha256).ConfigureAwait(false);
            catalog.Plugins.Add(Plugins.PluginCatalogEntry.FromManifest(manifest, name, new FileInfo(file).Length, sha256));
        }

        // Round trip through the parser: what is published is exactly what clients accept.
        return Plugins.PluginCatalog.Parse(catalog.Serialize());
    }

    /// <summary>"sha256  name" lines (sha256sum -c compatible) for every file except the checksum file itself.</summary>
    public static async Task<string> BuildChecksumsAsync(string assetsDirectory)
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.GetFiles(assetsDirectory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name == "SHA256SUMS.txt")
                continue;
            var hash = await FileHasher.ComputeAsync(file, HashKind.Sha256).ConfigureAwait(false);
            builder.Append(hash).Append("  ").Append(name).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Checks that every manifest asset exists with the right size and hash. Returns the list of problems.</summary>
    public static async Task<IReadOnlyList<string>> VerifyAsync(UpdateManifest manifest, string assetsDirectory, IReadOnlyCollection<string> requiredRids)
    {
        var problems = new List<string>();
        foreach (var asset in manifest.Assets)
        {
            var path = Path.Combine(assetsDirectory, asset.Name);
            if (!File.Exists(path))
            {
                problems.Add($"missing {asset.Name}");
                continue;
            }

            if (new FileInfo(path).Length != asset.Size)
                problems.Add($"size mismatch {asset.Name}");
            else if (!FileHasher.DigestsEqual(await FileHasher.ComputeAsync(path, HashKind.Sha256).ConfigureAwait(false), asset.Sha256))
                problems.Add($"hash mismatch {asset.Name}");
        }

        foreach (var rid in requiredRids)
        {
            if (!manifest.Assets.Any(a => a.Rid == rid))
                problems.Add($"no package for {rid}");
        }

        return problems;
    }

    /// <summary>Highest stable version among tags; used to decide which release is marked "Latest".</summary>
    public static SemVersion? HighestStable(IEnumerable<string> tags)
        => tags.Select(t => SemVersion.TryParse(t, out var v) ? v : null).Where(v => v is not null && !v.IsPrerelease).Max();

    public static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
