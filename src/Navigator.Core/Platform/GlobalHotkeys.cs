namespace Navigator.Core.Platform;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,

    /// <summary>The Windows key on Windows, Super on Linux, Command on macOS.</summary>
    Meta = 8,
}

/// <summary>
/// A system-wide key combination. <see cref="Key"/> is an Avalonia key name ("E", "D1", "F5", "OemComma"…), the same
/// names the shortcut editor stores, so the platform layer can map it to a native key code without referencing Avalonia.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>
    /// Parses a resolved gesture as produced by the command registry ("Ctrl+Shift+Alt+E", "Cmd+Alt+E", "Meta+E").
    /// Fails when there is no key or when the gesture has no Ctrl, Alt or Win/Cmd modifier: a system-wide grab of a
    /// plain or Shift-only key would block normal typing in every application.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0 || key is not null)
                return false;
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "alt" or "option":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "meta" or "cmd" or "win" or "super":
                    modifiers |= HotkeyModifiers.Meta;
                    break;
                default:
                    key = part;
                    break;
            }
        }

        if (key is null || (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Meta)) == 0)
            return false;
        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }
}

public enum HotkeyRegistrationResult
{
    Registered,

    /// <summary>The platform or session offers no global shortcuts (e.g. Wayland).</summary>
    Unsupported,

    /// <summary>The key has no native equivalent or the gesture lacks a Ctrl/Alt/Win/Cmd modifier.</summary>
    InvalidGesture,

    /// <summary>Another application (or the OS) already owns the combination.</summary>
    InUse,
}

/// <summary>
/// One system-wide hotkey that works while any application has focus. The callback may be invoked on any thread
/// (marshal to the UI thread yourself). Registering replaces the previous combination. Idle cost is a blocked thread
/// or nothing at all (macOS), never polling.
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    bool IsSupported { get; }

    HotkeyRegistrationResult Register(HotkeyGesture gesture, Action pressed);

    void Unregister();
}
