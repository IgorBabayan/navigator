using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Navigator.Platform.Hotkeys;

/// <summary>
/// The main run loop for the macOS shortcut agent: an "accessory" NSApplication (no Dock icon, no menu bar) whose event
/// loop delivers Carbon hotkey events. Returns only when the process exits.
/// </summary>
[SupportedOSPlatform("macos")]
public static partial class MacAgentRunLoop
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const long ActivationPolicyAccessory = 1;

    /// <summary>Makes the process a background accessory app. Call on the main thread before registering the hotkey.</summary>
    public static void Prepare()
    {
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var app = SharedApplication();
        SendLong(app, RegisterSelector("setActivationPolicy:"), ActivationPolicyAccessory);
    }

    /// <summary>Runs the application event loop on the main thread.</summary>
    public static void Run() => Send(SharedApplication(), RegisterSelector("run"));

    private static IntPtr SharedApplication() => Send(GetClass("NSApplication"), RegisterSelector("sharedApplication"));

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RegisterSelector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SendLong(IntPtr receiver, IntPtr selector, long argument);
}
