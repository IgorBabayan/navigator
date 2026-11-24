namespace Navigator.Platform.Hotkeys;

/// <summary>
/// Maps the Avalonia key names stored by the shortcut editor to native key codes: Windows virtual keys, X11 keysyms and
/// macOS virtual key codes (ANSI positions). Returns 0 for keys a global shortcut cannot use.
/// </summary>
internal static class HotkeyKeyCodes
{
    public static uint WindowsVirtualKey(string key)
    {
        if (TryLetter(key, out var letter))
            return letter; // VK_A..VK_Z equal 'A'..'Z'
        if (TryDigit(key, out var digit))
            return (uint)('0' + digit);
        if (TryNumbered(key, "NumPad", 0, 9, out var pad))
            return 0x60 + pad;
        if (TryNumbered(key, "F", 1, 24, out var function))
            return 0x70 + function - 1;
        return key switch
        {
            "Space" => 0x20,
            "Enter" or "Return" => 0x0D,
            "Escape" => 0x1B,
            "Tab" => 0x09,
            "Back" => 0x08,
            "Delete" => 0x2E,
            "Insert" => 0x2D,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" or "Prior" => 0x21,
            "PageDown" or "Next" => 0x22,
            "Left" => 0x25,
            "Up" => 0x26,
            "Right" => 0x27,
            "Down" => 0x28,
            "Pause" => 0x13,
            "OemSemicolon" => 0xBA,
            "OemPlus" => 0xBB,
            "OemComma" => 0xBC,
            "OemMinus" => 0xBD,
            "OemPeriod" => 0xBE,
            "OemQuestion" => 0xBF,
            "OemTilde" => 0xC0,
            "OemOpenBrackets" => 0xDB,
            "OemBackslash" or "OemPipe" => 0xDC,
            "OemCloseBrackets" => 0xDD,
            "OemQuotes" => 0xDE,
            _ => 0,
        };
    }

    public static uint X11Keysym(string key)
    {
        if (TryLetter(key, out var letter))
            return letter + 0x20; // lower-case Latin keysyms; the grab is by key code so the active layout does not matter
        if (TryDigit(key, out var digit))
            return (uint)('0' + digit);
        if (TryNumbered(key, "NumPad", 0, 9, out var pad))
            return 0xFFB0 + pad;
        if (TryNumbered(key, "F", 1, 24, out var function))
            return 0xFFBE + function - 1;
        return key switch
        {
            "Space" => 0x20,
            "Enter" or "Return" => 0xFF0D,
            "Escape" => 0xFF1B,
            "Tab" => 0xFF09,
            "Back" => 0xFF08,
            "Delete" => 0xFFFF,
            "Insert" => 0xFF63,
            "Home" => 0xFF50,
            "End" => 0xFF57,
            "PageUp" or "Prior" => 0xFF55,
            "PageDown" or "Next" => 0xFF56,
            "Left" => 0xFF51,
            "Up" => 0xFF52,
            "Right" => 0xFF53,
            "Down" => 0xFF54,
            "Pause" => 0xFF13,
            "OemSemicolon" => 0x3B,
            "OemPlus" => 0x3D,
            "OemComma" => 0x2C,
            "OemMinus" => 0x2D,
            "OemPeriod" => 0x2E,
            "OemQuestion" => 0x2F,
            "OemTilde" => 0x60,
            "OemOpenBrackets" => 0x5B,
            "OemBackslash" or "OemPipe" => 0x5C,
            "OemCloseBrackets" => 0x5D,
            "OemQuotes" => 0x27,
            _ => 0,
        };
    }

    // kVK_* constants from HIToolbox/Events.h (ANSI key positions). 0 is kVK_ANSI_A, so "not found" is uint.MaxValue.
    public const uint MacNoKey = uint.MaxValue;

    private static readonly byte[] MacLetters =
    [
        0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E, // A..M
        0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06, // N..Z
    ];

    private static readonly byte[] MacDigits = [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19];

    private static readonly byte[] MacKeypad = [0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C];

    private static readonly byte[] MacFunction =
    [
        0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F, // F1..F12
        0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A, // F13..F20
    ];

    public static uint MacKeyCode(string key)
    {
        if (TryLetter(key, out var letter))
            return MacLetters[letter - 'A'];
        if (TryDigit(key, out var digit))
            return MacDigits[digit];
        if (TryNumbered(key, "NumPad", 0, 9, out var pad))
            return MacKeypad[pad];
        if (TryNumbered(key, "F", 1, 20, out var function))
            return MacFunction[function - 1];
        return key switch
        {
            "Space" => 0x31,
            "Enter" or "Return" => 0x24,
            "Escape" => 0x35,
            "Tab" => 0x30,
            "Back" => 0x33,
            "Delete" => 0x75,
            "Insert" => 0x72,
            "Home" => 0x73,
            "End" => 0x77,
            "PageUp" or "Prior" => 0x74,
            "PageDown" or "Next" => 0x79,
            "Left" => 0x7B,
            "Right" => 0x7C,
            "Down" => 0x7D,
            "Up" => 0x7E,
            "OemSemicolon" => 0x29,
            "OemPlus" => 0x18,
            "OemComma" => 0x2B,
            "OemMinus" => 0x1B,
            "OemPeriod" => 0x2F,
            "OemQuestion" => 0x2C,
            "OemTilde" => 0x32,
            "OemOpenBrackets" => 0x21,
            "OemBackslash" or "OemPipe" => 0x2A,
            "OemCloseBrackets" => 0x1E,
            "OemQuotes" => 0x27,
            _ => MacNoKey,
        };
    }

    private static bool TryLetter(string key, out uint letter)
    {
        letter = key.Length == 1 && char.IsAsciiLetterUpper(key[0]) ? key[0] : 0u;
        return letter != 0;
    }

    private static bool TryDigit(string key, out int digit)
    {
        digit = key.Length == 2 && key[0] == 'D' && char.IsAsciiDigit(key[1]) ? key[1] - '0' : -1;
        return digit >= 0;
    }

    private static bool TryNumbered(string key, string prefix, int min, int max, out uint number)
    {
        number = 0;
        if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(key.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
            return false;
        number = (uint)value;
        return true;
    }
}
