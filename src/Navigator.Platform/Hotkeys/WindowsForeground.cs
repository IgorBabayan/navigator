using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Navigator.Platform.Hotkeys;

/// <summary>
/// Brings a window in front of whatever app the user is in, as a global shortcut must. Windows' foreground lock refuses
/// a plain SetForegroundWindow from a background process (the window would only flash in the taskbar), so the call is
/// made while this thread's input is attached to the current foreground thread's — the established way for an app
/// the user explicitly summoned. Used only in response to the user's own key press.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsForeground
{
    private const int SwRestore = 9;
    private const int AsfwAny = -1;

    public static void BringToFront(nint window)
    {
        if (window == 0)
            return;
        if (IsIconic(window))
            ShowWindow(window, SwRestore);

        var foreground = GetForegroundWindow();
        if (foreground == window)
            return;

        var self = GetCurrentThreadId();
        var foregroundThread = foreground == 0 ? 0u : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
                AttachThreadInput(self, foregroundThread, false);
        }
    }

    /// <summary>
    /// Called on the thread that received WM_HOTKEY, which Windows lets take the foreground: passes that right on to
    /// the whole process, so the UI thread can activate the window a moment later.
    /// </summary>
    internal static void AllowForThisProcess()
    {
        if (!AllowSetForegroundWindow((int)Environment.ProcessId))
            AllowSetForegroundWindow(AsfwAny);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
