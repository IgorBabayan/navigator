using Navigator.Core.Platform;
using Navigator.Platform.Hotkeys;
using Navigator.Platform.Icons;
using Navigator.Platform.Launch;
using Navigator.Platform.Preview;
using Navigator.Platform.ItemProperties;
using Navigator.Platform.Secrets;
using Navigator.Platform.Startup;
using Navigator.Platform.Trash;

namespace Navigator.Platform;

/// <summary>
/// Selects the implementation for the running OS once. Registered as singletons by the app's composition root,
/// so shared code never references a platform API directly.
/// </summary>
public static class PlatformServices
{
    public static ITrashService CreateTrash()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsRecycleBin();
        if (OperatingSystem.IsMacOS())
            return new MacTrash();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            return new FreedesktopTrash();
        return new UnsupportedTrash();
    }

    public static IFileLauncher CreateLauncher()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsFileLauncher();
        if (OperatingSystem.IsMacOS())
            return new MacFileLauncher();
        if (OperatingSystem.IsLinux())
            return new LinuxFileLauncher();
        throw new PlatformNotSupportedException();
    }

    /// <summary>OS-drawn icons for the sidebar and Home (Windows shell); elsewhere Navigator's own icons are kept.</summary>
    /// <summary>
    /// Icons the OS draws for file types, folders and drives: Windows shell image lists, the Linux desktop's icon theme
    /// (GIO + gdk-pixbuf, when installed), macOS NSWorkspace. Elsewhere Navigator keeps its own icons.
    /// </summary>
    public static ISystemIconProvider CreateSystemIcons(IPlatformInfo platform, IKnownFolderProvider? knownFolders = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (OperatingSystem.IsWindows())
            return new WindowsShellIcons();
        if (OperatingSystem.IsLinux())
            return new FreedesktopIcons(platform, knownFolders);
        if (OperatingSystem.IsMacOS())
            return new MacWorkspaceIcons(knownFolders);
        return new UnsupportedSystemIcons();
    }

    /// <summary>Document types for the "New" menu: Windows ShellNew registrations, the Templates folder elsewhere.</summary>
    public static INewItemTemplates CreateNewItemTemplates(IPlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return OperatingSystem.IsWindows() ? new WindowsNewItemTemplates() : new TemplateFolderNewItems(platform.HomeDirectory);
    }

    /// <summary>Files other applications put on the clipboard: Explorer copies and Remote Desktop file copies on Windows.</summary>
    public static ISystemFileClipboard CreateFileClipboard()
        => OperatingSystem.IsWindows() ? new DataTransfer.WindowsFileClipboard() : new DataTransfer.UnsupportedFileClipboard();

    /// <summary>The OS context menu shown under "Show more options" (Windows Explorer's classic menu; none elsewhere).</summary>
    public static IShellMenuProvider CreateShellMenus()
        => OperatingSystem.IsWindows() ? new Shell.WindowsShellMenus() : new Shell.UnsupportedShellMenus();

    /// <summary>
    /// The OS credential store for secrets that must survive a restart (OAuth refresh tokens of cloud plugins).
    /// <paramref name="directory"/> holds the DPAPI-encrypted files on Windows; other platforms keep secrets in the
    /// Keychain or the Secret Service.
    /// </summary>
    public static ISecretStore CreateSecretStore(string directory)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsDpapiSecretStore(directory);
        if (OperatingSystem.IsMacOS())
            return new MacKeychainSecretStore();
        if (OperatingSystem.IsLinux())
            return new SecretServiceStore();
        return new UnsupportedSecretStore();
    }

    /// <summary>Starts the global shortcut agent when the user signs in.</summary>
    public static ILoginItemService CreateLoginItems()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsLoginItem();
        if (OperatingSystem.IsMacOS())
            return new MacLoginItem();
        if (OperatingSystem.IsLinux())
            return new XdgLoginItem();
        return new UnsupportedLoginItem();
    }

    /// <summary>
    /// True where a background process can hold a system-wide shortcut: Windows, macOS, and Linux sessions on X11.
    /// Under Wayland no client may grab keys; there the desktop's own shortcut settings are used instead.
    /// </summary>
    public static bool SupportsShortcutAgent(IPlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
               || (OperatingSystem.IsLinux() && !platform.IsWaylandSession && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")));
    }

    /// <summary>A shortcut kept by the desktop itself (GNOME on Wayland); unsupported elsewhere.</summary>
    public static IDesktopShortcutService CreateDesktopShortcut(IPlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (OperatingSystem.IsLinux() && platform.IsWaylandSession)
            return new GnomeDesktopShortcut();
        return new UnsupportedDesktopShortcut();
    }

    /// <summary>
    /// System-wide hotkey for "open Navigator". Linux uses an X11 key grab, which does not work under Wayland even when
    /// the window itself runs through XWayland (the compositor only forwards keys to the focused client).
    /// </summary>
    public static IGlobalHotkeyService CreateGlobalHotkey(IPlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (OperatingSystem.IsWindows())
            return new WindowsGlobalHotkey();
        if (OperatingSystem.IsMacOS())
            return new MacGlobalHotkey();
        if (OperatingSystem.IsLinux() && !platform.IsWaylandSession && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return new X11GlobalHotkey();
        return new UnsupportedGlobalHotkey();
    }

    /// <summary>External previewers for the space bar (QuickLook, Seer, PowerToys Peek; Quick Look on macOS; Sushi on Linux).</summary>
    public static IQuickLookService CreateQuickLook() => new QuickLookService();

    /// <summary>Thumbnails of videos, PDFs and documents: Windows shell handlers, macOS Quick Look, freedesktop cache + poppler/ffmpeg.</summary>
    public static IPlatformThumbnailer CreateThumbnailer()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsShellThumbnailer();
        if (OperatingSystem.IsMacOS())
            return new MacQuickLookThumbnailer();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            return new FreedesktopThumbnailer();
        return new UnsupportedThumbnailer();
    }

    /// <summary>Audio playback for the preview pane: MCI on Windows, afplay on macOS, ffplay/mpv/gst-play/paplay on Linux.</summary>
    public static IAudioPreviewPlayer CreateAudioPlayer()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsMciAudioPlayer();
        var player = new ProcessAudioPlayer();
        return player.IsSupported ? player : new UnsupportedAudioPlayer();
    }

    /// <summary>Digital signatures: WinVerifyTrust (Windows), codesign/pkgutil (macOS), Authenticode decoding elsewhere.</summary>
    public static ISignatureVerifier CreateSignatureVerifier()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsSignatureVerifier();
        if (OperatingSystem.IsMacOS())
            return new MacSignatureVerifier();
        return new DecodingSignatureVerifier();
    }

    /// <summary>Owner, ACL and permissions for the Properties window's Security tab.</summary>
    public static ISecurityInfoProvider CreateSecurityInfo(IFileOwnershipProvider ownership)
        => OperatingSystem.IsWindows() ? new WindowsSecurityInfoProvider() : new UnixSecurityInfoProvider(ownership);

    /// <summary>Per-program compatibility layers (Windows only).</summary>
    public static IAppCompatibilityStore CreateAppCompatibility()
        => OperatingSystem.IsWindows() ? new WindowsAppCompatibilityStore() : new UnsupportedAppCompatibilityStore();

    /// <summary>Custom folder icons as the platform's file manager shows them.</summary>
    public static IFolderIconService CreateFolderIcons()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsFolderIconService();
        if (OperatingSystem.IsMacOS())
            return new MacFolderIconService();
        if (OperatingSystem.IsLinux())
        {
            var linux = new LinuxFolderIconService();
            if (linux.IsSupported)
                return linux;
        }

        return new UnsupportedFolderIconService();
    }
}
