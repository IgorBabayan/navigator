using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Navigator.Platform.Native;

/// <summary>
/// The few Objective-C runtime calls the item actions need (desktop picture, sharing), through objc_msgSend with exact
/// signatures. Callers wrap their work in <see cref="Pool"/> so autoreleased objects never leak on pool-less threads.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacObjC
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static nint Class(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* text = bytes)
            return objc_getClass(text);
    }

    public static nint Selector(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* text = bytes)
            return sel_registerName(text);
    }

    /// <summary>An autoreleased NSString.</summary>
    public static nint String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* text = bytes)
            return SendText(Class("NSString"), Selector("stringWithUTF8String:"), text);
    }

    public static string? ToManaged(nint nsString)
    {
        if (nsString == 0)
            return null;
        var utf8 = Send(nsString, Selector("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>An autoreleased file NSURL.</summary>
    public static nint FileUrl(string path) => Send(Class("NSURL"), Selector("fileURLWithPath:"), String(path));

    /// <summary>An autoreleased NSArray of file URLs.</summary>
    public static nint FileUrls(IReadOnlyList<string> paths)
    {
        var array = Send(Class("NSMutableArray"), Selector("array"));
        var add = Selector("addObject:");
        foreach (var path in paths)
            Send(array, add, FileUrl(path));
        return array;
    }

    public static nuint Count(nint array) => SendCount(array, Selector("count"));

    public static nint At(nint array, nuint index) => SendIndex(array, Selector("objectAtIndex:"), index);

    /// <summary>Pushes an autorelease pool; disposing pops it.</summary>
    public static PoolScope Pool() => new(objc_autoreleasePoolPush());

    public readonly struct PoolScope(nint pool) : IDisposable
    {
        public void Dispose() => objc_autoreleasePoolPop(pool);
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
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument);

    /// <summary>A method returning BOOL with four object arguments (signed char on Intel, bool on Apple silicon: one byte either way).</summary>
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendBool(nint receiver, nint selector, nint first, nint second, nint third, nint fourth);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendBool(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendText(nint receiver, nint selector, byte* text);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nuint SendCount(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendIndex(nint receiver, nint selector, nuint index);
}
