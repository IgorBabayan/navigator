using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Navigator.Core.Platform;

namespace Navigator.Platform.Icons;

/// <summary>
/// macOS: the icons Finder shows, from NSWorkspace (<c>iconForFileType:</c> for types, <c>iconForFile:</c> for
/// folders, drives and applications), drawn by CoreGraphics into a BGRA buffer at the requested size.
/// <para>
/// Calls go through the Objective-C runtime's C API (objc_msgSend with exact signatures), so no binding library is
/// needed. Requests run one at a time on a background thread inside an autorelease pool; NSWorkspace's icon methods
/// are documented as safe off the main thread.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacWorkspaceIcons : ISystemIconProvider, IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const uint PremultipliedFirstLittleEndian = 2 | (2 << 12); // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little → BGRA

    private readonly IKnownFolderProvider? _knownFolders;
    private readonly BlockingCollection<Action> _work = new(boundedCapacity: 1024);
    private readonly Lazy<Thread> _worker;

    public MacWorkspaceIcons(IKnownFolderProvider? knownFolders)
    {
        _knownFolders = knownFolders;
        _worker = new Lazy<Thread>(() =>
        {
            var thread = new Thread(Serve) { IsBackground = true, Name = "Navigator Finder icons", Priority = ThreadPriority.BelowNormal };
            thread.Start();
            return thread;
        });
    }

    public bool IsSupported => true;

    /// <summary>Application bundles and icon files show their own picture.</summary>
    public bool HasOwnIcon(string extension) => extension is ".app" or ".icns" or ".prefpane" or ".appex";

    public Task<SystemIconImage?> GetIconAsync(string key, int pixelSize, CancellationToken cancellationToken = default)
    {
        var target = Resolve(key);
        if (target is null || _work.IsAddingCompleted)
            return Task.FromResult<SystemIconImage?>(null);

        _ = _worker.Value;
        var done = new TaskCompletionSource<SystemIconImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var size = Math.Clamp(pixelSize, 16, 512);
        try
        {
            if (!_work.TryAdd(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        done.TrySetCanceled(cancellationToken);
                        return;
                    }

                    done.TrySetResult(Render(target.Value, size));
                }))
            {
                done.TrySetResult(null);
            }
        }
        catch (InvalidOperationException)
        {
            done.TrySetResult(null); // disposed meanwhile
        }

        return done.Task;
    }

    public void Dispose() => _work.CompleteAdding();

    private void Serve()
    {
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try
            {
                job();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Boundary of the icon thread: one icon failing must not stop the next one.
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        _work.Dispose();
    }

    /// <summary>What to ask NSWorkspace for: a type (extension without dot, or a UTI) or a path on disk.</summary>
    private (bool IsType, string Value)? Resolve(string key)
    {
        if (SystemIconKey.TryParseFileType(key, out var extension))
            return (true, extension.Length == 0 ? "public.data" : extension.TrimStart('.'));
        if (!SystemIconKey.TryParse(key, out var kind, out var path))
            return null;
        if (path is not null)
            return (false, path);

        return kind switch
        {
            SystemIconKind.Folder => (true, "public.folder"),
            SystemIconKind.RecycleBin or SystemIconKind.RecycleBinFull when _knownFolders?.GetPath(KnownFolder.Home) is { } home => (false, Path.Combine(home, ".Trash")),
            SystemIconKind.Computer or SystemIconKind.DriveFixed or SystemIconKind.DriveRam => (false, "/"),
            SystemIconKind.DriveRemovable or SystemIconKind.DriveOptical or SystemIconKind.DriveNetwork => (true, "public.volume"),
            SystemIconKind.Network => (true, "public.folder"),
            _ => null,
        };
    }

    private static SystemIconImage? Render((bool IsType, string Value) target, int size)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var workspace = Send(Class("NSWorkspace"), Selector("sharedWorkspace"));
            if (workspace == 0)
                return null;
            var text = NativeString(target.Value);
            var icon = Send(workspace, Selector(target.IsType ? "iconForFileType:" : "iconForFile:"), text);
            if (icon == 0)
                return null;

            // NSImage picks its best representation for the proposed rectangle; the CGImage belongs to the pool.
            var proposed = new Rect(0, 0, size, size);
            var picture = SendRect(icon, Selector("CGImageForProposedRect:context:hints:"), &proposed, 0, 0);
            return picture == 0 ? null : Draw(picture, size);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static SystemIconImage? Draw(nint picture, int size)
    {
        var pixels = new byte[size * size * 4];
        var colors = CGColorSpaceCreateDeviceRGB();
        try
        {
            fixed (byte* buffer = pixels)
            {
                var context = CGBitmapContextCreate(buffer, (nuint)size, (nuint)size, 8, (nuint)(size * 4), colors, PremultipliedFirstLittleEndian);
                if (context == 0)
                    return null;
                try
                {
                    CGContextDrawImage(context, new Rect(0, 0, size, size), picture);
                }
                finally
                {
                    CGContextRelease(context);
                }
            }
        }
        finally
        {
            CGColorSpaceRelease(colors);
        }

        // The context writes premultiplied alpha; Navigator's images carry straight alpha.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is 0 or 255)
                continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
        }

        return new SystemIconImage(size, size, pixels);
    }

    private static nint Class(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* text = bytes)
            return objc_getClass(text);
    }

    private static nint Selector(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* text = bytes)
            return sel_registerName(text);
    }

    /// <summary>An autoreleased NSString.</summary>
    private static nint NativeString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* text = bytes)
            return SendText(Class("NSString"), Selector("stringWithUTF8String:"), text);
    }

    /// <summary>CGRect / NSRect: four CGFloat (double on every supported Mac).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect(double x, double y, double width, double height)
    {
        public readonly double X = x;
        public readonly double Y = y;
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [LibraryImport(ObjC)]
    private static partial nint objc_getClass(byte* name);

    [LibraryImport(ObjC)]
    private static partial nint sel_registerName(byte* name);

    [LibraryImport(ObjC)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendText(nint receiver, nint selector, byte* text);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendRect(nint receiver, nint selector, Rect* rect, nint context, nint hints);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGColorSpaceCreateDeviceRGB();

    [LibraryImport(CoreGraphics)]
    private static partial void CGColorSpaceRelease(nint space);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGBitmapContextCreate(void* data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextDrawImage(nint context, Rect rect, nint image);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextRelease(nint context);
}
