using Blocker.UI.Controls.Rendering;

namespace Blocker.UI.Controls.Tests;

public sealed class DonutHeatmapMathTests
{
    [Fact]
    public void Donut_arcs_are_proportional_and_contiguous()
    {
        var arcs = DonutMath.Arcs([1, 1, 2]);

        Assert.Equal(3, arcs.Count);
        Assert.Equal(0, arcs[0].StartAngle, 6);
        Assert.Equal(90, arcs[0].Sweep, 6);
        Assert.Equal(90, arcs[1].StartAngle, 6);
        Assert.Equal(180, arcs[2].StartAngle, 6);
        Assert.Equal(180, arcs[2].Sweep, 6);
        Assert.Equal(0.5, arcs[2].Fraction, 6);
    }

    [Fact]
    public void Donut_with_no_positive_values_has_zero_sweeps()
    {
        var arcs = DonutMath.Arcs([0, -2, double.NaN]);

        Assert.All(arcs, arc => Assert.Equal(0, arc.Sweep));
    }

    [Fact]
    public void Donut_single_slice_stays_below_a_full_turn()
    {
        var arcs = DonutMath.Arcs([5]);

        Assert.Equal(DonutMath.MaxSweep, arcs[0].Sweep, 6);
        Assert.Equal(1, arcs[0].Fraction, 6);
    }

    [Theory]
    [InlineData(0, 100, 50, 0)]
    [InlineData(90, 150, 100, 50)]
    [InlineData(180, 100, 150, 50)]
    [InlineData(270, 50, 100, 50)]
    public void Arc_points_run_clockwise_from_twelve_oclock(double angle, double x, double y, double radius)
    {
        var point = DonutMath.ArcPoint(100, 100, radius, angle);

        Assert.Equal(x, point.X, 6);
        Assert.Equal(y, point.Y, 6);
    }

    [Fact]
    public void Donut_thickness_is_sixteen_percent_of_the_diameter() =>
        Assert.Equal(25.6, DonutMath.Thickness(160), 6);

    [Theory]
    [InlineData(0.5, "50%")]
    [InlineData(0.4256, "42.6%")]
    public void Percent_is_formatted_invariantly(double fraction, string expected) =>
        Assert.Equal(expected, DonutMath.Percent(fraction));

    [Theory]
    [InlineData(5, 10, 0.5)]
    [InlineData(20, 10, 1)]
    [InlineData(0, 10, 0)]
    [InlineData(3, 0, 0)]
    public void Heatmap_intensity_is_clamped(double value, double max, double expected) =>
        Assert.Equal(expected, HeatmapMath.Intensity(value, max));

    [Fact]
    public void Heatmap_opacity_grows_with_intensity()
    {
        Assert.Equal(1, HeatmapMath.Opacity(0));
        Assert.Equal(HeatmapMath.MinFilledOpacity, HeatmapMath.Opacity(double.Epsilon), 6);
        Assert.Equal(1, HeatmapMath.Opacity(1), 6);
        Assert.True(HeatmapMath.Opacity(0.7) > HeatmapMath.Opacity(0.3));
    }

    [Fact]
    public void Heatmap_blend_runs_from_olive_to_sage()
    {
        Assert.Equal((0x5F, 0x6F, 0x2E), HeatmapMath.Blend(0));
        Assert.Equal((0xBF, 0xD0, 0xB4), HeatmapMath.Blend(1));
        Assert.Equal(HeatmapMath.Blend(1), HeatmapMath.Blend(7));
    }

    [Fact]
    public void Heatmap_max_ignores_invalid_values() =>
        Assert.Equal(7, HeatmapMath.Max([[1, double.NaN], [7, 3], []]));
}
