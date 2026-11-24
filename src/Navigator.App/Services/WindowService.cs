using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Navigator.App.Views;
using Navigator.Core.Navigation;
using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Presentation.Services;
using Navigator.Presentation.Settings;
using Navigator.Presentation.SettingsPages;
using Navigator.Presentation.Shell;

namespace Navigator.App.Services;

/// <summary>Creates main windows (multi-window), restores and saves the session, and hosts the settings window.</summary>
internal sealed class WindowService(IServiceProvider services, SettingsService settings, AppDirectories directories) : IWindowService
{
    private readonly List<MainWindow> _windows = [];
    private readonly JsonFileStore<SessionState> _sessionStore = new(directories.SessionFile, SettingsJsonContext.Default.SessionState, () => new SessionState());
    private SettingsWindow? _settingsWindow;
    private bool _sessionSaved;

    public MainWindow? ActiveWindow => _windows.FirstOrDefault(w => w.IsActive) ?? _windows.LastOrDefault();

    public Window? ActiveTopLevel => _settingsWindow is { IsActive: true } ? _settingsWindow : ActiveWindow;

    public MainWindow CreateStartupWindows(string[] args)
    {
        var session = _sessionStore.Load();
        var start = settings.Current.General;

        // A folder passed on the command line ("Open with Navigator", default file manager) wins.
        var fromArgs = args.Where(a => !a.StartsWith('-')).Select(Path.GetFullPath).Where(Directory.Exists).ToList();
        if (fromArgs.Count > 0)
        {
            var window = CreateWindow(null);
            foreach (var path in fromArgs)
                window.Shell.AddTab(Location.Folder(path));
            return window;
        }

        if (start.Startup == StartupBehavior.RestoreSession && session.Windows.Count > 0)
        {
            MainWindow? first = null;
            foreach (var saved in session.Windows)
            {
                var window = CreateWindow(saved);
                window.Shell.RestoreTabs(saved.Tabs, saved.SelectedTabIndex);
                if (window.Shell.Tabs.Count == 0)
                    window.Shell.AddTab(Location.Home);
                if (first is null)
                {
                    first = window;
                    window.Shell.RestoreRecentlyClosed(session.RecentlyClosedTabs);
                }
                else
                {
                    window.Show();
                }
            }

            return first!;
        }

        var main = CreateWindow(session.Windows.FirstOrDefault());
        if (start.Startup == StartupBehavior.OpenSpecificLocations && start.StartupLocations.Count > 0)
        {
            foreach (var location in start.StartupLocations)
                main.Shell.AddTab(Location.TryParse(location) ?? (Directory.Exists(location) ? Location.Folder(location) : Location.Home));
        }

        if (main.Shell.Tabs.Count == 0)
            main.Shell.AddTab(Location.Home);
        return main;
    }

    private MainWindow CreateWindow(WindowSession? placement)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow(shell, services.GetRequiredService<PaneServices>());
        if (placement is not null)
            ApplyPlacement(window, placement);
        window.Closing += (_, _) => OnWindowClosing(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            shell.Dispose();
        };
        _windows.Add(window);
        return window;
    }

    public void OpenNewWindow(Location? location = null)
    {
        var window = CreateWindow(null);
        window.Shell.AddTab(location ?? Location.Home);
        window.Show();
    }

    public void OpenSettings(string? page = null)
    {
        if (_settingsWindow is null)
        {
            var model = services.GetRequiredService<SettingsViewModel>();
            _settingsWindow = new SettingsWindow { DataContext = model };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            if (ActiveWindow is { } owner)
                _settingsWindow.Show(owner);
            else
                _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }

        if (page is not null && _settingsWindow.DataContext is SettingsViewModel vm)
            vm.SelectedPage = vm.Pages.FirstOrDefault(p => p.Id == page) ?? vm.SelectedPage;
    }

    public void CloseCurrentWindow() => ActiveWindow?.Close();

    /// <summary>The last window closing saves the full session; earlier windows simply close.</summary>
    private void OnWindowClosing(MainWindow window)
    {
        if (_windows.Count == 1)
            SaveSession();
    }

    public void SaveSession()
    {
        if (_sessionSaved || _windows.Count == 0)
            return;
        _sessionSaved = true;
        var session = new SessionState
        {
            Windows = [.. _windows.Select(w => w.CaptureSession())],
            RecentlyClosedTabs = [.. _windows[0].Shell.RecentlyClosed],
        };
        try
        {
            _sessionStore.Save(session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The session is a convenience; losing it must not block shutdown.
        }
    }

    /// <summary>Restores size/position, keeping the window on a connected screen (monitor layouts change).</summary>
    private static void ApplyPlacement(Window window, WindowSession placement)
    {
        window.Width = Math.Max(640, placement.Width);
        window.Height = Math.Max(420, placement.Height);
        var position = new PixelPoint((int)placement.X, (int)placement.Y);
        var screens = window.Screens.All;
        if (screens.Any(s => s.WorkingArea.Contains(position + new PixelPoint(40, 40))))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = position;
        }

        if (placement.IsMaximized)
            window.WindowState = WindowState.Maximized;
    }
}
