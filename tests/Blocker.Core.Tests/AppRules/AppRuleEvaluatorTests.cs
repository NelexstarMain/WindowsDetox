using Blocker.Contracts;
using Blocker.Core.AppRules;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppRuleEvaluatorTests
{
    private static AppRuleEvaluation Evaluate(AppRuleDto rule, DateTime localNow, long used = 0, bool session = false) =>
        AppRuleEvaluator.Evaluate(rule, localNow, used, session);

    [Fact]
    public void The_calendar_of_the_tests_starts_on_a_monday() => Assert.Equal(DayOfWeek.Monday, Monday.DayOfWeek);

    [Fact]
    public void A_rule_without_ranges_or_pool_never_blocks()
    {
        AppRuleEvaluation result = Evaluate(Rule(), At(2, 12), used: 99_999);

        Assert.Equal(AppRuleState.Allowed, result.State);
        Assert.Null(result.BlockedUntil);
        Assert.Null(result.RemainingSeconds);
    }

    [Fact]
    public void A_running_session_decides_before_the_rule_even_inside_a_block_and_with_an_empty_pool()
    {
        AppRuleDto rule = Rule(Daily(1), Range(0, 0, 1440));

        AppRuleEvaluation result = Evaluate(rule, At(0, 12), used: 600, session: true);

        Assert.Equal(AppRuleState.SessionControlled, result.State);
        Assert.Null(result.BlockedUntil);
        Assert.Equal(0, result.RemainingSeconds);
    }

    [Fact]
    public void A_scheduled_range_blocks_from_its_first_minute_until_its_exclusive_end()
    {
        AppRuleDto rule = Rule(null, Range(0, 9 * 60, 17 * 60));

        Assert.Equal(AppRuleState.Allowed, Evaluate(rule, At(0, 8, 59, 59)).State);
        AppRuleEvaluation start = Evaluate(rule, At(0, 9));
        Assert.Equal(AppRuleState.ScheduledBlock, start.State);
        Assert.Equal(At(0, 17), start.BlockedUntil);
        Assert.Equal(AppRuleState.ScheduledBlock, Evaluate(rule, At(0, 16, 59, 59)).State);
        Assert.Equal(AppRuleState.Allowed, Evaluate(rule, At(0, 17)).State);
    }

    [Fact]
    public void A_range_only_applies_on_its_own_weekday()
    {
        AppRuleDto rule = Rule(null, Range(1, 0, 1440));

        Assert.Equal(AppRuleState.Allowed, Evaluate(rule, At(0, 12)).State);
        Assert.Equal(AppRuleState.ScheduledBlock, Evaluate(rule, At(1, 12)).State);
        Assert.Equal(AppRuleState.Allowed, Evaluate(rule, At(2, 0)).State);
    }

    [Fact]
    public void A_block_that_continues_after_midnight_ends_with_the_last_contiguous_range()
    {
        AppRuleDto rule = Rule(null, Range(0, 22 * 60, 1440), Range(1, 0, 7 * 60));

        AppRuleEvaluation result = Evaluate(rule, At(0, 23));

        Assert.Equal(AppRuleState.ScheduledBlock, result.State);
        Assert.Equal(At(1, 7), result.BlockedUntil);
    }

    [Fact]
    public void A_block_that_continues_past_the_end_of_the_week_ends_on_monday()
    {
        AppRuleDto rule = Rule(null, Range(6, 23 * 60, 1440), Range(0, 0, 6 * 60));

        AppRuleEvaluation result = Evaluate(rule, At(6, 23, 30));

        Assert.Equal(AppRuleState.ScheduledBlock, result.State);
        Assert.Equal(At(7, 6), result.BlockedUntil);
    }

    [Fact]
    public void A_block_that_runs_around_the_whole_week_ends_where_the_gap_begins()
    {
        // Blocked from Monday 08:00 through Monday 07:00 of the next week; the only free hour is Monday 07:00-08:00.
        AppRuleDto rule = Rule(null, Range(0, 0, 7 * 60), Range(0, 8 * 60, 1440), Range(1, 0, 1440), Range(2, 0, 1440), Range(3, 0, 1440), Range(4, 0, 1440), Range(5, 0, 1440), Range(6, 0, 1440));

        AppRuleEvaluation result = Evaluate(rule, At(0, 9));

        Assert.Equal(At(7, 7), result.BlockedUntil);
    }

    [Fact]
    public void A_week_that_is_blocked_all_the_time_has_no_end_and_reports_one_week_ahead()
    {
        AppRuleDto rule = Rule(null, [.. Enumerable.Range(0, 7).Select(d => Range(d, 0, 1440))]);

        AppRuleEvaluation result = Evaluate(rule, At(2, 10, 7, 30));

        Assert.Equal(AppRuleState.ScheduledBlock, result.State);
        Assert.Equal(At(2, 10, 7).AddDays(7), result.BlockedUntil);
        Assert.Equal(AppRuleEvaluator.FullyBlockedCap, result.BlockedUntil - At(2, 10, 7));
    }

    [Fact]
    public void A_daily_pool_is_exhausted_when_the_minutes_are_used_and_stays_so_until_midnight()
    {
        AppRuleDto rule = Rule(Daily(60));

        AppRuleEvaluation left = Evaluate(rule, At(1, 20), used: 3599);
        AppRuleEvaluation done = Evaluate(rule, At(1, 20), used: 3600);

        Assert.Equal(AppRuleState.Allowed, left.State);
        Assert.Equal(1, left.RemainingSeconds);
        Assert.Equal(AppRuleState.BudgetExhausted, done.State);
        Assert.Equal(0, done.RemainingSeconds);
        Assert.Equal(At(2, 0), done.BlockedUntil);
    }

    [Fact]
    public void Using_more_than_the_pool_never_reports_negative_time()
    {
        AppRuleEvaluation result = Evaluate(Rule(Daily(1)), At(0, 12), used: 500);

        Assert.Equal(0, result.RemainingSeconds);
    }

    [Fact]
    public void A_pool_of_zero_minutes_blocks_the_whole_day()
    {
        AppRuleEvaluation result = Evaluate(Rule(Daily(0)), At(3, 0), used: 0);

        Assert.Equal(AppRuleState.BudgetExhausted, result.State);
        Assert.Equal(At(4, 0), result.BlockedUntil);
    }

    [Fact]
    public void A_per_weekday_pool_uses_the_value_of_the_current_weekday()
    {
        AppRuleDto rule = Rule(PerWeekday(0, 10, 20, 30, 40, 50, 60));

        AppRuleEvaluation monday = Evaluate(rule, At(0, 12));
        AppRuleEvaluation wednesday = Evaluate(rule, At(2, 12), used: 1100);
        AppRuleEvaluation sunday = Evaluate(rule, At(6, 12), used: 3599);

        Assert.Equal((AppRuleState.BudgetExhausted, At(1, 0)), (monday.State, monday.BlockedUntil));
        Assert.Equal((AppRuleState.Allowed, 100L), (wednesday.State, wednesday.RemainingSeconds));
        Assert.Equal((AppRuleState.Allowed, 1L), (sunday.State, sunday.RemainingSeconds));
    }

    [Fact]
    public void A_weekly_pool_is_exhausted_until_the_next_monday_midnight()
    {
        AppRuleDto rule = Rule(Weekly(120));

        AppRuleEvaluation midweek = Evaluate(rule, At(2, 15), used: 7200);
        AppRuleEvaluation sundayNight = Evaluate(rule, At(6, 23, 59, 59), used: 9000);

        Assert.Equal((AppRuleState.BudgetExhausted, At(7, 0)), (midweek.State, midweek.BlockedUntil));
        Assert.Equal((AppRuleState.BudgetExhausted, At(7, 0)), (sundayNight.State, sundayNight.BlockedUntil));
        Assert.Equal(AppRuleState.Allowed, Evaluate(rule, At(2, 15), used: 7199).State);
    }

    [Fact]
    public void A_scheduled_block_takes_precedence_over_an_exhausted_pool()
    {
        AppRuleDto rule = Rule(Daily(10), Range(0, 600, 720));

        AppRuleEvaluation result = Evaluate(rule, At(0, 11), used: 6000);

        Assert.Equal(AppRuleState.ScheduledBlock, result.State);
        Assert.Equal(At(0, 12), result.BlockedUntil);
        Assert.Equal(0, result.RemainingSeconds);
    }

    [Fact]
    public void Seconds_inside_a_minute_do_not_move_the_end_of_a_block()
    {
        AppRuleEvaluation result = Evaluate(Rule(null, Range(0, 600, 720)), At(0, 10, 30, 45));

        Assert.Equal(At(0, 12), result.BlockedUntil);
    }

    [Fact]
    public void The_pool_period_is_the_week_only_for_a_weekly_pool()
    {
        Assert.True(AppRuleEvaluator.UsesWeeklyPeriod(Weekly(5)));
        Assert.False(AppRuleEvaluator.UsesWeeklyPeriod(Daily(5)));
        Assert.False(AppRuleEvaluator.UsesWeeklyPeriod(PerWeekday(1, 2, 3, 4, 5, 6, 7)));
        Assert.False(AppRuleEvaluator.UsesWeeklyPeriod(NoBudget));
    }
}
