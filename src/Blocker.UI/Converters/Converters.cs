using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Blocker.UI.Converters;

/// <summary>Maps <see langword="true"/> to <see cref="Visibility.Visible"/> (or the reverse when <see cref="Invert"/> is set).</summary>
internal sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Gets or sets whether the mapping is reversed.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Collapses <see langword="null"/> and empty text; shows everything else (or the reverse when <see cref="Invert"/> is set).</summary>
internal sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    /// <summary>Gets or sets whether the mapping is reversed.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value is string text ? text.Length > 0 : value is not null;
        return hasValue != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Decodes PNG bytes (the 32 px app icons from the service) into a frozen bitmap; <see langword="null"/> when absent or invalid.</summary>
internal sealed class PngBytesToImageConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        try
        {
            using MemoryStream stream = new(bytes);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            // A corrupt icon only costs the icon: the row still shows the app name.
            return null;
        }
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
