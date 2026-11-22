using System.Text.Json;
using Blocker.Plugin.Testing;
using Blocker.Sdk.Storage;
using Xunit;

namespace CalendarPlanner.Tests;

public class CalendarFormTests
{
    [Fact]
    public void A_valid_session_becomes_a_new_entry()
    {
        FormOutcome outcome = Kit.Submit(Kit.Form());

        Assert.True(outcome.Accepted);
        CalendarEntry entry = Assert.IsType<CalendarEntry>(outcome.Add);
        Assert.Equal((EntryKind.Session, "Deep work", Kit.D(2026, 1, 2), 600, 60, RepeatKind.Once), (entry.Kind, entry.Title, entry.StartDay, entry.StartMinute, entry.Duration, entry.Repeat));
    }

    [Fact]
    public void A_session_without_a_title_is_called_focus_session()
    {
        Assert.Equal("Focus session", Kit.Submit(Kit.Form(title: "  ")).Add!.Title);
    }

    [Fact]
    public void A_task_needs_a_title_but_not_a_time()
    {
        Assert.False(Kit.Submit(Kit.Form(kind: "task", title: " ", time: string.Empty, length: "0")).Accepted);

        CalendarEntry task = Kit.Submit(Kit.Form(kind: "task", title: "Buy milk", time: string.Empty, length: "30")).Add!;

        Assert.Equal((-1, 0), (task.StartMinute, task.Duration));
    }

    [Fact]
    public void A_task_with_a_time_keeps_its_length()
    {
        CalendarEntry task = Kit.Submit(Kit.Form(kind: "task", title: "Call", time: "14:30", length: "45")).Add!;

        Assert.Equal((870, 45), (task.StartMinute, task.Duration));
    }

    [Theory]
    [InlineData("14")]
    [InlineData("721")]
    [InlineData("abc")]
    [InlineData("")]
    public void A_session_length_outside_fifteen_minutes_to_twelve_hours_is_refused(string length)
    {
        FormOutcome outcome = Kit.Submit(Kit.Form(length: length));

        Assert.False(outcome.Accepted);
        Assert.NotNull(outcome.Error);
    }

    [Theory]
    [InlineData("15")]
    [InlineData("720")]
    [InlineData("90,4")]
    public void Session_lengths_at_the_limits_are_accepted(string length) => Assert.True(Kit.Submit(Kit.Form(length: length)).Accepted);

    [Fact]
    public void A_session_needs_a_start_time_and_it_must_be_valid()
    {
        Assert.Equal("A session needs a start time.", Kit.Submit(Kit.Form(time: string.Empty)).Error);
        Assert.Equal("Enter the time as hh:mm, for example 09:30.", Kit.Submit(Kit.Form(time: "25:00")).Error);
    }

    [Fact]
    public void A_bad_date_is_refused_and_an_impossible_one_is_adjusted_with_a_notice()
    {
        Assert.Equal("Enter the date as yyyy-mm-dd, for example 2026-03-09.", Kit.Submit(Kit.Form(date: "soon")).Error);

        FormOutcome adjusted = Kit.Submit(Kit.Form(date: "2026-02-31"));

        Assert.Equal(Kit.D(2026, 2, 28), adjusted.Add!.StartDay);
        Assert.Equal("The date was adjusted to 2026-02-28.", adjusted.Notice);
        Assert.Null(Kit.Submit(Kit.Form(date: "2026-02-20")).Notice);
    }

    [Fact]
    public void A_one_time_session_in_the_past_is_refused()
    {
        Assert.Equal("That time has already passed.", Kit.Submit(Kit.Form(date: "2025-12-31")).Error);
        Assert.Equal("That time has already passed.", Kit.Submit(Kit.Form(date: "2026-01-01", time: "08:59")).Error);
        Assert.True(Kit.Submit(Kit.Form(date: "2026-01-01", time: "09:01")).Accepted);
    }

    [Fact]
    public void A_task_in_the_past_is_fine()
    {
        Assert.True(Kit.Submit(Kit.Form(kind: "task", date: "2025-06-01", time: string.Empty, length: "0")).Accepted);
    }

    [Fact]
    public void A_series_that_started_in_the_past_still_has_a_future_occurrence()
    {
        Assert.True(Kit.Submit(Kit.Form(date: "2025-12-01", repeat: "daily")).Accepted);
    }

    [Fact]
    public void A_series_whose_every_occurrence_is_in_the_past_is_refused()
    {
        FormOutcome outcome = Kit.Submit(Kit.Form(date: "2025-12-01", repeat: "daily", end: "count", count: "3"));

        Assert.Equal("Every occurrence is in the past.", outcome.Error);
    }

    [Fact]
    public void Weekly_takes_the_checked_weekdays_and_defaults_to_the_weekday_of_the_date()
    {
        CalendarEntry picked = Kit.Submit(Kit.Form(date: "2026-01-05", repeat: "weekly", weekdays: [0, 2, 4])).Add!;
        CalendarEntry none = Kit.Submit(Kit.Form(date: "2026-01-07", repeat: "weekly")).Add!;

        Assert.Equal(0b10101, picked.WeekdayMask);
        Assert.Equal(0, none.WeekdayMask);
        Assert.Equal(RepeatKind.Weekly, none.Repeat);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("53")]
    [InlineData("x")]
    public void The_interval_must_be_between_1_and_52(string every) => Assert.False(Kit.Submit(Kit.Form(repeat: "daily", every: every)).Accepted);

    [Fact]
    public void An_end_date_before_the_first_day_is_refused_and_a_valid_one_is_kept()
    {
        Assert.Equal("The end date is before the first day.", Kit.Submit(Kit.Form(repeat: "daily", end: "date", until: "2026-01-01")).Error);
        Assert.Equal("Enter the end date as yyyy-mm-dd.", Kit.Submit(Kit.Form(repeat: "daily", end: "date", until: "later")).Error);

        CalendarEntry entry = Kit.Submit(Kit.Form(repeat: "daily", end: "date", until: "2026-02-30")).Add!;

        Assert.Equal((EndKind.OnDate, Kit.D(2026, 2, 28)), (entry.End, entry.UntilDay));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("367")]
    public void The_count_must_be_between_1_and_366(string count) => Assert.False(Kit.Submit(Kit.Form(repeat: "daily", end: "count", count: count)).Accepted);

    [Fact]
    public void A_one_time_entry_ignores_the_end_rule()
    {
        CalendarEntry entry = Kit.Submit(Kit.Form(repeat: "once", end: "count", count: "999")).Add!;

        Assert.Equal(EndKind.Never, entry.End);
    }

    [Fact]
    public void Unknown_choices_are_refused()
    {
        Assert.False(Kit.Submit(Kit.Form(kind: "meeting")).Accepted);
        Assert.False(Kit.Submit(Kit.Form(repeat: "monthly")).Accepted);
    }

    [Fact]
    public void An_out_of_range_colour_becomes_none()
    {
        Assert.Equal(0, Kit.Submit(Kit.Form(color: "9")).Add!.Color);
        Assert.Equal(3, Kit.Submit(Kit.Form(color: "3")).Add!.Color);
    }

    [Fact]
    public void A_clashing_session_is_refused_with_the_name_of_the_other_one()
    {
        CalendarEntry other = Kit.Session("Morning", Kit.D(2026, 1, 2), 570, 60);
        other.Id = 5;

        FormOutcome outcome = Kit.Submit(Kit.Form(time: "10:00"), [other]);

        Assert.Equal("Overlaps \"Morning\" on Fri 2 Jan at 09:30.", outcome.Error);
        Assert.True(Kit.Submit(Kit.Form(time: "10:30"), [other]).Accepted);
    }

    [Fact]
    public void A_task_may_overlap_a_session()
    {
        CalendarEntry other = Kit.Session("Morning", Kit.D(2026, 1, 2), 570, 60);
        other.Id = 5;

        Assert.True(Kit.Submit(Kit.Form(kind: "task", time: "10:00", length: "30"), [other]).Accepted);
    }

    [Fact]
    public void A_weekly_series_that_clashes_later_is_refused()
    {
        CalendarEntry other = Kit.Session("Third friday", Kit.D(2026, 1, 16), 600, 60);
        other.Id = 5;

        Assert.False(Kit.Submit(Kit.Form(date: "2026-01-02", time: "10:00", repeat: "weekly", weekdays: [4]), [other]).Accepted);
    }

    [Fact]
    public void Editing_a_one_time_entry_replaces_it_without_clashing_with_itself()
    {
        CalendarEntry existing = Kit.Session("Morning", Kit.D(2026, 1, 2), 570, 60);
        existing.Id = 5;
        CalendarDraft draft = CalendarForm.EditDraft(CalendarRecurrence.Expand([existing], existing.StartDay, existing.StartDay).Single());

        FormOutcome outcome = Kit.Submit(Kit.Form(title: "Renamed", time: "09:45"), [existing], draft);

        Assert.True(outcome.Accepted);
        Assert.Equal((5, "Renamed", 585), (outcome.Replace!.Id, outcome.Replace.Title, outcome.Replace.StartMinute));
    }

    [Fact]
    public void Editing_one_occurrence_of_a_series_makes_an_override_and_leaves_the_series_alone()
    {
        CalendarEntry series = Kit.Weekly(Kit.Session("Weekly", Kit.D(2026, 1, 5), 540, 60), 1);
        series.Id = 7;
        Occurrence second = CalendarRecurrence.Expand([series], Kit.D(2026, 1, 12), Kit.D(2026, 1, 12)).Single();
        CalendarDraft draft = CalendarForm.EditDraft(second);

        FormOutcome outcome = Kit.Submit(Kit.Form(title: "Longer", date: "2026-01-13", time: "09:00", length: "90", repeat: "weekly", weekdays: [0], scope: "occurrence"), [series], draft);

        Assert.True(outcome.Accepted);
        Assert.Null(outcome.Replace);
        Assert.Equal(7, outcome.OverrideEntryId);
        OccurrenceOverride change = outcome.Override!;
        Assert.Equal((Kit.D(2026, 1, 12), Kit.D(2026, 1, 13), 540, 90, "Longer"), (change.OriginalDay, change.Day, change.Minute, change.Duration, change.Title));
    }

    [Fact]
    public void Editing_all_occurrences_replaces_the_series_and_moving_the_date_shifts_it()
    {
        CalendarEntry series = Kit.Weekly(Kit.Session("Weekly", Kit.D(2026, 1, 5), 540, 60), 1);
        series.Id = 7;
        Occurrence second = CalendarRecurrence.Expand([series], Kit.D(2026, 1, 12), Kit.D(2026, 1, 12)).Single();
        CalendarDraft draft = CalendarForm.EditDraft(second);

        FormOutcome same = Kit.Submit(Kit.Form(date: "2026-01-12", repeat: "weekly", weekdays: [0], scope: "series"), [series], draft);
        FormOutcome shifted = Kit.Submit(Kit.Form(date: "2026-01-13", repeat: "weekly", weekdays: [0], scope: "series"), [series], draft);

        Assert.Equal(Kit.D(2026, 1, 5), same.Replace!.StartDay);
        Assert.Equal(Kit.D(2026, 1, 6), shifted.Replace!.StartDay);
    }

    [Fact]
    public void An_occurrence_cannot_be_moved_into_the_past_or_onto_another_session()
    {
        CalendarEntry series = Kit.Weekly(Kit.Session("Weekly", Kit.D(2026, 1, 5), 540, 60), 1);
        series.Id = 7;
        CalendarEntry other = Kit.Session("Other", Kit.D(2026, 1, 13), 540, 60);
        other.Id = 8;
        CalendarDraft draft = CalendarForm.EditDraft(CalendarRecurrence.Expand([series], Kit.D(2026, 1, 12), Kit.D(2026, 1, 12)).Single());

        Assert.Equal("That time has already passed.", Kit.Submit(Kit.Form(date: "2025-12-31", repeat: "weekly", weekdays: [0], scope: "occurrence"), [series, other], draft).Error);
        Assert.StartsWith("Overlaps \"Other\"", Kit.Submit(Kit.Form(date: "2026-01-13", time: "09:30", repeat: "weekly", weekdays: [0], scope: "occurrence"), [series, other], draft).Error);
    }

    [Fact]
    public void Editing_something_that_was_deleted_is_refused()
    {
        var draft = new CalendarDraft { EditId = 99, Day = Kit.D(2026, 1, 2) };

        Assert.Equal("That entry no longer exists.", Kit.Submit(Kit.Form(), [], draft).Error);
    }

    [Fact]
    public void A_dragged_block_becomes_a_session_draft_with_the_default_title()
    {
        CalendarDraft draft = CalendarForm.NewDraft(Kit.D(2026, 1, 5), 600, 690);
        CalendarDraft toMidnight = CalendarForm.NewDraft(Kit.D(2026, 1, 5), 1380, 1440);

        Assert.Equal((EntryKind.Session, "Focus session", Kit.D(2026, 1, 5), "10:00", "11:30", 0), (draft.Kind, draft.Title, draft.Day, draft.Time, draft.To, draft.EditId));
        Assert.Equal(("23:00", "00:00"), (toMidnight.Time, toMidnight.To));
    }

    [Fact]
    public void An_empty_end_gives_a_session_the_default_length_and_a_task_none()
    {
        Dictionary<string, string> session = Kit.Form();
        session[CalendarForm.ToId] = string.Empty;
        Dictionary<string, string> task = Kit.Form(kind: "task", title: "Call");
        task[CalendarForm.ToId] = " ";

        CalendarEntry s = Kit.Submit(session).Add!;
        CalendarEntry t = Kit.Submit(task).Add!;

        Assert.Equal(120, s.Duration);
        Assert.Equal((600, 0), (t.StartMinute, t.Duration));
    }

    [Fact]
    public void An_end_before_the_start_means_the_next_day_and_a_bad_end_is_refused()
    {
        Dictionary<string, string> late = Kit.Form(time: "23:00");
        late[CalendarForm.ToId] = "01:00";
        Dictionary<string, string> bad = Kit.Form();
        bad[CalendarForm.ToId] = "soon";

        Assert.Equal(120, Kit.Submit(late).Add!.Duration);
        Assert.Equal("Enter the end time as hh:mm, for example 10:30.", Kit.Submit(bad).Error);
    }

    [Fact]
    public void A_form_without_the_extra_fields_keeps_what_the_draft_has()
    {
        CalendarEntry series = Kit.Weekly(Kit.Task("Water plants", Kit.D(2026, 1, 5), 480), 0b101, 2);
        series.Id = 3;
        series.Duration = 30;
        series.End = EndKind.OnDate;
        series.UntilDay = Kit.D(2026, 6, 30);
        series.Color = 2;
        CalendarDraft draft = CalendarForm.EditDraft(CalendarRecurrence.Expand([series], series.StartDay, series.StartDay).Single());
        Dictionary<string, string> quick = new()
        {
            [CalendarForm.TitleId] = "Water the plants",
            [CalendarForm.TimeId] = "08:00",
            [CalendarForm.ToId] = "08:45",
            [CalendarForm.RepeatId] = "weekly",
            [CalendarForm.ScopeId] = "series",
        };

        CalendarEntry saved = Kit.Submit(quick, [series], draft).Replace!;

        Assert.Equal(("Water the plants", 45, 2, 0b101, EndKind.OnDate, Kit.D(2026, 6, 30), 2, Kit.D(2026, 1, 5)), (saved.Title, saved.Duration, saved.Interval, saved.WeekdayMask, saved.End, saved.UntilDay, saved.Color, saved.StartDay));
    }

    [Fact]
    public void Moving_a_one_time_entry_replaces_it_and_resizing_changes_its_length()
    {
        CalendarEntry session = Kit.Session("Deep work", Kit.D(2026, 1, 5), 600, 60);
        session.Id = 4;
        Occurrence occurrence = CalendarRecurrence.OccurrenceOf(session, session.StartDay)!;

        FormOutcome moved = CalendarForm.Move(occurrence, Kit.D(2026, 1, 6), 780, 870, [session], Kit.Start, TimeZoneInfo.Utc, new CalendarSettings());
        FormOutcome resized = CalendarForm.Move(occurrence, session.StartDay, 600, 750, [session], Kit.Start, TimeZoneInfo.Utc, new CalendarSettings());

        Assert.Equal((4, Kit.D(2026, 1, 6), 780, 90), (moved.Replace!.Id, moved.Replace.StartDay, moved.Replace.StartMinute, moved.Replace.Duration));
        Assert.Equal((Kit.D(2026, 1, 5), 600, 150), (resized.Replace!.StartDay, resized.Replace.StartMinute, resized.Replace.Duration));
    }

    [Fact]
    public void Moving_one_occurrence_of_a_series_changes_only_that_occurrence()
    {
        CalendarEntry series = Kit.Weekly(Kit.Session("Weekly", Kit.D(2026, 1, 5), 540, 60), 1);
        series.Id = 7;
        Occurrence second = CalendarRecurrence.OccurrenceOf(series, Kit.D(2026, 1, 12))!;

        FormOutcome outcome = CalendarForm.Move(second, Kit.D(2026, 1, 13), 600, 660, [series], Kit.Start, TimeZoneInfo.Utc, new CalendarSettings());

        Assert.Null(outcome.Replace);
        Assert.Equal(7, outcome.OverrideEntryId);
        OccurrenceOverride change = outcome.Override!;
        Assert.Equal((Kit.D(2026, 1, 12), Kit.D(2026, 1, 13), 600, 60, "Weekly"), (change.OriginalDay, change.Day, change.Minute, change.Duration, change.Title));
        Assert.Equal("Only this occurrence moved; the rest of the series stays.", outcome.Notice);
    }

    [Fact]
    public void A_block_cannot_be_moved_into_the_past_onto_another_session_or_made_longer_than_twelve_hours()
    {
        CalendarEntry session = Kit.Session("Deep work", Kit.D(2026, 1, 5), 600, 60);
        session.Id = 4;
        CalendarEntry other = Kit.Session("Other", Kit.D(2026, 1, 6), 780, 60);
        other.Id = 5;
        Occurrence occurrence = CalendarRecurrence.OccurrenceOf(session, session.StartDay)!;
        var settings = new CalendarSettings();

        Assert.Equal("That time has already passed.", CalendarForm.Move(occurrence, Kit.D(2025, 12, 30), 600, 660, [session, other], Kit.Start, TimeZoneInfo.Utc, settings).Error);
        Assert.StartsWith("Overlaps \"Other\"", CalendarForm.Move(occurrence, Kit.D(2026, 1, 6), 800, 860, [session, other], Kit.Start, TimeZoneInfo.Utc, settings).Error);
        Assert.Equal("A session lasts from 15 minutes to 12 hours.", CalendarForm.Move(occurrence, session.StartDay, 0, 780, [session, other], Kit.Start, TimeZoneInfo.Utc, settings).Error);
        Assert.Equal("That entry no longer exists.", CalendarForm.Move(occurrence, session.StartDay, 600, 660, [], Kit.Start, TimeZoneInfo.Utc, settings).Error);
    }

    [Fact]
    public void A_task_without_a_length_keeps_none_when_it_is_moved_as_the_short_block_it_is_drawn_as()
    {
        CalendarEntry task = Kit.Task("Call", Kit.D(2026, 1, 5), 600);
        task.Id = 2;
        Occurrence occurrence = CalendarRecurrence.OccurrenceOf(task, task.StartDay)!;

        FormOutcome moved = CalendarForm.Move(occurrence, task.StartDay, 660, 690, [task], Kit.Start, TimeZoneInfo.Utc, new CalendarSettings());
        FormOutcome resized = CalendarForm.Move(occurrence, task.StartDay, 600, 720, [task], Kit.Start, TimeZoneInfo.Utc, new CalendarSettings());

        Assert.Equal((660, 0), (moved.Replace!.StartMinute, moved.Replace.Duration));
        Assert.Equal(120, resized.Replace!.Duration);
    }

    [Fact]
    public void A_copy_is_a_one_time_entry_shifted_by_the_given_days_and_a_clashing_session_is_refused()
    {
        CalendarEntry series = Kit.Weekly(Kit.Session("Weekly", Kit.D(2026, 1, 5), 540, 60), 1);
        series.Id = 7;
        series.Color = 3;
        Occurrence first = CalendarRecurrence.OccurrenceOf(series, series.StartDay)!;

        FormOutcome next = CalendarForm.Copy(first, 1, [series], Kit.Start, TimeZoneInfo.Utc, new CalendarSettings());
        FormOutcome clash = CalendarForm.Copy(first, 7, [series], Kit.Start, TimeZoneInfo.Utc, new CalendarSettings());

        CalendarEntry copy = next.Add!;
        Assert.Equal((RepeatKind.Once, Kit.D(2026, 1, 6), 540, 60, "Weekly", 3), (copy.Repeat, copy.StartDay, copy.StartMinute, copy.Duration, copy.Title, copy.Color));
        Assert.StartsWith("Overlaps \"Weekly\"", clash.Error);
    }

    [Fact]
    public void An_edit_draft_carries_the_series_rule()
    {
        CalendarEntry series = Kit.Weekly(Kit.Task("Water plants", Kit.D(2026, 1, 5), 480), 0b0000101, 2);
        series.End = EndKind.OnDate;
        series.UntilDay = Kit.D(2026, 6, 30);
        series.Color = 2;

        CalendarDraft draft = CalendarForm.EditDraft(CalendarRecurrence.Expand([series], series.StartDay, series.StartDay).Single());

        Assert.Equal(("Water plants", "08:00", RepeatKind.Weekly, 2, 0b101, "2026-06-30", 2, true), (draft.Title, draft.Time, draft.Repeat, draft.Every, draft.Mask, draft.Until, draft.Color, draft.EditOccurrenceOnly));
    }
}

public class CalendarStoreTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Entries_get_ids_and_a_creation_time_and_survive_a_restart()
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync();

        CalendarEntry a = (await store.AddAsync(Kit.Task("a", Kit.D(2026, 1, 5)), Ct))!;
        CalendarEntry b = (await store.AddAsync(Kit.Session("b", Kit.D(2026, 1, 6), 540), Ct))!;
        var reloaded = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await reloaded.LoadAsync(Ct);

        Assert.Equal((1, 2), (a.Id, b.Id));
        Assert.Equal(Kit.Start.ToUnixTimeSeconds(), a.Created);
        Assert.Equal(["a", "b"], reloaded.Entries.Select(e => e.Title));
        Assert.Equal(EntryKind.Session, reloaded.Find(2)!.Kind);
    }

    [Fact]
    public async Task Ids_are_never_reused_even_after_a_delete_and_restart()
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync();
        _ = await store.AddAsync(Kit.Task("a", Kit.D(2026, 1, 5)), Ct);
        _ = await store.AddAsync(Kit.Task("b", Kit.D(2026, 1, 5)), Ct);
        _ = await store.DeleteAsync(2, Ct);
        var reloaded = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await reloaded.LoadAsync(Ct);

        CalendarEntry next = (await reloaded.AddAsync(Kit.Task("c", Kit.D(2026, 1, 5)), Ct))!;

        Assert.Equal(3, next.Id);
    }

    [Fact]
    public async Task The_stored_document_uses_short_names_and_stays_small()
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync();
        _ = await store.AddAsync(Kit.Task("a", Kit.D(2026, 1, 5)), Ct);

        using JsonDocument doc = JsonDocument.Parse((await ctx.Storage.ReadAsync(CalendarStore.DataKey, Ct))!);

        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        JsonElement entry = doc.RootElement.GetProperty("e")[0];
        Assert.Equal("a", entry.GetProperty("t").GetString());
        Assert.Equal(-1, entry.GetProperty("m").GetInt32());
        Assert.True((await ctx.Storage.ReadAsync(CalendarStore.DataKey, Ct))!.Length < 400);
    }

    [Fact]
    public async Task Values_equal_to_the_type_default_survive_a_restart()
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync();
        _ = await store.AddAsync(Kit.Session("Midnight", Kit.D(2026, 1, 5), 0, 30), Ct);
        await store.SetSettingsAsync(new CalendarSettings { Use24Hour = false, AutoStart = false, ReminderMinutes = 0 }, Ct);
        var reloaded = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await reloaded.LoadAsync(Ct);

        Assert.Equal(0, reloaded.Entries.Single().StartMinute);
        Assert.Equal((false, false, 0), (reloaded.Settings.Use24Hour, reloaded.Settings.AutoStart, reloaded.Settings.ReminderMinutes));
    }

    [Fact]
    public async Task Titles_are_cleaned_and_limited()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();

        CalendarEntry a = (await store.AddAsync(Kit.Task("  hello\r\nworld  ", Kit.D(2026, 1, 5)), Ct))!;
        CalendarEntry b = (await store.AddAsync(Kit.Task(new string('x', 200), Kit.D(2026, 1, 5)), Ct))!;
        CalendarEntry c = (await store.AddAsync(Kit.Task("   ", Kit.D(2026, 1, 5)), Ct))!;

        Assert.Equal("hello  world", a.Title);
        Assert.Equal(CalendarLimits.MaxTitleLength, b.Title.Length);
        Assert.Equal("Untitled", c.Title);
    }

    [Fact]
    public async Task The_calendar_holds_at_most_300_entries()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();
        for (int i = 0; i < CalendarLimits.MaxEntries; i++)
        {
            Assert.NotNull(await store.AddAsync(Kit.Task("t", Kit.D(2026, 1, 5)), Ct));
        }

        Assert.Null(await store.AddAsync(Kit.Task("one more", Kit.D(2026, 1, 5)), Ct));
        Assert.Equal(CalendarLimits.MaxEntries, store.Entries.Count);
    }

    [Fact]
    public async Task Replacing_a_series_drops_edits_and_stale_skips()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();
        CalendarEntry series = Kit.Weekly(Kit.Task("a", Kit.D(2026, 1, 5)), 1);
        series.Skipped.Add(Kit.D(2026, 1, 12));
        series.Overrides.Add(new OccurrenceOverride { OriginalDay = Kit.D(2026, 1, 19), Day = Kit.D(2026, 1, 20), Minute = -1, Title = "x" });
        CalendarEntry stored = (await store.AddAsync(series, Ct))!;
        CalendarEntry changed = stored.Clone();
        changed.StartDay = Kit.D(2026, 1, 6);
        changed.WeekdayMask = 0b10;

        Assert.True(await store.ReplaceAsync(changed, Ct));

        CalendarEntry now = store.Find(stored.Id)!;
        Assert.Empty(now.Overrides);
        Assert.Empty(now.Skipped);
        Assert.Equal(Kit.D(2026, 1, 6), now.StartDay);
        Assert.False(await store.ReplaceAsync(new CalendarEntry { Id = 99, Title = "x", StartDay = 800000 }, Ct));
    }

    [Fact]
    public async Task Skipping_an_occurrence_of_a_series_hides_it_but_a_one_time_entry_is_deleted()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();
        CalendarEntry series = (await store.AddAsync(Kit.Weekly(Kit.Task("a", Kit.D(2026, 1, 5)), 1), Ct))!;
        CalendarEntry once = (await store.AddAsync(Kit.Task("b", Kit.D(2026, 1, 5)), Ct))!;

        Assert.True(await store.SkipAsync(series.Id, Kit.D(2026, 1, 12), Ct));
        Assert.False(await store.SkipAsync(series.Id, Kit.D(2026, 1, 12), Ct));
        Assert.False(await store.SkipAsync(series.Id, Kit.D(2026, 1, 13), Ct));
        Assert.True(await store.SkipAsync(once.Id, Kit.D(2026, 1, 5), Ct));

        Assert.Null(store.Find(once.Id));
        Assert.Equal([Kit.D(2026, 1, 5), Kit.D(2026, 1, 19)], Kit.Days(CalendarRecurrence.Expand(store.Entries, Kit.D(2026, 1, 5), Kit.D(2026, 1, 19))));
    }

    [Fact]
    public async Task Overriding_an_occurrence_is_validated_and_replaces_an_earlier_edit()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();
        CalendarEntry series = (await store.AddAsync(Kit.Weekly(Kit.Session("s", Kit.D(2026, 1, 5), 540), 1), Ct))!;
        var first = new OccurrenceOverride { OriginalDay = Kit.D(2026, 1, 12), Day = Kit.D(2026, 1, 12), Minute = 600, Duration = 30, Title = "one" };
        var second = new OccurrenceOverride { OriginalDay = Kit.D(2026, 1, 12), Day = Kit.D(2026, 1, 14), Minute = 660, Duration = 5000, Title = "two" };

        Assert.True(await store.OverrideAsync(series.Id, first, Ct));
        Assert.True(await store.OverrideAsync(series.Id, second, Ct));
        Assert.False(await store.OverrideAsync(series.Id, new OccurrenceOverride { OriginalDay = Kit.D(2026, 1, 13), Day = Kit.D(2026, 1, 13), Minute = 1, Duration = 20, Title = "x" }, Ct));

        OccurrenceOverride kept = Assert.Single(store.Find(series.Id)!.Overrides);
        Assert.Equal(("two", Kit.D(2026, 1, 14), 660, CalendarLimits.MaxSessionMinutes), (kept.Title, kept.Day, kept.Minute, kept.Duration));
    }

    [Fact]
    public async Task Ticking_a_task_is_per_occurrence_and_sessions_cannot_be_ticked()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();
        CalendarEntry task = (await store.AddAsync(Kit.Daily(Kit.Task("a", Kit.D(2026, 1, 5))), Ct))!;
        CalendarEntry session = (await store.AddAsync(Kit.Session("s", Kit.D(2026, 1, 5), 540), Ct))!;

        Assert.True(await store.SetDoneAsync(task.Id, Kit.D(2026, 1, 6), true, Ct));
        Assert.False(await store.SetDoneAsync(task.Id, Kit.D(2026, 1, 6), true, Ct));
        Assert.False(await store.SetDoneAsync(session.Id, Kit.D(2026, 1, 5), true, Ct));
        Assert.False(await store.SetDoneAsync(task.Id, Kit.D(2025, 1, 6), true, Ct));

        Assert.Equal([false, true, false], CalendarRecurrence.Expand(store.Entries.Where(e => e.Kind == EntryKind.Task), Kit.D(2026, 1, 5), Kit.D(2026, 1, 7)).Select(o => o.Done));
        Assert.True(await store.SetDoneAsync(task.Id, Kit.D(2026, 1, 6), false, Ct));
        Assert.Empty(store.Find(task.Id)!.DoneDays);
    }

    [Fact]
    public async Task Changes_are_visible_through_a_snapshot_taken_before_them()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();
        CalendarEntry task = (await store.AddAsync(Kit.Task("a", Kit.D(2026, 1, 5)), Ct))!;
        IReadOnlyList<CalendarEntry> before = store.Entries;

        _ = await store.SetDoneAsync(task.Id, task.StartDay, true, Ct);

        Assert.Empty(before.Single().DoneDays);
        Assert.Single(store.Find(task.Id)!.DoneDays);
    }

    [Fact]
    public async Task Clearing_past_entries_removes_finished_ones_only()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();
        int today = Kit.D(2026, 1, 10);
        CalendarEntry oldOnce = (await store.AddAsync(Kit.Task("old", today - 1), Ct))!;
        CalendarEntry todayOnce = (await store.AddAsync(Kit.Task("today", today), Ct))!;
        CalendarEntry ended = Kit.Daily(Kit.Task("ended", today - 20));
        ended.End = EndKind.OnDate;
        ended.UntilDay = today - 2;
        CalendarEntry endedStored = (await store.AddAsync(ended, Ct))!;
        CalendarEntry running = (await store.AddAsync(Kit.Daily(Kit.Task("running", today - 20)), Ct))!;
        CalendarEntry counted = Kit.Daily(Kit.Task("counted", today - 20));
        counted.End = EndKind.AfterCount;
        counted.Count = 5;
        CalendarEntry countedStored = (await store.AddAsync(counted, Ct))!;
        CalendarEntry movedIn = Kit.Weekly(Kit.Task("moved", today - 30), 1);
        movedIn.End = EndKind.AfterCount;
        movedIn.Count = 2;
        movedIn.Overrides.Add(new OccurrenceOverride { OriginalDay = today - 30, Day = today + 3, Minute = -1, Title = "later" });
        CalendarEntry movedStored = (await store.AddAsync(movedIn, Ct))!;

        int removed = await store.ClearPastAsync(today, Ct);

        Assert.Equal(3, removed);
        Assert.Equal([todayOnce.Id, running.Id, movedStored.Id], store.Entries.Select(e => e.Id));
        Assert.DoesNotContain(store.Entries, e => e.Id == oldOnce.Id || e.Id == endedStored.Id || e.Id == countedStored.Id);
        Assert.Equal(0, await store.ClearPastAsync(today, Ct));
    }

    [Fact]
    public async Task History_records_once_per_occurrence_and_can_be_forgotten()
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync();
        long now = Kit.Start.ToUnixTimeSeconds();

        Assert.True(await store.RecordAsync("1:800000", SessionOutcome.Started, now, Ct));
        Assert.False(await store.RecordAsync("1:800000", SessionOutcome.Missed, now, Ct));
        Assert.Equal(SessionOutcome.Started, Assert.Single(store.History).Outcome);
        await store.UnrecordAsync("1:800000", Ct);
        Assert.Empty(store.History);
        Assert.True(await store.RecordAsync("1:800000", SessionOutcome.Missed, now, Ct));
        var reloaded = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await reloaded.LoadAsync(Ct);
        Assert.Equal(SessionOutcome.Missed, Assert.Single(reloaded.History).Outcome);
    }

    [Fact]
    public async Task Old_history_is_dropped_and_the_list_is_bounded()
    {
        (_, CalendarStore store) = await Kit.NewStoreAsync();
        long now = Kit.Start.ToUnixTimeSeconds();

        _ = await store.RecordAsync("1:1", SessionOutcome.Started, now - (200 * 86400L), Ct);
        for (int i = 0; i < CalendarLimits.MaxHistory + 20; i++)
        {
            _ = await store.RecordAsync($"2:{i}", SessionOutcome.Started, now - i, Ct);
        }

        Assert.Equal(CalendarLimits.MaxHistory, store.History.Count);
        Assert.DoesNotContain(store.History, r => r.Key == "1:1");
        Assert.True(store.History.Select(r => r.StartUnix).SequenceEqual(store.History.Select(r => r.StartUnix).Order()));
    }

    [Fact]
    public async Task Settings_are_clamped_and_persisted()
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync();

        await store.SetSettingsAsync(new CalendarSettings { DefaultSessionMinutes = 5, ReminderMinutes = 99, WeekStartsOnSunday = true, Use24Hour = false, AutoStart = false }, Ct);
        var reloaded = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await reloaded.LoadAsync(Ct);

        CalendarSettings s = reloaded.Settings;
        Assert.Equal((15, 30, true, false, false), (s.DefaultSessionMinutes, s.ReminderMinutes, s.WeekStartsOnSunday, s.Use24Hour, s.AutoStart));
        Assert.Equal((120, 1, false, true, true), (new CalendarSettings().DefaultSessionMinutes, new CalendarSettings().ReminderMinutes, new CalendarSettings().WeekStartsOnSunday, new CalendarSettings().Use24Hour, new CalendarSettings().AutoStart));
    }

    [Fact]
    public async Task A_corrupt_entries_document_starts_empty_and_is_kept_under_another_key()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        await ctx.Storage.WriteAsync(CalendarStore.DataKey, "{ this is not json", Ct);
        await ctx.Storage.WriteAsync(CalendarStore.SettingsKey, "{\"d\":45}", Ct);

        var store = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(Ct);

        Assert.True(store.RecoveredFromCorruption);
        Assert.Empty(store.Entries);
        Assert.Equal(45, store.Settings.DefaultSessionMinutes);
        Assert.Equal(Kit.Start.ToUnixTimeSeconds() + "\n{ this is not json", await ctx.Storage.ReadAsync(CalendarStore.DataKey + CalendarStore.CorruptSuffix, Ct));
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Message.Contains("unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_calendar_that_recovered_is_usable_and_saves_normally()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        await ctx.Storage.WriteAsync(CalendarStore.DataKey, "[1,2,3]", Ct);
        var store = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(Ct);

        _ = await store.AddAsync(Kit.Task("fresh", Kit.D(2026, 1, 5)), Ct);

        var again = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await again.LoadAsync(Ct);
        Assert.False(again.RecoveredFromCorruption);
        Assert.Equal(["fresh"], again.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task Bad_entries_are_dropped_and_odd_values_are_clamped_on_load()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        string day = Kit.D(2026, 1, 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string json = "{\"v\":1,\"n\":2,\"e\":["
            + "{\"i\":1,\"k\":1,\"t\":\"ok\",\"s\":" + day + ",\"m\":99999,\"n\":99999,\"r\":2,\"v\":500,\"w\":65535,\"e\":1,\"u\":1,\"c\":999999,\"g\":-8},"
            + "{\"i\":1,\"k\":1,\"t\":\"duplicate id\",\"s\":" + day + "},"
            + "{\"i\":0,\"k\":1,\"t\":\"zero id\",\"s\":" + day + "},"
            + "{\"i\":4,\"k\":0,\"t\":\"session without time\",\"s\":" + day + ",\"m\":-1,\"n\":60},"
            + "{\"i\":5,\"k\":0,\"t\":\"session\",\"s\":" + day + ",\"m\":600,\"n\":1},"
            + "{\"i\":6,\"k\":1,\"t\":\"before 2000\",\"s\":5},"
            + "null]}";
        await ctx.Storage.WriteAsync(CalendarStore.DataKey, json, Ct);

        var store = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(Ct);

        Assert.Equal([1, 5], store.Entries.Select(e => e.Id));
        Assert.False(store.ReadOnly);
        CalendarEntry ok = store.Find(1)!;
        Assert.Equal((0b1111111, CalendarLimits.MaxInterval, CalendarLimits.MaxCount, 1439), (ok.WeekdayMask, ok.Interval, ok.Count, ok.StartMinute));
        Assert.Equal(CalendarLimits.MaxTaskMinutes, ok.Duration);
        Assert.True(ok.UntilDay >= ok.StartDay);
        Assert.InRange(ok.Color, 0, CalendarLimits.ColorCount - 1);
        Assert.Equal(CalendarLimits.MinSessionMinutes, store.Find(5)!.Duration);
        await store.AddAsync(Kit.Task("next", Kit.D(2026, 1, 5)), Ct);
        Assert.True(store.Entries[^1].Id >= 6);
    }

    [Fact]
    public async Task A_full_storage_quota_is_logged_and_the_change_stays_in_memory_until_flushed()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        var store = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(Ct);
        await ctx.Storage.WriteAsync("filler", new string('x', (int)(ctx.Storage.QuotaBytes / 2) - 100), Ct);

        _ = await store.AddAsync(Kit.Task("a", Kit.D(2026, 1, 5)), Ct);

        Assert.Single(store.Entries);
        Assert.Contains(ctx.FakeLogger.Entries, e => e.Message.Contains("could not be saved", StringComparison.Ordinal));
        _ = await ctx.Storage.DeleteAsync("filler", Ct);
        await store.FlushAsync(Ct);
        Assert.NotNull(await ctx.Storage.ReadAsync(CalendarStore.DataKey, Ct));
    }

    [Fact]
    public async Task Today_follows_the_zone_of_the_pc()
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync(TimeZoneInfo.Utc);
        ctx.FakeTime.SetUtcNow(new DateTimeOffset(2026, 1, 12, 23, 30, 0, TimeSpan.Zero));
        Assert.Equal(Kit.D(2026, 1, 12), store.Today);

        (FakePluginContext ctx2, CalendarStore store2) = await Kit.NewStoreAsync(Kit.Warsaw);
        ctx2.FakeTime.SetUtcNow(new DateTimeOffset(2026, 1, 12, 23, 30, 0, TimeSpan.Zero));
        Assert.Equal(Kit.D(2026, 1, 13), store2.Today);
    }

    [Fact]
    public void The_streak_counts_started_sessions_back_to_the_first_one_that_was_not()
    {
        HistoryRecord[] records =
        [
            new() { Key = "1:1", Outcome = SessionOutcome.Started, StartUnix = 1 },
            new() { Key = "1:2", Outcome = SessionOutcome.Missed, StartUnix = 2 },
            new() { Key = "1:3", Outcome = SessionOutcome.Started, StartUnix = 3 },
            new() { Key = "1:4", Outcome = SessionOutcome.Started, StartUnix = 4 },
        ];

        Assert.Equal(2, CalendarScheduler.Streak(records));
        Assert.Equal(0, CalendarScheduler.Streak([]));
        Assert.Equal(0, CalendarScheduler.Streak([.. records, new HistoryRecord { Key = "1:5", Outcome = SessionOutcome.Failed, StartUnix = 5 }]));
    }
}
