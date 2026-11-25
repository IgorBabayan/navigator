using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Platform.Startup;
using Navigator.Presentation.Commands;
using Navigator.Tests;

namespace Navigator.UnitTests;

[TestClass]
public sealed class ShortcutAgentTests
{
    [TestMethod]
    public void AgentConfig_RoundTripsAndIsNotRewrittenWhenUnchanged()
    {
        using var temp = new TempDirectory();
        var files = new ShortcutAgentFiles(Directories(temp));
        var config = new ShortcutAgentConfig { Gesture = "Ctrl+Shift+Alt+E", Executable = "/opt/navigator/Navigator", Arguments = ["a b"] };

        Assert.IsTrue(files.WriteConfig(config));
        var stamp = File.GetLastWriteTimeUtc(files.ConfigFile);
        Assert.IsFalse(files.WriteConfig(new ShortcutAgentConfig { Gesture = "Ctrl+Shift+Alt+E", Executable = "/opt/navigator/Navigator", Arguments = ["a b"] }));
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(files.ConfigFile));

        var read = files.ReadConfig();
        Assert.IsNotNull(read);
        Assert.AreEqual(config.Gesture, read.Gesture);
        Assert.AreEqual(config.Executable, read.Executable);
        CollectionAssert.AreEqual(new[] { "a b" }, read.Arguments);

        Assert.IsTrue(files.WriteConfig(new ShortcutAgentConfig { Gesture = "Ctrl+Alt+N", Executable = "/opt/navigator/Navigator" }));
        files.DeleteConfig();
        Assert.IsNull(files.ReadConfig());
    }

    [TestMethod]
    public void AgentStatus_RoundTrips()
    {
        using var temp = new TempDirectory();
        var files = new ShortcutAgentFiles(Directories(temp));
        Assert.IsNull(files.ReadStatus());
        files.WriteStatus(new ShortcutAgentStatus { Gesture = "Ctrl+Alt+N", Result = HotkeyRegistrationResult.InUse, ProcessId = 42 });
        var status = files.ReadStatus();
        Assert.IsNotNull(status);
        Assert.AreEqual(HotkeyRegistrationResult.InUse, status.Result);
        Assert.AreEqual(42, status.ProcessId);
        File.WriteAllText(files.StatusFile, "{ not json");
        Assert.IsNull(files.ReadStatus());
    }

    [TestMethod]
    public void ActivationChannel_SignalsTheListeningInstanceOnly()
    {
        if (OperatingSystem.IsWindows() && !System.Net.Sockets.Socket.OSSupportsUnixDomainSockets)
            Assert.Inconclusive("No Unix domain sockets.");
        using var temp = new TempDirectory();
        var socket = ActivationChannel.SocketPath(Directories(temp));

        Assert.IsFalse(ActivationChannel.TrySignal(socket, TimeSpan.FromMilliseconds(300)), "nobody listens yet");

        using var activated = new SemaphoreSlim(0);
        using (var listener = ActivationChannel.Listen(socket, () => activated.Release()))
        {
            Assert.IsNotNull(listener);
            Assert.IsNull(ActivationChannel.Listen(socket, () => { }), "a second instance must not take over");
            Assert.IsTrue(ActivationChannel.TrySignal(socket, TimeSpan.FromSeconds(2)));
            Assert.IsTrue(activated.Wait(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(0, activated.CurrentCount, "the ping of the second instance does not activate");
        }

        Assert.IsFalse(ActivationChannel.TrySignal(socket, TimeSpan.FromMilliseconds(300)), "closed listener");

        // A stale socket file left by a crashed instance is replaced.
        File.WriteAllText(socket, string.Empty);
        using var replacement = ActivationChannel.Listen(socket, () => activated.Release());
        Assert.IsNotNull(replacement);
        Assert.IsTrue(ActivationChannel.TrySignal(socket, TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void ActivationChannel_HandsFoldersToTheRunningInstance()
    {
        if (OperatingSystem.IsWindows() && !System.Net.Sockets.Socket.OSSupportsUnixDomainSockets)
            Assert.Inconclusive("No Unix domain sockets.");
        using var temp = new TempDirectory();
        var socket = ActivationChannel.SocketPath(Directories(temp));
        string[] folders = [Path.Combine(temp.Path, "a b"), Path.Combine(temp.Path, "ünï")];

        IReadOnlyList<string>? received = null;
        using var activated = new SemaphoreSlim(0);
        using var listener = ActivationChannel.Listen(socket, (IReadOnlyList<string> paths) =>
        {
            received = paths;
            activated.Release();
        });
        Assert.IsNotNull(listener);

        Assert.IsTrue(ActivationChannel.TrySignal(socket, TimeSpan.FromSeconds(2), folders));
        Assert.IsTrue(activated.Wait(TimeSpan.FromSeconds(2)));
        CollectionAssert.AreEqual(folders, received!.ToArray());

        Assert.IsTrue(ActivationChannel.TrySignal(socket, TimeSpan.FromSeconds(2), []));
        Assert.IsTrue(activated.Wait(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(0, received!.Count, "a plain launch only activates");
    }

    [TestMethod]
    public void ActivationChannel_UsesAShortPathWhenTheCacheFolderIsDeep()
    {
        var deep = new AppDirectories("/c", "/" + new string('x', 120), "/l");
        var path = ActivationChannel.SocketPath(deep);
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(path) <= 100 || path.StartsWith(Path.GetTempPath(), StringComparison.Ordinal));
        Assert.AreEqual(path, ActivationChannel.SocketPath(deep), "stable for the same folder");
    }

    [TestMethod]
    public void GnomeAccelerator_MapsGesturesToGtkNames()
    {
        Assert.AreEqual("<Control><Shift><Alt>e", ShortcutText.Accelerator(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, "E")));
        Assert.AreEqual("<Super>1", ShortcutText.Accelerator(new HotkeyGesture(HotkeyModifiers.Meta, "D1")));
        Assert.AreEqual("<Control>F12", ShortcutText.Accelerator(new HotkeyGesture(HotkeyModifiers.Control, "F12")));
        Assert.AreEqual("<Alt>Page_Down", ShortcutText.Accelerator(new HotkeyGesture(HotkeyModifiers.Alt, "PageDown")));
        Assert.IsNull(ShortcutText.Accelerator(new HotkeyGesture(HotkeyModifiers.Control, "LaunchMail")));

        // The default shortcut must be expressible on GNOME.
        var gesture = CommandRegistry.GesturesFor(CommandRegistry.ActivateAppId, new AppSettings(), isMac: false)[0];
        Assert.IsTrue(HotkeyGesture.TryParse(gesture, out var parsed));
        Assert.IsNotNull(ShortcutText.Accelerator(parsed));
    }

    [TestMethod]
    public void GnomeKeybindingList_ParsesAndFormatsGVariantArrays()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), ShortcutText.ParseList("@as []\n"));
        var list = ShortcutText.ParseList("['/org/a/', '/org/b/']\n");
        CollectionAssert.AreEqual(new[] { "/org/a/", "/org/b/" }, list);
        Assert.AreEqual("['/org/a/', '/org/b/']", ShortcutText.FormatList(list));
        Assert.AreEqual(@"'it\'s \\ ok'", ShortcutText.Literal(@"it's \ ok"));
    }

    [TestMethod]
    [DataRow("/usr/bin/navigator", "/usr/bin/navigator")]
    [DataRow("/opt/My Apps/Navigator", "\"/opt/My Apps/Navigator\"")]
    [DataRow("/tmp/a$b", "\"/tmp/a\\$b\"")]
    [DataRow("50%", "50%%")]
    public void DesktopEntryExec_QuotesArguments(string value, string expected)
        => Assert.AreEqual(expected, ShortcutText.DesktopEntryQuote(value));

    [TestMethod]
    public void GlobalShortcutWhenClosed_IsOnByDefault()
        => Assert.IsTrue(new AppSettings().General.GlobalShortcutWhenClosed);

    private static AppDirectories Directories(TempDirectory temp)
        => new(temp.Combine("config"), temp.Combine("cache"), temp.Combine("logs"));
}
