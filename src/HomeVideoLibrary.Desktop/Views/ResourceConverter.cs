using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>Looks up an application resource by key, so view models can name a theme token without holding brushes.</summary>
public sealed class ResourceConverter : IValueConverter
{
    public static ResourceConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var resource) == true
            ? resource : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
