using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.IO;
using Navigator.Core.Platform;

namespace Navigator.Platform.Trash;

/// <summary>
/// macOS Trash through NSFileManager.trashItemAtURL:resultingItemURL:error: (the API Finder itself uses,
/// so trashed items appear in the Dock's Trash and support "Put Back" for items trashed by Finder).
/// Restore is available for items trashed by Navigator (the resulting URL is returned by the API);
/// browsing ~/.Trash requires Full Disk Access on recent macOS versions and degrades gracefully without it.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacTrash : ITrashService
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public bool IsSupported => true;

    public bool CanBrowse => true;

    public Task<TrashItem?> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run<TrashItem?>(() =>
        {
            path = PathUtil.Normalize(path);
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
                throw new FileNotFoundException($"'{path}' does not exist.", path);

            var length = isDirectory ? -1 : new FileInfo(path).Length;
            var pool = objc_autoreleasePoolPush();
            try
            {
                var fileManager = Send(Class("NSFileManager"), Selector("defaultManager"));
                var nsPath = Send(Class("NSString"), Selector("stringWithUTF8String:"), path);
                var url = Send(Class("NSURL"), Selector("fileURLWithPath:"), nsPath);

                var ok = SendTrash(fileManager, Selector("trashItemAtURL:resultingItemURL:error:"), url, out var resultingUrl, out var error);
                if (ok == 0)
                {
                    var message = error != IntPtr.Zero ? ReadNSString(Send(error, Selector("localizedDescription"))) : null;
                    throw new IOException(message ?? $"'{path}' could not be moved to the Trash.");
                }

                var storage = resultingUrl != IntPtr.Zero ? ReadNSString(Send(resultingUrl, Selector("path"))) : null;
                return new TrashItem(storage ?? path, Path.GetFileName(path), path, DateTimeOffset.Now, length, isDirectory, storage ?? string.Empty);
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }, cancellationToken);

    public Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<TrashItem>>(() =>
        {
            var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
            var items = new List<TrashItem>();
            try
            {
                foreach (var info in new DirectoryInfo(trash).EnumerateFileSystemInfos())
                {
                    if (info.Name == ".DS_Store")
                        continue;
                    var isDirectory = info is DirectoryInfo;
                    // The original location lives in Finder's private metadata; it is unknown here.
                    items.Add(new TrashItem(info.FullName, info.Name, string.Empty, null, isDirectory ? -1 : ((FileInfo)info).Length, isDirectory, info.FullName));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without Full Disk Access the folder is not readable: show an empty list with an explanation in the UI.
            }

            return items;
        }, cancellationToken);

    /// <summary>Stops at the first entry ListAsync would show, so the cost does not grow with the size of the trash.</summary>
    public Task<bool?> HasItemsAsync(CancellationToken cancellationToken = default)
        => Task.Run<bool?>(() =>
        {
            var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
            try
            {
                foreach (var name in Directory.EnumerateFileSystemEntries(trash))
                {
                    if (!name.EndsWith("/.DS_Store", StringComparison.Ordinal))
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without Full Disk Access the folder is not readable: ListAsync shows it as empty too.
            }

            return false;
        }, cancellationToken);

    public Task RestoreAsync(TrashItem item, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(item.OriginalPath))
                throw new IOException("The original location of this item is not known. Use Finder's \"Put Back\".");
            if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
                throw new IOException($"'{item.OriginalPath}' already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
            if (item.IsDirectory)
                Directory.Move(item.StoragePath, item.OriginalPath);
            else
                File.Move(item.StoragePath, item.OriginalPath);
        }, cancellationToken);

    public Task DeletePermanentlyAsync(TrashItem item, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (item.IsDirectory)
                Directory.Delete(item.StoragePath, recursive: true);
            else
                File.Delete(item.StoragePath);
        }, cancellationToken);

    public async Task EmptyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var item in await ListAsync(cancellationToken).ConfigureAwait(false))
            await DeletePermanentlyAsync(item, cancellationToken).ConfigureAwait(false);
    }

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Selector(string name) => sel_registerName(name);

    private static string? ReadNSString(IntPtr nsString)
        => nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Selector("UTF8String")));

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
    private static partial byte SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, out IntPtr resultingUrl, out IntPtr error);
}
