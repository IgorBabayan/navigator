using Avalonia;

namespace Navigator.App;

internal static class Program
{
    // No Avalonia or synchronization-context dependent code may run before AppMain.
    [STAThread]
    public static int Main(string[] args)
    {
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
