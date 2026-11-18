using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Blocker.Stub;

/// <summary>Shows an element only when the bound text is not empty.</summary>
internal sealed class HasTextToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
