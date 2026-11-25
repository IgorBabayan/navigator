using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Navigator.Core.Platform;
using Navigator.Presentation.Commands;
using Navigator.Presentation.Services;
using Navigator.Presentation.Settings;

namespace Navigator.App.Services;

/// <summary>
/// Keeps the global "open Navigator" shortcut (<see cref="CommandRegistry.ActivateAppId"/>) working as Settings asks.
/// Only the first assigned gesture is used. Three ways, chosen per platform and setting:
/// <list type="bullet">
/// <item><b>Agent</b> (Settings → General → "Works when Navigator isn't running", Windows, macOS, Linux on X11): a tiny
/// background process (<see cref="ShortcutAgentHost"/>) started at sign-in holds the key and activates or starts
/// Navigator. The app itself then registers nothing; the agent reaches it through the <see cref="ActivationChannel"/>.</item>
/// <item><b>Desktop</b> (GNOME on Wayland, where apps cannot grab keys): a GNOME custom shortcut runs
/// <c>Navigator --activate</c>.</item>
/// <item><b>In process</b> (setting off, or neither of the above): the running app registers the key itself.</item>
/// </list>
/// Started after the first frame; re-applies only when the gesture or the setting changes. The app always listens on
/// the activation channel (one pending accept), so <c>--activate</c> and the agent reach the running instance.
/// </summary>
internal sealed class GlobalHotkeyController : IGlobalHotkeyStatus, IDisposable
{
    private static readonly TimeSpan AgentStatusTimeout = TimeSpan.FromSeconds(4);

    private readonly IGlobalHotkeyService _hotkey;
    private readonly SettingsService _settings;
    private readonly WindowService _windows;
    private readonly ILogger<GlobalHotkeyController> _logger;
    private readonly ShortcutAgentFiles _files;
    private readonly ILoginItemService _loginItems;
    private readonly IDesktopShortcutService _desktop;
    private readonly bool _agentSupported;
    private readonly string _socketPath;
    private IDisposable? _listener;
    private bool _started;
    private bool _disposed;
    private (string? Gesture, bool WhenClosed)? _applied;
    private int _version;

    public GlobalHotkeyController(IGlobalHotkeyService hotkey, SettingsService settings, WindowService windows, ILogger<GlobalHotkeyController> logger,
        AppDirectories directories, ILoginItemService loginItems, IDesktopShortcutService desktop, IPlatformInfo platform)
    {
        _hotkey = hotkey;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _files = new ShortcutAgentFiles(directories);
        _loginItems = loginItems;
        _desktop = desktop;
        _agentSupported = loginItems.IsSupported && Navigator.Platform.PlatformServices.SupportsShortcutAgent(platform);
        _socketPath = ActivationChannel.SocketPath(directories);
    }

    public HotkeyRegistrationResult? State { get; private set; }

    public string Gesture { get; private set; } = string.Empty;

    public bool WorksWhenClosed { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Must be called on the UI thread (macOS registers on the main run loop).</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        _settings.Changed += OnSettingsChanged;
        _ = ListenAsync();
        Apply(force: true);
    }

    /// <summary>Before an update replaces the program files: the agent must not keep them in use. The next start restores it.</summary>
    public void StopAgentForUpdate() => _files.DeleteConfig();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _hotkey.Unregister();
        _listener?.Dispose();

        // The agent and a desktop shortcut deliberately outlive the app: that is their purpose.
    }

    private async Task ListenAsync()
    {
        try
        {
            var listener = await Task.Run(() => ActivationChannel.Listen(_socketPath, folders => Dispatcher.UIThread.Post(() => Activate(folders)))).ConfigureAwait(true);
            if (_disposed)
                listener?.Dispose();
            else
                _listener = listener;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Activation channel unavailable; the global shortcut works only while Navigator itself holds it");
        }
    }

    private void OnSettingsChanged(object? sender, string section)
    {
        if (section is "keyBindings" or "general" or "*")
            Dispatcher.UIThread.Post(() => Apply(force: false));
    }

    private void Apply(bool force)
    {
        if (_disposed)
            return;
        var gestures = CommandRegistry.GesturesFor(CommandRegistry.ActivateAppId, _settings.Current, OperatingSystem.IsMacOS());
        var gesture = gestures.Count > 0 ? gestures[0] : null;
        var whenClosed = _settings.Current.General.GlobalShortcutWhenClosed;
        if (!force && _applied is { } applied && string.Equals(applied.Gesture, gesture, StringComparison.OrdinalIgnoreCase) && applied.WhenClosed == whenClosed)
            return;
        _applied = (gesture, whenClosed);
        var version = ++_version;

        _hotkey.Unregister();
        WorksWhenClosed = false;
        if (gesture is null)
        {
            State = null;
            Gesture = string.Empty;
            _ = StopBackgroundShortcutsAsync(desktopToo: true);
            Raise();
            return;
        }

        Gesture = CommandRegistry.FormatForDisplay(gesture);
        if (!HotkeyGesture.TryParse(gesture, out var parsed))
        {
            _ = StopBackgroundShortcutsAsync(desktopToo: true);
            Report(HotkeyRegistrationResult.InvalidGesture, worksWhenClosed: false);
            return;
        }

        if (whenClosed && _agentSupported)
        {
            State = null; // pending until the agent reports
            _ = StartAgentAsync(gesture, version);
            return;
        }

        if (whenClosed && _desktop.IsSupported)
        {
            _ = StopBackgroundShortcutsAsync(desktopToo: false);
            _ = RegisterDesktopShortcutAsync(parsed, version);
            return;
        }

        _ = StopBackgroundShortcutsAsync(desktopToo: true);
        RegisterInProcess(parsed);
    }

    private void RegisterInProcess(HotkeyGesture gesture)
        => Report(_hotkey.IsSupported ? _hotkey.Register(gesture, OnPressed) : HotkeyRegistrationResult.Unsupported, worksWhenClosed: false);

    /// <summary>
    /// Writes the agent's config, registers it as a login item and (re)starts it unless the running agent already holds
    /// this gesture, then waits briefly for its registration result. Without an answer the app holds the key itself.
    /// </summary>
    private async Task StartAgentAsync(string gesture, int version)
    {
        HotkeyRegistrationResult? result;
        try
        {
            result = await Task.Run(async () =>
            {
                var (executable, prefix) = LaunchCommand();
                var config = new ShortcutAgentConfig { Gesture = gesture, Executable = executable, Arguments = [.. prefix] };
                var status = _files.ReadStatus();
                var changed = _files.WriteConfig(config);
                List<string> agentArguments = [.. prefix, ShortcutAgentFiles.AgentSwitch];
                if (!_loginItems.Register(executable, agentArguments))
                    _logger.LogWarning("The global shortcut agent could not be added to the login items");
                if (!changed && status is { Result: HotkeyRegistrationResult.Registered } && status.Gesture == gesture && IsAgentAlive(status.ProcessId))
                    return HotkeyRegistrationResult.Registered;

                if (changed)
                    await Task.Delay(150).ConfigureAwait(false); // the previous agent notices the new config and exits
                _files.DeleteStatus();
                StartAgentProcess(executable, agentArguments);
                return await WaitForAgentAsync(gesture).ConfigureAwait(false);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "The global shortcut agent could not be started");
            result = null;
        }

        if (_disposed || version != _version)
            return;
        if (result is HotkeyRegistrationResult.Registered)
        {
            Report(HotkeyRegistrationResult.Registered, worksWhenClosed: true);
        }
        else if (result is HotkeyRegistrationResult.InUse or HotkeyRegistrationResult.InvalidGesture)
        {
            Report(result.Value, worksWhenClosed: false);
        }
        else if (HotkeyGesture.TryParse(gesture, out var parsed))
        {
            // No agent answered: hold the key in this process so it works at least while Navigator runs.
            _files.DeleteConfig();
            RegisterInProcess(parsed);
        }
    }

    private async Task<HotkeyRegistrationResult?> WaitForAgentAsync(string gesture)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < AgentStatusTimeout)
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (_files.ReadStatus() is { } status && status.Gesture == gesture)
                return status.Result;
        }

        return null;
    }

    private async Task RegisterDesktopShortcutAsync(HotkeyGesture gesture, int version)
    {
        HotkeyRegistrationResult result;
        try
        {
            var (executable, prefix) = LaunchCommand();
            result = await _desktop.RegisterAsync(gesture, executable, [.. prefix, ShortcutAgentFiles.ActivateSwitch]).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "The desktop shortcut could not be registered");
            result = HotkeyRegistrationResult.Unsupported;
        }

        if (!_disposed && version == _version)
            Report(result, worksWhenClosed: result == HotkeyRegistrationResult.Registered);
    }

    private async Task StopBackgroundShortcutsAsync(bool desktopToo)
    {
        try
        {
            await Task.Run(() =>
            {
                _files.DeleteConfig(); // a running agent exits
                _files.DeleteStatus();
                _loginItems.Unregister();
            }).ConfigureAwait(false);
            if (desktopToo && _desktop.IsSupported)
                await _desktop.UnregisterAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Background shortcut could not be removed");
        }
    }

    private void Report(HotkeyRegistrationResult result, bool worksWhenClosed)
    {
        State = result;
        WorksWhenClosed = worksWhenClosed;
        if (result != HotkeyRegistrationResult.Registered && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("Global shortcut {Gesture} not registered: {Result}", Gesture, result);
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// How to start this Navigator again: the AppImage (its mount point changes every run), the dotnet host with the
    /// app's assembly (development), or the executable itself.
    /// </summary>
    private static (string Executable, IReadOnlyList<string> Prefix) LaunchCommand()
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage)
            return (appImage, []);
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("The program path is unknown.");
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } assembly)
            return (process, [assembly]);
        return (process, []);
    }

    private static void StartAgentProcess(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        // The agent idles for days: no background GC thread.
        info.Environment["DOTNET_gcConcurrent"] = "0";
        using var process = Process.Start(info);
    }

    private static bool IsAgentAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var self = Process.GetCurrentProcess();
            return !process.HasExited && string.Equals(process.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void Activate()
    {
        if (!_disposed)
            _windows.ShowOrActivate();
    }

    // A second launch of the app (or the agent) reached this instance; it may pass folders to open.
    private void Activate(IReadOnlyList<string> folders)
    {
        if (_disposed)
            return;
        if (folders.Count == 0)
            _windows.ShowOrActivate();
        else
            _windows.OpenFolders(folders);
    }

    // Called on the hotkey thread (Windows, X11) or the main thread (macOS).
    private void OnPressed() => Dispatcher.UIThread.Post(Activate);
}
