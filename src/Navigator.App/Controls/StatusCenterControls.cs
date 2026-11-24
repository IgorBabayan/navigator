using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Navigator.App.Controls;

/// <summary>
/// Circular progress indicator for the toolbar status-center button. Drawn in Render (one track circle and one arc,
/// no template or child visuals). When <see cref="IsIndeterminate"/> is set, a quarter arc spins using
/// TopLevel animation frames — the loop runs only while the ring is indeterminate, attached and actually rendered,
/// and is skipped entirely when motion is turned off.
/// </summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));

    public static readonly StyledProperty<bool> IsIndeterminateProperty = AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 2);

    private const double SpinDegreesPerMillisecond = 0.36;
    private const double SpinSweepDegrees = 100;

    private IPen? _arcPen;
    private IPen? _trackPen;
    private double _spinAngle;
    private bool _spinning;

    static ProgressRing()
    {
        AffectsRender<ProgressRing>(ValueProperty, IsIndeterminateProperty, ForegroundProperty, TrackBrushProperty, ThicknessProperty);
        WidthProperty.OverrideDefaultValue<ProgressRing>(16);
        HeightProperty.OverrideDefaultValue<ProgressRing>(16);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForegroundProperty || change.Property == ThicknessProperty)
            _arcPen = null;
        if (change.Property == TrackBrushProperty || change.Property == ThicknessProperty)
            _trackPen = null;
    }

    public override void Render(DrawingContext context)
    {
        var thickness = Thickness;
        var radius = (Math.Min(Bounds.Width, Bounds.Height) - thickness) / 2;
        if (radius <= 0)
            return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (TrackBrush is { } track)
        {
            _trackPen ??= new ImmutablePen(track.ToImmutable(), thickness);
            context.DrawEllipse(null, _trackPen, center, radius, radius);
        }

        if (Foreground is not { } foreground)
            return;
        _arcPen ??= new ImmutablePen(foreground.ToImmutable(), thickness, lineCap: PenLineCap.Round);

        if (IsIndeterminate)
        {
            DrawArc(context, _arcPen, center, radius, _spinAngle, SpinSweepDegrees);
            StartSpin();
            return;
        }

        var sweep = Math.Clamp(Value, 0, 100) * 3.6;
        if (sweep >= 359.5)
            context.DrawEllipse(null, _arcPen, center, radius, radius);
        else if (sweep > 0.5)
            DrawArc(context, _arcPen, center, radius, -90, sweep);
    }

    private static void DrawArc(DrawingContext context, IPen pen, Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var start = PointOnCircle(center, radius, startDegrees);
        var end = PointOnCircle(center, radius, startDegrees + sweepDegrees);
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(start, false);
            sink.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
            sink.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }

    private void StartSpin()
    {
        if (_spinning || !MotionEnabled())
            return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;
        _spinning = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        _spinning = false;
        if (!IsIndeterminate || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is null)
            return;
        _spinAngle = (time.TotalMilliseconds * SpinDegreesPerMillisecond) % 360;
        InvalidateVisual(); // Render requests the next frame, so a hidden or detached ring stops by itself.
    }

    private static bool MotionEnabled()
        => Application.Current is not { } app || !app.TryGetResource("Motion.Enabled", null, out var enabled) || enabled is not false;
}

/// <summary>
/// Throughput sparkline for an expanded status-center card. The samples list is owned by the view model; the view
/// redraws only when <see cref="Revision"/> changes (a couple of times per second while a transfer runs).
/// Newest sample is at the right edge; slots are fixed so the line scrolls instead of stretching.
/// </summary>
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty = AvaloniaProperty.Register<SpeedGraph, IReadOnlyList<double>?>(nameof(Samples));

    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<SpeedGraph, int>(nameof(Revision));

    public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<SpeedGraph, int>(nameof(Capacity), 60);

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> BaselineBrushProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(BaselineBrush));

    private IPen? _strokePen;

    static SpeedGraph()
    {
        AffectsRender<SpeedGraph>(SamplesProperty, RevisionProperty, CapacityProperty, StrokeProperty, FillProperty, BaselineBrushProperty);
    }

    public IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StrokeProperty)
            _strokePen = null;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        if (BaselineBrush is { } baseline)
            context.FillRectangle(baseline, new Rect(0, height - 1, width, 1));

        var samples = Samples;
        var count = samples is null ? 0 : Math.Min(samples.Count, Math.Max(2, Capacity));
        if (samples is null || count < 2)
            return;

        var first = samples.Count - count;
        var peak = 0.0;
        for (var i = first; i < samples.Count; i++)
            peak = Math.Max(peak, samples[i]);
        if (peak <= 0)
            return;

        // 12 % headroom keeps the peak off the top edge; 1 px keeps the line inside the bounds.
        var scale = (height - 1) / (peak * 1.12);
        var step = width / (Math.Max(2, Capacity) - 1);
        var startX = width - ((count - 1) * step);

        var line = new StreamGeometry();
        using (var sink = line.Open())
        {
            sink.BeginFigure(new Point(startX, height - (samples[first] * scale)), false);
            for (var i = 1; i < count; i++)
                sink.LineTo(new Point(startX + (i * step), height - (samples[first + i] * scale)));
            sink.EndFigure(false);
        }

        if (Fill is { } fill)
        {
            var area = new StreamGeometry();
            using (var sink = area.Open())
            {
                sink.BeginFigure(new Point(startX, height), true);
                for (var i = 0; i < count; i++)
                    sink.LineTo(new Point(startX + (i * step), height - (samples[first + i] * scale)));
                sink.LineTo(new Point(width, height));
                sink.EndFigure(true);
            }

            context.DrawGeometry(fill, null, area);
        }

        if (Stroke is { } stroke)
        {
            _strokePen ??= new ImmutablePen(stroke.ToImmutable(), 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, _strokePen, line);
        }
    }
}
