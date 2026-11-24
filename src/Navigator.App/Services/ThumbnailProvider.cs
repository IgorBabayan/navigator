using Avalonia.Media.Imaging;
using Navigator.Core.Caching;
using Navigator.Core.IO;
using Navigator.Core.Scheduling;
using Navigator.Presentation.Items;
using Navigator.Presentation.Services;
using Navigator.Presentation.Settings;

namespace Navigator.App.Services;

/// <summary>
/// Image thumbnails decoded at the size they are displayed (Bitmap.DecodeToWidth never materializes the full image),
/// on the shared background scheduler at "visible" priority, cached in a memory-bounded LRU keyed by path, size and
/// last-write time (so an edited image never shows a stale thumbnail). Concurrent requests for the same key share
/// one decode.
/// </summary>
internal sealed class ThumbnailProvider : IThumbnailProvider, IDisposable
{
    private const long MaxDecodableFileBytes = 64L * 1024 * 1024;
    private readonly BackgroundWorkScheduler _scheduler;
    private readonly SettingsService _settings;
    private readonly LruCache<string, Bitmap?> _cache;

    public ThumbnailProvider(BackgroundWorkScheduler scheduler, SettingsService settings)
    {
        _scheduler = scheduler;
        _settings = settings;
        _cache = new LruCache<string, Bitmap?>(
            settings.Current.Performance.ThumbnailMemoryCacheMegabytes * 1024L * 1024,
            bitmap => bitmap is null ? 64 : (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4,
            StringComparer.Ordinal,
            // An evicted bitmap may still be on screen: never dispose it here, the GC reclaims it once unreferenced.
            onEvicted: static _ => { });
        _settings.Changed += (_, section) =>
        {
            if (section is "performance" or "*")
                _cache.Resize(Math.Max(16, _settings.Current.Performance.ThumbnailMemoryCacheMegabytes) * 1024L * 1024);
        };
    }

    public bool CanCreateThumbnail(FileEntry entry)
        => _settings.Current.FilesAndFolders.ShowThumbnails
           && !entry.IsDirectory
           && entry.Length is > 0 and < MaxDecodableFileBytes
           && ItemCategories.IsPreviewableImage(entry.Extension);

    public async Task<object?> GetThumbnailAsync(FileEntry entry, int pixelSize, CancellationToken cancellationToken)
    {
        var key = $"{entry.FullPath}|{pixelSize}|{entry.LastWriteTimeUtc.Ticks}";
        return await _cache.GetOrCreateAsync(key, (_, _) => _scheduler.ScheduleAsync(WorkPriority.Visible, _ => ValueTask.FromResult(Decode(entry.FullPath, pixelSize)), cancellationToken), cancellationToken).ConfigureAwait(true);
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
