using System.Security.Cryptography;
using System.Text;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Plugins;

namespace Navigator.Presentation.Services;

/// <summary>
/// File work on plugin file systems for panes: listing, opening (download to a cache, then the default app),
/// download to a local folder, upload, create, rename, delete. Local writes follow the engine's safety rules:
/// a partial file is written and renamed into place, and nothing is ever overwritten (names get " (2)").
/// </summary>
public sealed class RemoteFileService(PluginHost plugins, AppDirectories directories)
{
    private const int MaxEntriesPerFolder = 100_000;

    /// <summary>The file system serving <paramref name="address"/>; waits for installed plugins to finish loading.</summary>
    public async Task<IRemoteFileSystem> ResolveAsync(RemoteAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (plugins.TryGetFileSystem(address.Scheme, out var fileSystem))
            return fileSystem;
        await plugins.WhenLoaded.WaitAsync(cancellationToken).ConfigureAwait(false);
        return plugins.TryGetFileSystem(address.Scheme, out fileSystem)
            ? fileSystem
            : throw new NoRemoteFileSystemException(address.Scheme);
    }

    /// <summary>The local folder a plugin address stands for (e.g. a network share), or null. No I/O.</summary>
    public string? TryMapToLocal(string remote)
        => RemoteUri.TryParse(remote, out var address) && plugins.TryGetFileSystem(address.Scheme, out var fileSystem) && fileSystem is IRemoteLocationMapper mapper
            ? mapper.GetLocalPath(address)
            : null;

    /// <summary>The plugin address above a local root folder (a share's server), or null. No I/O.</summary>
    public string? TryGetRemoteParent(string localPath)
    {
        foreach (var fileSystem in plugins.FileSystems)
        {
            if (fileSystem is IRemoteLocationMapper mapper && mapper.GetParentAddress(localPath) is { } parent)
                return parent;
        }

        return null;
    }

    /// <summary>A plugin's name for an address (e.g. "Network" for the root), or null.</summary>
    public string? TryGetDisplayName(string remote)
        => RemoteUri.TryParse(remote, out var address) && plugins.TryGetFileSystem(address.Scheme, out var fileSystem) && fileSystem is IRemoteLocationMapper mapper
            ? mapper.GetDisplayName(address)
            : null;

    /// <summary>Lists a remote folder as <see cref="FileEntry"/> snapshots (full paths are remote addresses).</summary>
    public async Task<List<FileEntry>> ListAsync(string folder, bool includeHidden, CancellationToken cancellationToken)
    {
        var address = RemoteUri.Parse(folder);
        var fileSystem = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        var entries = await fileSystem.ListAsync(address, cancellationToken).ConfigureAwait(false);
        var prefix = address.IsRoot ? address.ToString() : address + "/";
        var result = new List<FileEntry>(Math.Min(entries.Count, MaxEntriesPerFolder));
        foreach (var entry in entries)
        {
            if (result.Count >= MaxEntriesPerFolder)
                break;
            var name = entry.Name;
            if (name.Length == 0 || name is "." or ".." || name.Contains('/', StringComparison.Ordinal))
                continue;
            var hidden = name[0] == '.';
            if (hidden && !includeHidden)
                continue;
            var attributes = entry.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
            if (hidden)
                attributes |= FileAttributes.Hidden;
            if (entry.IsSymbolicLink)
                attributes |= FileAttributes.ReparsePoint;
            result.Add(new FileEntry(name, prefix + name, entry.IsDirectory, entry.Length, entry.LastWriteTimeUtc, entry.LastWriteTimeUtc, attributes, isRemote: true));
        }

        return result;
    }

    /// <summary>Downloads a file into the remote-files cache (replacing an older cached copy) and returns the local path.</summary>
    public async Task<string> DownloadToCacheAsync(string file, CancellationToken cancellationToken)
    {
        var address = RemoteUri.Parse(file);
        var fileSystem = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        var parent = RemoteUri.GetParent(file) ?? file;
        var folder = Path.Combine(directories.CacheDirectory, "remote", Hash(parent));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, LocalName(RemoteUri.GetName(file)));
        var partial = await WritePartialAsync(fileSystem, address, folder, cancellationToken).ConfigureAwait(false);
        File.Move(partial, target, overwrite: true); // the cache holds a view copy, never user data
        return target;
    }

    /// <summary>Copies remote files and folders into a local folder. Returns the number of files written.</summary>
    public async Task<int> DownloadAsync(IReadOnlyList<FileEntry> items, string localFolder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var count = 0;
        foreach (var item in items)
            count += await DownloadItemAsync(item.FullPath, item.IsDirectory, localFolder, cancellationToken).ConfigureAwait(false);
        return count;
    }

    private async Task<int> DownloadItemAsync(string remote, bool isDirectory, string localFolder, CancellationToken cancellationToken)
    {
        var address = RemoteUri.Parse(remote);
        var fileSystem = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        var name = PathUtil.GetUniqueName(localFolder, LocalName(RemoteUri.GetName(remote)), isDirectory, p => File.Exists(p) || Directory.Exists(p));
        var target = Path.Combine(localFolder, name);

        if (!isDirectory)
        {
            var partial = await WritePartialAsync(fileSystem, address, localFolder, cancellationToken).ConfigureAwait(false);
            File.Move(partial, target, overwrite: false);
            return 1;
        }

        Directory.CreateDirectory(target);
        var count = 0;
        foreach (var child in await fileSystem.ListAsync(address, cancellationToken).ConfigureAwait(false))
        {
            if (child.Name is "." or ".." || child.IsSymbolicLink)
                continue; // links are never followed
            count += await DownloadItemAsync(RemoteUri.Combine(remote, child.Name), child.IsDirectory, target, cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>Uploads local files and folders into a remote folder; existing remote names are kept (" (2)" suffix).</summary>
    public async Task<int> UploadAsync(IReadOnlyList<string> localPaths, string remoteFolder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        var folder = RemoteUri.Parse(remoteFolder);
        var fileSystem = await ResolveAsync(folder, cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var path in localPaths)
            count += await UploadItemAsync(fileSystem, path, remoteFolder, cancellationToken).ConfigureAwait(false);
        return count;
    }

    private static async Task<int> UploadItemAsync(IRemoteFileSystem fileSystem, string localPath, string remoteFolder, CancellationToken cancellationToken)
    {
        var info = new FileInfo(localPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return 0; // links are never followed
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        var name = await UniqueRemoteNameAsync(fileSystem, remoteFolder, Path.GetFileName(PathUtil.TrimTrailingSeparator(localPath)), isDirectory, cancellationToken).ConfigureAwait(false);
        var target = RemoteUri.Parse(RemoteUri.Combine(remoteFolder, name));

        if (!isDirectory)
        {
            await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await fileSystem.UploadAsync(source, target, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        await fileSystem.CreateFolderAsync(target, cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
            count += await UploadItemAsync(fileSystem, child, target.ToString(), cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task CreateFolderAsync(string remoteFolder, string name, CancellationToken cancellationToken)
    {
        var target = RemoteUri.Parse(RemoteUri.Combine(remoteFolder, name));
        var fileSystem = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        await fileSystem.CreateFolderAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(string remote, string newName, CancellationToken cancellationToken)
    {
        var address = RemoteUri.Parse(remote);
        var fileSystem = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        await fileSystem.RenameAsync(address, newName, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string remote, bool isDirectory, CancellationToken cancellationToken)
    {
        var address = RemoteUri.Parse(remote);
        var fileSystem = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        await fileSystem.DeleteAsync(address, isDirectory, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string remote, CancellationToken cancellationToken)
    {
        var address = RemoteUri.Parse(remote);
        var fileSystem = await ResolveAsync(address, cancellationToken).ConfigureAwait(false);
        return await fileSystem.GetEntryAsync(address, cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<string> UniqueRemoteNameAsync(IRemoteFileSystem fileSystem, string folder, string name, bool isDirectory, CancellationToken cancellationToken)
    {
        var (stem, extension) = PathUtil.SplitName(name, isDirectory);
        var candidate = name;
        for (var i = 2; i < 1000; i++)
        {
            if (await fileSystem.GetEntryAsync(RemoteUri.Parse(RemoteUri.Combine(folder, candidate)), cancellationToken).ConfigureAwait(false) is null)
                return candidate;
            candidate = $"{stem} ({i}){extension}";
        }

        throw new IOException($"No free name for '{name}' in {folder}.");
    }

    /// <summary>Streams a remote file into a ".navigator-partial" file in <paramref name="folder"/>; deleted again on failure.</summary>
    private static async Task<string> WritePartialAsync(IRemoteFileSystem fileSystem, RemoteAddress file, string folder, CancellationToken cancellationToken)
    {
        var partial = Path.Combine(folder, $".{Guid.NewGuid():N}.navigator-partial");
        try
        {
            await using var source = await fileSystem.OpenReadAsync(file, cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await source.CopyToAsync(destination, 256 * 1024, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return partial;
    }

    /// <summary>A remote name made safe for the local file system (Windows forbids some characters Unix servers allow).</summary>
    internal static string LocalName(string name)
    {
        if (PathUtil.ValidateName(name) == NameValidationResult.Valid)
            return name;
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(c < 32 || c is '<' or '>' or ':' or '"' or '\\' or '/' or '|' or '?' or '*' ? '_' : c);
        var cleaned = builder.ToString().TrimEnd('.', ' ');
        return cleaned.Length == 0 || PathUtil.ValidateName(cleaned) != NameValidationResult.Valid ? "_" + cleaned : cleaned;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 8));
}

/// <summary>No installed plugin handles the address's scheme (e.g. the SSH plugin was removed).</summary>
public sealed class NoRemoteFileSystemException(string scheme) : IOException($"No installed plugin opens {scheme}:// addresses.")
{
    public string Scheme { get; } = scheme;
}
