using System.Net;
using Navigator.Core.Platform;
using Navigator.Core.Versioning;

namespace Navigator.Updater;

public enum UpdateCheckStatus
{
    /// <summary>Dev build or no repository compiled in.</summary>
    NotConfigured,
    UpToDate,
    UpdateAvailable,

    /// <summary>A newer version exists but this installation cannot replace itself (package manager, read-only location…).</summary>
    ManualUpdateRequired,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, AvailableRelease? Release = null, ManifestAsset? Asset = null, string? Error = null);

/// <summary>
/// Orchestrates check → download → verify → prepare → restart. Every step is cancellable and none blocks the UI.
/// The caller supplies <c>canRestart</c>, which is false while file operations are running: the app is never
/// replaced in the middle of a copy or move.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateConfiguration _configuration;
    private readonly IReleaseSource? _source;
    private readonly UpdateDownloader _downloader;
    private readonly string _downloadDirectory;
    private readonly string _runtimeIdentifier;
    private IUpdateInstaller? _prepared;

    public UpdateService(UpdateConfiguration configuration, IPlatformInfo platform, string downloadDirectory, HttpClient? http = null)
        : this(configuration, platform.RuntimeIdentifier,
            InstallationDetector.Detect(configuration, platform.OperatingSystem, AppContext.BaseDirectory, Environment.GetEnvironmentVariable),
            downloadDirectory, http ?? CreateHttpClient(configuration.CurrentVersion))
    {
    }

    internal UpdateService(UpdateConfiguration configuration, string runtimeIdentifier, InstallationKind installation, string downloadDirectory, HttpClient http, IReleaseSource? source = null)
    {
        _configuration = configuration;
        _runtimeIdentifier = runtimeIdentifier;
        Installation = installation;
        _downloadDirectory = downloadDirectory;
        _downloader = new UpdateDownloader(http);
        _source = source ?? (configuration.Repository is { } repository ? new GitHubReleaseSource(http, repository, configuration.PublicKey) : null);
    }

    public SemVersion CurrentVersion => _configuration.CurrentVersion;

    public InstallationKind Installation { get; }

    public bool IsConfigured => _configuration.IsConfigured && _source is not null && Installation != InstallationKind.Development;

    /// <summary>True when releases are authenticated by a compiled-in signing key (not checksums alone).</summary>
    public bool RequiresSignedReleases => _configuration.PublicKey is not null;

    public string? ReleasesPage => _configuration.Repository is { } repository ? $"https://github.com/{repository}/releases" : null;

    public async Task<UpdateCheckResult> CheckAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured);

        try
        {
            var release = await _source!.FindNewerReleaseAsync(CurrentVersion, includePrereleases, cancellationToken).ConfigureAwait(false);
            if (release is null)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);

            var asset = UpdateAssetSelector.Select(release.Manifest, _runtimeIdentifier, Installation);
            return asset is null || !UpdateAssetSelector.SupportsSelfUpdate(Installation)
                ? new UpdateCheckResult(UpdateCheckStatus.ManualUpdateRequired, release)
                : new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release, asset);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>Downloads and verifies the package, then stages it. The running installation is untouched.</summary>
    public async Task DownloadAndPrepareAsync(UpdateCheckResult check, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (check is not { Status: UpdateCheckStatus.UpdateAvailable, Release: { } release, Asset: { } asset })
            throw new InvalidOperationException("No installable update.");
        if (release.Version <= CurrentVersion)
            throw new InvalidOperationException("Refusing to install an older or equal version.");
        if (!release.AssetUrls.TryGetValue(asset.Name, out var url))
            throw new InvalidDataException($"{asset.Name} is listed in the manifest but missing from the release.");

        var package = await _downloader.DownloadAsync(url, asset, Path.Combine(_downloadDirectory, release.Version.ToString()), progress, cancellationToken).ConfigureAwait(false);
        var installer = UpdateInstallerFactory.Create(Installation, AppContext.BaseDirectory)
                        ?? throw new InvalidOperationException("This installation cannot update itself.");
        await installer.PrepareAsync(package, cancellationToken).ConfigureAwait(false);
        _prepared = installer;
    }

    public bool IsReadyToRestart => _prepared is not null;

    /// <summary>
    /// Starts the swap/installer. Returns false (and does nothing) when <paramref name="canRestart"/> says the
    /// app is busy; the caller then asks again after the operations finish. On true the caller must exit.
    /// </summary>
    public bool TryRestartToUpdate(Func<bool> canRestart)
    {
        if (_prepared is null || !canRestart())
            return false;
        _prepared.StartAndExit();
        return true;
    }

    public void CleanupPreviousUpdate()
    {
        UpdateInstallerFactory.CleanupAfterSuccessfulStart(Installation, AppContext.BaseDirectory);
        try
        {
            if (Directory.Exists(_downloadDirectory))
                Directory.Delete(_downloadDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static HttpClient CreateHttpClient(SemVersion version)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        // GitHub rejects API requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Navigator-Updater/{version}");
        return client;
    }
}
