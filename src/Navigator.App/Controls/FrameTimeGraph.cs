using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Navigator.App.Controls;

/// <summary>
/// Tiny bar graph of recent worst-case frame times for the status bar performance indicator. Drawn directly in
/// Render (one rectangle per sample, no child visuals) and scaled to at least one 60 Hz frame budget, which is
/// marked with a faint line; bars over the budget use <see cref="WarningBrush"/>.
/// </summary>
public sealed class FrameTimeGraph : Control
{
    public const double FrameBudgetMilliseconds = 1000.0 / 60;

    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<FrameTimeGraph, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> WarningBrushProperty = AvaloniaProperty.Register<FrameTimeGraph, IBrush?>(nameof(WarningBrush));

    public static readonly StyledProperty<IBrush?> GuideBrushProperty = AvaloniaProperty.Register<FrameTimeGraph, IBrush?>(nameof(GuideBrush));

    private readonly double[] _values = new double[Services.PerformanceMonitor.HistoryLength];
    private int _count;

    static FrameTimeGraph()
    {
        AffectsRender<FrameTimeGraph>(ForegroundProperty, WarningBrushProperty, GuideBrushProperty);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? WarningBrush
    {
        get => GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    public IBrush? GuideBrush
    {
        get => GetValue(GuideBrushProperty);
        set => SetValue(GuideBrushProperty, value);
    }

    /// <summary>Replaces the plotted values (oldest first) and redraws.</summary>
    public void SetValues(ReadOnlySpan<double> values)
    {
        _count = Math.Min(values.Length, _values.Length);
        values[^_count..].CopyTo(_values);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var scale = FrameBudgetMilliseconds;
        for (var i = 0; i < _count; i++)
            scale = Math.Max(scale, _values[i]);

        var guideY = Math.Round(height - height * FrameBudgetMilliseconds / scale) + 0.5;
        if (GuideBrush is { } guide)
            context.FillRectangle(guide, new Rect(0, guideY - 0.5, width, 1));

        if (_count == 0)
            return;

        // Newest bar at the right edge, older ones to the left; slots stay fixed so bars do not stretch while filling.
        var slot = width / _values.Length;
        var barWidth = Math.Max(1, slot - 1);
        var normal = Foreground;
        var warning = WarningBrush ?? normal;
        for (var i = 0; i < _count; i++)
        {
            var value = _values[i];
            if (value <= 0)
                continue;
            var barHeight = Math.Max(1, height * value / scale);
            var x = width - (_count - i) * slot;
            var brush = value > FrameBudgetMilliseconds ? warning : normal;
            if (brush is not null)
                context.FillRectangle(brush, new Rect(x, height - barHeight, barWidth, barHeight));
        }
    }
}
