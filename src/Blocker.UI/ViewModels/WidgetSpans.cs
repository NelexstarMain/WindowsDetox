using System.Globalization;
using Blocker.UI.Sdk;
using Blocker.UI.Services;

namespace Blocker.UI.ViewModels;

/// <summary>Grid metrics shared by the board panel and the picker, and the size a new widget starts with.</summary>
internal static class WidgetSpans
{
    /// <summary>Height of one grid row in DIPs.</summary>
    public const double CellHeight = 140;

    /// <summary>Smallest width of one grid column in DIPs.</summary>
    public const double CellMinWidth = 260;

    /// <summary>Gap between cells in DIPs.</summary>
    public const double Gap = 12;

    // Widget surface padding plus the host header (icon and name), and the gap host content uses between flat nodes.
    private const double ChromeHeight = 52;
    private const double FlatGap = 10;

    // Content may overrun a row by this much before another row is added: the cell clips and fades its bottom edge, and the estimate
    // errs on the tall side, so a widget that nearly fits should start one row high instead of leaving a large empty area.
    private const double Slack = 40;

    /// <summary>Gets the placement a widget starts with: one column, as many rows as its content needs.</summary>
    /// <param name="contribution">The widget.</param>
    /// <returns>The placement.</returns>
    public static WidgetPlacement DefaultPlacement(ContributionViewModel contribution) => new(contribution.Key, 1, DefaultRows(contribution.Node));

    /// <summary>Estimates how many rows a rendered widget tree needs (one unless it clearly needs more), from 1 to <see cref="WidgetPlacement.MaxRows"/>.</summary>
    /// <param name="node">The widget tree.</param>
    /// <returns>The row span.</returns>
    public static int DefaultRows(UiNode node) =>
        WidgetPlacement.ClampRows((int)Math.Ceiling((ChromeHeight + Height(node) - Slack + Gap) / (CellHeight + Gap)));

    /// <summary>Formats a span for people, for example <c>2 × 1</c> (columns by rows).</summary>
    /// <param name="cols">Columns.</param>
    /// <param name="rows">Rows.</param>
    /// <returns>The label.</returns>
    public static string Label(int cols, int rows) => string.Create(CultureInfo.InvariantCulture, $"{cols} × {rows}");

    /// <summary>Gets the width in DIPs of a widget that spans <paramref name="cols"/> columns of minimum width.</summary>
    /// <param name="cols">Columns.</param>
    /// <returns>The width.</returns>
    public static double SlotWidth(int cols) => (cols * CellMinWidth) + ((cols - 1) * Gap);

    /// <summary>Gets the height in DIPs of a widget that spans <paramref name="rows"/> rows.</summary>
    /// <param name="rows">Rows.</param>
    /// <returns>The height.</returns>
    public static double SlotHeight(int rows) => (rows * CellHeight) + ((rows - 1) * Gap);

    private static double Height(UiNode node) => node switch
    {
        UiPage page => Stack(page.Children),
        UiCard card => Stack(card.Children),
        UiStack { Orientation: UiOrientation.Horizontal } row => row.Children.Select(Height).DefaultIfEmpty(0).Max(),
        UiStack column => Stack(column.Children),
        UiGrid grid => Rows(grid),
        UiText { Style: UiTextStyle.Caption } => 16,
        UiText => 22,
        UiStatTile tile => 60 + (string.IsNullOrEmpty(tile.Caption) ? 0 : 20),
        UiProgress progress => string.IsNullOrEmpty(progress.Label) ? 6 : 30,
        UiToggle toggle => 32 + (string.IsNullOrEmpty(toggle.Description) ? 0 : 18),
        UiButton => 34,
        UiBarChart or UiLineChart => 130,
        UiDonutChart => 160,
        UiHeatmap => 150,
        UiTable table => 30 * (table.Rows.Count + 1),
        UiMonthGrid month => (string.IsNullOrEmpty(month.Title) ? 0 : month.IsCompact ? 38 : 50) + (month.IsCompact ? 18 : 24) + (month.Cells.Count / 7 * (month.IsCompact ? 32 : 50)),
        UiGlyph glyph => glyph.Size,
        UiForm form => (60 * form.Fields.Count) + 40,
        _ => 40,
    };

    private static double Stack(IReadOnlyList<UiNode> children) =>
        children.Sum(Height) + (FlatGap * Math.Max(0, children.Count - 1));

    private static double Rows(UiGrid grid)
    {
        int columns = Math.Max(1, grid.Columns);
        double height = 0;
        int rows = 0;
        for (int i = 0; i < grid.Children.Count; i += columns)
        {
            height += grid.Children.Skip(i).Take(columns).Max(Height);
            rows++;
        }

        return height + (FlatGap * Math.Max(0, rows - 1));
    }
}
