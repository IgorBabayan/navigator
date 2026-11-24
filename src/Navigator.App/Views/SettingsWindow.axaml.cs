using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Navigator.Presentation.SettingsPages;

namespace Navigator.App.Views;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Tunnelling so a shortcut being recorded (even Esc, Tab or Ctrl+W) never reaches focus navigation or the window.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Closed += (_, _) => (DataContext as SettingsViewModel)?.CancelKeyBindingRecording();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not SettingsViewModel vm)
            return;
        vm.GestureValidator = IsParseableGesture;
        vm.FontFamilyProvider = static () => FontManager.Current.SystemFonts.Select(f => f.Name);
    }

    private static bool IsParseableGesture(string gesture)
    {
        try
        {
            KeyGesture.Parse(gesture.Replace("Cmd", "Meta", StringComparison.Ordinal));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        if (vm.IsRecordingKeyBinding)
        {
            e.Handled = true;
            if (IsModifierOnly(e.Key))
                return; // wait for the actual key of the combination
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
                vm.CancelKeyBindingRecording();
            else
                vm.CompleteKeyBindingRecording(ToGestureText(e.Key, e.KeyModifiers));
            return;
        }

        // Ctrl/Cmd+F on the shortcuts page jumps to its search box.
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.F && e.KeyModifiers == primary && vm.SelectedPage.Id == "keyboard")
        {
            KeyBindingSearch.Focus();
            KeyBindingSearch.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Enter in a shortcut text box saves it immediately (the binding otherwise updates on focus loss).</summary>
    private void OnGestureBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: KeyBindingRowViewModel row } box)
            return;
        row.Gestures = box.Text ?? string.Empty;
        e.Handled = true;
    }

    private static bool IsModifierOnly(Key key)
        => key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed;

    /// <summary>Builds "Primary+Shift+T"-style text; the platform's main modifier is stored as "Primary".</summary>
    private static string ToGestureText(Key key, KeyModifiers modifiers)
    {
        var isMac = OperatingSystem.IsMacOS();
        var builder = new System.Text.StringBuilder(32);
        if (modifiers.HasFlag(isMac ? KeyModifiers.Meta : KeyModifiers.Control))
            builder.Append("Primary+");
        if (isMac && modifiers.HasFlag(KeyModifiers.Control))
            builder.Append("Ctrl+");
        if (modifiers.HasFlag(KeyModifiers.Shift))
            builder.Append("Shift+");
        if (modifiers.HasFlag(KeyModifiers.Alt))
            builder.Append("Alt+");
        if (!isMac && modifiers.HasFlag(KeyModifiers.Meta))
            builder.Append("Meta+");
        return builder.Append(key.ToString()).ToString();
    }
}
