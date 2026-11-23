using Blocker.Plugin.Testing;
using Blocker.UI.Sdk;
using Xunit;

namespace CalendarPlanner.Tests;

/// <summary>Creating entries by dragging on the grid: the pending block, the quick editor and Save.</summary>
public class CalendarCreateTests
{
    private static readonly int Friday = Kit.D(2026, 1, 2);

    [Fact]
    public async Task Dragging_on_the_grid_shows_a_pending_block_and_the_quick_editor_with_nothing_saved_yet()
    {
        var rig = await Rig.CreateAsync();

        await rig.Schedule(CalendarIds.Grid, 4, 600, 690);
        UiPage page = await rig.PageAsync();

        UiTimeGrid grid = PageTree.Grid(page);
        Assert.Equal((4, 600, 690), (grid.PendingDay, grid.PendingStartMinute, grid.PendingEndMinute));
        Assert.Equal((CalendarIds.Grid, CalendarIds.Dismiss), (grid.PopoverAnchorId, grid.DismissId));
        UiNode popover = PageTree.Popover(page);
        UiForm form = PageTree.Form(popover);
        Assert.Equal((CalendarForm.FormId, "Save"), (form.Id, form.SubmitLabel));
        Assert.Equal(
            [CalendarForm.TitleId, CalendarForm.KindId, CalendarForm.WhenId, CalendarForm.TimeId, CalendarForm.ToId, CalendarForm.RepeatId],
            form.Fields.Select(f => f.Id));
        Assert.Equal(
            [UiFieldStyle.Title, UiFieldStyle.Chips, UiFieldStyle.Standard, UiFieldStyle.Compact, UiFieldStyle.Compact, UiFieldStyle.Standard],
            form.Fields.Select(f => f.Style));
        Assert.Equal("Add title", PageTree.Field<UiTextField>(popover, CalendarForm.TitleId).Placeholder);
        Assert.Equal(CalendarIds.More, form.FooterAction?.Id);
        Assert.Equal(new UiNoteField(CalendarForm.WhenId, "Date", "Friday 2 January", Icon: UiIcon.Clock), PageTree.Field<UiNoteField>(popover, CalendarForm.WhenId));
        Assert.Equal("Focus session", PageTree.Field<UiTextField>(popover, CalendarForm.TitleId).Value);
        UiSelectField kind = PageTree.Field<UiSelectField>(popover, CalendarForm.KindId);
        Assert.Equal(["session", "task"], kind.Options.Select(o => o.Value));
        Assert.Equal("session", kind.SelectedValue);
        Assert.Equal(("10:00", "11:30"), (PageTree.Field<UiTextField>(popover, CalendarForm.TimeId).Value, PageTree.Field<UiTextField>(popover, CalendarForm.ToId).Value));
        UiSelectField repeat = PageTree.Field<UiSelectField>(popover, CalendarForm.RepeatId);
        Assert.Equal(["once", "daily", "weekly"], repeat.Options.Select(o => o.Value));
        Assert.Equal("once", repeat.SelectedValue);
        Assert.Equal([CalendarIds.More], PageTree.ButtonIds(popover));
        Assert.Empty(rig.Store.Entries);
    }

    [Fact]
    public async Task Saving_creates_the_entry_closes_the_popover_and_persists_it()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 690);

        await rig.SaveEditor(Rig.Quick("Deep work"));

        CalendarEntry entry = Assert.Single(rig.Store.Entries);
        Assert.Equal((EntryKind.Session, "Deep work", Friday, 600, 90, RepeatKind.Once), (entry.Kind, entry.Title, entry.StartDay, entry.StartMinute, entry.Duration, entry.Repeat));
        var reloaded = new CalendarStore(rig.Ctx.Storage, rig.Ctx.Time, rig.Ctx.Logger);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Equal(["Deep work"], reloaded.Entries.Select(e => e.Title));
        UiPage page = await rig.PageAsync();
        UiTimeGrid grid = PageTree.Grid(page);
        Assert.Null(grid.Popover);
        Assert.Null(grid.PendingDay);
        Assert.Equal((4, 600, 690), (grid.Events.Single().Day, grid.Events.Single().StartMinute, grid.Events.Single().EndMinute));
        Assert.True(Kit.HasText(page, "Added \"Deep work\" on Fri 2 Jan."));
    }

    [Fact]
    public async Task A_save_without_a_title_uses_focus_session_and_the_task_kind_saves_a_task()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 660);
        await rig.SaveEditor(Rig.Quick(title: " "));
        await rig.Schedule(CalendarIds.Grid, 5, 600, 660);
        await rig.SaveEditor(Rig.Quick(title: "Call mum", kind: "task"));

        Assert.Equal([("Focus session", EntryKind.Session), ("Call mum", EntryKind.Task)], rig.Store.Entries.Select(e => (e.Title, e.Kind)));
    }

    [Fact]
    public async Task A_task_created_without_a_title_is_refused_with_a_reason_inside_the_popover()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 660);

        await rig.SaveEditor(Rig.Quick(title: " ", kind: "task"));
        UiPage page = await rig.PageAsync();

        Assert.Empty(rig.Store.Entries);
        Assert.True(Kit.HasText(PageTree.Popover(page), "Give the task a title."));
        Assert.Equal(1, Kit.Descendants(page).OfType<UiText>().Count(t => t.Text == "Give the task a title."));
    }

    [Fact]
    public async Task The_repeat_choice_saves_a_series()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 660);

        await rig.SaveEditor(Rig.Quick(repeat: "weekly"));

        CalendarEntry entry = Assert.Single(rig.Store.Entries);
        Assert.Equal((RepeatKind.Weekly, EndKind.Never, 1), (entry.Repeat, entry.End, entry.Interval));
        await rig.Click(CalendarIds.Next);
        Assert.Equal(4, Assert.Single(PageTree.Grid(await rig.PageAsync()).Events).Day);
    }

    [Fact]
    public async Task A_refused_save_keeps_the_pending_block_and_what_was_typed_and_says_why_inside_the_popover()
    {
        var rig = await Rig.CreateAsync();
        _ = await rig.AddAsync(Kit.Session("Existing", Friday, 600, 60));
        await rig.Schedule(CalendarIds.Grid, 4, 660, 720);

        await rig.SaveEditor(Rig.Quick("Typed title", time: "10:30", to: "11:30"));
        UiPage page = await rig.PageAsync();

        UiNode popover = PageTree.Popover(page);
        Assert.True(Kit.HasText(popover, "Overlaps \"Existing\" on Fri 2 Jan at 10:00."));
        Assert.Equal(1, Kit.Descendants(page).OfType<UiText>().Count(t => t.Text.StartsWith("Overlaps", StringComparison.Ordinal)));
        Assert.Equal("Typed title", PageTree.Field<UiTextField>(popover, CalendarForm.TitleId).Value);
        Assert.Equal(("10:30", "11:30"), (PageTree.Field<UiTextField>(popover, CalendarForm.TimeId).Value, PageTree.Field<UiTextField>(popover, CalendarForm.ToId).Value));
        Assert.Equal(4, PageTree.Grid(page).PendingDay);
        Assert.Single(rig.Store.Entries);
    }

    [Fact]
    public async Task Dismissing_discards_the_pending_block()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 690);

        await rig.Click(CalendarIds.Dismiss);
        UiTimeGrid grid = PageTree.Grid(await rig.PageAsync());

        Assert.Null(grid.Popover);
        Assert.Null(grid.PendingDay);
        Assert.Null(grid.DismissId);
        Assert.Empty(rig.Store.Entries);
        Assert.Null(rig.State.Pending);
        Assert.Null(rig.State.Draft);
    }

    [Fact]
    public async Task A_new_drag_replaces_the_pending_block()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 690);

        await rig.Schedule(CalendarIds.Grid, 1, 840, 900);
        UiTimeGrid grid = PageTree.Grid(await rig.PageAsync());

        Assert.Equal((1, 840, 900), (grid.PendingDay, grid.PendingStartMinute, grid.PendingEndMinute));
        Assert.Equal("14:00", PageTree.Field<UiTextField>(PageTree.Popover(await rig.PageAsync()), CalendarForm.TimeId).Value);
    }

    [Fact]
    public async Task More_options_lists_the_remaining_fields_and_fewer_options_hides_them_again()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 690);

        await rig.Click(CalendarIds.More);
        UiNode popover = PageTree.Popover(await rig.PageAsync());

        Assert.Equal(
            [
                CalendarForm.TitleId, CalendarForm.KindId, CalendarForm.WhenId, CalendarForm.TimeId, CalendarForm.ToId, CalendarForm.RepeatId, CalendarForm.DateId, CalendarForm.EveryId,
                .. Enumerable.Range(0, 7).Select(i => CalendarForm.WeekdayPrefix + i),
                CalendarForm.EndId, CalendarForm.UntilId, CalendarForm.CountId, CalendarForm.ColorId,
            ],
            PageTree.Form(popover).Fields.Select(f => f.Id));
        Assert.Equal("Fewer options", Kit.Button(popover, CalendarIds.More).Label);
        Assert.Equal("2026-01-02", PageTree.Field<UiTextField>(popover, CalendarForm.DateId).Value);
        Assert.Equal(["Fri"], PageTree.Form(popover).Fields.OfType<UiCheckboxField>().Where(f => f.IsChecked).Select(f => f.Label));

        await rig.Click(CalendarIds.More);
        UiNode quick = PageTree.Popover(await rig.PageAsync());
        Assert.Equal(6, PageTree.Form(quick).Fields.Count);
        Assert.Equal("More options", Kit.Button(quick, CalendarIds.More).Label);
    }

    [Fact]
    public async Task The_extra_fields_are_saved_and_a_date_in_another_week_moves_the_view_there()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 690);
        await rig.Click(CalendarIds.More);

        await rig.SaveEditor(Kit.Form(title: "Gym", date: "2026-02-10", time: "10:00", length: "60", repeat: "weekly", every: "2", end: "count", count: "5", color: "2", weekdays: [1, 3]));

        CalendarEntry entry = Assert.Single(rig.Store.Entries);
        Assert.Equal((Kit.D(2026, 2, 10), RepeatKind.Weekly, 2, 0b1010, EndKind.AfterCount, 5, 2), (entry.StartDay, entry.Repeat, entry.Interval, entry.WeekdayMask, entry.End, entry.Count, entry.Color));
        UiPage page = await rig.PageAsync();
        Assert.True(Kit.HasText(page, "9 – 15 February 2026"));
        Assert.Equal([1, 3], PageTree.Grid(page).Events.Select(e => e.Day).Order());
    }

    [Fact]
    public async Task Creating_in_the_day_view_uses_the_shown_day()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(CalendarIds.ColumnPrefix + Kit.D(2026, 1, 8));

        await rig.Schedule(CalendarIds.Grid, 0, 780, 840);
        await rig.SaveEditor(Rig.Quick(time: "13:00", to: "14:00"));

        Assert.Equal(Kit.D(2026, 1, 8), Assert.Single(rig.Store.Entries).StartDay);
    }

    [Fact]
    public async Task A_drag_that_makes_the_editor_save_a_session_that_is_too_long_is_refused()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 0, 1440);

        await rig.SaveEditor(Rig.Quick(time: "00:00", to: "00:00"));

        Assert.Empty(rig.Store.Entries);
        Assert.True(Kit.HasText(PageTree.Popover(await rig.PageAsync()), "A session lasts from 15 minutes to 12 hours."));
    }

    [Theory]
    [InlineData("9", "600", "700")]
    [InlineData("0", "700", "600")]
    [InlineData("0", "600", "600")]
    [InlineData("0", "600", "1441")]
    [InlineData("0", "x", "700")]
    public async Task Schedule_actions_with_impossible_values_are_ignored(string column, string start, string end)
    {
        var rig = await Rig.CreateAsync();

        await rig.Ctx.SendActionAsync(rig.Page, new UiAction(CalendarIds.Grid, UiActionKind.Schedule, null, new Dictionary<string, string> { ["day"] = column, ["start"] = start, ["end"] = end }));
        await rig.Ctx.SendActionAsync(rig.Page, new UiAction(CalendarIds.Grid, UiActionKind.Schedule, null, new Dictionary<string, string>()));

        Assert.Null(rig.State.Pending);
        Assert.Null(PageTree.Grid(await rig.PageAsync()).Popover);
    }

    [Fact]
    public async Task Dragging_does_nothing_in_the_month_view()
    {
        var rig = await Rig.CreateAsync();
        await rig.Click(CalendarIds.ViewPrefix + "month");

        await rig.Schedule(CalendarIds.Grid, 2, 600, 660);

        Assert.Null(rig.State.Pending);
        Assert.Empty(Kit.Descendants(await rig.PageAsync()).OfType<UiForm>());
    }

    [Fact]
    public async Task A_message_lasts_until_the_next_action()
    {
        var rig = await Rig.CreateAsync();
        await rig.Schedule(CalendarIds.Grid, 4, 600, 660);
        await rig.SaveEditor(Rig.Quick());
        Assert.True(Kit.HasTextContaining(await rig.PageAsync(), "Added \"Deep work\""));

        await rig.Click(CalendarIds.Next);

        Assert.False(Kit.HasTextContaining(await rig.PageAsync(), "Added"));
    }

    [Fact]
    public async Task The_calendar_being_full_is_reported_and_keeps_the_editor_open()
    {
        var rig = await Rig.CreateAsync();
        for (int i = 0; i < CalendarLimits.MaxEntries; i++)
        {
            _ = await rig.AddAsync(Kit.Task("t" + i, Kit.D(2026, 3, 1)));
        }

        await rig.Schedule(CalendarIds.Grid, 4, 600, 660);
        await rig.SaveEditor(Rig.Quick());

        Assert.Equal(CalendarLimits.MaxEntries, rig.Store.Entries.Count);
        Assert.True(Kit.HasText(PageTree.Popover(await rig.PageAsync()), "The calendar is full (300 entries). Delete some first."));
    }
}

/// <summary>Selecting blocks (one, several with Ctrl or Shift), the details popover and the multi-selection actions.</summary>
public class CalendarSelectTests
{
    private static readonly int Friday = Kit.D(2026, 1, 2);

    private static async Task<(Rig Rig, CalendarEntry A, CalendarEntry B)> TwoSessionsAsync()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));
        CalendarEntry b = await rig.AddAsync(Kit.Session("Beta", Friday, 660, 60));
        return (rig, a, b);
    }

    [Fact]
    public async Task Clicking_a_block_selects_it_and_shows_its_details_anchored_to_it()
    {
        (Rig rig, CalendarEntry a, CalendarEntry b) = await TwoSessionsAsync();

        await rig.Click(Rig.BlockId(a, Friday));
        UiPage page = await rig.PageAsync();

        UiTimeGrid grid = PageTree.Grid(page);
        Assert.Equal([true, false], grid.Events.OrderBy(e => e.StartMinute).Select(e => e.IsSelected));
        Assert.Equal((Rig.BlockId(a, Friday), CalendarIds.Dismiss), (grid.PopoverAnchorId, grid.DismissId));
        UiNode popover = PageTree.Popover(page);
        Assert.Equal(["Alpha", "Friday 2 January · 09:00 – 10:00", "Focus session 1 h"], Kit.Descendants(popover).OfType<UiText>().Select(t => t.Text));
        Assert.Equal([CalendarIds.Edit, CalendarIds.Duplicate, CalendarIds.Delete], PageTree.ButtonIds(popover));
        Assert.Equal(
            [(UiIcon.Edit, true), (UiIcon.Copy, true), (UiIcon.Delete, true)],
            Kit.Descendants(popover).OfType<UiButton>().Select(b => (b.Icon!.Value, b.IconOnly)));
        Assert.Empty(Kit.Descendants(popover).OfType<UiForm>());
        Assert.NotEqual(Rig.BlockId(b, Friday), grid.PopoverAnchorId);
    }

    [Fact]
    public async Task A_task_has_a_done_tick_in_its_details()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = Kit.Task("Call", Friday, 600);
        task.Color = 3;
        task = await rig.AddAsync(task);
        Assert.Equal(UiTone.Warning, PageTree.Grid(await rig.PageAsync()).Events.Single().Tone);
        await rig.Click(Rig.BlockId(task, Friday));

        UiToggle tick = Assert.Single(Kit.Descendants(PageTree.Popover(await rig.PageAsync())).OfType<UiToggle>());
        Assert.Equal((CalendarIds.Make(CalendarIds.DonePrefix, task.Id, Friday), "Done", false), (tick.Id, tick.Label, tick.IsOn));
        Assert.True(Kit.HasText(PageTree.Popover(await rig.PageAsync()), "Task - Amber"));
        await rig.Ctx.SendActionAsync(rig.Page, UiAction.Toggle(tick.Id, true));

        Assert.Contains(Friday, rig.Store.Find(task.Id)!.DoneDays);
        Assert.Equal(UiTone.Success, PageTree.Grid(await rig.PageAsync()).Events.Single().Tone);
    }

    [Fact]
    public async Task A_repeating_entry_offers_to_delete_this_one_or_the_series()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry series = await rig.AddAsync(Kit.Weekly(Kit.Session("Study", Friday, 540, 60), 0b10000));
        await rig.Click(Rig.BlockId(series, Friday));

        UiNode popover = PageTree.Popover(await rig.PageAsync());

        Assert.Equal([CalendarIds.Edit, CalendarIds.Duplicate, CalendarIds.Delete, CalendarIds.DeleteSeries], PageTree.ButtonIds(popover));
        Assert.Equal(["Delete this one", "Delete series"], new[] { CalendarIds.Delete, CalendarIds.DeleteSeries }.Select(id => Kit.Button(popover, id).Label));
        Assert.True(Kit.HasText(popover, "Focus session 1 h - Every Fri"));
    }

    [Fact]
    public async Task Clicking_another_block_replaces_the_selection_and_dismissing_or_navigating_clears_it()
    {
        (Rig rig, CalendarEntry a, CalendarEntry b) = await TwoSessionsAsync();
        await rig.Click(Rig.BlockId(a, Friday));

        await rig.Click(Rig.BlockId(b, Friday));
        UiTimeGrid grid = PageTree.Grid(await rig.PageAsync());
        Assert.Equal([Rig.BlockId(b, Friday)], grid.Events.Where(e => e.IsSelected).Select(e => e.Id));
        Assert.Equal(Rig.BlockId(b, Friday), grid.PopoverAnchorId);

        await rig.Click(CalendarIds.Dismiss);
        Assert.DoesNotContain(PageTree.Grid(await rig.PageAsync()).Events, e => e.IsSelected);
        Assert.Null(PageTree.Grid(await rig.PageAsync()).Popover);

        await rig.Click(Rig.BlockId(a, Friday));
        await rig.Click(CalendarIds.Next);
        await rig.Click(CalendarIds.Prev);
        Assert.Null(PageTree.Grid(await rig.PageAsync()).Popover);
        Assert.Empty(rig.State.Selection);
    }

    [Fact]
    public async Task Extending_adds_and_removes_blocks_and_the_popover_hangs_on_the_last_one_clicked()
    {
        (Rig rig, CalendarEntry a, CalendarEntry b) = await TwoSessionsAsync();
        await rig.ClickExtend(Rig.BlockId(a, Friday));
        Assert.Equal([Rig.BlockId(a, Friday)], PageTree.Grid(await rig.PageAsync()).Events.Where(e => e.IsSelected).Select(e => e.Id));

        await rig.ClickExtend(Rig.BlockId(b, Friday));
        UiPage both = await rig.PageAsync();

        UiTimeGrid grid = PageTree.Grid(both);
        Assert.Equal(2, grid.Events.Count(e => e.IsSelected));
        Assert.Equal(Rig.BlockId(b, Friday), grid.PopoverAnchorId);
        UiNode popover = PageTree.Popover(both);
        Assert.Equal(["2 selected"], Kit.Descendants(popover).OfType<UiText>().Select(t => t.Text));
        Assert.Equal([CalendarIds.DeleteSelected, CalendarIds.DuplicateNextWeek, CalendarIds.ClearSelection], PageTree.ButtonIds(popover));
        Assert.Equal(["Delete selected", "Duplicate to next week", "Clear selection"], PageTree.ButtonIds(popover).Select(id => Kit.Button(popover, id).Label));

        await rig.ClickExtend(Rig.BlockId(a, Friday));
        UiTimeGrid one = PageTree.Grid(await rig.PageAsync());
        Assert.Equal([Rig.BlockId(b, Friday)], one.Events.Where(e => e.IsSelected).Select(e => e.Id));
        Assert.Equal(Rig.BlockId(b, Friday), one.PopoverAnchorId);
        Assert.Contains(CalendarIds.Edit, PageTree.ButtonIds(PageTree.Popover(await rig.PageAsync())));

        await rig.ClickExtend(Rig.BlockId(b, Friday));
        Assert.Null(PageTree.Grid(await rig.PageAsync()).Popover);
    }

    [Fact]
    public async Task Both_halves_of_a_block_that_crosses_midnight_are_selected_together()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry night = await rig.AddAsync(Kit.Session("Night owl", Friday, 23 * 60, 120));

        await rig.Click(Rig.BlockId(night, Friday, 1));
        UiTimeGrid grid = PageTree.Grid(await rig.PageAsync());

        Assert.All(grid.Events, e => Assert.True(e.IsSelected));
        Assert.Equal(Rig.BlockId(night, Friday, 1), grid.PopoverAnchorId);
    }

    [Fact]
    public async Task Delete_selected_needs_a_second_click_and_removes_every_selected_entry()
    {
        (Rig rig, CalendarEntry a, CalendarEntry b) = await TwoSessionsAsync();
        _ = await rig.AddAsync(Kit.Session("Gamma", Friday, 780, 60));
        await rig.Click(Rig.BlockId(a, Friday));
        await rig.ClickExtend(Rig.BlockId(b, Friday));

        await rig.Click(CalendarIds.DeleteSelected);
        UiButton armed = Kit.Button(PageTree.Popover(await rig.PageAsync()), CalendarIds.DeleteSelected);
        Assert.Equal(("Click again to delete", UiButtonVariant.Warning), (armed.Label, armed.Variant));
        Assert.Equal(3, rig.Store.Entries.Count);

        await rig.Click(CalendarIds.DeleteSelected);
        UiPage page = await rig.PageAsync();

        Assert.Equal(["Gamma"], rig.Store.Entries.Select(e => e.Title));
        Assert.Null(PageTree.Grid(page).Popover);
        Assert.True(Kit.HasText(page, "Deleted 2 entries."));
    }

    [Fact]
    public async Task The_first_click_of_delete_times_out()
    {
        (Rig rig, CalendarEntry a, CalendarEntry b) = await TwoSessionsAsync();
        await rig.Click(Rig.BlockId(a, Friday));
        await rig.ClickExtend(Rig.BlockId(b, Friday));
        await rig.Click(CalendarIds.DeleteSelected);

        rig.Ctx.FakeTime.Advance(CalendarUiState.ConfirmWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("Delete selected", Kit.Button(PageTree.Popover(await rig.PageAsync()), CalendarIds.DeleteSelected).Label);
        await rig.Click(CalendarIds.DeleteSelected);

        Assert.Equal(2, rig.Store.Entries.Count);
        Assert.Equal("Click again to delete", Kit.Button(PageTree.Popover(await rig.PageAsync()), CalendarIds.DeleteSelected).Label);
    }

    [Fact]
    public async Task Deleting_selected_occurrences_of_a_series_skips_only_those_days()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry series = await rig.AddAsync(Kit.Daily(Kit.Task("Water", Kit.D(2026, 1, 2), 600)));
        await rig.Click(Rig.BlockId(series, Friday));
        await rig.ClickExtend(Rig.BlockId(series, Friday + 1));

        await rig.Click(CalendarIds.DeleteSelected);
        await rig.Click(CalendarIds.DeleteSelected);

        CalendarEntry left = rig.Store.Find(series.Id)!;
        Assert.Equal([Friday, Friday + 1], left.Skipped.Order());
        Assert.Equal([6], PageTree.Grid(await rig.PageAsync()).Events.Select(e => e.Day));
    }

    [Fact]
    public async Task Duplicate_to_next_week_copies_every_selected_block_seven_days_later()
    {
        (Rig rig, CalendarEntry a, CalendarEntry b) = await TwoSessionsAsync();
        await rig.Click(Rig.BlockId(a, Friday));
        await rig.ClickExtend(Rig.BlockId(b, Friday));

        await rig.Click(CalendarIds.DuplicateNextWeek);
        UiPage page = await rig.PageAsync();

        Assert.Equal(4, rig.Store.Entries.Count);
        CalendarEntry[] copies = [.. rig.Store.Entries.Skip(2)];
        Assert.Equal([("Alpha", Friday + 7, 540, 60, RepeatKind.Once), ("Beta", Friday + 7, 660, 60, RepeatKind.Once)], copies.Select(c => (c.Title, c.StartDay, c.StartMinute, c.Duration, c.Repeat)));
        Assert.Null(PageTree.Grid(page).Popover);
        Assert.True(Kit.HasText(page, "Duplicated 2 of 2 to next week."));
    }

    [Fact]
    public async Task Duplicate_to_next_week_skips_a_session_that_would_overlap_and_says_so()
    {
        (Rig rig, CalendarEntry a, CalendarEntry b) = await TwoSessionsAsync();
        _ = await rig.AddAsync(Kit.Session("In the way", Friday + 7, 570, 60));
        await rig.Click(Rig.BlockId(a, Friday));
        await rig.ClickExtend(Rig.BlockId(b, Friday));

        await rig.Click(CalendarIds.DuplicateNextWeek);

        Assert.Equal(["Alpha", "Beta", "In the way", "Beta"], rig.Store.Entries.Select(e => e.Title));
        Assert.True(Kit.HasTextContaining(await rig.PageAsync(), "Duplicated 1 of 2 to next week. 1 could not be copied"));
    }

    [Fact]
    public async Task Clear_selection_closes_the_popover_without_changing_anything()
    {
        (Rig rig, CalendarEntry a, CalendarEntry b) = await TwoSessionsAsync();
        await rig.Click(Rig.BlockId(a, Friday));
        await rig.ClickExtend(Rig.BlockId(b, Friday));

        await rig.Click(CalendarIds.ClearSelection);
        UiTimeGrid grid = PageTree.Grid(await rig.PageAsync());

        Assert.Null(grid.Popover);
        Assert.DoesNotContain(grid.Events, e => e.IsSelected);
        Assert.Equal(2, rig.Store.Entries.Count);
    }

    [Fact]
    public async Task Clicking_a_block_that_no_longer_exists_is_ignored()
    {
        (Rig rig, CalendarEntry a, _) = await TwoSessionsAsync();
        string id = Rig.BlockId(a, Friday);
        _ = await rig.Store.DeleteAsync(a.Id, CancellationToken.None);

        await rig.Click(id);
        await rig.ClickExtend(id);

        Assert.Empty(rig.State.Selection);
        Assert.Null(PageTree.Grid(await rig.PageAsync()).Popover);
    }

    [Fact]
    public async Task A_selected_block_whose_entry_vanished_closes_its_popover()
    {
        (Rig rig, CalendarEntry a, _) = await TwoSessionsAsync();
        await rig.Click(Rig.BlockId(a, Friday));
        _ = await rig.Store.DeleteAsync(a.Id, CancellationToken.None);

        Assert.Null(PageTree.Grid(await rig.PageAsync()).Popover);
    }
}

/// <summary>Editing, duplicating and deleting one entry from its popover.</summary>
public class CalendarEditTests
{
    private static readonly int Friday = Kit.D(2026, 1, 2);

    private static Dictionary<string, string> QuickWithoutKind(string title, string time, string to, string repeat = "once")
    {
        Dictionary<string, string> values = Rig.Quick(title, time: time, to: to, repeat: repeat);
        _ = values.Remove(CalendarForm.KindId);
        return values;
    }

    [Fact]
    public async Task Edit_opens_the_editor_on_the_block_with_its_values_and_without_a_kind_choice()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));
        await rig.Click(Rig.BlockId(a, Friday));

        await rig.Click(CalendarIds.Edit);
        UiPage page = await rig.PageAsync();

        UiNode popover = PageTree.Popover(page);
        Assert.Equal(Rig.BlockId(a, Friday), PageTree.Grid(page).PopoverAnchorId);
        Assert.Equal([CalendarForm.TitleId, CalendarForm.WhenId, CalendarForm.TimeId, CalendarForm.ToId, CalendarForm.RepeatId], PageTree.Form(popover).Fields.Select(f => f.Id));
        Assert.Equal(("Alpha", "09:00", "10:00"), (PageTree.Field<UiTextField>(popover, CalendarForm.TitleId).Value, PageTree.Field<UiTextField>(popover, CalendarForm.TimeId).Value, PageTree.Field<UiTextField>(popover, CalendarForm.ToId).Value));
        Assert.Null(PageTree.Grid(page).PendingDay);
        Assert.Equal([CalendarIds.More], PageTree.ButtonIds(popover));
    }

    [Fact]
    public async Task Saving_the_editor_changes_the_entry_and_closes_the_popover()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));
        await rig.Click(Rig.BlockId(a, Friday));
        await rig.Click(CalendarIds.Edit);

        await rig.SaveEditor(QuickWithoutKind("Alpha two", "09:30", "10:45"));
        UiPage page = await rig.PageAsync();

        CalendarEntry saved = rig.Store.Find(a.Id)!;
        Assert.Equal(("Alpha two", 570, 75, EntryKind.Session), (saved.Title, saved.StartMinute, saved.Duration, saved.Kind));
        Assert.Null(PageTree.Grid(page).Popover);
        Assert.Equal((570, 645, "Alpha two"), (PageTree.Grid(page).Events.Single().StartMinute, PageTree.Grid(page).Events.Single().EndMinute, PageTree.Grid(page).Events.Single().Title));
        Assert.True(Kit.HasText(page, "Saved \"Alpha two\"."));
    }

    [Fact]
    public async Task Editing_a_repeating_entry_asks_which_occurrences_and_defaults_to_this_one()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry series = await rig.AddAsync(Kit.Weekly(Kit.Session("Study", Friday, 540, 60), 0b10000));
        await rig.Click(Rig.BlockId(series, Friday));
        await rig.Click(CalendarIds.Edit);

        UiSelectField scope = PageTree.Field<UiSelectField>(PageTree.Popover(await rig.PageAsync()), CalendarForm.ScopeId);
        Assert.Equal(["occurrence", "series"], scope.Options.Select(o => o.Value));
        Assert.Equal("occurrence", scope.SelectedValue);

        Dictionary<string, string> thisOne = QuickWithoutKind("Study alone", "09:00", "10:00", "weekly");
        thisOne[CalendarForm.ScopeId] = "occurrence";
        await rig.SaveEditor(thisOne);

        CalendarEntry after = rig.Store.Find(series.Id)!;
        Assert.Equal("Study", after.Title);
        Assert.Equal("Study alone", Assert.Single(after.Overrides).Title);
        Assert.Equal("Study alone", PageTree.Grid(await rig.PageAsync()).Events.Single().Title);
    }

    [Fact]
    public async Task Editing_all_occurrences_changes_the_series_and_keeps_its_end_rule()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry plain = Kit.Weekly(Kit.Session("Study", Friday, 540, 60), 0b10000);
        plain.End = EndKind.AfterCount;
        plain.Count = 6;
        CalendarEntry series = await rig.AddAsync(plain);
        await rig.Click(Rig.BlockId(series, Friday));
        await rig.Click(CalendarIds.Edit);

        Dictionary<string, string> all = QuickWithoutKind("Study hard", "09:00", "11:00", "weekly");
        all[CalendarForm.ScopeId] = "series";
        await rig.SaveEditor(all);

        CalendarEntry after = rig.Store.Find(series.Id)!;
        Assert.Equal(("Study hard", 120, EndKind.AfterCount, 6, 0b10000), (after.Title, after.Duration, after.End, after.Count, after.WeekdayMask));
        Assert.Empty(after.Overrides);
    }

    [Fact]
    public async Task Duplicate_opens_the_editor_for_a_copy_on_the_same_slot_and_saving_adds_a_new_entry()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("Call", Friday, 600));
        await rig.Click(Rig.BlockId(task, Friday));

        await rig.Click(CalendarIds.Duplicate);
        UiPage page = await rig.PageAsync();

        UiTimeGrid grid = PageTree.Grid(page);
        Assert.Equal((CalendarIds.Grid, 4, 600, 630), (grid.PopoverAnchorId, grid.PendingDay, grid.PendingStartMinute, grid.PendingEndMinute));
        Assert.Equal("Call", PageTree.Field<UiTextField>(PageTree.Popover(page), CalendarForm.TitleId).Value);
        Assert.Equal("task", PageTree.Field<UiSelectField>(PageTree.Popover(page), CalendarForm.KindId).SelectedValue);
        Assert.DoesNotContain(grid.Events, e => e.IsSelected);

        await rig.SaveEditor(Rig.Quick("Call again", "task", "11:00", "11:30"));

        Assert.Equal([("Call", 600), ("Call again", 660)], rig.Store.Entries.Select(e => (e.Title, e.StartMinute)));
    }

    [Fact]
    public async Task Duplicating_a_session_onto_its_own_slot_is_refused_until_the_time_is_changed()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));
        await rig.Click(Rig.BlockId(a, Friday));
        await rig.Click(CalendarIds.Duplicate);

        await rig.SaveEditor(Rig.Quick("Alpha", time: "09:00", to: "10:00"));
        Assert.Single(rig.Store.Entries);
        Assert.True(Kit.HasTextContaining(PageTree.Popover(await rig.PageAsync()), "Overlaps \"Alpha\""));

        await rig.SaveEditor(Rig.Quick("Alpha", time: "10:00", to: "11:00"));
        Assert.Equal(2, rig.Store.Entries.Count);
    }

    [Fact]
    public async Task Delete_needs_a_second_click_and_removes_a_one_time_entry()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));
        await rig.Click(Rig.BlockId(a, Friday));

        await rig.Click(CalendarIds.Delete);
        UiButton armed = Kit.Button(PageTree.Popover(await rig.PageAsync()), CalendarIds.Delete);
        Assert.Equal(("Click again to delete", UiButtonVariant.Warning), (armed.Label, armed.Variant));
        Assert.Single(rig.Store.Entries);
        await rig.Click(CalendarIds.Delete);
        UiPage page = await rig.PageAsync();

        Assert.Empty(rig.Store.Entries);
        Assert.Null(PageTree.Grid(page).Popover);
        Assert.True(Kit.HasText(page, "Deleted \"Alpha\"."));
    }

    [Fact]
    public async Task Delete_this_one_skips_an_occurrence_and_delete_series_removes_the_entry()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry series = await rig.AddAsync(Kit.Daily(Kit.Session("Study", Friday, 540, 60)));
        await rig.Click(Rig.BlockId(series, Friday));
        await rig.Click(CalendarIds.Delete);
        await rig.Click(CalendarIds.Delete);

        Assert.Equal([Friday], rig.Store.Find(series.Id)!.Skipped);
        Assert.Equal([5, 6], PageTree.Grid(await rig.PageAsync()).Events.Select(e => e.Day));

        await rig.Click(Rig.BlockId(series, Friday + 1));
        await rig.Click(CalendarIds.DeleteSeries);
        await rig.Click(CalendarIds.DeleteSeries);

        Assert.Empty(rig.Store.Entries);
        Assert.True(Kit.HasText(await rig.PageAsync(), "Deleted \"Study\" and all its repeats."));
    }

    [Fact]
    public async Task Arming_one_delete_button_disarms_another()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry series = await rig.AddAsync(Kit.Daily(Kit.Session("Study", Friday, 540, 60)));
        await rig.Click(Rig.BlockId(series, Friday));

        await rig.Click(CalendarIds.Delete);
        await rig.Click(CalendarIds.DeleteSeries);
        UiNode popover = PageTree.Popover(await rig.PageAsync());

        Assert.Equal("Delete this one", Kit.Button(popover, CalendarIds.Delete).Label);
        Assert.Equal("Click again to delete all", Kit.Button(popover, CalendarIds.DeleteSeries).Label);
        Assert.Single(rig.Store.Entries);
        Assert.Empty(rig.Store.Entries[0].Skipped);
    }
}

/// <summary>Moving and resizing blocks on the grid.</summary>
public class CalendarMoveTests
{
    private static readonly int Friday = Kit.D(2026, 1, 2);

    [Fact]
    public async Task Moving_a_block_to_another_day_and_time_is_saved_at_once()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));
        await rig.Click(Rig.BlockId(a, Friday));

        await rig.Schedule(Rig.BlockId(a, Friday), 5, 660, 720);
        UiPage page = await rig.PageAsync();

        CalendarEntry saved = rig.Store.Find(a.Id)!;
        Assert.Equal((Friday + 1, 660, 60), (saved.StartDay, saved.StartMinute, saved.Duration));
        var reloaded = new CalendarStore(rig.Ctx.Storage, rig.Ctx.Time, rig.Ctx.Logger);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Equal((Friday + 1, 660), (reloaded.Entries.Single().StartDay, reloaded.Entries.Single().StartMinute));
        UiTimeGridEvent block = Assert.Single(PageTree.Grid(page).Events);
        Assert.Equal((5, 660, 720), (block.Day, block.StartMinute, block.EndMinute));
        Assert.Null(PageTree.Grid(page).Popover);
    }

    [Fact]
    public async Task Resizing_a_block_changes_its_end_and_keeps_its_start()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));

        await rig.Schedule(Rig.BlockId(a, Friday), 4, 540, 645);

        CalendarEntry saved = rig.Store.Find(a.Id)!;
        Assert.Equal((540, 105), (saved.StartMinute, saved.Duration));
        Assert.Equal(645, PageTree.Grid(await rig.PageAsync()).Events.Single().EndMinute);
    }

    [Fact]
    public async Task Moving_one_occurrence_of_a_series_changes_only_that_occurrence_and_says_so()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry series = await rig.AddAsync(Kit.Weekly(Kit.Session("Study", Friday, 540, 60), 0b10000));

        await rig.Schedule(Rig.BlockId(series, Friday), 5, 600, 660);
        UiPage page = await rig.PageAsync();

        CalendarEntry saved = rig.Store.Find(series.Id)!;
        Assert.Equal((Friday, 540, 60), (saved.StartDay, saved.StartMinute, saved.Duration));
        OccurrenceOverride change = Assert.Single(saved.Overrides);
        Assert.Equal((Friday, Friday + 1, 600, 60), (change.OriginalDay, change.Day, change.Minute, change.Duration));
        Assert.True(Kit.HasText(page, "Only this occurrence moved; the rest of the series stays."));
        Assert.Equal((5, 600), (PageTree.Grid(page).Events.Single().Day, PageTree.Grid(page).Events.Single().StartMinute));
        Assert.Equal(Rig.BlockId(series, Friday), PageTree.Grid(page).Events.Single().Id);
    }

    [Fact]
    public async Task A_move_into_the_past_or_onto_another_session_is_refused_and_changes_nothing()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));
        _ = await rig.AddAsync(Kit.Session("Other", Friday + 1, 600, 60));

        await rig.Schedule(Rig.BlockId(a, Friday), 0, 540, 600);
        Assert.True(Kit.HasText(await rig.PageAsync(), "That time has already passed."));
        await rig.Schedule(Rig.BlockId(a, Friday), 5, 630, 690);
        UiPage page = await rig.PageAsync();

        Assert.True(Kit.HasTextContaining(page, "Overlaps \"Other\""));
        Assert.Equal((Friday, 540), (rig.Store.Find(a.Id)!.StartDay, rig.Store.Find(a.Id)!.StartMinute));
    }

    [Fact]
    public async Task A_session_cannot_be_stretched_beyond_twelve_hours()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 300, 60));

        await rig.Schedule(Rig.BlockId(a, Friday), 4, 300, 1100);

        Assert.True(Kit.HasText(await rig.PageAsync(), "A session lasts from 15 minutes to 12 hours."));
        Assert.Equal(60, rig.Store.Find(a.Id)!.Duration);
    }

    [Fact]
    public async Task A_started_session_and_a_block_across_midnight_cannot_be_dragged()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry started = await rig.AddSessionTodayAsync("Started", 480, 30);
        _ = await rig.Store.RecordAsync($"{started.Id}:{Kit.D(2026, 1, 1)}", SessionOutcome.Started, Kit.Start.ToUnixTimeSeconds() - 3600, CancellationToken.None);
        CalendarEntry night = await rig.AddAsync(Kit.Session("Night owl", Friday, 23 * 60, 120));

        await rig.Schedule(Rig.BlockId(started, Kit.D(2026, 1, 1)), 3, 600, 630);
        Assert.True(Kit.HasText(await rig.PageAsync(), "This block cannot be dragged. Open it and use Edit."));
        await rig.Schedule(Rig.BlockId(night, Friday, 0), 4, 1320, 1440);

        Assert.Equal(480, rig.Store.Find(started.Id)!.StartMinute);
        Assert.Equal((23 * 60, 120), (rig.Store.Find(night.Id)!.StartMinute, rig.Store.Find(night.Id)!.Duration));
    }

    [Fact]
    public async Task A_task_in_the_past_can_be_moved_and_one_without_a_length_stays_without_one()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry task = await rig.AddAsync(Kit.Task("Call", Kit.D(2025, 12, 30), 600));

        await rig.Schedule(Rig.BlockId(task, Kit.D(2025, 12, 30)), 2, 720, 750);
        CalendarEntry moved = rig.Store.Find(task.Id)!;
        Assert.Equal((Kit.D(2025, 12, 31), 720, 0), (moved.StartDay, moved.StartMinute, moved.Duration));

        await rig.Schedule(Rig.BlockId(task, Kit.D(2025, 12, 31)), 2, 720, 840);
        Assert.Equal(120, rig.Store.Find(task.Id)!.Duration);
    }

    [Fact]
    public async Task Moving_in_the_day_view_uses_the_shown_day()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));
        await rig.Click(CalendarIds.ColumnPrefix + Friday);

        await rig.Schedule(Rig.BlockId(a, Friday), 0, 600, 660);

        Assert.Equal((Friday, 600), (rig.Store.Find(a.Id)!.StartDay, rig.Store.Find(a.Id)!.StartMinute));
    }

    [Fact]
    public async Task A_move_of_a_block_that_does_not_exist_is_ignored()
    {
        var rig = await Rig.CreateAsync();
        CalendarEntry a = await rig.AddAsync(Kit.Session("Alpha", Friday, 540, 60));

        await rig.Schedule(CalendarIds.EventPrefix + "99-20000-0", 1, 600, 660);
        await rig.Schedule(CalendarIds.EventPrefix + "x", 1, 600, 660);
        await rig.Schedule("cal-unknown", 1, 600, 660);

        Assert.Equal(540, rig.Store.Find(a.Id)!.StartMinute);
        Assert.Null(rig.State.Pending);
        Assert.Null(rig.State.Message);
    }
}

/// <summary>Data written by a newer version is shown but never changed.</summary>
public class CalendarReadOnlyPageTests
{
    private static readonly string NewerData = "{\"v\":2,\"n\":2,\"e\":[{\"i\":1,\"k\":1,\"t\":\"From the future\",\"s\":" + Kit.D(2026, 1, 2) + ",\"m\":600,\"n\":30}]}";

    [Fact]
    public async Task The_page_says_it_is_read_only_and_offers_no_way_to_change_anything()
    {
        var rig = await Rig.CreateWithStoredDataAsync(NewerData);

        UiPage page = await rig.PageAsync();

        Assert.True(Kit.HasText(page, CalendarController.ReadOnlyText));
        UiTimeGrid grid = PageTree.Grid(page);
        Assert.False(grid.AllowCreate);
        UiTimeGridEvent block = Assert.Single(grid.Events);
        Assert.Equal(("From the future", false, false), (block.Title, block.IsMovable, block.IsResizable));
    }

    [Fact]
    public async Task Selecting_a_block_shows_only_its_details()
    {
        var rig = await Rig.CreateWithStoredDataAsync(NewerData);
        string id = PageTree.Grid(await rig.PageAsync()).Events.Single().Id;

        await rig.Click(id);
        UiNode popover = PageTree.Popover(await rig.PageAsync());

        Assert.Empty(PageTree.ButtonIds(popover));
        Assert.Empty(Kit.Descendants(popover).OfType<UiToggle>());
        Assert.True(Kit.HasText(popover, "From the future"));
    }

    [Fact]
    public async Task Dragging_or_saving_changes_nothing_and_says_why()
    {
        var rig = await Rig.CreateWithStoredDataAsync(NewerData);
        string id = PageTree.Grid(await rig.PageAsync()).Events.Single().Id;

        await rig.Schedule(CalendarIds.Grid, 1, 600, 660);
        Assert.Null(rig.State.Pending);
        await rig.Schedule(id, 2, 700, 730);
        await rig.Click(CalendarIds.Edit);
        await rig.Click(CalendarIds.Duplicate);

        Assert.Equal(600, Assert.Single(rig.Store.Entries).StartMinute);
        Assert.Equal(NewerData, await rig.Ctx.Storage.ReadAsync(CalendarStore.DataKey, CancellationToken.None));
        Assert.True(Kit.HasText(await rig.PageAsync(), CalendarController.ReadOnlyText));
    }

    [Fact]
    public async Task The_settings_say_so_and_change_nothing()
    {
        var rig = await Rig.CreateWithStoredDataAsync(NewerData);

        Assert.True(Kit.HasText(await rig.SettingsAsync(), CalendarController.ReadOnlyText));
        await rig.Ctx.SendActionAsync(rig.Settings, UiAction.Toggle(CalendarIds.SetAuto, false));
        await rig.Click(rig.Settings, CalendarIds.SetLengthUp);

        Assert.True(rig.Store.Settings.AutoStart);
        Assert.Equal(120, rig.Store.Settings.DefaultSessionMinutes);
        Assert.Null(await rig.Ctx.Storage.ReadAsync(CalendarStore.SettingsKey, CancellationToken.None));
    }
}
