using System.Runtime.CompilerServices;

namespace Navigator.Core.IO;

/// <summary>
/// Immutable snapshot of one directory entry. Created directly from the low-level enumeration
/// struct (no FileInfo allocation) so that listing 100k entries stays cheap.
/// Expensive data (icons, thumbnails, folder sizes, owners) is loaded lazily elsewhere.
/// </summary>
public sealed class FileEntry
{
    private string? _extension;

    public FileEntry(
        string name,
        string fullPath,
        bool isDirectory,
        long length,
        DateTime creationTimeUtc,
        DateTime lastWriteTimeUtc,
        FileAttributes attributes,
        bool isRemote = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Length = isDirectory ? -1 : length;
        CreationTimeUtc = creationTimeUtc;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Attributes = attributes;
        IsRemote = isRemote;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    /// <summary>File size in bytes; -1 for directories.</summary>
    public long Length { get; }

    public DateTime CreationTimeUtc { get; }

    public DateTime LastWriteTimeUtc { get; }

    public FileAttributes Attributes { get; }

    /// <summary>
    /// The entry belongs to a plugin file system: <see cref="FullPath"/> is a remote address ("sftp://host/dir/name"),
    /// so local-only work (thumbnails, previews, folder sizes, shell actions) must skip it.
    /// </summary>
    public bool IsRemote { get; }

    /// <summary>
    /// True when both snapshots describe the same entry with the same metadata (path, kind, size, times, attributes).
    /// Lets a re-enumeration keep the existing instance, so the item view model built for it is kept as well.
    /// </summary>
    public bool IsEquivalentTo(FileEntry other)
        => ReferenceEquals(this, other)
           || (other is not null
               && IsDirectory == other.IsDirectory
               && Length == other.Length
               && LastWriteTimeUtc == other.LastWriteTimeUtc
               && CreationTimeUtc == other.CreationTimeUtc
               && Attributes == other.Attributes
               && IsRemote == other.IsRemote
               && string.Equals(FullPath, other.FullPath, StringComparison.Ordinal)
               && string.Equals(Name, other.Name, StringComparison.Ordinal));

    /// <summary>Hidden attribute. On Unix .NET reports dot-files as Hidden as well.</summary>
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    /// <summary>Windows "protected operating system files" (Hidden + System).</summary>
    public bool IsSystem => (Attributes & FileAttributes.System) != 0 && (Attributes & FileAttributes.Hidden) != 0;

    public bool IsDotFile => Name.Length > 1 && Name[0] == '.';

    public bool IsSymbolicLink => (Attributes & FileAttributes.ReparsePoint) != 0;

    public bool IsReadOnly => (Attributes & FileAttributes.ReadOnly) != 0;

    /// <summary>Lower-case extension including the dot ("" for folders and extension-less files).</summary>
    public string Extension => _extension ??= ComputeExtension();

    public static FileEntry FromInfo(FileSystemInfo info)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        return new FileEntry(
            info.Name,
            info.FullName,
            isDirectory,
            isDirectory ? -1 : ((FileInfo)info).Length,
            info.CreationTimeUtc,
            info.LastWriteTimeUtc,
            info.Attributes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static FileEntry FromEnumeration(ref System.IO.Enumeration.FileSystemEntry entry)
        => new(
            entry.FileName.ToString(),
            entry.ToFullPath(),
            entry.IsDirectory,
            entry.Length,
            entry.CreationTimeUtc.UtcDateTime,
            entry.LastWriteTimeUtc.UtcDateTime,
            entry.Attributes);

    private string ComputeExtension()
    {
        if (IsDirectory)
            return string.Empty;

        var dot = Name.LastIndexOf('.');
        return dot <= 0 || dot == Name.Length - 1 ? string.Empty : Name[dot..].ToLowerInvariant();
    }

    public override string ToString() => FullPath;
}
