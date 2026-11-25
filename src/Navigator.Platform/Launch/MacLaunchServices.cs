using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Navigator.Core.Platform;

namespace Navigator.Platform.Launch;

/// <summary>
/// macOS LaunchServices through its C API: which applications can open a file and which one is the default. Every
/// CoreFoundation object created here is released before returning; nothing is cached, the database is the cache.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacLaunchServices
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const uint AllRoles = 0xFFFFFFFF; // kLSRolesAll
    private const int PathCapacity = 4096;

    public static IReadOnlyList<AppHandler> ApplicationsFor(string path)
    {
        var document = UrlFor(path);
        if (document == 0)
            return [];
        try
        {
            var preferred = CopyDefaultPath(document);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<AppHandler>();
            if (preferred is not null && seen.Add(preferred))
                found.Add(Describe(preferred, isDefault: true));

            var list = LSCopyApplicationURLsForURL(document, AllRoles);
            if (list != 0)
            {
                try
                {
                    var count = CFArrayGetCount(list);
                    var others = new List<AppHandler>((int)Math.Min(count, 64));
                    for (nint index = 0; index < count; index++)
                    {
                        if (PathOf(CFArrayGetValueAtIndex(list, index)) is { } application && seen.Add(application))
                            others.Add(Describe(application, isDefault: false));
                    }

                    others.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
                    found.AddRange(others);
                }
                finally
                {
                    CFRelease(list);
                }
            }

            return found;
        }
        finally
        {
            CFRelease(document);
        }
    }

    /// <summary>The application path doubles as the handler id: <c>open -a</c> accepts it, and it names the icon.</summary>
    private static AppHandler Describe(string application, bool isDefault)
        => new(application, Path.GetFileNameWithoutExtension(application.TrimEnd('/')), application, isDefault);

    private static string? CopyDefaultPath(nint document)
    {
        var application = LSCopyDefaultApplicationURLForURL(document, AllRoles, null);
        if (application == 0)
            return null;
        try
        {
            return PathOf(application);
        }
        finally
        {
            CFRelease(application);
        }
    }

    private static nint UrlFor(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        fixed (byte* text = bytes)
            return CFURLCreateFromFileSystemRepresentation(0, text, bytes.Length, 0);
    }

    private static string? PathOf(nint url)
    {
        if (url == 0)
            return null;
        var buffer = stackalloc byte[PathCapacity];
        if (CFURLGetFileSystemRepresentation(url, 1, buffer, PathCapacity) == 0)
            return null;
        return Marshal.PtrToStringUTF8((nint)buffer);
    }

    [LibraryImport(CoreFoundation)]
    private static partial nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte* buffer, nint length, byte isDirectory);

    [LibraryImport(CoreFoundation)]
    private static partial byte CFURLGetFileSystemRepresentation(nint url, byte resolveAgainstBase, byte* buffer, nint capacity);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [LibraryImport(CoreServices)]
    private static partial nint LSCopyApplicationURLsForURL(nint url, uint roles);

    [LibraryImport(CoreServices)]
    private static partial nint LSCopyDefaultApplicationURLForURL(nint url, uint roles, nint* error);
}
