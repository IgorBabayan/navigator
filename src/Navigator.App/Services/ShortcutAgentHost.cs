using System.Diagnostics;
using System.Runtime;
using Navigator.Core.Platform;
using Navigator.Platform;
using Navigator.Platform.Hotkeys;

namespace Navigator.App.Services;

/// <summary>
/// <c>Navigator --shortcut-agent</c>: a tiny background process, started at sign-in, that holds the global "open Navigator"
/// shortcut so it works while Navigator is not running. It never loads Avalonia or the UI: it reads
/// <see cref="ShortcutAgentConfig"/>, registers the key and waits (a blocked thread on Windows and X11, the AppKit event
/// loop on macOS; no polling). When the key is pressed it asks a running Navigator to come to the front through the
/// <see cref="ActivationChannel"/> and starts Navigator only when none answers. It exits as soon as the config file
/// changes or disappears; the app then starts a fresh agent with the new shortcut.
/// </summary>
internal static class ShortcutAgentHost
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);
    private static readonly long LaunchGuardTicks = TimeSpan.FromSeconds(8).Ticks;
    private static readonly long RepeatGuardTicks = TimeSpan.FromMilliseconds(500).Ticks;

    private static long _lastPress;
    private static long _lastLaunch;
    private static int _launching;

    public static int Run()
    {
        var directories = AppDirectories.Resolve();
        var files = new ShortcutAgentFiles(directories);
        Directory.CreateDirectory(directories.CacheDirectory);

        // One agent per user. A replaced agent may still be exiting: wait a moment for its lock.
        using var held = AcquireLock(files.LockFile);
        if (held is null)
            return 0;

        var config = files.ReadConfig();
        if (config is null || config.Executable.Length == 0 || !HotkeyGesture.TryParse(config.Gesture, out var gesture))
        {
            files.DeleteStatus();
            return 0;
        }

        if (OperatingSystem.IsMacOS())
            MacAgentRunLoop.Prepare();

        var socket = ActivationChannel.SocketPath(directories);
        using var hotkey = PlatformServices.CreateGlobalHotkey(new PlatformInfo());
        var result = hotkey.IsSupported ? hotkey.Register(gesture, () => OnPressed(socket, config)) : HotkeyRegistrationResult.Unsupported;
        files.WriteStatus(new ShortcutAgentStatus { Gesture = config.Gesture, Result = result, ProcessId = Environment.ProcessId });
        if (result != HotkeyRegistrationResult.Registered)
            return 0;

        // Exit on any change of the config; the app starts the next agent itself.
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(files.ConfigFile)!, Path.GetFileName(files.ConfigFile))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += (_, _) => Environment.Exit(0);
        watcher.Created += (_, _) => Environment.Exit(0);
        watcher.Deleted += (_, _) => Environment.Exit(0);
        watcher.Renamed += (_, _) => Environment.Exit(0);
        watcher.EnableRaisingEvents = true;
        if (files.ReadConfig()?.Gesture != config.Gesture)
            return 0; // changed between reading and watching

        // Idle for days: hand everything that start-up allocated back to the OS once.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        if (OperatingSystem.IsMacOS())
            MacAgentRunLoop.Run();
        else
            Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static FileStream? AcquireLock(string path)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(LockWait.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Stopwatch.GetTimestamp() > deadline)
                    return null;
                Thread.Sleep(100);
            }
        }
    }

    // Runs on the hotkey thread (Windows, X11) or the main thread (macOS): only hand the work to the thread pool.
    private static void OnPressed(string socket, ShortcutAgentConfig config)
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Exchange(ref _lastPress, now) < RepeatGuardTicks)
            return;
        if (OperatingSystem.IsWindows())
            WindowsForeground.AllowForAnyProcess(); // this process may take the foreground right now; pass that on
        ThreadPool.QueueUserWorkItem(_ => Activate(socket, config));
    }

    private static void Activate(string socket, ShortcutAgentConfig config)
    {
        if (ActivationChannel.TrySignal(socket, TimeSpan.FromSeconds(2)))
            return;

        // Navigator is starting already (a second press during its start-up): it opens a window on its own.
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastLaunch) < LaunchGuardTicks || Interlocked.Exchange(ref _launching, 1) == 1)
            return;
        try
        {
            var info = new ProcessStartInfo(config.Executable) { UseShellExecute = false };
            foreach (var argument in config.Arguments)
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            Interlocked.Exchange(ref _lastLaunch, DateTime.UtcNow.Ticks);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Navigator was moved or removed; the app rewrites the config (and the agent) on its next start.
        }
        finally
        {
            Volatile.Write(ref _launching, 0);
        }
    }
}
