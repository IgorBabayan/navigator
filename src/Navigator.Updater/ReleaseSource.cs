using System.Net;
using System.Text;
using System.Text.Json;
using Navigator.Core.Versioning;

namespace Navigator.Updater;

/// <summary>A release that passed every authenticity and compatibility check.</summary>
public sealed record AvailableRelease(SemVersion Version, UpdateManifest Manifest, Uri ReleasePage, string? ReleaseNotes, IReadOnlyDictionary<string, Uri> AssetUrls, bool SignatureVerified);

public interface IReleaseSource
{
    /// <summary>The newest release newer than <paramref name="current"/> on the channel, or null.</summary>
    Task<AvailableRelease?> FindNewerReleaseAsync(SemVersion current, bool includePrereleases, CancellationToken cancellationToken);
}

/// <summary>
/// Reads GitHub Releases (HTTPS only). A release is considered only if it is published (not a draft),
/// carries update-manifest.json, the manifest version equals the tag, and — when a public key is compiled in —
/// update-manifest.json.sig verifies. Without a key, releases are accepted on checksum basis and the UI says so.
/// </summary>
public sealed class GitHubReleaseSource(HttpClient http, string repository, string? publicKey) : IReleaseSource
{
    private const int MaxManifestBytes = 256 * 1024;

    public async Task<AvailableRelease?> FindNewerReleaseAsync(SemVersion current, bool includePrereleases, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=20");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync(stream, UpdaterJsonContext.Default.ListGitHubRelease, cancellationToken).ConfigureAwait(false) ?? [];

        var candidates = releases
            .Where(r => !r.Draft && (includePrereleases || !r.Prerelease))
            .Select(r => (Release: r, Version: SemVersion.TryParse(r.TagName, out var v) ? v : null))
            .Where(x => x.Version is not null && x.Version > current && (includePrereleases || !x.Version.IsPrerelease))
            .OrderByDescending(x => x.Version);

        foreach (var (release, version) in candidates)
        {
            var available = await TryValidateAsync(release, version!, cancellationToken).ConfigureAwait(false);
            if (available is not null)
                return available;
        }

        return null;
    }

    private async Task<AvailableRelease?> TryValidateAsync(GitHubRelease release, SemVersion version, CancellationToken cancellationToken)
    {
        var urls = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var asset in release.Assets)
        {
            if (Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                urls[asset.Name] = uri;
        }

        if (!urls.TryGetValue(UpdateManifest.FileName, out var manifestUrl))
            return null;

        var manifestBytes = await DownloadSmallAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        var verified = false;
        if (publicKey is not null)
        {
            if (!urls.TryGetValue(UpdateManifest.SignatureFileName, out var signatureUrl))
                return null; // a signed build never accepts an unsigned release
            var signature = Encoding.ASCII.GetString(await DownloadSmallAsync(signatureUrl, cancellationToken).ConfigureAwait(false));
            if (!ManifestSignature.Verify(manifestBytes, signature, publicKey))
                return null;
            verified = true;
        }

        UpdateManifest manifest;
        try
        {
            manifest = UpdateManifest.Parse(manifestBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return null;
        }

        if (!SemVersion.TryParse(manifest.Version, out var manifestVersion) || manifestVersion != version)
            return null;

        var page = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var html) ? html : new Uri($"https://github.com/{repository}/releases");
        return new AvailableRelease(version, manifest, page, manifest.ReleaseNotes ?? release.Body, urls, verified);
    }

    private async Task<byte[]> DownloadSmallAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxManifestBytes)
            throw new InvalidDataException("Update metadata is unexpectedly large.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length > MaxManifestBytes ? throw new InvalidDataException("Update metadata is unexpectedly large.") : bytes;
    }
}
