using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Navigator.Core.IO;
using Navigator.Core.Platform;

namespace Navigator.Platform.Trash;

/// <summary>
/// Windows Recycle Bin.
/// Recycling goes through the shell (SHFileOperation with FOF_ALLOWUNDO) so Explorer sees the items.
/// FOF_WANTNUKEWARNING is always set: if an item cannot be recycled (too large, network share) the shell
/// asks before deleting permanently instead of silently destroying it.
/// Listing/restoring reads the $Recycle.Bin\&lt;SID&gt;\$I* metadata files (formats v1 and v2) on every fixed drive.
/// <see cref="Changed"/> watches those same folders (one <see cref="FileSystemWatcher"/> per drive, started lazily).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRecycleBin : ITrashService, IDisposable
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;
    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    private readonly Lock _watchGate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private EventHandler? _changed;
    private bool _watching;
    private bool _disposed;

    public bool IsSupported => true;

    public bool CanBrowse => true;

    public event EventHandler? Changed
    {
        add
        {
            lock (_watchGate)
            {
                _changed += value;
                if (_watching || _disposed)
                    return;
                _watching = true;
            }

            // Drive probing (IsReady) can stall on removable media: never on the subscriber's (UI) thread.
            _ = Task.Run(StartWatchers);
        }
        remove
        {
            lock (_watchGate)
                _changed -= value;
        }
    }

    /// <summary>Stops at the first $I metadata file found, so the cost does not grow with the size of the bin.</summary>
    public Task<bool?> HasItemsAsync(CancellationToken cancellationToken = default)
        => Task.Run<bool?>(() =>
        {
            foreach (var bin in UserBinFolders())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.EnumerateFiles(bin, "$I*").Any())
                        return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable bin counts as empty, matching what ListAsync shows.
                }
            }

            return false;
        }, cancellationToken);

    public void Dispose()
    {
        lock (_watchGate)
        {
            _disposed = true;
            foreach (var watcher in _watchers)
                watcher.Dispose();
            _watchers.Clear();
            _changed = null;
        }
    }

    private void StartWatchers()
    {
        foreach (var bin in UserBinFolders())
        {
            FileSystemWatcher watcher;
            try
            {
                // Only names matter (an item is added or removed); a small buffer is enough, and overflow is just one more ping.
                watcher = new FileSystemWatcher(bin)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 4096,
                };
                watcher.Created += OnBinChanged;
                watcher.Deleted += OnBinChanged;
                watcher.Renamed += OnBinChanged;
                watcher.Error += OnBinError;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            lock (_watchGate)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }

                _watchers.Add(watcher);
            }
        }
    }

    private void OnBinChanged(object sender, FileSystemEventArgs e) => Volatile.Read(ref _changed)?.Invoke(this, EventArgs.Empty);

    private void OnBinError(object sender, ErrorEventArgs e) => Volatile.Read(ref _changed)?.Invoke(this, EventArgs.Empty);

    /// <summary>The current user's existing $Recycle.Bin\&lt;SID&gt; folders on ready fixed and removable drives.</summary>
    private static IEnumerable<string> UserBinFolders()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
            yield break;

        foreach (var drive in DriveInfo.GetDrives())
        {
            string bin;
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;
                bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            }
            catch (IOException)
            {
                continue;
            }

            if (Directory.Exists(bin))
                yield return bin;
        }
    }

    public Task<TrashItem?> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run<TrashItem?>(() =>
        {
            path = PathUtil.Normalize(path);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException($"'{path}' does not exist.", path);

            var started = DateTime.UtcNow.AddSeconds(-2);
            var from = Marshal.StringToHGlobalUni(path + "\0\0");
            try
            {
                var operation = new ShFileOpStruct
                {
                    Func = FoDelete,
                    From = from,
                    Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
                };

                var result = SHFileOperationW(ref operation);
                if (result != 0)
                    throw new IOException($"The shell could not recycle '{path}' (error 0x{result:X}).");
                if (operation.AnyOperationsAborted != 0)
                    throw new OperationCanceledException("Recycling was cancelled.");
            }
            finally
            {
                Marshal.FreeHGlobal(from);
            }

            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"'{path}' could not be moved to the Recycle Bin.");

            // Find the entry the shell just created (for undo).
            return EnumerateItems(Path.GetPathRoot(path))
                .Where(i => PathUtil.AreEqual(i.OriginalPath, path) && i.DeletedAt >= started)
                .OrderByDescending(i => i.DeletedAt)
                .FirstOrDefault();
        }, cancellationToken);

    public Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<TrashItem>>(() =>
        {
            var items = new List<TrashItem>();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady))
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.AddRange(EnumerateItems(drive.RootDirectory.FullName));
            }

            return items;
        }, cancellationToken);

    private static IEnumerable<TrashItem> EnumerateItems(string? driveRoot)
    {
        if (driveRoot is null)
            yield break;

        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
            yield break;

        var bin = Path.Combine(driveRoot, "$Recycle.Bin", sid);
        if (!Directory.Exists(bin))
            yield break;

        string[] infoFiles;
        try
        {
            infoFiles = Directory.GetFiles(bin, "$I*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var infoFile in infoFiles)
        {
            if (!RecycleBinInfoFile.TryRead(infoFile, out var originalPath, out var length, out var deletedAt))
                continue;

            var storage = Path.Combine(bin, "$R" + Path.GetFileName(infoFile)[2..]);
            var isDirectory = Directory.Exists(storage);
            if (!isDirectory && !File.Exists(storage))
                continue;

            yield return new TrashItem(infoFile, Path.GetFileName(originalPath), originalPath, deletedAt, isDirectory ? -1 : length, isDirectory, storage);
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
            if (item.IsDirectory)
                Directory.Delete(item.StoragePath, recursive: true);
            else
                File.Delete(item.StoragePath);
            File.Delete(item.Id);
        }, cancellationToken);

    public Task EmptyAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var result = SHEmptyRecycleBinW(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
            // S_OK, or E_UNEXPECTED when the bin was already empty.
            if (result != 0 && result != unchecked((int)0x8000FFFF))
                throw new IOException($"Emptying the Recycle Bin failed (0x{result:X}).");
        }, cancellationToken);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Window;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref ShFileOpStruct operation);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(IntPtr window, string? rootPath, uint flags);
}

/// <summary>Parser for Recycle Bin $I metadata files (pure functions, usable on any OS for tests).</summary>
internal static class RecycleBinInfoFile
{
    /// <summary>
    /// $I file: Int64 version; Int64 size; Int64 FILETIME; then v1: 260 UTF-16 chars, v2: Int32 length + UTF-16 chars.
    /// </summary>
    internal static bool TryRead(string infoFile, out string originalPath, out long length, out DateTimeOffset deletedAt)
    {
        originalPath = string.Empty;
        length = 0;
        deletedAt = default;
        try
        {
            return TryRead(File.ReadAllBytes(infoFile), out originalPath, out length, out deletedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryRead(ReadOnlySpan<byte> data, out string originalPath, out long length, out DateTimeOffset deletedAt)
    {
        originalPath = string.Empty;
        length = 0;
        deletedAt = default;
        if (data.Length < 24)
            return false;

        var version = BitConverter.ToInt64(data[..8]);
        length = BitConverter.ToInt64(data[8..16]);
        var fileTime = BitConverter.ToInt64(data[16..24]);
        try
        {
            deletedAt = DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            deletedAt = DateTimeOffset.MinValue;
        }

        ReadOnlySpan<byte> text;
        if (version == 1)
        {
            if (data.Length < 24 + 520)
                return false;
            text = data.Slice(24, 520);
        }
        else if (version == 2)
        {
            if (data.Length < 28)
                return false;
            var chars = BitConverter.ToInt32(data[24..28]);
            if (chars <= 0 || data.Length < 28 + (chars * 2))
                return false;
            text = data.Slice(28, chars * 2);
        }
        else
        {
            return false;
        }

        originalPath = Encoding.Unicode.GetString(text).TrimEnd('\0');
        var nul = originalPath.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
            originalPath = originalPath[..nul];
        return originalPath.Length > 0;
    }
}
