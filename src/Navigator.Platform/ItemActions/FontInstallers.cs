using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Navigator.Core.Fonts;
using Navigator.Core.Platform;

namespace Navigator.Platform.ItemActions;

/// <summary>
/// Copies a font into the user's own font folder; nothing already installed is replaced. Windows also registers it
/// (HKCU font list, as Explorer's per-user "Install" does) and tells running applications; Linux refreshes the
/// fontconfig cache for that folder; macOS picks fonts in ~/Library/Fonts up by itself.
/// </summary>
public abstract class UserFontInstaller : IFontInstaller
{
    public bool IsSupported => true;

    /// <summary>The per-user font folder (created when missing).</summary>
    protected abstract string FontDirectory { get; }

    public async Task<FontInstallResult> InstallAsync(string fontPath, CancellationToken cancellationToken = default)
    {
        var info = await Task.Run(() => FontFileInfo.Read(fontPath), cancellationToken).ConfigureAwait(false);
        if (info is null)
            return FontInstallResult.Failed;

        var directory = FontDirectory;
        var target = Path.Combine(directory, Path.GetFileName(fontPath));
        var created = false;
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(target))
                return FontInstallResult.AlreadyInstalled;
            await using (var source = new FileStream(fontPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                created = true;
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (created)
                TryDelete(target); // never leave half a font behind
            else if (File.Exists(target))
                return FontInstallResult.AlreadyInstalled; // installed by someone else in the meantime
            if (ex is OperationCanceledException)
                throw;
            return FontInstallResult.Failed;
        }

        return await RegisterAsync(target, info, cancellationToken).ConfigureAwait(false) ? FontInstallResult.Installed : FontInstallResult.Failed;
    }

    /// <summary>Makes the copied font known to the system.</summary>
    protected abstract Task<bool> RegisterAsync(string installedPath, FontFileInfo info, CancellationToken cancellationToken);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the user to remove; reported as a failed install.
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsFontInstaller : UserFontInstaller
{
    private const string FontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const uint FontChangeMessage = 0x001D;
    private static readonly nint Broadcast = 0xFFFF;

    protected override string FontDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

    protected override Task<bool> RegisterAsync(string installedPath, FontFileInfo info, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(FontsKey, writable: true);
            key.SetValue($"{info.FullName} ({(info.IsOpenTypeCff ? "OpenType" : "TrueType")})", installedPath, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }

        // Available right away in this session; the registry entry loads it at every sign-in.
        var added = AddFontResourceW(installedPath) > 0;
        PostMessageW(Broadcast, FontChangeMessage, 0, 0);
        return added;
    }, cancellationToken);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AddFontResourceW(string fileName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}

[SupportedOSPlatform("linux")]
public sealed class LinuxFontInstaller : UserFontInstaller
{
    protected override string FontDirectory
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data) || !Path.IsPathRooted(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "fonts");
        }
    }

    protected override async Task<bool> RegisterAsync(string installedPath, FontFileInfo info, CancellationToken cancellationToken)
    {
        // Only the user folder is rescanned (seconds at most); without fontconfig the copy is still picked up at next login.
        if (ProcessRunner.FindOnPath("fc-cache") is { } cache)
            await ProcessRunner.RunAsync(cache, ["-f", Path.GetDirectoryName(installedPath)!], input: null, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return true;
    }
}

[SupportedOSPlatform("macos")]
public sealed class MacFontInstaller : UserFontInstaller
{
    protected override string FontDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts");

    protected override Task<bool> RegisterAsync(string installedPath, FontFileInfo info, CancellationToken cancellationToken) => Task.FromResult(true);
}
