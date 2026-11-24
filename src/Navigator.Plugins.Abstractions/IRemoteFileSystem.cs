using Navigator.Core.Navigation;

namespace Navigator.Plugins;

/// <summary>One item of a remote folder listing.</summary>
public sealed record RemoteEntry(string Name, bool IsDirectory, long Length, DateTime LastWriteTimeUtc, bool IsSymbolicLink = false);

public enum RemoteErrorKind
{
    ConnectionFailed,
    AuthenticationFailed,
    HostKeyRejected,
    NotFound,
    AccessDenied,
    AlreadyExists,

    /// <summary>The user cancelled a credential prompt; not shown as an error.</summary>
    Cancelled,
    Other,
}

/// <summary>Thrown by plugin file systems; the host shows <see cref="Exception.Message"/> for the kind.</summary>
public sealed class RemoteFileSystemException : IOException
{
    public RemoteFileSystemException()
    {
    }

    public RemoteFileSystemException(string message)
        : base(message)
    {
    }

    public RemoteFileSystemException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RemoteFileSystemException(RemoteErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public RemoteErrorKind Kind { get; } = RemoteErrorKind.Other;
}

/// <summary>
/// A file system for one or more URI schemes ("sftp", "ftp", "ftps"). Panes browse it like local folders.
/// Every method is called off the UI thread, must honor cancellation, and must be safe to call concurrently
/// (implementations usually serialize per connection). Paths in <see cref="RemoteAddress"/> are absolute and unescaped.
/// </summary>
public interface IRemoteFileSystem
{
    IReadOnlyList<string> Schemes { get; }

    Task<IReadOnlyList<RemoteEntry>> ListAsync(RemoteAddress folder, CancellationToken cancellationToken);

    /// <summary>Metadata of one item, or null when it does not exist.</summary>
    Task<RemoteEntry?> GetEntryAsync(RemoteAddress address, CancellationToken cancellationToken);

    /// <summary>A readable stream of the file. The caller disposes it.</summary>
    Task<Stream> OpenReadAsync(RemoteAddress file, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="source"/> to a new file. Must fail with <see cref="RemoteErrorKind.AlreadyExists"/> rather than overwrite.</summary>
    Task UploadAsync(Stream source, RemoteAddress target, CancellationToken cancellationToken);

    Task CreateFolderAsync(RemoteAddress folder, CancellationToken cancellationToken);

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    Task DeleteAsync(RemoteAddress address, bool isDirectory, CancellationToken cancellationToken);

    /// <summary>Renames within the same folder. Must not overwrite an existing item.</summary>
    Task RenameAsync(RemoteAddress address, string newName, CancellationToken cancellationToken);
}
