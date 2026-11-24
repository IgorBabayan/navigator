using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Navigator.App.Controls;

/// <summary>
/// Draws a catalog icon directly in Render: no child visuals, no template, no resource lookups per instance —
/// the cheapest possible icon for virtualized lists. Filled layers use their own colors; the outline uses Foreground.
/// </summary>
public sealed class IconView : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();

    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<IconView, double>(nameof(StrokeThickness), 1.6);

    static IconView()
    {
        AffectsRender<IconView>(KindProperty, ForegroundProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<IconView>(16);
        HeightProperty.OverrideDefaultValue<IconView>(16);
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var icon = IconCatalog.Get(Kind);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (icon is null || size <= 0)
            return;

        var scale = size / 24.0;
        var offset = new Vector((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            foreach (var (geometry, brush) in icon.Fills)
                context.DrawGeometry(brush, null, geometry);

            if (icon.Stroke is { } stroke)
            {
                var foreground = icon.Fills.Count > 0 ? Brushes.White : Foreground ?? Brushes.Gray;
                var pen = new Pen(foreground, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                context.DrawGeometry(null, pen, stroke);
            }
        }
    }
}
