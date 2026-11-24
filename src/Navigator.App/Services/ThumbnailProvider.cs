using System.IO.Compression;
using Avalonia.Media.Imaging;
using Navigator.Core.Caching;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Core.Scheduling;
using Navigator.Presentation.Items;
using Navigator.Presentation.Services;
using Navigator.Presentation.Settings;

namespace Navigator.App.Services;

/// <summary>
/// Image thumbnails decoded at the size they are displayed (Bitmap.DecodeToWidth never materializes the full image),
/// on the shared background scheduler at "visible" priority, cached in a memory-bounded LRU keyed by path, size,
/// length and last-write time (so an edited image never shows a stale thumbnail). Concurrent requests for the same
/// key share one decode.
/// <para>
/// Thumbnails of larger images are also kept on disk as small PNGs (Performance.ThumbnailDiskCacheMegabytes, 0 = off):
/// reading a few KB beats decoding a multi-MB photo again after a restart or a memory-cache eviction. Small images
/// are not written there, decoding them directly is as cheap as reading the cached copy.
/// </para>
/// </summary>
internal sealed class ThumbnailProvider : IThumbnailProvider, IDisposable
{
    private const long MaxDecodableFileBytes = 64L * 1024 * 1024;
    private const long MinDiskCachedFileBytes = 256L * 1024;
    private const long Megabyte = 1024L * 1024;
    private readonly BackgroundWorkScheduler _scheduler;
    private readonly SettingsService _settings;
    private readonly LruCache<string, Bitmap?> _cache;
    private readonly DiskCache _disk;

    public ThumbnailProvider(BackgroundWorkScheduler scheduler, SettingsService settings, AppDirectories directories)
    {
        _scheduler = scheduler;
        _settings = settings;
        // The directory is scanned (and trimmed to the limit) in the background on the first write, not at startup.
        _disk = new DiskCache(directories.ThumbnailCacheDirectory, settings.Current.Performance.ThumbnailDiskCacheMegabytes * Megabyte);
        _cache = new LruCache<string, Bitmap?>(
            settings.Current.Performance.ThumbnailMemoryCacheMegabytes * 1024L * 1024,
            bitmap => bitmap is null ? 64 : (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4,
            StringComparer.Ordinal,
            // An evicted bitmap may still be on screen: never dispose it here, the GC reclaims it once unreferenced.
            onEvicted: static _ => { });
        _settings.Changed += (_, section) =>
        {
            if (section is not ("performance" or "*"))
                return;
            _cache.Resize(Math.Max(16, _settings.Current.Performance.ThumbnailMemoryCacheMegabytes) * Megabyte);
            _disk.Resize(_settings.Current.Performance.ThumbnailDiskCacheMegabytes * Megabyte);
        };
    }

    public bool CanCreateThumbnail(FileEntry entry)
        => _settings.Current.FilesAndFolders.ShowThumbnails
           && !entry.IsDirectory
           && entry.Length is > 0 and < MaxDecodableFileBytes
           && ItemCategories.IsPreviewableImage(entry.Extension);

    public async Task<object?> GetThumbnailAsync(FileEntry entry, int pixelSize, CancellationToken cancellationToken)
    {
        var key = $"{entry.FullPath}|{pixelSize}|{entry.LastWriteTimeUtc.Ticks}|{entry.Length}";
        return await _cache.GetOrCreateAsync(key, (_, _) => _scheduler.ScheduleAsync(WorkPriority.Visible, _ => ValueTask.FromResult(Load(entry, key, pixelSize)), cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    public Task ClearDiskCacheAsync() => Task.Run(_disk.Clear);

    private Bitmap? Load(FileEntry entry, string key, int pixelSize)
    {
        var useDisk = _disk.IsEnabled && entry.Length >= MinDiskCachedFileBytes;
        if (useDisk && _disk.TryOpen(key) is { } cached)
        {
            using (cached)
            {
                try
                {
                    return new Bitmap(cached);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    // A damaged cache file: decode the original below and overwrite it.
                }
            }
        }

        var bitmap = Decode(entry.FullPath, pixelSize);
        if (useDisk && bitmap is not null)
        {
            try
            {
                _disk.Store(key, stream => bitmap.Save(stream, new PngBitmapEncoderOptions {  CompressionLevel = CompressionLevel.Fastest }));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
            {
                // Could not encode the thumbnail: it is still shown, just not kept on disk.
            }
        }

        return bitmap;
    }

    private static Bitmap? Decode(string path, int pixelSize)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return Bitmap.DecodeToWidth(stream, Math.Max(16, pixelSize), BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null; // corrupt or unsupported image: the type icon stays
        }
    }

    public void Dispose() => _cache.Dispose();
}
