using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Navigator.Core.IO;

/// <summary>Which entries a listing should include.</summary>
public sealed record EnumerationFilter(bool IncludeHidden, bool IncludeSystem, bool IncludeDotFiles)
{
    public static EnumerationFilter Default { get; } = new(IncludeHidden: false, IncludeSystem: false, IncludeDotFiles: false);

    public static EnumerationFilter All { get; } = new(IncludeHidden: true, IncludeSystem: true, IncludeDotFiles: true);

    internal bool ShouldInclude(ref FileSystemEntry entry)
    {
        var attributes = entry.Attributes;
        var name = entry.FileName;
        var isDot = name.Length > 1 && name[0] == '.';

        if (isDot)
            return IncludeDotFiles || IncludeHidden;

        if (!IncludeSystem && (attributes & (FileAttributes.System | FileAttributes.Hidden)) == (FileAttributes.System | FileAttributes.Hidden))
            return false;

        return IncludeHidden || (attributes & FileAttributes.Hidden) == 0;
    }
}

public enum EnumerationErrorKind
{
    NotFound,
    AccessDenied,
    NotADirectory,
    DeviceNotReady,
    Unknown,
}

public sealed class DirectoryEnumerationException(string path, EnumerationErrorKind kind, Exception inner)
    : IOException($"Cannot list '{path}': {kind}", inner)
{
    public string Path { get; } = path;

    public EnumerationErrorKind Kind { get; } = kind;
}

/// <summary>
/// Incremental directory listing. Enumeration runs on a pool thread and yields batches:
/// a small first batch so the first rows paint almost immediately, then larger batches
/// so that 100k-entry folders do not flood the UI thread with collection notifications.
/// </summary>
public static class DirectoryEnumerator
{
    public const int FirstBatchSize = 96;
    public const int BatchSize = 2048;
    private static readonly TimeSpan MaxBatchDelay = TimeSpan.FromMilliseconds(60);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024,
        MatchType = MatchType.Simple,
    };

    /// <summary>Synchronous full listing (used by tests, search and background jobs).</summary>
    public static List<FileEntry> List(string path, EnumerationFilter filter)
    {
        ValidateDirectory(path);
        try
        {
            return [.. CreateEnumerable(path, filter)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Wrap(path, ex);
        }
    }

    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateBatchesAsync(
        string path,
        EnumerationFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<FileEntry[]>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(() => ProduceAsync(path, filter, channel.Writer, cancellationToken), cancellationToken);

        await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return batch;

        await producer.ConfigureAwait(false);
    }

    private static async Task ProduceAsync(string path, EnumerationFilter filter, ChannelWriter<FileEntry[]> writer, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            ValidateDirectory(path);
            var buffer = new List<FileEntry>(FirstBatchSize);
            var target = FirstBatchSize;
            var lastFlush = Environment.TickCount64;

            foreach (var entry in CreateEnumerable(path, filter))
            {
                token.ThrowIfCancellationRequested();
                buffer.Add(entry);

                var elapsed = Environment.TickCount64 - lastFlush;
                if (buffer.Count >= target || (buffer.Count > 0 && elapsed >= MaxBatchDelay.TotalMilliseconds))
                {
                    await writer.WriteAsync([.. buffer], token).ConfigureAwait(false);
                    buffer.Clear();
                    target = BatchSize;
                    lastFlush = Environment.TickCount64;
                }
            }

            if (buffer.Count > 0)
                await writer.WriteAsync([.. buffer], token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            failure = ex;
        }
        catch (DirectoryEnumerationException ex)
        {
            failure = ex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = Wrap(path, ex);
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private static FileSystemEnumerable<FileEntry> CreateEnumerable(string path, EnumerationFilter filter)
        => new(path, static (ref FileSystemEntry entry) => FileEntry.FromEnumeration(ref entry), Options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => filter.ShouldInclude(ref entry),
        };

    private static void ValidateDirectory(string path)
    {
        if (Directory.Exists(path))
            return;

        if (File.Exists(path))
            throw new DirectoryEnumerationException(path, EnumerationErrorKind.NotADirectory, new IOException(path));

        throw new DirectoryEnumerationException(path, EnumerationErrorKind.NotFound, new DirectoryNotFoundException(path));
    }

    internal static DirectoryEnumerationException Wrap(string path, Exception ex) => ex switch
    {
        DirectoryEnumerationException d => d,
        UnauthorizedAccessException => new DirectoryEnumerationException(path, EnumerationErrorKind.AccessDenied, ex),
        DirectoryNotFoundException => new DirectoryEnumerationException(path, EnumerationErrorKind.NotFound, ex),
        DriveNotFoundException => new DirectoryEnumerationException(path, EnumerationErrorKind.DeviceNotReady, ex),
        IOException io when io.HResult == unchecked((int)0x80070015) => new DirectoryEnumerationException(path, EnumerationErrorKind.DeviceNotReady, ex),
        _ => new DirectoryEnumerationException(path, EnumerationErrorKind.Unknown, ex),
    };
}
