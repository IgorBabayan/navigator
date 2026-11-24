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
    private const uint InputMouse = 0;

    // INPUT with its MOUSEINPUT member (the largest of the union): 28 bytes on 32-bit, 40 on 64-bit.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    public static void BringToFront(nint window)
    {
        if (window == 0)
            return;
        if (IsIconic(window))
            ShowWindow(window, SwRestore);

        var foreground = GetForegroundWindow();
        if (foreground == window)
            return;

        // A zero-length synthetic mouse input makes this process the one that received the last input event, which
        // Windows requires before it lets SetForegroundWindow through. No movement, click or key reaches any app.
        SendNoOpInput();

        var self = GetCurrentThreadId();
        var foregroundThread = foreground == 0 ? 0u : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            SetFocus(window);
        }
        finally
        {
            if (attached)
                AttachThreadInput(self, foregroundThread, false);
        }

        // Last resort when the lock still refused (e.g. the foreground app is elevated): the task-switcher entry point.
        if (GetForegroundWindow() != window)
            SwitchToThisWindow(window, true);
    }

    private static void SendNoOpInput()
    {
        var input = new Input { Type = InputMouse };
        SendInput(1, ref input, Marshal.SizeOf<Input>());
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

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, ref Input input, int size);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint window);

    [LibraryImport("user32.dll")]
    private static partial void SwitchToThisWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
