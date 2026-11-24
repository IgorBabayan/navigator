using System.Runtime.Versioning;
using System.Security.Principal;
using Navigator.Core.Platform;

namespace Navigator.Platform;

/// <summary>
/// Desktop notifications through the standard command-line clients (libnotify's notify-send on Linux,
/// AppleScript on macOS). On Windows the in-app notification surface is used (see docs/PLATFORM_SUPPORT.md).
/// </summary>
public sealed class NativeNotificationService : INativeNotificationService
{
    private readonly string? _notifySend = OperatingSystem.IsLinux() ? ProcessRunner.FindOnPath("notify-send") : null;

    public bool IsSupported => _notifySend is not null || OperatingSystem.IsMacOS();

    public Task ShowAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        if (_notifySend is not null)
        {
            ProcessRunner.Start(_notifySend, ["--app-name=Navigator", "--icon=io.github.igorbabayan.Navigator", title, message]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Values are passed as argv to the script, never interpolated into AppleScript source.
            ProcessRunner.Start("/usr/bin/osascript",
            [
                "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run",
                title, message,
            ]);
        }

        return Task.CompletedTask;
    }
}

public sealed class FileOwnershipProvider : IFileOwnershipProvider
{
    public (string? Owner, string? Group) GetOwnership(string path)
    {
        if (OperatingSystem.IsWindows())
            return (GetWindowsOwner(path), null);

        // There is no portable .NET API for uid/gid; stat(1) is only invoked when Properties is opened.
        var arguments = OperatingSystem.IsMacOS() ? new[] { "-f", "%Su\n%Sg", path } : ["-c", "%U\n%G", path];
        var output = ProcessRunner.RunForOutputAsync("stat", arguments, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
        if (output is null)
            return (null, null);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (lines.ElementAtOrDefault(0), lines.ElementAtOrDefault(1));
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsOwner(string path)
    {
        try
        {
            System.Security.AccessControl.FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            return security.GetOwner(typeof(NTAccount))?.Value;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or IdentityNotMappedException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
