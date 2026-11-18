using Blocker.UI.ViewModels;
using Blocker.UI.Views;

namespace Blocker.UI.Tests;

public sealed class WidgetGridLayoutTests
{
    [Theory]
    [InlineData(100, 1)]
    [InlineData(260, 1)]
    [InlineData(531, 1)]
    [InlineData(532, 2)]
    [InlineData(803, 2)]
    [InlineData(804, 3)]
    [InlineData(1076, 4)]
    [InlineData(5000, 4)]
    public void Column_count_follows_the_width(double width, int expected) =>
        Assert.Equal(expected, WidgetGridLayout.ColumnCount(width));

    [Fact]
    public void An_unbounded_width_gets_two_columns() =>
        Assert.Equal(2, WidgetGridLayout.ColumnCount(double.PositiveInfinity));

    [Fact]
    public void Cell_width_shares_the_width_after_the_gaps() =>
        Assert.Equal(260, WidgetGridLayout.CellWidth((2 * 260) + WidgetSpans.Gap, 2), 6);

    [Fact]
    public void Single_cells_fill_rows_left_to_right()
    {
        GridSlot[] slots = WidgetGridLayout.Pack([(1, 1), (1, 1), (1, 1)], 2);

        Assert.Equal([new GridSlot(0, 0, 1, 1), new GridSlot(1, 0, 1, 1), new GridSlot(0, 1, 1, 1)], slots);
    }

    [Fact]
    public void Later_widgets_fill_gaps_left_by_earlier_ones()
    {
        // The wide widget does not fit beside the first one, so it starts the next row; the third widget takes the gap.
        GridSlot[] slots = WidgetGridLayout.Pack([(1, 1), (2, 1), (1, 1)], 2);

        Assert.Equal([new GridSlot(0, 0, 1, 1), new GridSlot(0, 1, 2, 1), new GridSlot(1, 0, 1, 1)], slots);
    }

    [Fact]
    public void Tall_widgets_block_the_rows_they_cover()
    {
        GridSlot[] slots = WidgetGridLayout.Pack([(1, 3), (1, 1), (1, 1), (1, 1), (1, 1)], 2);

        Assert.Equal(
            [new GridSlot(0, 0, 1, 3), new GridSlot(1, 0, 1, 1), new GridSlot(1, 1, 1, 1), new GridSlot(1, 2, 1, 1), new GridSlot(0, 3, 1, 1)],
            slots);
        Assert.Equal(4, WidgetGridLayout.RowCount(slots));
    }

    [Fact]
    public void Spans_wider_than_the_board_are_narrowed_to_it()
    {
        GridSlot[] slots = WidgetGridLayout.Pack([(2, 2), (2, 1)], 1);

        Assert.Equal([new GridSlot(0, 0, 1, 2), new GridSlot(0, 2, 1, 1)], slots);
    }

    [Fact]
    public void Nothing_to_pack_has_no_rows_and_no_height()
    {
        Assert.Empty(WidgetGridLayout.Pack([], 3));
        Assert.Equal(0, WidgetGridLayout.RowCount([]));
        Assert.Equal(0, WidgetGridLayout.Height(0));
    }

    [Fact]
    public void Height_is_the_rows_plus_the_gaps_between_them()
    {
        Assert.Equal(WidgetSpans.CellHeight, WidgetGridLayout.Height(1));
        Assert.Equal((3 * WidgetSpans.CellHeight) + (2 * WidgetSpans.Gap), WidgetGridLayout.Height(3));
    }

    [Theory]
    [InlineData(100, 60, 1, 1)]
    [InlineData(260, 140, 1, 1)]
    [InlineData(400, 140, 2, 1)]
    [InlineData(532, 292, 2, 2)]
    [InlineData(260, 440, 1, 3)]
    [InlineData(900, 900, 2, 3)]
    [InlineData(-20, -20, 1, 1)]
    public void A_dragged_size_snaps_to_the_nearest_whole_spans(double width, double height, int cols, int rows) =>
        Assert.Equal((cols, rows), WidgetGridLayout.SnapSpan(width, height, 260, 3));

    [Fact]
    public void A_dragged_width_never_exceeds_the_boards_columns() =>
        Assert.Equal((1, 1), WidgetGridLayout.SnapSpan(900, 100, 400, 1));
}

public sealed class WidgetSpansTests
{
    [Theory]
    [InlineData(1, 1, "1 × 1")]
    [InlineData(2, 3, "2 × 3")]
    public void Spans_are_labelled_columns_by_rows(int cols, int rows, string expected) =>
        Assert.Equal(expected, WidgetSpans.Label(cols, rows));

    [Fact]
    public void Slot_sizes_include_the_gaps_between_the_cells()
    {
        Assert.Equal(WidgetSpans.CellMinWidth, WidgetSpans.SlotWidth(1));
        Assert.Equal((2 * WidgetSpans.CellMinWidth) + WidgetSpans.Gap, WidgetSpans.SlotWidth(2));
        Assert.Equal((2 * WidgetSpans.CellHeight) + WidgetSpans.Gap, WidgetSpans.SlotHeight(2));
    }
}
