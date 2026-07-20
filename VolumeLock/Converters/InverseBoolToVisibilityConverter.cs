using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VolumeLock.Converters;

/// <summary>Opposite of the built-in BooleanToVisibilityConverter - used to show a fallback
/// glyph exactly when an item's extracted icon is null.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
