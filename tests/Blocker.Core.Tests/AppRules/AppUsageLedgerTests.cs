using Blocker.Contracts;
using Blocker.Core.AppRules;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppUsageLedgerTests
{
    private const string Key = "path:c:\\games\\game.exe";

    private static AppUsageLedger LedgerWith(string key, AppUsageEntry entry) => new(new AppUsageState(new Dictionary<string, AppUsageEntry> { [key] = entry }));

    [Fact]
    public void Usage_accumulates_for_the_day_and_the_week()
    {
        var ledger = new AppUsageLedger();

        ledger.Add(Key, 30, At(1, 10));
        ledger.Add(Key, 12, At(1, 11));

        Assert.Equal(42, ledger.DaySeconds(Key, At(1, 12)));
        Assert.Equal(42, ledger.WeekSeconds(Key, At(1, 12)));
        Assert.Equal(0, ledger.DaySeconds("other", At(1, 12)));
    }

    [Fact]
    public void The_day_counter_rolls_at_local_midnight_but_the_week_keeps_counting()
    {
        var ledger = new AppUsageLedger();
        ledger.Add(Key, 100, At(1, 23, 59, 59));

        Assert.Equal(100, ledger.DaySeconds(Key, At(1, 23, 59, 59)));
        Assert.Equal(0, ledger.DaySeconds(Key, At(2, 0)));
        Assert.Equal(100, ledger.WeekSeconds(Key, At(2, 0)));

        ledger.Add(Key, 5, At(2, 0, 0, 1));

        Assert.Equal(5, ledger.DaySeconds(Key, At(2, 1)));
        Assert.Equal(105, ledger.WeekSeconds(Key, At(2, 1)));
    }

    [Fact]
    public void The_week_rolls_on_monday_and_sunday_still_belongs_to_the_old_week()
    {
        var ledger = new AppUsageLedger();
        ledger.Add(Key, 100, At(0, 9));
        ledger.Add(Key, 50, At(6, 23));

        Assert.Equal(150, ledger.WeekSeconds(Key, At(6, 23, 59)));
        Assert.Equal(0, ledger.WeekSeconds(Key, At(7, 0)));
        Assert.Equal(0, ledger.DaySeconds(Key, At(7, 0)));
    }

    [Fact]
    public void A_clock_that_moved_back_by_a_day_or_less_never_gives_time_back()
    {
        var ledger = new AppUsageLedger();
        ledger.Add(Key, 100, At(3, 10));

        Assert.Equal(100, ledger.DaySeconds(Key, At(2, 10)));
        Assert.Equal(100, ledger.WeekSeconds(Key, At(2, 10)));

        ledger.Add(Key, 20, At(2, 10));

        Assert.Equal(120, ledger.DaySeconds(Key, At(3, 11)));
    }

    [Fact]
    public void A_stored_day_more_than_a_day_ahead_is_reset_so_a_clock_glitch_cannot_lock_the_pool()
    {
        AppUsageLedger ledger = LedgerWith(Key, new AppUsageEntry(DateOnly.FromDateTime(At(400, 10)), 5000, DateOnly.FromDateTime(At(0, 0)), 9000));

        Assert.Equal(0, ledger.DaySeconds(Key, At(1, 10)));
        Assert.Equal(9000, ledger.WeekSeconds(Key, At(1, 10))); // the week key is not ahead: that counter stays
        Assert.Equal(0, ledger.Used(Key, AppBudgetMode.Daily, At(1, 10)));
    }

    [Fact]
    public void A_stored_week_more_than_a_week_ahead_is_reset()
    {
        AppUsageLedger ledger = LedgerWith(Key, new AppUsageEntry(DateOnly.FromDateTime(At(1, 0)), 40, DateOnly.FromDateTime(At(400, 0)), 9000));

        Assert.Equal(40, ledger.DaySeconds(Key, At(1, 10)));
        Assert.Equal(0, ledger.WeekSeconds(Key, At(1, 10)));
        Assert.Equal(0, ledger.Used(Key, AppBudgetMode.Weekly, At(1, 10)));
    }

    [Fact]
    public void Counting_after_a_reset_starts_from_zero_and_the_state_no_longer_carries_the_future_keys()
    {
        AppUsageLedger ledger = LedgerWith(Key, new AppUsageEntry(DateOnly.FromDateTime(At(400, 10)), 5000, DateOnly.FromDateTime(At(400, 0)), 9000));

        ledger.Add(Key, 7, At(1, 10));

        Assert.Equal(7, ledger.DaySeconds(Key, At(1, 11)));
        Assert.Equal(7, ledger.WeekSeconds(Key, At(1, 11)));
        AppUsageEntry entry = ledger.State.Entries[Key];
        Assert.Equal((DateOnly.FromDateTime(At(1, 0)), DateOnly.FromDateTime(At(0, 0))), (entry.Day, entry.Week));
    }

    [Fact]
    public void Pruning_resets_the_entries_that_are_ahead_so_they_are_not_written_back()
    {
        AppUsageLedger ledger = LedgerWith(Key, new AppUsageEntry(DateOnly.FromDateTime(At(400, 10)), 5000, DateOnly.FromDateTime(At(400, 0)), 9000));

        ledger.Prune(At(1, 10));

        AppUsageEntry entry = ledger.State.Entries[Key];
        Assert.Equal((DateOnly.FromDateTime(At(1, 0)), 0L, DateOnly.FromDateTime(At(0, 0)), 0L), (entry.Day, entry.DaySeconds, entry.Week, entry.WeekSeconds));
    }

    [Fact]
    public void A_day_one_ahead_and_a_week_starting_tomorrow_are_still_honored()
    {
        // Sunday 23:00: tomorrow is Monday, whose week key is the next Monday.
        var ledger = new AppUsageLedger();
        ledger.Add(Key, 100, At(7, 10)); // the following Monday

        Assert.Equal(100, ledger.DaySeconds(Key, At(6, 23)));
        Assert.Equal(100, ledger.WeekSeconds(Key, At(6, 23)));
        Assert.Equal(0, ledger.DaySeconds(Key, At(5, 23))); // two days ahead
    }

    [Fact]
    public void The_period_of_the_pool_mode_picks_the_counter()
    {
        var ledger = new AppUsageLedger();
        ledger.Add(Key, 60, At(1, 10));
        ledger.Add(Key, 40, At(2, 10));

        Assert.Equal(0, ledger.Used(Key, AppBudgetMode.None, At(2, 11)));
        Assert.Equal(40, ledger.Used(Key, AppBudgetMode.Daily, At(2, 11)));
        Assert.Equal(40, ledger.Used(Key, AppBudgetMode.PerWeekday, At(2, 11)));
        Assert.Equal(100, ledger.Used(Key, AppBudgetMode.Weekly, At(2, 11)));
    }

    [Fact]
    public void Negative_amounts_are_ignored()
    {
        var ledger = new AppUsageLedger();
        ledger.Add(Key, 10, At(1, 10));

        ledger.Add(Key, -999, At(1, 10));

        Assert.Equal(10, ledger.DaySeconds(Key, At(1, 10)));
    }

    [Fact]
    public void The_state_can_be_restored_and_is_a_snapshot()
    {
        var ledger = new AppUsageLedger();
        ledger.Add(Key, 77, At(1, 10));
        AppUsageState state = ledger.State;
        ledger.Add(Key, 1, At(1, 10));

        var restored = new AppUsageLedger(state);

        Assert.Equal(77, restored.DaySeconds(Key, At(1, 11)));
        Assert.Equal(78, ledger.DaySeconds(Key, At(1, 11)));
    }

    [Fact]
    public void Moving_usage_to_a_new_key_adds_it_to_what_that_key_already_has()
    {
        var ledger = new AppUsageLedger();
        ledger.Add("old", 60, At(1, 10));
        ledger.Add("new", 15, At(1, 10));

        ledger.Move("old", "new");

        Assert.Equal(75, ledger.DaySeconds("new", At(1, 11)));
        Assert.Equal(0, ledger.DaySeconds("old", At(1, 11)));
        ledger.Move("missing", "new");
        Assert.Equal(75, ledger.DaySeconds("new", At(1, 11)));
    }

    [Fact]
    public void Pruning_drops_the_entries_that_are_stale_for_both_the_day_and_the_week()
    {
        var ledger = new AppUsageLedger();
        ledger.Add("last week", 10, At(1, 10));
        ledger.Add("yesterday", 10, At(8, 10));

        ledger.Prune(At(9, 10));

        Assert.DoesNotContain("last week", ledger.State.Entries.Keys);
        Assert.Contains("yesterday", ledger.State.Entries.Keys);
    }
}
