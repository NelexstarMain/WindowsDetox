using Blocker.UI.Services;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>Where a widget sits on the board grid.</summary>
/// <param name="Column">Zero-based first column.</param>
/// <param name="Row">Zero-based first row.</param>
/// <param name="ColumnSpan">Columns it covers.</param>
/// <param name="RowSpan">Rows it covers.</param>
internal readonly record struct GridSlot(int Column, int Row, int ColumnSpan, int RowSpan);

/// <summary>Pure grid maths of the dashboard board: column count, first-fit packing and snapping a dragged size to spans.</summary>
internal static class WidgetGridLayout
{
    /// <summary>Most columns the board uses, however wide the window is.</summary>
    public const int MaxColumns = 4;

    /// <summary>Gets the number of columns that fit <paramref name="width"/> (at least one).</summary>
    /// <param name="width">Available width in DIPs.</param>
    /// <returns>A value from 1 to <see cref="MaxColumns"/>.</returns>
    public static int ColumnCount(double width) =>
        double.IsFinite(width) ? Math.Clamp((int)Math.Floor((width + WidgetSpans.Gap) / (WidgetSpans.CellMinWidth + WidgetSpans.Gap)), 1, MaxColumns) : 2;

    /// <summary>Gets the width of one column once the available width is shared out.</summary>
    /// <param name="width">Available width in DIPs.</param>
    /// <param name="columns">Number of columns.</param>
    /// <returns>The column width.</returns>
    public static double CellWidth(double width, int columns) => Math.Max(0, (width - (WidgetSpans.Gap * (columns - 1))) / columns);

    /// <summary>
    /// Places widgets in order, each at the first free spot that fits (scanning rows top to bottom, then columns left to right),
    /// so later widgets fill gaps left by earlier ones, as on an Android home screen.
    /// </summary>
    /// <param name="spans">Requested columns and rows of each widget, in display order.</param>
    /// <param name="columns">Number of columns; wider requests are narrowed to it.</param>
    /// <returns>One slot per widget.</returns>
    public static GridSlot[] Pack(IReadOnlyList<(int Cols, int Rows)> spans, int columns)
    {
        List<bool[]> occupied = [];
        GridSlot[] slots = new GridSlot[spans.Count];
        for (int i = 0; i < spans.Count; i++)
        {
            int cols = Math.Clamp(spans[i].Cols, 1, columns);
            int rows = Math.Max(1, spans[i].Rows);
            slots[i] = Place(occupied, columns, cols, rows);
        }

        return slots;
    }

    /// <summary>
    /// Stretches every widget that has nothing below it down to the board's last row, so the bottom edge of the board is flush
    /// instead of ragged. Widgets that already reach the bottom, or have a neighbour below, are left alone.
    /// </summary>
    /// <param name="slots">Packed slots.</param>
    /// <returns>The slots with the free space under them taken up.</returns>
    public static GridSlot[] FillBottom(IReadOnlyList<GridSlot> slots)
    {
        int rows = RowCount(slots);
        GridSlot[] filled = [.. slots];
        for (int i = 0; i < filled.Length; i++)
        {
            GridSlot slot = filled[i];
            int bottom = slot.Row + slot.RowSpan;
            bool clear = bottom < rows && !filled.Where((_, j) => j != i).Any(other =>
                other.Row < rows && other.Row + other.RowSpan > bottom && other.Column < slot.Column + slot.ColumnSpan && other.Column + other.ColumnSpan > slot.Column);
            if (clear)
            {
                filled[i] = slot with { RowSpan = rows - slot.Row };
            }
        }

        return filled;
    }

    /// <summary>Gets the number of rows the slots cover.</summary>
    /// <param name="slots">The slots.</param>
    /// <returns>The row count, zero for none.</returns>
    public static int RowCount(IReadOnlyList<GridSlot> slots) => slots.Count == 0 ? 0 : slots.Max(s => s.Row + s.RowSpan);

    /// <summary>Gets the height of <paramref name="rows"/> rows including the gaps between them.</summary>
    /// <param name="rows">Row count.</param>
    /// <returns>The height in DIPs.</returns>
    public static double Height(int rows) => rows <= 0 ? 0 : WidgetSpans.SlotHeight(rows);

    /// <summary>Snaps a dragged size to the nearest whole spans.</summary>
    /// <param name="width">Dragged width in DIPs, measured from the widget's top-left corner.</param>
    /// <param name="height">Dragged height in DIPs.</param>
    /// <param name="cellWidth">Width of one column.</param>
    /// <param name="columns">Number of columns on the board.</param>
    /// <returns>Columns (at most <see cref="WidgetPlacement.MaxCols"/> and the board's columns) and rows (at most <see cref="WidgetPlacement.MaxRows"/>).</returns>
    public static (int Cols, int Rows) SnapSpan(double width, double height, double cellWidth, int columns)
    {
        int cols = (int)Math.Round((width + WidgetSpans.Gap) / (cellWidth + WidgetSpans.Gap));
        int rows = (int)Math.Round((height + WidgetSpans.Gap) / (WidgetSpans.CellHeight + WidgetSpans.Gap));
        return (Math.Clamp(cols, 1, Math.Min(WidgetPlacement.MaxCols, columns)), WidgetPlacement.ClampRows(rows));
    }

    private static GridSlot Place(List<bool[]> occupied, int columns, int cols, int rows)
    {
        for (int row = 0; ; row++)
        {
            for (int column = 0; column + cols <= columns; column++)
            {
                if (IsFree(occupied, column, row, cols, rows))
                {
                    for (int r = row; r < row + rows; r++)
                    {
                        EnsureRow(occupied, columns, r);
                        for (int c = column; c < column + cols; c++)
                        {
                            occupied[r][c] = true;
                        }
                    }

                    return new GridSlot(column, row, cols, rows);
                }
            }
        }
    }

    private static bool IsFree(List<bool[]> occupied, int column, int row, int cols, int rows)
    {
        for (int r = row; r < row + rows && r < occupied.Count; r++)
        {
            for (int c = column; c < column + cols; c++)
            {
                if (occupied[r][c])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void EnsureRow(List<bool[]> occupied, int columns, int row)
    {
        while (occupied.Count <= row)
        {
            occupied.Add(new bool[columns]);
        }
    }
}
