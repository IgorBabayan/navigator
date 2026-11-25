using System.Formats.Tar;
using System.IO.Compression;
using Navigator.Core.IO;
using Navigator.Core.Operations;

namespace Navigator.Core.Archives;

public enum ArchiveFormat
{
    Unknown,
    Zip,
    Tar,
    TarGz,

    /// <summary>Written through an installed 7-Zip / libarchive program (<see cref="SevenZipTool"/>); not extracted.</summary>
    SevenZip,
}

/// <summary>
/// ZIP and TAR(.GZ) creation and extraction with the .NET base class library.
/// Extraction refuses absolute paths, "..", and links that point outside the destination (path traversal, "zip slip").
/// 7z/RAR are not supported (no BCL implementation; see docs/FEATURE_PARITY.md).
/// </summary>
public static class ArchiveService
{
    public static ArchiveFormat DetectFormat(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".zip", StringComparison.Ordinal))
            return ArchiveFormat.Zip;
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
            return ArchiveFormat.TarGz;
        if (name.EndsWith(".tar", StringComparison.Ordinal))
            return ArchiveFormat.Tar;
        return ArchiveFormat.Unknown;
    }

    public static bool IsSupportedArchive(string path) => DetectFormat(path) != ArchiveFormat.Unknown;

    /// <summary>
    /// "Smart extract": a single top-level item is extracted into <paramref name="parentDirectory"/>;
    /// several top-level items go into a new folder named after the archive.
    /// </summary>
    public static string ChooseSmartDestination(string archivePath, string parentDirectory)
    {
        var topLevel = GetTopLevelNames(archivePath);
        return topLevel.Count == 1 ? parentDirectory : ChooseFolderDestination(archivePath, parentDirectory);
    }

    /// <summary>A new, not yet existing folder next to the archive, named after it ("photos.tar.gz" → "photos", "photos (2)"…).</summary>
    public static string ChooseFolderDestination(string archivePath, string parentDirectory)
    {
        var unique = PathUtil.GetUniqueName(parentDirectory, ArchiveStem(archivePath), isDirectory: true, p => Directory.Exists(p) || File.Exists(p));
        return Path.Combine(parentDirectory, unique);
    }

    /// <summary>File name of the archive without its archive suffix (double suffixes such as ".tar.gz" count as one).</summary>
    public static string ArchiveStem(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        foreach (var suffix in ArchiveSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        }

        return name;
    }

    private static readonly string[] ArchiveSuffixes = [".tar.gz", ".tgz", ".tar", ".zip"];

    internal static IReadOnlyCollection<string> GetTopLevelNames(string archivePath)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entryName in ListEntryNames(archivePath))
        {
            var trimmed = entryName.Replace('\\', '/').TrimStart('/');
            if (trimmed.StartsWith("./", StringComparison.Ordinal))
                trimmed = trimmed[2..];
            var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
            var top = slash < 0 ? trimmed : trimmed[..slash];
            if (top.Length > 0 && top != ".")
                names.Add(top);
        }

        return names;
    }

    private static IEnumerable<string> ListEntryNames(string archivePath)
    {
        switch (DetectFormat(archivePath))
        {
            case ArchiveFormat.Zip:
            {
                using var zip = ZipFile.OpenRead(archivePath);
                foreach (var entry in zip.Entries)
                    yield return entry.FullName;
                break;
            }

            case ArchiveFormat.Tar:
            case ArchiveFormat.TarGz:
            {
                using var stream = OpenTarStream(archivePath);
                using var reader = new TarReader(stream);
                while (reader.GetNextEntry() is { } entry)
                    yield return entry.Name;
                break;
            }
        }
    }

    /// <summary>Extracts into <paramref name="destinationDirectory"/> (created if missing). Existing files trigger conflict resolution.</summary>
    public static async Task ExtractAsync(string archivePath, string destinationDirectory, OperationContext context)
    {
        Directory.CreateDirectory(destinationDirectory);
        var root = PathUtil.Normalize(destinationDirectory);
        context.AddTotals(new FileInfo(archivePath).Length, 1);
        context.MarkRunning();

        switch (DetectFormat(archivePath))
        {
            case ArchiveFormat.Zip:
                await ExtractZipAsync(archivePath, root, context).ConfigureAwait(false);
                break;
            case ArchiveFormat.Tar:
            case ArchiveFormat.TarGz:
                await ExtractTarAsync(archivePath, root, context).ConfigureAwait(false);
                break;
            default:
                throw new OperationStepException(FileOperationErrorKind.NotSupported, $"'{Path.GetFileName(archivePath)}' is not a supported archive.");
        }

        context.CompleteItem();
        context.RecordTransferred(new TransferredItem(archivePath, destinationDirectory, IsDirectory: true, WasMoved: false));
    }

    private static async Task ExtractZipAsync(string archivePath, string root, OperationContext context)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var totalCompressed = Math.Max(1, zip.Entries.Sum(e => e.CompressedLength));
        var archiveLength = new FileInfo(archivePath).Length;

        foreach (var entry in zip.Entries)
        {
            await context.CheckpointAsync().ConfigureAwait(false);
            context.SetCurrentItem(entry.FullName);
            var target = string.Empty;
            if (!await context.RunWithRecoveryAsync(entry.FullName, () => { target = ResolveSafePath(root, entry.FullName); return ValueTask.CompletedTask; }).ConfigureAwait(false))
                continue;
            context.ReportBytes(entry.CompressedLength * archiveLength / totalCompressed);

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var destination = await ResolveExtractConflictAsync(target, entry.Length, entry.LastWriteTime.UtcDateTime, context).ConfigureAwait(false);
            if (destination is null)
                continue;

            await context.RunWithRecoveryAsync(destination, async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".navigator-partial";
                try
                {
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                        await input.CopyToAsync(output, context.CancellationToken).ConfigureAwait(false);

                    File.SetLastWriteTimeUtc(temporary, entry.LastWriteTime.UtcDateTime);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }).ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarAsync(string archivePath, string root, OperationContext context)
    {
        await using var stream = OpenTarStream(archivePath);
        await using var reader = new TarReader(stream);
        while (await reader.GetNextEntryAsync(copyData: false, context.CancellationToken).ConfigureAwait(false) is { } entry)
        {
            await context.CheckpointAsync().ConfigureAwait(false);
            context.SetCurrentItem(entry.Name);
            var target = string.Empty;
            var entryName = entry.Name;
            if (!await context.RunWithRecoveryAsync(entryName, () => { target = ResolveSafePath(root, entryName); return ValueTask.CompletedTask; }).ConfigureAwait(false))
                continue;

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                {
                    var destination = await ResolveExtractConflictAsync(target, entry.Length, entry.ModificationTime.UtcDateTime, context).ConfigureAwait(false);
                    if (destination is null || entry.DataStream is null)
                        break;

                    var data = entry.DataStream;
                    await context.RunWithRecoveryAsync(destination, async () =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        var temporary = destination + ".navigator-partial";
                        try
                        {
                            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                                await data.CopyToAsync(output, context.CancellationToken).ConfigureAwait(false);
                            File.SetLastWriteTimeUtc(temporary, entry.ModificationTime.UtcDateTime);
                            if (!OperatingSystem.IsWindows())
                                File.SetUnixFileMode(temporary, entry.Mode & (UnixFileMode)0x1FF);
                            File.Move(temporary, destination, overwrite: true);
                        }
                        finally
                        {
                            if (File.Exists(temporary))
                                File.Delete(temporary);
                        }
                    }).ConfigureAwait(false);

                    context.ReportBytes(entry.Length);
                    break;
                }

                case TarEntryType.SymbolicLink:
                {
                    // Only links that stay inside the extraction root are recreated.
                    var linkTarget = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, entry.LinkName));
                    if (PathUtil.IsSameOrDescendant(root, linkTarget) && !File.Exists(target) && !Directory.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.CreateSymbolicLink(target, entry.LinkName);
                    }

                    break;
                }

                default:
                    // Hard links, devices, FIFOs: skipped on purpose (not meaningful for a file manager extract).
                    break;
            }
        }
    }

    private static async ValueTask<string?> ResolveExtractConflictAsync(string target, long length, DateTime lastWriteUtc, OperationContext context)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
            return target;

        var existing = new FileInfo(target);
        var resolution = await context.ResolveConflictAsync(new ConflictInfo(
            target, target, false, Directory.Exists(target), length, existing.Exists ? existing.Length : -1,
            lastWriteUtc, existing.Exists ? existing.LastWriteTimeUtc : DateTime.MinValue)).ConfigureAwait(false);

        return resolution switch
        {
            ConflictResolution.Replace when existing.Exists => target,
            ConflictResolution.KeepBoth or ConflictResolution.Replace =>
                Path.Combine(Path.GetDirectoryName(target)!, PathUtil.GetUniqueName(Path.GetDirectoryName(target)!, Path.GetFileName(target), false, p => File.Exists(p) || Directory.Exists(p))),
            _ => null,
        };
    }

    /// <summary>Maps an archive entry name to a path inside <paramref name="root"/> or throws.</summary>
    internal static string ResolveSafePath(string root, string entryName)
    {
        var relative = entryName.Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal) && OperatingSystem.IsWindows())
            throw new OperationStepException(FileOperationErrorKind.InvalidName, $"Unsafe archive entry '{entryName}'.");

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathUtil.IsSameOrDescendant(root, full))
            throw new OperationStepException(FileOperationErrorKind.InvalidName, $"Archive entry '{entryName}' points outside the destination folder.");
        return full;
    }

    /// <summary>Creates a ZIP of the given files/folders. Links are stored as the files they point to are not followed into folders.</summary>
    public static async Task CreateZipAsync(IReadOnlyList<string> sources, string zipPath, CompressionLevel level, OperationContext context)
    {
        var entries = new List<(string FullPath, string EntryName, bool IsDirectory, long Length)>();
        foreach (var source in sources)
        {
            var trimmed = PathUtil.TrimTrailingSeparator(source);
            var baseDirectory = Path.GetDirectoryName(trimmed)!;
            Collect(trimmed, baseDirectory, entries);
        }

        context.AddTotals(entries.Sum(e => Math.Max(0, e.Length)), entries.Count);
        context.MarkRunning();

        var temporary = zipPath + ".navigator-partial";
        var committed = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (fullPath, entryName, isDirectory, length) in entries)
                {
                    await context.CheckpointAsync().ConfigureAwait(false);
                    context.SetCurrentItem(fullPath);
                    if (isDirectory)
                    {
                        zip.CreateEntry(entryName + "/");
                    }
                    else
                    {
                        await context.RunWithRecoveryAsync(fullPath, async () =>
                        {
                            var entry = zip.CreateEntry(entryName, level);
                            entry.LastWriteTime = File.GetLastWriteTime(fullPath);
                            await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await using var output = entry.Open();
                            await input.CopyToAsync(output, context.CancellationToken).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                        context.ReportBytes(length);
                    }

                    context.CompleteItem();
                }
            }

            File.Move(temporary, zipPath, overwrite: false);
            committed = true;
            context.RecordTransferred(new TransferredItem(sources[0], zipPath, IsDirectory: false, WasMoved: false));
        }
        finally
        {
            if (!committed && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static void Collect(string path, string baseDirectory, List<(string FullPath, string EntryName, bool IsDirectory, long Length)> entries)
    {
        var relative = Path.GetRelativePath(baseDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            entries.Add((path, relative, true, 0));
            if (directory.LinkTarget is not null)
                return; // do not follow directory links (cycle safety)

            foreach (var child in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                Collect(child.FullName, baseDirectory, entries);
            return;
        }

        var file = new FileInfo(path);
        if (file.Exists)
            entries.Add((path, relative, false, file.Length));
    }

    private static Stream OpenTarStream(string archivePath)
    {
        var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return DetectFormat(archivePath) == ArchiveFormat.TarGz ? new GZipStream(file, CompressionMode.Decompress) : file;
    }
}
