namespace Navigator.Core.Platform;

/// <summary>Desktop and lock screen pictures.</summary>
public interface IDesktopBackgroundService
{
    /// <summary>The desktop picture can be set on this system (decided once, without I/O on the caller's path).</summary>
    bool CanSetWallpaper { get; }

    /// <summary>The lock screen picture can be set on this system.</summary>
    bool CanSetLockScreen { get; }

    Task<bool> SetWallpaperAsync(string imagePath, CancellationToken cancellationToken = default);

    Task<bool> SetLockScreenAsync(string imagePath, CancellationToken cancellationToken = default);
}

public enum FontInstallResult
{
    Installed,

    /// <summary>A font file of the same name is already installed for the user; nothing was replaced.</summary>
    AlreadyInstalled,

    Failed,
}

/// <summary>Installs font files for the current user (no administrator rights needed).</summary>
public interface IFontInstaller
{
    bool IsSupported { get; }

    Task<FontInstallResult> InstallAsync(string fontPath, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of connecting a network share.</summary>
public enum NetworkConnectResult
{
    Connected,
    Cancelled,

    /// <summary>The server asked for a user name and password: ask the user and call again with them.</summary>
    NeedsCredentials,
    Failed,
}

/// <summary>Credentials for one connection attempt. Never stored or logged by the platform layer.</summary>
public sealed record NetworkCredentials(string UserName, string Password, string? Domain = null);

/// <summary>
/// Drives and disk images: mounting ISO/VHD files, ejecting removable media and mounted images, mapping and
/// disconnecting network drives. The <c>Can…</c> members decide from the name and volume kind only (no I/O), so menus
/// can ask for every right-click.
/// </summary>
public interface IDriveService
{
    /// <summary>The file is a disk image this system can mount.</summary>
    bool CanMount(string path);

    /// <summary>Mounts the image. The result carries the new volume's root when the system reports it.</summary>
    Task<DriveActionResult> MountAsync(string imagePath, CancellationToken cancellationToken = default);

    bool CanEject(VolumeInfo volume);

    Task<bool> EjectAsync(VolumeInfo volume, CancellationToken cancellationToken = default);

    /// <summary>Network drives can be mapped (Windows) or network shares connected (smb://, afp://, nfs://…) here.</summary>
    bool CanConnectNetworkDrive { get; }

    /// <summary>
    /// True when Navigator asks for the server address itself; false when the system shows its own dialog
    /// (Windows "Map Network Drive").
    /// </summary>
    bool AsksForNetworkAddress { get; }

    /// <param name="address">Share address ("smb://server/share"); ignored when <see cref="AsksForNetworkAddress"/> is false.</param>
    /// <param name="credentials">Credentials after <see cref="NetworkConnectResult.NeedsCredentials"/>; null on the first attempt.</param>
    /// <param name="ownerWindow">Native window handle that parents a system dialog (0 for none).</param>
    Task<NetworkConnectResult> ConnectNetworkDriveAsync(string? address, NetworkCredentials? credentials, nint ownerWindow, CancellationToken cancellationToken = default);

    bool CanDisconnect(VolumeInfo volume);

    Task<bool> DisconnectAsync(VolumeInfo volume, CancellationToken cancellationToken = default);
}

/// <summary>Result of mounting: whether it worked and, when the system reports it, where the image appeared.</summary>
public sealed record DriveActionResult(bool Succeeded, string? MountPath = null, string? Message = null)
{
    public static DriveActionResult Failed(string? message = null) => new(false, null, message);
}

/// <summary>A destination of "Share" (AirDrop, Mail, Messages… on macOS; the mail composer on Linux).</summary>
public sealed record ShareTarget(string Id, string Title, string IconKey);

/// <summary>
/// Hands files to other applications. Windows' share sheet is reached through the shell's own "Share" verb by the
/// presentation layer instead (it needs the Explorer context menu), so this service is unsupported there.
/// </summary>
public interface IShareService
{
    bool IsSupported { get; }

    /// <summary>Destinations for these files. On macOS this must be called on the UI (main) thread.</summary>
    Task<IReadOnlyList<ShareTarget>> GetTargetsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Shares the files with a target from <see cref="GetTargetsAsync"/>. On macOS this must be called on the UI thread.</summary>
    Task<bool> ShareAsync(IReadOnlyList<string> paths, ShareTarget target, CancellationToken cancellationToken = default);
}

/// <summary>Stand-ins for platforms (and tests) without these features.</summary>
public sealed class UnsupportedItemActions : IDesktopBackgroundService, IFontInstaller, IDriveService, IShareService
{
    public static UnsupportedItemActions Instance { get; } = new();

    public bool CanSetWallpaper => false;

    public bool CanSetLockScreen => false;

    public bool IsSupported => false;

    public bool CanConnectNetworkDrive => false;

    public bool AsksForNetworkAddress => true;

    public Task<bool> SetWallpaperAsync(string imagePath, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> SetLockScreenAsync(string imagePath, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<FontInstallResult> InstallAsync(string fontPath, CancellationToken cancellationToken = default) => Task.FromResult(FontInstallResult.Failed);

    public bool CanMount(string path) => false;

    public Task<DriveActionResult> MountAsync(string imagePath, CancellationToken cancellationToken = default) => Task.FromResult(DriveActionResult.Failed());

    public bool CanEject(VolumeInfo volume) => false;

    public Task<bool> EjectAsync(VolumeInfo volume, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<NetworkConnectResult> ConnectNetworkDriveAsync(string? address, NetworkCredentials? credentials, nint ownerWindow, CancellationToken cancellationToken = default)
        => Task.FromResult(NetworkConnectResult.Failed);

    public bool CanDisconnect(VolumeInfo volume) => false;

    public Task<bool> DisconnectAsync(VolumeInfo volume, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<IReadOnlyList<ShareTarget>> GetTargetsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ShareTarget>>([]);

    public Task<bool> ShareAsync(IReadOnlyList<string> paths, ShareTarget target, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
