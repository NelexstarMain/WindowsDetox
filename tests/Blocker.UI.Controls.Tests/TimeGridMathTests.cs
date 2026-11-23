using Blocker.UI.Controls.Rendering;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Tests;

public sealed class TimeGridMathTests
{
    private static readonly TimeGridGeometry Week = new(56, 700, 7, 48, 15);

    private static UiTimeGridEvent Event(string id, int day, int start, int end) => new(id, day, start, end, id);

    [Fact]
    public void Disjoint_events_keep_the_full_width()
    {
        ColumnSlot[] slots = TimeGridMath.PackColumns([Event("a", 0, 60, 120), Event("b", 0, 120, 180), Event("c", 1, 60, 180)]);

        Assert.All(slots, s => Assert.Equal(new ColumnSlot(0, 1), s));
    }

    [Fact]
    public void Overlapping_events_split_the_width()
    {
        ColumnSlot[] slots = TimeGridMath.PackColumns([Event("a", 0, 540, 600), Event("b", 0, 570, 630)]);

        Assert.Equal(new ColumnSlot(0, 2), slots[0]);
        Assert.Equal(new ColumnSlot(1, 2), slots[1]);
    }

    [Fact]
    public void A_lane_is_reused_once_its_event_has_ended_and_the_cluster_keeps_its_width()
    {
        // a spans the whole cluster; b and c share the second lane one after the other.
        ColumnSlot[] slots = TimeGridMath.PackColumns([Event("a", 0, 540, 660), Event("b", 0, 540, 600), Event("c", 0, 600, 660)]);

        Assert.Equal(new ColumnSlot(0, 2), slots[0]);
        Assert.Equal(new ColumnSlot(1, 2), slots[1]);
        Assert.Equal(new ColumnSlot(1, 2), slots[2]);
    }

    [Fact]
    public void Three_way_overlap_uses_three_lanes_and_a_later_event_starts_a_new_cluster()
    {
        ColumnSlot[] slots = TimeGridMath.PackColumns(
        [
            Event("a", 0, 540, 600),
            Event("b", 0, 545, 600),
            Event("c", 0, 550, 600),
            Event("later", 0, 600, 660),
        ]);

        Assert.Equal([new ColumnSlot(0, 3), new ColumnSlot(1, 3), new ColumnSlot(2, 3), new ColumnSlot(0, 1)], slots);
    }

    [Fact]
    public void Packing_is_per_day_and_independent_of_input_order()
    {
        ColumnSlot[] slots = TimeGridMath.PackColumns([Event("late", 1, 570, 630), Event("early", 1, 540, 600), Event("other-day", 0, 540, 600)]);

        Assert.Equal(new ColumnSlot(1, 2), slots[0]);
        Assert.Equal(new ColumnSlot(0, 2), slots[1]);
        Assert.Equal(new ColumnSlot(0, 1), slots[2]);
    }

    [Fact]
    public void Longer_event_takes_the_first_lane_when_starts_are_equal()
    {
        ColumnSlot[] slots = TimeGridMath.PackColumns([Event("short", 0, 540, 570), Event("long", 0, 540, 660)]);

        Assert.Equal(0, slots[1].Column);
        Assert.Equal(1, slots[0].Column);
    }

    [Fact]
    public void Block_rect_places_events_in_their_day_and_lane()
    {
        RectD wide = TimeGridMath.BlockRect(Week, new TimeSlot(2, 540, 600), 0, 1);
        RectD second = TimeGridMath.BlockRect(Week, new TimeSlot(2, 540, 600), 1, 2);

        Assert.Equal(56 + 200 + TimeGridMath.ColumnInset, wide.X, 6);
        Assert.Equal(Week.ColumnX(3) - TimeGridMath.ColumnInset, wide.Right, 6);
        Assert.Equal(TimeGridMath.LaneGutter, second.X - (wide.X + wide.Width), 6);
        Assert.Equal((540 * 0.8) + 1, wide.Y, 6);
        Assert.Equal(48 - 2, wide.Height, 6);
        Assert.True(second.X > wide.X + (wide.Width / 2) - 1);
        Assert.True(second.Right <= Week.ColumnX(3));
    }

    [Fact]
    public void Tiny_events_keep_a_minimum_height_and_show_only_the_title()
    {
        RectD tiny = TimeGridMath.BlockRect(Week, new TimeSlot(0, 600, 605), 0, 1);

        Assert.Equal(TimeGridMath.MinEventHeight, tiny.Height);
        Assert.False(TimeGridMath.ShowsDetail(tiny.Height));
        Assert.True(TimeGridMath.ShowsDetail(TimeGridMath.BlockRect(Week, new TimeSlot(0, 600, 660), 0, 1).Height));
    }

    [Theory]
    [InlineData("10:30–11:00", "10:30")]
    [InlineData("10:30 – 11:00", "10:30")]
    [InlineData("9:00 AM-10:00 AM", "9:00 AM")]
    [InlineData("14:00", "14:00")]
    [InlineData("", "")]
    public void The_one_line_time_of_a_short_block_is_the_start_of_its_range(string detail, string expected) =>
        Assert.Equal(expected, TimeGridMath.StartOf(detail));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(480, 384)]
    [InlineData(1440, 1152)]
    public void Minute_and_pixel_convert_both_ways(double minute, double y)
    {
        Assert.Equal(y, Week.YOf(minute), 6);
        Assert.Equal(minute, Week.MinuteAt(y), 6);
    }

    [Fact]
    public void Pixel_to_minute_is_clamped_to_the_day()
    {
        Assert.Equal(0, Week.MinuteAt(-50));
        Assert.Equal(1440, Week.MinuteAt(5000));
    }

    [Theory]
    [InlineData(7, 0)]
    [InlineData(10, 15)]
    [InlineData(22.5, 30)]
    [InlineData(-3, 0)]
    public void Snap_rounds_to_the_nearest_step(double minutes, int expected) => Assert.Equal(expected, TimeGridMath.Snap(minutes, 15));

    [Fact]
    public void Snap_down_and_up_bracket_the_value()
    {
        Assert.Equal(540, TimeGridMath.SnapDown(547, 15));
        Assert.Equal(555, TimeGridMath.SnapUp(547, 15));
        Assert.Equal(555, TimeGridMath.SnapUp(555, 15));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(10, 0)]
    [InlineData(100, 1)]
    [InlineData(699, 6)]
    [InlineData(900, 6)]
    public void Day_at_is_clamped_to_the_columns(double x, int expected) => Assert.Equal(expected, Week.DayAt(x + 56));

    [Fact]
    public void Create_range_snaps_outwards_and_is_at_least_one_step()
    {
        Assert.Equal((540, 585), TimeGridMath.CreateRange(547, 580, 15));
        Assert.Equal((540, 585), TimeGridMath.CreateRange(580, 547, 15));
        Assert.Equal((540, 555), TimeGridMath.CreateRange(542, 542, 15));
        Assert.Equal((540, 555), TimeGridMath.CreateRange(540, 540, 15));
    }

    [Fact]
    public void Create_range_is_clamped_to_the_day()
    {
        Assert.Equal((0, 15), TimeGridMath.CreateRange(0, 0, 15));
        Assert.Equal((1425, 1440), TimeGridMath.CreateRange(1440, 1440, 15));
        Assert.Equal((1380, 1440), TimeGridMath.CreateRange(1380, 1500, 15));
    }

    [Theory]
    [InlineData(547.0, 540, 600)]
    [InlineData(0.0, 0, 60)]
    [InlineData(1439.0, 1380, 1440)]
    public void Click_creates_an_hour_at_the_snapped_slot(double minute, int start, int end) =>
        Assert.Equal((start, end), TimeGridMath.ClickCreateRange(minute, 15));

    [Fact]
    public void Move_keeps_duration_snaps_the_start_and_clamps_inside_the_day()
    {
        Assert.Equal((600, 690), TimeGridMath.MoveRange(540, 630, 58, 15));
        Assert.Equal((0, 90), TimeGridMath.MoveRange(540, 630, -900, 15));
        Assert.Equal((1350, 1440), TimeGridMath.MoveRange(540, 630, 900, 15));
    }

    [Fact]
    public void Resize_snaps_the_end_keeps_one_step_minimum_and_clamps()
    {
        Assert.Equal(660, TimeGridMath.ResizeEnd(540, 630, 31, 15));
        Assert.Equal(555, TimeGridMath.ResizeEnd(540, 630, -500, 15));
        Assert.Equal(1440, TimeGridMath.ResizeEnd(540, 630, 5000, 15));
        Assert.Equal(1440, TimeGridMath.ResizeEnd(1439, 1440, -10, 15));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(545, "09:05")]
    [InlineData(1439, "23:59")]
    [InlineData(1440, "24:00")]
    public void Times_are_formatted_in_24_hours(int minute, string expected) => Assert.Equal(expected, TimeGridMath.FormatTime(minute));

    [Fact]
    public void Ranges_are_formatted_with_an_en_dash() => Assert.Equal("09:00 – 10:30", TimeGridMath.FormatRange(540, 630));

    [Theory]
    [InlineData(double.PositiveInfinity, 720)]
    [InlineData(400, 720)]
    [InlineData(784, 720)]
    [InlineData(900, 836)]
    public void Viewport_fills_larger_space_but_never_shrinks_below_the_default(double available, double expected) =>
        Assert.Equal(expected, TimeGridMath.ViewportHeight(available));

    [Fact]
    public void Popover_goes_right_of_the_anchor_when_it_fits()
    {
        PopoverPosition p = TimeGridMath.PlacePopover(new RectD(100, 200, 80, 40), 340, 200, 800, 600);

        Assert.True(p.OnRight);
        Assert.Equal(188, p.X);
        Assert.Equal(200, p.Y);
    }

    [Fact]
    public void Popover_flips_left_when_the_right_side_is_too_narrow()
    {
        PopoverPosition p = TimeGridMath.PlacePopover(new RectD(600, 200, 100, 40), 340, 200, 800, 600);

        Assert.False(p.OnRight);
        Assert.Equal(600 - 8 - 340, p.X);
    }

    [Fact]
    public void Popover_is_clamped_inside_the_bounds_vertically()
    {
        PopoverPosition low = TimeGridMath.PlacePopover(new RectD(100, 550, 80, 40), 340, 200, 800, 600);
        PopoverPosition high = TimeGridMath.PlacePopover(new RectD(100, -30, 80, 40), 340, 200, 800, 600);

        Assert.Equal(600 - 200 - TimeGridMath.PopoverMargin, low.Y);
        Assert.Equal(TimeGridMath.PopoverMargin, high.Y);
    }

    [Fact]
    public void Popover_stays_inside_when_neither_side_has_room()
    {
        PopoverPosition p = TimeGridMath.PlacePopover(new RectD(40, 100, 300, 40), 340, 200, 400, 600);

        Assert.True(p.X >= TimeGridMath.PopoverMargin);
        Assert.True(p.X + 340 <= 400 - TimeGridMath.PopoverMargin || p.X == TimeGridMath.PopoverMargin);
    }

    [Fact]
    public void Popover_without_a_known_anchor_is_centred()
    {
        PopoverPosition p = TimeGridMath.CenterPopover(300, 200, 800, 600);

        Assert.Equal(250, p.X);
        Assert.Equal(200, p.Y);
    }

    [Theory]
    [InlineData(3, 100, 3)]
    [InlineData(4, 100, 4)]
    [InlineData(4, 70, 2)]
    [InlineData(4, 40, 1)]
    [InlineData(4, 20, 0)]
    [InlineData(2, 36, 2)]
    public void Month_chips_fit_the_height_and_leave_room_for_the_overflow_line(int total, double height, int expected) =>
        Assert.Equal(expected, MonthChipMath.VisibleChips(total, height));

    [Fact]
    public void Schedule_action_carries_invariant_integer_strings()
    {
        UiAction action = TimeGridActions.Schedule("grid", new TimeSlot(3, 540, 600));

        Assert.Equal("grid", action.ElementId);
        Assert.Equal(UiActionKind.Schedule, action.Kind);
        Assert.Null(action.Value);
        Assert.Equal(new Dictionary<string, string> { ["day"] = "3", ["start"] = "540", ["end"] = "600" }, action.FormValues);
    }

    [Fact]
    public void Click_action_carries_extend_only_when_requested()
    {
        Assert.Empty(TimeGridActions.Click("e1", false).FormValues);
        UiAction extended = TimeGridActions.Click("e1", true);

        Assert.Equal(UiActionKind.Click, extended.Kind);
        Assert.Equal(new Dictionary<string, string> { ["extend"] = "true" }, extended.FormValues);
    }

    [Fact]
    public void Empty_space_click_creates_a_one_hour_block_on_the_pressed_day()
    {
        // x in day 2, y at 09:07 -> slot 09:00.
        var gesture = new TimeGridGesture(TimeGridGestureKind.Create, "grid", new TimeSlot(2, 0, 0), 56 + 250, Week.YOf(547), Week);

        Assert.False(gesture.Update(56 + 252, Week.YOf(547) + 2, Week));
        UiAction? action = gesture.Complete(false, Week);

        Assert.NotNull(action);
        Assert.Equal("grid", action.ElementId);
        Assert.Equal(UiActionKind.Schedule, action.Kind);
        Assert.Equal("2", action.FormValues["day"]);
        Assert.Equal("540", action.FormValues["start"]);
        Assert.Equal("600", action.FormValues["end"]);
    }

    [Fact]
    public void Dragging_on_empty_space_creates_a_snapped_block_and_reports_live_previews()
    {
        double x = 56 + 50;
        var gesture = new TimeGridGesture(TimeGridGestureKind.Create, "grid", new TimeSlot(0, 0, 0), x, Week.YOf(547), Week);

        Assert.True(gesture.Update(x, Week.YOf(547) + 30, Week));
        Assert.Equal(new TimeSlot(0, 540, 585), gesture.Current);
        Assert.False(gesture.Update(x, Week.YOf(547) + 30, Week));
        UiAction? action = gesture.Complete(false, Week);

        Assert.Equal("540", action!.FormValues["start"]);
        Assert.Equal("585", action.FormValues["end"]);
    }

    [Fact]
    public void Dragging_upwards_creates_a_block_ending_at_the_press_slot()
    {
        double x = 56 + 50;
        var gesture = new TimeGridGesture(TimeGridGestureKind.Create, "grid", new TimeSlot(0, 0, 0), x, Week.YOf(600), Week);

        gesture.Update(x, Week.YOf(600) - 40, Week);

        Assert.Equal(new TimeSlot(0, 540, 600), gesture.Current);
    }

    [Fact]
    public void Dragging_an_event_moves_it_across_days_keeping_the_duration()
    {
        double startX = 56 + 50;
        var origin = new TimeSlot(0, 540, 630);
        var gesture = new TimeGridGesture(TimeGridGestureKind.Move, "ev", origin, startX, Week.YOf(550), Week);

        Assert.True(gesture.Update(startX + 200, Week.YOf(550) + Week.PixelsPerMinute * 60, Week));
        UiAction? action = gesture.Complete(false, Week);

        Assert.Equal("ev", action!.ElementId);
        Assert.Equal(UiActionKind.Schedule, action.Kind);
        Assert.Equal("2", action.FormValues["day"]);
        Assert.Equal("600", action.FormValues["start"]);
        Assert.Equal("690", action.FormValues["end"]);
    }

    [Fact]
    public void Moving_back_to_the_original_slot_raises_nothing()
    {
        var gesture = new TimeGridGesture(TimeGridGestureKind.Move, "ev", new TimeSlot(1, 540, 600), 200, Week.YOf(550), Week);

        gesture.Update(200, Week.YOf(550) + 20, Week);
        gesture.Update(200, Week.YOf(550) + 1, Week);

        Assert.True(gesture.Moved);
        Assert.Null(gesture.Complete(false, Week));
    }

    [Fact]
    public void Dragging_the_bottom_edge_resizes_only_the_end()
    {
        var gesture = new TimeGridGesture(TimeGridGestureKind.Resize, "ev", new TimeSlot(1, 540, 600), 200, Week.YOf(598), Week);

        gesture.Update(260, Week.YOf(598) + Week.PixelsPerMinute * 45, Week);
        UiAction? action = gesture.Complete(false, Week);

        Assert.Equal("ev", action!.ElementId);
        Assert.Equal("1", action.FormValues["day"]);
        Assert.Equal("540", action.FormValues["start"]);
        Assert.Equal("645", action.FormValues["end"]);
    }

    [Fact]
    public void Resizing_cannot_shrink_below_one_snap_step()
    {
        var gesture = new TimeGridGesture(TimeGridGestureKind.Resize, "ev", new TimeSlot(0, 540, 600), 100, Week.YOf(598), Week);

        gesture.Update(100, Week.YOf(598) - 400, Week);

        Assert.Equal(555, gesture.Current.End);
    }

    [Fact]
    public void Press_and_release_without_movement_is_a_click_on_the_event()
    {
        var gesture = new TimeGridGesture(TimeGridGestureKind.Move, "ev", new TimeSlot(0, 540, 600), 100, 300, Week);

        gesture.Update(102, 301, Week);
        UiAction? action = gesture.Complete(false, Week);

        Assert.False(gesture.Moved);
        Assert.Equal(UiActionKind.Click, action!.Kind);
        Assert.Equal("ev", action.ElementId);
        Assert.Empty(action.FormValues);
    }

    [Fact]
    public void Ctrl_or_shift_click_on_an_event_carries_extend()
    {
        var gesture = new TimeGridGesture(TimeGridGestureKind.Resize, "ev", new TimeSlot(0, 540, 600), 100, 300, Week);

        UiAction? action = gesture.Complete(true, Week);

        Assert.Equal(UiActionKind.Click, action!.Kind);
        Assert.Equal("true", action.FormValues["extend"]);
    }

    [Fact]
    public void Dragging_a_block_that_cannot_move_does_nothing()
    {
        var gesture = new TimeGridGesture(TimeGridGestureKind.Press, "ev", new TimeSlot(0, 540, 600), 100, 300, Week);

        Assert.False(gesture.Update(100, 400, Week));
        Assert.Null(gesture.Complete(false, Week));
    }

    [Fact]
    public void Four_pixels_of_travel_start_a_drag()
    {
        var gesture = new TimeGridGesture(TimeGridGestureKind.Move, "ev", new TimeSlot(0, 540, 600), 100, 300, Week);

        Assert.False(gesture.Update(100, 303.9, Week));
        Assert.True(gesture.Update(100, 304, Week));
        Assert.True(gesture.Moved);
    }

    [Fact]
    public void Every_schedule_action_passes_the_host_validation_shape()
    {
        UiAction action = TimeGridActions.Schedule("grid", new TimeSlot(6, 1425, 1440));

        Assert.All(action.FormValues.Values, v => Assert.True(v.All(char.IsAsciiDigit) && v.Length <= 4));
    }
}
