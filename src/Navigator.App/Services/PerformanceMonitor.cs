using System.Diagnostics;
using System.Diagnostics.Metrics;
using Avalonia.Threading;

namespace Navigator.App.Services;

/// <summary>Average and worst duration of one kind of pass during a sample interval, and how often it ran.</summary>
public readonly record struct PassStatistics(double AverageMilliseconds, double MaxMilliseconds, int Count);

/// <summary>One sample of the performance indicator (Settings → Performance → "Show performance diagnostics").</summary>
public sealed record PerformanceSample(
    double FramesPerSecond,
    PassStatistics Frame,
    PassStatistics CompositorRender,
    PassStatistics CompositorUpdate,
    PassStatistics Layout,
    PassStatistics RenderRecording,
    PassStatistics Input,
    long VisualCount,
    long ManagedBytes,
    long HeapCommittedBytes,
    long WorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

/// <summary>
/// Collects Avalonia's own timing metrics (meter "Avalonia.Diagnostic.Meter", enabled in Program.Main) with a
/// <see cref="MeterListener"/> while at least one indicator is subscribed, and publishes a sample twice a second.
/// <para>
/// Nothing is measured while no one listens: Avalonia only starts timing a pass once its histogram has a listener,
/// so the indicator being off costs nothing. Frames are counted from real compositor render passes; unlike the
/// renderer's built-in FPS overlay this never forces extra frames, so an idle window correctly shows ~0 fps.
/// </para>
/// </summary>
public sealed class PerformanceMonitor : IDisposable
{
    public const string AvaloniaMeterName = "Avalonia.Diagnostic.Meter";

    private const int CompositorRender = 0;
    private const int CompositorUpdate = 1;
    private const int Measure = 2;
    private const int Arrange = 3;
    private const int RenderRecording = 4;
    private const int Input = 5;
    private const int PassKinds = 6;

    // Boxed once: passed as the listener state so the measurement callback needs no name lookup.
    private static readonly object[] PassStates = [CompositorRender, CompositorUpdate, Measure, Arrange, RenderRecording, Input];
    private static readonly object VisualCountState = new();

    private readonly Lock _gate = new();
    private readonly double[] _sum = new double[PassKinds];
    private readonly double[] _max = new double[PassKinds];
    private readonly int[] _count = new int[PassKinds];
    private MeterListener? _listener;
    private DispatcherTimer? _timer;
    private int _subscribers;
    private long _visualCount;
    private long _lastSampleTimestamp;
    private bool _disposed;

    /// <summary>Raised on the UI thread with each new sample.</summary>
    public event EventHandler<PerformanceSample>? Sampled;

    public PerformanceSample? Latest { get; private set; }

    /// <summary>Starts measuring for the caller; dispose the result to stop (measuring ends with the last subscriber).</summary>
    public IDisposable Subscribe()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_subscribers++ == 0)
            Start();
        return new Subscription(this);
    }

    private void Unsubscribe()
    {
        if (_subscribers > 0 && --_subscribers == 0)
            Stop();
    }

    private void Start()
    {
        ResetAccumulators();
        _lastSampleTimestamp = Stopwatch.GetTimestamp();

        var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name != AvaloniaMeterName)
                    return;
                var state = StateFor(instrument.Name);
                if (state is not null)
                    listener.EnableMeasurementEvents(instrument, state);
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, state) => OnDuration(value, state));
        listener.SetMeasurementEventCallback<int>((_, value, _, state) => OnCount(value, state));
        listener.SetMeasurementEventCallback<long>((_, value, _, state) => OnCount(value, state));
        listener.Start();
        _listener = listener;

        // This constructor starts the timer.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => TakeSample());
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _listener?.Dispose();
        _listener = null;
        Latest = null;
    }

    private static object? StateFor(string instrumentName) => instrumentName switch
    {
        "avalonia.comp.render.time" => PassStates[CompositorRender],
        "avalonia.comp.update.time" => PassStates[CompositorUpdate],
        "avalonia.ui.measure.time" => PassStates[Measure],
        "avalonia.ui.arrange.time" => PassStates[Arrange],
        "avalonia.ui.render.time" => PassStates[RenderRecording],
        "avalonia.ui.input.time" => PassStates[Input],
        "avalonia.ui.visual.count" => VisualCountState,
        _ => null,
    };

    // Called on the render thread (compositor passes) and the UI thread (layout, input): only a few adds under a lock.
    private void OnDuration(double milliseconds, object? state)
    {
        if (state is not int kind)
            return;
        lock (_gate)
        {
            _sum[kind] += milliseconds;
            _count[kind]++;
            if (milliseconds > _max[kind])
                _max[kind] = milliseconds;
        }
    }

    private void OnCount(long value, object? state)
    {
        if (ReferenceEquals(state, VisualCountState))
            Interlocked.Exchange(ref _visualCount, value);
    }

    private void TakeSample()
    {
        if (_listener is null)
            return;
        _listener.RecordObservableInstruments(); // visual count

        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Max(0.001, Stopwatch.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds);
        _lastSampleTimestamp = now;

        Span<PassStatistics> passes = stackalloc PassStatistics[PassKinds];
        lock (_gate)
        {
            for (var i = 0; i < PassKinds; i++)
                passes[i] = new PassStatistics(_count[i] == 0 ? 0 : _sum[i] / _count[i], _max[i], _count[i]);
            ResetAccumulators();
        }

        // A frame is the compositor update plus render pass; layout is measure plus arrange.
        var frame = Combine(passes[CompositorRender], passes[CompositorUpdate]);
        var layout = Combine(passes[Measure], passes[Arrange]);
        var sample = new PerformanceSample(
            passes[CompositorRender].Count / seconds,
            frame,
            passes[CompositorRender],
            passes[CompositorUpdate],
            layout,
            passes[RenderRecording],
            passes[Input],
            Interlocked.Read(ref _visualCount),
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetGCMemoryInfo().TotalCommittedBytes, // as of the last collection; the indicator never shows less than in use
            Environment.WorkingSet,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));

        Latest = sample;
        Sampled?.Invoke(this, sample);
    }

    /// <summary>Two passes that run once per frame each: averages add up, the worst case is bounded by the sum of maxima.</summary>
    private static PassStatistics Combine(PassStatistics a, PassStatistics b)
        => new(a.AverageMilliseconds + b.AverageMilliseconds, a.MaxMilliseconds + b.MaxMilliseconds, Math.Max(a.Count, b.Count));

    private void ResetAccumulators()
    {
        Array.Clear(_sum);
        Array.Clear(_max);
        Array.Clear(_count);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private sealed class Subscription(PerformanceMonitor owner) : IDisposable
    {
        private PerformanceMonitor? _owner = owner;

        public void Dispose()
        {
            _owner?.Unsubscribe();
            _owner = null;
        }
    }
}
