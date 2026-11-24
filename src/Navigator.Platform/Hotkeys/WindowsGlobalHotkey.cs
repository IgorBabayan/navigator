using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;

namespace Navigator.Platform.Hotkeys;

/// <summary>
/// RegisterHotKey on a dedicated thread with its own message queue (hWnd = null posts WM_HOTKEY to that thread), so
/// nothing depends on Avalonia's message loop. The thread sleeps in GetMessage: no CPU while idle. MOD_NOREPEAT stops
/// auto-repeat from firing the hotkey again while it is held.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsGlobalHotkey : IGlobalHotkeyService
{
    private const int HotkeyId = 0x4E41; // "NA"
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly Lock _gate = new();
    private Thread? _thread;
    private uint _threadId;

    public bool IsSupported => true;

    public HotkeyRegistrationResult Register(HotkeyGesture gesture, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);
        var key = HotkeyKeyCodes.WindowsVirtualKey(gesture.Key);
        if (key == 0)
            return HotkeyRegistrationResult.InvalidGesture;

        var modifiers = ModNoRepeat;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
            modifiers |= ModControl;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
            modifiers |= ModShift;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
            modifiers |= ModAlt;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Meta))
            modifiers |= ModWin;

        lock (_gate)
        {
            StopThread();
            var result = HotkeyRegistrationResult.InUse;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() => Run(modifiers, key, pressed, ready, out result))
            {
                IsBackground = true,
                Name = "Global hotkey",
            };
            thread.Start();
            ready.Wait();
            if (result == HotkeyRegistrationResult.Registered)
                _thread = thread;
            else
                thread.Join();
            return result;
        }
    }

    public void Unregister()
    {
        lock (_gate)
            StopThread();
    }

    public void Dispose() => Unregister();

    private void Run(uint modifiers, uint key, Action pressed, ManualResetEventSlim ready, out HotkeyRegistrationResult result)
    {
        // Creates the thread's message queue before anyone can post WM_QUIT to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
        _threadId = GetCurrentThreadId();
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers, key))
        {
            result = Marshal.GetLastPInvokeError() == ErrorHotkeyAlreadyRegistered ? HotkeyRegistrationResult.InUse : HotkeyRegistrationResult.InvalidGesture;
            ready.Set();
            return;
        }

        result = HotkeyRegistrationResult.Registered;
        ready.Set();
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmHotkey && message.WParam == HotkeyId)
                {
                    // This thread may take the foreground right now; let the UI thread use that when it activates.
                    WindowsForeground.AllowForThisProcess();
                    pressed();
                }
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }
    }

    private void StopThread()
    {
        if (_thread is null)
            return;
        PostThreadMessage(_threadId, WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
