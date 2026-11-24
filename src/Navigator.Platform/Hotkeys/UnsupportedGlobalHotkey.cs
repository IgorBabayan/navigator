using Navigator.Core.Platform;

namespace Navigator.Platform.Hotkeys;

/// <summary>
/// Wayland (by design no application may grab keys system-wide) and unknown platforms. The desktop's own keyboard
/// settings can bind a shortcut to the Navigator command instead.
/// </summary>
internal sealed class UnsupportedGlobalHotkey : IGlobalHotkeyService
{
    public bool IsSupported => false;

    public HotkeyRegistrationResult Register(HotkeyGesture gesture, Action pressed) => HotkeyRegistrationResult.Unsupported;

    public void Unregister()
    {
    }

    public void Dispose()
    {
    }
}
