using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.Sdk.Extensions;
using Blocker.UI.Sdk;
using Xunit;

namespace CalendarPlanner.Tests;

public class CalendarWidgetTests
{
    private static readonly int Today = Kit.D(2026, 1, 1);

    private static UiStatTile Stat(UiNode root) => Kit.Descendants(root).OfType<UiStatTile>().Single();

    private static UiMonthGrid Month(UiNode root) => Kit.Descendants(root).OfType<UiMonthGrid>().Single();

    [Fact]
    public async Task Both_widgets_are_cards_without_title_logo_or_nested_cards()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();

        foreach (UiCard card in new[] { await rig.WidgetAsync(), await rig.CompactAsync() })
        {
            Assert.Null(card.Title);
            Assert.Null(card.Subtitle);
            Assert.Null(card.Icon);
            Assert.Empty(Kit.Descendants(card).OfType<UiGlyph>());
            Assert.Empty(Kit.Descendants(card).Skip(1).OfType<UiCard>());
            Assert.Empty(Kit.Descendants(card).OfType<UiForm>());
            Assert.Single(Kit.Descendants(card).OfType<UiStatTile>());
            Assert.IsType<UiStatTile>(card.Children[0]);
        }
    }

    [Fact]
    public async Task The_sizes_are_small_and_medium()
    {
        var rig = await Rig.CreateAsync();

        Assert.Equal((UiWidgetSize.Small, UiWidgetSize.Medium), (rig.Compact.Size, rig.Widget.Size));
    }

    [Fact]
    public async Task The_small_widget_is_a_single_tile_so_nothing_is_clipped_in_one_cell()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync("Deep work", 10 * 60);

        UiCard card = await rig.CompactAsync();

        UiStatTile tile = Assert.IsType<UiStatTile>(Assert.Single(card.Children));
        Assert.Equal(("Next session", "60 min", "Deep work, 10:00", UiTone.Accent), (tile.Label, tile.Value, tile.Caption, tile.Tone));
        Assert.True(tile.Caption!.Length <= 30);
        Assert.Null(tile.Icon);
    }

    [Fact]
    public async Task The_small_widget_with_nothing_planned_has_a_calm_empty_state()
    {
        var rig = await Rig.CreateAsync();

        UiCard card = await rig.CompactAsync();

        UiStatTile stat = Assert.IsType<UiStatTile>(Assert.Single(card.Children));
        Assert.Equal(("Next session", "None", "Plan one on the Calendar page", UiTone.Neutral), (stat.Label, stat.Value, stat.Caption, stat.Tone));
    }

    [Theory]
    [InlineData(0, "now")]
    [InlineData(30, "30 min")]
    [InlineData(150, "2.5 h")]
    public async Task The_countdown_is_short_for_every_distance(int minutesBefore, string value)
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Session("S", Today, 540 + minutesBefore, 60));

        UiStatTile stat = Stat(await rig.CompactAsync());

        Assert.Equal(value, stat.Value);
        Assert.True(stat.Value.Length <= 6);
    }

    [Fact]
    public async Task A_session_tomorrow_says_tomorrow_and_a_later_one_gives_the_date()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Session("Tomorrow's", Today + 1, 540, 60));
        Assert.Equal("Tomorrow's, tomorrow 09:00", Stat(await rig.CompactAsync()).Caption);

        var other = await Rig.CreateAsync();
        _ = await other.AddAsync(Kit.Session("Later", Today + 5, 540, 60));
        Assert.Equal("Later, Tue 6 Jan 09:00", Stat(await other.CompactAsync()).Caption);
        Assert.Equal("5 d", Stat(await other.CompactAsync()).Value);
    }

    [Fact]
    public async Task A_started_session_shows_now_in_success_tone_and_when_it_ends()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry s = await rig.AddSessionTodayAsync("Deep work", 8 * 60, 240);
        _ = await rig.Store.RecordAsync($"{s.Id}:{Today}", SessionOutcome.Started, Kit.Start.AddHours(-1).ToUnixTimeSeconds(), CancellationToken.None);

        UiStatTile stat = Stat(await rig.CompactAsync());

        Assert.Equal(("Now", UiTone.Success, "Deep work, until 12:00"), (stat.Value, stat.Tone, stat.Caption));
    }

    [Fact]
    public async Task A_missed_session_is_skipped_for_the_next_one()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry missed = await rig.AddSessionTodayAsync("Missed one", 9 * 60 + 5, 60);
        _ = await rig.AddSessionTodayAsync("Next one", 15 * 60, 60);
        _ = await rig.Store.RecordAsync($"{missed.Id}:{Today}", SessionOutcome.Missed, Kit.Start.ToUnixTimeSeconds(), CancellationToken.None);

        Assert.StartsWith("Next one", Stat(await rig.CompactAsync()).Caption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_title_gives_way_but_the_time_stays_in_one_line()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync(new string('W', 60), 10 * 60);

        string caption = Stat(await rig.CompactAsync()).Caption!;

        Assert.Equal(30, caption.Length);
        Assert.EndsWith("., 10:00", caption, StringComparison.Ordinal);
        Assert.StartsWith("WWW", caption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rich_widget_orders_content_by_importance()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        _ = await rig.AddAsync(Kit.Task("Water plants", Today, 11 * 60));
        _ = await rig.AddAsync(Kit.Task("Tomorrow's task", Today + 1));

        UiCard card = await rig.WidgetAsync();

        UiNode[] top = [.. card.Children.Take(5)];
        Assert.IsType<UiStatTile>(top[0]);
        Assert.IsType<UiMonthGrid>(top[1]);
        Assert.Equal("Today", Assert.IsType<UiText>(top[2]).Text);
        Assert.Equal("10:00-11:00  Deep work", Assert.IsType<UiText>(top[3]).Text.Replace(" (started)", string.Empty));
        Assert.Equal("11:00  Water plants", Assert.IsType<UiToggle>(top[4]).Label);
        Assert.True(Kit.HasText(card, "Coming up"));
        Assert.True(Kit.HasText(card, "Fri 2 Jan  Tomorrow's task"));
    }

    [Fact]
    public async Task The_rich_widget_has_a_compact_month_with_its_own_navigation_and_one_today()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();

        UiCard card = await rig.WidgetAsync();

        UiMonthGrid month = Month(card);
        Assert.True(month.IsCompact);
        Assert.Equal(("January 2026", CalendarIds.WidgetPrev, CalendarIds.WidgetNext), (month.Title, month.PreviousId, month.NextId));
        Assert.Equal(["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"], month.WeekdayLabels);
        Assert.Equal(35, month.Cells.Count);   // the sixth week has no day of January and is left out
        Assert.Equal(1, month.Cells.Count(c => c.IsToday));
        UiMonthCell today = month.Cells.Single(c => c.IsToday);
        Assert.Equal((CalendarIds.WidgetDayPrefix + Today, 1, 1, true), (today.Id, today.Day, today.Markers, today.IsSelected));
        Assert.StartsWith("Monday 29 December", month.Cells[0].Description, StringComparison.Ordinal);
        Assert.All(month.Cells.Take(3), c => Assert.True(c.IsOutside));
        Assert.Empty(Kit.Descendants(card).OfType<UiButton>());
    }

    [Fact]
    public async Task The_rich_widget_with_nothing_planned_stays_calm_but_complete()
    {
        var rig = await Rig.CreateAsync();

        UiCard card = await rig.WidgetAsync();

        Assert.True(Kit.HasText(card, "Today: nothing planned."));
        Assert.False(Kit.HasText(card, "Coming up"));
        Assert.Equal("None", Stat(card).Value);
        Assert.Equal(35, Month(card).Cells.Count);
        Assert.All(Month(card).Cells, c => Assert.Equal(0, c.Markers));
    }

    [Fact]
    public async Task A_month_that_needs_six_weeks_keeps_all_six()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Widget, CalendarIds.WidgetNext);
        await rig.Click(rig.Widget, CalendarIds.WidgetNext);   // March 2026 starts on a Sunday: Monday-first needs six rows

        UiMonthGrid month = Month(await rig.WidgetAsync());

        Assert.Equal("March 2026", month.Title);
        Assert.Equal(42, month.Cells.Count);
    }

    [Fact]
    public async Task Many_items_today_are_cut_to_three_with_a_count()
    {
        var rig = await Rig.CreateAsync();
        for (int i = 0; i < 8; i++)
        {
            _ = await rig.AddAsync(Kit.Task("t" + i, Today, 600 + i));
        }

        UiCard card = await rig.WidgetAsync();

        Assert.Equal(CalendarWidget.MaxDayRows, Kit.Descendants(card).OfType<UiToggle>().Count());
        Assert.True(Kit.HasText(card, "+5 more"));
    }

    [Fact]
    public async Task Previous_and_next_month_move_the_compact_calendar()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Widget, CalendarIds.WidgetNext);
        Assert.Equal("February 2026", Month(await rig.WidgetAsync()).Title);
        await rig.Click(rig.Widget, CalendarIds.WidgetNext);
        await rig.Click(rig.Widget, CalendarIds.WidgetPrev);
        UiMonthGrid month = Month(await rig.WidgetAsync());

        Assert.Equal("February 2026", month.Title);
        Assert.Contains(month.Cells, c => c.Id == CalendarIds.WidgetDayPrefix + Kit.D(2026, 2, 1) && !c.IsOutside && !c.IsSelected);
    }

    [Fact]
    public async Task Selecting_a_day_shows_its_agenda_instead_of_todays()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("Dentist", Kit.D(2026, 1, 8), 14 * 60));
        _ = await rig.AddAsync(Kit.Task("Today's thing", Today));

        await rig.Click(rig.Widget, CalendarIds.WidgetDayPrefix + Kit.D(2026, 1, 8));
        UiCard card = await rig.WidgetAsync();

        Assert.True(Kit.HasText(card, "Thu 8 Jan"));
        Assert.Equal(["14:00  Dentist"], Kit.Descendants(card).OfType<UiToggle>().Select(t => t.Label));
        Assert.True(Month(card).Cells.Single(c => c.Id == CalendarIds.WidgetDayPrefix + Kit.D(2026, 1, 8)).IsSelected);
        Assert.False(Month(card).Cells.Single(c => c.IsToday).IsSelected);
    }

    [Fact]
    public async Task Selecting_an_empty_day_says_so()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Widget, CalendarIds.WidgetDayPrefix + Kit.D(2026, 1, 20));

        Assert.True(Kit.HasText(await rig.WidgetAsync(), "Tue 20 Jan: nothing planned."));
    }

    [Fact]
    public async Task Ticking_a_task_in_the_widget_is_stored_and_renders_again()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("a", Today));
        _ = await rig.WidgetAsync();
        int before = rig.Ctx.RenderRequests;

        await rig.Ctx.SendActionAsync(rig.Widget, UiAction.Toggle(CalendarIds.Make(CalendarIds.WidgetDonePrefix, task.Id, Today), true));

        Assert.Single(rig.Store.Find(task.Id)!.DoneDays);
        Assert.True(rig.Ctx.RenderRequests > before);
        Assert.True(Kit.Descendants(await rig.WidgetAsync()).OfType<UiToggle>().Single().IsOn);
    }

    [Fact]
    public async Task Widget_clicks_with_bad_ids_change_nothing()
    {
        var rig = await Rig.CreateAsync();

        foreach (string id in new[] { "cal-w-day-x", "cal-w-day-5", "cal-w-day-1-2", "cal-w-done-1", "cal-w-nope", string.Empty })
        {
            await rig.Click(rig.Widget, id);
        }

        UiCard card = await rig.WidgetAsync();
        Assert.Equal("January 2026", Month(card).Title);
        Assert.True(Kit.HasText(card, "Today: nothing planned."));
    }

    [Fact]
    public async Task A_calendar_with_hundreds_of_entries_renders_both_widgets_within_the_tree_limits()
    {
        var rig = await Rig.CreateAsync();
        for (int i = 0; i < 250; i++)
        {
            CalendarEntry entry = Kit.Weekly(Kit.Task("task " + i, Today + (i % 20), (i % 24) * 60), 0b1111111, 1 + (i % 4));
            _ = await rig.AddAsync(entry);
        }

        Assert.NotNull(await rig.WidgetAsync());
        Assert.NotNull(await rig.CompactAsync());
    }

    [Fact]
    public async Task The_small_widget_ignores_actions()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Compact, "anything");

        Assert.Empty(rig.Store.Entries);
    }

    [Fact]
    public async Task Widgets_render_from_memory_without_reading_storage_again()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        await rig.Ctx.Storage.DeleteAsync(CalendarStore.DataKey, CancellationToken.None);

        UiCard card = await rig.WidgetAsync();

        Assert.Equal("60 min", Stat(card).Value);
    }
}

public class CalendarSettingsTests
{
    private static async Task<UiNode> RenderAsync(Rig rig) => await rig.SettingsAsync();

    [Fact]
    public async Task The_section_has_every_documented_control()
    {
        var rig = await Rig.CreateAsync();

        UiNode tree = await RenderAsync(rig);

        Assert.Equal("Calendar", rig.Settings.Title);
        Assert.Equal([CalendarIds.SetAuto, CalendarIds.SetWeek, CalendarIds.Set24h], Kit.Descendants(tree).OfType<UiToggle>().Select(t => t.Id));
        Assert.Equal(
            [CalendarIds.SetLengthDown, CalendarIds.SetLengthUp, CalendarIds.SetLeadDown, CalendarIds.SetLeadUp, CalendarIds.SetClear],
            Kit.Descendants(tree).OfType<UiButton>().Select(b => b.Id));
        Assert.True(Kit.HasText(tree, "Default session length: 2 h"));
        Assert.True(Kit.HasText(tree, "Reminder before a session: 1 min"));
        Assert.True(Kit.Descendants(tree).OfType<UiToggle>().All(t => t.Id != CalendarIds.SetWeek || !t.IsOn));
    }

    [Fact]
    public async Task Toggles_change_and_persist_the_settings()
    {
        var rig = await Rig.CreateAsync();

        await rig.Ctx.SendActionAsync(rig.Settings, UiAction.Toggle(CalendarIds.SetWeek, true));
        await rig.Ctx.SendActionAsync(rig.Settings, UiAction.Toggle(CalendarIds.Set24h, false));
        await rig.Ctx.SendActionAsync(rig.Settings, UiAction.Toggle(CalendarIds.SetAuto, false));

        CalendarSettings s = rig.Store.Settings;
        Assert.Equal((true, false, false), (s.WeekStartsOnSunday, s.Use24Hour, s.AutoStart));
        var reloaded = new CalendarStore(rig.Ctx.Storage, rig.Ctx.Time, rig.Ctx.Logger);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Equal((true, false, false), (reloaded.Settings.WeekStartsOnSunday, reloaded.Settings.Use24Hour, reloaded.Settings.AutoStart));
    }

    [Fact]
    public async Task The_default_session_length_moves_by_fifteen_minutes_within_its_range()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Settings, CalendarIds.SetLengthUp);
        Assert.Equal(135, rig.Store.Settings.DefaultSessionMinutes);
        await rig.Click(rig.Settings, CalendarIds.SetLengthDown);
        await rig.Click(rig.Settings, CalendarIds.SetLengthDown);
        Assert.Equal(105, rig.Store.Settings.DefaultSessionMinutes);
        for (int i = 0; i < 20; i++)
        {
            await rig.Click(rig.Settings, CalendarIds.SetLengthDown);
        }

        UiNode tree = await RenderAsync(rig);
        Assert.Equal(15, rig.Store.Settings.DefaultSessionMinutes);
        Assert.False(Kit.Button(tree, CalendarIds.SetLengthDown).IsEnabled);
        Assert.True(Kit.Button(tree, CalendarIds.SetLengthUp).IsEnabled);
    }

    [Fact]
    public async Task The_reminder_lead_goes_from_off_to_thirty_minutes()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(rig.Settings, CalendarIds.SetLeadDown);
        Assert.True(Kit.HasText(await RenderAsync(rig), "Reminder before a session: off"));
        Assert.False(Kit.Button(await RenderAsync(rig), CalendarIds.SetLeadDown).IsEnabled);
        for (int i = 0; i < 40; i++)
        {
            await rig.Click(rig.Settings, CalendarIds.SetLeadUp);
        }

        Assert.Equal(30, rig.Store.Settings.ReminderMinutes);
        Assert.False(Kit.Button(await RenderAsync(rig), CalendarIds.SetLeadUp).IsEnabled);
    }

    [Fact]
    public async Task A_new_default_length_is_used_when_the_end_time_is_left_empty()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(rig.Settings, CalendarIds.SetLengthUp);
        await rig.Schedule(CalendarIds.Grid, 4, 600, 660);
        Dictionary<string, string> values = Rig.Quick();
        values[CalendarForm.ToId] = string.Empty;

        await rig.SaveEditor(values);

        Assert.Equal(135, Assert.Single(rig.Store.Entries).Duration);
    }

    [Fact]
    public async Task Clearing_past_entries_is_disabled_when_there_is_nothing_to_clear()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("today", Kit.D(2026, 1, 1)));

        UiNode tree = await RenderAsync(rig);

        Assert.False(Kit.Button(tree, CalendarIds.SetClear).IsEnabled);
        Assert.True(Kit.HasText(tree, "There are no past entries to clear."));
    }

    [Fact]
    public async Task Clearing_past_entries_needs_two_clicks_and_the_first_times_out()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("old", Kit.D(2025, 12, 1)));
        _ = await rig.AddAsync(Kit.Task("today", Kit.D(2026, 1, 1)));

        await rig.Click(rig.Settings, CalendarIds.SetClear);
        UiButton armed = Kit.Button(await RenderAsync(rig), CalendarIds.SetClear);
        Assert.Equal(("Click again to confirm", UiButtonVariant.Warning), (armed.Label, armed.Variant));
        Assert.Equal(2, rig.Store.Entries.Count);

        rig.Ctx.FakeTime.Advance(CalendarUiState.ConfirmWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("Clear past entries", Kit.Button(await RenderAsync(rig), CalendarIds.SetClear).Label);
        await rig.Click(rig.Settings, CalendarIds.SetClear);
        Assert.Equal(2, rig.Store.Entries.Count);

        await rig.Click(rig.Settings, CalendarIds.SetClear);
        Assert.Equal(["today"], rig.Store.Entries.Select(e => e.Title));
        Assert.True(Kit.HasText(await RenderAsync(rig), "Cleared 1 past entry."));
    }

    [Fact]
    public async Task Clearing_keeps_the_streak()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry old = await rig.AddAsync(Kit.Session("old", Kit.D(2025, 12, 30), 540, 60));
        _ = await rig.Store.RecordAsync($"{old.Id}:{old.StartDay}", SessionOutcome.Started, Kit.Start.AddDays(-2).ToUnixTimeSeconds(), CancellationToken.None);

        await rig.Click(rig.Settings, CalendarIds.SetClear);
        await rig.Click(rig.Settings, CalendarIds.SetClear);

        Assert.Empty(rig.Store.Entries);
        Assert.Equal("1", Kit.Tiles(await rig.PageAsync())[3].Value);
    }

    [Fact]
    public async Task Bad_settings_actions_are_ignored()
    {
        var rig = await Rig.CreateAsync();

        await rig.Ctx.SendActionAsync(rig.Settings, UiAction.Toggle("cal-set-nothing", true));
        await rig.Ctx.SendActionAsync(rig.Settings, UiAction.Toggle(CalendarIds.SetWeek, true) with { Value = "yes" });
        await rig.Click(rig.Settings, "cal-set-nothing");

        Assert.False(rig.Store.Settings.WeekStartsOnSunday);
    }
}

public class CalendarPluginTests
{
    [Fact]
    public async Task Missing_capability_throws_PluginCapabilityException()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar", PluginCapability.UiPage);

        var ex = await Assert.ThrowsAsync<PluginCapabilityException>(async () => await new CalendarPlugin().InitializeAsync(ctx, CancellationToken.None));

        Assert.Equal(PluginCapability.UiDashboardWidget, ex.Capability);
    }

    [Fact]
    public async Task Without_tick_the_auto_starter_cannot_register()
    {
        PluginCapability[] allButTick = [PluginCapability.UiPage, PluginCapability.UiDashboardWidget, PluginCapability.UiSettingsSection, PluginCapability.SessionStart, PluginCapability.Notify];
        var ctx = new FakePluginContext("com.windowsdetox.calendar", allButTick);

        var ex = await Assert.ThrowsAsync<PluginCapabilityException>(async () => await new CalendarPlugin().InitializeAsync(ctx, CancellationToken.None));

        Assert.Equal(PluginCapability.Tick, ex.Capability);
    }

    [Fact]
    public async Task The_plugin_registers_a_page_two_widgets_a_settings_section_and_a_tick_listener()
    {
        FakePluginContext ctx = await Kit.StartPluginAsync();

        Assert.Equal(
            [typeof(CalendarPage), typeof(CalendarWidget), typeof(CalendarCompactWidget), typeof(CalendarSettingsSection), typeof(CalendarAutoStarter)],
            ctx.RegisteredExtensions.Select(e => e.GetType()));
        Assert.Equal(2, ctx.RegisteredExtensions.OfType<IDashboardWidgetPlugin>().Count());
        Assert.Equal("Calendar", ctx.GetExtension<IUiPagePlugin>().Navigation.Title);
        Assert.Equal(UiIcon.Calendar, ctx.GetExtension<IUiPagePlugin>().Navigation.Icon);
        Assert.Empty(ctx.RegisteredIpcMethods);
    }

    [Fact]
    public async Task Every_contribution_renders_a_valid_tree_on_a_fresh_install()
    {
        FakePluginContext ctx = await Kit.StartPluginAsync();

        Assert.IsType<UiPage>(await ctx.RenderAsync(ctx.GetExtension<IUiPagePlugin>()));
        foreach (IDashboardWidgetPlugin widget in ctx.RegisteredExtensions.OfType<IDashboardWidgetPlugin>())
        {
            Assert.IsType<UiCard>(await ctx.RenderAsync(widget));
        }

        Assert.NotNull(await ctx.RenderAsync(ctx.GetExtension<ISettingsSectionPlugin>()));
    }

    private static ValueTask DragAsync(FakePluginContext ctx, int column, int start, int end) =>
        ctx.SendActionAsync(
            ctx.GetExtension<IUiPagePlugin>(),
            new UiAction(
                CalendarIds.Grid,
                UiActionKind.Schedule,
                null,
                new Dictionary<string, string> { ["day"] = column.ToString(System.Globalization.CultureInfo.InvariantCulture), ["start"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture), ["end"] = end.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

    [Fact]
    public async Task A_session_dragged_on_the_page_starts_by_itself_end_to_end()
    {
        FakePluginContext ctx = await Kit.StartPluginAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        _ = await ctx.RenderAsync(page);
        await DragAsync(ctx, 3, 570, 615);
        await ctx.SendActionAsync(page, UiAction.Submit(CalendarForm.FormId, Rig.Quick("Study", time: "09:30", to: "10:15")));
        CalendarAutoStarter starter = (CalendarAutoStarter)ctx.GetExtension<ITickListener>();

        for (int i = 0; i < 4 * 31; i++)
        {
            await ctx.TickAsync(TimeSpan.FromSeconds(15));
            await starter.Work;
        }

        (TimeSpan duration, string reason) = Assert.Single(ctx.FakeSessions.Calls);
        Assert.Equal((TimeSpan.FromMinutes(45), "Study"), (duration, reason));
        Assert.Contains(ctx.FakeNotifier.Messages, m => m.StartsWith("\"Study\" starts in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_calendar_survives_a_restart_of_the_plugin()
    {
        FakePluginContext ctx = await Kit.StartPluginAsync();
        IUiPagePlugin page = ctx.GetExtension<IUiPagePlugin>();
        await DragAsync(ctx, 3, 840, 870);
        await ctx.SendActionAsync(page, UiAction.Submit(CalendarForm.FormId, Kit.Form(kind: "task", title: "Remember", date: "2026-01-20", time: "14:00", length: "30")));

        var again = new FakePluginContext("com.windowsdetox.calendar");
        await again.Storage.WriteAsync(CalendarStore.DataKey, (await ctx.Storage.ReadAsync(CalendarStore.DataKey, CancellationToken.None))!, CancellationToken.None);
        await new CalendarPlugin().InitializeAsync(again, CancellationToken.None);
        IUiPagePlugin restarted = again.GetExtension<IUiPagePlugin>();
        await again.SendActionAsync(restarted, UiAction.Click(CalendarIds.ViewPrefix + "month"));
        await again.SendActionAsync(restarted, UiAction.Click(CalendarIds.DayPrefix + Kit.D(2026, 1, 20)));
        UiNode tree = await again.RenderAsync(restarted);

        Assert.Equal("Remember", Assert.Single(Kit.Descendants(tree).OfType<UiTimeGrid>().Single().Events).Title);
    }

    [Fact]
    public async Task Shutdown_flushes_and_can_be_called_twice()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        var plugin = new CalendarPlugin();
        await plugin.InitializeAsync(ctx, CancellationToken.None);

        await plugin.ShutdownAsync(CancellationToken.None);
        await plugin.ShutdownAsync(CancellationToken.None);

        Assert.Empty(ctx.FakeLogger.Entries.Where(e => e.Level >= Blocker.Sdk.Diagnostics.PluginLogLevel.Warning));
    }

    [Fact]
    public async Task The_plugin_never_uses_the_network_secrets_or_the_oauth_helpers()
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar", PluginCapability.UiPage, PluginCapability.UiDashboardWidget, PluginCapability.UiSettingsSection, PluginCapability.Tick, PluginCapability.SessionStart, PluginCapability.Notify);

        await new CalendarPlugin().InitializeAsync(ctx, CancellationToken.None);

        Assert.DoesNotContain(PluginCapability.Network, ctx.Plugin.Capabilities);
        Assert.Equal(6, ctx.Plugin.Capabilities.Count);
    }
}
