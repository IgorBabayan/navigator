using System.Buffers;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Navigator.Core.IO;

namespace Navigator.Core.Search;

public enum SearchItemKind
{
    Any,
    Files,
    Folders,
}

/// <summary>Search criteria. Empty <see cref="Text"/> matches every name (useful with filters only).</summary>
public sealed record SearchQuery
{
    public required string Root { get; init; }

    /// <summary>Substring, or a wildcard pattern when it contains '*' or '?'.</summary>
    public string Text { get; init; } = string.Empty;

    public bool Recursive { get; init; } = true;

    public SearchItemKind Kind { get; init; } = SearchItemKind.Any;

    /// <summary>Lower-case extensions including the dot (".jpg"). Empty = any.</summary>
    public IReadOnlySet<string> Extensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public long? MinSize { get; init; }

    public long? MaxSize { get; init; }

    public DateTime? ModifiedAfterUtc { get; init; }

    public DateTime? ModifiedBeforeUtc { get; init; }

    public bool IncludeHidden { get; init; }

    /// <summary>Optional text that must appear inside the file (UTF-8/ASCII text files only).</summary>
    public string? ContentText { get; init; }

    public long MaxContentFileSize { get; init; } = 32L * 1024 * 1024;

    public int MaxResults { get; init; } = 5000;

    public int MaxDepth { get; init; } = 64;

    /// <summary>Extra filter (e.g. "has tag X") evaluated after the cheap checks.</summary>
    public Func<FileEntry, bool>? Predicate { get; init; }
}

/// <summary>
/// Cross-platform fallback search: breadth-first walk with cheap name filtering inside the enumerator
/// (non-matching entries are never materialized), symlinked folders are not followed, results stream
/// out in batches, and cancellation stops the walk immediately. No index is built.
/// </summary>
public static class FileSearchEngine
{
    private const int BatchSize = 64;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(120);

    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> SearchAsync(SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<FileEntry[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(() => Produce(query, channel.Writer, cancellationToken), cancellationToken);

        await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return batch;

        await producer.ConfigureAwait(false);
    }

    private static async Task Produce(SearchQuery query, ChannelWriter<FileEntry[]> writer, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            var matcher = new NameMatcher(query.Text);
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((PathUtil.TrimTrailingSeparator(query.Root), 0));
            var buffer = new List<FileEntry>(BatchSize);
            var found = 0;
            var lastFlush = Environment.TickCount64;

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = query.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
                ReturnSpecialDirectories = false,
            };

            while (pending.TryDequeue(out var current))
            {
                token.ThrowIfCancellationRequested();
                List<string>? subdirectories = query.Recursive && current.Depth < query.MaxDepth ? [] : null;

                FileSystemEnumerable<FileEntry?> enumerable;
                try
                {
                    enumerable = new FileSystemEnumerable<FileEntry?>(
                        current.Path,
                        (ref FileSystemEntry entry) =>
                        {
                            if (subdirectories is not null && entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                                subdirectories.Add(entry.ToFullPath());

                            return MatchesCheap(ref entry, query, matcher) ? FileEntry.FromEnumeration(ref entry) : null;
                        },
                        options);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                try
                {
                    foreach (var entry in enumerable)
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry is null)
                            continue;
                        if (query.Predicate is not null && !query.Predicate(entry))
                            continue;
                        if (!string.IsNullOrEmpty(query.ContentText) && !ContainsText(entry, query.ContentText, query.MaxContentFileSize, token))
                            continue;

                        buffer.Add(entry);
                        found++;
                        if (buffer.Count >= BatchSize || Environment.TickCount64 - lastFlush > FlushInterval.TotalMilliseconds)
                        {
                            await writer.WriteAsync([.. buffer], token).ConfigureAwait(false);
                            buffer.Clear();
                            lastFlush = Environment.TickCount64;
                        }

                        if (found >= query.MaxResults)
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Folder vanished or became unreadable mid-walk: skip it and keep searching.
                }

                if (found >= query.MaxResults)
                    break;

                if (subdirectories is not null)
                {
                    foreach (var directory in subdirectories)
                        pending.Enqueue((directory, current.Depth + 1));
                }
            }

            if (buffer.Count > 0)
                await writer.WriteAsync([.. buffer], token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            failure = ex;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private static bool MatchesCheap(ref FileSystemEntry entry, SearchQuery query, NameMatcher matcher)
    {
        var isDirectory = entry.IsDirectory;
        if ((query.Kind == SearchItemKind.Files && isDirectory) || (query.Kind == SearchItemKind.Folders && !isDirectory))
            return false;

        if (!query.IncludeHidden && entry.FileName.Length > 1 && entry.FileName[0] == '.')
            return false;

        if (!matcher.Matches(entry.FileName))
            return false;

        if (query.Extensions.Count > 0)
        {
            if (isDirectory)
                return false;
            var name = entry.FileName;
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || !query.Extensions.Contains(name[dot..].ToString()))
                return false;
        }

        if (!isDirectory)
        {
            if (query.MinSize is { } min && entry.Length < min)
                return false;
            if (query.MaxSize is { } max && entry.Length > max)
                return false;
        }

        var modified = entry.LastWriteTimeUtc.UtcDateTime;
        if (query.ModifiedAfterUtc is { } after && modified < after)
            return false;
        if (query.ModifiedBeforeUtc is { } before && modified > before)
            return false;

        return string.IsNullOrEmpty(query.ContentText) || !isDirectory;
    }

    /// <summary>Streaming, case-insensitive text scan. Files that look binary (NUL in the first block) are skipped.</summary>
    internal static bool ContainsText(FileEntry entry, string text, long maxSize, CancellationToken token)
    {
        if (entry.IsDirectory || entry.Length > maxSize || entry.Length < text.Length)
            return false;

        var charBuffer = ArrayPool<char>.Shared.Rent(64 * 1024 + text.Length);
        try
        {
            using var stream = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
            var carried = 0;
            var first = true;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = reader.Read(charBuffer, carried, 64 * 1024);
                if (read == 0)
                    return false;

                var window = charBuffer.AsSpan(0, carried + read);
                if (first && window[carried..].Contains('\0'))
                    return false;
                first = false;

                if (window.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Keep the tail so that a match spanning two reads is still found.
                carried = Math.Min(text.Length - 1, window.Length);
                window[^carried..].CopyTo(charBuffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    private sealed class NameMatcher(string text)
    {
        private readonly string _text = text.Trim();
        private readonly bool _isPattern = text.AsSpan().IndexOfAny('*', '?') >= 0;

        public bool Matches(ReadOnlySpan<char> name)
        {
            if (_text.Length == 0)
                return true;
            return _isPattern
                ? FileSystemName.MatchesSimpleExpression(_text, name, ignoreCase: true)
                : name.Contains(_text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
