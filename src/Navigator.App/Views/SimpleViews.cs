using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Navigator.Presentation.Shell;

namespace Navigator.App.Views;

public sealed partial class SidebarView : UserControl
{
    private SidebarItemViewModel? _pressed;

    public SidebarView() => InitializeComponent();

    public SidebarViewModel? Model => DataContext as SidebarViewModel;

    /// <summary>Menu items a plugin added to the previous menu; replaced each time a menu opens.</summary>
    private static readonly object PluginItemTag = new();

    /// <summary>Appends the entry's plugin actions (e.g. "Edit…", "Remove" for a saved server) to its context menu.</summary>
    private void OnItemMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: SidebarItemViewModel item } menu)
            return;
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is Control { Tag: var tag } && ReferenceEquals(tag, PluginItemTag))
                menu.Items.RemoveAt(i);
        }

        if (!item.HasCommands)
            return;
        menu.Items.Add(new Separator { Tag = PluginItemTag });
        foreach (var command in item.Commands)
        {
            var menuItem = new MenuItem { Header = command.Title, Command = command.Command, Tag = PluginItemTag };
            if (command.IsDestructive)
                menuItem.Classes.Add("destructive");
            menu.Items.Add(menuItem);
        }
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SidebarItemViewModel item })
            _pressed = item;
    }

    /// <summary>Left click navigates, middle click opens in a new tab; dropping a pinned item on another reorders.</summary>
    private void OnItemReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressed = _pressed;
        _pressed = null;
        if (pressed is null || Model is not { } model)
            return;

        var target = (this.InputHitTest(e.GetPosition(this)) as Control)?.DataContext as SidebarItemViewModel;
        if (target is not null && target != pressed && pressed.Path is not null && target.Path is not null)
        {
            var section = model.Sections.FirstOrDefault(s => s.IsReorderable && s.Items.Contains(target) && s.Items.Contains(pressed));
            if (section is not null)
            {
                model.MovePinned(pressed, section.Items.IndexOf(target));
                return;
            }
        }

        if (target != pressed)
            return;
        if (e.InitialPressMouseButton == MouseButton.Middle)
            model.OpenInNewTabCommand.Execute(pressed);
        else if (e.InitialPressMouseButton == MouseButton.Left)
            model.NavigateCommand.Execute(pressed);
    }
}

/// <summary>Section expanded → chevron down, collapsed → chevron right.</summary>
public sealed class ExpandGlyphConverter : IValueConverter
{
    public static ExpandGlyphConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "ChevronDown" : "ChevronRight";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}

public sealed partial class PreviewPaneView : UserControl
{
    public PreviewPaneView() => InitializeComponent();
}

public sealed partial class OperationsView : UserControl
{
    public OperationsView() => InitializeComponent();
}
