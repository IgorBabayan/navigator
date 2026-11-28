using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Navigator.Presentation.Details;

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

    /// <summary>Security, signatures, compatibility and the folder icon are read only when their tab is first opened.</summary>
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Tabs) && Tabs.SelectedItem is TabItem { Tag: string id } && DataContext is PropertiesViewModel model)
            model.OnTabSelected(id);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
