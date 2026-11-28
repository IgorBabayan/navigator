using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Navigator.Core.Platform;

namespace Navigator.Platform.ItemActions;

/// <summary>
/// Windows: images are mounted with the Storage module's Mount-DiskImage (ISO/IMG as the user; VHD/VHDX need
/// administrator rights, so they go through a UAC prompt); ejecting runs the shell's own "Eject" verb (USB media,
/// optical drives, mounted ISOs), or Dismount-DiskImage for VHDs mounted from Navigator. Network drives use the
/// system's "Map Network Drive" dialog (WNetConnectionDialog) and WNetCancelConnection2.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDriveService : IDriveService
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(60);

    /// <summary>VHDs mounted from here (root → image): Windows lists them as fixed disks, so only these offer "Eject".</summary>
    private readonly ConcurrentDictionary<string, string> _mountedImages = new(StringComparer.OrdinalIgnoreCase);

    public bool CanMount(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".iso" or ".img" or ".vhd" or ".vhdx" && OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    public async Task<DriveActionResult> MountAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(imagePath);
        var isVirtualDisk = Path.GetExtension(path).ToLowerInvariant() is ".vhd" or ".vhdx";
        var script = Script(path,
            "$image = Mount-DiskImage -ImagePath $path -PassThru",
            "$letter = ($image | Get-Volume | Where-Object { $_.DriveLetter } | Select-Object -First 1).DriveLetter",
            "if ($letter) { [Console]::Out.Write($letter) }");

        var result = await RunScriptAsync(script, cancellationToken).ConfigureAwait(false);
        if (result is { ExitCode: 0 } done)
            return Mounted(path, done.Output, isVirtualDisk);

        if (!isVirtualDisk)
            return DriveActionResult.Failed(result?.Error);

        // Attaching a virtual disk needs administrator rights: the same script once more behind a UAC prompt. Its output
        // cannot be read across the elevation boundary, so the new drive is found by the caller.
        var elevated = await ProcessRunner.RunWithVerbAsync("powershell.exe", PowerShellArguments(script), "runas", cancellationToken).ConfigureAwait(false);
        return elevated == 0 ? new DriveActionResult(true) : DriveActionResult.Failed();
    }

    private DriveActionResult Mounted(string imagePath, string output, bool isVirtualDisk)
    {
        var letter = output.Trim();
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
            return new DriveActionResult(true);
        var root = letter.ToUpperInvariant() + @":\";
        if (isVirtualDisk)
            _mountedImages[root] = imagePath;
        return new DriveActionResult(true, root);
    }

    public bool CanEject(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return volume.Kind is VolumeKind.Removable or VolumeKind.Optical || _mountedImages.ContainsKey(volume.RootPath);
    }

    public async Task<bool> EjectAsync(VolumeInfo volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (_mountedImages.TryGetValue(volume.RootPath, out var image))
        {
            var detach = Script(image, "Dismount-DiskImage -ImagePath $path | Out-Null");
            var ok = await RunScriptAsync(detach, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 }
                     || await ProcessRunner.RunWithVerbAsync("powershell.exe", PowerShellArguments(detach), "runas", cancellationToken).ConfigureAwait(false) == 0;
            if (ok)
                _mountedImages.TryRemove(volume.RootPath, out _);
            return ok;
        }

        // The shell verb is what Explorer runs: it flushes, locks and dismounts the volume, then ejects or detaches it.
        // It returns before the drive is gone, so the script waits (up to 5 s) for the root to disappear.
        var eject = Script(volume.RootPath,
            "$item = (New-Object -ComObject Shell.Application).Namespace(17).ParseName($path)",
            "if ($null -eq $item) { exit 2 }",
            "$item.InvokeVerb('Eject')",
            "for ($i = 0; $i -lt 25 -and (Test-Path -LiteralPath $path); $i++) { Start-Sleep -Milliseconds 200 }",
            "if (Test-Path -LiteralPath $path) { exit 3 }");
        return await RunScriptAsync(eject, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 };
    }

    public bool CanConnectNetworkDrive => true;

    public bool AsksForNetworkAddress => false;

    /// <summary>Shows the system dialog modally over <paramref name="ownerWindow"/>; call it on the UI thread.</summary>
    public Task<NetworkConnectResult> ConnectNetworkDriveAsync(string? address, NetworkCredentials? credentials, nint ownerWindow, CancellationToken cancellationToken = default)
    {
        var status = WNetConnectionDialog(ownerWindow, ResourceTypeDisk);
        return Task.FromResult(status switch
        {
            0 => NetworkConnectResult.Connected,
            uint.MaxValue => NetworkConnectResult.Cancelled,
            _ => NetworkConnectResult.Failed,
        });
    }

    /// <summary>Mapped drive letters ("Z:\"); UNC locations have nothing to disconnect.</summary>
    public bool CanDisconnect(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return volume.Kind == VolumeKind.Network && volume.RootPath.Length is 2 or 3 && volume.RootPath[1] == ':';
    }

    public Task<bool> DisconnectAsync(VolumeInfo volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var name = volume.RootPath[..2];
        return Task.Run(() => WNetCancelConnection2W(name, UpdateProfile, force: false) == 0, cancellationToken);
    }

    /// <summary>A script whose single input path travels Base64-encoded (no file name can ever be read as script text).</summary>
    private static string Script(string path, params string[] lines)
    {
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .Append("$path = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('")
            .Append(Convert.ToBase64String(Encoding.Unicode.GetBytes(path)))
            .AppendLine("'))");
        foreach (var line in lines)
            script.AppendLine(line);
        return script.ToString();
    }

    private static string[] PowerShellArguments(string script)
        => ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];

    private static Task<(int ExitCode, string Output, string Error)?> RunScriptAsync(string script, CancellationToken cancellationToken)
        => ProcessRunner.RunWithInputCapturingAsync("powershell.exe", PowerShellArguments(script), input: null, ScriptTimeout, cancellationToken);

    private const uint ResourceTypeDisk = 1;
    private const uint UpdateProfile = 1;

    [LibraryImport("mpr.dll")]
    private static partial uint WNetConnectionDialog(nint owner, uint type);

    [LibraryImport("mpr.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WNetCancelConnection2W(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);
}

/// <summary>
/// Linux through udisks2 (what desktops use, no root needed for the active session): <c>udisksctl loop-setup</c> and
/// <c>mount</c> for ISO/IMG files, <c>unmount</c> plus <c>loop-delete</c> or <c>power-off</c> for ejecting. Network
/// shares are mounted by GVfs (<c>gio mount smb://…</c>); credentials are answered on standard input, never passed
/// as arguments.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxDriveService : IDriveService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly Lazy<string?> _udisks = new(() => ProcessRunner.FindOnPath("udisksctl"));
    private readonly Lazy<string?> _gio = new(() => ProcessRunner.FindOnPath("gio"));
    private readonly Lazy<string?> _umount = new(() => ProcessRunner.FindOnPath("umount"));

    public bool CanMount(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".iso" or ".img" && _udisks.Value is not null;

    public async Task<DriveActionResult> MountAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (_udisks.Value is not { } udisks)
            return DriveActionResult.Failed();
        var setup = await ProcessRunner.RunWithInputCapturingAsync(udisks, ["loop-setup", "--no-user-interaction", "-r", "-f", Path.GetFullPath(imagePath)], null, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (setup is not { ExitCode: 0 } mapped || LoopDevice().Match(mapped.Output) is not { Success: true } device)
            return DriveActionResult.Failed(setup?.Error);

        // The desktop may already have mounted it (automount); otherwise the whole device, then its first partition.
        foreach (var candidate in new[] { device.Value, device.Value + "p1" })
        {
            if (UnixMounts.FindMountPoint(candidate) is { } already)
                return new DriveActionResult(true, already);
            if (!File.Exists(candidate) && !candidate.Equals(device.Value, StringComparison.Ordinal))
                continue;
            var mount = await ProcessRunner.RunWithInputCapturingAsync(udisks, ["mount", "--no-user-interaction", "-b", candidate], null, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (mount is { ExitCode: 0 } mounted)
            {
                var at = mounted.Output.IndexOf(" at ", StringComparison.Ordinal);
                return new DriveActionResult(true, at < 0 ? UnixMounts.FindMountPoint(candidate) : mounted.Output[(at + 4)..].Trim().TrimEnd('.'));
            }
        }

        return new DriveActionResult(true); // attached; the desktop will show it when it mounts it
    }

    public bool CanEject(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return volume.Kind is VolumeKind.Removable or VolumeKind.Optical && (_udisks.Value is not null || _gio.Value is not null);
    }

    public async Task<bool> EjectAsync(VolumeInfo volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var device = UnixMounts.FindDevice(volume.RootPath);
        if (_udisks.Value is { } udisks && device is not null)
        {
            var unmount = await ProcessRunner.RunWithInputCapturingAsync(udisks, ["unmount", "--no-user-interaction", "-b", device], null, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (unmount is not { ExitCode: 0 })
                return false;

            // Then the hardware: a loop device is released, a drive is powered off so it can be pulled out (best effort).
            var whole = UnixMounts.WholeDevice(device);
            string[] release = whole.StartsWith("/dev/loop", StringComparison.Ordinal)
                ? ["loop-delete", "--no-user-interaction", "-b", whole]
                : ["power-off", "--no-user-interaction", "-b", whole];
            await ProcessRunner.RunWithInputCapturingAsync(udisks, release, null, CommandTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_gio.Value is { } gio)
            return await ProcessRunner.RunWithInputCapturingAsync(gio, ["mount", "-e", volume.RootPath], null, CommandTimeout, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 };
        return false;
    }

    public bool CanConnectNetworkDrive => _gio.Value is not null;

    public bool AsksForNetworkAddress => true;

    public async Task<NetworkConnectResult> ConnectNetworkDriveAsync(string? address, NetworkCredentials? credentials, nint ownerWindow, CancellationToken cancellationToken = default)
    {
        if (_gio.Value is not { } gio || string.IsNullOrWhiteSpace(address))
            return NetworkConnectResult.Failed;

        // gio asks "User", then "Domain" for SMB, then "Password"; unanswered questions read end-of-input and fail.
        string? answers = null;
        if (credentials is not null)
        {
            var smb = address.StartsWith("smb:", StringComparison.OrdinalIgnoreCase);
            answers = smb
                ? $"{credentials.UserName}\n{credentials.Domain ?? string.Empty}\n{credentials.Password}\n"
                : $"{credentials.UserName}\n{credentials.Password}\n";
        }

        var result = await ProcessRunner.RunWithInputCapturingAsync(gio, ["mount", "--", address.Trim()], answers, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result is { ExitCode: 0 })
            return NetworkConnectResult.Connected;
        var text = (result?.Output ?? string.Empty) + (result?.Error ?? string.Empty);
        return credentials is null && (text.Contains("assword", StringComparison.Ordinal) || text.Contains("uthentication", StringComparison.Ordinal))
            ? NetworkConnectResult.NeedsCredentials
            : NetworkConnectResult.Failed;
    }

    public bool CanDisconnect(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return volume.Kind == VolumeKind.Network && (_gio.Value is not null || _umount.Value is not null);
    }

    public async Task<bool> DisconnectAsync(VolumeInfo volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (_gio.Value is { } gio
            && await ProcessRunner.RunWithInputCapturingAsync(gio, ["mount", "-u", volume.RootPath], null, CommandTimeout, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 })
            return true;
        return _umount.Value is { } umount
               && await ProcessRunner.RunWithInputCapturingAsync(umount, [volume.RootPath], null, CommandTimeout, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 };
    }

    [GeneratedRegex(@"/dev/loop\d+")]
    private static partial Regex LoopDevice();
}

/// <summary>Linux mount table and block device names (parsing only, no I/O beyond /proc/self/mounts).</summary>
internal static partial class UnixMounts
{
    /// <summary>"/dev/sdb1" → "/dev/sdb", "/dev/nvme0n1p2" / "/dev/loop3p1" → "/dev/nvme0n1" / "/dev/loop3".</summary>
    internal static string WholeDevice(string device)
    {
        if (PartitionSuffix().Match(device) is { Success: true } numbered)
            return device[..numbered.Index];
        return SdPartition().Match(device) is { Success: true } sd ? device[..sd.Index] : device;
    }

    /// <summary>The device mounted at <paramref name="mountPoint"/>, from /proc/self/mounts.</summary>
    internal static string? FindDevice(string mountPoint) => FindInMounts(entry => PathsEqual(entry.MountPoint, mountPoint))?.Device;

    internal static string? FindMountPoint(string device) => FindInMounts(entry => entry.Device == device)?.MountPoint;

    private static (string Device, string MountPoint)? FindInMounts(Func<(string Device, string MountPoint), bool> match)
    {
        string text;
        try
        {
            text = File.ReadAllText("/proc/self/mounts");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var entry in ParseMounts(text))
        {
            if (match(entry))
                return entry;
        }

        return null;
    }

    internal static IEnumerable<(string Device, string MountPoint)> ParseMounts(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length >= 2)
                yield return (Unescape(fields[0]), Unescape(fields[1]));
        }
    }

    /// <summary>/proc/self/mounts writes space, tab, newline and backslash as three-digit octal escapes.</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
            return field;
        var builder = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && IsOctal(field, i + 1))
            {
                builder.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(field[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctal(string text, int start)
        => start + 3 <= text.Length && text[start] is >= '0' and <= '7' && text[start + 1] is >= '0' and <= '7' && text[start + 2] is >= '0' and <= '7';

    private static bool PathsEqual(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    [GeneratedRegex(@"(?<=(?:nvme\d+n\d+|mmcblk\d+|loop\d+))p\d+$")]
    private static partial Regex PartitionSuffix();

    [GeneratedRegex(@"(?<=/dev/(?:sd|hd|vd|xvd)[a-z]+)\d+$")]
    private static partial Regex SdPartition();
}

/// <summary>
/// macOS: <c>hdiutil attach</c> for disk images, <c>diskutil eject</c> (then <c>hdiutil detach</c>) for ejecting, and the
/// system's own "Connect to Server" sign-in through AppleScript's <c>mount volume</c> for network shares.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacDriveService : IDriveService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public bool CanMount(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".iso" or ".dmg" or ".img" or ".cdr" or ".sparseimage" or ".sparsebundle";

    public async Task<DriveActionResult> MountAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunWithInputCapturingAsync("/usr/bin/hdiutil", ["attach", Path.GetFullPath(imagePath)], null, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 } attached)
            return DriveActionResult.Failed(result?.Error);

        // Each attached device is a line of tab-separated columns; mounted ones end with their "/Volumes/…" path.
        foreach (var line in attached.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = line.IndexOf("/Volumes/", StringComparison.Ordinal);
            if (at >= 0)
                return new DriveActionResult(true, line[at..].TrimEnd());
        }

        return new DriveActionResult(true);
    }

    public bool CanEject(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return volume.Kind is VolumeKind.Removable or VolumeKind.Optical;
    }

    public async Task<bool> EjectAsync(VolumeInfo volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (await ProcessRunner.RunWithInputCapturingAsync("/usr/sbin/diskutil", ["eject", volume.RootPath], null, CommandTimeout, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 })
            return true;
        return await ProcessRunner.RunWithInputCapturingAsync("/usr/bin/hdiutil", ["detach", volume.RootPath], null, CommandTimeout, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 };
    }

    public bool CanConnectNetworkDrive => true;

    public bool AsksForNetworkAddress => true;

    public async Task<NetworkConnectResult> ConnectNetworkDriveAsync(string? address, NetworkCredentials? credentials, nint ownerWindow, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return NetworkConnectResult.Failed;

        // The address is an argument of the script's run handler, never part of the script text.
        var result = await ProcessRunner.RunWithInputCapturingAsync("/usr/bin/osascript",
            ["-e", "on run argv", "-e", "mount volume (item 1 of argv)", "-e", "end run", address.Trim()],
            null, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result is { ExitCode: 0 })
            return NetworkConnectResult.Connected;
        return result?.Error.Contains("-128", StringComparison.Ordinal) == true ? NetworkConnectResult.Cancelled : NetworkConnectResult.Failed;
    }

    public bool CanDisconnect(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return volume.Kind == VolumeKind.Network;
    }

    public async Task<bool> DisconnectAsync(VolumeInfo volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return await ProcessRunner.RunWithInputCapturingAsync("/usr/sbin/diskutil", ["unmount", volume.RootPath], null, CommandTimeout, cancellationToken).ConfigureAwait(false) is { ExitCode: 0 };
    }
}
