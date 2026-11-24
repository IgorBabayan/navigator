using Navigator.Core.IO;
using Navigator.Core.Platform;

namespace Navigator.Platform;

/// <summary>
/// Drives and mounted volumes. On Linux system/virtual mounts (proc, sysfs, snaps, containers…) are hidden.
/// Change detection is event-based where the OS offers it cheaply (mount folders via file system notifications);
/// otherwise a tiny, fast check runs every few seconds only while someone is subscribed
/// (GetLogicalDrives bitmask on Windows, /proc/self/mounts on Linux — neither touches a disk).
/// </summary>
public sealed class VolumeProvider : IVolumeProvider, IDisposable
{
    private static readonly string[] HiddenLinuxPrefixes =
    [
        "/proc", "/sys", "/dev", "/run", "/snap", "/boot", "/var/lib/docker", "/var/lib/containers",
        "/var/snap", "/tmp", "/efi", "/usr", "/opt", "/var", "/etc", "/srv",
    ];

    private static readonly HashSet<string> NetworkFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfs", "nfs4", "cifs", "smb3", "smbfs", "fuse.sshfs", "sshfs", "afpfs", "webdav", "davfs", "fuse.rclone", "9p",
    };

    private static readonly HashSet<string> VirtualFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "securityfs", "pstore", "debugfs", "tracefs",
        "configfs", "fusectl", "mqueue", "hugetlbfs", "bpf", "autofs", "overlay", "squashfs", "nsfs", "binfmt_misc",
        "efivarfs", "ramfs", "rpc_pipefs", "fuse.portal", "fuse.gvfsd-fuse", "zram",
    };

    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private EventHandler? _volumesChanged;
    private Timer? _timer;
    private string _lastFingerprint = string.Empty;

    public event EventHandler? VolumesChanged
    {
        add
        {
            lock (_gate)
            {
                _volumesChanged += value;
                StartMonitoring();
            }
        }

        remove
        {
            lock (_gate)
            {
                _volumesChanged -= value;
                if (_volumesChanged is null)
                    StopMonitoring();
            }
        }
    }

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var result = new List<VolumeInfo>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var drive in drives)
        {
            var root = drive.RootDirectory.FullName;
            string format;
            try
            {
                format = drive.DriveFormat;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                format = string.Empty;
            }

            if (!ShouldShow(root, drive.DriveType, format))
                continue;

            var kind = Classify(drive.DriveType, format, root);
            bool ready;
            long total = 0, free = 0;
            string label = string.Empty;
            try
            {
                ready = drive.IsReady;
                if (ready)
                {
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                    label = OperatingSystem.IsWindows() ? drive.VolumeLabel : string.Empty;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ready = false;
            }

            if (string.IsNullOrEmpty(label))
                label = DefaultLabel(root);

            result.Add(new VolumeInfo(PathUtil.TrimTrailingSeparator(root), label, kind, format, total, free, ready));
        }

        return result;
    }

    internal static bool ShouldShow(string root, DriveType type, string format)
    {
        if (OperatingSystem.IsWindows())
            return type != DriveType.NoRootDirectory;

        var path = PathUtil.TrimTrailingSeparator(root);
        if (path == "/")
            return true;

        if (OperatingSystem.IsMacOS())
            return path.StartsWith("/Volumes/", StringComparison.Ordinal);

        if (path.StartsWith("/run/media/", StringComparison.Ordinal) || path.StartsWith("/media/", StringComparison.Ordinal) || path.StartsWith("/mnt/", StringComparison.Ordinal))
            return !VirtualFileSystems.Contains(format);

        if (NetworkFileSystems.Contains(format))
            return !HiddenLinuxPrefixes.Any(p => PathUtil.IsSameOrDescendant(p, path));

        if (VirtualFileSystems.Contains(format) || type == DriveType.Ram)
            return false;

        // Separate partitions such as /home or /data are useful; system ones are not.
        return !HiddenLinuxPrefixes.Any(p => PathUtil.IsSameOrDescendant(p, path));
    }

    private static VolumeKind Classify(DriveType type, string format, string root)
    {
        if (NetworkFileSystems.Contains(format) || type == DriveType.Network)
            return VolumeKind.Network;

        return type switch
        {
            DriveType.Removable => VolumeKind.Removable,
            DriveType.CDRom => VolumeKind.Optical,
            DriveType.Ram => VolumeKind.Ram,
            _ when root.StartsWith("/run/media/", StringComparison.Ordinal) || root.StartsWith("/media/", StringComparison.Ordinal) => VolumeKind.Removable,
            _ when OperatingSystem.IsMacOS() && root.StartsWith("/Volumes/", StringComparison.Ordinal) => VolumeKind.Removable,
            DriveType.Fixed => VolumeKind.Fixed,
            _ => VolumeKind.Unknown,
        };
    }

    private static string DefaultLabel(string root)
    {
        var trimmed = PathUtil.TrimTrailingSeparator(root);
        if (OperatingSystem.IsWindows())
            return string.Empty;
        return trimmed == "/" ? string.Empty : Path.GetFileName(trimmed);
    }

    private void StartMonitoring()
    {
        if (_timer is not null || _watchers.Count > 0)
            return;

        _lastFingerprint = Fingerprint();
        var watched = OperatingSystem.IsMacOS()
            ? new[] { "/Volumes" }
            : OperatingSystem.IsLinux()
                ? new[] { "/media", "/mnt", $"/run/media/{Environment.UserName}" }
                : [];

        foreach (var folder in watched.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.DirectoryName, IncludeSubdirectories = false };
                watcher.Created += (_, _) => CheckForChanges();
                watcher.Deleted += (_, _) => CheckForChanges();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Watching mount folders is an optimization; the periodic check below still works.
            }
        }

        if (!OperatingSystem.IsMacOS())
            _timer = new Timer(_ => CheckForChanges(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void StopMonitoring()
    {
        _timer?.Dispose();
        _timer = null;
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    private void CheckForChanges()
    {
        var fingerprint = Fingerprint();
        EventHandler? handler;
        lock (_gate)
        {
            if (fingerprint == _lastFingerprint)
                return;
            _lastFingerprint = fingerprint;
            handler = _volumesChanged;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }

    private static string Fingerprint()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return string.Join('|', Environment.GetLogicalDrives());
            if (OperatingSystem.IsLinux() && File.Exists("/proc/self/mounts"))
                return File.ReadAllText("/proc/self/mounts");
            return string.Join('|', DriveInfo.GetDrives().Select(d => d.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _volumesChanged = null;
            StopMonitoring();
        }
    }
}
