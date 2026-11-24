using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;

namespace Navigator.Platform.Icons;

/// <summary>Platforms without a cheap, dependency-free way to render OS icons keep Navigator's own icon set.</summary>
public sealed class UnsupportedSystemIcons : ISystemIconProvider
{
    public bool IsSupported => false;

    public Task<SystemIconImage?> GetIconAsync(string key, int pixelSize, CancellationToken cancellationToken = default)
        => Task.FromResult<SystemIconImage?>(null);
}

/// <summary>
/// Windows shell icons (stock icons and the per-folder icons of Desktop, Downloads, Pictures…) taken from the shell's
/// system image lists, the same source File Explorer uses.
/// <para>
/// All shell calls run on one lazily started, background STA thread: the shell requires COM to be initialized and icon
/// handlers are apartment-threaded, and a disconnected network folder can block — neither may touch the UI thread.
/// Only a handful of icons are ever requested (sidebar and Home), and callers cache the results, so one thread is plenty.
/// The queue is bounded; when it is full a request simply falls back to Navigator's own icon.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsShellIcons : ISystemIconProvider, IDisposable
{
    // SHGetImageList list ids
    private const int ShilLarge = 0;       // 32 px (scaled by system DPI)
    private const int ShilSmall = 1;       // 16 px
    private const int ShilExtraLarge = 2;  // 48 px
    private const int ShilJumbo = 4;       // 256 px

    private const uint ShgfiSysIconIndex = 0x4000;
    private const uint ShgsiSysIconIndex = 0x4000;
    private const uint IldTransparent = 0x1;

    private static readonly Guid ImageListId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    private readonly BlockingCollection<Request> _queue = new(boundedCapacity: 128);
    private readonly nint[] _imageLists = new nint[5];
    private readonly Lock _startGate = new();
    private Thread? _thread;

    public bool IsSupported => true;

    public Task<SystemIconImage?> GetIconAsync(string key, int pixelSize, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<SystemIconImage?>(cancellationToken);
        if (!SystemIconKey.TryParse(key, out _, out _) || _queue.IsAddingCompleted)
            return Task.FromResult<SystemIconImage?>(null);

        EnsureStarted();
        // Continuations must not run on the shell thread: the caller converts pixels into a bitmap afterwards.
        var request = new Request(key, Math.Max(16, pixelSize), new TaskCompletionSource<SystemIconImage?>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
        try
        {
            if (!_queue.TryAdd(request))
                return Task.FromResult<SystemIconImage?>(null);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult<SystemIconImage?>(null); // disposed meanwhile
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
            var thread = new Thread(Run) { IsBackground = true, Name = "Navigator shell icons", Priority = ThreadPriority.BelowNormal };
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
                request.Completion.TrySetResult(Load(request.Key, request.PixelSize));
            }
            catch (Exception)
            {
                // Error boundary of the icon thread: an icon that cannot be rendered falls back to Navigator's own icon,
                // and the thread must stay alive for the next request.
                request.Completion.TrySetResult(null);
            }
        }

        for (var i = 0; i < _imageLists.Length; i++)
        {
            if (_imageLists[i] != 0)
                Release(_imageLists[i]);
            _imageLists[i] = 0;
        }
    }

    private SystemIconImage? Load(string key, int pixelSize)
    {
        if (!SystemIconKey.TryParse(key, out var kind, out var path))
            return null;

        var index = path is not null ? PathIconIndex(path) : StockIconIndex(kind);
        if (index < 0)
            return null;

        var list = pixelSize <= 16 ? ShilSmall : pixelSize <= 32 ? ShilLarge : pixelSize <= 48 ? ShilExtraLarge : ShilJumbo;
        var image = Render(list, index);

        // The jumbo list draws icons that have no large frame as a small image in the top-left corner of a 256 px canvas.
        if (list == ShilJumbo && image is not null && OpaqueExtent(image) <= 48)
            image = Render(ShilExtraLarge, index) ?? image;
        return image;
    }

    private static int PathIconIndex(string path)
    {
        ShFileInfo info = default;
        var result = SHGetFileInfoW(path, 0, &info, (uint)sizeof(ShFileInfo), ShgfiSysIconIndex);
        return result == 0 ? -1 : info.IconIndex;
    }

    private static int StockIconIndex(SystemIconKind kind)
    {
        uint id = kind switch
        {
            SystemIconKind.Folder => 3,          // SIID_FOLDER
            SystemIconKind.DriveRemovable => 7,  // SIID_DRIVEREMOVE
            SystemIconKind.DriveFixed => 8,      // SIID_DRIVEFIXED
            SystemIconKind.DriveNetwork => 9,    // SIID_DRIVENET
            SystemIconKind.DriveOptical => 11,   // SIID_DRIVECD
            SystemIconKind.DriveRam => 12,       // SIID_DRIVERAM
            SystemIconKind.Network => 17,        // SIID_MYNETWORK
            SystemIconKind.RecycleBin => 31,     // SIID_RECYCLER
            SystemIconKind.RecycleBinFull => 32, // SIID_RECYCLERFULL
            SystemIconKind.Computer => 94,       // SIID_DESKTOPPC ("This PC")
            _ => uint.MaxValue,
        };
        if (id == uint.MaxValue)
            return -1;

        StockIconInfo info = default;
        info.Size = (uint)sizeof(StockIconInfo);
        return SHGetStockIconInfo(id, ShgsiSysIconIndex, &info) == 0 ? info.SystemImageIndex : -1;
    }

    private SystemIconImage? Render(int list, int index)
    {
        var imageList = _imageLists[list];
        if (imageList == 0)
        {
            var id = ImageListId;
            if (SHGetImageList(list, &id, &imageList) != 0 || imageList == 0)
                return null;
            _imageLists[list] = imageList;
        }

        // IImageList::GetIcon is vtable slot 10 (IUnknown's three, then Add, ReplaceIcon, SetOverlayImage, Replace,
        // AddMasked, Draw, Remove, GetIcon). Called through the vtable so no built-in COM interop is needed (trimming).
        var vtable = *(nint**)imageList;
        var getIcon = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)vtable[10];
        nint icon = 0;
        if (getIcon(imageList, index, IldTransparent, &icon) != 0 || icon == 0)
            return null;

        try
        {
            return ToPixels(icon);
        }
        finally
        {
            _ = DestroyIcon(icon);
        }
    }

    private static void Release(nint unknown)
    {
        var vtable = *(nint**)unknown;
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(unknown);
    }

    /// <summary>HICON → top-down BGRA pixels with straight alpha.</summary>
    private static SystemIconImage? ToPixels(nint icon)
    {
        IconInfo info;
        if (GetIconInfo(icon, &info) == 0)
            return null;

        try
        {
            if (info.Color == 0)
                return null; // monochrome icon: not used by the shell image lists at these sizes

            BitmapHeaderInfo bitmap;
            if (GetObjectW(info.Color, sizeof(BitmapHeaderInfo), &bitmap) == 0 || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 1024 || bitmap.Height > 1024)
                return null;

            var width = bitmap.Width;
            var height = bitmap.Height;
            var pixels = new byte[width * height * 4];
            var dc = GetDC(0);
            try
            {
                if (!ReadBits(dc, info.Color, width, height, pixels))
                    return null;

                // Icons without an alpha channel carry transparency in the AND mask (0 = opaque).
                if (!HasAlpha(pixels))
                {
                    var mask = new byte[pixels.Length];
                    var hasMask = info.Mask != 0 && ReadBits(dc, info.Mask, width, height, mask);
                    for (var i = 3; i < pixels.Length; i += 4)
                        pixels[i] = !hasMask || mask[i - 3] == 0 ? (byte)255 : (byte)0;
                }
            }
            finally
            {
                _ = ReleaseDC(0, dc);
            }

            return new SystemIconImage(width, height, pixels);
        }
        finally
        {
            if (info.Color != 0)
                _ = DeleteObject(info.Color);
            if (info.Mask != 0)
                _ = DeleteObject(info.Mask);
        }
    }

    private static bool ReadBits(nint dc, nint bitmap, int width, int height, byte[] target)
    {
        // BITMAPINFO: header plus room for a color table, which GetDIBits may write.
        var buffer = stackalloc byte[sizeof(BitmapInfoHeader) + (256 * 4)];
        var header = (BitmapInfoHeader*)buffer;
        *header = default;
        header->Size = (uint)sizeof(BitmapInfoHeader);
        header->Width = width;
        header->Height = -height; // top-down
        header->Planes = 1;
        header->BitCount = 32;
        fixed (byte* bits = target)
            return GetDIBits(dc, bitmap, 0, (uint)height, bits, buffer, 0) == height;
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return true;
        }

        return false;
    }

    /// <summary>Largest x or y (exclusive) of any visible pixel.</summary>
    private static int OpaqueExtent(SystemIconImage image)
    {
        var extent = 0;
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Width * 4;
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Pixels[row + (x * 4) + 3] != 0)
                    extent = Math.Max(extent, Math.Max(x, y) + 1);
            }
        }

        return extent;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        // The thread drains the queue, releases the image lists and exits; it is a background thread, so it never blocks shutdown.
        if (_thread is null)
            _queue.Dispose();
    }

    private sealed record Request(string Key, int PixelSize, TaskCompletionSource<SystemIconImage?> Completion, CancellationToken CancellationToken);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        public fixed ushort DisplayName[260];
        public fixed ushort TypeName[80];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StockIconInfo
    {
        public uint Size;
        public nint Icon;
        public int SystemImageIndex;
        public int IconIndex;
        public fixed ushort Path[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
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
    private static partial nint SHGetFileInfoW(string path, uint fileAttributes, ShFileInfo* info, uint infoSize, uint flags);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetStockIconInfo(uint id, uint flags, StockIconInfo* info);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetImageList(int list, Guid* interfaceId, nint* imageList);

    [LibraryImport("user32.dll")]
    private static partial int GetIconInfo(nint icon, IconInfo* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

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
