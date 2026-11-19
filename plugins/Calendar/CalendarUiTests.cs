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

    private static UiCard CalendarCard(UiPage page) => page.Children.OfType<UiCard>().First();

    private static UiCard PlanCard(UiPage page) => page.Children.OfType<UiCard>().Last();

    private static UiMonthGrid Month(UiNode root) => Kit.Descendants(root).OfType<UiMonthGrid>().Single();

    [Fact]
    public async Task The_empty_page_follows_the_standard_layout()
    {
        var rig = await Rig.CreateAsync();

        UiPage page = await rig.PageAsync();

        Assert.Equal("Calendar", page.Title);
        Assert.False(string.IsNullOrWhiteSpace(page.Subtitle));
        Assert.Equal(4, Assert.IsType<UiGrid>(page.Children[0]).Columns);
        Assert.Equal(["January 2026", "Plan"], page.Children.OfType<UiCard>().Select(c => c.Title));
        Assert.Empty(Kit.Descendants(page).OfType<UiGlyph>());
        Assert.DoesNotContain(Kit.Descendants(page).OfType<UiText>(), t => t.Text == "Calendar");
        Assert.Empty(page.Children.OfType<UiCard>().SelectMany(c => c.Children).OfType<UiStatTile>());
        Assert.Empty(Kit.Descendants(page).OfType<UiCard>().Skip(2));
    }

    [Fact]
    public async Task An_empty_calendar_invites_planning_with_a_primary_button()
    {
        var rig = await Rig.CreateAsync();

        UiPage page = await rig.PageAsync();

        Assert.True(Kit.HasText(page, "Your calendar is empty. Plan a focus session and it starts by itself when its time comes."));
        UiButton invite = Kit.Button(page, CalendarIds.EmptyPlan);
        Assert.Equal(("Plan a session", UiButtonVariant.Primary), (invite.Label, invite.Variant));
        Assert.True(Kit.HasText(page, "Nothing planned"));
    }

    [Fact]
    public async Task A_free_day_in_a_busy_calendar_gets_a_quieter_invitation()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("x", Kit.D(2026, 1, 9)));

        UiPage page = await rig.PageAsync();

        Assert.True(Kit.HasText(page, "A free day. Plan a focus session for it, or pick another day."));
        Assert.Equal(UiButtonVariant.Secondary, Kit.Button(page, CalendarIds.EmptyPlan).Variant);
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
        Assert.Equal([UiIcon.Clock, UiIcon.Checkmark, UiIcon.Timer, UiIcon.Flame], tiles.Select(t => t.Icon!.Value));
        Assert.All(tiles, t => Assert.Equal(UiTone.Neutral, t.Tone));
    }

    [Fact]
    public async Task The_tiles_show_the_next_session_today_and_the_week()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        _ = await rig.AddAsync(Kit.Task("Buy milk", Today));

        UiStatTile[] tiles = Kit.Tiles(await rig.PageAsync());

        Assert.Equal(("60 min", "Deep work, today 10:00"), (tiles[0].Value, tiles[0].Caption));
        Assert.Equal(("0 / 2", "2 to go", UiTone.Accent), (tiles[1].Value, tiles[1].Caption, tiles[1].Tone));
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
    public async Task The_view_switch_and_the_navigation_are_separate_and_the_current_view_is_filled()
    {
        var rig = await Rig.CreateAsync();

        UiStack toolbar = Assert.IsType<UiStack>(CalendarCard(await rig.PageAsync()).Children[0]);

        Assert.Equal((UiOrientation.Horizontal, UiGap.Large, 2), (toolbar.Orientation, toolbar.Gap, toolbar.Children.Count));
        UiButton[] views = [.. Assert.IsType<UiStack>(toolbar.Children[0]).Children.Cast<UiButton>()];
        UiButton[] navigation = [.. Assert.IsType<UiStack>(toolbar.Children[1]).Children.Cast<UiButton>()];
        Assert.Equal([CalendarIds.ViewPrefix + "month", CalendarIds.ViewPrefix + "week", CalendarIds.ViewPrefix + "agenda"], views.Select(b => b.Id));
        Assert.Equal(["Month", "Week", "Agenda"], views.Select(b => b.Label));
        Assert.Equal([UiButtonVariant.Primary, UiButtonVariant.Subtle, UiButtonVariant.Subtle], views.Select(b => b.Variant));
        Assert.Equal([CalendarIds.Prev, CalendarIds.Today, CalendarIds.Next], navigation.Select(b => b.Id));
        Assert.Equal(["‹", "Today", "›"], navigation.Select(b => b.Label));
        Assert.All(navigation, b => Assert.Equal(UiButtonVariant.Secondary, b.Variant));
    }

    [Theory]
    [InlineData("week", 1)]
    [InlineData("agenda", 2)]
    public async Task Choosing_another_view_fills_that_button(string view, int index)
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + view);

        UiStack toolbar = Assert.IsType<UiStack>(CalendarCard(await rig.PageAsync()).Children[0]);

        UiButton[] views = [.. Assert.IsType<UiStack>(toolbar.Children[0]).Children.Cast<UiButton>()];
        Assert.Equal(index, Array.FindIndex(views, b => b.Variant == UiButtonVariant.Primary));
        Assert.Single(views, b => b.Variant == UiButtonVariant.Primary);
    }

    [Fact]
    public async Task The_month_is_one_aligned_grid_with_today_selected_and_entries_marked()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        _ = await rig.AddAsync(Kit.Task("b", Today));

        UiPage page = await rig.PageAsync();

        UiMonthGrid month = Month(page);
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], month.WeekdayLabels);
        Assert.Equal(42, month.Cells.Count);
        Assert.False(month.IsCompact);
        Assert.Null(month.Title);
        Assert.Null(month.PreviousId);
        UiMonthCell today = month.Cells.Single(c => c.IsToday);
        Assert.Equal((CalendarIds.DayPrefix + Today, 1, 2, true, false), (today.Id, today.Day, today.Markers, today.IsSelected, today.IsOutside));
        Assert.Equal("Thursday 1 January, 2 entries", today.Description);
        UiMonthCell december = month.Cells[0];
        Assert.Equal((CalendarIds.DayPrefix + Kit.D(2025, 12, 29), 29, 0, true), (december.Id, december.Day, december.Markers, december.IsOutside));
        Assert.Equal(Kit.D(2026, 1, 2), 1 + Kit.D(2026, 1, 1));
        Assert.Equal(("January 2026", "Pick a day to see its plan"), (CalendarCard(page).Title, CalendarCard(page).Subtitle));
        Assert.Equal(month.Cells.Count, month.Cells.Select(c => c.Id).Distinct().Count());
        Assert.Equal(31, month.Cells.Count(c => !c.IsOutside));
    }

    [Fact]
    public async Task A_day_with_one_entry_says_entry_in_its_description()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("b", Kit.D(2026, 1, 5)));

        UiMonthCell cell = Month(await rig.PageAsync()).Cells.Single(c => c.Id == CalendarIds.DayPrefix + Kit.D(2026, 1, 5));

        Assert.Equal(("Monday 5 January, 1 entry", 1), (cell.Description, cell.Markers));
    }

    [Fact]
    public async Task More_than_three_entries_on_a_day_report_the_real_count()
    {
        var rig = await Rig.CreateAsync();
        for (int i = 0; i < 5; i++)
        {
            _ = await rig.AddAsync(Kit.Task("t" + i, Today));
        }

        Assert.Equal(5, Month(await rig.PageAsync()).Cells.Single(c => c.IsToday).Markers);
    }

    [Fact]
    public async Task Weeks_can_start_on_sunday_and_the_clock_can_be_twelve_hours()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        await rig.Store.SetSettingsAsync(new CalendarSettings { WeekStartsOnSunday = true, Use24Hour = false }, CancellationToken.None);

        UiPage page = await rig.PageAsync();

        Assert.Equal(["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"], Month(page).WeekdayLabels);
        Assert.Equal(28, Month(page).Cells[0].Day);
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
        UiMonthGrid month = Month(page);
        Assert.Equal([CalendarIds.DayPrefix + Kit.D(2026, 1, 8)], month.Cells.Where(c => c.IsSelected).Select(c => c.Id));
        Assert.True(month.Cells.Single(c => c.IsToday).Id == CalendarIds.DayPrefix + Today);
    }

    [Fact]
    public async Task Clicking_a_day_of_another_month_moves_the_month()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 2, 2));
        UiPage page = await rig.PageAsync();

        Assert.Equal("February 2026", CalendarCard(page).Title);
        Assert.Equal(["Monday 2 February"], Kit.Descendants(page).OfType<UiText>().Where(t => t.Style == UiTextStyle.Subtitle).Select(t => t.Text));
    }

    [Fact]
    public async Task Previous_next_and_today_navigate_the_month()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Page, CalendarIds.Next);
        await rig.Click(rig.Page, CalendarIds.Next);
        Assert.Equal("March 2026", CalendarCard(await rig.PageAsync()).Title);
        await rig.Click(rig.Page, CalendarIds.Prev);
        Assert.Equal("February 2026", CalendarCard(await rig.PageAsync()).Title);
        await rig.Click(rig.Page, CalendarIds.Today);
        UiPage page = await rig.PageAsync();
        Assert.Equal("January 2026", CalendarCard(page).Title);
        Assert.True(Kit.HasText(page, "Thursday 1 January (today)"));
    }

    [Fact]
    public async Task The_week_view_has_a_week_strip_and_groups_the_entries_by_day()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        _ = await rig.AddAsync(Kit.Task("Friday task", Kit.D(2026, 1, 2)));
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "week");

        UiPage page = await rig.PageAsync();

        UiCard card = CalendarCard(page);
        Assert.Equal(("Mon 29 Dec - Sun 4 Jan", "Seven days, grouped by day"), (card.Title, card.Subtitle));
        UiMonthGrid strip = Month(page);
        Assert.Equal(7, strip.Cells.Count);
        Assert.Equal(Enumerable.Range(Kit.D(2025, 12, 29), 7).Select(d => CalendarIds.DayPrefix + d), strip.Cells.Select(c => c.Id));
        Assert.All(strip.Cells, c => Assert.False(c.IsOutside));
        Assert.Equal(1, strip.Cells.Single(c => c.IsToday).Markers);
        Assert.Equal(1, strip.Cells.Single(c => c.Id == CalendarIds.DayPrefix + Kit.D(2026, 1, 2)).Markers);
        UiText[] headers = [.. Kit.Descendants(card).OfType<UiText>().Where(t => t.Style == UiTextStyle.Subtitle)];
        Assert.Equal(7, headers.Length);
        Assert.Equal(("Thursday 1 January", UiTone.Accent), (headers[3].Text, headers[3].Tone));
        Assert.True(Kit.HasText(page, "10:00-11:00  Deep work"));
        Assert.Contains(Kit.Descendants(page).OfType<UiToggle>(), t => t.Label == "Friday task");
        Assert.Equal(5, Kit.Descendants(page).OfType<UiText>().Count(t => t.Text == "Free"));
        Assert.True(Kit.HasText(page, "Today, 1 entry"));
    }

    [Fact]
    public async Task The_week_view_moves_by_whole_weeks()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "week");

        await rig.Click(rig.Page, CalendarIds.Next);

        Assert.Equal("Mon 5 Jan - Sun 11 Jan", CalendarCard(await rig.PageAsync()).Title);
    }

    [Fact]
    public async Task Clicking_a_day_of_the_week_strip_selects_it_without_leaving_the_week()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "week");

        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 1, 3));
        UiPage page = await rig.PageAsync();

        Assert.Equal("Mon 29 Dec - Sun 4 Jan", CalendarCard(page).Title);
        Assert.Equal([CalendarIds.DayPrefix + Kit.D(2026, 1, 3)], Month(page).Cells.Where(c => c.IsSelected).Select(c => c.Id));
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

        UiCard card = CalendarCard(page);
        Assert.Equal(("Next 14 days", "Thu 1 Jan - Wed 14 Jan"), (card.Title, card.Subtitle));
        Assert.Equal(["Thursday 1 January", "Friday 9 January"], Kit.Descendants(card).OfType<UiText>().Where(t => t.Style == UiTextStyle.Subtitle).Select(t => t.Text));
        Assert.DoesNotContain(Kit.Descendants(page).OfType<UiToggle>(), t => t.Label == "Too far");
        Assert.Empty(Kit.Descendants(card).OfType<UiMonthGrid>());
    }

    [Fact]
    public async Task An_empty_agenda_invites_planning_and_a_long_one_is_cut()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.ViewPrefix + "agenda");
        UiPage empty = await rig.PageAsync();
        Assert.True(Kit.HasText(empty, "Nothing planned in these days. Plan a focus session and it shows up here."));
        Assert.Equal(UiButtonVariant.Primary, Kit.Button(empty, CalendarIds.EmptyPlan).Variant);

        _ = await rig.AddAsync(Kit.Daily(Kit.Task("daily", Today, 480)));
        _ = await rig.AddAsync(Kit.Daily(Kit.Task("daily 2", Today, 490)));
        _ = await rig.AddAsync(Kit.Daily(Kit.Task("daily 3", Today, 500)));
        UiPage page = await rig.PageAsync();

        Assert.Equal(CalendarPage.MaxAgendaRows, Kit.Descendants(page).OfType<UiToggle>().Count());
        Assert.True(Kit.HasText(page, "+2 more"));
        Assert.False(Kit.HasButton(page, CalendarIds.EmptyPlan));
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
        Assert.True(Kit.HasText(await rig.PageAsync(), "A free day. Plan a focus session for it, or pick another day."));
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

        foreach (string id in new[] { "cal-done-x-y", "cal-done-1", "cal-done--1-2", "cal-del-", "cal-del-1", "cal-del-x-y", "cal-edit-1-2", "cal-day-99999999999", "cal-day-5", "cal-view-year", "cal-repeat-yearly", "cal-repeat-", "unknown", string.Empty })
        {
            await rig.Click(rig.Page, id);
        }

        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle("cal-done-1-" + Today, true) with { Value = "maybe" });
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Submit("cal-other", new Dictionary<string, string>()));

        Assert.Single(rig.Store.Entries);
        Assert.Empty(rig.Store.Find(task.Id)!.DoneDays);
        Assert.Null(rig.State.Draft);
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
        Assert.Equal("Plan", PlanCard(page).Title);
        Assert.Empty(Kit.Descendants(page).OfType<UiForm>());
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

        Assert.Equal(["January 2026", "Edit entry"], editing.Children.OfType<UiCard>().Select(c => c.Title));
        Assert.Equal(("Morning", "09:00", "2026-01-02"), (Field(editing, CalendarForm.TitleId).Value, Field(editing, CalendarForm.TimeId).Value, Field(editing, CalendarForm.DateId).Value));
        Assert.Equal("Save", Kit.Descendants(editing).OfType<UiForm>().Single().SubmitLabel);
        Assert.True(Kit.HasButton(editing, CalendarIds.CancelEdit));
        Assert.False(Kit.HasButton(editing, CalendarIds.PlanTask));   // the kind of an existing entry is not switched with buttons

        await rig.Submit(rig.Page, CalendarForm.FormId, Kit.Form(title: "Renamed", date: "2026-01-02", time: "09:30"));

        Assert.Equal(("Renamed", 570), (rig.Store.Find(session.Id)!.Title, rig.Store.Find(session.Id)!.StartMinute));
        Assert.Single(rig.Store.Entries);
        Assert.Equal("Plan", PlanCard(await rig.PageAsync()).Title);
    }

    [Fact]
    public async Task Cancelling_closes_the_form()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("a", Today));
        await rig.Click(rig.Page, CalendarIds.Make(CalendarIds.EditPrefix, task.Id, Today, Today));
        Assert.NotEmpty(Kit.Descendants(await rig.PageAsync()).OfType<UiForm>());

        await rig.Click(rig.Page, CalendarIds.CancelEdit);

        UiPage page = await rig.PageAsync();
        Assert.Equal("Plan", PlanCard(page).Title);
        Assert.Empty(Kit.Descendants(page).OfType<UiForm>());
        Assert.True(Kit.HasButton(page, CalendarIds.PlanSession));
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
    public async Task The_closed_plan_card_offers_two_buttons_and_no_form()
    {
        var rig = await Rig.CreateAsync();

        UiCard plan = PlanCard(await rig.PageAsync());

        Assert.Equal(("Plan", "A focus session or a task"), (plan.Title, plan.Subtitle));
        Assert.Empty(Kit.Descendants(plan).OfType<UiForm>());
        UiButton[] buttons = [.. Kit.Descendants(plan).OfType<UiButton>()];
        Assert.Equal([(CalendarIds.PlanSession, "Plan a session", UiButtonVariant.Primary), (CalendarIds.PlanTask, "Add a task", UiButtonVariant.Secondary)], buttons.Select(b => (b.Id, b.Label, b.Variant)));
        Assert.True(Kit.HasTextContaining(plan, "starts by itself when its time comes"));
    }

    [Fact]
    public async Task Plan_a_session_opens_a_short_form_for_the_selected_day()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 1, 8));

        await rig.Click(rig.Page, CalendarIds.PlanSession);
        UiPage page = await rig.PageAsync();

        UiCard plan = PlanCard(page);
        Assert.Equal(("New session", "Thu 8 Jan"), (plan.Title, plan.Subtitle));
        UiForm form = Kit.Descendants(plan).OfType<UiForm>().Single();
        Assert.Equal("Save", form.SubmitLabel);
        Assert.Equal([CalendarForm.TitleId, CalendarForm.DateId, CalendarForm.TimeId, CalendarForm.LengthId, CalendarForm.ColorId], form.Fields.Select(f => f.Id));
        Assert.Equal("2026-01-08", Field(page, CalendarForm.DateId).Value);
        Assert.Equal(("Start time (hh:mm)", "10:00"), (Field(page, CalendarForm.TimeId).Label, Field(page, CalendarForm.TimeId).Value));
        UiNumberField length = form.Fields.OfType<UiNumberField>().Single();
        Assert.Equal(("Length (minutes)", 120d, 15d, 720d), (length.Label, length.Value, length.Minimum, length.Maximum));
        Assert.Equal(1, plan.Children.OfType<UiForm>().Count());
        Assert.True(Kit.HasButton(plan, CalendarIds.CancelEdit));
    }

    [Fact]
    public async Task The_kind_buttons_switch_between_a_session_and_a_task_form()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.PlanSession);
        Assert.Equal(UiButtonVariant.Secondary, Kit.Button(await rig.PageAsync(), CalendarIds.PlanSession).Variant);
        Assert.Equal(UiButtonVariant.Subtle, Kit.Button(await rig.PageAsync(), CalendarIds.PlanTask).Variant);

        await rig.Click(rig.Page, CalendarIds.PlanTask);
        UiPage page = await rig.PageAsync();

        Assert.Equal("New task", PlanCard(page).Title);
        Assert.Equal(UiButtonVariant.Subtle, Kit.Button(page, CalendarIds.PlanSession).Variant);
        Assert.Equal(UiButtonVariant.Secondary, Kit.Button(page, CalendarIds.PlanTask).Variant);
        Assert.Equal("Time (hh:mm, optional)", Field(page, CalendarForm.TimeId).Label);
        UiNumberField duration = Kit.Descendants(page).OfType<UiForm>().Single().Fields.OfType<UiNumberField>().Single();
        Assert.Equal(("Duration (minutes, optional)", 0d, 1440d), (duration.Label, duration.Minimum, duration.Maximum));
        Assert.False(Kit.HasTextContaining(PlanCard(page), "starts by itself"));   // only sessions start by themselves
    }

    [Fact]
    public async Task The_repeat_buttons_add_only_the_fields_that_repeating_needs()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.PlanSession);
        Assert.Equal(UiButtonVariant.Secondary, Kit.Button(await rig.PageAsync(), CalendarIds.RepeatPrefix + "once").Variant);

        await rig.Click(rig.Page, CalendarIds.RepeatPrefix + "daily");
        UiPage daily = await rig.PageAsync();
        Assert.Equal(
            [CalendarForm.TitleId, CalendarForm.DateId, CalendarForm.TimeId, CalendarForm.LengthId, CalendarForm.EveryId, CalendarForm.EndId, CalendarForm.UntilId, CalendarForm.CountId, CalendarForm.ColorId],
            Kit.Descendants(daily).OfType<UiForm>().Single().Fields.Select(f => f.Id));
        Assert.Equal(UiButtonVariant.Secondary, Kit.Button(daily, CalendarIds.RepeatPrefix + "daily").Variant);
        Assert.Equal(UiButtonVariant.Subtle, Kit.Button(daily, CalendarIds.RepeatPrefix + "once").Variant);

        await rig.Click(rig.Page, CalendarIds.RepeatPrefix + "weekly");
        UiForm weekly = Kit.Descendants(await rig.PageAsync()).OfType<UiForm>().Single();
        List<string> expected = [CalendarForm.TitleId, CalendarForm.DateId, CalendarForm.TimeId, CalendarForm.LengthId, CalendarForm.EveryId];
        expected.AddRange(Enumerable.Range(0, 7).Select(i => CalendarForm.WeekdayPrefix + i));
        expected.AddRange([CalendarForm.EndId, CalendarForm.UntilId, CalendarForm.CountId, CalendarForm.ColorId]);
        Assert.Equal(expected, weekly.Fields.Select(f => f.Id));
        Assert.Equal(["wd3"], weekly.Fields.OfType<UiCheckboxField>().Where(c => c.IsChecked).Select(c => c.Id[^3..]));   // the selected day is a Thursday

        await rig.Click(rig.Page, CalendarIds.RepeatPrefix + "once");
        Assert.Equal(5, Kit.Descendants(await rig.PageAsync()).OfType<UiForm>().Single().Fields.Count);
    }

    [Fact]
    public async Task Choosing_the_kind_keeps_the_repeat_rule_and_a_form_without_them_uses_the_draft()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.PlanSession);
        await rig.Click(rig.Page, CalendarIds.RepeatPrefix + "daily");

        await rig.Click(rig.Page, CalendarIds.PlanTask);
        Assert.Equal(RepeatKind.Daily, rig.State.Draft!.Repeat);

        // The submitted form carries no kind and no repeat field: the draft decides.
        Dictionary<string, string> values = Kit.Form(kind: "task", title: "Stretch", date: "2026-01-02", time: "08:00", length: "10", repeat: "daily", every: "2");
        _ = values.Remove(CalendarForm.KindId);
        _ = values.Remove(CalendarForm.RepeatId);
        await rig.Submit(rig.Page, CalendarForm.FormId, values);

        CalendarEntry entry = Assert.Single(rig.Store.Entries);
        Assert.Equal((EntryKind.Task, RepeatKind.Daily, 2), (entry.Kind, entry.Repeat, entry.Interval));
    }

    [Fact]
    public async Task The_empty_day_button_opens_a_session_form_on_that_day()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 1, 20));

        await rig.Click(rig.Page, CalendarIds.EmptyPlan);
        UiPage page = await rig.PageAsync();

        Assert.Equal("New session", PlanCard(page).Title);
        Assert.Equal("2026-01-20", Field(page, CalendarForm.DateId).Value);
    }

    [Fact]
    public async Task A_new_entry_form_can_be_opened_again_and_keeps_its_day()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 1, 20));
        await rig.Click(rig.Page, CalendarIds.PlanSession);
        await rig.Click(rig.Page, CalendarIds.DayPrefix + Kit.D(2026, 1, 21));

        await rig.Click(rig.Page, CalendarIds.PlanTask);

        Assert.Equal(Kit.D(2026, 1, 20), rig.State.Draft!.Day);   // switching the kind does not move the date the form was opened for
        Assert.Equal(EntryKind.Task, rig.State.Draft.Kind);
    }

    [Fact]
    public async Task Saving_a_planned_session_through_the_buttons_and_the_form_adds_it()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Page, CalendarIds.PlanSession);

        Dictionary<string, string> values = Kit.Form(title: "Study", date: "2026-01-02", time: "10:00", length: "45");
        _ = values.Remove(CalendarForm.KindId);
        _ = values.Remove(CalendarForm.RepeatId);
        await rig.Submit(rig.Page, CalendarForm.FormId, values);

        CalendarEntry entry = Assert.Single(rig.Store.Entries);
        Assert.Equal((EntryKind.Session, "Study", 45), (entry.Kind, entry.Title, entry.Duration));
        Assert.Null(rig.State.Draft);
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
    public async Task At_most_one_primary_button_per_area_apart_from_the_view_switch()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("a", Today));

        UiPage page = await rig.PageAsync();

        UiButton[] primary = [.. Kit.Descendants(page).OfType<UiButton>().Where(b => b.Variant == UiButtonVariant.Primary)];
        Assert.Equal([CalendarIds.ViewPrefix + "month", CalendarIds.PlanSession], primary.Select(b => b.Id));
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
        await rig.Click(rig.Page, CalendarIds.PlanSession);
        await rig.Click(rig.Page, CalendarIds.RepeatPrefix + "weekly");
        UiPage planning = await rig.PageAsync();

        Assert.NotEmpty(month.Children);
        Assert.NotEmpty(week.Children);
        Assert.NotEmpty(agenda.Children);
        Assert.NotEmpty(planning.Children);
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
