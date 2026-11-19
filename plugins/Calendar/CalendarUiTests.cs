using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.UI.Sdk;
using Xunit;

namespace CalendarPlanner.Tests;

/// <summary>The store, the state and every contribution wired together on a fake context.</summary>
public sealed class Rig
{
    private Rig(FakePluginContext ctx, CalendarStore store)
    {
        Ctx = ctx;
        Store = store;
        State = new CalendarUiState(ctx.Time);
        Controller = new CalendarController(store, State);
        Page = new CalendarPage(store, State, Controller);
        Widget = new CalendarWidget(store, State, Controller);
        Compact = new CalendarCompactWidget(store, State, Controller);
        Settings = new CalendarSettingsSection(store, State, Controller);
    }

    public FakePluginContext Ctx { get; }

    public CalendarStore Store { get; }

    public CalendarUiState State { get; }

    public CalendarController Controller { get; }

    public CalendarPage Page { get; }

    public CalendarWidget Widget { get; }

    public CalendarCompactWidget Compact { get; }

    public CalendarSettingsSection Settings { get; }

    public static async Task<Rig> CreateAsync(TimeZoneInfo? zone = null)
    {
        (FakePluginContext ctx, CalendarStore store) = await Kit.NewStoreAsync(zone);
        return new Rig(ctx, store);
    }

    public async Task<CalendarEntry> AddAsync(CalendarEntry entry) => (await Store.AddAsync(entry, CancellationToken.None))!;

    public async Task<UiPage> PageAsync() => Assert.IsType<UiPage>(await Ctx.RenderAsync(Page));

    public async Task<UiCard> WidgetAsync() => Assert.IsType<UiCard>(await Ctx.RenderAsync(Widget));

    public async Task<UiCard> CompactAsync() => Assert.IsType<UiCard>(await Ctx.RenderAsync(Compact));

    public async Task<UiNode> SettingsAsync() => await Ctx.RenderAsync(Settings);

    public ValueTask Click(IUiContribution target, string id) => Ctx.SendActionAsync(target, UiAction.Click(id));

    public ValueTask Submit(IUiContribution target, string id, Dictionary<string, string> values) => Ctx.SendActionAsync(target, UiAction.Submit(id, values));

    public async Task<CalendarEntry> AddSessionTodayAsync(string title = "Deep work", int minute = 600, int length = 60) =>
        await AddAsync(Kit.Session(title, Kit.D(2026, 1, 1), minute, length));
}

public class CalendarPageTests
{
    private static readonly int Today = Kit.D(2026, 1, 1);

    private static UiTextField Field(UiNode root, string id) => Kit.Descendants(root).OfType<UiForm>().Single().Fields.OfType<UiTextField>().Single(f => f.Id == id);

    private static UiSelectField Select(UiNode root, string id) => Kit.Descendants(root).OfType<UiForm>().Single().Fields.OfType<UiSelectField>().Single(f => f.Id == id);

    [Fact]
    public async Task The_empty_page_follows_the_standard_layout()
    {
        var rig = await Rig.CreateAsync();

        UiPage page = await rig.PageAsync();

        Assert.Equal("Calendar", page.Title);
        Assert.False(string.IsNullOrWhiteSpace(page.Subtitle));
        Assert.Equal(4, Assert.IsType<UiGrid>(page.Children[0]).Columns);
        Assert.Equal(["Calendar", "Plan"], page.Children.OfType<UiCard>().Select(c => c.Title));
        Assert.Empty(Kit.Descendants(page).OfType<UiGlyph>());
        Assert.DoesNotContain(Kit.Descendants(page).OfType<UiText>(), t => t.Text == "Calendar");
        Assert.Empty(page.Children.OfType<UiCard>().SelectMany(c => c.Children).OfType<UiStatTile>());
        Assert.True(Kit.HasText(page, "Nothing planned yet. Use the Plan card below to add a focus session or a task."));
    }

    [Fact]
    public async Task The_empty_tiles_read_clearly_and_fit()
    {
        var rig = await Rig.CreateAsync();

        UiStatTile[] tiles = Kit.Tiles(await rig.PageAsync());

        Assert.Equal(
            [("Next session", "None", "Plan one below"), ("Today", "0 / 0", "Nothing planned"), ("This week", "0 h", "Planned focus"), ("Streak", "0", "Sessions kept")],
            tiles.Select(t => (t.Label, t.Value, t.Caption)));
        Assert.All(tiles, t => Assert.True(t.Value.Length <= 6));
        Assert.All(tiles, t => Assert.True(t.Label.Split(' ').Length <= 2));
        Assert.All(tiles, t => Assert.NotNull(t.Icon));
    }

    [Fact]
    public async Task The_tiles_show_the_next_session_today_and_the_week()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        _ = await rig.AddAsync(Kit.Task("Buy milk", Today));

        UiStatTile[] tiles = Kit.Tiles(await rig.PageAsync());

        Assert.Equal(("60 min", "Deep work, today 10:00"), (tiles[0].Value, tiles[0].Caption));
        Assert.Equal(("0 / 2", "2 to go"), (tiles[1].Value, tiles[1].Caption));
        Assert.Equal("1 h", tiles[2].Value);
    }

    [Fact]
    public async Task A_session_in_progress_shows_now_and_when_it_ends()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry session = await rig.AddSessionTodayAsync(minute: 8 * 60, length: 180);
        _ = await rig.Store.RecordAsync($"{session.Id}:{Today}", SessionOutcome.Started, Kit.Start.AddHours(-1).ToUnixTimeSeconds(), CancellationToken.None);

        UiStatTile next = Kit.Tiles(await rig.PageAsync())[0];

        Assert.Equal(("Now", UiTone.Success, "Deep work, until 11:00"), (next.Value, next.Tone, next.Caption));
    }

    [Fact]
    public async Task A_done_task_turns_the_today_tile_green()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("a", Today));
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle(CalendarIds.Make(CalendarIds.DonePrefix, task.Id, Today), true));

        UiStatTile tile = Kit.Tiles(await rig.PageAsync())[1];

        Assert.Equal(("1 / 1", "All done", UiTone.Success), (tile.Value, tile.Caption, tile.Tone));
    }

    [Fact]
    public async Task The_streak_tile_counts_kept_sessions()
    {
        var rig = await Rig.CreateAsync();
        long now = Kit.Start.ToUnixTimeSeconds();
        _ = await rig.Store.RecordAsync("1:1", SessionOutcome.Started, now - 100, CancellationToken.None);
        _ = await rig.Store.RecordAsync("1:2", SessionOutcome.Started, now - 50, CancellationToken.None);

        UiStatTile tile = Kit.Tiles(await rig.PageAsync())[3];

        Assert.Equal(("2", UiTone.Success), (tile.Value, tile.Tone));
    }

    [Fact]
    public async Task The_month_grid_has_42_day_buttons_and_marks_today_and_entries()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        _ = await rig.AddAsync(Kit.Task("b", Today));

        UiPage page = await rig.PageAsync();

        UiButton[] days = [.. Kit.Descendants(page).OfType<UiButton>().Where(b => b.Id.StartsWith(CalendarIds.DayPrefix, StringComparison.Ordinal))];
        Assert.Equal(42, days.Length);
        Assert.Equal("[1••]", Kit.Button(page, CalendarIds.DayPrefix + Today).Label);
        Assert.Equal(UiButtonVariant.Secondary, Kit.Button(page, CalendarIds.DayPrefix + Today).Variant);
        Assert.Equal("·29", Kit.Button(page, CalendarIds.DayPrefix + Kit.D(2025, 12, 29)).Label);
        Assert.Equal("2", Kit.Button(page, CalendarIds.DayPrefix + Kit.D(2026, 1, 2)).Label);
        Assert.Equal("January 2026", Assert.Single(page.Children.OfType<UiCard>(), c => c.Title == "Calendar").Subtitle);
    }

    [Fact]
    public async Task More_than_three_entries_on_a_day_show_a_plus()
    {
        var rig = await Rig.CreateAsync();
        for (int i = 0; i < 5; i++)
        {
            _ = await rig.AddAsync(Kit.Task("t" + i, Today));
        }

        Assert.Equal("[1•••+]", Kit.Button(await rig.PageAsync(), CalendarIds.DayPrefix + Today).Label);
    }

    [Fact]
    public async Task Weeks_can_start_on_sunday_and_the_clock_can_be_twelve_hours()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        await rig.Store.SetSettingsAsync(new CalendarSettings { WeekStartsOnSunday = true, Use24Hour = false }, CancellationToken.None);

        UiPage page = await rig.PageAsync();

        Assert.Equal(["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"], Kit.Descendants(page).OfType<UiStack>().First(s => s.Children.Count == 7 && s.Children[0] is UiText).Children.Cast<UiText>().Select(t => t.Text));
        Assert.Equal("Deep work, today 10:00 AM", Kit.Tiles(page)[0].Caption);
    }

    [Fact]
    public async Task Clicking_a_day_selects_it_and_shows_its_agenda()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("Dentist", Kit.D(2026, 1, 8), 14 * 60));

        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 1, 8));
        UiPage page = await rig.PageAsync();

        Assert.True(Kit.HasText(page, "Thursday 8 January"));
        Assert.Contains(Kit.Descendants(page).OfType<UiToggle>(), t => t.Label == "14:00  Dentist");
        Assert.Equal(UiButtonVariant.Secondary, Kit.Button(page, CalendarIds.DayPrefix + Kit.D(2026, 1, 8)).Variant);
        Assert.Equal(UiButtonVariant.Subtle, Kit.Button(page, CalendarIds.DayPrefix + Today).Variant);
    }

    [Fact]
    public async Task A_day_without_entries_says_so()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("x", Kit.D(2026, 1, 9)));

        Assert.True(Kit.HasText(await rig.PageAsync(), "Nothing planned on Thu 1 Jan."));
    }

    [Fact]
    public async Task Clicking_a_day_of_another_month_moves_the_month()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 2, 2));
        UiPage page = await rig.PageAsync();

        Assert.Equal("February 2026", Assert.Single(page.Children.OfType<UiCard>(), c => c.Title == "Calendar").Subtitle);
    }

    [Fact]
    public async Task Previous_next_and_today_navigate_the_month()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Page, CalendarIds.Next);
        await rig.Click(rig.Page, CalendarIds.Next);
        Assert.Equal("March 2026", Assert.Single((await rig.PageAsync()).Children.OfType<UiCard>(), c => c.Title == "Calendar").Subtitle);
        await rig.Click(rig.Page, CalendarIds.Prev);
        Assert.Equal("February 2026", Assert.Single((await rig.PageAsync()).Children.OfType<UiCard>(), c => c.Title == "Calendar").Subtitle);
        await rig.Click(rig.Page, CalendarIds.Today);
        UiPage page = await rig.PageAsync();
        Assert.Equal("January 2026", Assert.Single(page.Children.OfType<UiCard>(), c => c.Title == "Calendar").Subtitle);
        Assert.True(Kit.HasText(page, "Thursday 1 January"));
    }

    [Fact]
    public async Task The_week_view_lists_seven_days_with_their_entries()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        _ = await rig.AddAsync(Kit.Task("Friday task", Kit.D(2026, 1, 2)));
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "week");

        UiPage page = await rig.PageAsync();

        UiCard card = Assert.Single(page.Children.OfType<UiCard>(), c => c.Title == "Calendar");
        Assert.Equal("Mon 29 Dec - Sun 4 Jan", card.Subtitle);
        Assert.Equal(7, Kit.Descendants(page).OfType<UiButton>().Count(b => b.Id.StartsWith(CalendarIds.DayPrefix, StringComparison.Ordinal)));
        Assert.Equal("Thu 1 Jan (today)", Kit.Button(page, CalendarIds.DayPrefix + Today).Label);
        Assert.True(Kit.HasText(page, "10:00-11:00  Deep work"));
        Assert.Contains(Kit.Descendants(page).OfType<UiToggle>(), t => t.Label == "Friday task");
        Assert.Equal(5, Kit.Descendants(page).OfType<UiText>().Count(t => t.Text == "Free"));
    }

    [Fact]
    public async Task The_week_view_moves_by_whole_weeks()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "week");

        await rig.Click(rig.Page, CalendarIds.Next);

        Assert.Equal("Mon 5 Jan - Sun 11 Jan", Assert.Single((await rig.PageAsync()).Children.OfType<UiCard>(), c => c.Title == "Calendar").Subtitle);
    }

    [Fact]
    public async Task The_agenda_groups_the_next_days_and_skips_empty_ones()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        _ = await rig.AddAsync(Kit.Task("Later", Kit.D(2026, 1, 9)));
        _ = await rig.AddAsync(Kit.Task("Too far", Kit.D(2026, 2, 20)));
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "agenda");

        UiPage page = await rig.PageAsync();

        UiCard card = Assert.Single(page.Children.OfType<UiCard>(), c => c.Title == "Calendar");
        Assert.Equal("Thu 1 Jan - Wed 14 Jan", card.Subtitle);
        Assert.Equal(["Thu 1 Jan (today)", "Fri 9 Jan"], Kit.Descendants(card).OfType<UiText>().Where(t => t.Style == UiTextStyle.Subtitle).Select(t => t.Text));
        Assert.DoesNotContain(Kit.Descendants(page).OfType<UiToggle>(), t => t.Label == "Too far");
    }

    [Fact]
    public async Task An_empty_agenda_says_so_and_a_long_one_is_cut()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "agenda");
        Assert.True(Kit.HasText(await rig.PageAsync(), "Nothing planned in these days."));

        _ = await rig.AddAsync(Kit.Daily(Kit.Task("daily", Today, 480)));
        _ = await rig.AddAsync(Kit.Daily(Kit.Task("daily 2", Today, 490)));
        _ = await rig.AddAsync(Kit.Daily(Kit.Task("daily 3", Today, 500)));
        UiPage page = await rig.PageAsync();

        Assert.Equal(CalendarPage.MaxAgendaRows, Kit.Descendants(page).OfType<UiToggle>().Count());
        Assert.True(Kit.HasText(page, "+2 more"));
    }

    [Fact]
    public async Task A_session_row_shows_its_state_and_a_task_row_is_a_toggle()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry session = await rig.AddSessionTodayAsync(minute: 8 * 60);
        _ = await rig.AddAsync(Kit.Task("Water plants", Today, 11 * 60));
        _ = await rig.Store.RecordAsync($"{session.Id}:{Today}", SessionOutcome.Missed, Kit.Start.ToUnixTimeSeconds() - 3600, CancellationToken.None);

        UiPage page = await rig.PageAsync();

        Assert.True(Kit.HasText(page, "08:00-09:00  Deep work"));
        Assert.True(Kit.HasText(page, "Missed"));
        UiToggle toggle = Kit.Descendants(page).OfType<UiToggle>().Single();
        Assert.Equal(("11:00  Water plants", false), (toggle.Label, toggle.IsOn));
    }

    [Fact]
    public async Task Repeating_rows_offer_skip_and_delete_all_and_show_the_rule()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry draft = Kit.Weekly(Kit.Task("Review", Kit.D(2025, 12, 29), 600), 0b0001001);
        draft.Color = 2;
        CalendarEntry weekly = await rig.AddAsync(draft);

        UiPage page = await rig.PageAsync();

        Assert.True(Kit.HasButton(page, CalendarIds.Make(CalendarIds.SkipPrefix, weekly.Id, Today)));
        Assert.Equal("Delete all", Kit.Descendants(page).OfType<UiButton>().Single(b => b.Id == CalendarIds.Make(CalendarIds.DeletePrefix, weekly.Id, Today)).Label);
        Assert.Contains(Kit.Descendants(page).OfType<UiToggle>(), t => t.Description == "Every Mon, Thu - Green");
    }

    [Fact]
    public async Task Delete_needs_a_second_click_and_the_first_one_times_out()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("a", Today));
        string id = CalendarIds.Make(CalendarIds.DeletePrefix, task.Id, Today);

        await rig.Click(rig.Page, id);
        UiButton armed = Kit.Button(await rig.PageAsync(), id);
        Assert.Equal(("Click again to delete", UiButtonVariant.Warning), (armed.Label, armed.Variant));
        Assert.Single(rig.Store.Entries);

        rig.Ctx.FakeTime.Advance(CalendarUiState.ConfirmWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("Delete", Kit.Button(await rig.PageAsync(), id).Label);
        await rig.Click(rig.Page, id);
        Assert.Single(rig.Store.Entries);

        await rig.Click(rig.Page, id);
        Assert.Empty(rig.Store.Entries);
        Assert.True(Kit.HasText(await rig.PageAsync(), "Deleted \"a\"."));
    }

    [Fact]
    public async Task Arming_one_button_disarms_another()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Task("a", Today));
        CalendarEntry b = await rig.AddAsync(Kit.Task("b", Today));

        await rig.Click(rig.Page, CalendarIds.Make(CalendarIds.DeletePrefix, a.Id, Today));
        await rig.Click(rig.Page, CalendarIds.Make(CalendarIds.DeletePrefix, b.Id, Today));

        Assert.Equal(2, rig.Store.Entries.Count);
        await rig.Click(rig.Page, CalendarIds.Make(CalendarIds.DeletePrefix, b.Id, Today));
        Assert.Equal([a.Id], rig.Store.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Skip_needs_two_clicks_and_hides_one_occurrence()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry daily = await rig.AddAsync(Kit.Daily(Kit.Task("daily", Today)));
        string id = CalendarIds.Make(CalendarIds.SkipPrefix, daily.Id, Today);

        await rig.Click(rig.Page, id);
        Assert.Equal("Click again to skip", Kit.Button(await rig.PageAsync(), id).Label);
        await rig.Click(rig.Page, id);

        Assert.Equal([Today], rig.Store.Find(daily.Id)!.Skipped);
        Assert.True(Kit.HasText(await rig.PageAsync(), "Skipped \"daily\" on Thu 1 Jan."));
        Assert.True(Kit.HasText(await rig.PageAsync(), "Nothing planned on Thu 1 Jan."));
    }

    [Fact]
    public async Task Ticking_a_task_on_the_page_is_stored()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("a", Today));

        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle(CalendarIds.Make(CalendarIds.DonePrefix, task.Id, Today), true));
        Assert.Single(rig.Store.Find(task.Id)!.DoneDays);
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle(CalendarIds.Make(CalendarIds.DonePrefix, task.Id, Today), false));

        Assert.Empty(rig.Store.Find(task.Id)!.DoneDays);
    }

    [Fact]
    public async Task Malformed_ids_and_values_are_ignored()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("a", Today));

        foreach (string id in new[] { "cal-done-x-y", "cal-done-1", "cal-done--1-2", "cal-del-", "cal-del-1", "cal-del-x-y", "cal-edit-1-2", "cal-day-99999999999", "cal-day-5", "cal-view-year", "unknown", string.Empty })
        {
            await rig.Click(rig.Page, id);
        }

        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle("cal-done-1-" + Today, true) with { Value = "maybe" });
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Submit("cal-other", new Dictionary<string, string>()));

        Assert.Single(rig.Store.Entries);
        Assert.Empty(rig.Store.Find(task.Id)!.DoneDays);
        Assert.NotEmpty((await rig.PageAsync()).Children);
    }

    [Fact]
    public async Task Submitting_the_form_adds_an_entry_selects_its_day_and_confirms()
    {
        var rig = await Rig.CreateAsync();

        await rig.Submit(rig.Page, CalendarForm.FormId, Kit.Form(date: "2026-01-15", time: "13:30", length: "90", title: "Report"));
        UiPage page = await rig.PageAsync();

        CalendarEntry entry = Assert.Single(rig.Store.Entries);
        Assert.Equal(("Report", Kit.D(2026, 1, 15), 810, 90), (entry.Title, entry.StartDay, entry.StartMinute, entry.Duration));
        Assert.True(Kit.HasText(page, "Added \"Report\" on Thu 15 Jan."));
        Assert.True(Kit.HasText(page, "Thursday 15 January"));
        Assert.True(Kit.HasText(page, "13:30-15:00  Report"));
    }

    [Fact]
    public async Task A_refused_form_shows_the_reason_and_keeps_what_was_typed()
    {
        var rig = await Rig.CreateAsync();

        await rig.Submit(rig.Page, CalendarForm.FormId, Kit.Form(title: "Typed title", time: "99:99"));
        UiPage page = await rig.PageAsync();

        Assert.Empty(rig.Store.Entries);
        Assert.True(Kit.HasText(page, "Enter the time as hh:mm, for example 09:30."));
        Assert.Equal("Typed title", Field(page, CalendarForm.TitleId).Value);
        Assert.Equal("99:99", Field(page, CalendarForm.TimeId).Value);
    }

    [Fact]
    public async Task The_message_can_be_dismissed()
    {
        var rig = await Rig.CreateAsync();
        await rig.Submit(rig.Page, CalendarForm.FormId, Kit.Form(time: "x"));
        Assert.True(Kit.HasButton(await rig.PageAsync(), CalendarIds.Dismiss));

        await rig.Click(rig.Page, CalendarIds.Dismiss);

        Assert.False(Kit.HasButton(await rig.PageAsync(), CalendarIds.Dismiss));
    }

    [Fact]
    public async Task Editing_an_entry_fills_the_form_and_saving_changes_it()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry session = await rig.AddAsync(Kit.Session("Morning", Kit.D(2026, 1, 2), 540, 60));
        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 1, 2));

        await rig.Click(rig.Page, CalendarIds.Make(CalendarIds.EditPrefix, session.Id, session.StartDay, session.StartDay));
        UiPage editing = await rig.PageAsync();

        Assert.Equal(["Calendar", "Edit entry"], editing.Children.OfType<UiCard>().Select(c => c.Title));
        Assert.Equal(("Morning", "09:00", "2026-01-02"), (Field(editing, CalendarForm.TitleId).Value, Field(editing, CalendarForm.TimeId).Value, Field(editing, CalendarForm.DateId).Value));
        Assert.Equal("Save changes", Kit.Descendants(editing).OfType<UiForm>().Single().SubmitLabel);
        Assert.True(Kit.HasButton(editing, CalendarIds.CancelEdit));

        await rig.Submit(rig.Page, CalendarForm.FormId, Kit.Form(title: "Renamed", date: "2026-01-02", time: "09:30"));

        Assert.Equal(("Renamed", 570), (rig.Store.Find(session.Id)!.Title, rig.Store.Find(session.Id)!.StartMinute));
        Assert.Single(rig.Store.Entries);
        Assert.Equal("Plan", (await rig.PageAsync()).Children.OfType<UiCard>().Last().Title);
    }

    [Fact]
    public async Task Cancelling_an_edit_returns_to_a_fresh_form()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("a", Today));
        await rig.Click(rig.Page, CalendarIds.Make(CalendarIds.EditPrefix, task.Id, Today, Today));

        await rig.Click(rig.Page, CalendarIds.CancelEdit);

        Assert.Equal("Plan", (await rig.PageAsync()).Children.OfType<UiCard>().Last().Title);
    }

    [Fact]
    public async Task Editing_a_repeating_entry_offers_the_scope_and_one_occurrence_can_be_changed()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry weekly = await rig.AddAsync(Kit.Weekly(Kit.Session("Weekly", Kit.D(2026, 1, 5), 540, 60), 1));
        int second = Kit.D(2026, 1, 12);
        await rig.Click(rig.Page, CalendarIds.DayPrefix + second);

        await rig.Click(rig.Page, CalendarIds.Make(CalendarIds.EditPrefix, weekly.Id, second, second));
        UiSelectField scope = Select(await rig.PageAsync(), CalendarForm.ScopeId);
        Assert.Equal("occurrence", scope.SelectedValue);

        await rig.Submit(rig.Page, CalendarForm.FormId, Kit.Form(title: "Only this one", date: "2026-01-12", time: "11:00", length: "45", repeat: "weekly", weekdays: [0], scope: "occurrence"));

        CalendarEntry stored = rig.Store.Find(weekly.Id)!;
        Assert.Equal("Weekly", stored.Title);
        Assert.Equal("Only this one", Assert.Single(stored.Overrides).Title);
        Assert.True(Kit.HasText(await rig.PageAsync(), "11:00-11:45  Only this one"));
    }

    [Fact]
    public async Task The_form_has_the_documented_fields_and_valid_ids()
    {
        var rig = await Rig.CreateAsync();

        UiForm form = Kit.Descendants(await rig.PageAsync()).OfType<UiForm>().Single();

        Assert.Equal(CalendarForm.FormId, form.Id);
        List<string> expected = [CalendarForm.KindId, CalendarForm.TitleId, CalendarForm.DateId, CalendarForm.TimeId, CalendarForm.LengthId, CalendarForm.RepeatId, CalendarForm.EveryId];
        expected.AddRange(Enumerable.Range(0, 7).Select(i => CalendarForm.WeekdayPrefix + i));
        expected.AddRange([CalendarForm.EndId, CalendarForm.UntilId, CalendarForm.CountId, CalendarForm.ColorId]);
        Assert.Equal(expected, form.Fields.Select(f => f.Id));
        Assert.DoesNotContain(Kit.Descendants(await rig.PageAsync()).OfType<UiButton>(), b => b.Variant == UiButtonVariant.Primary);
    }

    [Fact]
    public async Task The_plan_card_explains_auto_start_and_changes_its_text_when_it_is_off()
    {
        var rig = await Rig.CreateAsync();
        Assert.True(Kit.HasTextContaining(await rig.PageAsync(), "starts by itself when its time comes"));

        await rig.Store.SetSettingsAsync(new CalendarSettings { AutoStart = false }, CancellationToken.None);

        Assert.True(Kit.HasTextContaining(await rig.PageAsync(), "Auto-start is off"));
    }

    [Fact]
    public async Task A_calendar_that_recovered_from_corrupt_data_says_so()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        await ctx.Storage.WriteAsync(CalendarStore.DataKey, "garbage", CancellationToken.None);
        var store = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);
        var page = new CalendarPage(store, new CalendarUiState(ctx.Time), new CalendarController(store, new CalendarUiState(ctx.Time)));

        UiNode tree = await ctx.RenderAsync(page);

        Assert.True(Kit.HasTextContaining(tree, "could not be read"));
    }

    [Fact]
    public async Task A_busy_calendar_still_renders_within_the_node_limits_in_every_view()
    {
        var rig = await Rig.CreateAsync();
        for (int i = 0; i < 120; i++)
        {
            CalendarEntry e = Kit.Daily(Kit.Task("task " + i, Today, (i % 24) * 60), 1 + (i % 3));
            e.Color = i % 6;
            _ = await rig.AddAsync(e);
        }

        UiPage month = await rig.PageAsync();
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "week");
        UiPage week = await rig.PageAsync();
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "agenda");
        UiPage agenda = await rig.PageAsync();

        Assert.NotEmpty(month.Children);
        Assert.NotEmpty(week.Children);
        Assert.NotEmpty(agenda.Children);
    }

    [Fact]
    public async Task Rendering_requests_follow_changes()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.PageAsync();
        int before = rig.Ctx.RenderRequests;

        _ = await rig.AddAsync(Kit.Task("a", Today));
        await rig.Click(rig.Page, CalendarIds.Next);

        Assert.True(rig.Ctx.RenderRequests >= before + 2);
    }
}
