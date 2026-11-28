using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Navigator.Core.Platform;

namespace Navigator.Platform.Preview;

/// <summary>File types worth asking a platform thumbnailer about.</summary>
internal static class ThumbnailTypes
{
    public static readonly FrozenSet<string> Video = new[]
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".3gp", ".ts", ".mts", ".m2ts", ".ogv",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Documents = new[]
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".epub", ".xps", ".oxps",
        ".pages", ".numbers", ".key",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pictures the UI toolkit cannot decode but the OS can (HEIF, TIFF, RAW, PSD, SVG…).</summary>
    public static readonly FrozenSet<string> Pictures = new[]
    {
        ".heic", ".heif", ".avif", ".tif", ".tiff", ".svg", ".psd", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".jxl",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Audio = new[] { ".mp3", ".flac", ".m4a", ".wma", ".ogg", ".opus" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class UnsupportedThumbnailer : IPlatformThumbnailer
{
    public bool IsSupported => false;

    public bool CanThumbnail(string extension) => false;

    public Task<ThumbnailImage?> GetThumbnailAsync(string path, int pixelSize, CancellationToken cancellationToken = default) => Task.FromResult<ThumbnailImage?>(null);
}

/// <summary>
/// The Windows shell's thumbnails (IShellItemImageFactory, the same handlers and cache Explorer uses: video frames,
/// PDF and Office first pages, HEIF/RAW pictures when the codecs are installed). All calls run on one STA thread with
/// a bounded queue, called through the COM vtable so no built-in COM interop is needed (trimming).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsShellThumbnailer : IPlatformThumbnailer, IDisposable
{
    private const int SiigbfBiggerSizeOk = 0x01;
    private const int SiigbfThumbnailOnly = 0x08;
    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    private readonly BlockingCollection<Request> _queue = new(boundedCapacity: 256);
    private readonly Lock _startGate = new();
    private Thread? _thread;

    public bool IsSupported => true;

    public bool CanThumbnail(string extension)
        => ThumbnailTypes.Video.Contains(extension) || ThumbnailTypes.Documents.Contains(extension) || ThumbnailTypes.Pictures.Contains(extension) || ThumbnailTypes.Audio.Contains(extension);

    public Task<ThumbnailImage?> GetThumbnailAsync(string path, int pixelSize, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<ThumbnailImage?>(cancellationToken);
        if (_queue.IsAddingCompleted)
            return Task.FromResult<ThumbnailImage?>(null);
        EnsureStarted();
        var request = new Request(path, Math.Clamp(pixelSize, 16, 1024), new TaskCompletionSource<ThumbnailImage?>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
        try
        {
            if (!_queue.TryAdd(request))
                return Task.FromResult<ThumbnailImage?>(null); // saturated: the type icon stays, the row asks again when realized
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult<ThumbnailImage?>(null);
        }

        return request.Completion.Task;
    }

    private void EnsureStarted()
    {
        if (_thread is not null)
            return;
        lock (_startGate)
        {
            if (_thread is not null)
                return;
            var thread = new Thread(Run) { IsBackground = true, Name = "Navigator shell thumbnails", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _thread = thread;
        }
    }

    private void Run()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                continue;
            }

            try
            {
                request.Completion.TrySetResult(Load(request.Path, request.PixelSize));
            }
            catch (Exception)
            {
                // Error boundary of the thumbnail thread: a broken handler must not stop the next request.
                request.Completion.TrySetResult(null);
            }
        }
    }

    private static ThumbnailImage? Load(string path, int pixelSize)
    {
        var id = ImageFactoryId;
        nint factory = 0;
        if (SHCreateItemFromParsingName(path, 0, &id, &factory) != 0 || factory == 0)
            return null;
        try
        {
            // IShellItemImageFactory::GetImage is vtable slot 3 (after IUnknown).
            var vtable = *(nint**)factory;
            var getImage = (delegate* unmanaged[Stdcall]<nint, NativeSize, int, nint*, int>)vtable[3];
            nint bitmap = 0;
            if (getImage(factory, new NativeSize { Width = pixelSize, Height = pixelSize }, SiigbfThumbnailOnly | SiigbfBiggerSizeOk, &bitmap) != 0 || bitmap == 0)
                return null;
            try
            {
                return ToImage(bitmap);
            }
            finally
            {
                _ = DeleteObject(bitmap);
            }
        }
        finally
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)factory)[2])(factory);
        }
    }

    private static ThumbnailImage? ToImage(nint bitmap)
    {
        BitmapHeaderInfo info;
        if (GetObjectW(bitmap, sizeof(BitmapHeaderInfo), &info) == 0 || info.Width <= 0 || info.Height <= 0 || info.Width > 4096 || info.Height > 4096)
            return null;
        var width = info.Width;
        var height = info.Height;
        var pixels = new byte[width * height * 4];
        var buffer = stackalloc byte[sizeof(BitmapInfoHeader) + (256 * 4)];
        var header = (BitmapInfoHeader*)buffer;
        *header = default;
        header->Size = (uint)sizeof(BitmapInfoHeader);
        header->Width = width;
        header->Height = -height; // top-down
        header->Planes = 1;
        header->BitCount = 32;
        var dc = GetDC(0);
        try
        {
            fixed (byte* bits = pixels)
            {
                if (GetDIBits(dc, bitmap, 0, (uint)height, bits, buffer, 0) != height)
                    return null;
            }
        }
        finally
        {
            _ = ReleaseDC(0, dc);
        }

        // Thumbnails of opaque content often carry alpha 0 everywhere: treat them as opaque.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length && !hasAlpha; i += 4)
            hasAlpha = pixels[i] != 0;
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        return new ThumbnailImage(width, height, pixels, IsEncoded: false);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (_thread is null)
            _queue.Dispose();
    }

    private sealed record Request(string Path, int PixelSize, TaskCompletionSource<ThumbnailImage?> Completion, CancellationToken CancellationToken);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeaderInfo
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, Guid* interfaceId, nint* item);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial int GetObjectW(nint handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, void* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);
}

/// <summary>Helper processes that write a PNG into a private temporary folder; at most two run at a time.</summary>
internal abstract class ProcessThumbnailer : IPlatformThumbnailer, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _slots = new(2, 2);

    public abstract bool IsSupported { get; }

    public abstract bool CanThumbnail(string extension);

    public async Task<ThumbnailImage?> GetThumbnailAsync(string path, int pixelSize, CancellationToken cancellationToken = default)
    {
        if (FromCache(path, pixelSize) is { } cached)
            return cached;

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(Path.GetTempPath(), "navigator-thumbnails", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var output = await GenerateAsync(path, Math.Clamp(pixelSize, 16, 2048), folder, Timeout, cancellationToken).ConfigureAwait(false);
            if (output is null || !File.Exists(output))
                return null;
            var bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            return bytes.Length > 8 ? new ThumbnailImage(0, 0, bytes, IsEncoded: true) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _slots.Release();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>A thumbnail another application already made (shared cache); null when there is none.</summary>
    protected virtual ThumbnailImage? FromCache(string path, int pixelSize) => null;

    /// <summary>Runs the tool; returns the path of the PNG it wrote, or null.</summary>
    protected abstract Task<string?> GenerateAsync(string path, int pixelSize, string folder, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>macOS Quick Look thumbnails (<c>qlmanage -t</c>): every type Finder can preview, including iWork and Office.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacQuickLookThumbnailer : ProcessThumbnailer
{
    public override bool IsSupported => true;

    public override bool CanThumbnail(string extension)
        => ThumbnailTypes.Video.Contains(extension) || ThumbnailTypes.Documents.Contains(extension) || ThumbnailTypes.Pictures.Contains(extension) || ThumbnailTypes.Audio.Contains(extension);

    protected override async Task<string?> GenerateAsync(string path, int pixelSize, string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunCapturingAsync("/usr/bin/qlmanage",
            ["-t", "-s", pixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", folder, path], timeout, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return null;
        var output = Path.Combine(folder, Path.GetFileName(path) + ".png");
        return File.Exists(output) ? output : Directory.EnumerateFiles(folder, "*.png").FirstOrDefault();
    }
}

/// <summary>
/// Linux: the freedesktop.org shared thumbnail cache first (thumbnails the desktop's file manager already made, checked
/// against the file's modification time), then poppler's pdftoppm for PDFs and ffmpegthumbnailer/ffmpeg for videos
/// when they are installed.
/// </summary>
internal sealed class FreedesktopThumbnailer : ProcessThumbnailer
{
    private readonly string? _pdftoppm = ProcessRunner.FindOnPath("pdftoppm");
    private readonly string? _ffmpegThumbnailer = ProcessRunner.FindOnPath("ffmpegthumbnailer");
    private readonly string? _ffmpeg = ProcessRunner.FindOnPath("ffmpeg");
    private readonly string _cacheRoot = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache ? cache : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "thumbnails");

    private readonly bool _hasSharedCache;

    public FreedesktopThumbnailer() => _hasSharedCache = Directory.Exists(_cacheRoot); // once: CanThumbnail runs for every realized row

    public override bool IsSupported => _pdftoppm is not null || _ffmpegThumbnailer is not null || _ffmpeg is not null || _hasSharedCache;

    public override bool CanThumbnail(string extension)
        => (ThumbnailTypes.Video.Contains(extension) && (_ffmpegThumbnailer is not null || _ffmpeg is not null || _hasSharedCache))
           || (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && (_pdftoppm is not null || _hasSharedCache))
           || ((ThumbnailTypes.Documents.Contains(extension) || ThumbnailTypes.Pictures.Contains(extension)) && _hasSharedCache);

    protected override ThumbnailImage? FromCache(string path, int pixelSize)
    {
        string uri;
        long mtime;
        try
        {
            uri = new Uri(path).AbsoluteUri;
            mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
        }
        catch (Exception ex) when (ex is UriFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

#pragma warning disable CA5351 // The thumbnail spec names cache files by the MD5 of the URI; nothing security-related.
        var name = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(uri))) + ".png";
#pragma warning restore CA5351
        string[] sizes = pixelSize <= 128 ? ["normal", "large", "x-large", "xx-large"] : pixelSize <= 256 ? ["large", "x-large", "xx-large"] : pixelSize <= 512 ? ["x-large", "xx-large"] : ["xx-large"];
        foreach (var size in sizes)
        {
            var candidate = Path.Combine(_cacheRoot, size, name);
            try
            {
                if (!File.Exists(candidate))
                    continue;
                var bytes = File.ReadAllBytes(candidate);
                if (PngTextValue(bytes, "Thumb::MTime") is { } stored && stored == mtime.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    return new ThumbnailImage(0, 0, bytes, IsEncoded: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable cache entry: generate below.
            }
        }

        return null;
    }

    /// <summary>A tEXt chunk's value (the thumbnail spec stores the source's URI and modification time there).</summary>
    internal static string? PngTextValue(byte[] png, string key)
    {
        if (png.Length < 16 || png[0] != 0x89 || png[1] != (byte)'P')
            return null;
        var position = 8;
        while (position + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            var type = Encoding.ASCII.GetString(png, position + 4, 4);
            var data = position + 8;
            if (length < 0 || data + length > png.Length || type == "IDAT")
                return null;
            if (type == "tEXt")
            {
                var chunk = png.AsSpan(data, length);
                var nul = chunk.IndexOf((byte)0);
                if (nul > 0 && Encoding.Latin1.GetString(chunk[..nul]) == key)
                    return Encoding.Latin1.GetString(chunk[(nul + 1)..]);
            }

            position = data + length + 4;
        }

        return null;
    }

    protected override async Task<string?> GenerateAsync(string path, int pixelSize, string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var size = pixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var output = Path.Combine(folder, "thumbnail.png");
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (_pdftoppm is null)
                return null;
            var result = await ProcessRunner.RunCapturingAsync(_pdftoppm,
                ["-png", "-f", "1", "-l", "1", "-singlefile", "-scale-to", size, path, Path.Combine(folder, "thumbnail")], timeout, cancellationToken).ConfigureAwait(false);
            return result is { ExitCode: 0 } ? output : null;
        }

        if (!ThumbnailTypes.Video.Contains(extension))
            return null;
        if (_ffmpegThumbnailer is not null)
        {
            var result = await ProcessRunner.RunCapturingAsync(_ffmpegThumbnailer, ["-i", path, "-o", output, "-s", size, "-c", "png"], timeout, cancellationToken).ConfigureAwait(false);
            if (result is { ExitCode: 0 } && File.Exists(output))
                return output;
        }

        if (_ffmpeg is null)
            return null;

        // A frame a few seconds in is more telling than a black first frame; short clips fall back to the start.
        foreach (var seek in new[] { "5", "0" })
        {
            var result = await ProcessRunner.RunCapturingAsync(_ffmpeg,
                ["-v", "error", "-nostdin", "-y", "-ss", seek, "-i", path, "-frames:v", "1",
                 "-vf", $"scale=w={size}:h={size}:force_original_aspect_ratio=decrease", output],
                timeout, cancellationToken).ConfigureAwait(false);
            if (result is { ExitCode: 0 } && File.Exists(output) && new FileInfo(output).Length > 0)
                return output;
        }

        return null;
    }
}
