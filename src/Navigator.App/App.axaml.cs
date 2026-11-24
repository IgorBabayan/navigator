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
            _services.Dispose();
        }
    }
}
