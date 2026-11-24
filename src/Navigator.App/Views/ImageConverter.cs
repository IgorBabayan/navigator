using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Navigator.App.Views;

/// <summary>View models expose thumbnails as <c>object</c> (toolkit-agnostic); this narrows them to <see cref="IImage"/>.</summary>
public sealed class ImageConverter : IValueConverter
{
    public static ImageConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value as IImage;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
