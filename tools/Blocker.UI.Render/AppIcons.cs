using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Blocker.UI.Render;

/// <summary>Draws the placeholder application icons of the allowlist picker: a rounded colour tile with the app's initial.</summary>
internal static class AppIcons
{
    private const int Size = 32;

    /// <summary>Creates a 32 px PNG, base64 encoded like the service delivers icons.</summary>
    /// <param name="letter">Initial drawn on the tile.</param>
    /// <param name="rgb">Tile colour as 0xRRGGBB.</param>
    /// <returns>Base64 of the PNG bytes.</returns>
    public static string Png(char letter, int rgb)
    {
        var tile = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        var text = new FormattedText(
            letter.ToString(),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"),
            18,
            Brushes.White,
            1.0);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(tile, null, new Rect(1, 1, Size - 2, Size - 2), 7, 7);
            context.DrawText(text, new Point((Size - text.Width) / 2, (Size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
}
