using Navigator.Core.Platform;
using Navigator.Platform.Hotkeys;
using Navigator.Platform.Icons;
using Navigator.Platform.Launch;
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
    public static ISystemIconProvider CreateSystemIcons()
        => OperatingSystem.IsWindows() ? new WindowsShellIcons() : new UnsupportedSystemIcons();

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
}
