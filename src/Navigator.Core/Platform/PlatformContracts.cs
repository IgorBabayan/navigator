using System.Runtime.InteropServices;

namespace Navigator.Core.Platform;

public enum OperatingSystemKind
{
    Windows,
    Linux,
    MacOS,
    Other,
}

/// <summary>Facts about the running platform. Implementations must be cheap and side-effect free.</summary>
public interface IPlatformInfo
{
    OperatingSystemKind OperatingSystem { get; }

    Architecture ProcessArchitecture { get; }

    /// <summary>Runtime identifier of this build's platform, e.g. "win-x64", "linux-arm64", "osx-arm64".</summary>
    string RuntimeIdentifier { get; }

    /// <summary>Linux desktop environment as reported by XDG_CURRENT_DESKTOP (e.g. "KDE", "GNOME", "Hyprland").</summary>
    string? DesktopEnvironment { get; }

    /// <summary>True when the session is Wayland (the window itself may still run through XWayland).</summary>
    bool IsWaylandSession { get; }

    string HomeDirectory { get; }
}

public enum KnownFolder
{
    Home,
    Desktop,
    Documents,
    Downloads,
    Pictures,
    Music,
    Videos,
}

public interface IKnownFolderProvider
{
    /// <summary>Path of a well-known user folder, or null when it does not exist on this system.</summary>
    string? GetPath(KnownFolder folder);
}

public enum VolumeKind
{
    Fixed,
    Removable,
    Network,
    Optical,
    Ram,
    Unknown,
}

public sealed record VolumeInfo(
    string RootPath,
    string Label,
    VolumeKind Kind,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool IsReady)
{
    public double UsedFraction => TotalBytes > 0 ? Math.Clamp(1 - ((double)FreeBytes / TotalBytes), 0, 1) : 0;
}

public interface IVolumeProvider
{
    IReadOnlyList<VolumeInfo> GetVolumes();

    /// <summary>Raised (on any thread) when volumes are mounted, unmounted or inserted.</summary>
    event EventHandler? VolumesChanged;
}

/// <summary>An item currently in the Recycle Bin / Trash.</summary>
public sealed record TrashItem(
    string Id,
    string Name,
    string OriginalPath,
    DateTimeOffset? DeletedAt,
    long Length,
    bool IsDirectory,
    string StoragePath);

public interface ITrashService
{
    bool IsSupported { get; }

    /// <summary>Whether <see cref="ListAsync"/>, <see cref="RestoreAsync"/> and permanent deletion are available.</summary>
    bool CanBrowse { get; }

    /// <summary>Moves a file or folder to the trash. Returns the trash entry when the platform reports it (used for undo).</summary>
    Task<TrashItem?> MoveToTrashAsync(string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores to the original location. Throws <see cref="IOException"/> if something already exists there.</summary>
    Task RestoreAsync(TrashItem item, CancellationToken cancellationToken = default);

    Task DeletePermanentlyAsync(TrashItem item, CancellationToken cancellationToken = default);

    Task EmptyAsync(CancellationToken cancellationToken = default);
}

public sealed record AppHandler(string Id, string DisplayName, string? IconPath, bool IsDefault);

public interface IFileLauncher
{
    /// <summary>Opens with the default application. Returns false if no handler is registered.</summary>
    Task<bool> OpenAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Applications registered for the item's type (may be empty where the OS offers no enumeration API).</summary>
    Task<IReadOnlyList<AppHandler>> GetHandlersAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> OpenWithAsync(string path, AppHandler handler, CancellationToken cancellationToken = default);

    /// <summary>Shows the operating system's own "Open with" chooser if it has one.</summary>
    Task<bool> ShowOpenWithDialogAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> OpenTerminalAsync(string directory, CancellationToken cancellationToken = default);

    Task<bool> RevealInSystemFileManagerAsync(string path, CancellationToken cancellationToken = default);
}

public interface INativeNotificationService
{
    bool IsSupported { get; }

    Task ShowAsync(string title, string message, CancellationToken cancellationToken = default);
}

/// <summary>Extended metadata that has no portable .NET API (owner, group).</summary>
public interface IFileOwnershipProvider
{
    /// <summary>Owner and group names, or null where the platform does not expose them.</summary>
    (string? Owner, string? Group) GetOwnership(string path);
}
