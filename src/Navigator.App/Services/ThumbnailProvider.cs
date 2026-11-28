using System.IO.Compression;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Navigator.Core.Caching;
using Navigator.Core.Documents;
using Navigator.Core.IO;
using Navigator.Core.Media;
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
/// <para>
/// Videos, PDFs, documents and audio files get their picture from, in order: what the file embeds (album art, the
/// thumbnail Office/OpenDocument save in the package), then the platform thumbnailer (Windows shell, macOS Quick Look,
/// poppler/ffmpeg on Linux). Those are expensive to make, so every result — including "there is none" as an empty
/// entry — goes to the disk cache, and rows only ask for them in icon and tile layouts (64 px and up).
/// </para>
/// </summary>
internal sealed class ThumbnailProvider : IThumbnailProvider, IDisposable
{
    private const long MaxDecodableFileBytes = 64L * 1024 * 1024;
    private const long MinDiskCachedFileBytes = 256L * 1024;
    private const long Megabyte = 1024L * 1024;

    /// <summary>Below this size (details and list rows) media files keep their type icon: a 20 px video frame says nothing.</summary>
    private const int MinMediaThumbnailPixels = 64;

    private readonly BackgroundWorkScheduler _scheduler;
    private readonly SettingsService _settings;
    private readonly IPlatformThumbnailer _platform;
    private readonly LruCache<string, Bitmap?> _cache;
    private readonly DiskCache _disk;

    public ThumbnailProvider(BackgroundWorkScheduler scheduler, SettingsService settings, AppDirectories directories, IPlatformThumbnailer platform)
    {
        _scheduler = scheduler;
        _settings = settings;
        _platform = platform;
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
    {
        var files = _settings.Current.FilesAndFolders;
        if (!files.ShowThumbnails || entry.IsDirectory || entry.IsRemote)
            return false;
        return IsDecodableImage(entry) || (files.ShowMediaThumbnails && IsMedia(entry));
    }

    public async Task<object?> GetThumbnailAsync(FileEntry entry, int pixelSize, CancellationToken cancellationToken)
    {
        if (IsDecodableImage(entry))
            return await GetImageAsync(entry, pixelSize, cancellationToken).ConfigureAwait(true);
        if (pixelSize < MinMediaThumbnailPixels)
            return null;
        return await GetMediaAsync(entry, pixelSize, cancellationToken).ConfigureAwait(true);
    }

    public bool CanCreatePreviewImage(FileEntry entry)
        => !entry.IsDirectory && !entry.IsRemote && (IsDecodableImage(entry) || IsMedia(entry));

    public async Task<object?> GetPreviewImageAsync(FileEntry entry, int pixelSize, CancellationToken cancellationToken)
    {
        if (IsDecodableImage(entry))
            return await GetImageAsync(entry, pixelSize, cancellationToken).ConfigureAwait(true);
        return IsMedia(entry) ? await GetMediaAsync(entry, pixelSize, cancellationToken).ConfigureAwait(true) : null;
    }

    public Task ClearDiskCacheAsync() => Task.Run(() =>
    {
        _disk.Clear();
        _cache.RemoveWhere(static key => key.StartsWith("m|", StringComparison.Ordinal)); // also forget "no thumbnail" results
    });

    private static bool IsDecodableImage(FileEntry entry)
        => entry.Length is > 0 and < MaxDecodableFileBytes && ItemCategories.IsPreviewableImage(entry.Extension);

    /// <summary>Decided from the name only: something embedded or the platform may have a picture of it.</summary>
    private bool IsMedia(FileEntry entry)
    {
        if (entry.Length <= 0)
            return false;
        var extension = entry.Extension;
        return MediaInfoReader.CanContainCoverArt(extension) || OfficeDocumentReader.IsSupported(extension) || _platform.CanThumbnail(extension);
    }

    // ── Pictures ────────────────────────────────────────────────

    private async Task<object?> GetImageAsync(FileEntry entry, int pixelSize, CancellationToken cancellationToken)
    {
        var key = $"{entry.FullPath}|{pixelSize}|{entry.LastWriteTimeUtc.Ticks}|{entry.Length}";
        return await _cache.GetOrCreateAsync(key, (_, _) => _scheduler.ScheduleAsync(WorkPriority.Visible, _ => ValueTask.FromResult(Load(entry, key, pixelSize)), cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    private Bitmap? Load(FileEntry entry, string key, int pixelSize)
    {
        var useDisk = _disk.IsEnabled && entry.Length >= MinDiskCachedFileBytes;
        if (useDisk && TryReadDisk(key, out var cached))
            return cached;

        var bitmap = Decode(entry.FullPath, pixelSize);
        if (useDisk && bitmap is not null)
            StoreDisk(key, bitmap);
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

    // ── Videos, PDFs, documents, album art ──────────────────────

    private async Task<object?> GetMediaAsync(FileEntry entry, int pixelSize, CancellationToken cancellationToken)
    {
        var key = $"m|{entry.FullPath}|{pixelSize}|{entry.LastWriteTimeUtc.Ticks}|{entry.Length}";
        return await _cache.GetOrCreateAsync(key, (_, _) => LoadMediaAsync(entry, key, pixelSize, cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    private async Task<Bitmap?> LoadMediaAsync(FileEntry entry, string key, int pixelSize, CancellationToken cancellationToken)
    {
        if (_disk.IsEnabled)
        {
            var (hit, cached) = await _scheduler.ScheduleAsync(WorkPriority.Normal, _ => ValueTask.FromResult((TryReadDisk(key, out var bitmap), bitmap)), cancellationToken).ConfigureAwait(false);
            if (hit)
                return cached;
        }

        // Embedded pictures cost a few reads; the platform thumbnailer may start a process or a shell handler.
        var result = await _scheduler.ScheduleAsync(WorkPriority.Normal, _ => ValueTask.FromResult(FromEmbedded(entry, pixelSize)), cancellationToken).ConfigureAwait(false);
        if (result is null && _platform.CanThumbnail(entry.Extension))
        {
            if (await _platform.GetThumbnailAsync(entry.FullPath, pixelSize, cancellationToken).ConfigureAwait(false) is { } image)
                result = await _scheduler.ScheduleAsync(WorkPriority.Normal, _ => ValueTask.FromResult(ToBitmap(image, pixelSize)), cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_disk.IsEnabled)
        {
            if (result is not null)
                StoreDisk(key, result);
            else
                _disk.Store(key, static _ => { }); // empty entry: "no picture", not asked again until the file changes
        }

        return result;
    }

    private static Bitmap? FromEmbedded(FileEntry entry, int pixelSize)
    {
        byte[]? encoded = null;
        try
        {
            if (MediaInfoReader.CanContainCoverArt(entry.Extension))
                encoded = MediaInfoReader.Read(entry.FullPath, includeCoverArt: true)?.CoverArt;
            else if (OfficeDocumentReader.IsSupported(entry.Extension))
                encoded = OfficeDocumentReader.ReadThumbnail(entry.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }

        return encoded is null ? null : DecodeEncoded(encoded, pixelSize);
    }

    private static Bitmap? ToBitmap(ThumbnailImage image, int pixelSize)
    {
        if (image.IsEncoded)
            return DecodeEncoded(image.Data, pixelSize);
        if (image.Width <= 0 || image.Height <= 0 || image.Data.Length < image.Width * image.Height * 4)
            return null;

        var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                new PixelSize(image.Width, image.Height), new Vector(96, 96), image.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        var longest = Math.Max(image.Width, image.Height);
        if (longest <= pixelSize)
            return bitmap;
        var scale = (double)pixelSize / longest;
        var scaled = bitmap.CreateScaledBitmap(
            new PixelSize(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))),
            BitmapInterpolationMode.MediumQuality);
        bitmap.Dispose();
        return scaled;
    }

    private static Bitmap? DecodeEncoded(byte[] data, int pixelSize)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return Bitmap.DecodeToWidth(stream, Math.Max(16, pixelSize), BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    // ── Disk cache ──────────────────────────────────────────────

    /// <summary>True on a hit; an empty entry is a remembered "no thumbnail" (bitmap null).</summary>
    private bool TryReadDisk(string key, out Bitmap? bitmap)
    {
        bitmap = null;
        if (_disk.TryOpen(key) is not { } cached)
            return false;
        using (cached)
        {
            if (cached.Length == 0)
                return true;
            try
            {
                bitmap = new Bitmap(cached);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return false; // a damaged cache file: made again and overwritten
            }
        }
    }

    private void StoreDisk(string key, Bitmap bitmap)
    {
        try
        {
            _disk.Store(key, stream => bitmap.Save(stream, new PngBitmapEncoderOptions { CompressionLevel = CompressionLevel.Fastest }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Could not encode the thumbnail: it is still shown, just not kept on disk.
        }
    }

    public void Dispose() => _cache.Dispose();
}
