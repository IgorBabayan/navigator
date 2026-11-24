using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Navigator.App.Services;
using Navigator.Core.Formatting;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Settings;

namespace Navigator.App.Views;

/// <summary>
/// Shows <see cref="PerformanceMonitor"/> samples in the status bar while Advanced.ShowPerformanceOverlay is on.
/// Measuring runs only while the indicator is enabled and attached to a window.
/// </summary>
public sealed partial class PerformanceIndicator : UserControl
{
    private readonly SettingsService _settings;
    private readonly Localizer _localizer;
    private readonly PerformanceMonitor _monitor;
    private readonly double[] _history = new double[PerformanceMonitor.HistoryLength];
    private IDisposable? _subscription;
    private bool _attached;

    public PerformanceIndicator()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<SettingsService>();
        _localizer = App.Services.GetRequiredService<Localizer>();
        _monitor = App.Services.GetRequiredService<PerformanceMonitor>();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _settings.Changed += OnSettingsChanged;
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _settings.Changed -= OnSettingsChanged;
        Apply();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSettingsChanged(object? sender, string section)
    {
        if (section is "advanced" or "*")
            Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
    }

    private void Apply()
    {
        var enabled = _attached && _settings.Current.Advanced.ShowPerformanceOverlay;
        IsVisible = enabled;
        if (enabled == (_subscription is not null))
            return;

        if (enabled)
        {
            _monitor.Sampled += OnSampled;
            _subscription = _monitor.Subscribe();
            Summary.Text = _localizer["Perf.Measuring"];
            ToolTip.SetTip(Chip, null);
            Graph.SetValues([]);
        }
        else
        {
            _monitor.Sampled -= OnSampled;
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    private void OnSampled(object? sender, PerformanceSample sample)
    {
        var culture = _localizer.Culture;
        Summary.Text = _localizer.Format("Perf.Summary",
            sample.Frame.AverageMilliseconds, sample.Layout.AverageMilliseconds, sample.FramesPerSecond, ByteSize.Format(sample.ManagedBytes, culture));
        ToolTip.SetTip(Chip, _localizer.Format("Perf.Details",
            sample.FramesPerSecond,
            sample.CompositorRender.AverageMilliseconds, sample.CompositorRender.MaxMilliseconds,
            sample.CompositorUpdate.AverageMilliseconds, sample.CompositorUpdate.MaxMilliseconds,
            sample.Layout.AverageMilliseconds, sample.Layout.MaxMilliseconds, sample.Layout.Count,
            sample.RenderRecording.AverageMilliseconds, sample.RenderRecording.MaxMilliseconds,
            sample.Input.AverageMilliseconds, sample.Input.MaxMilliseconds,
            sample.VisualCount,
            ByteSize.Format(sample.ManagedBytes, culture),
            ByteSize.Format(sample.WorkingSetBytes, culture),
            sample.Gen0Collections, sample.Gen1Collections, sample.Gen2Collections));

        var count = _monitor.CopyHistory(_history);
        Graph.SetValues(_history.AsSpan(0, count));
    }
}
