using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.UI.Sdk;
using Xunit;

namespace CalendarPlanner.Tests;

/// <summary>The store, the state and every contribution wired together on a fake context (2026-01-01 09:00 UTC, a Thursday; the week is 29 Dec to 4 Jan).</summary>
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

    public static async Task<Rig> CreateWithStoredDataAsync(string dataJson)
    {
        var ctx = new FakePluginContext("com.windowsdetox.calendar");
        await ctx.Storage.WriteAsync(CalendarStore.DataKey, dataJson, CancellationToken.None);
        var store = new CalendarStore(ctx.Storage, ctx.Time, ctx.Logger);
        await store.LoadAsync(CancellationToken.None);
        return new Rig(ctx, store);
    }

    public async Task<CalendarEntry> AddAsync(CalendarEntry entry) => (await Store.AddAsync(entry, CancellationToken.None))!;

    public async Task<UiPage> PageAsync() => Assert.IsType<UiPage>(await Ctx.RenderAsync(Page));

    public async Task<UiCard> WidgetAsync() => Assert.IsType<UiCard>(await Ctx.RenderAsync(Widget));

    public async Task<UiCard> CompactAsync() => Assert.IsType<UiCard>(await Ctx.RenderAsync(Compact));

    public async Task<UiNode> SettingsAsync() => await Ctx.RenderAsync(Settings);

    public ValueTask Click(IUiContribution target, string id) => Ctx.SendActionAsync(target, UiAction.Click(id));

    public ValueTask Click(string id) => Click(Page, id);

    public ValueTask ClickExtend(string id) =>
        Ctx.SendActionAsync(Page, new UiAction(id, UiActionKind.Click, null, new Dictionary<string, string> { ["extend"] = "true" }));

    public ValueTask Schedule(string id, int column, int start, int end) =>
        Ctx.SendActionAsync(
            Page,
            new UiAction(
                id,
                UiActionKind.Schedule,
                null,
                new Dictionary<string, string>
                {
                    ["day"] = column.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["start"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["end"] = end.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));

    public ValueTask Submit(IUiContribution target, string id, Dictionary<string, string> values) => Ctx.SendActionAsync(target, UiAction.Submit(id, values));

    public ValueTask SaveEditor(Dictionary<string, string> values) => Submit(Page, CalendarForm.FormId, values);

    public async Task<CalendarEntry> AddSessionTodayAsync(string title = "Deep work", int minute = 600, int length = 60) =>
        await AddAsync(Kit.Session(title, Kit.D(2026, 1, 1), minute, length));

    /// <summary>The quick editor's fields as the host would send them.</summary>
    public static Dictionary<string, string> Quick(string title = "Deep work", string kind = "session", string time = "10:00", string to = "11:30", string repeat = "once") => new()
    {
        [CalendarForm.TitleId] = title,
        [CalendarForm.KindId] = kind,
        [CalendarForm.TimeId] = time,
        [CalendarForm.ToId] = to,
        [CalendarForm.RepeatId] = repeat,
    };

    public static string BlockId(CalendarEntry entry, int originalDay, int segment = 0) =>
        CalendarIds.Make(CalendarIds.EventPrefix, entry.Id, originalDay, segment);
}

/// <summary>Shared lookups into the page tree.</summary>
public static class PageTree
{
    public static UiTimeGrid Grid(UiNode root) => Kit.Descendants(root).OfType<UiTimeGrid>().Single();

    public static UiMonthGrid Month(UiNode root) => Kit.Descendants(root).OfType<UiMonthGrid>().Single();

    public static UiCard CalendarCard(UiPage page) => page.Children.OfType<UiCard>().Single();

    public static UiForm Form(UiNode root) => Kit.Descendants(root).OfType<UiForm>().Single();

    public static T Field<T>(UiNode root, string id)
        where T : UiFormField => Form(root).Fields.OfType<T>().Single(f => f.Id == id);

    public static UiNode Popover(UiPage page) => Grid(page).Popover ?? throw new InvalidOperationException("The grid has no popover.");

    public static UiTimeGridEvent Block(UiPage page, string id) => Grid(page).Events.Single(e => e.Id == id);

    public static string[] ButtonIds(UiNode root) => [.. Kit.Descendants(root).OfType<UiButton>().Select(b => b.Id)];
}

public class CalendarPageTests
{
    private static readonly int Today = Kit.D(2026, 1, 1);

    [Fact]
    public async Task The_empty_page_is_a_header_row_and_one_time_grid_and_nothing_else()
    {
        var rig = await Rig.CreateAsync();

        UiPage page = await rig.PageAsync();

        Assert.Equal("Calendar", page.Title);
        Assert.False(string.IsNullOrWhiteSpace(page.Subtitle));
        Assert.Equal(4, Assert.IsType<UiGrid>(page.Children[0]).Columns);
        UiCard card = PageTree.CalendarCard(page);
        Assert.Null(card.Title);
        Assert.Equal(
            [CalendarIds.Today, CalendarIds.Prev, CalendarIds.Next, CalendarIds.ViewPrefix + "day", CalendarIds.ViewPrefix + "week", CalendarIds.ViewPrefix + "month"],
            PageTree.ButtonIds(page));
        Assert.Empty(Kit.Descendants(page).OfType<UiForm>());
        Assert.Empty(Kit.Descendants(page).OfType<UiToggle>());
        Assert.Empty(Kit.Descendants(page).OfType<UiGlyph>());
        Assert.Empty(Kit.Descendants(page).OfType<UiMonthGrid>());
        Assert.DoesNotContain(Kit.Descendants(page).OfType<UiText>(), t => t.Text == "Calendar");
    }

    [Fact]
    public async Task The_week_is_the_default_view_with_seven_columns_now_and_a_scroll_position()
    {
        var rig = await Rig.CreateAsync();

        UiTimeGrid grid = PageTree.Grid(await rig.PageAsync());

        Assert.Equal((CalendarIds.Grid, 15, true, 9 * 60, 8 * 60), (grid.Id, grid.SnapMinutes, grid.AllowCreate, grid.NowMinute, grid.ScrollToMinute));
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], grid.Days.Select(d => d.Label));
        Assert.Equal([29, 30, 31, 1, 2, 3, 4], grid.Days.Select(d => d.Number!.Value));
        Assert.Equal([3], grid.Days.Select((d, i) => (d.IsToday, i)).Where(x => x.IsToday).Select(x => x.i));
        Assert.Equal(CalendarIds.ColumnPrefix + Kit.D(2025, 12, 29), grid.Days[0].Id);
        Assert.Empty(grid.Events);
        Assert.Null(grid.Popover);
        Assert.Null(grid.PendingDay);
    }

    [Fact]
    public async Task The_period_title_follows_the_view()
    {
        var rig = await Rig.CreateAsync();

        Assert.True(Kit.HasText(await rig.PageAsync(), "29 Dec 2025 – 4 Jan 2026"));
        await rig.Click(CalendarIds.ViewPrefix + "day");
        Assert.True(Kit.HasText(await rig.PageAsync(), "Thursday 1 January 2026"));
        await rig.Click(CalendarIds.ViewPrefix + "month");
        Assert.True(Kit.HasText(await rig.PageAsync(), "January 2026"));
    }

    [Fact]
    public async Task The_current_view_is_the_filled_button_of_the_switch()
    {
        var rig = await Rig.CreateAsync();

        UiPage page = await rig.PageAsync();

        UiButton[] views = [.. new[] { "day", "week", "month" }.Select(v => Kit.Button(page, CalendarIds.ViewPrefix + v))];
        Assert.Equal(["Day", "Week", "Month"], views.Select(b => b.Label));
        Assert.Equal([UiButtonVariant.Subtle, UiButtonVariant.Secondary, UiButtonVariant.Subtle], views.Select(b => b.Variant));
        Assert.DoesNotContain(Kit.Descendants(page).OfType<UiButton>(), b => b.Variant == UiButtonVariant.Primary);
        Assert.Equal(["Today", "‹", "›"], new[] { CalendarIds.Today, CalendarIds.Prev, CalendarIds.Next }.Select(id => Kit.Button(page, id).Label));
    }

    [Theory]
    [InlineData("day", 1)]
    [InlineData("week", 7)]
    public async Task Day_and_week_are_a_time_grid_with_one_or_seven_columns(string view, int columns)
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(CalendarIds.ViewPrefix + view);

        UiPage page = await rig.PageAsync();

        Assert.Equal(columns, PageTree.Grid(page).Days.Count);
        Assert.Empty(Kit.Descendants(page).OfType<UiMonthGrid>());
    }

    [Fact]
    public async Task Previous_next_and_today_move_by_a_day_a_week_or_a_month()
    {
        var rig = await Rig.CreateAsync();

        await rig.Click(CalendarIds.Next);
        Assert.True(Kit.HasText(await rig.PageAsync(), "5 – 11 January 2026"));
        await rig.Click(CalendarIds.Prev);
        await rig.Click(CalendarIds.Prev);
        Assert.True(Kit.HasText(await rig.PageAsync(), "22 – 28 December 2025"));
        await rig.Click(CalendarIds.Today);
        Assert.True(Kit.HasText(await rig.PageAsync(), "29 Dec 2025 – 4 Jan 2026"));

        await rig.Click(CalendarIds.ViewPrefix + "day");
        await rig.Click(CalendarIds.Next);
        Assert.True(Kit.HasText(await rig.PageAsync(), "Friday 2 January 2026"));
        await rig.Click(CalendarIds.Today);
        Assert.True(Kit.HasText(await rig.PageAsync(), "Thursday 1 January 2026"));

        await rig.Click(CalendarIds.ViewPrefix + "month");
        await rig.Click(CalendarIds.Next);
        await rig.Click(CalendarIds.Next);
        Assert.True(Kit.HasText(await rig.PageAsync(), "March 2026"));
        await rig.Click(CalendarIds.Prev);
        Assert.True(Kit.HasText(await rig.PageAsync(), "February 2026"));
    }

    [Fact]
    public async Task Switching_the_view_keeps_the_day_that_was_shown()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(CalendarIds.Next);
        await rig.Click(CalendarIds.Next);

        await rig.Click(CalendarIds.ViewPrefix + "month");

        Assert.True(Kit.HasText(await rig.PageAsync(), "January 2026"));
        await rig.Click(CalendarIds.ViewPrefix + "week");
        Assert.True(Kit.HasText(await rig.PageAsync(), "12 – 18 January 2026"));
    }

    [Fact]
    public async Task Weeks_can_start_on_sunday_and_the_clock_can_be_twelve_hours()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddSessionTodayAsync();
        await rig.Store.SetSettingsAsync(new CalendarSettings { WeekStartsOnSunday = true, Use24Hour = false }, CancellationToken.None);

        UiPage page = await rig.PageAsync();

        UiTimeGrid grid = PageTree.Grid(page);
        Assert.Equal(["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"], grid.Days.Select(d => d.Label));
        Assert.Equal(28, grid.Days[0].Number);
        Assert.Equal("10:00 AM–11:00 AM", Assert.Single(grid.Events).Detail);
        Assert.Equal("Deep work, today 10:00 AM", Kit.Tiles(page)[0].Caption);
    }

    [Fact]
    public async Task The_empty_tiles_read_clearly_and_fit()
    {
        var rig = await Rig.CreateAsync();

        UiStatTile[] tiles = Kit.Tiles(await rig.PageAsync());

        Assert.Equal(
            [("Next session", "None", "Drag on the calendar"), ("Today", "0 / 0", "Nothing planned"), ("This week", "0 h", "Planned focus"), ("Streak", "0", "Sessions kept")],
            tiles.Select(t => (t.Label, t.Value, t.Caption)));
        Assert.All(tiles, t => Assert.True(t.Value.Length <= 6));
        Assert.All(tiles, t => Assert.True(t.Label.Split(' ').Length <= 2));
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
    public async Task A_busy_calendar_still_renders_within_the_node_limits_in_every_view_and_popover_state()
    {
        var rig = await Rig.CreateAsync();
        for (int i = 0; i < 150; i++)
        {
            CalendarEntry e = Kit.Daily(i % 2 == 0 ? Kit.Task("task " + i, Today, (i % 24) * 60) : Kit.Task("untimed " + i, Today), 1 + (i % 3));
            e.Duration = i % 2 == 0 ? 45 : 0;
            e.Color = i % 6;
            _ = await rig.AddAsync(e);
        }

        UiPage week = await rig.PageAsync();
        string firstBlock = PageTree.Grid(week).Events[0].Id;
        await rig.Click(firstBlock);
        UiPage selected = await rig.PageAsync();
        await rig.ClickExtend(PageTree.Grid(week).Events[3].Id);
        UiPage several = await rig.PageAsync();
        await rig.Schedule(CalendarIds.Grid, 2, 600, 660);
        UiPage creating = await rig.PageAsync();
        await rig.Click(CalendarIds.More);
        UiPage more = await rig.PageAsync();
        await rig.Click(CalendarIds.ViewPrefix + "day");
        UiPage day = await rig.PageAsync();
        await rig.Click(CalendarIds.ViewPrefix + "month");
        UiPage month = await rig.PageAsync();

        Assert.True(PageTree.Grid(week).Events.Count <= CalendarGrid.MaxBlocks);
        Assert.NotNull(PageTree.Grid(selected).Popover);
        Assert.NotNull(PageTree.Grid(several).Popover);
        Assert.NotNull(PageTree.Grid(creating).Popover);
        Assert.NotNull(PageTree.Grid(more).Popover);
        Assert.NotEmpty(PageTree.Grid(day).Events);
        Assert.Equal(42, PageTree.Month(month).Cells.Count);
    }

    [Fact]
    public async Task Rendering_requests_follow_changes()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.PageAsync();
        int before = rig.Ctx.RenderRequests;

        _ = await rig.AddAsync(Kit.Task("a", Today));
        await rig.Click(CalendarIds.Next);

        Assert.True(rig.Ctx.RenderRequests >= before + 2);
    }
}

public class CalendarGridBlockTests
{
    private static readonly int Today = Kit.D(2026, 1, 1);
    private static readonly int Friday = Kit.D(2026, 1, 2);

    [Fact]
    public async Task A_session_is_an_accent_block_in_its_column_with_the_time_range_as_detail()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry session = await rig.AddSessionTodayAsync();

        UiTimeGridEvent block = Assert.Single(PageTree.Grid(await rig.PageAsync()).Events);

        Assert.Equal((Rig.BlockId(session, Today), 3, 600, 660, "Deep work", "10:00–11:00", UiTone.Accent), (block.Id, block.Day, block.StartMinute, block.EndMinute, block.Title, block.Detail, block.Tone));
        Assert.Equal((false, true, true), (block.IsSelected, block.IsMovable, block.IsResizable));
    }

    [Fact]
    public async Task Tasks_take_the_tone_of_their_colour_tag_and_turn_green_when_done()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry plain = await rig.AddAsync(Kit.Task("plain", Today, 540));
        CalendarEntry blue = Kit.Task("blue", Today, 600);
        blue.Color = 1;
        CalendarEntry red = Kit.Task("red", Today, 660);
        red.Color = 4;
        CalendarEntry blueStored = await rig.AddAsync(blue);
        _ = await rig.AddAsync(red);
        _ = await rig.Store.SetDoneAsync(plain.Id, Today, true, CancellationToken.None);

        UiTimeGridEvent[] blocks = [.. PageTree.Grid(await rig.PageAsync()).Events.OrderBy(b => b.StartMinute)];

        Assert.Equal([UiTone.Success, UiTone.Accent, UiTone.Danger], blocks.Select(b => b.Tone));
        Assert.Equal(blueStored.Id, int.Parse(blocks[1].Id.Split('-')[2], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_task_without_a_length_is_a_short_block_with_only_its_start_as_detail()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("Call", Today, 570));

        UiTimeGridEvent block = Assert.Single(PageTree.Grid(await rig.PageAsync()).Events);

        Assert.Equal((570, 600, "09:30"), (block.StartMinute, block.EndMinute, block.Detail));
    }

    [Fact]
    public async Task A_block_that_crosses_midnight_is_split_per_day_and_both_halves_show_the_whole_range()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry night = await rig.AddAsync(Kit.Session("Night owl", Friday, 23 * 60, 120));

        UiTimeGridEvent[] blocks = [.. PageTree.Grid(await rig.PageAsync()).Events];

        Assert.Equal([Rig.BlockId(night, Friday, 0), Rig.BlockId(night, Friday, 1)], blocks.Select(b => b.Id));
        Assert.Equal([(4, 1380, 1440), (5, 0, 60)], blocks.Select(b => (b.Day, b.StartMinute, b.EndMinute)));
        Assert.All(blocks, b => Assert.Equal("23:00–01:00", b.Detail));
        Assert.All(blocks, b => Assert.Equal((false, false), (b.IsMovable, b.IsResizable)));
    }

    [Fact]
    public async Task The_second_half_of_a_block_from_the_last_sunday_shows_in_the_next_week()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry night = await rig.AddAsync(Kit.Session("Night owl", Kit.D(2026, 1, 4), 23 * 60, 120));
        await rig.Click(CalendarIds.Next);

        UiTimeGridEvent block = Assert.Single(PageTree.Grid(await rig.PageAsync()).Events);

        Assert.Equal((Rig.BlockId(night, Kit.D(2026, 1, 4), 1), 0, 0, 60), (block.Id, block.Day, block.StartMinute, block.EndMinute));
    }

    [Fact]
    public async Task A_block_ending_exactly_at_midnight_stays_in_one_piece()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Session("Late", Friday, 22 * 60, 120));

        UiTimeGridEvent block = Assert.Single(PageTree.Grid(await rig.PageAsync()).Events);

        Assert.Equal((1320, 1440, true), (block.StartMinute, block.EndMinute, block.IsMovable));
    }

    [Fact]
    public async Task A_repeating_entry_has_a_block_on_each_of_its_days_in_the_week()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Weekly(Kit.Session("Study", Kit.D(2025, 12, 29), 540, 60), 0b0010101));

        UiTimeGridEvent[] blocks = [.. PageTree.Grid(await rig.PageAsync()).Events];

        Assert.Equal([0, 2, 4], blocks.Select(b => b.Day));
    }

    [Fact]
    public async Task A_session_that_started_or_was_missed_gets_a_result_tone_and_cannot_be_dragged()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry started = await rig.AddSessionTodayAsync("Started", 480, 30);
        CalendarEntry missed = await rig.AddSessionTodayAsync("Missed", 420, 30);
        _ = await rig.Store.RecordAsync($"{started.Id}:{Today}", SessionOutcome.Started, Kit.Start.ToUnixTimeSeconds() - 3600, CancellationToken.None);
        _ = await rig.Store.RecordAsync($"{missed.Id}:{Today}", SessionOutcome.Missed, Kit.Start.ToUnixTimeSeconds() - 7200, CancellationToken.None);

        UiTimeGrid grid = PageTree.Grid(await rig.PageAsync());

        UiTimeGridEvent a = grid.Events.Single(b => b.Title == "Started");
        UiTimeGridEvent b = grid.Events.Single(b => b.Title == "Missed");
        Assert.Equal((UiTone.Success, false, false), (a.Tone, a.IsMovable, a.IsResizable));
        Assert.Equal((UiTone.Warning, false, false), (b.Tone, b.IsMovable, b.IsResizable));
    }

    [Fact]
    public async Task The_grid_scrolls_to_an_hour_before_the_first_block_or_to_eight()
    {
        var rig = await Rig.CreateAsync();
        Assert.Equal(480, PageTree.Grid(await rig.PageAsync()).ScrollToMinute);

        _ = await rig.AddAsync(Kit.Task("late", Today, 14 * 60 + 30));
        _ = await rig.AddAsync(Kit.Task("early", Friday, 10 * 60 + 30));

        Assert.Equal(570, PageTree.Grid(await rig.PageAsync()).ScrollToMinute);
        _ = await rig.AddAsync(Kit.Task("dawn", Today, 20));
        Assert.Equal(0, PageTree.Grid(await rig.PageAsync()).ScrollToMinute);
    }

    [Fact]
    public async Task The_now_line_appears_only_while_today_is_visible()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(CalendarIds.Next);

        Assert.Null(PageTree.Grid(await rig.PageAsync()).NowMinute);

        await rig.Click(CalendarIds.Today);
        Assert.Equal(540, PageTree.Grid(await rig.PageAsync()).NowMinute);
    }

    [Fact]
    public async Task Tasks_without_a_time_are_listed_under_the_grid_with_a_tick_and_stay_off_the_grid()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("Buy milk", Friday));

        UiPage page = await rig.PageAsync();

        Assert.Empty(PageTree.Grid(page).Events);
        Assert.True(Kit.HasText(page, "No time set"));
        UiToggle tick = Assert.Single(Kit.Descendants(page).OfType<UiToggle>());
        Assert.Equal((CalendarIds.Make(CalendarIds.DonePrefix, task.Id, Friday), "Fri 2 Jan  Buy milk", false), (tick.Id, tick.Label, tick.IsOn));
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle(tick.Id, true));
        Assert.True(Assert.Single(Kit.Descendants(await rig.PageAsync()).OfType<UiToggle>()).IsOn);
        Assert.Contains(Friday, rig.Store.Find(task.Id)!.DoneDays);
    }

    [Fact]
    public async Task The_untimed_list_is_cut_after_six_rows()
    {
        var rig = await Rig.CreateAsync();
        for (int i = 0; i < 8; i++)
        {
            _ = await rig.AddAsync(Kit.Task("t" + i, Friday));
        }

        UiPage page = await rig.PageAsync();

        Assert.Equal(CalendarPage.MaxUntimedRows, Kit.Descendants(page).OfType<UiToggle>().Count());
        Assert.True(Kit.HasText(page, "+2 more"));
    }

    [Fact]
    public async Task Opening_an_untimed_task_shows_its_details_under_the_list_not_in_a_popover()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("Buy milk", Friday));

        await rig.Click(CalendarIds.Make(CalendarIds.OpenPrefix, task.Id, Friday));
        UiPage page = await rig.PageAsync();

        Assert.Null(PageTree.Grid(page).Popover);
        Assert.True(Kit.HasText(page, "Friday 2 January, no time set"));
        Assert.All([CalendarIds.Edit, CalendarIds.Duplicate, CalendarIds.Delete], id => Assert.Contains(id, PageTree.ButtonIds(page)));
        Assert.Single(Kit.Descendants(page).OfType<UiToggle>());
    }

    [Fact]
    public async Task Duplicating_an_untimed_task_copies_it_at_once()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("Buy milk", Friday));
        await rig.Click(CalendarIds.Make(CalendarIds.OpenPrefix, task.Id, Friday));

        await rig.Click(CalendarIds.Duplicate);

        Assert.Equal(["Buy milk", "Buy milk"], rig.Store.Entries.Select(e => e.Title));
        Assert.Equal(2, rig.Store.Entries.Count(e => e.StartDay == Friday && e.StartMinute == -1));
        Assert.True(Kit.HasText(await rig.PageAsync(), "Duplicated \"Buy milk\"."));
    }
}

public class CalendarMonthTests
{
    private static readonly int Today = Kit.D(2026, 1, 1);

    private static async Task<Rig> MonthRigAsync()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(CalendarIds.ViewPrefix + "month");
        return rig;
    }

    [Fact]
    public async Task The_month_is_one_full_size_grid_and_the_only_thing_under_the_header()
    {
        var rig = await MonthRigAsync();

        UiPage page = await rig.PageAsync();

        UiMonthGrid month = PageTree.Month(page);
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], month.WeekdayLabels);
        Assert.Equal(42, month.Cells.Count);
        Assert.False(month.IsCompact);
        Assert.Null(month.Title);
        Assert.Null(month.PreviousId);
        Assert.Equal(month.Cells.Count, month.Cells.Select(c => c.Id).Distinct().Count());
        Assert.Equal(31, month.Cells.Count(c => !c.IsOutside));
        Assert.Empty(Kit.Descendants(page).OfType<UiForm>());
        Assert.Empty(Kit.Descendants(page).OfType<UiTimeGrid>());
        Assert.Equal(
            [CalendarIds.Today, CalendarIds.Prev, CalendarIds.Next, CalendarIds.ViewPrefix + "day", CalendarIds.ViewPrefix + "week", CalendarIds.ViewPrefix + "month"],
            PageTree.ButtonIds(page));
    }

    [Fact]
    public async Task Today_and_the_selected_day_are_marked_and_other_months_are_dimmed()
    {
        var rig = await MonthRigAsync();

        UiMonthGrid month = PageTree.Month(await rig.PageAsync());

        UiMonthCell today = month.Cells.Single(c => c.IsToday);
        Assert.Equal((CalendarIds.DayPrefix + Today, 1, true, false), (today.Id, today.Day, today.IsSelected, today.IsOutside));
        Assert.Equal("Thursday 1 January", today.Description);
        UiMonthCell december = month.Cells[0];
        Assert.Equal((CalendarIds.DayPrefix + Kit.D(2025, 12, 29), 29, true), (december.Id, december.Day, december.IsOutside));
    }

    [Fact]
    public async Task A_day_carries_the_titles_of_its_entries_at_most_three_and_the_real_count()
    {
        var rig = await MonthRigAsync();
        _ = await rig.AddSessionTodayAsync("Deep work", 600);
        for (int i = 1; i <= 4; i++)
        {
            _ = await rig.AddAsync(Kit.Task("task " + i, Today, 700 + i));
        }

        _ = await rig.AddAsync(Kit.Task("Dentist", Kit.D(2026, 1, 8)));

        UiMonthGrid month = PageTree.Month(await rig.PageAsync());

        UiMonthCell today = month.Cells.Single(c => c.IsToday);
        Assert.Equal(["Deep work", "task 1", "task 2"], today.Chips);
        Assert.Equal((5, "Thursday 1 January, 5 entries"), (today.Markers, today.Description));
        UiMonthCell dentist = month.Cells.Single(c => c.Id == CalendarIds.DayPrefix + Kit.D(2026, 1, 8));
        Assert.Equal(["Dentist"], dentist.Chips);
        Assert.Equal((1, "Thursday 8 January, 1 entry"), (dentist.Markers, dentist.Description));
        Assert.Null(month.Cells.Single(c => c.Id == CalendarIds.DayPrefix + Kit.D(2026, 1, 9)).Chips);
    }

    [Fact]
    public async Task Clicking_a_day_opens_the_day_view_on_that_day()
    {
        var rig = await MonthRigAsync();
        _ = await rig.AddAsync(Kit.Task("Dentist", Kit.D(2026, 1, 8), 14 * 60));

        await rig.Click(CalendarIds.DayPrefix + Kit.D(2026, 1, 8));
        UiPage page = await rig.PageAsync();

        UiTimeGrid grid = PageTree.Grid(page);
        Assert.Equal(["Thu"], grid.Days.Select(d => d.Label));
        Assert.Equal("Dentist", Assert.Single(grid.Events).Title);
        Assert.True(Kit.HasText(page, "Thursday 8 January 2026"));
        Assert.Null(grid.NowMinute);
        Assert.Empty(Kit.Descendants(page).OfType<UiMonthGrid>());
    }

    [Fact]
    public async Task Clicking_a_day_of_another_month_opens_that_day_too()
    {
        var rig = await MonthRigAsync();

        await rig.Click(CalendarIds.DayPrefix + Kit.D(2026, 2, 2));

        Assert.True(Kit.HasText(await rig.PageAsync(), "Monday 2 February 2026"));
    }

    [Fact]
    public async Task Going_back_to_the_month_marks_the_day_that_was_opened_as_selected()
    {
        var rig = await MonthRigAsync();
        await rig.Click(CalendarIds.DayPrefix + Kit.D(2026, 1, 20));

        await rig.Click(CalendarIds.ViewPrefix + "month");

        UiMonthGrid month = PageTree.Month(await rig.PageAsync());
        Assert.Equal([CalendarIds.DayPrefix + Kit.D(2026, 1, 20)], month.Cells.Where(c => c.IsSelected).Select(c => c.Id));
    }

    [Fact]
    public async Task Clicking_a_week_column_header_opens_the_day_view_for_that_day()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Task("Friday task", Kit.D(2026, 1, 2), 600));

        await rig.Click(CalendarIds.ColumnPrefix + Kit.D(2026, 1, 2));
        UiPage page = await rig.PageAsync();

        UiTimeGrid grid = PageTree.Grid(page);
        Assert.Equal(["Fri"], grid.Days.Select(d => d.Label));
        Assert.Equal("Friday task", Assert.Single(grid.Events).Title);
        Assert.True(Kit.HasText(page, "Friday 2 January 2026"));
    }

    [Fact]
    public async Task Malformed_ids_and_values_change_nothing()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("a", Today, 600));
        string[] bad =
        [
            "cal-day-x", "cal-col-", "cal-day-9999999999", "cal-ev-1-2", "cal-ev-a-b-c", "cal-view-year", "cal-open-1", "cal-nothing", "cal-delete-1-2", CalendarIds.ColumnPrefix + "99999999",
        ];

        foreach (string id in bad)
        {
            await rig.Click(id);
        }

        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle(CalendarIds.DonePrefix + "x-y", true));
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle(CalendarIds.Make(CalendarIds.DonePrefix, task.Id, Today), true) with { Value = "maybe" });
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Submit(CalendarForm.FormId, Rig.Quick()));
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Submit("cal-other", Rig.Quick()));

        UiPage page = await rig.PageAsync();
        Assert.True(Kit.HasText(page, "29 Dec 2025 – 4 Jan 2026"));
        Assert.Null(PageTree.Grid(page).Popover);
        Assert.Single(rig.Store.Entries);
        Assert.Empty(rig.Store.Find(task.Id)!.DoneDays);
    }
}
