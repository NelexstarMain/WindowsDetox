using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>Shared drawing helpers for the lightweight, dependency-free charts.</summary>
internal abstract class ChartElementBase : FrameworkElement
{
    private const double MinChartWidth = 160;

    protected const double AxisFontSize = 11;
    protected const double PlotGap = 8;

    protected ChartElementBase()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected abstract double PreferredHeight { get; }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? MinChartWidth * 2 : Math.Max(availableSize.Width, MinChartWidth), PreferredHeight);

    protected Brush ThemeBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected Typeface MonoFace() => FaceFor("Blocker.Font.Mono", "Cascadia Mono, Consolas");

    protected Typeface UiFace() => FaceFor("Blocker.Font.Ui", "Segoe UI Variable Text, Segoe UI");

    protected FormattedText Text(string text, Typeface face, Brush brush, double maxWidth = double.PositiveInfinity, double size = AxisFontSize)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection, face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        if (!double.IsInfinity(maxWidth))
        {
            formatted.MaxTextWidth = Math.Max(maxWidth, 1);
        }

        return formatted;
    }

    protected Pen HairPen() => new(ThemeBrush("Blocker.Border.Subtle", Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), 1);

    /// <summary>Rounds to a half pixel so 1 px lines render crisply.</summary>
    protected static double Crisp(double v) => Math.Floor(v) + 0.5;

    private Typeface FaceFor(string key, string fallback) =>
        new(TryFindResource(key) as FontFamily ?? new FontFamily(fallback), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
}

/// <summary>Single-series bar chart drawn directly with <see cref="DrawingContext"/>.</summary>
internal sealed class BarChartElement : ChartElementBase
{
    private readonly IReadOnlyList<UiDataPoint> _points;
    private readonly string? _suffix;

    public BarChartElement(IReadOnlyList<UiDataPoint> points, string? suffix)
    {
        _points = points;
        _suffix = suffix;
    }

    protected override double PreferredHeight => 200;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (_points.Count == 0 || width < 40 || height < 40)
        {
            return;
        }

        Brush muted = ThemeBrush("Blocker.Text.Muted", Color.FromRgb(0x5C, 0x62, 0x70));
        Brush secondary = ThemeBrush("Blocker.Text.Secondary", Color.FromRgb(0x9A, 0xA0, 0xAE));
        Brush primary = ThemeBrush("Blocker.Text.Primary", Color.FromRgb(0xEE, 0xF0, 0xF4));
        Brush accent = ThemeBrush("Blocker.Accent", Color.FromRgb(0x5F, 0x6F, 0x2E));
        Typeface mono = MonoFace();
        Typeface ui = UiFace();
        Pen grid = HairPen();

        double min = _points.Min(p => p.Value);
        double max = _points.Max(p => p.Value);
        AxisScale axis = ChartScale.Nice(min, max);

        double labelWidth = axis.Ticks.Max(t => Text(ChartScale.FormatValue(t), mono, muted).Width);
        double left = labelWidth + PlotGap;
        double top = 18;
        double bottom = height - 22;
        double plotWidth = width - left;
        double plotHeight = bottom - top;
        if (plotWidth < 20 || plotHeight < 20)
        {
            return;
        }

        double Y(double value) => bottom - (axis.Normalize(value) * plotHeight);

        foreach (double tick in axis.Ticks)
        {
            double y = Crisp(Y(tick));
            dc.DrawLine(grid, new Point(left, y), new Point(width, y));
            FormattedText label = Text(ChartScale.FormatValue(tick), mono, muted);
            dc.DrawText(label, new Point(left - PlotGap - label.Width, y - (label.Height / 2)));
        }

        double slot = plotWidth / _points.Count;
        double barWidth = Math.Min(slot * 0.62, 44);
        double zero = Y(0);
        int labelEvery = Math.Max(1, (int)Math.Ceiling(34 / slot));
        bool showValues = slot >= 30;

        for (int i = 0; i < _points.Count; i++)
        {
            UiDataPoint point = _points[i];
            double x = left + (i * slot) + ((slot - barWidth) / 2);
            double y = Y(point.Value);
            double barTop = Math.Min(y, zero);
            double barHeight = Math.Max(Math.Abs(zero - y), 1);
            dc.DrawRoundedRectangle(accent, null, new Rect(x, barTop, barWidth, barHeight), 3, 3);

            if (showValues)
            {
                FormattedText value = Text(ChartScale.FormatValue(point.Value, _suffix), mono, primary, slot);
                double vy = point.Value >= 0 ? barTop - value.Height - 3 : barTop + barHeight + 3;
                dc.DrawText(value, new Point(left + (i * slot) + ((slot - value.Width) / 2), Math.Max(0, vy)));
            }

            if (i % labelEvery == 0)
            {
                FormattedText category = Text(point.Label, ui, secondary, slot * labelEvery);
                dc.DrawText(category, new Point(left + (i * slot) + ((slot - category.Width) / 2), bottom + 6));
            }
        }
    }
}

/// <summary>Multi-series line chart (up to six series) drawn directly with <see cref="DrawingContext"/>.</summary>
internal sealed class LineChartElement : ChartElementBase
{
    private readonly IReadOnlyList<UiSeries> _series;
    private readonly string? _suffix;

    public LineChartElement(IReadOnlyList<UiSeries> series, string? suffix)
    {
        _series = series;
        _suffix = suffix;
    }

    protected override double PreferredHeight => _series.Count > 1 ? 232 : 208;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        int pointCount = _series.Count == 0 ? 0 : _series.Max(s => s.Points.Count);
        if (pointCount == 0 || width < 40 || height < 60)
        {
            return;
        }

        Brush muted = ThemeBrush("Blocker.Text.Muted", Color.FromRgb(0x5C, 0x62, 0x70));
        Brush secondary = ThemeBrush("Blocker.Text.Secondary", Color.FromRgb(0x9A, 0xA0, 0xAE));
        Brush primary = ThemeBrush("Blocker.Text.Primary", Color.FromRgb(0xEE, 0xF0, 0xF4));
        Brush card = ThemeBrush("Blocker.Bg.Card", Color.FromRgb(0x0B, 0x0C, 0x0F));
        Typeface mono = MonoFace();
        Typeface ui = UiFace();
        Pen grid = HairPen();

        double min = _series.Min(s => s.Points.Count == 0 ? 0 : s.Points.Min(p => p.Value));
        double max = _series.Max(s => s.Points.Count == 0 ? 0 : s.Points.Max(p => p.Value));
        AxisScale axis = ChartScale.Nice(min, max, includeZero: min >= 0 && min < max * 0.6);

        double labelWidth = axis.Ticks.Max(t => Text(ChartScale.FormatValue(t), mono, muted).Width);
        double left = labelWidth + PlotGap;
        double legendHeight = _series.Count > 1 ? 22 : 0;
        double top = 18;
        double bottom = height - 22 - legendHeight;
        double plotWidth = width - left - 12;
        double plotHeight = bottom - top;
        if (plotWidth < 20 || plotHeight < 20)
        {
            return;
        }

        double Y(double value) => bottom - (axis.Normalize(value) * plotHeight);
        double X(int index) => pointCount == 1 ? left + (plotWidth / 2) : left + (index * plotWidth / (pointCount - 1));

        foreach (double tick in axis.Ticks)
        {
            double y = Crisp(Y(tick));
            dc.DrawLine(grid, new Point(left, y), new Point(width, y));
            FormattedText label = Text(ChartScale.FormatValue(tick), mono, muted);
            dc.DrawText(label, new Point(left - PlotGap - label.Width, y - (label.Height / 2)));
        }

        UiSeries labelSource = _series.OrderByDescending(s => s.Points.Count).First();
        double step = pointCount == 1 ? plotWidth : plotWidth / (pointCount - 1);
        int labelEvery = Math.Max(1, (int)Math.Ceiling(56 / step));
        for (int i = 0; i < labelSource.Points.Count; i += labelEvery)
        {
            FormattedText category = Text(labelSource.Points[i].Label, ui, secondary, step * labelEvery);
            double cx = Math.Clamp(X(i) - (category.Width / 2), left, Math.Max(left, width - category.Width));
            dc.DrawText(category, new Point(cx, bottom + 6));
        }

        for (int s = 0; s < _series.Count; s++)
        {
            UiSeries series = _series[s];
            if (series.Points.Count == 0)
            {
                continue;
            }

            Brush stroke = ThemeBrush(UiMetrics.SeriesKey(s), Color.FromRgb(0x5F, 0x6F, 0x2E));
            var line = new StreamGeometry();
            using (StreamGeometryContext ctx = line.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(series.Points[0].Value)), false, false);
                for (int i = 1; i < series.Points.Count; i++)
                {
                    ctx.LineTo(new Point(X(i), Y(series.Points[i].Value)), true, true);
                }
            }

            line.Freeze();
            var pen = new Pen(stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, line);

            if (series.Points.Count <= 31)
            {
                var ring = new Pen(stroke, 1.5);
                for (int i = 0; i < series.Points.Count; i++)
                {
                    dc.DrawEllipse(card, ring, new Point(X(i), Y(series.Points[i].Value)), 3, 3);
                }
            }

            if (_series.Count == 1)
            {
                int last = series.Points.Count - 1;
                FormattedText value = Text(ChartScale.FormatValue(series.Points[last].Value, _suffix), mono, primary);
                double vx = Math.Clamp(X(last) - value.Width, left, width - value.Width);
                dc.DrawText(value, new Point(vx, Math.Max(0, Y(series.Points[last].Value) - value.Height - 6)));
            }
        }

        if (_series.Count > 1)
        {
            double x = left;
            double y = height - 15;
            for (int s = 0; s < _series.Count; s++)
            {
                Brush swatch = ThemeBrush(UiMetrics.SeriesKey(s), Color.FromRgb(0x5F, 0x6F, 0x2E));
                FormattedText name = Text(_series[s].Name, ui, secondary, 140);
                if (x + 16 + name.Width > width)
                {
                    break;
                }

                dc.DrawRoundedRectangle(swatch, null, new Rect(x, y + 2, 8, 8), 2, 2);
                dc.DrawText(name, new Point(x + 14, y - 1));
                x += 14 + name.Width + 16;
            }
        }
    }
}
