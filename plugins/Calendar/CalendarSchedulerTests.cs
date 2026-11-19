using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Sessions;
using Xunit;

namespace CalendarPlanner.Tests;

public class CalendarSchedulerTests
{
    private static readonly int Mon = Kit.D(2026, 1, 5);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static DateTimeOffset At(int hour, int minute, int second = 0, int dayOffset = 0) =>
        new DateTimeOffset(2026, 1, 5, hour, minute, second, TimeSpan.Zero).AddDays(dayOffset);

    private static IReadOnlyList<SchedulerAction> Decide(DateTimeOffset now, IReadOnlyList<CalendarEntry> entries, bool running = false, CalendarSettings? settings = null, IReadOnlySet<string>? recorded = null, IReadOnlySet<string>? reminded = null, TimeZoneInfo? zone = null) =>
        CalendarScheduler.Decide(now, zone ?? TimeZoneInfo.Utc, entries, settings ?? new CalendarSettings(), running, recorded ?? None, reminded ?? None);

    private static CalendarEntry Planned(int minute = 540, int length = 60, int day = 0)
    {
        CalendarEntry e = Kit.Session("Deep work", day == 0 ? Mon : day, minute, length);
        e.Id = 1;
        return e;
    }

    [Fact]
    public void Nothing_happens_long_before_a_session()
    {
        Assert.Empty(Decide(At(8, 58), [Planned()]));
        Assert.Empty(Decide(At(7, 0), [Planned()]));
    }

    [Fact]
    public void A_reminder_comes_once_inside_the_lead_time()
    {
        IReadOnlyList<SchedulerAction> actions = Decide(At(8, 59, 30), [Planned()]);

        SchedulerAction action = Assert.Single(actions);
        Assert.Equal(SchedulerActionKind.Remind, action.Kind);
        Assert.Empty(Decide(At(8, 59, 30), [Planned()], reminded: new HashSet<string> { $"1:{Mon}" }));
    }

    [Fact]
    public void The_reminder_lead_is_configurable_and_zero_means_none()
    {
        Assert.Single(Decide(At(8, 55), [Planned()], settings: new CalendarSettings { ReminderMinutes = 5 }));
        Assert.Empty(Decide(At(8, 59, 30), [Planned()], settings: new CalendarSettings { ReminderMinutes = 0 }));
    }

    [Fact]
    public void The_session_starts_at_its_time_for_its_full_length()
    {
        SchedulerAction action = Assert.Single(Decide(At(9, 0), [Planned()]));

        Assert.Equal((SchedulerActionKind.Start, 60), (action.Kind, action.Minutes));
        Assert.Equal(At(9, 0), action.StartUtc);
    }

    [Fact]
    public void A_late_start_inside_the_grace_window_runs_for_the_remaining_time()
    {
        SchedulerAction action = Assert.Single(Decide(At(9, 5), [Planned()]));

        Assert.Equal((SchedulerActionKind.Start, 55), (action.Kind, action.Minutes));
    }

    [Fact]
    public void The_grace_window_is_ten_minutes_inclusive_and_then_the_session_is_missed()
    {
        Assert.Equal(SchedulerActionKind.Start, Assert.Single(Decide(At(9, 10), [Planned()])).Kind);
        Assert.Equal(SchedulerActionKind.Miss, Assert.Single(Decide(At(9, 10, 1), [Planned()])).Kind);
        Assert.Equal(SchedulerActionKind.Miss, Assert.Single(Decide(At(12, 0), [Planned()])).Kind);
    }

    [Fact]
    public void A_session_with_less_than_fifteen_minutes_left_is_missed_not_started()
    {
        CalendarEntry shortOne = Planned(540, 20);

        Assert.Equal(SchedulerActionKind.Start, Assert.Single(Decide(At(9, 5), [shortOne])).Kind);
        Assert.Equal(SchedulerActionKind.Miss, Assert.Single(Decide(At(9, 6), [shortOne])).Kind);
    }

    [Fact]
    public void A_running_session_blocks_the_start_but_the_occurrence_waits_inside_the_grace_window()
    {
        Assert.Empty(Decide(At(9, 2), [Planned()], running: true));
        Assert.Equal(SchedulerActionKind.Start, Assert.Single(Decide(At(9, 4), [Planned()], running: false)).Kind);
        Assert.Equal(SchedulerActionKind.Miss, Assert.Single(Decide(At(9, 11), [Planned()], running: true)).Kind);
    }

    [Fact]
    public void An_occurrence_with_a_record_is_never_decided_again()
    {
        var recorded = new HashSet<string> { $"1:{Mon}" };

        Assert.Empty(Decide(At(9, 0), [Planned()], recorded: recorded));
        Assert.Empty(Decide(At(12, 0), [Planned()], recorded: recorded));
    }

    [Fact]
    public void With_auto_start_off_nothing_is_decided_at_all()
    {
        var off = new CalendarSettings { AutoStart = false };

        Assert.Empty(Decide(At(8, 59, 30), [Planned()], settings: off));
        Assert.Empty(Decide(At(9, 0), [Planned()], settings: off));
        Assert.Empty(Decide(At(12, 0), [Planned()], settings: off));
    }

    [Fact]
    public void After_sleep_a_session_slept_through_is_missed_and_a_later_one_still_starts()
    {
        CalendarEntry first = Planned(540, 20);
        CalendarEntry second = Kit.Session("Second", Mon, 565, 30);
        second.Id = 2;

        IReadOnlyList<SchedulerAction> actions = Decide(At(9, 26), [first, second]);

        Assert.Equal([(SchedulerActionKind.Miss, 1), (SchedulerActionKind.Start, 2)], actions.Select(a => (a.Kind, a.Occurrence.Entry.Id)));
        Assert.Equal(29, actions[1].Minutes);
    }

    [Fact]
    public void At_most_one_session_is_started_per_decision()
    {
        CalendarEntry a = Planned(540, 60);
        CalendarEntry b = Kit.Session("B", Mon, 545, 60);
        b.Id = 2;

        IReadOnlyList<SchedulerAction> actions = Decide(At(9, 6), [a, b]);

        Assert.Single(actions, x => x.Kind == SchedulerActionKind.Start);
        Assert.Equal(1, actions.Single(x => x.Kind == SchedulerActionKind.Start).Occurrence.Entry.Id);
    }

    [Fact]
    public void Occurrences_before_the_entry_was_created_are_ignored()
    {
        CalendarEntry series = Kit.Daily(Planned(540, 60));
        series.Created = At(9, 30).ToUnixTimeSeconds();

        Assert.Empty(Decide(At(9, 31), [series]).Where(a => a.Occurrence.Day == Mon));
    }

    [Fact]
    public void Occurrences_older_than_three_days_are_not_recorded_as_missed()
    {
        Assert.Empty(Decide(At(9, 0, dayOffset: 4), [Planned()]));
        Assert.Single(Decide(At(9, 0, dayOffset: 2), [Planned()]));
    }

    [Fact]
    public void A_weekly_series_is_keyed_by_its_series_day()
    {
        CalendarEntry weekly = Kit.Weekly(Planned(), 1);

        SchedulerAction action = Assert.Single(Decide(At(9, 0, dayOffset: 7), [weekly]));

        Assert.Equal($"1:{Mon + 7}", action.Occurrence.Key);
    }

    [Fact]
    public void An_edited_occurrence_starts_at_its_new_time_but_keeps_its_key()
    {
        CalendarEntry weekly = Kit.Weekly(Planned(), 1);
        weekly.Overrides.Add(new OccurrenceOverride { OriginalDay = Mon + 7, Day = Mon + 8, Minute = 600, Duration = 45, Title = "Moved" });

        Assert.Empty(Decide(At(9, 0, dayOffset: 7), [weekly]).Where(a => a.Kind == SchedulerActionKind.Start));
        SchedulerAction moved = Assert.Single(Decide(At(10, 0, dayOffset: 8), [weekly]));
        Assert.Equal(($"1:{Mon + 7}", 45), (moved.Occurrence.Key, moved.Minutes));
    }

    [Fact]
    public void Tasks_are_never_started()
    {
        CalendarEntry task = Kit.Task("a", Mon, 540);
        task.Id = 1;

        Assert.Empty(Decide(At(9, 0), [task]));
    }

    [Fact]
    public void A_session_in_the_spring_forward_gap_starts_at_the_first_valid_minute()
    {
        CalendarEntry gap = Kit.Session("Gap", Kit.D(2026, 3, 29), 150, 60);
        gap.Id = 1;
        var before = new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero);

        Assert.DoesNotContain(Decide(before, [gap], zone: Kit.Warsaw), a => a.Kind == SchedulerActionKind.Start);
        Assert.Equal(SchedulerActionKind.Start, Assert.Single(Decide(at, [gap], zone: Kit.Warsaw), a => a.Kind == SchedulerActionKind.Start).Kind);
    }

    [Fact]
    public void A_session_in_the_autumn_overlap_starts_on_the_first_pass_only()
    {
        CalendarEntry twice = Kit.Session("Twice", Kit.D(2026, 10, 25), 150, 60);
        twice.Id = 1;
        var first = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);

        Assert.Equal(SchedulerActionKind.Start, Assert.Single(Decide(first, [twice], zone: Kit.Warsaw)).Kind);
        Assert.Equal(SchedulerActionKind.Miss, Assert.Single(Decide(second, [twice], zone: Kit.Warsaw)).Kind);
    }

    [Fact]
    public void The_same_wall_clock_session_starts_at_different_instants_in_different_zones()
    {
        CalendarEntry session = Planned();

        Assert.Equal(SchedulerActionKind.Start, Assert.Single(Decide(At(9, 0), [session], zone: TimeZoneInfo.Utc)).Kind);
        Assert.DoesNotContain(Decide(At(9, 0), [session], zone: Kit.Warsaw), a => a.Kind == SchedulerActionKind.Start);
        Assert.Equal(SchedulerActionKind.Start, Assert.Single(Decide(At(8, 0), [session], zone: Kit.Warsaw)).Kind);
    }
}

public class CalendarAutoStarterTests
{
    private static readonly int Today = Kit.D(2026, 1, 1);

    private static async Task<(FakePluginContext Ctx, CalendarStore Store, CalendarAutoStarter Starter)> StartAsync(Action<CalendarEntry>? tweak = null, int minute = 545, int length = 60)
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync();
        CalendarEntry entry = Kit.Session("Deep work", Today, minute, length);
        tweak?.Invoke(entry);
        _ = await store.AddAsync(entry, CancellationToken.None);
        var starter = new CalendarAutoStarter(store, ctx.Sessions, ctx.Notifier, ctx.Logger);
        ctx.Extensions.Add(starter);
        return (ctx, store, starter);
    }

    private static async Task StepAsync(FakePluginContext ctx, CalendarAutoStarter starter, TimeSpan total)
    {
        for (TimeSpan done = TimeSpan.Zero; done < total; done += TimeSpan.FromSeconds(15))
        {
            await ctx.TickAsync(TimeSpan.FromSeconds(15));
            await starter.Work;
        }
    }

    // Simulates the PC waking up: the clock jumped, and the next tick sees the new time.
    private static async Task TickNowAsync(FakePluginContext ctx, CalendarAutoStarter starter, TimeSpan jump)
    {
        ctx.FakeTime.Advance(jump);
        await starter.OnTickAsync(new TickContext(ctx.FakeTime.GetUtcNow(), ctx.FakeSession.Current), CancellationToken.None);
        await starter.Work;
    }

    [Fact]
    public async Task The_starter_is_a_tick_listener_with_a_fifteen_second_interval()
    {
        (FakePluginContext ctx, _, CalendarAutoStarter starter) = await StartAsync();

        Assert.Same(starter, ctx.GetExtension<ITickListener>());
        Assert.Equal(TimeSpan.FromSeconds(15), starter.Interval);
    }

    [Fact]
    public async Task A_reminder_then_one_start_at_the_planned_time_and_never_a_second()
    {
        (FakePluginContext ctx, CalendarStore store, CalendarAutoStarter starter) = await StartAsync();

        await StepAsync(ctx, starter, TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(30));
        Assert.Empty(ctx.FakeSessions.Calls);
        Assert.Single(ctx.FakeNotifier.Messages);
        Assert.Equal("\"Deep work\" starts in 1 min. It cannot be shortened once it begins.", ctx.FakeNotifier.Messages[0]);

        await StepAsync(ctx, starter, TimeSpan.FromSeconds(45));
        (TimeSpan duration, string reason) = Assert.Single(ctx.FakeSessions.Calls);
        Assert.Equal((TimeSpan.FromMinutes(60), "Deep work"), (duration, reason));
        Assert.Equal(SessionOutcome.Started, Assert.Single(store.History).Outcome);

        await StepAsync(ctx, starter, TimeSpan.FromMinutes(30));
        Assert.Single(ctx.FakeSessions.Calls);
        Assert.Single(ctx.FakeNotifier.Messages);
    }

    [Fact]
    public async Task A_running_session_prevents_the_start_and_the_occurrence_is_then_missed_with_a_notice()
    {
        (FakePluginContext ctx, CalendarStore store, CalendarAutoStarter starter) = await StartAsync();
        ctx.FakeSession.Current = Kit.Running();

        await StepAsync(ctx, starter, TimeSpan.FromMinutes(17));

        Assert.Empty(ctx.FakeSessions.Calls);
        Assert.Equal(SessionOutcome.Missed, Assert.Single(store.History).Outcome);
        Assert.Contains(ctx.FakeNotifier.Messages, m => m.StartsWith("Missed \"Deep work\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_that_ends_inside_the_grace_window_lets_the_planned_one_start_late_for_the_remaining_time()
    {
        (FakePluginContext ctx, _, CalendarAutoStarter starter) = await StartAsync();
        ctx.FakeSession.Current = Kit.Running();
        await StepAsync(ctx, starter, TimeSpan.FromMinutes(6));
        Assert.Empty(ctx.FakeSessions.Calls);

        ctx.FakeSession.Current = Blocker.Sdk.Session.SessionSnapshot.Idle;
        await StepAsync(ctx, starter, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromMinutes(59), Assert.Single(ctx.FakeSessions.Calls).Duration);
    }

    [Fact]
    public async Task After_a_long_sleep_the_missed_session_is_recorded_and_never_started()
    {
        (FakePluginContext ctx, CalendarStore store, CalendarAutoStarter starter) = await StartAsync();

        await TickNowAsync(ctx, starter, TimeSpan.FromHours(3));

        Assert.Empty(ctx.FakeSessions.Calls);
        Assert.Equal(SessionOutcome.Missed, Assert.Single(store.History).Outcome);
    }

    [Fact]
    public async Task After_a_short_sleep_the_session_starts_for_its_remaining_time()
    {
        (FakePluginContext ctx, _, CalendarAutoStarter starter) = await StartAsync();

        await TickNowAsync(ctx, starter, TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.FromMinutes(55), Assert.Single(ctx.FakeSessions.Calls).Duration);
    }

    [Fact]
    public async Task A_restart_does_not_start_the_same_occurrence_again()
    {
        (FakePluginContext ctx, CalendarStore store, CalendarAutoStarter starter) = await StartAsync();
        await StepAsync(ctx, starter, TimeSpan.FromMinutes(6));
        Assert.Single(ctx.FakeSessions.Calls);

        var reloaded = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await reloaded.LoadAsync(CancellationToken.None);
        var second = new CalendarAutoStarter(reloaded, ctx.Sessions, ctx.Notifier, ctx.Logger);
        await second.OnTickAsync(new TickContext(ctx.FakeTime.GetUtcNow(), ctx.FakeSession.Current), CancellationToken.None);
        await second.Work;

        Assert.Single(ctx.FakeSessions.Calls);
        Assert.Single(store.History);
    }

    [Fact]
    public async Task A_refusal_is_retried_twice_then_recorded_as_failed_with_a_notice()
    {
        (FakePluginContext ctx, CalendarStore store, CalendarAutoStarter starter) = await StartAsync();
        ctx.FakeSessions.NextResult = new SessionStartResult(false, "The saved allowlist is empty.");

        await StepAsync(ctx, starter, TimeSpan.FromMinutes(8));

        Assert.Equal(CalendarAutoStarter.MaxAttempts, ctx.FakeSessions.Calls.Count);
        Assert.Equal(SessionOutcome.Failed, Assert.Single(store.History).Outcome);
        Assert.Contains(ctx.FakeNotifier.Messages, m => m == "Could not start \"Deep work\": The saved allowlist is empty.");
    }

    [Fact]
    public async Task A_refusal_that_clears_up_lets_the_next_attempt_start_the_session()
    {
        (FakePluginContext ctx, CalendarStore store, CalendarAutoStarter starter) = await StartAsync();
        ctx.FakeSessions.NextResult = new SessionStartResult(false, "A session is already running.");
        await StepAsync(ctx, starter, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15));
        Assert.Single(ctx.FakeSessions.Calls);
        Assert.Empty(store.History);

        ctx.FakeSessions.NextResult = new SessionStartResult(true, null);
        await StepAsync(ctx, starter, TimeSpan.FromSeconds(45));

        Assert.Equal(2, ctx.FakeSessions.Calls.Count);
        Assert.Equal(SessionOutcome.Started, Assert.Single(store.History).Outcome);
    }

    [Fact]
    public async Task With_auto_start_off_nothing_is_started_or_announced()
    {
        (FakePluginContext ctx, CalendarStore store, CalendarAutoStarter starter) = await StartAsync();
        await store.SetSettingsAsync(new CalendarSettings { AutoStart = false }, CancellationToken.None);

        await StepAsync(ctx, starter, TimeSpan.FromMinutes(20));

        Assert.Empty(ctx.FakeSessions.Calls);
        Assert.Empty(ctx.FakeNotifier.Messages);
        Assert.Empty(store.History);
    }

    [Fact]
    public async Task A_daily_series_starts_every_day_once()
    {
        (FakePluginContext ctx, _, CalendarAutoStarter starter) = await StartAsync(e => Kit.Daily(e), 600, 30);

        await StepAsync(ctx, starter, TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5));
        Assert.Single(ctx.FakeSessions.Calls);

        await TickNowAsync(ctx, starter, TimeSpan.FromHours(23) + TimeSpan.FromMinutes(55));

        Assert.Equal(2, ctx.FakeSessions.Calls.Count);
    }

    [Fact]
    public async Task The_tick_itself_returns_before_the_service_call_finishes()
    {
        (FakePluginContext ctx, _, CalendarAutoStarter starter) = await StartAsync();
        ctx.FakeTime.Advance(TimeSpan.FromMinutes(5));

        ValueTask tick = starter.OnTickAsync(new TickContext(ctx.FakeTime.GetUtcNow(), ctx.FakeSession.Current), CancellationToken.None);

        Assert.True(tick.IsCompleted);
        await starter.Work;
        Assert.Single(ctx.FakeSessions.Calls);
    }

    [Fact]
    public async Task Shutdown_waits_for_running_work()
    {
        (FakePluginContext ctx, _, CalendarAutoStarter starter) = await StartAsync();
        ctx.FakeTime.Advance(TimeSpan.FromMinutes(5));
        await starter.OnTickAsync(new TickContext(ctx.FakeTime.GetUtcNow(), ctx.FakeSession.Current), CancellationToken.None);

        await starter.StopAsync();

        Assert.True(starter.Work.IsCompleted);
    }
}
