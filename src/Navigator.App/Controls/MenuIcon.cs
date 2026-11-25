using Avalonia;
using Avalonia.Controls;

namespace Navigator.App.Controls;

/// <summary>
/// <c>c:MenuIcon.Kind="Tag"</c> on a <see cref="MenuItem"/> gives it a 16 px <see cref="IconView"/> icon (an
/// <see cref="IconCatalog"/> key or any Material icon name) without the verbose <c>MenuItem.Icon</c> element syntax.
/// </summary>
public sealed class MenuIcon : AvaloniaObject
{
    public static readonly AttachedProperty<string?> KindProperty = AvaloniaProperty.RegisterAttached<MenuIcon, MenuItem, string?>("Kind");

    static MenuIcon()
    {
        KindProperty.Changed.AddClassHandler<MenuItem>((item, change) =>
            item.Icon = change.NewValue is string { Length: > 0 } kind ? new IconView { Kind = kind, Width = 16, Height = 16 } : null);
    }

    private MenuIcon()
    {
    }

    public static string? GetKind(MenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.GetValue(KindProperty);
    }

    public static void SetKind(MenuItem item, string? value)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.SetValue(KindProperty, value);
    }
}
