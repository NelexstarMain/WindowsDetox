using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
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

/// <summary>
/// Redraws an app icon in the theme: desaturated, then tinted toward the olive accent, so every icon sits quietly in the same palette.
/// <see cref="Active"/> icons (selected apps) are brighter and greener than the muted default. Transparency is kept.
/// </summary>
internal sealed class PngBytesToThemedIconConverter : IValueConverter
{
    private const double TintAmount = 0.62;

    // The brand olive lifted for text (Blocker.Accent.Text).
    private const double TintRed = 0x98;
    private const double TintGreen = 0xAD;
    private const double TintBlue = 0x4A;

    private static readonly PngBytesToImageConverter Decoder = new();

    /// <summary>Gets or sets whether the icon is drawn in the brighter, selected look.</summary>
    public bool Active { get; set; }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (Decoder.Convert(value, typeof(BitmapSource), null, culture) is not BitmapSource decoded)
        {
            return null;
        }

        var straight = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        int width = straight.PixelWidth;
        int height = straight.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        straight.CopyPixels(pixels, stride, 0);
        double brightness = Active ? 1.0 : 0.7;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double luma = (0.114 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.299 * pixels[i + 2]);
            double lift = 0.4 + (0.6 * luma / 255);
            pixels[i] = Mix(luma, TintBlue * lift, brightness);
            pixels[i + 1] = Mix(luma, TintGreen * lift, brightness);
            pixels[i + 2] = Mix(luma, TintRed * lift, brightness);
        }

        BitmapSource themed = BitmapSource.Create(width, height, decoded.DpiX, decoded.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        themed.Freeze();
        return themed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static byte Mix(double gray, double tint, double brightness) =>
        (byte)Math.Clamp(((1 - TintAmount) * gray + TintAmount * tint) * brightness, 0, 255);
}
