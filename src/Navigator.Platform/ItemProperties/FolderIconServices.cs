using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Navigator.Core.Customization;
using Navigator.Core.Platform;

namespace Navigator.Platform.ItemProperties;

public sealed class UnsupportedFolderIconService : IFolderIconService
{
    public bool IsSupported => false;

    public IReadOnlyList<string> IconExtensions => [];

    public bool SupportsIconIndex => false;

    public Task<string?> GetCustomIconAsync(string folder, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<bool> SetIconAsync(string folder, string iconPath, int index, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> ResetAsync(string folder, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>
/// Windows: desktop.ini ([.ShellClassInfo] IconResource=path,index), written as UTF-16 like Explorer does, marked hidden
/// and system, with the folder's read-only flag set so the shell reads it; Explorer is told to redraw the folder.
/// Other desktop.ini content (localized names, view settings) is preserved.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsFolderIconService : IFolderIconService
{
    private const string Section = ".ShellClassInfo";
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcnfPathW = 0x0005;

    public bool IsSupported => true;

    public IReadOnlyList<string> IconExtensions { get; } = [".ico", ".exe", ".dll", ".icl"];

    public bool SupportsIconIndex => true;

    public Task<string?> GetCustomIconAsync(string folder, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var ini = Path.Combine(folder, "desktop.ini");
            if (!File.Exists(ini))
                return null;
            var text = Read(ini);
            if (IniText.GetValue(text, Section, "IconResource") is { Length: > 0 } resource)
                return resource;
            return IniText.GetValue(text, Section, "IconFile") is { Length: > 0 } file
                ? file + "," + (IniText.GetValue(text, Section, "IconIndex") ?? "0")
                : null;
        }, cancellationToken);

    public Task<bool> SetIconAsync(string folder, string iconPath, int index, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var ini = Path.Combine(folder, "desktop.ini");
            var text = File.Exists(ini) ? Read(ini) : string.Empty;
            text = IniText.SetValue(text, Section, "IconResource", iconPath + "," + index.ToString(CultureInfo.InvariantCulture));
            text = IniText.SetValue(text, Section, "IconFile", null);
            text = IniText.SetValue(text, Section, "IconIndex", null);
            if (!Write(ini, text))
                return false;
            try
            {
                File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            Notify(folder);
            return true;
        }, cancellationToken);

    public Task<bool> ResetAsync(string folder, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var ini = Path.Combine(folder, "desktop.ini");
            if (!File.Exists(ini))
                return true;
            var text = Read(ini);
            text = IniText.SetValue(text, Section, "IconResource", null);
            text = IniText.SetValue(text, Section, "IconFile", null);
            text = IniText.SetValue(text, Section, "IconIndex", null);
            try
            {
                if (IniText.HasNoKeys(text))
                {
                    File.SetAttributes(ini, FileAttributes.Normal);
                    File.Delete(ini);
                    var attributes = File.GetAttributes(folder);
                    if ((attributes & FileAttributes.System) == 0)
                        File.SetAttributes(folder, attributes & ~FileAttributes.ReadOnly);
                }
                else if (!Write(ini, text))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            Notify(folder);
            return true;
        }, cancellationToken);

    private static string Read(string ini)
    {
        try
        {
            return File.ReadAllText(ini); // detects the UTF-16 byte order mark Explorer writes
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool Write(string ini, string text)
    {
        try
        {
            if (File.Exists(ini))
                File.SetAttributes(ini, FileAttributes.Normal);
            File.WriteAllText(ini, text, Encoding.Unicode);
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Notify(string folder) => SHChangeNotify(ShcneUpdateItem, ShcnfPathW, folder, 0);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(uint eventId, uint flags, string item1, nint item2);
}

/// <summary>
/// Linux: KDE reads Icon= from the folder's .directory file; GNOME/GTK file managers (Nautilus, Nemo, Caja, Thunar)
/// read the GIO metadata attribute metadata::custom-icon, set with the gio tool.
/// </summary>
internal sealed class LinuxFolderIconService : IFolderIconService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly bool _kde = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Contains("KDE", StringComparison.OrdinalIgnoreCase);
    private readonly string? _gio = ProcessRunner.FindOnPath("gio");

    public bool IsSupported => _kde || _gio is not null;

    public IReadOnlyList<string> IconExtensions { get; } = [".png", ".svg", ".svgz", ".xpm", ".ico", ".jpg", ".jpeg"];

    public bool SupportsIconIndex => false;

    public async Task<string?> GetCustomIconAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (_kde)
        {
            var directory = Path.Combine(folder, ".directory");
            return File.Exists(directory) ? IniText.GetValue(await File.ReadAllTextAsync(directory, cancellationToken).ConfigureAwait(false), "Desktop Entry", "Icon") : null;
        }

        if (_gio is null)
            return null;
        var output = await ProcessRunner.RunForOutputAsync(_gio, ["info", "-a", "metadata::custom-icon", folder], Timeout, cancellationToken).ConfigureAwait(false);
        var line = output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("metadata::custom-icon:", StringComparison.Ordinal));
        if (line is null)
            return null;
        var value = line["metadata::custom-icon:".Length..].Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : value;
    }

    public async Task<bool> SetIconAsync(string folder, string iconPath, int index, CancellationToken cancellationToken = default)
    {
        if (_kde)
            return await EditDirectoryFileAsync(folder, iconPath, cancellationToken).ConfigureAwait(false);
        if (_gio is null)
            return false;
        var result = await ProcessRunner.RunCapturingAsync(_gio, ["set", "-t", "string", folder, "metadata::custom-icon", new Uri(iconPath).AbsoluteUri], Timeout, cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    public async Task<bool> ResetAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (_kde)
            return await EditDirectoryFileAsync(folder, null, cancellationToken).ConfigureAwait(false);
        if (_gio is null)
            return false;
        var result = await ProcessRunner.RunCapturingAsync(_gio, ["set", "-t", "unset", folder, "metadata::custom-icon"], Timeout, cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    private static async Task<bool> EditDirectoryFileAsync(string folder, string? icon, CancellationToken cancellationToken)
    {
        var file = Path.Combine(folder, ".directory");
        try
        {
            var text = File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : string.Empty;
            text = IniText.SetValue(text, "Desktop Entry", "Icon", icon);
            if (icon is null && IniText.HasNoKeys(text))
                File.Delete(file);
            else
                await File.WriteAllTextAsync(file, text, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>macOS: NSWorkspace setIcon:forFile:options: (Finder stores the picture in the folder's "Icon\r" file).</summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacFolderIconService : IFolderIconService
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public bool IsSupported => true;

    public IReadOnlyList<string> IconExtensions { get; } = [".icns", ".png", ".jpg", ".jpeg", ".tiff", ".heic", ".ico"];

    public bool SupportsIconIndex => false;

    public Task<string?> GetCustomIconAsync(string folder, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(Path.Combine(folder, "Icon\r")) ? string.Empty : null);

    public Task<bool> SetIconAsync(string folder, string iconPath, int index, CancellationToken cancellationToken = default)
        => Task.Run(() => SetIcon(folder, iconPath), cancellationToken);

    public Task<bool> ResetAsync(string folder, CancellationToken cancellationToken = default)
        => Task.Run(() => SetIcon(folder, null), cancellationToken);

    private static bool SetIcon(string folder, string? iconPath)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var image = IntPtr.Zero;
            if (iconPath is not null)
            {
                var allocated = Send(objc_getClass("NSImage"), sel_registerName("alloc"));
                image = Send(allocated, sel_registerName("initWithContentsOfFile:"), Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), iconPath));
                if (image == IntPtr.Zero)
                    return false;
            }

            try
            {
                var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                var path = Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), folder);
                return SendSetIcon(workspace, sel_registerName("setIcon:forFile:options:"), image, path, 0) != 0;
            }
            finally
            {
                if (image != IntPtr.Zero)
                    _ = Send(image, sel_registerName("release"));
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC)]
    private static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(IntPtr pool);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector, string argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendSetIcon(IntPtr receiver, IntPtr selector, IntPtr image, IntPtr path, nuint options);
}
