using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Navigator.App.Infrastructure;
using Navigator.App.Services;
using Navigator.Core.Tags;
using Navigator.Presentation.Settings;
using Navigator.Presentation.Updates;

namespace Navigator.App;

public sealed partial class App : Application
{
    /// <summary>Upper bound for cancelled file operations and background workers to wind down when the app exits.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private ServiceProvider? _services;
    private bool _shutDown;

    public static IServiceProvider Services => ((App)Current!)._services ?? throw new InvalidOperationException("Services are not ready.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = CompositionRoot.Build();
            _services.GetRequiredService<ThemeService>().Initialize();

            var windows = _services.GetRequiredService<WindowService>();
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = windows.CreateStartupWindows(desktop.Args ?? []);
            desktop.ShutdownRequested += (_, _) => windows.SaveSession();

            var update = _services.GetRequiredService<UpdateViewModel>();
            update.ExitRequested += (_, _) => desktop.Shutdown();

            // Everything that is not needed for the first frame runs after it (startup budget, docs/PERFORMANCE.md).
            Dispatcher.UIThread.Post(() =>
            {
                var logger = _services.GetRequiredService<ILogger<App>>();
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("First frame after {Milliseconds} ms", (int)StartupTimer.Elapsed.TotalMilliseconds);
                update.StartBackgroundChecks();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Flushes state and releases services. Safe to call more than once.</summary>
    public void Shutdown()
    {
        if (_shutDown || _services is null)
            return;
        _shutDown = true;

        try
        {
            _services.GetRequiredService<TagStore>().SaveIfDirty();
            _services.GetRequiredService<SettingsService>().Dispose();
        }
        finally
        {
            DisposeServices(_services);
        }
    }

    /// <summary>
    /// Some singletons (<c>FileOperationQueue</c>, <c>BackgroundWorkScheduler</c>) are only <see cref="IAsyncDisposable"/>,
    /// so the container must be disposed asynchronously. The UI loop has already stopped here, so the dispatcher's
    /// synchronization context is cleared first (a continuation posted to it would never run), and the wait is bounded:
    /// operations are cancelled before the wait and copies are atomic, so an exit never leaves partial files.
    /// </summary>
    private static void DisposeServices(ServiceProvider services)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var disposal = services.DisposeAsync().AsTask();
            if (disposal.Wait(ShutdownTimeout))
                disposal.GetAwaiter().GetResult();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }
}
