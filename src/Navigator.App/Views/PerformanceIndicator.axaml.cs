using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Navigator.App.Services;
using Navigator.Core.Formatting;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Settings;

namespace Navigator.App.Views;

/// <summary>
/// Shows <see cref="PerformanceMonitor"/> samples in the status bar while Advanced.ShowPerformanceOverlay is on, as a
/// memory bar in the style of the JetBrains IDE memory indicator. Measuring runs only while the indicator is enabled
/// and attached to a window.
/// <para>
/// Per sample the bar costs one transform change (render only, no layout) and, only when a megabyte value changes, one
/// short string. The detailed tooltip text is built only while the pointer is over the indicator.
/// </para>
/// </summary>
public sealed partial class PerformanceIndicator : UserControl
{
    private const long BytesPerMegabyte = 1024 * 1024;

    private readonly SettingsService _settings;
    private readonly Localizer _localizer;
    private readonly PerformanceMonitor _monitor;
    private readonly ScaleTransform _fillScale = new(0, 1);
    private IDisposable? _subscription;
    private PerformanceSample? _latest;
    private long _shownUsedMegabytes = -1;
    private long _shownTotalMegabytes = -1;
    private bool _attached;

    public PerformanceIndicator()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<SettingsService>();
        _localizer = App.Services.GetRequiredService<Localizer>();
        _monitor = App.Services.GetRequiredService<PerformanceMonitor>();
        Fill.RenderTransform = _fillScale;
        Chip.PointerEntered += OnChipPointerEntered;
        Chip.PointerReleased += OnChipPointerReleased;
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
            _latest = null;
            _shownUsedMegabytes = -1;
            _shownTotalMegabytes = -1;
            _fillScale.ScaleX = 0;
            Summary.Text = _localizer["Perf.Measuring"];
            // A non-null tip from the start, so hovering before the first sample still opens the tooltip.
            ToolTip.SetTip(Chip, _localizer["Perf.Measuring"]);
        }
        else
        {
            _monitor.Sampled -= OnSampled;
            _subscription?.Dispose();
            _subscription = null;
            _latest = null;
        }
    }

    private void OnSampled(object? sender, PerformanceSample sample)
    {
        _latest = sample;

        var used = Math.Max(0, sample.ManagedBytes);
        var total = Math.Max(used, sample.HeapCommittedBytes);
        _fillScale.ScaleX = total == 0 ? 0 : (double)used / total;

        var usedMegabytes = used / BytesPerMegabyte;
        var totalMegabytes = (total + BytesPerMegabyte - 1) / BytesPerMegabyte;
        if (usedMegabytes != _shownUsedMegabytes || totalMegabytes != _shownTotalMegabytes)
        {
            _shownUsedMegabytes = usedMegabytes;
            _shownTotalMegabytes = totalMegabytes;
            Summary.Text = _localizer.Format("Perf.Memory", usedMegabytes, totalMegabytes);
        }

        if (Chip.IsPointerOver)
            UpdateToolTip(sample);
    }

    private void OnChipPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_latest is { } sample)
            UpdateToolTip(sample);
    }

    // Like the JetBrains indicator: a click runs a full blocking, compacting collection; the next sample shows the result.
    private void OnChipPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || _subscription is null)
            return;
        e.Handled = true;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private void UpdateToolTip(PerformanceSample sample)
    {
        var culture = _localizer.Culture;
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
    }
}
