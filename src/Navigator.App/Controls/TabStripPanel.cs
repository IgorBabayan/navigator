using Avalonia;
using Avalonia.Controls;

namespace Navigator.App.Controls;

/// <summary>
/// Lays tabs out in one row, all of the same width: <see cref="ItemWidth"/> while they fit, then shrinking
/// equally down to <see cref="MinItemWidth"/> as more tabs are opened, so a short title never makes a narrow tab.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public static readonly StyledProperty<double> ItemWidthProperty = AvaloniaProperty.Register<TabStripPanel, double>(nameof(ItemWidth), 240);

    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<TabStripPanel, double>(nameof(MinItemWidth), 100);

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<TabStripPanel, double>(nameof(Spacing));

    static TabStripPanel()
    {
        AffectsMeasure<TabStripPanel>(ItemWidthProperty, MinItemWidthProperty, SpacingProperty);
    }

    /// <summary>Width of every tab while there is room for all of them.</summary>
    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>Tabs never shrink below this width; past it the strip is clipped.</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = VisibleCount();
        if (count == 0)
            return default;

        var width = ItemWidthFor(availableSize.Width, count);
        var height = 0d;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size((width * count) + (Spacing * (count - 1)), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = VisibleCount();
        if (count == 0)
            return finalSize;

        var width = ItemWidthFor(finalSize.Width, count);
        var x = 0d;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + Spacing;
        }

        return finalSize;
    }

    private double ItemWidthFor(double available, int count)
    {
        var max = Math.Max(ItemWidth, MinItemWidth);
        if (double.IsInfinity(available) || double.IsNaN(available))
            return max;
        var fit = (available - (Spacing * (count - 1))) / count;
        return Math.Clamp(fit, MinItemWidth, max);
    }

    private int VisibleCount()
    {
        var count = 0;
        foreach (var child in Children)
        {
            if (child.IsVisible)
                count++;
        }

        return count;
    }
}
