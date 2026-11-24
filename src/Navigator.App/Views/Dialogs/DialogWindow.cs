using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Navigator.Presentation.Localization;

namespace Navigator.App.Views.Dialogs;

/// <summary>
/// Content-dialog style modal window (title, message, optional extra content, right-aligned buttons, Enter/Escape).
/// Built in code: one small reusable surface for confirm, text input, conflict and error dialogs.
/// </summary>
internal sealed class DialogWindow : Window
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _body = new() { Spacing = 12 };
    private string? _result;
    private string? _defaultResult;
    private string _cancelResult = "cancel";

    public DialogWindow(string title, string? message)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");

        _body.Children.Add(new TextBlock { Text = title, Classes = { "title" }, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(message))
            _body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "body" } });

        var footer = new Border { Classes = { "dialog-footer" }, Child = _buttons, Padding = new Thickness(24, 16) };
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new Border { Padding = new Thickness(24, 24, 24, 16), Child = _body });
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(_cancelResult);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _defaultResult is not null && FocusManager?.GetFocusedElement() is not Button)
            {
                Close(_defaultResult);
                e.Handled = true;
            }
        };
        Closing += (_, _) => _result ??= _cancelResult;
    }

    public static Localizer Text => Localizer.Current;

    public void AddContent(Control control) => _body.Children.Add(control);

    public Button AddButton(string text, string result, bool isDefault = false, bool isDestructive = false, bool isCancel = false)
    {
        var button = new Button { Content = text, MinWidth = 96 };
        if (isDefault)
        {
            button.Classes.Add(isDestructive ? "danger" : "accent");
            _defaultResult = result;
        }

        if (isCancel)
            _cancelResult = result;
        button.Click += (_, _) =>
        {
            _result = result;
            Close(result);
        };
        _buttons.Children.Add(button);
        return button;
    }

    public async Task<string> ShowFor(Window? owner)
    {
        if (owner is null)
        {
            var completion = new TaskCompletionSource<string>();
            Closed += (_, _) => completion.TrySetResult(_result ?? _cancelResult);
            Show();
            return await completion.Task.ConfigureAwait(true);
        }

        return await ShowDialog<string?>(owner).ConfigureAwait(true) ?? _cancelResult;
    }
}
