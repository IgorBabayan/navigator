using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Navigator.Core.Platform;
using Navigator.Presentation.Commands;
using Navigator.Presentation.Services;
using Navigator.Presentation.Settings;

namespace Navigator.App.Services;

/// <summary>
/// Keeps the OS registration of the global "open Navigator" shortcut (<see cref="CommandRegistry.ActivateAppId"/>) in
/// sync with Settings → Keyboard shortcuts. Only the first assigned gesture is registered. Started after the first frame,
/// so it never delays startup; re-registers only when that gesture actually changes.
/// </summary>
internal sealed class GlobalHotkeyController(IGlobalHotkeyService hotkey, SettingsService settings, WindowService windows, ILogger<GlobalHotkeyController> logger)
    : IGlobalHotkeyStatus, IDisposable
{
    private bool _started;
    private bool _disposed;
    private string? _appliedGesture;

    public HotkeyRegistrationResult? State { get; private set; }

    public string Gesture { get; private set; } = string.Empty;

    public event EventHandler? Changed;

    /// <summary>Must be called on the UI thread (macOS registers on the main run loop).</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        settings.Changed += OnSettingsChanged;
        Apply(force: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        settings.Changed -= OnSettingsChanged;
        hotkey.Unregister();
    }

    private void OnSettingsChanged(object? sender, string section)
    {
        if (section is "keyBindings" or "*")
            Dispatcher.UIThread.Post(() => Apply(force: false));
    }

    private void Apply(bool force)
    {
        if (_disposed)
            return;
        var gestures = CommandRegistry.GesturesFor(CommandRegistry.ActivateAppId, settings.Current, OperatingSystem.IsMacOS());
        var gesture = gestures.Count > 0 ? gestures[0] : null;
        if (!force && string.Equals(gesture, _appliedGesture, StringComparison.OrdinalIgnoreCase))
            return;
        _appliedGesture = gesture;

        hotkey.Unregister();
        if (gesture is null)
        {
            State = null;
            Gesture = string.Empty;
        }
        else
        {
            Gesture = CommandRegistry.FormatForDisplay(gesture);
            State = !hotkey.IsSupported ? HotkeyRegistrationResult.Unsupported
                : HotkeyGesture.TryParse(gesture, out var parsed) ? hotkey.Register(parsed, OnPressed)
                : HotkeyRegistrationResult.InvalidGesture;
            if (State != HotkeyRegistrationResult.Registered && logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("Global shortcut {Gesture} not registered: {Result}", Gesture, State);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Called on the hotkey thread (Windows, X11) or the main thread (macOS).
    private void OnPressed() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
            windows.ShowOrActivate();
    });
}
