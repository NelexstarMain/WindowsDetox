using Blocker.UI.Controls.Rendering;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Tests;

public sealed class RenderingMathTests
{
    public static TheoryData<UiIcon> AllIcons => [.. Enum.GetValues<UiIcon>()];

    [Theory]
    [MemberData(nameof(AllIcons))]
    public void Every_icon_maps_to_a_defined_symbol(UiIcon icon)
    {
        var symbol = UiIconMap.ToSymbol(icon);

        Assert.True(Enum.IsDefined(symbol));
        Assert.NotEqual(Wpf.Ui.Controls.SymbolRegular.Empty, symbol);
    }

    [Fact]
    public void Icon_map_gives_distinct_symbols()
    {
        var symbols = Enum.GetValues<UiIcon>().Select(UiIconMap.ToSymbol).ToList();

        Assert.Equal(symbols.Count, symbols.Distinct().Count());
    }

    [Fact]
    public void Icon_map_rejects_undefined_values() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => UiIconMap.ToSymbol((UiIcon)999));

    [Fact]
    public void Every_tone_has_theme_keys()
    {
        foreach (UiTone tone in Enum.GetValues<UiTone>())
        {
            Assert.StartsWith("Blocker.", ToneMap.ForegroundKey(tone));
            Assert.StartsWith("Blocker.", ToneMap.FillKey(tone));
            Assert.StartsWith("Blocker.", ToneMap.SoftKey(tone));
        }
    }

    [Theory]
    [InlineData(UiTone.Neutral, "Blocker.Text.Primary", "Blocker.Text.Secondary", "Blocker.Bg.Raised")]
    [InlineData(UiTone.Accent, "Blocker.Accent", "Blocker.Accent", "Blocker.Accent.Soft")]
    [InlineData(UiTone.Warning, "Blocker.Warning", "Blocker.Warning", "Blocker.Warning.Soft")]
    [InlineData(UiTone.Danger, "Blocker.Danger", "Blocker.Danger", "Blocker.Danger.Soft")]
    [InlineData(UiTone.Success, "Blocker.Success", "Blocker.Success", "Blocker.Success.Soft")]
    public void Tone_keys_match_the_palette(UiTone tone, string foreground, string fill, string soft)
    {
        Assert.Equal(foreground, ToneMap.ForegroundKey(tone));
        Assert.Equal(fill, ToneMap.FillKey(tone));
        Assert.Equal(soft, ToneMap.SoftKey(tone));
    }

    [Fact]
    public void Every_text_style_and_button_variant_has_a_style_key()
    {
        foreach (UiTextStyle style in Enum.GetValues<UiTextStyle>())
        {
            Assert.StartsWith("Blocker.Text.", ToneMap.TextStyleKey(style));
        }

        foreach (UiButtonVariant variant in Enum.GetValues<UiButtonVariant>())
        {
            Assert.StartsWith("Blocker.Button.", ToneMap.ButtonStyleKey(variant));
        }
    }

    [Theory]
    [InlineData(UiGap.Small, 8)]
    [InlineData(UiGap.Medium, 12)]
    [InlineData(UiGap.Large, 16)]
    public void Gap_maps_to_the_design_system_spacing(UiGap gap, double expected) =>
        Assert.Equal(expected, UiMetrics.GapPixels(gap));

    [Theory]
    [InlineData(0, "Blocker.Chart.Series1")]
    [InlineData(5, "Blocker.Chart.Series6")]
    [InlineData(6, "Blocker.Chart.Series1")]
    public void Series_keys_wrap_after_six(int index, string expected) => Assert.Equal(expected, UiMetrics.SeriesKey(index));

    [Theory]
    [InlineData(0, 100, 0, 100, 25)]
    [InlineData(0, 7, 0, 8, 2)]
    [InlineData(3, 47, 0, 60, 20)]
    [InlineData(-5, 12, -5, 15, 5)]
    public void Nice_axis_covers_the_data_with_round_steps(double dataMin, double dataMax, double min, double max, double step)
    {
        AxisScale axis = ChartScale.Nice(dataMin, dataMax, 4);

        Assert.Equal(min, axis.Min);
        Assert.Equal(max, axis.Max);
        Assert.Equal(step, axis.Step);
        Assert.Equal(axis.Min, axis.Ticks[0]);
        Assert.Equal(axis.Max, axis.Ticks[^1]);
    }

    [Fact]
    public void Nice_axis_survives_flat_and_invalid_data()
    {
        AxisScale flat = ChartScale.Nice(5, 5);
        AxisScale invalid = ChartScale.Nice(double.NaN, double.PositiveInfinity);

        Assert.True(flat.Max > flat.Min);
        Assert.True(flat.Min <= 0 && flat.Max >= 5);
        Assert.True(invalid.Max > invalid.Min);
    }

    [Fact]
    public void Nice_axis_can_exclude_zero()
    {
        AxisScale axis = ChartScale.Nice(95, 105, 4, includeZero: false);

        Assert.True(axis.Min > 0);
        Assert.True(axis.Min <= 95 && axis.Max >= 105);
    }

    [Fact]
    public void Axis_normalizes_values_between_bounds()
    {
        AxisScale axis = ChartScale.Nice(0, 100, 4);

        Assert.Equal(0.5, axis.Normalize(50));
    }

    [Theory]
    [InlineData(50, 100, 0.5)]
    [InlineData(150, 100, 1)]
    [InlineData(-3, 100, 0)]
    [InlineData(5, 0, 0)]
    [InlineData(double.NaN, 10, 0)]
    public void Ratio_is_clamped(double value, double max, double expected) => Assert.Equal(expected, ChartScale.Ratio(value, max));

    [Theory]
    [InlineData(42, null, "42")]
    [InlineData(3.14159, null, "3.14")]
    [InlineData(12500, null, "12.5k")]
    [InlineData(2_500_000, "min", "2.5M min")]
    [InlineData(-8, "h", "-8 h")]
    public void Values_are_formatted_invariantly(double value, string? suffix, string expected) =>
        Assert.Equal(expected, ChartScale.FormatValue(value, suffix));
}
