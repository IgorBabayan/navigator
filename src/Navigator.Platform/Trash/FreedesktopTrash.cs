using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Platform.Native;

namespace Navigator.Platform.Trash;

/// <summary>
/// Implementation of the freedesktop.org Trash specification 1.0
/// (https://specifications.freedesktop.org/trash-spec/latest/), interoperable with GNOME Files,
/// Dolphin, Thunar and command-line tools such as gio/trash-cli.
/// <list type="bullet">
/// <item>Items on the home volume go to $XDG_DATA_HOME/Trash.</item>
/// <item>Items on other volumes go to $topdir/.Trash/$uid (if the admin-created, sticky, non-link .Trash exists)
/// or $topdir/.Trash-$uid — never copied across volumes.</item>
/// <item>The .trashinfo file is created atomically (O_EXCL) before the item is renamed into files/.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
public sealed class FreedesktopTrash : ITrashService
{
    private const string InfoExtension = ".trashinfo";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _homeTrash;
    private readonly uint _uid;
    private readonly IVolumeLocator _volumes;
    private readonly Func<IEnumerable<string>> _mountRoots;

    public FreedesktopTrash()
        : this(DefaultHomeTrash(), UnixNative.GetUserId(), new VolumeLocator(), DefaultMountRoots)
    {
    }

    internal FreedesktopTrash(string homeTrash, uint uid, IVolumeLocator volumes, Func<IEnumerable<string>> mountRoots)
    {
        _homeTrash = homeTrash;
        _uid = uid;
        _volumes = volumes;
        _mountRoots = mountRoots;
    }

    public bool IsSupported => true;

    public bool CanBrowse => true;

    public Task<TrashItem?> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run<TrashItem?>(() => MoveToTrash(path), cancellationToken);

    private TrashItem MoveToTrash(string path)
    {
        path = PathUtil.Normalize(path);
        var isDirectory = Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null;
        var isLink = new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
        if (!isDirectory && !isLink && !File.Exists(path))
            throw new FileNotFoundException($"'{path}' does not exist.", path);

        if (PathUtil.IsSameOrDescendant(path, _homeTrash))
            throw new IOException("The trash folder itself cannot be moved to the trash.");

        var (trashRoot, topDirectory) = SelectTrash(path);
        var filesDirectory = Path.Combine(trashRoot, "files");
        var infoDirectory = Path.Combine(trashRoot, "info");
        Directory.CreateDirectory(filesDirectory, PrivateDirectory);
        Directory.CreateDirectory(infoDirectory, PrivateDirectory);

        var originalName = Path.GetFileName(path);
        var deletedAt = DateTime.Now;
        var recordedPath = topDirectory is null ? path : Path.GetRelativePath(topDirectory, path);
        var content = TrashInfoFile.Build(recordedPath, deletedAt);

        for (var attempt = 1; attempt < 10_000; attempt++)
        {
            var candidate = attempt == 1 ? originalName : MakeCandidate(originalName, attempt, isDirectory);
            var infoPath = Path.Combine(infoDirectory, candidate + InfoExtension);
            var storagePath = Path.Combine(filesDirectory, candidate);
            if (File.Exists(storagePath) || Directory.Exists(storagePath))
                continue;

            try
            {
                using var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                continue; // another process reserved this name first
            }

            try
            {
                if (isDirectory)
                    Directory.Move(path, storagePath);
                else
                    File.Move(path, storagePath);
            }
            catch
            {
                File.Delete(infoPath);
                throw;
            }

            var length = isDirectory || isLink ? -1 : new FileInfo(storagePath).Length;
            return new TrashItem(infoPath, originalName, path, new DateTimeOffset(deletedAt), length, isDirectory, storagePath);
        }

        throw new IOException($"Could not find a free name for '{originalName}' in the trash.");
    }

    private static string MakeCandidate(string name, int counter, bool isDirectory)
    {
        var (stem, extension) = PathUtil.SplitName(name, isDirectory);
        return $"{stem}.{counter.ToString(CultureInfo.InvariantCulture)}{extension}";
    }

    /// <summary>Returns the trash directory for <paramref name="path"/> and, for volume trashes, the volume's top directory.</summary>
    private (string TrashRoot, string? TopDirectory) SelectTrash(string path)
    {
        var homeAnchor = NearestExistingAncestor(_homeTrash);
        if (_volumes.AreOnSameVolume(path, homeAnchor))
            return (_homeTrash, null);

        var top = _volumes.GetVolumeRoot(path);
        var adminTrash = Path.Combine(top, ".Trash");
        var adminInfo = new DirectoryInfo(adminTrash);
        if (adminInfo.Exists && adminInfo.LinkTarget is null && (adminInfo.UnixFileMode & UnixFileMode.StickyBit) != 0)
        {
            var userTrash = Path.Combine(adminTrash, _uid.ToString(CultureInfo.InvariantCulture));
            try
            {
                Directory.CreateDirectory(userTrash, PrivateDirectory);
                return (userTrash, top);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Spec: fall back to $topdir/.Trash-$uid.
            }
        }

        var fallback = Path.Combine(top, ".Trash-" + _uid.ToString(CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(fallback, PrivateDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"This volume ({top}) has no usable trash. The item was not deleted.", ex);
        }

        return (fallback, top);
    }

    private static string NearestExistingAncestor(string path)
    {
        var current = path;
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
                return "/";
            current = parent;
        }

        return current;
    }

    public Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<TrashItem>>(() =>
        {
            var items = new List<TrashItem>();
            foreach (var (root, top) in EnumerateTrashDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadTrash(root, top, items);
            }

            return items;
        }, cancellationToken);

    private IEnumerable<(string Root, string? Top)> EnumerateTrashDirectories()
    {
        if (Directory.Exists(_homeTrash))
            yield return (_homeTrash, null);

        var uid = _uid.ToString(CultureInfo.InvariantCulture);
        foreach (var top in _mountRoots())
        {
            var admin = Path.Combine(top, ".Trash", uid);
            if (Directory.Exists(admin) && !PathUtil.AreEqual(admin, _homeTrash))
                yield return (admin, top);
            var user = Path.Combine(top, ".Trash-" + uid);
            if (Directory.Exists(user) && !PathUtil.AreEqual(user, _homeTrash))
                yield return (user, top);
        }
    }

    private static void ReadTrash(string root, string? top, List<TrashItem> items)
    {
        var infoDirectory = Path.Combine(root, "info");
        var filesDirectory = Path.Combine(root, "files");
        if (!Directory.Exists(infoDirectory))
            return;

        IEnumerable<string> infoFiles;
        try
        {
            infoFiles = Directory.EnumerateFiles(infoDirectory, "*" + InfoExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var infoPath in infoFiles)
        {
            var storedName = Path.GetFileName(infoPath)[..^InfoExtension.Length];
            var storagePath = Path.Combine(filesDirectory, storedName);
            var isDirectory = Directory.Exists(storagePath);
            if (!isDirectory && !File.Exists(storagePath) && new FileInfo(storagePath).LinkTarget is null)
                continue;

            if (!TrashInfoFile.TryParse(infoPath, out var recordedPath, out var deletedAt))
                continue;

            var original = Path.IsPathRooted(recordedPath) ? recordedPath : Path.Combine(top ?? "/", recordedPath);
            var length = isDirectory ? -1 : new FileInfo(storagePath) is { Exists: true } f ? f.Length : 0;
            items.Add(new TrashItem(infoPath, Path.GetFileName(original), original, deletedAt, length, isDirectory, storagePath));
        }
    }

    public Task RestoreAsync(TrashItem item, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
                throw new IOException($"'{item.OriginalPath}' already exists. Rename or move it before restoring.");

            Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
            if (item.IsDirectory)
                Directory.Move(item.StoragePath, item.OriginalPath);
            else
                File.Move(item.StoragePath, item.OriginalPath);

            File.Delete(item.Id);
        }, cancellationToken);

    public Task DeletePermanentlyAsync(TrashItem item, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            DeleteStorage(item.StoragePath);
            File.Delete(item.Id);
        }, cancellationToken);

    public async Task EmptyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var item in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeletePermanentlyAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void DeleteStorage(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            if (directory.LinkTarget is not null)
                directory.Delete(recursive: false);
            else
                directory.Delete(recursive: true);
            return;
        }

        File.Delete(path);
    }

    private static string DefaultHomeTrash()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "Trash");
    }

    private static IEnumerable<string> DefaultMountRoots()
    {
        try
        {
            return [.. DriveInfo.GetDrives().Select(d => PathUtil.TrimTrailingSeparator(d.RootDirectory.FullName))
                .Where(r => r == "/" || r.StartsWith("/media/", StringComparison.Ordinal) || r.StartsWith("/run/media/", StringComparison.Ordinal) || r.StartsWith("/mnt/", StringComparison.Ordinal) || r.StartsWith("/home", StringComparison.Ordinal))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Reads and writes .trashinfo files (pure functions, usable on any OS for tests and tooling).</summary>
internal static class TrashInfoFile
{
    internal static string Build(string recordedPath, DateTime deletedAtLocal)
    {
        var escaped = string.Join('/', recordedPath.Split('/').Select(Uri.EscapeDataString));
        return "[Trash Info]\n"
               + "Path=" + escaped + "\n"
               + "DeletionDate=" + deletedAtLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "\n";
    }

    internal static bool TryParse(string infoPath, out string originalPath, out DateTimeOffset? deletedAt)
    {
        originalPath = string.Empty;
        deletedAt = null;
        try
        {
            return TryParse(File.ReadAllLines(infoPath), out originalPath, out deletedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryParse(IEnumerable<string> lines, out string originalPath, out DateTimeOffset? deletedAt)
    {
        originalPath = string.Empty;
        deletedAt = null;
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line == "[Trash Info]";
                continue;
            }

            if (!inSection)
                continue;

            if (line.StartsWith("Path=", StringComparison.Ordinal))
            {
                originalPath = Uri.UnescapeDataString(line[5..]);
            }
            else if (line.StartsWith("DeletionDate=", StringComparison.Ordinal)
                     && DateTime.TryParseExact(line[13..], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
            {
                deletedAt = new DateTimeOffset(date);
            }
        }

        return originalPath.Length > 0;
    }
}
