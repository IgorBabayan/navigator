using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Navigator.Core.Platform;
using Navigator.Platform.Native;

namespace Navigator.Platform.ItemActions;

/// <summary>
/// Windows: the desktop picture through SystemParametersInfo (in-process, every monitor), the lock screen through the
/// WinRT LockScreen API, reached via Windows PowerShell (the path travels Base64-encoded, never as script text).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDesktopBackground : IDesktopBackgroundService
{
    private const uint SetDesktopWallpaper = 0x0014;
    private const uint UpdateIniFile = 0x01;
    private const uint SendChange = 0x02;

    public bool CanSetWallpaper => true;

    public bool CanSetLockScreen => OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    public Task<bool> SetWallpaperAsync(string imagePath, CancellationToken cancellationToken = default)
        => Task.Run(() => SystemParametersInfoW(SetDesktopWallpaper, 0, Path.GetFullPath(imagePath), UpdateIniFile | SendChange), cancellationToken);

    public async Task<bool> SetLockScreenAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        static string Literal(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .AppendLine("Add-Type -AssemblyName System.Runtime.WindowsRuntime")
            .Append("$path = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('").Append(Literal(Path.GetFullPath(imagePath))).AppendLine("'))")
            .AppendLine("$extensions = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 }")
            .AppendLine("$forAction = $extensions | Where-Object { $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1")
            .AppendLine("$forOperation = $extensions | Where-Object { $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1")
            .AppendLine("[void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]")
            .AppendLine("[void][Windows.System.UserProfile.LockScreen, Windows.System.UserProfile, ContentType = WindowsRuntime]")
            .AppendLine("$open = $forOperation.MakeGenericMethod([Windows.Storage.StorageFile]).Invoke($null, @([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)))")
            .AppendLine("$open.Wait(-1) | Out-Null")
            .AppendLine("$set = $forAction.Invoke($null, @([Windows.System.UserProfile.LockScreen]::SetImageFileAsync($open.Result)))")
            .AppendLine("$set.Wait(-1) | Out-Null")
            .ToString();

        var result = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Literal(script)],
            input: null, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint parameter, string value, uint flags);
}

/// <summary>
/// Linux: the desktop's own settings, chosen by XDG_CURRENT_DESKTOP — GNOME and its relatives (gsettings), Cinnamon,
/// MATE, KDE Plasma (plasma-apply-wallpaperimage / kwriteconfig), Xfce (xfconf-query, every monitor and workspace),
/// LXQt and LXDE (pcmanfm). Lock screens: GNOME and KDE Plasma.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDesktopBackground(IPlatformInfo platform) : IDesktopBackgroundService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private enum Desktop
    {
        Unknown,
        Gnome,
        Cinnamon,
        Mate,
        Kde,
        Xfce,
        Lxqt,
        Lxde,
    }

    private readonly Lazy<Desktop> _desktop = new(() => Detect(platform.DesktopEnvironment));
    private Lazy<bool>? _canSetWallpaper;
    private Lazy<bool>? _canSetLockScreen;

    /// <summary>Asked on every right-click on a picture: the PATH search runs once.</summary>
    public bool CanSetWallpaper => (_canSetWallpaper ??= new Lazy<bool>(FindWallpaperTool)).Value;

    public bool CanSetLockScreen => (_canSetLockScreen ??= new Lazy<bool>(FindLockScreenTool)).Value;

    private bool FindWallpaperTool() => _desktop.Value switch
    {
        Desktop.Gnome or Desktop.Cinnamon or Desktop.Mate => Tool("gsettings") is not null,
        Desktop.Kde => Tool("plasma-apply-wallpaperimage") is not null,
        Desktop.Xfce => Tool("xfconf-query") is not null,
        Desktop.Lxqt => Tool("pcmanfm-qt") is not null,
        Desktop.Lxde => Tool("pcmanfm") is not null,
        _ => false,
    };

    private bool FindLockScreenTool() => _desktop.Value switch
    {
        Desktop.Gnome => Tool("gsettings") is not null,
        Desktop.Kde => KWriteConfig() is not null,
        _ => false,
    };

    public async Task<bool> SetWallpaperAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(imagePath);
        var uri = new Uri(path).AbsoluteUri;
        switch (_desktop.Value)
        {
            case Desktop.Gnome:
                if (!await Run("gsettings", ["set", "org.gnome.desktop.background", "picture-uri", uri], cancellationToken).ConfigureAwait(false))
                    return false;
                // GNOME 42+ keeps a separate picture for the dark style; older versions lack the key (ignored).
                await Run("gsettings", ["set", "org.gnome.desktop.background", "picture-uri-dark", uri], cancellationToken).ConfigureAwait(false);
                return true;
            case Desktop.Cinnamon:
                return await Run("gsettings", ["set", "org.cinnamon.desktop.background", "picture-uri", uri], cancellationToken).ConfigureAwait(false);
            case Desktop.Mate:
                return await Run("gsettings", ["set", "org.mate.background", "picture-filename", path], cancellationToken).ConfigureAwait(false);
            case Desktop.Kde:
                return await Run("plasma-apply-wallpaperimage", [path], cancellationToken).ConfigureAwait(false);
            case Desktop.Xfce:
                return await SetXfceAsync(path, cancellationToken).ConfigureAwait(false);
            case Desktop.Lxqt:
                return await Run("pcmanfm-qt", ["--set-wallpaper", path], cancellationToken).ConfigureAwait(false);
            case Desktop.Lxde:
                return await Run("pcmanfm", ["--set-wallpaper", path], cancellationToken).ConfigureAwait(false);
            default:
                return false;
        }
    }

    public async Task<bool> SetLockScreenAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(Path.GetFullPath(imagePath)).AbsoluteUri;
        switch (_desktop.Value)
        {
            case Desktop.Gnome:
                return await Run("gsettings", ["set", "org.gnome.desktop.screensaver", "picture-uri", uri], cancellationToken).ConfigureAwait(false);
            case Desktop.Kde when KWriteConfig() is { } kwrite:
                return await Run(kwrite,
                    ["--file", "kscreenlockerrc", "--group", "Greeter", "--group", "Wallpaper", "--group", "org.kde.image", "--group", "General", "--key", "Image", uri],
                    cancellationToken).ConfigureAwait(false)
                    && await Run(kwrite,
                        ["--file", "kscreenlockerrc", "--group", "Greeter", "--group", "Wallpaper", "--group", "org.kde.image", "--group", "General", "--key", "PreviewImage", uri],
                        cancellationToken).ConfigureAwait(false);
            default:
                return false;
        }
    }

    /// <summary>Every "…/last-image" property: one per monitor and workspace.</summary>
    private static async Task<bool> SetXfceAsync(string path, CancellationToken cancellationToken)
    {
        var listing = await ProcessRunner.RunForOutputAsync("xfconf-query", ["-c", "xfce4-desktop", "-l"], CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (listing is null)
            return false;
        var any = false;
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.EndsWith("/last-image", StringComparison.Ordinal))
                any |= await Run("xfconf-query", ["-c", "xfce4-desktop", "-p", line, "-s", path], cancellationToken).ConfigureAwait(false);
        }

        return any;
    }

    private static async Task<bool> Run(string program, IEnumerable<string> arguments, CancellationToken cancellationToken)
        => await ProcessRunner.RunAsync(program, arguments, input: null, CommandTimeout, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 };

    private static string? Tool(string name) => ProcessRunner.FindOnPath(name);

    private static string? KWriteConfig() => Tool("kwriteconfig6") ?? Tool("kwriteconfig5");

    private static Desktop Detect(string? desktop)
    {
        // XDG_CURRENT_DESKTOP is a colon-separated list ("ubuntu:GNOME", "X-Cinnamon"); the first known name wins.
        foreach (var part in (desktop ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.ToUpperInvariant();
            if (name.Contains("CINNAMON", StringComparison.Ordinal))
                return Desktop.Cinnamon;
            if (name is "GNOME" or "UNITY" or "BUDGIE" or "PANTHEON" or "GNOME-FLASHBACK" or "COSMIC")
                return Desktop.Gnome;
            if (name == "MATE")
                return Desktop.Mate;
            if (name == "KDE")
                return Desktop.Kde;
            if (name == "XFCE")
                return Desktop.Xfce;
            if (name == "LXQT")
                return Desktop.Lxqt;
            if (name == "LXDE")
                return Desktop.Lxde;
        }

        return Desktop.Unknown;
    }
}

/// <summary>macOS: NSWorkspace sets the desktop picture of every screen (no Automation permission needed). No lock screen API.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacDesktopBackground : IDesktopBackgroundService
{
    public bool CanSetWallpaper => true;

    public bool CanSetLockScreen => false;

    public Task<bool> SetWallpaperAsync(string imagePath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var pool = MacObjC.Pool();
        var workspace = MacObjC.Send(MacObjC.Class("NSWorkspace"), MacObjC.Selector("sharedWorkspace"));
        var screens = MacObjC.Send(MacObjC.Class("NSScreen"), MacObjC.Selector("screens"));
        var options = MacObjC.Send(MacObjC.Class("NSDictionary"), MacObjC.Selector("dictionary"));
        var url = MacObjC.FileUrl(Path.GetFullPath(imagePath));
        if (workspace == 0 || screens == 0 || url == 0)
            return false;

        var set = MacObjC.Selector("setDesktopImageURL:forScreen:options:error:");
        var count = MacObjC.Count(screens);
        var done = count > 0;
        for (nuint i = 0; i < count; i++)
            done &= MacObjC.SendBool(workspace, set, url, MacObjC.At(screens, i), options, 0) != 0;
        return done;
    }, cancellationToken);

    public Task<bool> SetLockScreenAsync(string imagePath, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
