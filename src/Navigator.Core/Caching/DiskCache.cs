using System.Security.Cryptography;
using System.Text;

namespace Navigator.Core.Caching;

/// <summary>
/// Size-bounded cache of small blobs on disk (used for encoded thumbnails). Keys are hashed to file names in one
/// flat directory; writes go through a temp file and an atomic rename, so a reader never sees a partial file.
/// <para>
/// The total size is tracked in memory after one background scan of the directory. When it exceeds the limit the
/// oldest files (by last write time; a hit refreshes it at most once a day) are deleted until the cache is back
/// under 90 % of the limit. All methods are thread-safe; failures (full disk, read-only folder) are ignored, the
/// cache then simply misses.
/// </para>
/// </summary>
public sealed class DiskCache
{
    private const string Extension = ".bin";
    private static readonly TimeSpan TouchInterval = TimeSpan.FromDays(1);

    private readonly string _directory;
    private long _maxBytes;
    private long _currentBytes = -1; // unknown until the first scan
    private int _trimRunning;

    public DiskCache(string directory, long maxBytes)
    {
        _directory = directory;
        _maxBytes = Math.Max(0, maxBytes);
    }

    public string DirectoryPath => _directory;

    /// <summary>0 disables the cache (no reads, no writes).</summary>
    public long MaxBytes => Interlocked.Read(ref _maxBytes);

    public bool IsEnabled => MaxBytes > 0;

    /// <summary>Approximate bytes on disk; -1 until the directory has been scanned.</summary>
    public long CurrentBytes => Interlocked.Read(ref _currentBytes);

    public void Resize(long maxBytes)
    {
        Interlocked.Exchange(ref _maxBytes, Math.Max(0, maxBytes));
        ScheduleTrimIfNeeded();
    }

    /// <summary>Opens the cached blob for reading, or returns null on a miss.</summary>
    public FileStream? TryOpen(string key)
    {
        if (!IsEnabled)
            return null;
        var path = PathFor(key);
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16 * 1024);
            Touch(path);
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // a miss, including a file deleted by a concurrent trim
        }
    }

    /// <summary>Stores a blob; <paramref name="write"/> writes it to the given stream. Returns false when not stored.</summary>
    public bool Store(string key, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (!IsEnabled)
            return false;
        var path = PathFor(key);
        var temp = path + "." + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            long length;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024))
            {
                write(stream);
                length = stream.Length;
            }

            var existing = new FileInfo(path);
            var replaced = existing.Exists ? existing.Length : 0;
            File.Move(temp, path, overwrite: true);
            if (Interlocked.Read(ref _currentBytes) >= 0)
                Interlocked.Add(ref _currentBytes, length - replaced);
            ScheduleTrimIfNeeded();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return false;
        }
        catch
        {
            TryDelete(temp); // the writer failed (e.g. an encoder error): no half-written file is left behind
            throw;
        }
    }

    /// <summary>Deletes every cached blob.</summary>
    public void Clear()
    {
        try
        {
            foreach (var file in EnumerateFiles())
                TryDelete(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever could not be deleted is found again by the next trim.
        }

        Interlocked.Exchange(ref _currentBytes, 0);
    }

    /// <summary>
    /// Scans the directory (first call) and deletes the oldest files while the cache is over its limit.
    /// Runs synchronously; <see cref="ScheduleTrimIfNeeded"/> is the background entry point.
    /// </summary>
    public void Trim()
    {
        var files = EnumerateFiles().ToList();
        var total = 0L;
        foreach (var file in files)
            total += file.Length;

        var max = MaxBytes;
        if (total > max)
        {
            var target = max * 9 / 10;
            files.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var file in files)
            {
                if (total <= target)
                    break;
                if (TryDelete(file.FullName))
                    total -= file.Length;
            }
        }

        Interlocked.Exchange(ref _currentBytes, total);
    }

    /// <summary>Starts a background trim when the size is unknown or over the limit (never more than one at a time).</summary>
    public void ScheduleTrimIfNeeded()
    {
        var current = Interlocked.Read(ref _currentBytes);
        if (current >= 0 && current <= MaxBytes)
            return;
        if (Interlocked.CompareExchange(ref _trimRunning, 1, 0) != 0)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The directory cannot be scanned: count from zero so writes do not schedule a scan each time.
                Interlocked.CompareExchange(ref _currentBytes, 0, -1);
            }
            finally
            {
                Volatile.Write(ref _trimRunning, 0);
            }
        });
    }

    internal string PathFor(string key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return Path.Combine(_directory, Convert.ToHexStringLower(hash[..16]) + Extension);
    }

    private IEnumerable<FileInfo> EnumerateFiles()
    {
        var directory = new DirectoryInfo(_directory);
        if (!directory.Exists)
            return [];
        try
        {
            return directory.EnumerateFiles("*" + Extension, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Touch(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TouchInterval)
                File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only affects eviction order.
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
