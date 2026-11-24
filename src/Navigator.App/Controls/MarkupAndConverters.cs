using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;

namespace Navigator.App.Controls;

/// <summary>{c:Tr Key}: a live-updating localized string (switching language updates every bound text).</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Localizer.Current, Mode = BindingMode.OneWay };
}

/// <summary>"#RRGGBB" → brush (tag colors).</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static HexToBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && Color.TryParse(text, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>Notification severity → icon kind.</summary>
public sealed class SeverityToIconConverter : IValueConverter
{
    public static SeverityToIconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        NotificationSeverity.Success => "Success",
        NotificationSeverity.Warning => "Warning",
        NotificationSeverity.Error => "Error",
        _ => "Info",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>Compares a bound value with ConverterParameter (radio-style menu checks for layout/sort/group).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static EqualsConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>
/// Entrance motion: Motion.Enter="True" fades (and, at full motion, slides 6 px) an element in when it is attached.
/// Uses the shared transition resources, so "reduce motion"/"off" settings apply automatically.
/// </summary>
public static class Motion
{
    public static readonly AttachedProperty<bool> EnterProperty = AvaloniaProperty.RegisterAttached<Visual, bool>("Enter", typeof(Motion));

    static Motion()
    {
        EnterProperty.Changed.AddClassHandler<Visual>((visual, e) =>
        {
            if (e.NewValue is true)
                visual.AttachedToVisualTree += OnAttached;
            else
                visual.AttachedToVisualTree -= OnAttached;
        });
    }

    public static bool GetEnter(Visual visual) => visual.GetValue(EnterProperty);

    public static void SetEnter(Visual visual, bool value) => visual.SetValue(EnterProperty, value);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Visual visual || Application.Current?.Resources is not { } resources)
            return;
        if (!resources.TryGetResource("Motion.Enabled", null, out var enabled) || enabled is not true)
            return;

        resources.TryGetResource("Motion.FadeAndMove", null, out var transitions);
        visual.Transitions = transitions as Transitions;
        visual.Opacity = 0;
        var moves = resources.TryGetResource("Motion.Movement", null, out var movement) && movement is true;
        if (moves)
            visual.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(6px)");

        Dispatcher.UIThread.Post(() =>
        {
            visual.Opacity = 1;
            if (moves)
                visual.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Render);
    }
}
