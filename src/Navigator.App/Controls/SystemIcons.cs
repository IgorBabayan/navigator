using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Navigator.Core.Platform;

namespace Navigator.App.Controls;

/// <summary>
/// Bitmaps of OS-drawn icons for <see cref="IconView.SystemIcon"/>. Each (key, size) is rendered once by the platform
/// provider off the UI thread, scaled down to the requested size and kept for the session. File rows share one bitmap
/// per file type, so the set stays small; it is still capped. All state is touched on the UI thread only, so no locks.
/// </summary>
internal static class SystemIcons
{
    private const int MaxEntries = 1024;
    private static readonly Dictionary<(string Key, int Size), Bitmap?> Cache = [];
    private static readonly Dictionary<(string Key, int Size), List<WeakReference<IconView>>> Pending = [];

    /// <summary>Template icons (SF Symbols, "-symbolic" theme icons): drawn as a mask filled with the text color.</summary>
    private static readonly Dictionary<Bitmap, IBrush> Masks = [];
    private static ISystemIconProvider? _provider;

    public static bool IsSupported => _provider is not null;

    public static void Initialize(ISystemIconProvider provider) => _provider = provider.IsSupported ? provider : null;

    /// <summary>
    /// True when the icon is resolved (<paramref name="bitmap"/> is null if the OS has none: keep the fallback icon).
    /// False while it loads; <paramref name="requester"/> is invalidated when it arrives.
    /// </summary>
    public static bool TryGet(string key, int pixelSize, IconView requester, out Bitmap? bitmap)
    {
        bitmap = null;
        if (_provider is null)
            return true;

        // Sizes are bucketed so DPI changes and slightly different layouts share one bitmap.
        var cacheKey = (key, Math.Clamp((pixelSize + 7) / 8 * 8, 16, 256));
        if (Cache.TryGetValue(cacheKey, out bitmap))
            return true;

        if (Pending.TryGetValue(cacheKey, out var waiters))
        {
            waiters.Add(new WeakReference<IconView>(requester));
            return false;
        }

        Pending[cacheKey] = [new WeakReference<IconView>(requester)];
        _ = LoadAsync(_provider, cacheKey);
        return false;
    }

    /// <summary>The brush that paints a template icon's shape, or null for an ordinary (colored) icon. UI thread only.</summary>
    public static IBrush? MaskOf(Bitmap bitmap) => Masks.GetValueOrDefault(bitmap);

    private static async Task LoadAsync(ISystemIconProvider provider, (string Key, int Size) cacheKey)
    {
        Bitmap? bitmap = null;
        var isTemplate = false;
        try
        {
            var image = await provider.GetIconAsync(cacheKey.Key, cacheKey.Size).ConfigureAwait(false);
            if (image is not null)
            {
                bitmap = ToBitmap(image, cacheKey.Size);
                isTemplate = image.IsTemplate;
            }
        }
        catch (Exception)
        {
            // Error boundary: a system icon that cannot be produced leaves Navigator's own icon in place.
            bitmap = null;
        }

        Dispatcher.UIThread.Post(() => Complete(cacheKey, bitmap, isTemplate));
    }

    private static void Complete((string Key, int Size) cacheKey, Bitmap? bitmap, bool isTemplate)
    {
        if (Cache.Count >= MaxEntries)
        {
            Cache.Clear(); // bitmaps still on screen stay referenced by their views; the GC reclaims the rest
            Masks.Clear();
        }

        Cache[cacheKey] = bitmap;
        if (bitmap is not null && isTemplate)
            Masks[bitmap] = new ImageBrush(bitmap) { Stretch = Stretch.Fill };

        if (!Pending.Remove(cacheKey, out var waiters))
            return;
        foreach (var waiter in waiters)
        {
            if (waiter.TryGetTarget(out var view))
                view.InvalidateVisual();
        }
    }

    internal static Bitmap ToBitmap(SystemIconImage image, int size)
    {
        var handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
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

        // Keep only what is drawn: a 256 px jumbo icon shown at 48 px costs 1/28 of the memory and draws faster.
        if (image.Width <= size && image.Height <= size)
            return bitmap;

        var scale = (double)size / Math.Max(image.Width, image.Height);
        var scaled = bitmap.CreateScaledBitmap(
            new PixelSize(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))),
            BitmapInterpolationMode.HighQuality);
        bitmap.Dispose();
        return scaled;
    }
}
