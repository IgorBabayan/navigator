using Avalonia;
using Avalonia.Controls;

namespace Navigator.App.Controls;

/// <summary>
/// Lays out the cells of a details row (and the column header) left to right in the user's column order. Each cell
/// names its column with <see cref="CellProperty"/>; cells without one (the trash's original-location text) follow at
/// the end in markup order. Cells have fixed widths, so measuring is one pass with no allocation, and a new order only
/// re-arranges the existing children: reordering columns never rebuilds rows or their bindings.
/// </summary>
public sealed class DetailsCellsPanel : Panel
{
    public static readonly StyledProperty<IReadOnlyList<string>?> OrderProperty =
        AvaloniaProperty.Register<DetailsCellsPanel, IReadOnlyList<string>?>(nameof(Order));

    public static readonly AttachedProperty<string?> CellProperty =
        AvaloniaProperty.RegisterAttached<DetailsCellsPanel, Control, string?>("Cell");

    static DetailsCellsPanel()
    {
        AffectsMeasure<DetailsCellsPanel>(OrderProperty);
        AffectsParentMeasure<DetailsCellsPanel>(CellProperty);
    }

    public IReadOnlyList<string>? Order
    {
        get => GetValue(OrderProperty);
        set => SetValue(OrderProperty, value);
    }

    public static string? GetCell(Control control) => control.GetValue(CellProperty);

    public static void SetCell(Control control, string? value) => control.SetValue(CellProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Hidden cells measure to zero, so they take no room.
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        double width = 0, tallest = 0;
        foreach (var child in Children)
        {
            child.Measure(unbounded);
            width += child.DesiredSize.Width;
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }

        return new Size(width, tallest);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        var order = Order;
        if (order is not null)
        {
            foreach (var id in order)
            {
                foreach (var child in Children)
                {
                    if (string.Equals(GetCell(child), id, StringComparison.Ordinal))
                        x = Place(child, x, finalSize.Height);
                }
            }
        }

        foreach (var child in Children)
        {
            if (GetCell(child) is not { } id || order is null || !Contains(order, id))
                x = Place(child, x, finalSize.Height);
        }

        return finalSize;

        static bool Contains(IReadOnlyList<string> ids, string id)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                if (string.Equals(ids[i], id, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

    private static double Place(Control child, double x, double height)
    {
        if (!child.IsVisible)
            return x;
        var width = child.DesiredSize.Width;
        child.Arrange(new Rect(x, 0, width, height));
        return x + width;
    }
}
