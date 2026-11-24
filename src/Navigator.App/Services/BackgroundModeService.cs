using System.Runtime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Settings;

namespace Navigator.App.Services;

/// <summary>
/// Settings → General → "Keep running in the background": the process stays alive after the last window closes (with
/// a notification-area icon to reopen or exit), so the global shortcut opens a window instantly. Off by default; when
/// off, nothing here is created. When the last window closes in background mode the managed heap is compacted once so
/// the idle process holds as little memory as possible.
/// </summary>
internal sealed class BackgroundModeService(SettingsService settings, WindowService windows, Localizer localizer) : IDisposable
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _tray;
    private bool _disposed;

    public void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_desktop is not null || _disposed)
            return;
        _desktop = desktop;
        settings.Changed += OnSettingsChanged;
        windows.LastWindowClosed += OnLastWindowClosed;

        // The shutdown mode must be right before any window can close; the tray icon can wait for the first frame.
        desktop.ShutdownMode = IsEnabled ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnLastWindowClose;
        Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        settings.Changed -= OnSettingsChanged;
        windows.LastWindowClosed -= OnLastWindowClosed;
    }

    private bool IsEnabled => settings.Current.General.KeepRunningInBackground;

    private void OnSettingsChanged(object? sender, string section)
    {
        if (section is "general" or "*")
            Dispatcher.UIThread.Post(Apply);
    }

    private void Apply()
    {
        if (_desktop is null || _disposed)
            return;
        var enabled = IsEnabled;
        _desktop.ShutdownMode = enabled ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnLastWindowClose;
        if (enabled && _tray is null)
            ShowTrayIcon();
        else if (!enabled && _tray is not null)
            HideTrayIcon();
    }

    private void ShowTrayIcon()
    {
        var open = new NativeMenuItem(localizer["Tray.Open"]);
        open.Click += (_, _) => windows.ShowOrActivate();
        var exit = new NativeMenuItem(localizer["Tray.Exit"]);
        exit.Click += (_, _) => Exit();

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        // Windows draws .ico best in the notification area; other platforms get the PNG.
        var asset = OperatingSystem.IsWindows() ? "navigator.ico" : "navigator-256.png";
        using var stream = AssetLoader.Open(new Uri("avares://Navigator/Assets/" + asset));
        var tray = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = "Navigator",
            Menu = menu,
            IsVisible = true,
        };
        tray.Clicked += (_, _) => windows.ShowOrActivate();
        TrayIcon.SetIcons(Application.Current!, [tray]);
        _tray = tray;
    }

    private void HideTrayIcon()
    {
        TrayIcon.SetIcons(Application.Current!, []);
        _tray = null;
    }

    private void Exit()
    {
        windows.SaveSession();
        _desktop?.Shutdown();
    }

    private void OnLastWindowClosed(object? sender, EventArgs e)
    {
        if (!IsEnabled)
            return;

        // Once, after the windows' visuals are gone: return freed memory to the OS while Navigator idles.
        Dispatcher.UIThread.Post(() =>
        {
            if (windows.WindowCount != 0)
                return;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }, DispatcherPriority.ContextIdle);
    }
}
