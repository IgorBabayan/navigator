using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Navigator.App.Views;

public sealed partial class PropertiesWindow : Window
{
    public PropertiesWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
