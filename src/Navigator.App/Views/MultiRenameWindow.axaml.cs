using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Panes;

namespace Navigator.App.Views;

/// <summary>Multi-rename dialog; see <see cref="MultiRenameViewModel"/>. <see cref="Confirmed"/> tells how it was closed.</summary>
public sealed partial class MultiRenameWindow : Window
{
    private static readonly (string Key, string Token)[] Placeholders =
    [
        ("MultiRename.Placeholder.Name", "[N]"),
        ("MultiRename.Placeholder.NameRange", "[N1-5]"),
        ("MultiRename.Placeholder.Extension", "[E]"),
        ("MultiRename.Placeholder.Counter", "[C]"),
        ("MultiRename.Placeholder.Date", "[Y]-[M]-[D]"),
        ("MultiRename.Placeholder.Time", "[h].[m].[s]"),
        ("MultiRename.Placeholder.Parent", "[P]"),
        ("MultiRename.Placeholder.Upper", "[U]"),
        ("MultiRename.Placeholder.Lower", "[L]"),
        ("MultiRename.Placeholder.Words", "[F]"),
    ];

    /// <summary>The mask box a placeholder goes into: the one that had focus last.</summary>
    private TextBox _target;

    public MultiRenameWindow()
    {
        InitializeComponent();
        _target = NameMaskBox;
        NameMaskBox.GotFocus += (_, _) => _target = NameMaskBox;
        ExtensionMaskBox.GotFocus += (_, _) => _target = ExtensionMaskBox;
        KeyDown += OnWindowKeyDown;
        Opened += (_, _) =>
        {
            NameMaskBox.Focus();
            NameMaskBox.SelectAll();
        };
    }

    public bool Confirmed { get; private set; }

    private MultiRenameViewModel? Model => DataContext as MultiRenameViewModel;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is not Button && Model?.CanRename == true)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void OnInsertClick(object? sender, RoutedEventArgs e)
    {
        var text = Localizer.Current;
        var flyout = new MenuFlyout();
        foreach (var (key, token) in Placeholders)
        {
            var entry = new MenuItem { Header = text[key] };
            entry.Click += (_, _) => Insert(token);
            flyout.Items.Add(entry);
        }

        flyout.ShowAt(InsertButton);
    }

    /// <summary>Puts <paramref name="token"/> at the caret of the last focused mask (replacing a selection).</summary>
    private void Insert(string token)
    {
        var box = _target;
        var current = box.Text ?? string.Empty;
        var start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        box.Text = string.Concat(current.AsSpan(0, start), token, current.AsSpan(end));
        box.Focus();
        box.SelectionStart = box.SelectionEnd = start + token.Length;
        box.CaretIndex = start + token.Length;
    }

    private void OnReset(object? sender, RoutedEventArgs e) => Model?.Reset();

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if (Model?.CanRename == true)
            Confirm();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void Confirm()
    {
        Confirmed = true;
        Close(true);
    }
}
