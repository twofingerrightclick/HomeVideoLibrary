using System.Globalization;
using Avalonia.Data.Converters;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>A queued job is removed from the queue; a running one is cancelled.</summary>
public sealed class JobCancelLabel : IValueConverter
{
    public static JobCancelLabel Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "REMOVE" : "CANCEL";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
