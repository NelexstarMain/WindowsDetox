using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Blocker.UI.Sdk;
using NumberBox = Wpf.Ui.Controls.NumberBox;
using NumberBoxSpinButtonPlacementMode = Wpf.Ui.Controls.NumberBoxSpinButtonPlacementMode;
using NumberBoxValidationMode = Wpf.Ui.Controls.NumberBoxValidationMode;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using ToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;
using WpfUiTextBox = Wpf.Ui.Controls.TextBox;

namespace Blocker.UI.Controls.Rendering;

/// <summary>
/// Turns a validated <see cref="UiNode"/> tree into WPF visuals. Every colour, font and style comes from the
/// <c>Blocker.*</c> theme resources (referenced dynamically); plugins cannot influence them.
/// </summary>
internal sealed class UiVisualBuilder
{
    private const double CardContentGap = 12;
    private const double FlatContentGap = 10;
    private const double PageGap = 16;

    private readonly Action<UiAction> _raise;
    private readonly bool _flat;

    /// <summary>Initializes a new instance of the <see cref="UiVisualBuilder"/> class.</summary>
    /// <param name="raise">Called when the user interacts with a button, toggle or form.</param>
    /// <param name="flat">
    /// Flat mode for content the host already places on its own surface (dashboard widgets): cards render as plain stacks without
    /// border, background, padding or header (title, subtitle and icon are always dropped, the host draws the identity), and stat
    /// tiles render as value and label without a tile.
    /// </param>
    public UiVisualBuilder(Action<UiAction> raise, bool flat = false)
    {
        _raise = raise;
        _flat = flat;
    }

    /// <summary>Builds the visual for <paramref name="node"/>.</summary>
    /// <param name="node">The node.</param>
    /// <param name="insideCard">Whether the node is nested in a card (nested tiles use the inset style).</param>
    /// <returns>The element.</returns>
    public FrameworkElement Build(UiNode node, bool insideCard = false) => node switch
    {
        UiPage page => BuildPage(page),
        UiStack stack => BuildStack(stack, insideCard),
        UiGrid grid => BuildGrid(grid, insideCard),
        UiCard card => BuildCard(card),
        UiText text => BuildText(text),
        UiStatTile tile => BuildStatTile(tile, insideCard, _flat),
        UiProgress progress => BuildProgress(progress),
        UiBarChart chart => BuildBarChart(chart),
        UiLineChart chart => BuildLineChart(chart),
        UiDonutChart donut => BuildDonutChart(donut),
        UiHeatmap heatmap => BuildHeatmap(heatmap),
        UiGlyph glyph => BuildGlyph(glyph),
        UiTable table => BuildTable(table),
        UiButton button => BuildButton(button),
        UiToggle toggle => BuildToggle(toggle),
        UiForm form => BuildForm(form),
        _ => Caption("Unsupported element.", UiTone.Danger),
    };

    /// <summary>Builds the card shown instead of plugin content that failed validation.</summary>
    /// <param name="errors">Validation messages.</param>
    /// <returns>The error card.</returns>
    public static FrameworkElement BuildErrorCard(IReadOnlyList<string> errors)
    {
        var stack = new StackPanel();
        stack.Children.Add(Styled(new TextBlock { Text = "This content can't be shown" }, "Blocker.Text.CardTitle"));
        TextBlock reason = Caption("The plugin sent a layout that failed validation, so the host did not render it.");
        reason.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(reason);
        foreach (string error in errors.Take(3))
        {
            TextBlock line = Styled(new TextBlock { Text = error, Margin = new Thickness(0, 4, 0, 0) }, "Blocker.Text.Mono");
            line.FontSize = 11;
            line.TextWrapping = TextWrapping.Wrap;
            line.SetResourceReference(TextBlock.ForegroundProperty, "Blocker.Danger");
            stack.Children.Add(line);
        }

        var border = new Border { Child = stack };
        border.SetResourceReference(FrameworkElement.StyleProperty, "Blocker.CardBorder");
        border.SetResourceReference(Border.BorderBrushProperty, "Blocker.Danger");
        return border;
    }

    private static T Styled<T>(T element, string styleKey)
        where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return element;
    }

    private static TextBlock Caption(string text, UiTone tone = UiTone.Neutral)
    {
        TextBlock block = Styled(new TextBlock { Text = text }, "Blocker.Text.Caption");
        if (tone != UiTone.Neutral)
        {
            block.SetResourceReference(TextBlock.ForegroundProperty, ToneMap.ForegroundKey(tone));
        }

        return block;
    }

    private static SymbolIcon Icon(UiIcon icon, double size, string? brushKey = null)
    {
        var symbol = new SymbolIcon { Symbol = UiIconMap.ToSymbol(icon), FontSize = size };
        if (brushKey is not null)
        {
            symbol.SetResourceReference(Wpf.Ui.Controls.IconElement.ForegroundProperty, brushKey);
        }

        return symbol;
    }

    private FrameworkElement BuildPage(UiPage page)
    {
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 32) };
        root.Children.Add(Styled(new TextBlock { Text = page.Title }, "Blocker.Text.Title"));
        if (!string.IsNullOrWhiteSpace(page.Subtitle))
        {
            TextBlock subtitle = Styled(new TextBlock { Text = page.Subtitle, Margin = new Thickness(0, 4, 0, 0) }, "Blocker.Text.Subtitle");
            root.Children.Add(subtitle);
        }

        Panel content = BuildChildren(page.Children, UiOrientation.Vertical, PageGap, insideCard: false);
        content.Margin = new Thickness(0, 20, 0, 0);
        root.Children.Add(content);
        return root;
    }

    private FrameworkElement BuildStack(UiStack stack, bool insideCard) =>
        BuildChildren(stack.Children, stack.Orientation, UiMetrics.GapPixels(stack.Gap), insideCard);

    private StackPanel BuildChildren(IReadOnlyList<UiNode> children, UiOrientation orientation, double gap, bool insideCard)
    {
        bool vertical = orientation == UiOrientation.Vertical;
        var panel = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal };
        foreach (UiNode child in children)
        {
            FrameworkElement element = Build(child, insideCard);
            if (panel.Children.Count > 0)
            {
                element.Margin = vertical ? new Thickness(0, gap, 0, 0) : new Thickness(gap, 0, 0, 0);
            }

            panel.Children.Add(element);
        }

        return panel;
    }

    private FrameworkElement BuildGrid(UiGrid grid, bool insideCard)
    {
        const double gap = 12;
        var panel = new UniformGrid { Columns = Math.Clamp(grid.Columns, 1, 6), Margin = new Thickness(-gap / 2) };
        foreach (UiNode child in grid.Children)
        {
            FrameworkElement element = Build(child, insideCard);
            element.Margin = new Thickness(gap / 2);
            panel.Children.Add(element);
        }

        return panel;
    }

    private FrameworkElement BuildCard(UiCard card)
    {
        var stack = new StackPanel();
        bool hasHeader = !_flat && (card.Title is not null || card.Subtitle is not null || card.Icon is not null);
        if (hasHeader)
        {
            stack.Children.Add(BuildCardHeader(card));
        }

        bool first = true;
        foreach (UiNode child in card.Children)
        {
            FrameworkElement element = Build(child, insideCard: true);
            if (!first || hasHeader)
            {
                element.Margin = new Thickness(0, first ? 16 : _flat ? FlatContentGap : CardContentGap, 0, 0);
            }

            first = false;
            stack.Children.Add(element);
        }

        return _flat ? stack : Styled(new Border { Child = stack }, "Blocker.CardBorder");
    }

    private static FrameworkElement BuildCardHeader(UiCard card)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (card.Icon is { } icon)
        {
            SymbolIcon symbol = Icon(icon, 20, "Blocker.Accent");
            symbol.Margin = new Thickness(0, 1, 12, 0);
            symbol.VerticalAlignment = VerticalAlignment.Top;
            header.Children.Add(symbol);
        }

        var texts = new StackPanel();
        Grid.SetColumn(texts, 1);
        if (card.Title is not null)
        {
            texts.Children.Add(Styled(new TextBlock { Text = card.Title }, "Blocker.Text.CardTitle"));
        }

        if (card.Subtitle is not null)
        {
            TextBlock subtitle = Caption(card.Subtitle);
            subtitle.Margin = new Thickness(0, card.Title is null ? 0 : 2, 0, 0);
            texts.Children.Add(subtitle);
        }

        header.Children.Add(texts);
        return header;
    }

    private static FrameworkElement BuildText(UiText text)
    {
        TextBlock block = Styled(new TextBlock { Text = text.Text }, ToneMap.TextStyleKey(text.Style));
        if (text.Tone != UiTone.Neutral)
        {
            block.SetResourceReference(TextBlock.ForegroundProperty, ToneMap.ForegroundKey(text.Tone));
        }

        return block;
    }

    private static FrameworkElement BuildStatTile(UiStatTile tile, bool insideCard, bool flat)
    {
        var stack = new StackPanel();

        var labelRow = new Grid();
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelRow.Children.Add(Caption(tile.Label));
        if (tile.Icon is { } icon)
        {
            SymbolIcon symbol = Icon(icon, 16, "Blocker.Text.Muted");
            Grid.SetColumn(symbol, 1);
            symbol.Margin = new Thickness(8, 0, 0, 0);
            labelRow.Children.Add(symbol);
        }

        stack.Children.Add(labelRow);

        TextBlock value = Styled(new TextBlock { Text = tile.Value, Margin = new Thickness(0, 6, 0, 0) }, "Blocker.Text.Mono");
        value.FontSize = 28;
        value.FontWeight = FontWeights.SemiBold;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        if (tile.Tone != UiTone.Neutral)
        {
            value.SetResourceReference(TextBlock.ForegroundProperty, ToneMap.ForegroundKey(tile.Tone));
        }

        stack.Children.Add(value);

        if (!string.IsNullOrEmpty(tile.Caption))
        {
            TextBlock caption = Caption(tile.Caption);
            caption.Margin = new Thickness(0, 4, 0, 0);
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Blocker.Text.Muted");
            stack.Children.Add(caption);
        }

        string name = tile.Label + ": " + tile.Value;
        if (flat)
        {
            AutomationProperties.SetName(stack, name);
            return stack;
        }

        var border = Styled(new Border { Child = stack }, insideCard ? "Blocker.InsetBorder" : "Blocker.CardBorder");
        if (!insideCard)
        {
            border.Padding = new Thickness(16);
        }

        AutomationProperties.SetName(border, name);
        return border;
    }

    private static FrameworkElement BuildProgress(UiProgress progress)
    {
        double ratio = ChartScale.Ratio(progress.Value, progress.Maximum);
        var stack = new StackPanel();

        if (!string.IsNullOrEmpty(progress.Label))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Caption(progress.Label));
            TextBlock percent = Styled(new TextBlock { Text = (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + "%" }, "Blocker.Text.Mono");
            percent.FontSize = 12;
            percent.SetResourceReference(TextBlock.ForegroundProperty, "Blocker.Text.Secondary");
            Grid.SetColumn(percent, 1);
            row.Children.Add(percent);
            row.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(row);
        }

        var bar = new Grid { Height = 4 };
        var track = new Border { CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, "Blocker.Bg.Raised");
        bar.Children.Add(track);

        var fillHost = new Grid();
        fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
        fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
        if (ratio > 0)
        {
            var fill = new Border { CornerRadius = new CornerRadius(2) };
            fill.SetResourceReference(Border.BackgroundProperty, ToneMap.FillKey(progress.Tone));
            fillHost.Children.Add(fill);
        }

        bar.Children.Add(fillHost);
        stack.Children.Add(bar);

        AutomationProperties.SetName(stack, progress.Label ?? "Progress");
        AutomationProperties.SetHelpText(stack, (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + " percent");
        return stack;
    }

    private static FrameworkElement BuildBarChart(UiBarChart chart)
    {
        var element = new BarChartElement(chart.Points, chart.ValueSuffix) { HorizontalAlignment = HorizontalAlignment.Stretch };
        string summary = string.Join(", ", chart.Points.Select(p => p.Label + " " + ChartScale.FormatValue(p.Value, chart.ValueSuffix)));
        return WithChartTitle(element, chart.Title, summary);
    }

    private static FrameworkElement BuildLineChart(UiLineChart chart)
    {
        var element = new LineChartElement(chart.Series, chart.ValueSuffix) { HorizontalAlignment = HorizontalAlignment.Stretch };
        string summary = string.Join("; ", chart.Series.Select(s => s.Name + ": " + string.Join(", ", s.Points.Select(p => p.Label + " " + ChartScale.FormatValue(p.Value, chart.ValueSuffix)))));
        return WithChartTitle(element, chart.Title, summary);
    }

    private static FrameworkElement BuildDonutChart(UiDonutChart chart)
    {
        const double diameter = 160;
        double thickness = DonutMath.Thickness(diameter);
        double outer = diameter / 2;
        double inner = outer - thickness;
        double[] values = [.. chart.Slices.Select(s => s.Value)];
        IReadOnlyList<DonutArc> arcs = DonutMath.Arcs(values);
        double total = values.Where(v => double.IsFinite(v) && v > 0).Sum();

        var ring = new Grid { Width = diameter, Height = diameter, VerticalAlignment = VerticalAlignment.Center };
        var canvas = new Canvas { Width = diameter, Height = diameter };
        if (total <= 0)
        {
            var empty = new Ellipse { Width = diameter - thickness, Height = diameter - thickness, StrokeThickness = thickness };
            empty.SetResourceReference(Shape.StrokeProperty, "Blocker.Bg.Raised");
            Canvas.SetLeft(empty, thickness / 2);
            Canvas.SetTop(empty, thickness / 2);
            canvas.Children.Add(empty);
        }
        else
        {
            for (int i = 0; i < arcs.Count; i++)
            {
                if (arcs[i].Sweep <= 0)
                {
                    continue;
                }

                UiDataPoint slice = chart.Slices[i];
                var path = new Path
                {
                    Data = DonutSlice(outer, inner, arcs[i]),
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    ToolTip = slice.Label + ": " + ChartScale.FormatValue(slice.Value) + " (" + DonutMath.Percent(arcs[i].Fraction) + ")",
                };
                path.SetResourceReference(Shape.FillProperty, UiMetrics.SeriesKey(i));
                path.SetResourceReference(Shape.StrokeProperty, "Blocker.Bg.Card");
                canvas.Children.Add(path);
            }
        }

        ring.Children.Add(canvas);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        string centerValue = total <= 0 ? "No data" : chart.CenterValue ?? string.Empty;
        if (centerValue.Length > 0)
        {
            TextBlock big = Styled(new TextBlock { Text = centerValue, TextAlignment = TextAlignment.Center }, "Blocker.Text.Mono");
            big.FontSize = total <= 0 ? 12 : 26;
            big.FontWeight = FontWeights.SemiBold;
            big.SetResourceReference(TextBlock.ForegroundProperty, total <= 0 ? "Blocker.Text.Muted" : "Blocker.Text.Primary");
            center.Children.Add(big);
        }

        if (total > 0 && !string.IsNullOrEmpty(chart.CenterLabel))
        {
            TextBlock caption = Caption(chart.CenterLabel);
            caption.TextAlignment = TextAlignment.Center;
            caption.MaxWidth = inner * 1.6;
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            center.Children.Add(caption);
        }

        ring.Children.Add(center);

        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
        for (int i = 0; i < chart.Slices.Count; i++)
        {
            UiDataPoint slice = chart.Slices[i];
            var row = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var swatch = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            swatch.SetResourceReference(Border.BackgroundProperty, UiMetrics.SeriesKey(i));
            TextBlock name = Caption(slice.Label);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(0, 0, 16, 0);
            name.SetResourceReference(TextBlock.ForegroundProperty, "Blocker.Text.Secondary");
            string text = ChartScale.FormatValue(slice.Value) + (total > 0 ? "  " + DonutMath.Percent(arcs[i].Fraction) : string.Empty);
            TextBlock value = Styled(new TextBlock { Text = text }, "Blocker.Text.Mono");
            value.FontSize = 12;
            Grid.SetColumn(name, 1);
            Grid.SetColumn(value, 2);
            row.Children.Add(swatch);
            row.Children.Add(name);
            row.Children.Add(value);
            legend.Children.Add(row);
        }

        var layout = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(legend, 1);
        layout.Children.Add(ring);
        layout.Children.Add(legend);

        string summary = total <= 0
            ? "No data"
            : string.Join(", ", chart.Slices.Select((s, i) => s.Label + " " + ChartScale.FormatValue(s.Value) + " (" + DonutMath.Percent(arcs[i].Fraction) + ")"));
        return WithChartTitle(layout, chart.Title, summary);
    }

    private static Geometry DonutSlice(double outer, double inner, DonutArc arc)
    {
        double end = arc.StartAngle + arc.Sweep;
        (double X, double Y) p1 = DonutMath.ArcPoint(outer, outer, outer, arc.StartAngle);
        (double X, double Y) p2 = DonutMath.ArcPoint(outer, outer, outer, end);
        (double X, double Y) p3 = DonutMath.ArcPoint(outer, outer, inner, end);
        (double X, double Y) p4 = DonutMath.ArcPoint(outer, outer, inner, arc.StartAngle);
        bool large = arc.Sweep > 180;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(p1.X, p1.Y), true, true);
            ctx.ArcTo(new Point(p2.X, p2.Y), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(p3.X, p3.Y), true, false);
            ctx.ArcTo(new Point(p4.X, p4.Y), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static FrameworkElement BuildHeatmap(UiHeatmap map)
    {
        const double cell = 16;
        const double gap = 3;
        double max = HeatmapMath.Max(map.Values);
        int rows = map.RowLabels.Count;
        int cols = map.ColumnLabels.Count;

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int c = 0; c < cols; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cell + gap) });
        }

        for (int r = 0; r <= rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = r == 0 ? GridLength.Auto : new GridLength(cell + gap) });
        }

        for (int c = 0; c < cols; c++)
        {
            if (string.IsNullOrEmpty(map.ColumnLabels[c]))
            {
                continue;
            }

            TextBlock label = Caption(map.ColumnLabels[c]);
            label.FontSize = 10;
            label.TextWrapping = TextWrapping.NoWrap;
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.Margin = new Thickness(0, 0, 0, 4);
            Grid.SetColumn(label, c + 1);
            grid.Children.Add(label);
        }

        for (int r = 0; r < rows; r++)
        {
            if (!string.IsNullOrEmpty(map.RowLabels[r]))
            {
                TextBlock label = Caption(map.RowLabels[r]);
                label.FontSize = 10;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetRow(label, r + 1);
                grid.Children.Add(label);
            }

            for (int c = 0; c < cols; c++)
            {
                double value = r < map.Values.Count && c < map.Values[r].Count ? map.Values[r][c] : 0;
                Border box = HeatCell(HeatmapMath.Intensity(value, max), cell);
                box.Margin = new Thickness(0, 0, gap, gap);
                box.ToolTip = map.RowLabels[r] + " · " + map.ColumnLabels[c] + ": " + ChartScale.FormatValue(value, map.ValueSuffix);
                Grid.SetRow(box, r + 1);
                Grid.SetColumn(box, c + 1);
                grid.Children.Add(box);
            }
        }

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        TextBlock less = Caption("Less");
        less.FontSize = 10;
        less.Margin = new Thickness(0, 0, 6, 0);
        less.VerticalAlignment = VerticalAlignment.Center;
        legend.Children.Add(less);
        foreach (double step in new[] { 0, 0.33, 0.66, 1 })
        {
            Border swatch = HeatCell(step, 12);
            swatch.Margin = new Thickness(0, 0, 3, 0);
            legend.Children.Add(swatch);
        }

        TextBlock more = Caption("More");
        more.FontSize = 10;
        more.Margin = new Thickness(3, 0, 0, 0);
        more.VerticalAlignment = VerticalAlignment.Center;
        legend.Children.Add(more);

        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(legend);
        string summary = string.Join("; ", map.RowLabels.Select((row, r) => row + ": " + string.Join(", ", map.Values[r].Select(v => ChartScale.FormatValue(v, map.ValueSuffix)))));
        return WithChartTitle(stack, map.Title, summary);
    }

    private static Border HeatCell(double intensity, double size)
    {
        var box = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(2) };
        if (intensity <= 0)
        {
            box.Background = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1F)));
        }
        else
        {
            (byte r, byte g, byte b) = HeatmapMath.Blend(intensity);
            box.Background = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
            box.Opacity = HeatmapMath.Opacity(intensity);
        }

        return box;
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static FrameworkElement BuildGlyph(UiGlyph glyph)
    {
        Geometry geometry;
        try
        {
            geometry = Geometry.Parse(glyph.PathData);
            geometry.Freeze();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            return Caption("Logo unavailable.");
        }

        var path = new Path
        {
            Data = geometry,
            Stretch = Stretch.Uniform,
            Width = glyph.Size,
            Height = glyph.Size,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        path.SetResourceReference(Shape.FillProperty, glyph.Tone == UiTone.Accent ? "Blocker.Logo.Brush" : ToneMap.FillKey(glyph.Tone));
        AutomationProperties.SetName(path, "Logo");
        return path;
    }

    private static FrameworkElement WithChartTitle(FrameworkElement chart, string? title, string summary)
    {
        AutomationProperties.SetName(chart, title ?? "Chart");
        AutomationProperties.SetHelpText(chart, summary);
        if (string.IsNullOrEmpty(title))
        {
            return chart;
        }

        var stack = new StackPanel();
        TextBlock heading = Styled(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 10) }, "Blocker.Text.CardTitle");
        stack.Children.Add(heading);
        stack.Children.Add(chart);
        return stack;
    }

    private static FrameworkElement BuildTable(UiTable table)
    {
        var grid = new Grid();
        int columns = table.Columns.Count;
        foreach (UiTableColumn column in table.Columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = column.Alignment == UiAlignment.End ? GridLength.Auto : new GridLength(1, GridUnitType.Star),
                MinWidth = column.Alignment == UiAlignment.End ? 72 : 0,
            });
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRowDivider(grid, 0, columns, "Blocker.Border.Strong");
        for (int c = 0; c < columns; c++)
        {
            UiTableColumn column = table.Columns[c];
            TextBlock header = Caption(column.Header);
            header.FontWeight = FontWeights.SemiBold;
            header.SetResourceReference(TextBlock.ForegroundProperty, "Blocker.Text.Muted");
            PlaceCell(grid, header, 0, c, column.Alignment);
        }

        for (int r = 0; r < table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int rowIndex = r + 1;
            AddRowDivider(grid, rowIndex, columns, "Blocker.Border.Subtle");
            IReadOnlyList<string> cells = table.Rows[r].Cells;
            for (int c = 0; c < columns && c < cells.Count; c++)
            {
                UiTableColumn column = table.Columns[c];
                TextBlock cell = Styled(new TextBlock { Text = cells[c] }, column.Monospace ? "Blocker.Text.Mono" : "Blocker.Text.Body");
                PlaceCell(grid, cell, rowIndex, c, column.Alignment);
            }
        }

        AutomationProperties.SetName(grid, "Table");
        return grid;
    }

    private static void AddRowDivider(Grid grid, int row, int columns, string brushKey)
    {
        var divider = new Border { BorderThickness = new Thickness(0, 0, 0, 1), IsHitTestVisible = false };
        divider.SetResourceReference(Border.BorderBrushProperty, brushKey);
        Grid.SetRow(divider, row);
        Grid.SetColumnSpan(divider, columns);
        grid.Children.Add(divider);
    }

    private static void PlaceCell(Grid grid, TextBlock cell, int row, int column, UiAlignment alignment)
    {
        cell.Margin = new Thickness(0, 9, column == grid.ColumnDefinitions.Count - 1 ? 0 : 20, 9);
        cell.HorizontalAlignment = alignment switch
        {
            UiAlignment.Center => HorizontalAlignment.Center,
            UiAlignment.End => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
        cell.TextAlignment = alignment switch
        {
            UiAlignment.Center => TextAlignment.Center,
            UiAlignment.End => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private FrameworkElement BuildButton(UiButton node)
    {
        var button = new Button { IsEnabled = node.IsEnabled, HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, ToneMap.ButtonStyleKey(node.Variant));
        button.Content = ButtonContent(node.Label, node.Icon);
        AutomationProperties.SetName(button, node.Label);
        AutomationProperties.SetAutomationId(button, node.Id);
        string id = node.Id;
        button.Click += (_, _) => _raise(UiAction.Click(id));
        return button;
    }

    private static object ButtonContent(string label, UiIcon? icon)
    {
        if (icon is not { } value)
        {
            return label;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        SymbolIcon symbol = Icon(value, 16);
        symbol.Margin = new Thickness(0, 0, 8, 0);
        symbol.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(symbol);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private FrameworkElement BuildToggle(UiToggle node)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        texts.Children.Add(Styled(new TextBlock { Text = node.Label }, "Blocker.Text.Body"));
        if (!string.IsNullOrEmpty(node.Description))
        {
            TextBlock description = Caption(node.Description);
            description.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(description);
        }

        row.Children.Add(texts);

        var toggle = new ToggleSwitch { IsChecked = node.IsOn, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(toggle, 1);
        AutomationProperties.SetName(toggle, node.Label);
        AutomationProperties.SetAutomationId(toggle, node.Id);
        string id = node.Id;
        toggle.Checked += (_, _) => _raise(UiAction.Toggle(id, true));
        toggle.Unchecked += (_, _) => _raise(UiAction.Toggle(id, false));
        row.Children.Add(toggle);
        return row;
    }

    private FrameworkElement BuildForm(UiForm form)
    {
        var stack = new StackPanel { MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left };
        var readers = new List<(string Id, Func<string> Read)>();

        foreach (UiFormField field in form.Fields)
        {
            var group = new StackPanel { Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 14, 0, 0) };
            (FrameworkElement control, Func<string> read, bool labelled) = BuildField(field);
            if (!labelled)
            {
                TextBlock label = Caption(field.Label);
                label.Margin = new Thickness(0, 0, 0, 6);
                group.Children.Add(label);
            }

            AutomationProperties.SetName(control, field.Label);
            AutomationProperties.SetAutomationId(control, field.Id);
            group.Children.Add(control);
            stack.Children.Add(group);
            readers.Add((field.Id, read));
        }

        var submit = new Button { Content = form.SubmitLabel, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 20, 0, 0) };
        submit.SetResourceReference(FrameworkElement.StyleProperty, "Blocker.Button.Primary");
        AutomationProperties.SetAutomationId(submit, form.Id);
        string formId = form.Id;
        submit.Click += (_, _) =>
        {
            var values = new Dictionary<string, string>(readers.Count, StringComparer.Ordinal);
            foreach ((string id, Func<string> read) in readers)
            {
                values[id] = read();
            }

            _raise(UiAction.Submit(formId, values));
        };
        stack.Children.Add(submit);
        return stack;
    }

    private static (FrameworkElement Control, Func<string> Read, bool Labelled) BuildField(UiFormField field)
    {
        switch (field)
        {
            case UiTextField text:
                var box = new WpfUiTextBox { Text = text.Value, MaxLength = text.MaxLength, PlaceholderText = text.Placeholder ?? string.Empty };
                return (box, () => box.Text, false);

            case UiNumberField number:
                var numberBox = new NumberBox
                {
                    Minimum = number.Minimum,
                    Maximum = number.Maximum,
                    SmallChange = number.Step,
                    LargeChange = number.Step * 10,
                    MaxDecimalPlaces = DecimalPlaces(number.Step),
                    Value = Math.Clamp(number.Value, number.Minimum, number.Maximum),
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
                };
                return (numberBox, () => Math.Clamp(numberBox.Value ?? number.Value, number.Minimum, number.Maximum).ToString("R", CultureInfo.InvariantCulture), false);

            case UiSelectField select:
                var combo = new ComboBox
                {
                    ItemsSource = select.Options,
                    DisplayMemberPath = nameof(UiOption.Label),
                    SelectedValuePath = nameof(UiOption.Value),
                    SelectedValue = select.SelectedValue,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                return (combo, () => combo.SelectedValue as string ?? string.Empty, false);

            case UiCheckboxField check:
                var checkBox = new CheckBox { Content = check.Label, IsChecked = check.IsChecked };
                return (checkBox, () => checkBox.IsChecked == true ? "true" : "false", true);

            default:
                return (Caption("Unsupported field."), () => string.Empty, true);
        }
    }

    private static int DecimalPlaces(double step)
    {
        for (int digits = 0; digits < 6; digits++)
        {
            double scaled = step * Math.Pow(10, digits);
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9)
            {
                return digits;
            }
        }

        return 6;
    }
}
