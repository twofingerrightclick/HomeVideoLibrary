using Avalonia.Data.Converters;
using System.Globalization;

namespace HomeVideoLibrary.Desktop.Views;

public sealed class HalfWidthConverter : IValueConverter
{
    public static HalfWidthConverter Instance { get; } = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is double width ? Math.Max(0, (width - 36) / 2) : 0d;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
