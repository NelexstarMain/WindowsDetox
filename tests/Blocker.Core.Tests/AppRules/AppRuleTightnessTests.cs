using Blocker.Contracts;
using Blocker.Core.AppRules;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppRuleTightnessTests
{
    private static bool NotLooser(AppRuleDto next, AppRuleDto previous) => AppRuleTightness.IsNotLooserThan(next, previous);

    [Fact]
    public void An_identical_rule_is_not_looser()
    {
        AppRuleDto rule = Rule(Daily(60), Range(0, 600, 720));

        Assert.True(NotLooser(rule, rule with { }));
    }

    [Fact]
    public void More_blocked_time_is_tighter_and_less_is_looser()
    {
        AppRuleDto previous = Rule(null, Range(0, 600, 720), Range(2, 0, 60));

        Assert.True(NotLooser(Rule(null, Range(0, 500, 800), Range(2, 0, 60), Range(3, 0, 10)), previous));
        Assert.False(NotLooser(Rule(null, Range(0, 600, 719), Range(2, 0, 60)), previous));
        Assert.False(NotLooser(Rule(null, Range(0, 601, 720), Range(2, 0, 60)), previous));
        Assert.False(NotLooser(Rule(null, Range(0, 600, 720)), previous));
        Assert.False(NotLooser(Rule(null, Range(1, 600, 720), Range(2, 0, 60)), previous));
    }

    [Fact]
    public void Removing_every_range_or_the_whole_pool_is_looser()
    {
        Assert.False(NotLooser(Rule(), Rule(null, Range(0, 0, 60))));
        Assert.False(NotLooser(Rule(), Rule(Daily(60))));
    }

    [Fact]
    public void A_range_must_stay_covered_by_one_new_range_even_when_the_new_ones_are_split()
    {
        // The validator merges touching ranges; unmerged input is not trusted to cover a longer old range.
        Assert.False(NotLooser(Rule(null, Range(0, 600, 700), Range(0, 701, 800)), Rule(null, Range(0, 600, 800))));
    }

    [Fact]
    public void A_pool_can_be_added_to_a_rule_that_had_none()
    {
        Assert.True(NotLooser(Rule(Daily(60)), Rule()));
        Assert.True(NotLooser(Rule(Weekly(60)), Rule()));
    }

    [Fact]
    public void A_daily_pool_may_shrink_but_not_grow_or_change_mode()
    {
        Assert.True(NotLooser(Rule(Daily(30)), Rule(Daily(60))));
        Assert.True(NotLooser(Rule(Daily(60)), Rule(Daily(60))));
        Assert.False(NotLooser(Rule(Daily(61)), Rule(Daily(60))));
        Assert.False(NotLooser(Rule(Weekly(60)), Rule(Daily(60))));
        Assert.False(NotLooser(Rule(PerWeekday(0, 0, 0, 0, 0, 0, 0)), Rule(Daily(60))));
    }

    [Fact]
    public void A_per_weekday_pool_must_not_grow_on_any_day()
    {
        AppRuleDto previous = Rule(PerWeekday(10, 20, 30, 40, 50, 60, 70));

        Assert.True(NotLooser(Rule(PerWeekday(10, 20, 30, 40, 50, 60, 70)), previous));
        Assert.True(NotLooser(Rule(PerWeekday(0, 20, 30, 40, 50, 60, 5)), previous));
        Assert.False(NotLooser(Rule(PerWeekday(10, 20, 30, 40, 50, 60, 71)), previous));
        Assert.False(NotLooser(Rule(Daily(10)), previous));
    }

    [Fact]
    public void A_weekly_pool_may_shrink_but_not_grow()
    {
        Assert.True(NotLooser(Rule(Weekly(100)), Rule(Weekly(120))));
        Assert.False(NotLooser(Rule(Weekly(121)), Rule(Weekly(120))));
        Assert.False(NotLooser(Rule(Daily(1440)), Rule(Weekly(120))));
    }
}
