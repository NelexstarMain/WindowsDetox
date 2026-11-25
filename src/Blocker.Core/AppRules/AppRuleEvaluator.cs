using Blocker.Contracts;

namespace Blocker.Core.AppRules;

/// <summary>What currently decides about an app and for how long.</summary>
/// <param name="State">The deciding state.</param>
/// <param name="BlockedUntil">Local wall-clock time at which the current block or exhausted pool ends; <see langword="null"/> when not blocked.</param>
/// <param name="RemainingSeconds">Pool time left in the current period; <see langword="null"/> without a pool.</param>
public readonly record struct AppRuleEvaluation(AppRuleState State, DateTime? BlockedUntil, long? RemainingSeconds);

/// <summary>
/// Pure decision of one rule at one local wall-clock time. Precedence: a running session decides first, then a scheduled block, then the pool.
/// The rule must be normalized (<see cref="AppRuleValidator"/>).
/// </summary>
public static class AppRuleEvaluator
{
    /// <summary>
    /// Where <see cref="AppRuleEvaluation.BlockedUntil"/> lands when every minute of the week is blocked: such a block has no end, so a
    /// bounded date one week ahead is reported instead.
    /// </summary>
    public static readonly TimeSpan FullyBlockedCap = TimeSpan.FromDays(7);

    /// <summary>Decides the rule.</summary>
    /// <param name="rule">The normalized rule.</param>
    /// <param name="localNow">Local wall-clock time in the zone the rules were frozen in.</param>
    /// <param name="usedSeconds">Foreground time counted in the current pool period (see <see cref="UsesWeeklyPeriod"/>).</param>
    /// <param name="sessionRunning">Whether a session (including an emergency pass) runs.</param>
    /// <returns>The state, when it ends and the pool time left.</returns>
    public static AppRuleEvaluation Evaluate(AppRuleDto rule, DateTime localNow, long usedSeconds, bool sessionRunning)
    {
        ArgumentNullException.ThrowIfNull(rule);
        long used = Math.Max(0, usedSeconds);
        long? budget = BudgetSeconds(rule.Budget, localNow);
        long? remaining = budget is { } pool ? Math.Max(0, pool - used) : null;
        if (sessionRunning)
        {
            return new AppRuleEvaluation(AppRuleState.SessionControlled, null, remaining);
        }

        if (ScheduledBlockEnd(rule.BlockedRanges, localNow) is { } scheduledEnd)
        {
            return new AppRuleEvaluation(AppRuleState.ScheduledBlock, scheduledEnd, remaining);
        }

        if (remaining == 0)
        {
            DateTime end = UsesWeeklyPeriod(rule.Budget) ? localNow.Date.AddDays(7 - DayIndex(localNow)) : localNow.Date.AddDays(1);
            return new AppRuleEvaluation(AppRuleState.BudgetExhausted, end, 0);
        }

        return new AppRuleEvaluation(AppRuleState.Allowed, null, remaining);
    }

    /// <summary>Gets whether the pool of <paramref name="budget"/> is counted per week (otherwise per day or not at all).</summary>
    public static bool UsesWeeklyPeriod(AppBudgetDto budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return budget.Mode == AppBudgetMode.Weekly;
    }

    /// <summary>Gets the size of the pool for the period that contains <paramref name="localNow"/>; <see langword="null"/> without a pool.</summary>
    public static long? BudgetSeconds(AppBudgetDto budget, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return budget.Mode switch
        {
            AppBudgetMode.Daily => budget.DailyMinutes * 60L,
            AppBudgetMode.Weekly => budget.WeeklyMinutes * 60L,
            // A malformed list means no time at all: the rule fails closed.
            AppBudgetMode.PerWeekday => budget.PerWeekdayMinutes is { Count: 7 } days ? days[DayIndex(localNow)] * 60L : 0,
            _ => null,
        };
    }

    /// <summary>Weekday of <paramref name="time"/>, 0 = Monday through 6 = Sunday.</summary>
    internal static int DayIndex(DateTime time) => ((int)time.DayOfWeek + 6) % 7;

    /// <summary>
    /// End of the contiguous blocked span that contains <paramref name="localNow"/>, following it across midnight and from Sunday to Monday;
    /// <see langword="null"/> when it is not inside a blocked range.
    /// </summary>
    private static DateTime? ScheduledBlockEnd(IReadOnlyList<WeeklyRangeDto> ranges, DateTime localNow)
    {
        if (ranges.Count == 0)
        {
            return null;
        }

        var minuteStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, localNow.Minute, 0, localNow.Kind);
        int day = DayIndex(localNow);
        int cursor = (localNow.Hour * 60) + localNow.Minute;
        int span = 0;

        // Eight passes are the week plus the day we started on: a span that is still blocked when it gets back to its own day never ends.
        for (int pass = 0; pass <= 7; pass++)
        {
            WeeklyRangeDto? containing = ranges.FirstOrDefault(r => r.Day == day && r.StartMinute <= cursor && cursor < r.EndMinute);
            if (containing is null)
            {
                return span == 0 ? null : minuteStart.AddMinutes(span);
            }

            span += containing.EndMinute - cursor;
            if (containing.EndMinute < AppRuleValidator.MinutesPerDay)
            {
                return minuteStart.AddMinutes(span);
            }

            (cursor, day) = (0, (day + 1) % 7);
        }

        return minuteStart + FullyBlockedCap;
    }
}

/// <summary>Decides whether a replacement rule gives the app at least as little freedom as the rule it replaces.</summary>
public static class AppRuleTightness
{
    /// <summary>
    /// True when <paramref name="next"/> blocks at least what <paramref name="previous"/> blocks (every old range lies inside one new range of the
    /// same day) and its pool is no larger: a rule without a pool may gain any pool, otherwise the mode must stay and no minutes may grow.
    /// Both rules must be normalized.
    /// </summary>
    /// <param name="next">The replacement.</param>
    /// <param name="previous">The rule in force.</param>
    /// <returns>Whether the replacement is identical or tighter.</returns>
    public static bool IsNotLooserThan(AppRuleDto next, AppRuleDto previous)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(previous);
        return previous.BlockedRanges.All(old => next.BlockedRanges.Any(r => r.Day == old.Day && r.StartMinute <= old.StartMinute && r.EndMinute >= old.EndMinute))
            && IsPoolNotLarger(next.Budget, previous.Budget);
    }

    private static bool IsPoolNotLarger(AppBudgetDto next, AppBudgetDto previous)
    {
        if (previous.Mode == AppBudgetMode.None)
        {
            return true;
        }

        if (next.Mode != previous.Mode)
        {
            return false;
        }

        return previous.Mode switch
        {
            AppBudgetMode.Daily => next.DailyMinutes <= previous.DailyMinutes,
            AppBudgetMode.Weekly => next.WeeklyMinutes <= previous.WeeklyMinutes,
            AppBudgetMode.PerWeekday => next.PerWeekdayMinutes is { Count: 7 } now
                && previous.PerWeekdayMinutes is { Count: 7 } before
                && now.Zip(before).All(static pair => pair.First <= pair.Second),
            _ => false,
        };
    }
}
