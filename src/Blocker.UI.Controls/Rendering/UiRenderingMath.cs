using System.Globalization;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>Maps semantic tones to theme resource keys; plugins never pick colours.</summary>
internal static class ToneMap
{
    /// <summary>Gets the text brush key for a tone. Neutral maps to the primary text colour.</summary>
    /// <param name="tone">The tone.</param>
    /// <returns>A <c>Blocker.*</c> brush key.</returns>
    public static string ForegroundKey(UiTone tone) => tone switch
    {
        UiTone.Neutral => "Blocker.Text.Primary",
        UiTone.Accent => "Blocker.Accent.Text",
        UiTone.Success => "Blocker.Success",
        UiTone.Warning => "Blocker.Warning",
        UiTone.Danger => "Blocker.Danger",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    /// <summary>Gets the brush key for a filled bar or mark. Neutral maps to the secondary text colour so it stays calm.</summary>
    /// <param name="tone">The tone.</param>
    /// <returns>A <c>Blocker.*</c> brush key.</returns>
    public static string FillKey(UiTone tone) => tone switch
    {
        UiTone.Neutral => "Blocker.Text.Secondary",
        UiTone.Accent => "Blocker.Accent",
        _ => ForegroundKey(tone),
    };

    /// <summary>Gets the translucent background brush key for a tone.</summary>
    /// <param name="tone">The tone.</param>
    /// <returns>A <c>Blocker.*.Soft</c> brush key (raised surface for neutral).</returns>
    public static string SoftKey(UiTone tone) => tone switch
    {
        UiTone.Neutral => "Blocker.Bg.Raised",
        UiTone.Accent => "Blocker.Accent.Soft",
        UiTone.Success => "Blocker.Success.Soft",
        UiTone.Warning => "Blocker.Warning.Soft",
        UiTone.Danger => "Blocker.Danger.Soft",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    /// <summary>Gets the text style key for a <see cref="UiTextStyle"/>.</summary>
    /// <param name="style">The style.</param>
    /// <returns>A <c>Blocker.Text.*</c> style key.</returns>
    public static string TextStyleKey(UiTextStyle style) => style switch
    {
        UiTextStyle.Body => "Blocker.Text.Body",
        UiTextStyle.Caption => "Blocker.Text.Caption",
        UiTextStyle.Subtitle => "Blocker.Text.Subtitle",
        UiTextStyle.Title => "Blocker.Text.Title",
        UiTextStyle.Mono => "Blocker.Text.Mono",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, null),
    };

    /// <summary>Gets the button style key for a <see cref="UiButtonVariant"/>.</summary>
    /// <param name="variant">The variant.</param>
    /// <returns>A <c>Blocker.Button.*</c> style key.</returns>
    public static string ButtonStyleKey(UiButtonVariant variant) => variant switch
    {
        UiButtonVariant.Primary => "Blocker.Button.Primary",
        UiButtonVariant.Secondary => "Blocker.Button.Secondary",
        UiButtonVariant.Warning => "Blocker.Button.Warning",
        UiButtonVariant.Subtle => "Blocker.Button.Subtle",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };
}

/// <summary>Layout constants shared by the renderer.</summary>
internal static class UiMetrics
{
    /// <summary>Converts a <see cref="UiGap"/> to DIPs.</summary>
    /// <param name="gap">The gap.</param>
    /// <returns>8, 12 or 16.</returns>
    public static double GapPixels(UiGap gap) => gap switch
    {
        UiGap.Small => 8,
        UiGap.Medium => 12,
        UiGap.Large => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(gap), gap, null),
    };

    /// <summary>Gets the brush key of the n-th chart series (wraps after six).</summary>
    /// <param name="index">Zero-based series index.</param>
    /// <returns>A <c>Blocker.Chart.SeriesN</c> key.</returns>
    public static string SeriesKey(int index) => "Blocker.Chart.Series" + (((index % 6) + 6) % 6 + 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>An axis with "nice" bounds and tick values.</summary>
/// <param name="Min">Lower bound.</param>
/// <param name="Max">Upper bound.</param>
/// <param name="Step">Distance between ticks.</param>
/// <param name="Ticks">Tick values from <paramref name="Min"/> to <paramref name="Max"/>.</param>
internal sealed record AxisScale(double Min, double Max, double Step, IReadOnlyList<double> Ticks)
{
    /// <summary>Maps a value to 0..1 along the axis.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The normalised position (not clamped).</returns>
    public double Normalize(double value) => Max > Min ? (value - Min) / (Max - Min) : 0.5;
}

/// <summary>Chart axis scaling and value formatting.</summary>
internal static class ChartScale
{
    /// <summary>Computes a nice axis (steps of 1, 2, 2.5, 5 times a power of ten) that covers the data.</summary>
    /// <param name="dataMin">Smallest data value.</param>
    /// <param name="dataMax">Largest data value.</param>
    /// <param name="targetTicks">Approximate number of intervals wanted.</param>
    /// <param name="includeZero">Extend the axis to zero when the data is all positive.</param>
    /// <returns>The scale.</returns>
    public static AxisScale Nice(double dataMin, double dataMax, int targetTicks = 4, bool includeZero = true)
    {
        if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax))
        {
            return Nice(0, 1, targetTicks, includeZero);
        }

        double lo = Math.Min(dataMin, dataMax);
        double hi = Math.Max(dataMin, dataMax);
        if (includeZero)
        {
            lo = Math.Min(lo, 0);
            hi = Math.Max(hi, 0);
        }

        if (hi - lo < 1e-12)
        {
            hi = lo + 1;
        }

        double step = NiceStep((hi - lo) / Math.Max(targetTicks, 1));
        double min = Math.Floor((lo / step) + 1e-9) * step;
        double max = Math.Ceiling((hi / step) - 1e-9) * step;
        int count = (int)Math.Round((max - min) / step);
        var ticks = new double[count + 1];
        for (int i = 0; i <= count; i++)
        {
            ticks[i] = Math.Round(min + (i * step), 10);
        }

        return new AxisScale(Math.Round(min, 10), Math.Round(max, 10), step, ticks);
    }

    /// <summary>Clamps <c>value / maximum</c> to 0..1; zero or invalid maxima give 0.</summary>
    /// <param name="value">Current value.</param>
    /// <param name="maximum">Maximum value.</param>
    /// <returns>The ratio.</returns>
    public static double Ratio(double value, double maximum) =>
        maximum > 0 && double.IsFinite(value) ? Math.Clamp(value / maximum, 0, 1) : 0;

    /// <summary>Formats a chart value with at most two decimals, abbreviating thousands and millions, with an optional unit.</summary>
    /// <param name="value">The value.</param>
    /// <param name="suffix">Optional unit.</param>
    /// <returns>Display text in the invariant culture.</returns>
    public static string FormatValue(double value, string? suffix = null)
    {
        double abs = Math.Abs(value);
        string number = abs switch
        {
            >= 1_000_000 => (value / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M",
            >= 10_000 => (value / 1_000).ToString("0.##", CultureInfo.InvariantCulture) + "k",
            _ => value.ToString("0.##", CultureInfo.InvariantCulture),
        };

        return string.IsNullOrEmpty(suffix) ? number : number + " " + suffix;
    }

    private static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double fraction = raw / magnitude;
        double nice = fraction switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10,
        };

        return nice * magnitude;
    }
}
