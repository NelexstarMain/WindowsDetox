using Blocker.Contracts;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class WeeklyBlockScheduleTests
{
    [Fact]
    public void Overlapping_ranges_on_one_day_merge_into_one()
    {
        WeeklyBlockSchedule schedule = new([]);

        _ = schedule.Add(1, 540, 660);
        WeeklyRangeDto? merged = schedule.Add(1, 600, 720);

        Assert.Equal(new WeeklyRangeDto(1, 540, 720), merged);
        Assert.Equal([new WeeklyRangeDto(1, 540, 720)], schedule.Ranges);
    }

    [Fact]
    public void Touching_ranges_merge_but_a_gap_keeps_them_apart()
    {
        WeeklyBlockSchedule schedule = new([]);

        _ = schedule.Add(0, 60, 120);
        _ = schedule.Add(0, 120, 180);
        _ = schedule.Add(0, 240, 300);

        Assert.Equal([new WeeklyRangeDto(0, 60, 180), new WeeklyRangeDto(0, 240, 300)], schedule.Ranges);
    }

    [Fact]
    public void A_range_that_bridges_several_ranges_swallows_all_of_them()
    {
        WeeklyBlockSchedule schedule = new([new(2, 60, 120), new(2, 180, 240), new(2, 300, 360), new(3, 60, 120)]);

        _ = schedule.Add(2, 100, 320);

        Assert.Equal([new WeeklyRangeDto(2, 60, 360), new WeeklyRangeDto(3, 60, 120)], schedule.Ranges);
    }

    [Fact]
    public void Ranges_on_different_days_never_merge_and_stay_sorted()
    {
        WeeklyBlockSchedule schedule = new([]);

        _ = schedule.Add(4, 60, 120);
        _ = schedule.Add(0, 600, 660);
        _ = schedule.Add(0, 60, 120);

        Assert.Equal([new WeeklyRangeDto(0, 60, 120), new WeeklyRangeDto(0, 600, 660), new WeeklyRangeDto(4, 60, 120)], schedule.Ranges);
    }

    [Theory]
    [InlineData(-1, 0, 60)]
    [InlineData(7, 0, 60)]
    [InlineData(0, -5, 60)]
    [InlineData(0, 60, 60)]
    [InlineData(0, 90, 60)]
    [InlineData(0, 0, 1441)]
    public void Invalid_ranges_are_rejected(int day, int start, int end)
    {
        WeeklyBlockSchedule schedule = new([]);

        Assert.Null(schedule.Add(day, start, end));
        Assert.Empty(schedule.Ranges);
    }

    [Fact]
    public void The_whole_day_is_valid()
    {
        WeeklyBlockSchedule schedule = new([]);

        Assert.Equal(new WeeklyRangeDto(6, 0, 1440), schedule.Add(6, 0, 1440));
    }

    [Fact]
    public void Replace_moves_a_range_and_merges_it_with_its_new_neighbours()
    {
        WeeklyBlockSchedule schedule = new([new(0, 60, 120), new(1, 60, 180)]);

        WeeklyRangeDto? moved = schedule.Replace(new WeeklyRangeDto(0, 60, 120), 1, 150, 240);

        Assert.Equal(new WeeklyRangeDto(1, 60, 240), moved);
        Assert.Equal([new WeeklyRangeDto(1, 60, 240)], schedule.Ranges);
    }

    [Fact]
    public void Replace_with_an_invalid_position_changes_nothing()
    {
        WeeklyBlockSchedule schedule = new([new(0, 60, 120)]);

        Assert.Null(schedule.Replace(new WeeklyRangeDto(0, 60, 120), 0, 200, 100));
        Assert.Equal([new WeeklyRangeDto(0, 60, 120)], schedule.Ranges);
    }

    [Fact]
    public void Stored_ranges_are_normalised_on_load()
    {
        WeeklyBlockSchedule schedule = new([new(0, 100, 200), new(0, 50, 120), new(9, 0, 60)]);

        Assert.Equal([new WeeklyRangeDto(0, 50, 200)], schedule.Ranges);
    }

    [Fact]
    public void Remove_and_clear_empty_the_schedule()
    {
        WeeklyBlockSchedule schedule = new([new(0, 60, 120), new(1, 60, 120)]);

        Assert.True(schedule.Remove(new WeeklyRangeDto(0, 60, 120)));
        Assert.False(schedule.Remove(new WeeklyRangeDto(0, 60, 120)));
        schedule.Clear();

        Assert.Empty(schedule.Ranges);
    }
}
