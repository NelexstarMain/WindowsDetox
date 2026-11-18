using System.Globalization;

namespace Blocker.UI.Controls.Rendering;

/// <summary>One donut slice as a start angle and sweep in degrees (0 = 12 o'clock, clockwise).</summary>
/// <param name="StartAngle">Start angle in degrees.</param>
/// <param name="Sweep">Sweep in degrees.</param>
/// <param name="Fraction">Share of the total, 0..1.</param>
internal readonly record struct DonutArc(double StartAngle, double Sweep, double Fraction);

/// <summary>Pure geometry for donut charts.</summary>
internal static class DonutMath
{
    /// <summary>Largest sweep drawn for one arc; a full 360 degree arc has identical start and end points and would vanish.</summary>
    public const double MaxSweep = 359.99;

    /// <summary>Splits a circle proportionally to <paramref name="values"/>; negative or non-finite values count as zero.</summary>
    /// <param name="values">Slice values.</param>
    /// <returns>One arc per value; all sweeps are zero when the total is zero.</returns>
    public static IReadOnlyList<DonutArc> Arcs(IReadOnlyList<double> values)
    {
        double[] clean = [.. values.Select(v => double.IsFinite(v) && v > 0 ? v : 0)];
        double total = clean.Sum();
        var arcs = new List<DonutArc>(clean.Length);
        double angle = 0;
        foreach (double value in clean)
        {
            double fraction = total > 0 ? value / total : 0;
            double sweep = Math.Min(fraction * 360, MaxSweep);
            arcs.Add(new DonutArc(angle, sweep, fraction));
            angle += fraction * 360;
        }

        return arcs;
    }

    /// <summary>Gets the point on a circle for an angle measured clockwise from 12 o'clock.</summary>
    /// <param name="centerX">Centre x.</param>
    /// <param name="centerY">Centre y.</param>
    /// <param name="radius">Radius.</param>
    /// <param name="angleDegrees">Angle in degrees.</param>
    /// <returns>The x and y coordinates.</returns>
    public static (double X, double Y) ArcPoint(double centerX, double centerY, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180;
        return (centerX + (radius * Math.Sin(radians)), centerY - (radius * Math.Cos(radians)));
    }

    /// <summary>Ring thickness for a diameter (16 percent, at least 4).</summary>
    /// <param name="diameter">Outer diameter.</param>
    /// <returns>The thickness.</returns>
    public static double Thickness(double diameter) => Math.Max(4, diameter * 0.16);

    /// <summary>Formats a share such as <c>42.5%</c>.</summary>
    /// <param name="fraction">Share, 0..1.</param>
    /// <returns>Display text.</returns>
    public static string Percent(double fraction) =>
        (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}

/// <summary>Pure colour/opacity mapping for heatmaps.</summary>
internal static class HeatmapMath
{
    /// <summary>Opacity of the faintest non-empty cell.</summary>
    public const double MinFilledOpacity = 0.2;

    /// <summary>Gets the 0..1 intensity of a cell; invalid values or maxima give 0.</summary>
    /// <param name="value">Cell value.</param>
    /// <param name="max">Largest value in the grid.</param>
    /// <returns>The intensity.</returns>
    public static double Intensity(double value, double max) => ChartScale.Ratio(value, max);

    /// <summary>Gets the cell opacity: empty cells use a separate background brush (opacity 1), others scale linearly.</summary>
    /// <param name="intensity">Intensity from <see cref="Intensity"/>.</param>
    /// <returns>Opacity in 0..1.</returns>
    public static double Opacity(double intensity) =>
        intensity <= 0 ? 1 : MinFilledOpacity + ((1 - MinFilledOpacity) * Math.Clamp(intensity, 0, 1));

    /// <summary>Interpolates from the olive accent (#5F6F2E) to sage (#BFD0B4).</summary>
    /// <param name="intensity">Intensity 0..1.</param>
    /// <returns>RGB bytes.</returns>
    public static (byte R, byte G, byte B) Blend(double intensity)
    {
        double t = Math.Clamp(intensity, 0, 1);
        return (Lerp(0x5F, 0xBF, t), Lerp(0x6F, 0xD0, t), Lerp(0x2E, 0xB4, t));
    }

    /// <summary>Gets the largest finite value in the grid, or 0.</summary>
    /// <param name="values">Rows of values.</param>
    /// <returns>The maximum.</returns>
    public static double Max(IReadOnlyList<IReadOnlyList<double>> values)
    {
        double max = 0;
        foreach (IReadOnlyList<double> row in values)
        {
            foreach (double v in row)
            {
                if (double.IsFinite(v) && v > max)
                {
                    max = v;
                }
            }
        }

        return max;
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + ((b - a) * t));
}
