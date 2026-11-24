using Avalonia;

namespace Navigator.App;

internal static class Program
{
    // No Avalonia or synchronization-context dependent code may run before AppMain.
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTimer.Start();
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
