using Avalonia;

namespace Navigator.App;

internal static class Program
{
    // No Avalonia or synchronization-context dependent code may run before AppMain.
    [STAThread]
    public static int Main(string[] args)
    {
        // The global shortcut agent never touches Avalonia: it only holds the system-wide key (Services/ShortcutAgentHost).
        if (args.Length > 0 && args[0] == Navigator.Core.Platform.ShortcutAgentFiles.AgentSwitch)
            return Services.ShortcutAgentHost.Run();

        // "--activate" (desktop shortcuts, e.g. GNOME on Wayland): bring a running Navigator to the front, or start normally.
        if (Array.IndexOf(args, Navigator.Core.Platform.ShortcutAgentFiles.ActivateSwitch) >= 0)
        {
            var socket = Navigator.Core.Platform.ActivationChannel.SocketPath(Navigator.Core.Platform.AppDirectories.Resolve());
            if (Navigator.Core.Platform.ActivationChannel.TrySignal(socket, TimeSpan.FromSeconds(2)))
                return 0;
        }

        StartupTimer.Start();

        // Lets Avalonia publish its layout/render timing metrics ("Avalonia.Diagnostic.Meter"), read by the status bar
        // performance indicator. Must be set before any Avalonia type is used. Without a listener (indicator off)
        // Avalonia does not even start the timers, so this costs nothing in normal use.
        AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            (Application.Current as App)?.Shutdown();
        }
    }

    // Used by the visual designer as well.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        if (OperatingSystem.IsLinux())
            builder = builder.With(new X11PlatformOptions { WmClass = "navigator" });

        return builder;
    }
}

/// <summary>Measures time from process start to the first rendered frame of the main window (docs/PERFORMANCE.md).</summary>
internal static class StartupTimer
{
    private static long _started;

    public static void Start() => _started = System.Diagnostics.Stopwatch.GetTimestamp();

    public static TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_started);
}
