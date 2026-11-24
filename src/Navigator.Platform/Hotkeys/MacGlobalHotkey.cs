using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;

namespace Navigator.Platform.Hotkeys;

/// <summary>
/// Carbon RegisterEventHotKey: the only system-wide hotkey API on macOS that needs no Accessibility permission. Events
/// are delivered by the application's main run loop (Avalonia's UI thread), so there is no extra thread and no cost
/// while idle. Register/Unregister must be called on the main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacGlobalHotkey : IGlobalHotkeyService
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint KEventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint KEventHotKeyPressed = 5;
    private const uint Signature = 0x4E415647; // 'NAVG'
    private const uint CmdKey = 0x0100;
    private const uint ShiftKey = 0x0200;
    private const uint OptionKey = 0x0800;
    private const uint ControlKey = 0x1000;
    private const int EventHotKeyExistsErr = -9878;

    private static Action? _pressed;
    private IntPtr _handler;
    private IntPtr _hotKey;

    public bool IsSupported => true;

    public HotkeyRegistrationResult Register(HotkeyGesture gesture, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);
        var key = HotkeyKeyCodes.MacKeyCode(gesture.Key);
        if (key == HotkeyKeyCodes.MacNoKey)
            return HotkeyRegistrationResult.InvalidGesture;

        uint modifiers = 0;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Meta))
            modifiers |= CmdKey;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
            modifiers |= ShiftKey;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
            modifiers |= OptionKey;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
            modifiers |= ControlKey;

        Unregister();
        _pressed = pressed;
        if (_handler == IntPtr.Zero)
        {
            var spec = new EventTypeSpec { EventClass = KEventClassKeyboard, EventKind = KEventHotKeyPressed };
            if (InstallEventHandler(GetApplicationEventTarget(), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnHotKey, 1, &spec, IntPtr.Zero, out _handler) != 0)
            {
                _handler = IntPtr.Zero;
                return HotkeyRegistrationResult.Unsupported;
            }
        }

        var status = RegisterEventHotKey(key, modifiers, new EventHotKeyId { Signature = Signature, Id = 1 }, GetApplicationEventTarget(), 0, out _hotKey);
        if (status == 0)
            return HotkeyRegistrationResult.Registered;
        _hotKey = IntPtr.Zero;
        return status == EventHotKeyExistsErr ? HotkeyRegistrationResult.InUse : HotkeyRegistrationResult.InvalidGesture;
    }

    public void Unregister()
    {
        if (_hotKey == IntPtr.Zero)
            return;
        _ = UnregisterEventHotKey(_hotKey);
        _hotKey = IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        if (_handler != IntPtr.Zero)
        {
            _ = RemoveEventHandler(_handler);
            _handler = IntPtr.Zero;
        }

        _pressed = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnHotKey(IntPtr callRef, IntPtr eventRef, IntPtr userData)
    {
        try
        {
            _pressed?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // An exception must never unwind into Carbon; a shutting-down app simply ignores the key.
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [LibraryImport(Carbon)]
    private static partial IntPtr GetApplicationEventTarget();

    [LibraryImport(Carbon)]
    private static partial int InstallEventHandler(IntPtr target, IntPtr handler, uint typeCount, EventTypeSpec* types, IntPtr userData, out IntPtr handlerRef);

    [LibraryImport(Carbon)]
    private static partial int RemoveEventHandler(IntPtr handlerRef);

    [LibraryImport(Carbon)]
    private static partial int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, IntPtr target, uint options, out IntPtr hotKeyRef);

    [LibraryImport(Carbon)]
    private static partial int UnregisterEventHotKey(IntPtr hotKeyRef);
}
