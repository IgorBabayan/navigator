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

/// <summary>Stock icons the operating system draws itself (Windows shell stock icons).</summary>
public enum SystemIconKind
{
    Folder,
    Computer,
    Network,
    DriveFixed,
    DriveRemovable,
    DriveNetwork,
    DriveOptical,
    DriveRam,
    RecycleBin,
    RecycleBinFull,
}

/// <summary>
/// String keys naming a system icon, so view models can ask for one without referencing any UI or OS type:
/// "stock:&lt;SystemIconKind&gt;" for a stock icon, "path:&lt;item&gt;" for the icon the OS shows for that very item
/// (e.g. the Downloads folder, or a program with its own icon), and "type:&lt;.ext&gt;" for the icon the OS shows for
/// every file of that type (one bitmap shared by all files with the extension).
/// </summary>
public static class SystemIconKey
{
    private const string StockPrefix = "stock:";
    private const string PathPrefix = "path:";
    private const string TypePrefix = "type:";

    private static readonly string[] StockKeys = Enum.GetNames<SystemIconKind>().Select(name => StockPrefix + name).ToArray();

    public static string Stock(SystemIconKind kind) => StockKeys[(int)kind];

    public static string ForPath(string path) => PathPrefix + path;

    /// <summary>Key of the OS icon for a file type; the extension is normalized to lower case with its dot (".pdf").</summary>
    public static string ForFileType(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (extension.Length == 0)
            return TypePrefix;
        var dotted = extension[0] == '.' ? extension : "." + extension;
        return TypePrefix + dotted.ToLowerInvariant();
    }

    /// <summary>True for a <see cref="ForFileType"/> key; <paramref name="extension"/> is "" for files without one.</summary>
    public static bool TryParseFileType(string? key, out string extension)
    {
        if (key is not null && key.StartsWith(TypePrefix, StringComparison.Ordinal))
        {
            extension = key[TypePrefix.Length..];
            return true;
        }

        extension = string.Empty;
        return false;
    }

    public static bool TryParse(string? key, out SystemIconKind kind, out string? path)
    {
        kind = default;
        path = null;
        if (string.IsNullOrEmpty(key))
            return false;
        if (key.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            path = key[PathPrefix.Length..];
            return path.Length > 0;
        }

        return key.StartsWith(StockPrefix, StringComparison.Ordinal)
               && Enum.TryParse(key.AsSpan(StockPrefix.Length), ignoreCase: false, out kind)
               && Enum.IsDefined(kind);
    }
}

/// <summary>A decoded system icon: 32-bit BGRA pixels, straight (not premultiplied) alpha, rows packed (stride = Width * 4).</summary>
public sealed record SystemIconImage(int Width, int Height, byte[] Pixels);

/// <summary>Icons drawn by the operating system. Where unsupported, callers keep Navigator's own icons.</summary>
public interface ISystemIconProvider
{
    bool IsSupported { get; }

    /// <summary>
    /// Renders the icon named by a <see cref="SystemIconKey"/> at (or just above) <paramref name="pixelSize"/>.
    /// Never blocks the caller; returns null when the icon is unavailable.
    /// </summary>
    Task<SystemIconImage?> GetIconAsync(string key, int pixelSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Files of this type carry their own icon (Windows programs, shortcuts, icon files; macOS applications), so a
    /// per-item "path:" key is needed instead of the shared "type:" key. Decided from the extension only.
    /// </summary>
    bool HasOwnIcon(string extension) => false;
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

    /// <summary>
    /// Whether the trash holds anything, answered off the caller's thread without listing it (cheap enough to call on every
    /// change); null where the platform cannot tell cheaply.
    /// </summary>
    Task<bool?> HasItemsAsync(CancellationToken cancellationToken = default) => Task.FromResult<bool?>(null);

    /// <summary>
    /// Raised (on any thread, possibly in bursts) when the trash contents may have changed, including from other apps.
    /// Platforms that cannot observe the trash never raise it. Watching starts with the first subscriber.
    /// </summary>
    event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

public sealed record AppHandler(string Id, string DisplayName, string? IconPath, bool IsDefault);

/// <summary>Ways the operating system can start a program under other credentials.</summary>
[Flags]
public enum ElevationOptions
{
    None = 0,

    /// <summary>Start with administrator rights (Windows UAC "runas").</summary>
    Administrator = 1,

    /// <summary>Start under a different account (Windows credential prompt "runasuser").</summary>
    OtherUser = 2,
}

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

    /// <summary>
    /// Which elevated launches the OS offers for this file. Decided from the name only (no I/O), so a context menu can ask
    /// for every right-click.
    /// </summary>
    ElevationOptions GetElevationOptions(string path);

    /// <summary>
    /// Starts a program with <see cref="ElevationOptions.Administrator"/> or <see cref="ElevationOptions.OtherUser"/>.
    /// False when it did not start, including when the user dismissed the system prompt.
    /// </summary>
    Task<bool> RunElevatedAsync(string path, ElevationOptions mode, CancellationToken cancellationToken = default);

    /// <summary>Extension a shortcut file gets on this platform (".lnk" on Windows; empty where shortcuts are symbolic links).</summary>
    string ShortcutExtension { get; }

    /// <summary>
    /// Creates a shortcut to <paramref name="targetPath"/> at exactly <paramref name="shortcutPath"/>. Never replaces an
    /// existing file: false when something is already there or the link could not be created.
    /// </summary>
    Task<bool> CreateShortcutAsync(string targetPath, string shortcutPath, CancellationToken cancellationToken = default);
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

/// <summary>
/// The operating system's credential store: Windows DPAPI (per-user encrypted files), the macOS login Keychain, or the
/// freedesktop Secret Service (GNOME Keyring, KWallet) on Linux. A secret is identified by a service and an account
/// name. Implementations never log or write secrets in plain text; when the platform has no store,
/// <see cref="IsPersistent"/> is false and writes fail (callers keep the secret in memory).
/// </summary>
public interface ISecretStore
{
    bool IsPersistent { get; }

    Task<string?> ReadAsync(string service, string account, CancellationToken cancellationToken = default);

    /// <summary>Stores or replaces a secret. False when it could not be stored.</summary>
    Task<bool> WriteAsync(string service, string account, string secret, CancellationToken cancellationToken = default);

    Task DeleteAsync(string service, string account, CancellationToken cancellationToken = default);
}

/// <summary>
/// One entry of the operating system's own context menu (Explorer's classic menu on Windows). <see cref="Key"/> is
/// unique within the menu it came from and is what <see cref="IShellMenu"/> needs to expand or run the entry.
/// </summary>
public sealed record ShellMenuItem(
    int Key,
    string Label,
    string? Verb,
    bool IsSeparator,
    bool IsEnabled,
    bool IsChecked,
    bool HasChildren,
    SystemIconImage? Icon)
{
    public static ShellMenuItem Separator(int key) => new(key, string.Empty, null, true, false, false, false, null);
}

/// <summary>What to ask the shell for.</summary>
/// <param name="Background">The menu of the folder itself (right-click on empty space), not of items in it.</param>
/// <param name="ExtendedVerbs">Shift was held: include the verbs the shell normally hides.</param>
/// <param name="HiddenVerbs">Verbs (or labels) left out because Navigator offers the same command itself.</param>
public sealed record ShellMenuRequest(bool Background, bool ExtendedVerbs, IReadOnlySet<string> HiddenVerbs);

/// <summary>
/// A live shell context menu. Its handlers stay loaded until it is disposed, so a menu is opened per right-click and
/// disposed when the Navigator menu closes. All members may be called from any thread.
/// </summary>
public interface IShellMenu : IDisposable
{
    IReadOnlyList<ShellMenuItem> Items { get; }

    /// <summary>Entries of a sub-menu; the shell fills some of them ("Send to", "Open with") only when asked.</summary>
    Task<IReadOnlyList<ShellMenuItem>> GetChildrenAsync(ShellMenuItem parent, CancellationToken cancellationToken = default);

    /// <summary>Runs the entry. <paramref name="ownerWindow"/> parents any dialog the handler shows (0 for none).</summary>
    Task<bool> InvokeAsync(ShellMenuItem item, nint ownerWindow, CancellationToken cancellationToken = default);
}

/// <summary>The operating system's own context menu for files and folders, where the platform has one.</summary>
public interface IShellMenuProvider
{
    bool IsSupported { get; }

    /// <summary>
    /// Builds the shell menu for <paramref name="paths"/> (items, or one folder for <see cref="ShellMenuRequest.Background"/>).
    /// Null when unsupported or when the shell could not produce one. Third-party handlers run off the caller's thread.
    /// </summary>
    Task<IShellMenu?> OpenAsync(IReadOnlyList<string> paths, ShellMenuRequest request, CancellationToken cancellationToken = default);
}
