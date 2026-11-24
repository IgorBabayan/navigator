using System.Text;
using Navigator.Core.Hashing;
using Navigator.Updater;

namespace Navigator.Plugins;

/// <summary>Where the plugin shop reads its catalog from.</summary>
/// <param name="CatalogLocation">HTTPS URL or local file path of plugin-catalog.json; null when the shop is not available (dev builds).</param>
/// <param name="PublicKey">ECDSA public key (the update signing key). When set, the catalog must carry a valid signature.</param>
public sealed record PluginStoreOptions(string? CatalogLocation, string? PublicKey, string DownloadDirectory)
{
    /// <summary>Overrides the catalog location (local folder builds, testing): a file path or an HTTPS URL.</summary>
    public const string CatalogOverrideVariable = "NAVIGATOR_PLUGIN_CATALOG";

    /// <summary>
    /// Release builds read the catalog published with the newest release of their own repository:
    /// https://github.com/{repository}/releases/latest/download/plugin-catalog.json (one request, no API rate limit).
    /// </summary>
    public static PluginStoreOptions Create(string? repository, string? publicKey, string downloadDirectory, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var location = env(CatalogOverrideVariable) is { Length: > 0 } overridden ? overridden
            : repository is { Length: > 0 } ? $"https://github.com/{repository}/releases/latest/download/{PluginCatalog.FileName}"
            : null;
        return new PluginStoreOptions(location, publicKey, downloadDirectory);
    }
}

/// <summary>A verified catalog and whether its signature was checked.</summary>
public sealed record PluginCatalogResult(PluginCatalog Catalog, bool SignatureVerified);

/// <summary>
/// Plugin shop client: downloads and verifies the catalog (size limit, ECDSA signature when a key is compiled in),
/// then downloads packages over HTTPS with resume and SHA-256 verification (the updater's downloader).
/// </summary>
public sealed class PluginStoreClient(HttpClient http, PluginStoreOptions options)
{
    private readonly UpdateDownloader _downloader = new(http);

    public bool IsConfigured => options.CatalogLocation is not null;

    public async Task<PluginCatalogResult> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var location = options.CatalogLocation ?? throw new InvalidOperationException("The plugin shop is not configured for this build.");
        var bytes = await ReadSmallAsync(location, required: true, cancellationToken).ConfigureAwait(false);
        var verified = false;
        if (options.PublicKey is { } key)
        {
            var signature = await ReadSmallAsync(Sibling(location, PluginCatalog.SignatureFileName), required: false, cancellationToken).ConfigureAwait(false);
            if (signature is null || !ManifestSignature.Verify(bytes!, Encoding.ASCII.GetString(signature), key))
                throw new InvalidDataException("The plugin catalog signature is missing or invalid. Nothing will be installed from it.");
            verified = true;
        }

        return new PluginCatalogResult(PluginCatalog.Parse(bytes!), verified);
    }

    /// <summary>Downloads (or copies, for a local catalog) a package and verifies its size and SHA-256.</summary>
    public async Task<string> DownloadAsync(PluginCatalogEntry entry, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var location = options.CatalogLocation ?? throw new InvalidOperationException("The plugin shop is not configured for this build.");
        var asset = new ManifestAsset { Name = entry.Package, Size = entry.Size, Sha256 = entry.Sha256, Kind = "plugin" };
        var directory = Path.Combine(options.DownloadDirectory, entry.Id);
        var source = Sibling(location, entry.Package);

        if (IsHttps(source, out var url))
            return await _downloader.DownloadAsync(url, asset, directory, progress, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, entry.Package);
        File.Copy(source, target, overwrite: true);
        var actual = await FileHasher.ComputeAsync(target, HashKind.Sha256, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (new FileInfo(target).Length != entry.Size || !FileHasher.DigestsEqual(actual, entry.Sha256))
        {
            File.Delete(target);
            throw new InvalidDataException($"{entry.Package} failed integrity verification (SHA-256 mismatch).");
        }

        return target;
    }

    private async Task<byte[]?> ReadSmallAsync(string location, bool required, CancellationToken cancellationToken)
    {
        if (IsHttps(location, out var url))
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!required && response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("The plugin catalog was redirected to a non-HTTPS location.");
            if (response.Content.Headers.ContentLength > PluginCatalog.MaxBytes)
                throw new InvalidDataException("The plugin catalog is unexpectedly large.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length > PluginCatalog.MaxBytes ? throw new InvalidDataException("The plugin catalog is unexpectedly large.") : bytes;
        }

        if (Uri.TryCreate(location, UriKind.Absolute, out var other) && !other.IsFile)
            throw new InvalidDataException("The plugin catalog must be an HTTPS URL or a local file.");
        if (!File.Exists(location))
            return required ? throw new FileNotFoundException("The plugin catalog was not found.", location) : null;
        if (new FileInfo(location).Length > PluginCatalog.MaxBytes)
            throw new InvalidDataException("The plugin catalog is unexpectedly large.");
        return await File.ReadAllBytesAsync(location, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A file next to the catalog (same release, or same local folder).</summary>
    internal static string Sibling(string catalogLocation, string fileName)
    {
        if (IsHttps(catalogLocation, out var url))
            return new Uri(url, Uri.EscapeDataString(fileName)).ToString();
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogLocation))!, fileName);
    }

    private static bool IsHttps(string location, out Uri url)
        => Uri.TryCreate(location, UriKind.Absolute, out url!) && url.Scheme == Uri.UriSchemeHttps;
}
