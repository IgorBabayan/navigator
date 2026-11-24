using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;

namespace Navigator.Platform.Hotkeys;

/// <summary>
/// Passive key grab on the X11 root window through libxcb on a dedicated connection and thread. XCB reports a failed
/// grab (key already taken) as a checked reply instead of calling the process-wide Xlib error handler, which Avalonia
/// owns. The thread sleeps in poll() on the X connection and a wake-up pipe: no CPU while idle.
/// The key is grabbed with every combination of Caps Lock and Num Lock so those locks do not disable the shortcut.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe partial class X11GlobalHotkey : IGlobalHotkeyService
{
    private const string Xcb = "libxcb.so.1";
    private const string Libc = "libc.so.6";
    private const ushort ShiftMask = 1;
    private const ushort LockMask = 2;
    private const ushort ControlMask = 4;
    private const ushort Mod1Mask = 8; // Alt
    private const ushort Mod2Mask = 16; // Num Lock
    private const ushort Mod4Mask = 64; // Super
    private const byte GrabModeAsync = 1;
    private const byte KeyPress = 2;
    private const short PollIn = 0x001;
    private const int Eintr = 4;

    // Holding the keys auto-repeats KeyPress events; one activation per press is enough.
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromMilliseconds(400);
    private static readonly ushort[] LockVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    private readonly Lock _gate = new();
    private Thread? _thread;
    private int _wakeWrite = -1;

    public bool IsSupported => true;

    public HotkeyRegistrationResult Register(HotkeyGesture gesture, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);
        var keysym = HotkeyKeyCodes.X11Keysym(gesture.Key);
        if (keysym == 0)
            return HotkeyRegistrationResult.InvalidGesture;

        ushort modifiers = 0;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
            modifiers |= ControlMask;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
            modifiers |= ShiftMask;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
            modifiers |= Mod1Mask;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Meta))
            modifiers |= Mod4Mask;

        lock (_gate)
        {
            StopThread();
            var pipe = stackalloc int[2];
            if (CreatePipe(pipe) != 0)
                return HotkeyRegistrationResult.Unsupported;
            int wakeRead = pipe[0], wakeWrite = pipe[1];
            var outcome = new StrongBox<HotkeyRegistrationResult>(HotkeyRegistrationResult.Unsupported);
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() => Run(keysym, modifiers, pressed, wakeRead, outcome, ready))
            {
                IsBackground = true,
                Name = "Global hotkey",
            };
            thread.Start();
            ready.Wait();
            var result = outcome.Value;
            if (result == HotkeyRegistrationResult.Registered)
            {
                _thread = thread;
                _wakeWrite = wakeWrite;
            }
            else
            {
                thread.Join();
                _ = Close(wakeWrite);
            }

            return result;
        }
    }

    public void Unregister()
    {
        lock (_gate)
            StopThread();
    }

    public void Dispose() => Unregister();

    private void StopThread()
    {
        if (_thread is null)
            return;
        byte signal = 1;
        _ = Write(_wakeWrite, &signal, 1);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ = Close(_wakeWrite);
        _wakeWrite = -1;
        _thread = null;
    }

    /// <summary>
    /// Grabs the key, publishes the outcome and signals <paramref name="ready"/> (exactly once, before serving: the
    /// caller disposes the event as soon as it has the outcome), then serves events until woken through the pipe.
    /// </summary>
    private static void Run(uint keysym, ushort modifiers, Action pressed, int wakeRead, StrongBox<HotkeyRegistrationResult> outcome, ManualResetEventSlim ready)
    {
        var connection = IntPtr.Zero;
        var signaled = false;
        try
        {
            outcome.Value = Grab(keysym, modifiers, out connection, out var root, out var keycode);
            signaled = true;
            ready.Set();
            if (outcome.Value != HotkeyRegistrationResult.Registered)
                return;
            try
            {
                Serve(connection, wakeRead, pressed);
            }
            finally
            {
                foreach (var variant in LockVariants)
                    _ = XcbUngrabKey(connection, keycode, root, (ushort)(modifiers | variant));
                _ = XcbFlush(connection);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libxcb (unusual X11 setup): the outcome stays Unsupported; never crash the app from this thread.
        }
        finally
        {
            if (!signaled)
                ready.Set();
            if (connection != IntPtr.Zero)
                XcbDisconnect(connection);
            _ = Close(wakeRead);
        }
    }

    private static HotkeyRegistrationResult Grab(uint keysym, ushort modifiers, out IntPtr connection, out uint root, out byte keycode)
    {
        root = 0;
        keycode = 0;
        connection = XcbConnect(null, out var screenNumber);
        if (connection == IntPtr.Zero || XcbConnectionHasError(connection) != 0)
            return HotkeyRegistrationResult.Unsupported;

        var setup = XcbGetSetup(connection);
        root = FindRoot(setup, screenNumber);
        keycode = FindKeycode(connection, setup, keysym);
        if (keycode == 0)
            return HotkeyRegistrationResult.InvalidGesture;

        foreach (var variant in LockVariants)
        {
            var error = XcbRequestCheck(connection, XcbGrabKeyChecked(connection, 1, root, (ushort)(modifiers | variant), keycode, GrabModeAsync, GrabModeAsync));
            if (error == IntPtr.Zero)
                continue;
            Free(error);
            foreach (var undo in LockVariants)
                _ = XcbUngrabKey(connection, keycode, root, (ushort)(modifiers | undo));
            _ = XcbFlush(connection);
            return HotkeyRegistrationResult.InUse;
        }

        _ = XcbFlush(connection);
        return HotkeyRegistrationResult.Registered;
    }

    private static void Serve(IntPtr connection, int wakeRead, Action pressed)
    {
        var fds = stackalloc PollFd[2];
        fds[0] = new PollFd { Fd = XcbGetFileDescriptor(connection), Events = PollIn };
        fds[1] = new PollFd { Fd = wakeRead, Events = PollIn };
        var lastFired = 0L;
        while (true)
        {
            fds[0].Revents = 0;
            fds[1].Revents = 0;
            if (Poll(fds, 2, -1) < 0)
            {
                if (Marshal.GetLastPInvokeError() == Eintr)
                    continue;
                return;
            }

            if (fds[1].Revents != 0 || XcbConnectionHasError(connection) != 0)
                return;

            IntPtr evt;
            while ((evt = XcbPollForEvent(connection)) != IntPtr.Zero)
            {
                var type = (byte)(*(byte*)evt & 0x7F);
                Free(evt);
                if (type != KeyPress || System.Diagnostics.Stopwatch.GetElapsedTime(lastFired) < RepeatGuard)
                    continue;
                lastFired = System.Diagnostics.Stopwatch.GetTimestamp();
                pressed();
            }
        }
    }

    private static uint FindRoot(IntPtr setup, int screenNumber)
    {
        var iterator = XcbSetupRootsIterator(setup);
        for (var i = 0; i < screenNumber && iterator.Rem > 1; i++)
            XcbScreenNext(&iterator);
        return *(uint*)iterator.Data; // xcb_screen_t.root is the first field
    }

    /// <summary>Key code carrying the keysym in any group/level of the current keyboard mapping (0 if none).</summary>
    private static byte FindKeycode(IntPtr connection, IntPtr setup, uint keysym)
    {
        // xcb_setup_t: min_keycode at offset 34, max_keycode at 35.
        var min = *((byte*)setup + 34);
        var max = *((byte*)setup + 35);
        var count = (byte)(max - min + 1);
        var reply = XcbGetKeyboardMappingReply(connection, XcbGetKeyboardMapping(connection, min, count), IntPtr.Zero);
        if (reply == IntPtr.Zero)
            return 0;
        try
        {
            // Reply: keysyms_per_keycode at offset 1, the keysym array starts after the 32-byte header.
            var perKeycode = *((byte*)reply + 1);
            var keysyms = (uint*)((byte*)reply + 32);
            for (var level = 0; level < perKeycode; level++)
            {
                for (var index = 0; index < count; index++)
                {
                    if (keysyms[(index * perKeycode) + level] == keysym)
                        return (byte)(min + index);
                }
            }

            return 0;
        }
        finally
        {
            Free(reply);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator
    {
        public IntPtr Data;
        public int Rem;
        public int Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport(Xcb, EntryPoint = "xcb_connect")]
    private static partial IntPtr XcbConnect(byte* displayName, out int screen);

    [LibraryImport(Xcb, EntryPoint = "xcb_connection_has_error")]
    private static partial int XcbConnectionHasError(IntPtr connection);

    [LibraryImport(Xcb, EntryPoint = "xcb_disconnect")]
    private static partial void XcbDisconnect(IntPtr connection);

    [LibraryImport(Xcb, EntryPoint = "xcb_get_setup")]
    private static partial IntPtr XcbGetSetup(IntPtr connection);

    [LibraryImport(Xcb, EntryPoint = "xcb_setup_roots_iterator")]
    private static partial ScreenIterator XcbSetupRootsIterator(IntPtr setup);

    [LibraryImport(Xcb, EntryPoint = "xcb_screen_next")]
    private static partial void XcbScreenNext(ScreenIterator* iterator);

    [LibraryImport(Xcb, EntryPoint = "xcb_get_keyboard_mapping")]
    private static partial uint XcbGetKeyboardMapping(IntPtr connection, byte firstKeycode, byte count);

    [LibraryImport(Xcb, EntryPoint = "xcb_get_keyboard_mapping_reply")]
    private static partial IntPtr XcbGetKeyboardMappingReply(IntPtr connection, uint cookie, IntPtr error);

    [LibraryImport(Xcb, EntryPoint = "xcb_grab_key_checked")]
    private static partial uint XcbGrabKeyChecked(IntPtr connection, byte ownerEvents, uint grabWindow, ushort modifiers, byte key, byte pointerMode, byte keyboardMode);

    [LibraryImport(Xcb, EntryPoint = "xcb_ungrab_key")]
    private static partial uint XcbUngrabKey(IntPtr connection, byte key, uint grabWindow, ushort modifiers);

    [LibraryImport(Xcb, EntryPoint = "xcb_request_check")]
    private static partial IntPtr XcbRequestCheck(IntPtr connection, uint cookie);

    [LibraryImport(Xcb, EntryPoint = "xcb_flush")]
    private static partial int XcbFlush(IntPtr connection);

    [LibraryImport(Xcb, EntryPoint = "xcb_get_file_descriptor")]
    private static partial int XcbGetFileDescriptor(IntPtr connection);

    [LibraryImport(Xcb, EntryPoint = "xcb_poll_for_event")]
    private static partial IntPtr XcbPollForEvent(IntPtr connection);

    [LibraryImport(Libc, EntryPoint = "free")]
    private static partial void Free(IntPtr pointer);

    [LibraryImport(Libc, EntryPoint = "pipe")]
    private static partial int CreatePipe(int* fds);

    [LibraryImport(Libc, EntryPoint = "write")]
    private static partial nint Write(int fd, byte* buffer, nint count);

    [LibraryImport(Libc, EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport(Libc, EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(PollFd* fds, nuint count, int timeout);
}
