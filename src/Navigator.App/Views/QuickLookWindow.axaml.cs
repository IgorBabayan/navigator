using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Navigator.Presentation.Details;
using Navigator.Presentation.Shell;

namespace Navigator.App.Views;

/// <summary>
/// Navigator's own Quick Look window (used when no external previewer is chosen or running). It shows the shell's
/// <see cref="ShellViewModel.QuickLook"/> preview of the active pane's selection: Space or Escape closes it, the arrow
/// keys move the selection in the folder behind it (the preview follows), Enter opens the file.
/// </summary>
public sealed partial class QuickLookWindow : Window
{
    private readonly ShellViewModel? _shell;

    public QuickLookWindow()
    {
        InitializeComponent();
        // Tunnel: scroll viewers inside the window would otherwise take the arrow keys.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    public QuickLookWindow(ShellViewModel shell)
        : this()
    {
        _shell = shell;
        DataContext = shell.QuickLook;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
            return;
        var preview = DataContext as PreviewViewModel;
        var scrollsText = preview is { IsText: true } or { IsMarkdown: true } or { ShowDocumentTextOnly: true };
        int? step = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up when !scrollsText => -1,
            Key.Down when !scrollsText => 1,
            _ => null,
        };

        if (step is { } delta)
        {
            _shell?.ActivePane?.StepSelection(delta);
            e.Handled = true;
        }
        else if (e.Key is Key.Space or Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && preview?.OpenCommand.CanExecute(null) == true)
        {
            preview.OpenCommand.Execute(null);
            Close();
            e.Handled = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
