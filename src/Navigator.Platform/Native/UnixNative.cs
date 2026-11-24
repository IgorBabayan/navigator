using System.Runtime.InteropServices;

namespace Navigator.Platform.Native;

/// <summary>Minimal libc imports. Only functions with stable, struct-free signatures are used.</summary>
internal static partial class UnixNative
{
    private const string LinuxLibc = "libc.so.6";
    private const string MacLibc = "/usr/lib/libSystem.dylib";

    [LibraryImport(LinuxLibc, EntryPoint = "getuid")]
    private static partial uint GetUidLinux();

    [LibraryImport(MacLibc, EntryPoint = "getuid")]
    private static partial uint GetUidMac();

    public static uint GetUserId()
    {
        if (OperatingSystem.IsLinux())
            return GetUidLinux();
        if (OperatingSystem.IsMacOS())
            return GetUidMac();
        throw new PlatformNotSupportedException();
    }
}
