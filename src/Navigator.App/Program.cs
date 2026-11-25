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

        // One Navigator per user: a second launch (shortcut, executable, "Open with", "--activate" from a desktop
        // shortcut) brings the running instance to the front, handing it any folders, and ends here.
        if (!Services.SingleInstance.Claim(args))
            return 0;

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
            Services.SingleInstance.Release();
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
