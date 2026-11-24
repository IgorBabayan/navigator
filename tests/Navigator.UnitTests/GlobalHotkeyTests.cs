using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Platform.Hotkeys;
using Navigator.Presentation.Commands;

namespace Navigator.UnitTests;

[TestClass]
public sealed class GlobalHotkeyTests
{
    [TestMethod]
    [DataRow("Ctrl+Shift+Alt+E", HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, "E")]
    [DataRow("Cmd+Alt+D1", HotkeyModifiers.Meta | HotkeyModifiers.Alt, "D1")]
    [DataRow("Meta+E", HotkeyModifiers.Meta, "E")]
    [DataRow("Ctrl+F12", HotkeyModifiers.Control, "F12")]
    public void TryParse_ReadsResolvedGestures(string text, HotkeyModifiers modifiers, string key)
    {
        Assert.IsTrue(HotkeyGesture.TryParse(text, out var gesture));
        Assert.AreEqual(new HotkeyGesture(modifiers, key), gesture);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("E")]
    [DataRow("Shift+E")]
    [DataRow("Ctrl+Shift")]
    [DataRow("Ctrl+A+B")]
    public void TryParse_RejectsGesturesThatWouldBlockTyping(string text)
        => Assert.IsFalse(HotkeyGesture.TryParse(text, out _));

    [TestMethod]
    public void DefaultActivateShortcut_IsAValidGlobalGestureOnEveryPlatform()
    {
        foreach (var isMac in new[] { false, true })
        {
            var gestures = CommandRegistry.GesturesFor(CommandRegistry.ActivateAppId, new AppSettings(), isMac);
            Assert.AreEqual(1, gestures.Count);
            Assert.IsTrue(HotkeyGesture.TryParse(gestures[0], out var gesture), gestures[0]);
            Assert.AreNotEqual(0u, HotkeyKeyCodes.WindowsVirtualKey(gesture.Key));
            Assert.AreNotEqual(0u, HotkeyKeyCodes.X11Keysym(gesture.Key));
            Assert.AreNotEqual(HotkeyKeyCodes.MacNoKey, HotkeyKeyCodes.MacKeyCode(gesture.Key));
        }
    }

    [TestMethod]
    public void ActivateCommand_IsGlobalAndOnlyOne()
        => Assert.AreEqual(CommandRegistry.ActivateAppId, CommandRegistry.All.Single(c => c.IsGlobal).Id);

    [TestMethod]
    [DataRow("E", 0x45u, 0x65u, 0x0Eu)]
    [DataRow("D0", 0x30u, 0x30u, 0x1Du)]
    [DataRow("F5", 0x74u, 0xFFC2u, 0x60u)]
    [DataRow("OemComma", 0xBCu, 0x2Cu, 0x2Bu)]
    [DataRow("Space", 0x20u, 0x20u, 0x31u)]
    public void KeyCodes_MapAvaloniaKeyNames(string key, uint windows, uint x11, uint mac)
    {
        Assert.AreEqual(windows, HotkeyKeyCodes.WindowsVirtualKey(key));
        Assert.AreEqual(x11, HotkeyKeyCodes.X11Keysym(key));
        Assert.AreEqual(mac, HotkeyKeyCodes.MacKeyCode(key));
    }

    [TestMethod]
    public void KeyCodes_UnknownKeyIsRejected()
    {
        Assert.AreEqual(0u, HotkeyKeyCodes.WindowsVirtualKey("LaunchMail"));
        Assert.AreEqual(0u, HotkeyKeyCodes.X11Keysym("LaunchMail"));
        Assert.AreEqual(HotkeyKeyCodes.MacNoKey, HotkeyKeyCodes.MacKeyCode("LaunchMail"));
    }
}
